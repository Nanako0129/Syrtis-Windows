use crate::agent_account_scope::{
    self, AccountScope, AccountScopeError, AuthoritativeIdKind, HistoryScope, RefreshCheckpoint,
    RefreshScopeTransaction,
};
use crate::agent_antigravity;
use crate::agent_copilot;
use crate::agent_grok;
use crate::agent_grokbot;
use crate::agent_kiro;
use crate::agent_opencode_go;
use crate::agent_quota_duration::{DurationEvidence, DurationSource, DurationUnavailableReason};
use crate::agent_quota_history::{
    BatchObservationResult, HistoricalPace, HistoryError, HistoryOutcome, QuotaObservation,
    SeriesKey, StrandedSeriesFold,
};
use chrono::{DateTime, SecondsFormat, TimeZone, Utc};
use hyper_util::client::legacy::connect::dns::{
    GaiResolver as HyperGaiResolver, Name as HyperDnsName,
};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};
use std::fs;
use std::future::Future;
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::sync::{LazyLock, Mutex};
use tower_service::Service;

const CODEX_USAGE_URL: &str = "https://chatgpt.com/backend-api/wham/usage";
const CODEX_REFRESH_URL: &str = "https://auth.openai.com/oauth/token";
const CODEX_CLIENT_ID: &str = "app_EMoamEEZ73f0CkXaXp7hrann";
const CODEX_ACCESS_TOKEN_REFRESH_WINDOW_MINUTES: i64 = 5;
const CODEX_TOKEN_REFRESH_INTERVAL_DAYS: i64 = 8;
/// The terminal message `load_codex_credentials_from` produces for a missing
/// `auth.json`, matched by `required_card_source` against nothing else — an
/// unreadable-but-present file gets its own, different message and keeps its
/// tab. Matches macOS's marker string exactly (agent_usage.rs :1826-1832 on
/// TokenBar-Native), though the two are independent Rust crates.
const CODEX_UNCONFIGURED_ERROR: &str = "Codex auth.json not found. Run `codex` to log in.";
/// An `auth.json` that exists but cannot be read — a configured account whose
/// credential is broken. `required_card_source` leaves this at `oauth`, so the
/// card keeps its tab and shows the failure instead of claiming the user never
/// logged in.
const CODEX_CREDENTIALS_UNREADABLE_ERROR: &str = "Codex auth.json could not be read.";
const CLAUDE_USAGE_URL: &str = "https://api.anthropic.com/api/oauth/usage";
const CLAUDE_REFRESH_URL: &str = "https://platform.claude.com/v1/oauth/token";
const CLAUDE_CLIENT_ID: &str = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
const CLAUDE_KEYCHAIN_SERVICE: &str = "Claude Code-credentials";
// Minimal-request endpoint whose response headers carry the unified rate-limit
// windows. Used as a fallback for inference-only `claude setup-token` tokens,
// which get HTTP 403 on the oauth/usage endpoint (it requires user:profile).
const CLAUDE_MESSAGES_URL: &str = "https://api.anthropic.com/v1/messages";
// Cheapest model for the header probe. Alias (not a dated snapshot) so it
// outlives model retirements.
const CLAUDE_PROBE_MODEL: &str = "claude-haiku-4-5";
// Keychain generic-password service holding a RAW setup-token (`sk-ant-oat01-…`),
// the launch-method-independent way to hand TokenBar a token for the limits card:
//   security add-generic-password -a "$USER" -s tokenbar-claude-oauth-token -w "<token>"
const CLAUDE_RAW_TOKEN_KEYCHAIN_SERVICE: &str = "tokenbar-claude-oauth-token";

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentUsagePayload {
    generated_at: String,
    /// Monotonic order assigned by the Rust publication gate, not by wall time
    /// or provider completion order.
    publication_generation: u64,
    agents: Vec<AgentUsageSnapshot>,
    /// Subscription-type providers opencode is authenticated against (its
    /// `auth.json` `type: "oauth"` entries), e.g. ["Codex", "Copilot"]. Surfaced
    /// so the user can see which agent subscriptions opencode also draws on.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    opencode_subscriptions: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentUsageSnapshot {
    client_id: String,
    /// Which account of `client_id` this card belongs to: `None` for the
    /// primary account (omitted on the wire, so a single-account payload is
    /// byte-identical to one produced before this field existed), a configured
    /// `CLAUDE_CONFIG_DIR` path, or `CLAUDE_DESKTOP_ACCOUNT_KEY`. Stamped by
    /// `apply_account_outcome_with`, never by a fetch.
    #[serde(skip_serializing_if = "Option::is_none")]
    account_key: Option<String>,
    /// The binding-keyed `/api/oauth/profile` identity this fetch proved, used
    /// only by the Claude merge pass. Never serialized: it is an HMAC scope,
    /// not something a consumer may join on.
    #[serde(skip)]
    merge_scope: Option<AccountScope>,
    source: String,
    updated_at: String,
    identity: Option<AgentIdentity>,
    /// The opaque HMAC scope of the currently-authenticated identity for this
    /// provider, or the reason it could not be resolved. Exposed on the wire
    /// (unlike the earlier `#[serde(skip)]`) so a C# consumer can match a live
    /// agent to the SAME account's stored quota-history series rather than
    /// falling back to "first series under this window key" — the two can
    /// differ the moment an account switch adds a second series under one
    /// `(providerId, windowKey)`. `Ok`/`Err` are kept distinguishable on the
    /// wire (an object with either `scope` or `error` set, never both) —
    /// collapsing a resolution failure into an absent field would read as "no
    /// scope needed" rather than "could not tell".
    #[serde(serialize_with = "serialize_account_scope")]
    pub(crate) account_scope: Result<AccountScope, AccountScopeError>,
    /// Identity for the durable pace history only. Distinct from `account_scope`
    /// on purpose: the cache binding and the plan label must fragment when the
    /// credential rotates, and a weeks-long series must not.
    ///
    /// Serialized (macOS keeps it `#[serde(skip)]`) because on Windows the join
    /// between a live card and its stored series happens in C#: the store keys
    /// series on this value, and `accountScope` no longer equals it for a
    /// provider without an authoritative owner ID. Same wire shape as
    /// `accountScope`. Not a new exposure: the same string is already the
    /// `accountScope` of the series `tb_quota_history` exports.
    #[serde(serialize_with = "serialize_history_scope")]
    pub(crate) history_scope: Result<HistoryScope, AccountScopeError>,
    windows: Vec<UsageWindow>,
    credits: Option<CreditsSnapshot>,
    error: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    transport_diagnostic: Option<SafeTransportDiagnostic>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AccountScopeWire<'a> {
    #[serde(skip_serializing_if = "Option::is_none")]
    scope: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}

fn serialize_account_scope<S>(
    value: &Result<AccountScope, AccountScopeError>,
    serializer: S,
) -> Result<S::Ok, S::Error>
where
    S: serde::Serializer,
{
    serialize_scope_wire(value.as_ref().map(AccountScope::as_str), serializer)
}

fn serialize_history_scope<S>(
    value: &Result<HistoryScope, AccountScopeError>,
    serializer: S,
) -> Result<S::Ok, S::Error>
where
    S: serde::Serializer,
{
    serialize_scope_wire(value.as_ref().map(HistoryScope::as_str), serializer)
}

fn serialize_scope_wire<S>(
    value: Result<&str, &AccountScopeError>,
    serializer: S,
) -> Result<S::Ok, S::Error>
where
    S: serde::Serializer,
{
    let wire = match value {
        Ok(scope) => AccountScopeWire {
            scope: Some(scope),
            error: None,
        },
        Err(error) => AccountScopeWire {
            scope: None,
            error: Some(error.to_string()),
        },
    };
    wire.serialize(serializer)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) enum TransportCategory {
    Timeout,
    Dns,
    Tls,
    ConnectionRefused,
    ConnectionReset,
    Connect,
    Request,
    ResponseBody,
    RateLimited,
    ServerError,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SafeTransportDiagnostic {
    category: TransportCategory,
    #[serde(skip_serializing_if = "Option::is_none")]
    status: Option<u16>,
    #[serde(skip_serializing_if = "Option::is_none")]
    os_code: Option<i32>,
}

impl SafeTransportDiagnostic {
    pub(crate) fn from_facts(facts: TransportErrorFacts) -> Self {
        let category = if facts.is_timeout {
            TransportCategory::Timeout
        } else {
            match facts.raw_os_code {
                Some(61 | 111 | 10061) => TransportCategory::ConnectionRefused,
                Some(54 | 104 | 10054) => TransportCategory::ConnectionReset,
                _ if facts.is_dns => TransportCategory::Dns,
                _ if facts.is_tls => TransportCategory::Tls,
                _ if facts.is_connect => TransportCategory::Connect,
                _ if facts.phase == TransportPhase::ResponseBody => TransportCategory::ResponseBody,
                _ => TransportCategory::Request,
            }
        };
        Self {
            category,
            status: None,
            os_code: facts.raw_os_code,
        }
    }

    fn rate_limited(status: u16) -> Self {
        Self {
            category: TransportCategory::RateLimited,
            status: (100..=599).contains(&status).then_some(status),
            os_code: None,
        }
    }

    fn server_error(status: u16) -> Self {
        Self {
            category: TransportCategory::ServerError,
            status: (100..=599).contains(&status).then_some(status),
            os_code: None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum TransportPhase {
    Request,
    ResponseBody,
}

#[derive(Debug)]
struct DnsResolutionError {
    source: Box<dyn std::error::Error + Send + Sync>,
}

impl DnsResolutionError {
    fn new(source: impl std::error::Error + Send + Sync + 'static) -> Self {
        Self {
            source: Box::new(source),
        }
    }
}

impl std::fmt::Display for DnsResolutionError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("DNS resolution failed")
    }
}

impl std::error::Error for DnsResolutionError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(self.source.as_ref())
    }
}

#[derive(Debug, Clone, Copy)]
struct TypedGaiResolver;

impl reqwest::dns::Resolve for TypedGaiResolver {
    fn resolve(&self, name: reqwest::dns::Name) -> reqwest::dns::Resolving {
        let parsed_name = name.as_str().parse::<HyperDnsName>();
        Box::pin(async move {
            let parsed_name = parsed_name.map_err(|source| {
                Box::new(DnsResolutionError::new(source))
                    as Box<dyn std::error::Error + Send + Sync>
            })?;
            let addresses = HyperGaiResolver::new()
                .call(parsed_name)
                .await
                .map_err(|source| {
                    Box::new(DnsResolutionError::new(source))
                        as Box<dyn std::error::Error + Send + Sync>
                })?;
            Ok(Box::new(addresses) as reqwest::dns::Addrs)
        })
    }
}

pub(crate) fn provider_http_client_builder() -> reqwest::ClientBuilder {
    reqwest::Client::builder().dns_resolver(TypedGaiResolver)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct TransportErrorFacts {
    is_timeout: bool,
    is_connect: bool,
    is_dns: bool,
    is_tls: bool,
    phase: TransportPhase,
    raw_os_code: Option<i32>,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
struct TransportSourceFacts {
    is_dns: bool,
    is_tls: bool,
    raw_os_code: Option<i32>,
}

fn transport_source_facts(error: &(dyn std::error::Error + 'static)) -> TransportSourceFacts {
    let mut sources = vec![error];
    let mut facts = TransportSourceFacts::default();
    while let Some(current) = sources.pop() {
        if let Some(io_error) = current.downcast_ref::<std::io::Error>() {
            if facts.raw_os_code.is_none() {
                facts.raw_os_code = io_error.raw_os_error();
            }
            if let Some(inner) = io_error.get_ref() {
                sources.push(inner);
            }
        }
        facts.is_dns |= current.downcast_ref::<DnsResolutionError>().is_some();
        facts.is_tls |= current.downcast_ref::<rustls::Error>().is_some();
        if let Some(source) = current.source() {
            sources.push(source);
        }
    }
    facts
}

impl TransportErrorFacts {
    pub(crate) fn from_reqwest(error: &reqwest::Error, phase: TransportPhase) -> Self {
        let source_facts = transport_source_facts(error);
        Self {
            is_timeout: error.is_timeout(),
            is_connect: error.is_connect(),
            is_dns: source_facts.is_dns,
            is_tls: source_facts.is_tls,
            phase,
            raw_os_code: source_facts.raw_os_code,
        }
    }

    #[cfg(test)]
    pub(crate) fn synthetic(
        is_timeout: bool,
        is_connect: bool,
        phase: TransportPhase,
        raw_os_code: Option<i32>,
    ) -> Self {
        Self {
            is_timeout,
            is_connect,
            is_dns: false,
            is_tls: false,
            phase,
            raw_os_code,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct ProviderCacheBinding {
    primary: AccountScope,
    corroborating: Option<AccountScope>,
}

impl ProviderCacheBinding {
    pub(crate) fn new(primary: AccountScope, corroborating: Option<AccountScope>) -> Self {
        Self {
            primary,
            corroborating,
        }
    }

    pub(crate) fn primary(primary: AccountScope) -> Self {
        Self::new(primary, None)
    }
}

pub(crate) async fn request_after_verified_binding<B, T, E, F, Future>(
    binding: Result<B, E>,
    request: F,
) -> Result<T, E>
where
    F: FnOnce(B) -> Future,
    Future: std::future::Future<Output = Result<T, E>>,
{
    request(binding?).await
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum RefreshTargetError {
    TargetMissing,
    TargetChanged,
    TargetMalformed,
    TargetUnverified,
    Persistence,
}

impl RefreshTargetError {
    pub(crate) fn is_persistence(self) -> bool {
        matches!(self, Self::Persistence)
    }
}

#[derive(Debug, Clone)]
pub(crate) enum ProviderFetchFailure {
    Transient {
        display: String,
        attempt_binding: Option<ProviderCacheBinding>,
        transport_diagnostic: SafeTransportDiagnostic,
    },
    Terminal {
        display: String,
    },
}

impl ProviderFetchFailure {
    pub(crate) fn transient(
        display: impl Into<String>,
        attempt_binding: Option<ProviderCacheBinding>,
        transport_diagnostic: SafeTransportDiagnostic,
    ) -> Self {
        Self::Transient {
            display: display.into(),
            attempt_binding,
            transport_diagnostic,
        }
    }

    pub(crate) fn terminal(display: impl Into<String>) -> Self {
        Self::Terminal {
            display: display.into(),
        }
    }

    pub(crate) fn from_send_error(
        display: impl Into<String>,
        attempt_binding: Option<ProviderCacheBinding>,
        error: &reqwest::Error,
    ) -> Self {
        if error.is_builder() {
            return Self::terminal(display);
        }
        Self::transient(
            display,
            attempt_binding,
            SafeTransportDiagnostic::from_facts(TransportErrorFacts::from_reqwest(
                error,
                TransportPhase::Request,
            )),
        )
    }
}

#[derive(Debug, Clone)]
pub(crate) enum ProviderFetchOutcome {
    Absent,
    Success {
        snapshot: AgentUsageSnapshot,
        cache_binding: Option<ProviderCacheBinding>,
    },
    Failure(ProviderFetchFailure),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum ResponseReadFailure {
    Transient(SafeTransportDiagnostic),
    Terminal(u16),
}

pub(crate) async fn read_response_body<F, Future>(
    status: u16,
    allow_forbidden_body: bool,
    read: F,
) -> Result<String, ResponseReadFailure>
where
    F: FnOnce() -> Future,
    Future: std::future::Future<Output = Result<String, TransportErrorFacts>>,
{
    if status == 429 {
        return Err(ResponseReadFailure::Transient(
            SafeTransportDiagnostic::rate_limited(status),
        ));
    }
    if (500..=599).contains(&status) {
        return Err(ResponseReadFailure::Transient(
            SafeTransportDiagnostic::server_error(status),
        ));
    }
    let may_read = (200..=299).contains(&status) || (allow_forbidden_body && status == 403);
    if !may_read {
        return Err(ResponseReadFailure::Terminal(status));
    }
    match read().await {
        Ok(body) => Ok(body),
        Err(_) if status == 403 => Err(ResponseReadFailure::Terminal(status)),
        Err(facts) => Err(ResponseReadFailure::Transient(
            SafeTransportDiagnostic::from_facts(facts),
        )),
    }
}

#[derive(Debug, Clone)]
struct LastGoodEntry {
    binding: ProviderCacheBinding,
    snapshot: AgentUsageSnapshot,
}

/// One account's slot in a per-client cache: `(client_id, account_key)`, with
/// the account key already passed through `account_key_component`. Keyed on
/// `client_id` alone, a second Claude account would overwrite the primary's
/// entry and the primary would have no last-good to recover.
type AccountSlot = (String, Option<String>);

fn account_slot(client_id: &str, account: Option<&str>) -> AccountSlot {
    (
        client_id.to_string(),
        account_key_component(account).map(str::to_string),
    )
}

/// The one place an account argument becomes "which account is this": `None`
/// or empty is the primary, anything else is used byte for byte. Nobody trims:
/// the last-good slot, the 429 gate, the header and profile caches, the wire
/// `accountKey` and the config-dir history digest all go through this rule.
pub(crate) fn account_key_component(account: Option<&str>) -> Option<&str> {
    account.filter(|value| !value.is_empty())
}

#[derive(Debug, Default)]
struct ProviderLastGoodCache {
    entries: HashMap<AccountSlot, LastGoodEntry>,
}

impl ProviderLastGoodCache {
    fn clean_for(
        &self,
        slot: &AccountSlot,
        binding: &ProviderCacheBinding,
    ) -> Option<AgentUsageSnapshot> {
        self.entries
            .get(slot)
            .filter(|entry| &entry.binding == binding)
            .map(|entry| entry.snapshot.clone())
    }

    fn replace(
        &mut self,
        slot: &AccountSlot,
        binding: ProviderCacheBinding,
        mut snapshot: AgentUsageSnapshot,
    ) {
        snapshot.error = None;
        snapshot.transport_diagnostic = None;
        self.entries
            .insert(slot.clone(), LastGoodEntry { binding, snapshot });
    }

    fn clear(&mut self, slot: &AccountSlot) {
        self.entries.remove(slot);
    }
}

static PROVIDER_LAST_GOOD: LazyLock<Mutex<ProviderLastGoodCache>> =
    LazyLock::new(|| Mutex::new(ProviderLastGoodCache::default()));

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentIdentity {
    pub(crate) email: Option<String>,
    pub(crate) plan: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HistoricalPacePayload {
    pub(crate) expected_used_percent: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub(crate) eta_seconds: Option<f64>,
    pub(crate) will_last_to_reset: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub(crate) run_out_probability: Option<f64>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
enum PaceState {
    LearningDuration,
    LearningHistory,
    Available,
    Unavailable,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct PaceStatusPayload {
    state: PaceState,
    #[serde(skip_serializing_if = "Option::is_none")]
    window_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    duration_seconds: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    duration_source: Option<DurationSource>,
    complete_cycles: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    reason: Option<String>,
}

#[derive(Debug, Clone)]
pub struct UsageWindow {
    card_id: String,
    label: String,
    used_percent: f64,
    remaining_percent: f64,
    resets_at: Option<String>,
    reset_text: Option<String>,
    /// Legacy compatibility only. Wire serialization derives this from
    /// `duration_seconds`; provider adapters must never use this as identity.
    window_minutes: Option<i64>,
    window_key: Option<String>,
    duration_seconds: Option<i64>,
    duration_source: Option<DurationSource>,
    provider_duration: Option<DurationEvidence>,
    contract_duration: Option<DurationEvidence>,
    pace_status: PaceStatusPayload,
    historical_pace: Option<HistoricalPacePayload>,
    /// The model this window's allowance is scoped to, as the provider's own
    /// display-name slug — `fable` for a "Fable only" weekly limit.
    ///
    /// Set ONLY where the provider declares a scope (`limits[].scope.model`),
    /// never inferred from a label. "Designs" and "Daily Routines" are windows
    /// with a narrow scope that is not a MODEL, and a flat `seven_day_opus`
    /// field says nothing about scope at all — guessing from those is how a
    /// filter starts excluding usage the allowance actually counts.
    ///
    /// The display name rather than `scope.model.id` for the reason the card id
    /// uses it: the live payload reports `id: null` while the field exists.
    model_scope: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CreditsSnapshot {
    remaining: Option<f64>,
    unlimited: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct UsageWindowWire<'a> {
    card_id: &'a str,
    label: &'a str,
    used_percent: f64,
    remaining_percent: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    resets_at: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    reset_text: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    window_minutes: Option<i64>,
    pace_status: &'a PaceStatusPayload,
    #[serde(skip_serializing_if = "Option::is_none")]
    historical_pace: Option<&'a HistoricalPacePayload>,
    #[serde(skip_serializing_if = "Option::is_none")]
    model_scope: Option<&'a str>,
}

impl Serialize for UsageWindow {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        self.validate_wire().map_err(serde::ser::Error::custom)?;
        UsageWindowWire {
            card_id: &self.card_id,
            label: &self.label,
            used_percent: self.used_percent,
            remaining_percent: self.remaining_percent,
            resets_at: self.resets_at.as_deref(),
            reset_text: self.reset_text.as_deref(),
            window_minutes: self.duration_seconds.map(|seconds| seconds / 60),
            pace_status: &self.pace_status,
            historical_pace: self.historical_pace.as_ref(),
            model_scope: self.model_scope.as_deref(),
        }
        .serialize(serializer)
    }
}

impl UsageWindow {
    /// Build a window from a "remaining fraction" (0..1) — the shape Antigravity
    /// reports per model. Used-percent is derived; identity and duration are
    /// attached by the provider adapter before the snapshot is emitted.
    pub(crate) fn from_fraction(
        label: String,
        remaining_fraction: f64,
        resets_at: Option<DateTime<Utc>>,
        now: DateTime<Utc>,
    ) -> Self {
        Self::from_used_percent(
            label,
            (1.0 - remaining_fraction) * 100.0,
            resets_at,
            now,
            None,
        )
    }

    /// Build a window from an absolute used-percent (0..100), with an optional
    /// legacy duration hint. The hint is retained only for existing tests and
    /// converted to exact seconds before any wire serialization.
    pub(crate) fn from_used_percent(
        label: String,
        used_percent: f64,
        resets_at: Option<DateTime<Utc>>,
        now: DateTime<Utc>,
        window_minutes: Option<i64>,
    ) -> Self {
        let used = used_percent.clamp(0.0, 100.0);
        let remaining = (100.0 - used).clamp(0.0, 100.0);
        let duration_seconds = window_minutes
            .filter(|minutes| *minutes > 0)
            .and_then(|minutes| minutes.checked_mul(60));
        let mut window = UsageWindow {
            card_id: "row.unassigned.v1".to_string(),
            label,
            used_percent: used,
            remaining_percent: remaining,
            resets_at: resets_at.map(|d| d.to_rfc3339_opts(SecondsFormat::Millis, true)),
            reset_text: resets_at.map(|d| reset_text(d, now)),
            window_minutes,
            window_key: None,
            duration_seconds,
            duration_source: duration_seconds.map(|_| DurationSource::Contract),
            provider_duration: None,
            contract_duration: duration_seconds.map(DurationEvidence::contract),
            pace_status: PaceStatusPayload {
                state: PaceState::Unavailable,
                window_key: None,
                duration_seconds: None,
                duration_source: None,
                complete_cycles: 0,
                reason: Some("windowIdentity".to_string()),
            },
            historical_pace: None,
            // Absent by default and attached only by the adapter that has a
            // provider-declared scope. A window nobody scoped must read as
            // "not scoped", never as "scoped to nothing".
            model_scope: None,
        };
        window.refresh_initial_pace_status();
        window
    }

    /// Preserve the raw provider reading until identity is attached so the
    /// generic adapter can classify invalid evidence. `with_identity` then
    /// restores finite display percentages before any wire serialization.
    pub(crate) fn from_provider_used_percent(
        label: String,
        used_percent: f64,
        resets_at: Option<DateTime<Utc>>,
        now: DateTime<Utc>,
    ) -> Self {
        let mut window = Self::from_used_percent(label, used_percent, resets_at, now, None);
        window.used_percent = used_percent;
        window.remaining_percent = 100.0 - used_percent;
        window
    }

    pub(crate) fn from_provider_fraction(
        label: String,
        remaining_fraction: f64,
        resets_at: Option<DateTime<Utc>>,
        now: DateTime<Utc>,
    ) -> Self {
        Self::from_provider_used_percent(label, (1.0 - remaining_fraction) * 100.0, resets_at, now)
    }

    pub(crate) fn try_from_provider_used_percent(
        label: String,
        used_percent: f64,
        resets_at: Option<DateTime<Utc>>,
        now: DateTime<Utc>,
    ) -> Option<Self> {
        (used_percent.is_finite() && (0.0..=100.0).contains(&used_percent))
            .then(|| Self::from_provider_used_percent(label, used_percent, resets_at, now))
    }

    pub(crate) fn try_from_provider_fraction(
        label: String,
        remaining_fraction: f64,
        resets_at: Option<DateTime<Utc>>,
        now: DateTime<Utc>,
    ) -> Option<Self> {
        (remaining_fraction.is_finite() && (0.0..=1.0).contains(&remaining_fraction))
            .then(|| Self::from_provider_fraction(label, remaining_fraction, resets_at, now))
    }

    /// Attach the provider-declared model scope. Separate from `with_identity`
    /// because identity is emitted for every window and scope only for the few
    /// the provider narrows — folding it in would make every call site state a
    /// scope it does not have.
    pub(crate) fn with_model_scope(mut self, slug: impl Into<String>) -> Self {
        let slug = slug.into();
        if !slug.is_empty() {
            self.model_scope = Some(slug);
        }
        self
    }

    /// Attach provider-semantic presentation and history identity plus the
    /// frozen provider/contract duration evidence.
    pub(crate) fn with_identity(
        mut self,
        card_id: impl Into<String>,
        window_key: Option<String>,
        provider_duration: Option<DurationEvidence>,
        contract_duration: Option<DurationEvidence>,
    ) -> Self {
        let invalid_reading =
            !self.used_percent.is_finite() || !(0.0..=100.0).contains(&self.used_percent);
        if invalid_reading {
            self.used_percent = if self.used_percent.is_finite() {
                self.used_percent.clamp(0.0, 100.0)
            } else {
                0.0
            };
            self.remaining_percent = 100.0 - self.used_percent;
        }
        self.card_id = card_id.into();
        self.window_key = window_key;
        self.provider_duration = provider_duration;
        self.contract_duration = contract_duration;
        self.duration_seconds = self
            .provider_duration
            .or(self.contract_duration)
            .map(|evidence| evidence.duration_seconds)
            .filter(|duration| *duration > 0);
        self.duration_source = if self.provider_duration.is_some() {
            Some(DurationSource::Provider)
        } else if self.contract_duration.is_some() {
            Some(DurationSource::Contract)
        } else {
            None
        };
        self.window_minutes = self.duration_seconds.map(|seconds| seconds / 60);
        self.refresh_initial_pace_status();
        if invalid_reading && self.window_key.is_some() {
            self.unavailable("invalidEvidence");
        }
        self
    }

    fn refresh_initial_pace_status(&mut self) {
        if self.window_key.is_none() {
            self.duration_seconds = None;
            self.duration_source = None;
            self.window_minutes = None;
            self.pace_status = PaceStatusPayload {
                state: PaceState::Unavailable,
                window_key: None,
                duration_seconds: None,
                duration_source: None,
                complete_cycles: 0,
                reason: Some("windowIdentity".to_string()),
            };
            self.historical_pace = None;
            return;
        }
        if self.resets_at.is_none() {
            self.duration_seconds = None;
            self.duration_source = None;
            self.window_minutes = None;
            self.pace_status = PaceStatusPayload {
                state: PaceState::Unavailable,
                window_key: self.window_key.clone(),
                duration_seconds: None,
                duration_source: None,
                complete_cycles: 0,
                reason: Some("missingReset".to_string()),
            };
            self.historical_pace = None;
            return;
        }
        let state = if self.duration_seconds.is_some() {
            PaceState::LearningHistory
        } else {
            PaceState::LearningDuration
        };
        self.pace_status = PaceStatusPayload {
            state,
            window_key: self.window_key.clone(),
            duration_seconds: self.duration_seconds,
            duration_source: self.duration_source,
            complete_cycles: 0,
            reason: None,
        };
        self.historical_pace = None;
    }

    pub(crate) fn unavailable(&mut self, reason: impl Into<String>) {
        let reason = reason.into();
        self.duration_seconds = None;
        self.duration_source = None;
        self.window_minutes = None;
        self.historical_pace = None;
        self.pace_status = PaceStatusPayload {
            state: PaceState::Unavailable,
            window_key: self.window_key.clone(),
            duration_seconds: None,
            duration_source: None,
            complete_cycles: 0,
            reason: Some(reason),
        };
    }

    fn validate_wire(&self) -> Result<(), String> {
        if self.card_id.trim().is_empty() {
            return Err("pace cardId must be non-empty".to_string());
        }
        if self.window_key != self.pace_status.window_key {
            return Err("pace windowKey internal and nested values differ".to_string());
        }
        if self.duration_seconds != self.pace_status.duration_seconds {
            return Err("pace durationSeconds internal and nested values differ".to_string());
        }
        if self.duration_source != self.pace_status.duration_source {
            return Err("pace durationSource internal and nested values differ".to_string());
        }
        if self.window_minutes != self.duration_seconds.map(|seconds| seconds / 60) {
            return Err("pace windowMinutes must derive from durationSeconds".to_string());
        }
        if self.duration_seconds.is_none()
            && self.duration_source.is_some()
            && !(self.pace_status.state == PaceState::LearningDuration
                && self.duration_source == Some(DurationSource::Observed))
        {
            return Err("pace durationSource requires a duration".to_string());
        }
        if let Some(window_key) = self.pace_status.window_key.as_deref() {
            if window_key.trim().is_empty() {
                return Err("pace windowKey must be non-empty".to_string());
            }
        }
        let identity_unavailable = self.pace_status.state == PaceState::Unavailable
            && self.pace_status.reason.as_deref() == Some("windowIdentity");
        if self.pace_status.window_key.is_none() != identity_unavailable {
            return Err("pace windowKey identity invariant failed".to_string());
        }
        if let Some(duration) = self.pace_status.duration_seconds {
            if duration <= 0 {
                return Err("pace durationSeconds must be positive".to_string());
            }
            if self.pace_status.duration_source.is_none() {
                return Err("pace durationSource is required with durationSeconds".to_string());
            }
        }
        match self.pace_status.state {
            PaceState::Available => {
                if self.pace_status.duration_seconds.is_none() || self.historical_pace.is_none() {
                    return Err("available pace requires duration and historicalPace".to_string());
                }
            }
            PaceState::LearningHistory => {
                if self.pace_status.duration_seconds.is_none() || self.historical_pace.is_some() {
                    return Err("learningHistory pace invariant failed".to_string());
                }
            }
            PaceState::LearningDuration => {
                if self.pace_status.duration_seconds.is_some() || self.historical_pace.is_some() {
                    return Err("learningDuration pace invariant failed".to_string());
                }
            }
            PaceState::Unavailable => {
                if self.historical_pace.is_some() || self.pace_status.reason.as_deref().is_none() {
                    return Err("unavailable pace invariant failed".to_string());
                }
            }
        }
        if let Some(historical) = &self.historical_pace {
            if !historical.expected_used_percent.is_finite()
                || !(0.0..=100.0).contains(&historical.expected_used_percent)
                || historical
                    .eta_seconds
                    .is_some_and(|eta| !eta.is_finite() || eta < 0.0)
                || historical.run_out_probability.is_some_and(|probability| {
                    !probability.is_finite() || !(0.0..=1.0).contains(&probability)
                })
                || (historical.eta_seconds.is_none() != historical.will_last_to_reset)
            {
                return Err("historicalPace contains contradictory values".to_string());
            }
        }
        Ok(())
    }

    #[cfg(test)]
    pub(crate) fn label_for_test(&self) -> &str {
        &self.label
    }

    #[cfg(test)]
    pub(crate) fn remaining_for_test(&self) -> f64 {
        self.remaining_percent
    }

    #[cfg(test)]
    pub(crate) fn resets_at_for_test(&self) -> Option<&str> {
        self.resets_at.as_deref()
    }

    #[cfg(test)]
    pub(crate) fn window_minutes_for_test(&self) -> Option<i64> {
        self.duration_seconds.map(|seconds| seconds / 60)
    }

    #[cfg(test)]
    pub(crate) fn pace_window_key_for_test(&self) -> Option<&str> {
        self.pace_status.window_key.as_deref()
    }

    #[cfg(test)]
    pub(crate) fn pace_reason_for_test(&self) -> Option<&str> {
        self.pace_status.reason.as_deref()
    }
}

#[derive(Debug, Clone)]
struct CredentialSlot {
    semantic_source: &'static str,
    canonical_location: String,
}

#[derive(Debug, Clone)]
struct ResolvedClaudeToken {
    access_token: String,
    scope_slot: CredentialSlot,
}

#[derive(Debug, Clone)]
struct CodexCredentials {
    access_token: String,
    refresh_token: Option<String>,
    id_token: Option<String>,
    account_id: Option<String>,
    last_refresh: Option<DateTime<Utc>>,
    auth_path: PathBuf,
    raw_json: Value,
    scope_slot: CredentialSlot,
}

#[derive(Debug)]
struct CodexCredentialWriteReceipt {
    path: PathBuf,
    previous_root: Value,
    persisted_root: Value,
}

impl CodexCredentials {
    fn scope_marker(&self) -> &[u8] {
        self.refresh_token
            .as_deref()
            .map(str::trim)
            .filter(|token| !token.is_empty())
            .unwrap_or_else(|| self.access_token.trim())
            .as_bytes()
    }
}

#[derive(Debug, Clone)]
struct ClaudeCredentials {
    access_token: String,
    refresh_token: Option<String>,
    expires_at: Option<DateTime<Utc>>,
    scopes: Vec<String>,
    rate_limit_tier: Option<String>,
    subscription_type: Option<String>,
    /// Where the credentials were read from, so a rotated token can be written
    /// back to the same place (the Claude CLI shares this store).
    source: ClaudeCredentialSource,
    /// Full credentials JSON captured at reload. The target object is the
    /// optimistic write guard; top-level siblings are merged from the current
    /// store at save time.
    raw_root: Option<Value>,
    /// Exact Keychain account whose item was read at refresh reload. A later
    /// write-back may validate this identity, but must never retarget it.
    keychain_account: Option<String>,
    scope_slot: CredentialSlot,
}

impl ClaudeCredentials {
    fn scope_marker(&self) -> Option<&[u8]> {
        match self.source {
            ClaudeCredentialSource::Keychain | ClaudeCredentialSource::File => self
                .refresh_token
                .as_deref()
                .filter(|token| !token.is_empty())
                .map(str::as_bytes),
            ClaudeCredentialSource::Environment => Some(self.access_token.as_bytes()),
            ClaudeCredentialSource::Desktop | ClaudeCredentialSource::ConfigDir(_) => Some(
                self.refresh_token
                    .as_deref()
                    .filter(|token| !token.is_empty())
                    .unwrap_or(&self.access_token)
                    .as_bytes(),
            ),
        }
    }

    fn resolve_account_scope(&self) -> Result<AccountScope, AccountScopeError> {
        let marker = self
            .scope_marker()
            .ok_or(AccountScopeError::NoTrustedEvidence)?;
        agent_account_scope::resolve_credential(
            "claude",
            self.scope_slot.semantic_source,
            &self.scope_slot.canonical_location,
            marker,
        )
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum ClaudeCredentialSource {
    Keychain,
    File,
    /// Token injected via env var — read-only, has no refresh token.
    Environment,
    /// Claude Desktop's safeStorage token cache (Windows) — read-only. Never
    /// refreshed: a refresh rotates the refresh token and would sign the user
    /// out of Claude Desktop.
    Desktop,
    /// `<dir>\.credentials.json` of a configured `CLAUDE_CONFIG_DIR` (the
    /// directory is carried). Read-only: refreshing would rotate that
    /// account's refresh token behind Claude Code's back, and the refresh
    /// path writes only to the primary's stores.
    ConfigDir(PathBuf),
}

impl ClaudeCredentialSource {
    /// A credential TokenBar must never refresh, reload for refresh, or write.
    fn is_read_only(&self) -> bool {
        matches!(self, Self::Desktop | Self::ConfigDir(_))
    }
}

#[derive(Debug)]
enum ClaudeLoginResolution {
    Absent,
    ExplicitLogout,
    Ready(ClaudeCredentials),
    Terminal,
}

#[derive(Debug, Deserialize)]
struct ClaudeCredentialsRoot {
    #[serde(default, rename = "claudeAiOauth")]
    claude_ai_oauth: Option<ClaudeCredentialsOauth>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ClaudeCredentialsOauth {
    access_token: Option<String>,
    refresh_token: Option<String>,
    expires_at: Option<f64>,
    scopes: Option<Vec<String>>,
    rate_limit_tier: Option<String>,
    subscription_type: Option<String>,
}

#[derive(Debug, Deserialize)]
struct CodexUsageResponse {
    #[serde(default)]
    plan_type: Option<String>,
    #[serde(default)]
    rate_limit: Option<CodexRateLimit>,
    #[serde(default)]
    additional_rate_limits: Option<Vec<CodexAdditionalRateLimit>>,
    #[serde(default)]
    credits: Option<CodexCredits>,
}

#[derive(Debug, Deserialize)]
struct CodexRateLimit {
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    primary_window: Option<CodexWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    secondary_window: Option<CodexWindow>,
}

#[derive(Debug, Clone, Deserialize)]
struct CodexWindow {
    used_percent: f64,
    reset_at: i64,
    limit_window_seconds: i64,
}

#[derive(Debug, Deserialize)]
struct CodexAdditionalRateLimit {
    #[serde(default)]
    limit_name: Option<String>,
    #[serde(default)]
    metered_feature: Option<String>,
    #[serde(default)]
    rate_limit: Option<CodexRateLimit>,
}

#[derive(Debug, Deserialize)]
struct CodexCredits {
    #[serde(default)]
    unlimited: bool,
    #[serde(default, deserialize_with = "deserialize_optional_f64")]
    balance: Option<f64>,
}

fn finite_codex_balance(credits: Option<&CodexCredits>) -> Option<f64> {
    credits
        .and_then(|credits| credits.balance)
        .filter(|balance| balance.is_finite())
}

#[derive(Debug, Deserialize, Default)]
struct ClaudeUsageResponse {
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    five_hour: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    seven_day: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    seven_day_oauth_apps: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    seven_day_opus: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    seven_day_sonnet: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    seven_day_design: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    seven_day_claude_design: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    claude_design: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    design: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    seven_day_omelette: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    omelette: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    omelette_promotional: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    seven_day_routines: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    seven_day_claude_routines: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    claude_routines: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    routines: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    routine: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    seven_day_cowork: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    cowork: Option<ClaudeWindow>,
    #[serde(default, deserialize_with = "deserialize_optional_claude_limits")]
    limits: Option<Vec<ClaudeLimitEntry>>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    extra_usage: Option<ClaudeExtraUsage>,
}

#[derive(Debug, Clone, Deserialize)]
struct ClaudeWindow {
    #[serde(default, deserialize_with = "deserialize_optional_f64")]
    utilization: Option<f64>,
    #[serde(default)]
    resets_at: Option<String>,
}

impl ClaudeWindow {
    fn has_valid_utilization(&self) -> bool {
        self.utilization
            .is_some_and(|used| used.is_finite() && (0.0..=100.0).contains(&used))
    }
}

#[derive(Debug, Deserialize)]
struct ClaudeLimitEntry {
    #[serde(default, deserialize_with = "deserialize_optional_non_empty_string")]
    kind: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_non_empty_string")]
    group: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_f64")]
    percent: Option<f64>,
    #[serde(default, deserialize_with = "deserialize_optional_non_empty_string")]
    resets_at: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    scope: Option<ClaudeLimitScope>,
}

#[derive(Debug, Deserialize)]
struct ClaudeLimitScope {
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    model: Option<ClaudeLimitModel>,
}

#[derive(Debug, Deserialize)]
struct ClaudeLimitModel {
    #[serde(default, deserialize_with = "deserialize_optional_non_empty_string")]
    id: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_non_empty_string")]
    display_name: Option<String>,
}

#[derive(Debug, Deserialize)]
struct ClaudeExtraUsage {
    #[serde(default)]
    is_enabled: bool,
    #[serde(default, deserialize_with = "deserialize_optional_f64")]
    monthly_limit: Option<f64>,
    #[serde(default, deserialize_with = "deserialize_optional_f64")]
    used_credits: Option<f64>,
    #[serde(default, deserialize_with = "deserialize_optional_f64")]
    utilization: Option<f64>,
    #[serde(default)]
    currency: Option<String>,
}

#[derive(Debug, Deserialize)]
struct ClaudeRefreshResponse {
    access_token: String,
    #[serde(default, deserialize_with = "deserialize_optional_non_empty_string")]
    refresh_token: Option<String>,
    expires_in: i64,
}

fn empty_error_snapshot(
    client_id: &str,
    source: &str,
    now: DateTime<Utc>,
    display: String,
    transport_diagnostic: Option<SafeTransportDiagnostic>,
) -> AgentUsageSnapshot {
    AgentUsageSnapshot {
        account_key: None,
        merge_scope: None,
        client_id: client_id.to_string(),
        source: source.to_string(),
        updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
        identity: None,
        account_scope: Err(AccountScopeError::NoTrustedEvidence),
        history_scope: Err(AccountScopeError::NoTrustedEvidence),
        windows: Vec::new(),
        credits: None,
        error: Some(display),
        transport_diagnostic,
    }
}

fn usable_success(snapshot: &AgentUsageSnapshot) -> bool {
    match snapshot.client_id.as_str() {
        "codex" => {
            !snapshot.windows.is_empty()
                || snapshot
                    .credits
                    .as_ref()
                    .and_then(|credits| credits.remaining)
                    .is_some_and(f64::is_finite)
        }
        "grok" => snapshot
            .windows
            .iter()
            .any(|window| window.card_id == "billing.weekly.v1"),
        // Grok Bot is stricter than the rest: a response can carry windows
        // while omitting the weekly meter, and only that meter is the card
        // (macOS `usable_success`).
        "grok-bot" => snapshot
            .windows
            .iter()
            .any(|window| window.card_id == agent_grokbot::WEEKLY_WINDOW_KEY),
        // "kiro" carries the Kiro subscription quota and "opencode" the OpenCode
        // Go quota; each success is a non-empty window set, so a later transient
        // keeps the last-good card instead of a bare error (macOS `usable_success`).
        "claude" | "copilot" | "antigravity" | "kiro" | "opencode" => !snapshot.windows.is_empty(),
        _ => false,
    }
}

fn lock_last_good(
    cache: &Mutex<ProviderLastGoodCache>,
) -> std::sync::MutexGuard<'_, ProviderLastGoodCache> {
    cache
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn apply_provider_outcome_with<F>(
    cache: &Mutex<ProviderLastGoodCache>,
    client_id: &str,
    failure_source: &str,
    now: DateTime<Utc>,
    outcome: ProviderFetchOutcome,
    enrich: F,
) -> Option<AgentUsageSnapshot>
where
    F: FnMut(&mut AgentUsageSnapshot),
{
    apply_account_outcome_with(cache, client_id, None, failure_source, now, outcome, enrich)
}

/// `apply_provider_outcome_with` for one account of `client_id`. Every cache
/// operation uses that account's own slot, and every snapshot it returns —
/// fresh, last-good or error — carries that account's `account_key`.
fn apply_account_outcome_with<F>(
    cache: &Mutex<ProviderLastGoodCache>,
    client_id: &str,
    account: Option<&str>,
    failure_source: &str,
    now: DateTime<Utc>,
    outcome: ProviderFetchOutcome,
    mut enrich: F,
) -> Option<AgentUsageSnapshot>
where
    F: FnMut(&mut AgentUsageSnapshot),
{
    let slot = account_slot(client_id, account);
    let error_snapshot = |source: &str, display: String, diagnostic| {
        let mut snapshot = empty_error_snapshot(client_id, source, now, display, diagnostic);
        snapshot.account_key = slot.1.clone();
        snapshot
    };
    match outcome {
        ProviderFetchOutcome::Absent => {
            lock_last_good(cache).clear(&slot);
            None
        }
        ProviderFetchOutcome::Success {
            mut snapshot,
            cache_binding,
        } => {
            match snapshot.account_scope.as_ref() {
                Ok(_) | Err(AccountScopeError::NoTrustedEvidence) => {}
                Err(_) => {
                    lock_last_good(cache).clear(&slot);
                    let source = snapshot.source.clone();
                    return Some(error_snapshot(
                        &source,
                        format!(
                            "{} account identity could not be verified.",
                            clean_plan(client_id)
                        ),
                        None,
                    ));
                }
            }

            snapshot.account_key = slot.1.clone();
            enrich(&mut snapshot);
            let cacheable = snapshot.account_scope.is_ok()
                && snapshot.error.is_none()
                && snapshot.transport_diagnostic.is_none()
                && usable_success(&snapshot);
            let mut cache = lock_last_good(cache);
            match (cacheable, cache_binding) {
                (true, Some(binding)) => cache.replace(&slot, binding, snapshot.clone()),
                _ => cache.clear(&slot),
            }
            Some(snapshot)
        }
        ProviderFetchOutcome::Failure(ProviderFetchFailure::Terminal { display }) => {
            lock_last_good(cache).clear(&slot);
            Some(error_snapshot(failure_source, display, None))
        }
        ProviderFetchOutcome::Failure(ProviderFetchFailure::Transient {
            display,
            attempt_binding,
            transport_diagnostic,
        }) => {
            let fallback = attempt_binding
                .as_ref()
                .and_then(|binding| lock_last_good(cache).clean_for(&slot, binding));
            let Some(mut snapshot) = fallback else {
                lock_last_good(cache).clear(&slot);
                return Some(error_snapshot(
                    failure_source,
                    display,
                    Some(transport_diagnostic),
                ));
            };
            snapshot.account_scope = Err(AccountScopeError::NoTrustedEvidence);
            snapshot.error = Some(display);
            snapshot.transport_diagnostic = Some(transport_diagnostic);
            Some(snapshot)
        }
    }
}

/// Takes no `now`: it reads the clock itself, and the outcome it is handed is
/// proof the response has already arrived (macOS bf7a6b92).
///
/// Every caller used to capture `Utc::now()` before its request and hold it
/// across the round trip, so the timestamp the pace evidence was validated
/// against was older than the response by construction. For a window a
/// provider reports as not yet started — Codex answers one with no usage with
/// `reset_at = <its now> + limit_window_seconds` — `valid_evidence`'s
/// `cycle_started_at <= now` then rejects the provider's duration whenever the
/// round trip crosses a second boundary, and `UsageWindow::unavailable` clears
/// the window's duration and pace with it. Measured on macOS, not on Windows.
///
/// The parameter is gone rather than moved below the `await` at each caller,
/// so a pre-request timestamp cannot be handed back in.
/// `apply_provider_outcome_with` still takes one, because a test needs to
/// state the instant it is asserting about.
fn apply_provider_outcome(
    client_id: &str,
    failure_source: &str,
    outcome: ProviderFetchOutcome,
) -> Option<AgentUsageSnapshot> {
    let now = Utc::now();
    apply_provider_outcome_with(
        &PROVIDER_LAST_GOOD,
        client_id,
        failure_source,
        now,
        outcome,
        |snapshot| enrich_snapshot(snapshot, now.timestamp()),
    )
}

type BoxedFetch<T> = Pin<Box<dyn Future<Output = T>>>;

/// One fetcher per quota provider joined by `run_with`, plus the opencode
/// subscription probe. Production is `PRODUCTION_FETCHERS`; a test passes
/// stubs so the join itself can be asserted without reading any real profile.
struct Fetchers {
    codex: fn() -> BoxedFetch<AgentUsageSnapshot>,
    claude_accounts: fn() -> BoxedFetch<Vec<AgentUsageSnapshot>>,
    antigravity: fn() -> BoxedFetch<AgentUsageSnapshot>,
    copilot: fn() -> BoxedFetch<Option<AgentUsageSnapshot>>,
    grok: fn() -> BoxedFetch<Option<AgentUsageSnapshot>>,
    kiro: fn() -> BoxedFetch<Option<AgentUsageSnapshot>>,
    opencode_go: fn() -> BoxedFetch<Option<AgentUsageSnapshot>>,
    grok_bot: fn() -> BoxedFetch<Option<AgentUsageSnapshot>>,
    subscriptions: fn() -> Vec<String>,
}

const PRODUCTION_FETCHERS: Fetchers = Fetchers {
    codex: || Box::pin(fetch_codex()),
    claude_accounts: || Box::pin(fetch_claude_accounts()),
    antigravity: || Box::pin(fetch_antigravity()),
    copilot: || Box::pin(fetch_copilot()),
    grok: || Box::pin(fetch_grok()),
    kiro: || Box::pin(fetch_kiro()),
    opencode_go: || Box::pin(fetch_opencode_go()),
    grok_bot: || Box::pin(fetch_grokbot()),
    subscriptions: crate::opencode_integrations::detect_subscriptions,
};

pub async fn run(publication_generation: u64) -> AgentUsagePayload {
    run_with(&PRODUCTION_FETCHERS, publication_generation).await
}

async fn run_with(fetchers: &Fetchers, publication_generation: u64) -> AgentUsagePayload {
    let generated_at = Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true);
    let (codex, claude, antigravity, copilot, grok, kiro, opencode_go, grok_bot) = tokio::join!(
        (fetchers.codex)(),
        (fetchers.claude_accounts)(),
        (fetchers.antigravity)(),
        (fetchers.copilot)(),
        (fetchers.grok)(),
        (fetchers.kiro)(),
        (fetchers.opencode_go)(),
        (fetchers.grok_bot)()
    );
    let mut agents = vec![codex];
    agents.extend(claude);
    agents.push(antigravity);
    // Copilot only appears when signed in (via opencode); skip a bare not-signed-in error card.
    if let Some(copilot) = copilot {
        agents.push(copilot);
    }
    // Grok only appears when ~/.grok/auth.json has credentials.
    if let Some(grok) = grok {
        agents.push(grok);
    }
    // Kiro only appears when its IDE token file is present.
    if let Some(kiro) = kiro {
        agents.push(kiro);
    }
    // OpenCode Go only appears when opencode's auth.json holds an `opencode-go` key.
    if let Some(opencode_go) = opencode_go {
        agents.push(opencode_go);
    }
    // Grok Bot only appears when a Cursor login or a Grok Bot install is present.
    if let Some(grok_bot) = grok_bot {
        agents.push(grok_bot);
    }
    AgentUsagePayload {
        generated_at,
        publication_generation,
        agents,
        opencode_subscriptions: (fetchers.subscriptions)(),
    }
}

/// Everything `fetch_kiro_with` reaches outside itself: where the credential
/// comes from, which account-scope store binds it, where the request goes, which
/// last-good cache it lands in, and which history writer enriches it.
///
/// Sealed in its own module so the rest of this file cannot build one: the
/// only constructor outside `#[cfg(test)]` is `KiroDeps::system()`, which takes
/// no arguments. There is no override and no environment variable of its own;
/// the token file is found through `user_home_dir()` like every other provider
/// (which honours `HOME` when set).
mod kiro_deps {
    use super::*;
    use crate::kiro_integrations::KiroCredentialLoad;

    pub(super) type KiroLoad = Pin<Box<dyn Future<Output = KiroCredentialLoad>>>;
    pub(super) use crate::agent_kiro::{ResolveCredential, ResolveHistoryScope};
    pub(super) type Enrich = dyn Fn(&mut AgentUsageSnapshot, i64);

    pub(super) struct KiroDeps<'a> {
        pub(super) load_credential: &'a dyn Fn(DateTime<Utc>) -> KiroLoad,
        pub(super) resolve_credential: &'a ResolveCredential,
        pub(super) resolve_history_scope: &'a ResolveHistoryScope,
        pub(super) usage_url: &'a str,
        pub(super) last_good: &'a Mutex<ProviderLastGoodCache>,
        pub(super) enrich: &'a Enrich,
        _sealed: (),
    }

    fn load_system_credential(now: DateTime<Utc>) -> KiroLoad {
        Box::pin(crate::kiro_integrations::kiro_credential(now))
    }

    impl KiroDeps<'static> {
        pub(super) fn system() -> Self {
            Self {
                load_credential: &load_system_credential,
                resolve_credential: &agent_account_scope::resolve_credential,
                resolve_history_scope: &agent_account_scope::resolve_history_scope,
                usage_url: crate::agent_kiro::USAGE_URL,
                last_good: &PROVIDER_LAST_GOOD,
                enrich: &enrich_snapshot,
                _sealed: (),
            }
        }
    }

    #[cfg(test)]
    impl<'a> KiroDeps<'a> {
        pub(super) fn for_test(
            load_credential: &'a dyn Fn(DateTime<Utc>) -> KiroLoad,
            resolve_credential: &'a ResolveCredential,
            resolve_history_scope: &'a ResolveHistoryScope,
            usage_url: &'a str,
            last_good: &'a Mutex<ProviderLastGoodCache>,
            enrich: &'a Enrich,
        ) -> Self {
            Self {
                load_credential,
                resolve_credential,
                resolve_history_scope,
                usage_url,
                last_good,
                enrich,
                _sealed: (),
            }
        }
    }
}
use kiro_deps::KiroDeps;

async fn fetch_kiro() -> Option<AgentUsageSnapshot> {
    fetch_kiro_with(&KiroDeps::system()).await
}

async fn fetch_kiro_with(deps: &KiroDeps<'_>) -> Option<AgentUsageSnapshot> {
    use crate::kiro_integrations::KiroCredentialLoad;
    let now = Utc::now();
    let outcome = match (deps.load_credential)(now).await {
        KiroCredentialLoad::Absent => ProviderFetchOutcome::Absent,
        KiroCredentialLoad::Terminal(display) => {
            ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(display))
        }
        KiroCredentialLoad::Present(credential) => {
            match agent_kiro::fetch(credential, deps.usage_url, deps.resolve_credential).await {
                Ok(data) => ProviderFetchOutcome::Success {
                    cache_binding: Some(data.cache_binding),
                    snapshot: AgentUsageSnapshot {
                        account_key: None,
                        merge_scope: None,
                        client_id: "kiro".to_string(),
                        source: "oauth".to_string(),
                        updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
                        identity: data.identity,
                        account_scope: data.account_scope,
                        // Kiro has no authoritative owner ID in what Syrtis fetches.
                        history_scope: (deps.resolve_history_scope)("kiro", None),
                        windows: data.windows,
                        credits: None,
                        error: None,
                        transport_diagnostic: None,
                    },
                },
                Err(failure) => ProviderFetchOutcome::Failure(failure),
            }
        }
    };
    // Same as `apply_provider_outcome`: the clock is read after the outcome.
    let now = Utc::now();
    apply_provider_outcome_with(deps.last_good, "kiro", "oauth", now, outcome, |snapshot| {
        (deps.enrich)(snapshot, now.timestamp())
    })
}

/// Everything `fetch_opencode_go_with` reaches outside itself, sealed the same
/// way as `KiroDeps`: outside `#[cfg(test)]` the only constructor is
/// `OpenCodeGoDeps::system()`. The key is read from opencode's `auth.json`
/// through the same `auth_path()` the Copilot card uses; no override and no
/// environment variable of its own.
mod opencode_go_deps {
    use super::kiro_deps::{Enrich, ResolveCredential, ResolveHistoryScope};
    use super::*;
    use crate::opencode_integrations::OpenCodeGoCredentialLoad;

    pub(super) struct OpenCodeGoDeps<'a> {
        pub(super) load_credential: &'a dyn Fn() -> OpenCodeGoCredentialLoad,
        pub(super) resolve_credential: &'a ResolveCredential,
        pub(super) resolve_history_scope: &'a ResolveHistoryScope,
        pub(super) usage_url: &'a str,
        pub(super) last_good: &'a Mutex<ProviderLastGoodCache>,
        pub(super) enrich: &'a Enrich,
        _sealed: (),
    }

    impl OpenCodeGoDeps<'static> {
        pub(super) fn system() -> Self {
            Self {
                load_credential: &crate::opencode_integrations::opencode_go_credential,
                resolve_credential: &agent_account_scope::resolve_credential,
                resolve_history_scope: &agent_account_scope::resolve_history_scope,
                usage_url: crate::agent_opencode_go::USAGE_URL,
                last_good: &PROVIDER_LAST_GOOD,
                enrich: &enrich_snapshot,
                _sealed: (),
            }
        }
    }

    #[cfg(test)]
    impl<'a> OpenCodeGoDeps<'a> {
        pub(super) fn for_test(
            load_credential: &'a dyn Fn() -> OpenCodeGoCredentialLoad,
            resolve_credential: &'a ResolveCredential,
            resolve_history_scope: &'a ResolveHistoryScope,
            usage_url: &'a str,
            last_good: &'a Mutex<ProviderLastGoodCache>,
            enrich: &'a Enrich,
        ) -> Self {
            Self {
                load_credential,
                resolve_credential,
                resolve_history_scope,
                usage_url,
                last_good,
                enrich,
                _sealed: (),
            }
        }
    }
}
use opencode_go_deps::OpenCodeGoDeps;

async fn fetch_opencode_go() -> Option<AgentUsageSnapshot> {
    fetch_opencode_go_with(&OpenCodeGoDeps::system()).await
}

async fn fetch_opencode_go_with(deps: &OpenCodeGoDeps<'_>) -> Option<AgentUsageSnapshot> {
    use crate::opencode_integrations::OpenCodeGoCredentialLoad;
    let now = Utc::now();
    let outcome = match (deps.load_credential)() {
        OpenCodeGoCredentialLoad::Absent => ProviderFetchOutcome::Absent,
        OpenCodeGoCredentialLoad::Terminal(display) => {
            ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(display))
        }
        OpenCodeGoCredentialLoad::Present(credential) => {
            match agent_opencode_go::fetch(credential, deps.usage_url, deps.resolve_credential)
                .await
            {
                Ok(data) => ProviderFetchOutcome::Success {
                    cache_binding: Some(data.cache_binding),
                    snapshot: AgentUsageSnapshot {
                        account_key: None,
                        merge_scope: None,
                        // The Go subscription quota attaches to the existing
                        // `opencode` client tab, mirroring how the Copilot quota
                        // (also fetched via opencode auth) feeds the `copilot`
                        // tab rather than a separate one.
                        client_id: "opencode".to_string(),
                        source: "api".to_string(),
                        updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
                        identity: data.identity,
                        account_scope: data.account_scope,
                        // OpenCode Go has no authoritative owner ID in what Syrtis fetches.
                        history_scope: (deps.resolve_history_scope)("opencode", None),
                        windows: data.windows,
                        credits: None,
                        error: None,
                        transport_diagnostic: None,
                    },
                },
                Err(failure) => ProviderFetchOutcome::Failure(failure),
            }
        }
    };
    // Same as `apply_provider_outcome`: the clock is read after the outcome.
    let now = Utc::now();
    apply_provider_outcome_with(
        deps.last_good,
        "opencode",
        "api",
        now,
        outcome,
        |snapshot| (deps.enrich)(snapshot, now.timestamp()),
    )
}

/// Everything `fetch_grokbot_with` reaches outside itself, sealed the same way
/// as `KiroDeps`: outside `#[cfg(test)]` the only constructor is
/// `GrokBotDeps::system()`, so release builds read the real config root and
/// send only to `GROK_BOT_USAGE_URL`. No environment variable overrides either.
mod grokbot_deps {
    use super::kiro_deps::{Enrich, ResolveCredential, ResolveHistoryScope};
    use super::*;

    pub(super) struct GrokBotDeps<'a> {
        /// `%APPDATA%`; both the Grok Bot and the Cursor store live under it.
        pub(super) config_dir: Option<PathBuf>,
        pub(super) resolve_credential: &'a ResolveCredential,
        pub(super) resolve_history_scope: &'a ResolveHistoryScope,
        pub(super) usage_url: &'a str,
        pub(super) last_good: &'a Mutex<ProviderLastGoodCache>,
        pub(super) enrich: &'a Enrich,
        _sealed: (),
    }

    impl GrokBotDeps<'static> {
        pub(super) fn system() -> Self {
            Self {
                config_dir: dirs::config_dir(),
                resolve_credential: &agent_account_scope::resolve_credential,
                resolve_history_scope: &agent_account_scope::resolve_history_scope,
                usage_url: agent_grokbot::GROK_BOT_USAGE_URL,
                last_good: &PROVIDER_LAST_GOOD,
                enrich: &enrich_snapshot,
                _sealed: (),
            }
        }
    }

    #[cfg(test)]
    impl<'a> GrokBotDeps<'a> {
        pub(super) fn for_test(
            config_dir: PathBuf,
            resolve_credential: &'a ResolveCredential,
            resolve_history_scope: &'a ResolveHistoryScope,
            usage_url: &'a str,
            last_good: &'a Mutex<ProviderLastGoodCache>,
            enrich: &'a Enrich,
        ) -> Self {
            Self {
                config_dir: Some(config_dir),
                resolve_credential,
                resolve_history_scope,
                usage_url,
                last_good,
                enrich,
                _sealed: (),
            }
        }
    }
}
use grokbot_deps::GrokBotDeps;

async fn fetch_grokbot() -> Option<AgentUsageSnapshot> {
    fetch_grokbot_with(&GrokBotDeps::system()).await
}

async fn fetch_grokbot_with(deps: &GrokBotDeps<'_>) -> Option<AgentUsageSnapshot> {
    let result = agent_grokbot::fetch(
        deps.config_dir.clone(),
        deps.usage_url,
        deps.resolve_credential,
        deps.resolve_history_scope,
    )
    .await;
    // After the fetch, not before it: this instant becomes `updated_at` and
    // the enrich timestamp (macOS `fetch_grokbot`).
    let now = Utc::now();
    let outcome = grokbot_outcome(result, now);
    apply_provider_outcome_with(
        deps.last_good,
        "grok-bot",
        "oauth",
        now,
        outcome,
        |snapshot| (deps.enrich)(snapshot, now.timestamp()),
    )
}

fn grokbot_outcome(
    result: Result<Option<agent_grokbot::GrokBotData>, ProviderFetchFailure>,
    now: DateTime<Utc>,
) -> ProviderFetchOutcome {
    match result {
        Ok(Some(data)) => ProviderFetchOutcome::Success {
            cache_binding: data.cache_binding,
            snapshot: AgentUsageSnapshot {
                account_key: None,
                merge_scope: None,
                client_id: "grok-bot".to_string(),
                source: "oauth".to_string(),
                updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
                identity: data.identity,
                account_scope: data.account_scope,
                history_scope: data.history_scope,
                windows: data.windows,
                credits: None,
                error: None,
                transport_diagnostic: None,
            },
        },
        Ok(None) => ProviderFetchOutcome::Absent,
        Err(failure) => ProviderFetchOutcome::Failure(failure),
    }
}

async fn fetch_grok() -> Option<AgentUsageSnapshot> {
    let now = Utc::now();
    let outcome = match agent_grok::fetch(now).await {
        Ok(Some(data)) => ProviderFetchOutcome::Success {
            cache_binding: data.cache_binding,
            snapshot: AgentUsageSnapshot {
                account_key: None,
                merge_scope: None,
                client_id: "grok".to_string(),
                source: "oauth".to_string(),
                updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
                identity: data.identity,
                account_scope: data.account_scope,
                // Grok has no authoritative owner ID in anything TokenBar fetches.
                history_scope: agent_account_scope::resolve_history_scope("grok", None),
                windows: data.windows,
                credits: None,
                error: None,
                transport_diagnostic: None,
            },
        },
        Ok(None) => ProviderFetchOutcome::Absent,
        Err(failure) => ProviderFetchOutcome::Failure(failure),
    };
    apply_provider_outcome("grok", "oauth", outcome)
}

async fn fetch_copilot() -> Option<AgentUsageSnapshot> {
    let now = Utc::now();
    let outcome = match crate::opencode_integrations::github_copilot_credential() {
        crate::opencode_integrations::GitHubCopilotCredentialLoad::Absent => {
            ProviderFetchOutcome::Absent
        }
        crate::opencode_integrations::GitHubCopilotCredentialLoad::Terminal(display) => {
            ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(display))
        }
        crate::opencode_integrations::GitHubCopilotCredentialLoad::Present(credential) => {
            match agent_copilot::fetch(now, credential).await {
                Ok(data) => ProviderFetchOutcome::Success {
                    cache_binding: Some(data.cache_binding),
                    snapshot: AgentUsageSnapshot {
                        account_key: None,
                        merge_scope: None,
                        client_id: "copilot".to_string(),
                        source: "oauth".to_string(),
                        updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
                        identity: data.identity,
                        account_scope: data.account_scope,
                        // Copilot has no authoritative owner ID either.
                        history_scope: agent_account_scope::resolve_history_scope("copilot", None),
                        windows: data.windows,
                        credits: None,
                        error: None,
                        transport_diagnostic: None,
                    },
                },
                Err(failure) => ProviderFetchOutcome::Failure(failure),
            }
        }
    };
    apply_provider_outcome("copilot", "oauth", outcome)
}

async fn fetch_antigravity() -> AgentUsageSnapshot {
    let now = Utc::now();
    let outcome = match agent_antigravity::fetch(now).await {
        Ok(fetched) => ProviderFetchOutcome::Success {
            cache_binding: fetched.cache_binding,
            snapshot: AgentUsageSnapshot {
                account_key: None,
                merge_scope: None,
                client_id: "antigravity".to_string(),
                source: fetched.source,
                updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
                identity: fetched.identity,
                account_scope: fetched.account_scope,
                history_scope: fetched.history_scope,
                windows: fetched.windows,
                credits: None,
                error: None,
                transport_diagnostic: None,
            },
        },
        Err(failure) => ProviderFetchOutcome::Failure(failure),
    };
    let source = required_card_source(&outcome, agent_antigravity::ANTIGRAVITY_UNCONFIGURED_ERROR);
    apply_provider_outcome("antigravity", source, outcome)
        .expect("Antigravity is a required provider card")
}

async fn fetch_codex() -> AgentUsageSnapshot {
    let outcome = fetch_codex_inner().await;
    let source = required_card_source(&outcome, CODEX_UNCONFIGURED_ERROR);
    apply_provider_outcome("codex", source, outcome)
        .expect("Codex is a required provider card")
}

/// The `source` a required provider card reports for a failed fetch.
///
/// Codex, Claude and Antigravity are pushed into `agents` whether or not the
/// user has them — `run` only filters the optional providers by whether a
/// login exists. So for these three, "a card is present" says nothing about
/// whether anything is configured, and the payload has to carry the
/// difference: the C# side reads `IsSetupPlaceholder`, and through it
/// `ConfiguredClientIds`, to decide which quota sources earn a tab. Reporting
/// `oauth` for a card that has never had a credential gave every install an
/// Antigravity tab and a Codex tab purely from an unconfigured card.
///
/// Ported from macOS `required_card_source` (agent_usage.rs :1814-1823). A
/// transient failure is never `unconfigured`: it means the credential could
/// not be reached, not that it is absent.
fn required_card_source(outcome: &ProviderFetchOutcome, unconfigured: &str) -> &'static str {
    match outcome {
        ProviderFetchOutcome::Failure(ProviderFetchFailure::Terminal { display })
            if display == unconfigured =>
        {
            "unconfigured"
        }
        _ => "oauth",
    }
}

/// Claude's `/api/oauth/usage` rate-limits aggressively. The gate stores only
/// the cooldown deadline and the exact opaque binding that triggered it; display
/// snapshots live exclusively in the provider last-good cache.
#[derive(Debug, Default)]
struct ClaudeUsageGate {
    blocked_until: Option<DateTime<Utc>>,
    binding: Option<ProviderCacheBinding>,
}

impl ClaudeUsageGate {
    fn blocked_until_for(
        &mut self,
        binding: &ProviderCacheBinding,
        now: DateTime<Utc>,
    ) -> Option<DateTime<Utc>> {
        if self.binding.as_ref() != Some(binding) {
            self.blocked_until = None;
            self.binding = None;
            return None;
        }
        match self.blocked_until {
            Some(until) if until > now => Some(until),
            Some(_) => {
                self.blocked_until = None;
                self.binding = None;
                None
            }
            None => None,
        }
    }

    fn record_rate_limit(
        &mut self,
        binding: ProviderCacheBinding,
        retry_after: Option<DateTime<Utc>>,
        now: DateTime<Utc>,
    ) {
        self.blocked_until = Some(
            retry_after
                .filter(|until| *until > now)
                .unwrap_or_else(|| now + chrono::Duration::minutes(5)),
        );
        self.binding = Some(binding);
    }

    fn clear(&mut self) {
        self.blocked_until = None;
        self.binding = None;
    }
}

/// One 429 gate per Claude account (`account_key_component`), so a binding
/// mismatch in one account's gate can never wipe another account's cooldown.
type ClaudeUsageGates = HashMap<Option<String>, ClaudeUsageGate>;

static CLAUDE_USAGE_GATES: LazyLock<Mutex<ClaudeUsageGates>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));

fn with_gate<T>(account: Option<&str>, body: impl FnOnce(&mut ClaudeUsageGate) -> T) -> T {
    with_gate_in(&CLAUDE_USAGE_GATES, account, body)
}

fn with_gate_in<T>(
    gates: &Mutex<ClaudeUsageGates>,
    account: Option<&str>,
    body: impl FnOnce(&mut ClaudeUsageGate) -> T,
) -> T {
    let mut gates = gates
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    body(
        gates
            .entry(account_key_component(account).map(str::to_string))
            .or_default(),
    )
}

fn claude_gate_failure(
    binding: ProviderCacheBinding,
    blocked_until: DateTime<Utc>,
    now: DateTime<Utc>,
) -> ProviderFetchFailure {
    let wait_secs = (blocked_until - now).num_seconds().max(0);
    ProviderFetchFailure::transient(
        format!(
            "Claude OAuth usage endpoint is rate limited. Retrying automatically in ~{wait_secs}s."
        ),
        Some(binding),
        SafeTransportDiagnostic::rate_limited(429),
    )
}

fn parse_retry_after(value: Option<&reqwest::header::HeaderValue>) -> Option<DateTime<Utc>> {
    let raw = value?.to_str().ok()?.trim();
    if raw.is_empty() {
        return None;
    }
    if let Ok(seconds) = raw.parse::<i64>() {
        return (seconds >= 0).then(|| Utc::now() + chrono::Duration::seconds(seconds));
    }
    DateTime::parse_from_rfc2822(raw)
        .ok()
        .map(|t| t.with_timezone(&Utc))
}

/// `accountKey` of the Claude Desktop card. Not an absolute path, so
/// `claude_config_dirs::normalize` can never register a directory that
/// collides with it.
const CLAUDE_DESKTOP_ACCOUNT_KEY: &str = "claude-desktop";

/// At most this many Claude account fetches are in flight at once.
const MAX_ACCOUNT_FETCHES_IN_FLIGHT: usize = 4;

/// Which Claude card a fetch is for. Decides the account key, the credential
/// source the card may use, its durable history scope, and whether it asks
/// `/api/oauth/profile` for a merge identity.
#[derive(Debug, Clone, PartialEq, Eq)]
enum ClaudeAccount {
    /// The existing chain (env token -> CLI login -> setup token). `identify`
    /// is set only when another card exists that it could merge with; without
    /// one the primary makes no profile request, so its payload is unchanged.
    Primary { identify: bool },
    /// A configured `CLAUDE_CONFIG_DIR`, exactly as registered.
    ConfigDir(String),
    /// Claude Desktop's own login.
    Desktop,
}

impl ClaudeAccount {
    fn key(&self) -> Option<&str> {
        match self {
            Self::Primary { .. } => None,
            Self::ConfigDir(dir) => account_key_component(Some(dir)),
            Self::Desktop => Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
        }
    }

    fn wants_profile(&self) -> bool {
        match self {
            Self::Primary { identify } => *identify,
            Self::ConfigDir(_) | Self::Desktop => true,
        }
    }
}

enum ClaudeAccountRequest {
    Primary { identify: bool },
    ConfigDir(String),
    Desktop(Result<ClaudeCredentials, ProviderFetchFailure>),
}

/// The cards this run fetches, in precedence order: primary, then configured
/// directories in the order the user listed them, then Claude Desktop (only
/// when a Desktop login or a failure to read one exists).
fn claude_account_requests(
    config_dirs: Vec<String>,
    desktop: Result<Option<ClaudeCredentials>, ProviderFetchFailure>,
) -> Vec<ClaudeAccountRequest> {
    let desktop = desktop.transpose();
    let identify = !config_dirs.is_empty() || matches!(desktop, Some(Ok(_)));
    std::iter::once(ClaudeAccountRequest::Primary { identify })
        .chain(config_dirs.into_iter().map(ClaudeAccountRequest::ConfigDir))
        .chain(desktop.map(ClaudeAccountRequest::Desktop))
        .collect()
}

/// One account's raw fetch result, before merge and before
/// `apply_account_outcome_with` (history recording, last-good).
struct ClaudeAccountFetch {
    account_key: Option<String>,
    failure_source: &'static str,
    outcome: ProviderFetchOutcome,
    /// The identity remembered for this card's current binding, used when the
    /// fetch itself produced none (expired token, failed request). Always
    /// `None` for the primary, which is never merged away.
    remembered_scope: Option<AccountScope>,
}

impl ClaudeAccountFetch {
    /// A success carries the identity its own profile step settled on; any
    /// other outcome falls back to what this binding was last proved to be.
    fn merge_scope(&self) -> Option<&AccountScope> {
        match &self.outcome {
            ProviderFetchOutcome::Success { snapshot, .. } => snapshot.merge_scope.as_ref(),
            _ => self.remembered_scope.as_ref(),
        }
    }
}

/// Every Claude card this publication carries. The registry is read once per
/// run. With no configured directory and no Desktop login this is the single
/// primary card it was before multi-account support.
async fn fetch_claude_accounts() -> Vec<AgentUsageSnapshot> {
    let (config_dirs, generation) = crate::claude_config_dirs::snapshot();
    let requests = claude_account_requests(config_dirs, load_claude_desktop_login());
    let work: Vec<Pin<Box<dyn Future<Output = ClaudeAccountFetch>>>> = requests
        .into_iter()
        .map(|request| Box::pin(fetch_claude_account(request)) as Pin<Box<dyn Future<Output = _>>>)
        .collect();
    let fetched = join_bounded_ordered(work, MAX_ACCOUNT_FETCHES_IN_FLIGHT).await;
    let now = Utc::now();
    let _state = lock_claude_account_state();
    let (current_dirs, current_generation) = crate::claude_config_dirs::snapshot();
    settle_claude_run_with(
        &ClaudeAccountCaches::process(),
        generation,
        &current_dirs,
        current_generation,
        now,
        agent_account_scope::resolve_history_scope("claude", None),
        fetched,
        |snapshot| enrich_snapshot(snapshot, now.timestamp()),
    )
}

async fn fetch_claude_account(request: ClaudeAccountRequest) -> ClaudeAccountFetch {
    let (account, loaded) = match request {
        ClaudeAccountRequest::Primary { identify } => {
            let account = ClaudeAccount::Primary { identify };
            let (failure_source, outcome) = fetch_claude_inner(account.clone()).await;
            return ClaudeAccountFetch {
                account_key: None,
                failure_source,
                outcome,
                remembered_scope: None,
            };
        }
        ClaudeAccountRequest::ConfigDir(dir) => {
            let loaded = load_claude_config_dir_credentials_bounded(
                dir.clone(),
                CLAUDE_CONFIG_DIR_READ_TIMEOUT,
                load_claude_config_dir_credentials,
            )
            .await;
            (ClaudeAccount::ConfigDir(dir), loaded)
        }
        ClaudeAccountRequest::Desktop(loaded) => (ClaudeAccount::Desktop, loaded),
    };
    let account_key = account.key().map(str::to_string);
    let credentials = match loaded {
        Ok(credentials) => credentials,
        Err(failure) => {
            return ClaudeAccountFetch {
                account_key,
                failure_source: "oauth",
                outcome: ProviderFetchOutcome::Failure(failure),
                remembered_scope: None,
            };
        }
    };
    let binding = claude_cache_binding(&credentials);
    let remembered_scope = binding.as_ref().ok().and_then(|binding| {
        remembered_claude_identity(
            &CLAUDE_IDENTITY_CACHE,
            &claude_profile_slot(account_key.as_deref(), &binding.primary),
        )
        .map(|(scope, _)| scope)
    });
    let verified = binding.map_err(|_| {
        ProviderFetchFailure::terminal("Claude account identity could not be verified.")
    });
    let (failure_source, outcome) =
        request_after_verified_binding(verified, |binding| async move {
            Ok(fetch_claude_oauth_usage(credentials, binding, account).await)
        })
        .await
        .unwrap_or_else(|failure| ("oauth", ProviderFetchOutcome::Failure(failure)));
    ClaudeAccountFetch {
        account_key,
        failure_source,
        outcome,
        remembered_scope,
    }
}

/// Keep one card per known identity, then run only the kept cards through
/// `apply_account_outcome_with`, so a merged-away card records no history and
/// touches no last-good slot.
///
/// Within one identity a successful card wins over a failed one (a failed card
/// has its identity from `remembered_scope`); among several successes, or
/// several failures, the earliest by precedence wins (primary, then config
/// directories in order, then Desktop). The primary is never dropped: it only
/// has an identity when it succeeded, and then it is the earliest success. A
/// card whose identity is unknown is never merged: two cards for one account
/// is recoverable, hiding an account behind another is not.
fn publish_claude_accounts_with<F>(
    cache: &Mutex<ProviderLastGoodCache>,
    now: DateTime<Utc>,
    fetched: Vec<ClaudeAccountFetch>,
    mut enrich: F,
) -> Vec<AgentUsageSnapshot>
where
    F: FnMut(&mut AgentUsageSnapshot),
{
    let succeeded =
        |fetch: &ClaudeAccountFetch| matches!(fetch.outcome, ProviderFetchOutcome::Success { .. });
    let winner = |scope: &AccountScope| {
        fetched
            .iter()
            .enumerate()
            .filter(|(_, fetch)| fetch.merge_scope() == Some(scope))
            .min_by_key(|(index, fetch)| (!succeeded(fetch), *index))
            .map(|(index, _)| index)
    };
    let keep: Vec<bool> = fetched
        .iter()
        .enumerate()
        .map(|(index, fetch)| match fetch.merge_scope() {
            Some(scope) if fetch.account_key.is_some() => winner(scope) == Some(index),
            _ => true,
        })
        .collect();
    fetched
        .into_iter()
        .zip(keep)
        .filter(|(_, keep)| *keep)
        .filter_map(|(fetch, _)| {
            apply_account_outcome_with(
                cache,
                "claude",
                fetch.account_key.as_deref(),
                fetch.failure_source,
                now,
                fetch.outcome,
                &mut enrich,
            )
        })
        .collect()
}

/// Serializes the end of a Claude run (registry re-check, apply) against the
/// registry setter's purge, so a directory removed mid-run can never have its
/// state written back after the purge. Held only across synchronous code.
static CLAUDE_ACCOUNT_STATE_LOCK: Mutex<()> = Mutex::new(());

fn lock_claude_account_state() -> std::sync::MutexGuard<'static, ()> {
    CLAUDE_ACCOUNT_STATE_LOCK
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// Every per-account Claude cache, so the purge and the end-of-run settle can
/// be driven against test instances.
struct ClaudeAccountCaches<'a> {
    last_good: &'a Mutex<ProviderLastGoodCache>,
    gates: &'a Mutex<ClaudeUsageGates>,
    headers: &'a Mutex<ClaudeHeaderCache>,
    profiles: &'a Mutex<ClaudeProfileCache>,
    identities: &'a Mutex<ClaudeIdentityCache>,
}

impl ClaudeAccountCaches<'static> {
    fn process() -> Self {
        Self {
            last_good: &PROVIDER_LAST_GOOD,
            gates: &CLAUDE_USAGE_GATES,
            headers: &CLAUDE_HEADER_CACHE,
            profiles: &CLAUDE_PROFILE_CACHE,
            identities: &CLAUDE_IDENTITY_CACHE,
        }
    }
}

/// End of a Claude run, under `CLAUDE_ACCOUNT_STATE_LOCK`. If the registry
/// moved since the run's snapshot, every config-directory card of this run is
/// dropped unpublished and unapplied — not only removed ones: a directory
/// removed and re-added under the same path may hold a different login by
/// now, and the path alone cannot tell. The next poll refetches under the
/// current generation. Every entry an in-flight fetch of a now-removed
/// directory wrote (gate, header, profile, identity) is purged again before
/// the primary and Desktop cards are applied.
///
/// The primary card always names its history scope: the per-installation
/// constant `primary_history_scope` (`resolve_history_scope("claude", None)`),
/// which depends on no credential. An error, unconfigured or last-good card
/// of the primary carries it too, because the C# window card joins stored
/// series to a live card strictly by `historyScope.scope`; without it the
/// primary's history would vanish whenever its fetch fails. This happens
/// after enrich, and error cards have no windows, so nothing new is recorded.
/// Other providers and non-primary Claude cards are untouched: their failure
/// cards keep `NoTrustedEvidence`.
fn settle_claude_run_with<F>(
    caches: &ClaudeAccountCaches<'_>,
    snapshot_generation: u64,
    current_dirs: &[String],
    current_generation: u64,
    now: DateTime<Utc>,
    primary_history_scope: Result<HistoryScope, AccountScopeError>,
    fetched: Vec<ClaudeAccountFetch>,
    enrich: F,
) -> Vec<AgentUsageSnapshot>
where
    F: FnMut(&mut AgentUsageSnapshot),
{
    let fetched = if current_generation == snapshot_generation {
        fetched
    } else {
        purge_removed_claude_accounts_in(caches, current_dirs);
        fetched
            .into_iter()
            .filter(|fetch| {
                matches!(
                    fetch.account_key.as_deref(),
                    None | Some(CLAUDE_DESKTOP_ACCOUNT_KEY)
                )
            })
            .collect()
    };
    let mut published = publish_claude_accounts_with(caches.last_good, now, fetched, enrich);
    for card in &mut published {
        if card.account_key.is_none() && card.history_scope.is_err() {
            card.history_scope = primary_history_scope.clone();
        }
    }
    published
}

/// Replace the config-directory registry and purge the removed accounts'
/// state as one step under `CLAUDE_ACCOUNT_STATE_LOCK`. `replace` installs the
/// new registry and returns `(report, registered)`; on `Err` nothing is purged.
///
/// Both halves must sit under the one lock: released in between, two
/// concurrent setters could replace in one order and purge in the other,
/// deleting the state of accounts that remain registered. The lock order
/// matches `settle_claude_run_with` (state lock, then registry, then caches),
/// and nothing here awaits.
pub(crate) fn replace_claude_config_dirs<T, E>(
    replace: impl FnOnce() -> Result<(T, Vec<String>), E>,
) -> Result<T, E> {
    replace_claude_config_dirs_in(
        &CLAUDE_ACCOUNT_STATE_LOCK,
        &ClaudeAccountCaches::process(),
        replace,
    )
}

fn replace_claude_config_dirs_in<T, E>(
    state_lock: &Mutex<()>,
    caches: &ClaudeAccountCaches<'_>,
    replace: impl FnOnce() -> Result<(T, Vec<String>), E>,
) -> Result<T, E> {
    let _state = state_lock
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let (report, registered) = replace()?;
    // Forget every per-account entry (last-good, 429 gate, header, profile,
    // identity) of a directory no longer registered. The primary and the
    // Claude Desktop card are never purged here.
    purge_removed_claude_accounts_in(caches, &registered);
    Ok(report)
}

fn purge_removed_claude_accounts_in(caches: &ClaudeAccountCaches<'_>, configured: &[String]) {
    let ClaudeAccountCaches {
        last_good,
        gates,
        headers,
        profiles,
        identities,
    } = caches;
    let removed = |key: &Option<String>| {
        key.as_deref().is_some_and(|key| {
            key != CLAUDE_DESKTOP_ACCOUNT_KEY && !configured.iter().any(|dir| dir == key)
        })
    };
    lock_last_good(last_good)
        .entries
        .retain(|(client_id, key), _| client_id != "claude" || !removed(key));
    gates
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .retain(|key, _| !removed(key));
    headers
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .retain(|key, _| !removed(key));
    profiles
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .retain(|(key, _), _| !removed(key));
    identities
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .retain(|(key, _), _| !removed(key));
}

/// Run `futures` concurrently, at most `limit` at a time, and return their
/// results in input order (the order cards are laid out in), not completion
/// order. Polled in place on the calling task: the Windows core has no
/// `LocalSet`, and a Claude fetch is not `Send`.
async fn join_bounded_ordered<'a, T>(
    futures: Vec<Pin<Box<dyn Future<Output = T> + 'a>>>,
    limit: usize,
) -> Vec<T> {
    let limit = limit.max(1);
    let total = futures.len();
    let mut pending: Vec<Option<Pin<Box<dyn Future<Output = T> + 'a>>>> =
        futures.into_iter().map(Some).collect();
    let mut results: Vec<Option<T>> = (0..total).map(|_| None).collect();
    let mut active: Vec<usize> = Vec::new();
    let mut next = 0;
    std::future::poll_fn(|cx| {
        loop {
            while active.len() < limit && next < total {
                active.push(next);
                next += 1;
            }
            let before = active.len();
            active.retain(|&index| {
                let Some(future) = pending[index].as_mut() else {
                    return false;
                };
                match future.as_mut().poll(cx) {
                    std::task::Poll::Ready(value) => {
                        results[index] = Some(value);
                        pending[index] = None;
                        false
                    }
                    std::task::Poll::Pending => true,
                }
            });
            // Refill freed slots and poll the newcomers in this same call, so
            // each of them registers a waker before we return Pending.
            if active.len() == before || next == total {
                break;
            }
        }
        if active.is_empty() && next == total {
            std::task::Poll::Ready(())
        } else {
            std::task::Poll::Pending
        }
    })
    .await;
    results.into_iter().flatten().collect()
}

async fn fetch_codex_inner() -> ProviderFetchOutcome {
    let loaded = match load_codex_credentials() {
        Ok(credentials) => credentials,
        Err(display) => {
            return ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(display));
        }
    };
    let verified = if codex_credentials_needs_refresh(&loaded.access_token, loaded.last_refresh) {
        refresh_codex_credentials(&loaded.auth_path).await
    } else {
        resolve_codex_cache_binding(&loaded)
            .map(|binding| (loaded, binding))
            .map_err(|_| {
                ProviderFetchFailure::terminal("Codex account identity could not be verified.")
            })
    };
    let (credentials, cache_binding, response) =
        match request_after_verified_binding(verified, |(credentials, cache_binding)| async move {
            let client = provider_http_client_builder()
                .timeout(std::time::Duration::from_secs(30))
                .build()
                .map_err(|_| {
                    ProviderFetchFailure::terminal("Codex usage client could not be created.")
                })?;
            let request_account_id = credentials
                .account_id
                .as_deref()
                .map(str::trim)
                .filter(|value| !value.is_empty());
            let mut request = client
                .get(CODEX_USAGE_URL)
                .bearer_auth(&credentials.access_token)
                .header(reqwest::header::ACCEPT, "application/json")
                .header(reqwest::header::USER_AGENT, "TokenBar");
            if let Some(account_id) = request_account_id {
                request = request.header("ChatGPT-Account-Id", account_id);
            }
            let response = request.send().await.map_err(|error| {
                ProviderFetchFailure::from_send_error(
                    "Codex usage request failed. Retrying automatically.",
                    Some(cache_binding.clone()),
                    &error,
                )
            })?;
            Ok((credentials, cache_binding, response))
        })
        .await
        {
            Ok(verified) => verified,
            Err(failure) => return ProviderFetchOutcome::Failure(failure),
        };
    let request_account_id = credentials
        .account_id
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty());
    let status = response.status().as_u16();
    let body = match read_response_body(status, false, || async {
        response.text().await.map_err(|error| {
            TransportErrorFacts::from_reqwest(&error, TransportPhase::ResponseBody)
        })
    })
    .await
    {
        Ok(body) => body,
        Err(ResponseReadFailure::Transient(diagnostic)) => {
            return ProviderFetchOutcome::Failure(ProviderFetchFailure::transient(
                "Codex usage request failed. Retrying automatically.",
                Some(cache_binding),
                diagnostic,
            ));
        }
        Err(ResponseReadFailure::Terminal(401 | 403)) => {
            return ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                "Codex OAuth token expired or invalid. Run `codex` to log in again.",
            ));
        }
        Err(ResponseReadFailure::Terminal(status)) => {
            return ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(format!(
                "Codex usage API rejected the request (status {status})."
            )));
        }
    };

    let mut usage: CodexUsageResponse = match serde_json::from_str(&body) {
        Ok(usage) => usage,
        Err(_) => {
            return ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                "Codex usage response could not be decoded.",
            ));
        }
    };
    let now = Utc::now();
    let account_scope = cache_binding
        .corroborating
        .clone()
        .unwrap_or_else(|| cache_binding.primary.clone());
    let identity = Some(AgentIdentity {
        email: credentials.id_token.as_deref().and_then(jwt_email),
        plan: usage.plan_type.as_deref().map(clean_plan).or_else(|| {
            credentials
                .id_token
                .as_deref()
                .and_then(jwt_plan)
                .map(clean_plan)
        }),
    });
    let windows = codex_windows(
        usage.rate_limit.as_ref(),
        usage.additional_rate_limits.as_deref(),
        now,
    );
    let finite_balance = finite_codex_balance(usage.credits.as_ref());
    if let Some(credits) = usage.credits.as_mut() {
        credits.balance = finite_balance;
    }
    if windows.is_empty() && finite_balance.is_none() {
        return ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
            "Codex usage API returned no usable quota data.",
        ));
    }

    let history_scope = codex_history_scope(&credentials);

    if let (Some(request_account_id), Ok(history_scope)) =
        (request_account_id, history_scope.as_ref())
    {
        // The importer keys on the same history scope as the live path. Keying
        // it on the account scope would let imported v2 samples land in a series
        // the live path never touches again, the moment the two diverge.
        let _ = crate::agent_quota_history::migrate_codex_v2(
            request_account_id,
            history_scope,
            now.timestamp(),
            stranded_series_fold,
        );
    }

    ProviderFetchOutcome::Success {
        snapshot: AgentUsageSnapshot {
            account_key: None,
            merge_scope: None,
            client_id: "codex".to_string(),
            source: "oauth".to_string(),
            updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
            identity,
            account_scope: Ok(account_scope),
            history_scope,
            windows,
            credits: usage.credits.map(|credits| CreditsSnapshot {
                remaining: credits.balance,
                unlimited: credits.unlimited,
            }),
            error: None,
            transport_diagnostic: None,
        },
        cache_binding: Some(cache_binding),
    }
}

fn resolve_codex_cache_binding(
    credentials: &CodexCredentials,
) -> Result<ProviderCacheBinding, AccountScopeError> {
    let primary = agent_account_scope::resolve_credential(
        "codex",
        credentials.scope_slot.semantic_source,
        &credentials.scope_slot.canonical_location,
        credentials.scope_marker(),
    )?;
    let corroborating = credentials
        .account_id
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(|account_id| {
            agent_account_scope::resolve_authoritative(
                "codex",
                AuthoritativeIdKind::OpaqueId,
                account_id,
            )
        })
        .transpose()?;
    Ok(ProviderCacheBinding::new(primary, corroborating))
}

fn claude_cache_binding(
    credentials: &ClaudeCredentials,
) -> Result<ProviderCacheBinding, AccountScopeError> {
    credentials
        .resolve_account_scope()
        .map(ProviderCacheBinding::primary)
}

/// Clear the primary account's 429 gate only when its final outcome is
/// `unconfigured` (the rule PR #151 introduced): a cooldown recorded against a
/// binding survives every outcome that still resolved some credential,
/// including a setup token serving while the CLI login is absent. Other
/// accounts' gates are untouched.
fn clear_claude_gate_if_unconfigured(failure_source: &str, gate: &mut ClaudeUsageGate) {
    if failure_source == "unconfigured" {
        gate.clear();
    }
}

/// The primary card's chain: `CLAUDE_CODE_OAUTH_TOKEN`, then the stored CLI
/// login, then the setup-token item. Claude Desktop is its own card, never a
/// fallback here.
async fn fetch_claude_inner(account: ClaudeAccount) -> (&'static str, ProviderFetchOutcome) {
    if let Some(token) = resolve_claude_code_oauth_token().await {
        return fetch_claude_setup_token(token).await;
    }

    let login = load_claude_login_credentials();
    let gate_account = account.clone();
    let (failure_source, outcome) = fetch_claude_login_or_setup_with(
        login,
        |credentials| fetch_claude_verified_login(credentials, account),
        resolve_claude_keychain_token,
        fetch_claude_setup_token,
    )
    .await;
    with_gate(gate_account.key(), |gate| {
        clear_claude_gate_if_unconfigured(failure_source, gate)
    });
    (failure_source, outcome)
}

async fn fetch_claude_verified_login(
    credentials: ClaudeCredentials,
    account: ClaudeAccount,
) -> (&'static str, ProviderFetchOutcome) {
    let verified = claude_cache_binding(&credentials).map_err(|_| {
        ProviderFetchFailure::terminal("Claude account identity could not be verified.")
    });
    request_after_verified_binding(verified, |binding| async move {
        Ok(fetch_claude_oauth_usage(credentials, binding, account).await)
    })
    .await
    .unwrap_or_else(|failure| ("oauth", ProviderFetchOutcome::Failure(failure)))
}

/// Primary only: a setup token has no backing account directory.
async fn fetch_claude_setup_token(
    token: ResolvedClaudeToken,
) -> (&'static str, ProviderFetchOutcome) {
    let credentials = claude_credentials_from_access_token(token);
    let verified = claude_cache_binding(&credentials).map_err(|_| {
        ProviderFetchFailure::terminal("Claude setup-token account identity could not be verified.")
    });
    let outcome = request_after_verified_binding(verified, |binding| async move {
        Ok(claude_header_snapshot(
            &credentials,
            &ClaudeAccount::Primary { identify: false },
            Utc::now(),
            Ok(binding.primary.clone()),
            Some(binding),
        )
        .await)
    })
    .await
    .unwrap_or_else(ProviderFetchOutcome::Failure);
    ("setup-token", outcome)
}

async fn fetch_claude_login_or_setup_with<Login, LoginFuture, LoadSetup, Setup, SetupFuture>(
    login: ClaudeLoginResolution,
    request_login: Login,
    load_setup: LoadSetup,
    request_setup: Setup,
) -> (&'static str, ProviderFetchOutcome)
where
    Login: FnOnce(ClaudeCredentials) -> LoginFuture,
    LoginFuture: std::future::Future<Output = (&'static str, ProviderFetchOutcome)>,
    LoadSetup: FnOnce() -> Result<Option<ResolvedClaudeToken>, String>,
    Setup: FnOnce(ResolvedClaudeToken) -> SetupFuture,
    SetupFuture: std::future::Future<Output = (&'static str, ProviderFetchOutcome)>,
{
    match login {
        ClaudeLoginResolution::Ready(credentials) => request_login(credentials).await,
        ClaudeLoginResolution::Terminal => (
            "oauth",
            ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                CLAUDE_CREDENTIALS_LOAD_ERROR,
            )),
        ),
        ClaudeLoginResolution::Absent | ClaudeLoginResolution::ExplicitLogout => {
            match load_setup() {
                Ok(Some(token)) => request_setup(token).await,
                Ok(None) => (
                    "unconfigured",
                    ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                        CLAUDE_UNCONFIGURED_ERROR,
                    )),
                ),
                Err(_) => (
                    "setup-token",
                    ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                        CLAUDE_CREDENTIALS_LOAD_ERROR,
                    )),
                ),
            }
        }
    }
}

async fn fetch_claude_oauth_usage(
    credentials: ClaudeCredentials,
    pre_binding: ProviderCacheBinding,
    account: ClaudeAccount,
) -> (&'static str, ProviderFetchOutcome) {
    let gate_account = account.clone();
    let header_account = account.clone();
    fetch_claude_login_usage_with(
        credentials,
        pre_binding,
        Utc::now(),
        move |binding, now| {
            with_gate(gate_account.key(), |gate| {
                gate.blocked_until_for(binding, now)
            })
        },
        |credentials| async move {
            // Unreachable for a read-only source (the expiry guard in
            // `fetch_claude_login_usage_with` returns first); kept so no
            // future path can take the refresh lock for one.
            if credentials.source.is_read_only() {
                return Err(ProviderFetchFailure::terminal(
                    CLAUDE_READ_ONLY_REFRESH_ERROR,
                ));
            }
            refresh_claude_credentials(&credentials).await
        },
        move |credentials, account_scope, cache_binding| async move {
            claude_header_snapshot(
                &credentials,
                &header_account,
                Utc::now(),
                Ok(account_scope),
                cache_binding,
            )
            .await
        },
        move |credentials, account_scope, cache_binding, gate_binding| async move {
            fetch_claude_oauth_usage_request(
                &credentials,
                &account,
                account_scope,
                cache_binding,
                gate_binding,
            )
            .await
        },
    )
    .await
}

async fn fetch_claude_login_usage_with<
    Gate,
    Refresh,
    RefreshFuture,
    Header,
    HeaderFuture,
    Oauth,
    OauthFuture,
>(
    credentials: ClaudeCredentials,
    pre_binding: ProviderCacheBinding,
    now: DateTime<Utc>,
    blocked_until_for: Gate,
    refresh: Refresh,
    header: Header,
    oauth: Oauth,
) -> (&'static str, ProviderFetchOutcome)
where
    Gate: FnOnce(&ProviderCacheBinding, DateTime<Utc>) -> Option<DateTime<Utc>>,
    Refresh: FnOnce(ClaudeCredentials) -> RefreshFuture,
    RefreshFuture: std::future::Future<
        Output = Result<
            (
                ClaudeCredentials,
                AccountScope,
                Option<ProviderCacheBinding>,
            ),
            ProviderFetchFailure,
        >,
    >,
    Header: FnOnce(ClaudeCredentials, AccountScope, Option<ProviderCacheBinding>) -> HeaderFuture,
    HeaderFuture: std::future::Future<Output = ProviderFetchOutcome>,
    Oauth: FnOnce(
        ClaudeCredentials,
        AccountScope,
        Option<ProviderCacheBinding>,
        ProviderCacheBinding,
    ) -> OauthFuture,
    OauthFuture: std::future::Future<Output = (&'static str, ProviderFetchOutcome)>,
{
    let header_route = !credentials.scopes.is_empty()
        && !credentials
            .scopes
            .iter()
            .any(|scope| scope == "user:profile");
    if !header_route {
        if let Some(blocked_until) = blocked_until_for(&pre_binding, now) {
            return (
                "oauth",
                ProviderFetchOutcome::Failure(claude_gate_failure(pre_binding, blocked_until, now)),
            );
        }
    }

    // Never refresh a read-only credential (Claude Desktop or a configured
    // directory): stop before the refresh lock, the network and any write.
    if let Some(expired) = claude_read_only_expired_message(&credentials.source) {
        if claude_read_only_credentials_expired(&credentials, now) {
            return (
                "oauth",
                ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(expired)),
            );
        }
    }

    let (credentials, account_scope, cache_binding) = if claude_credentials_expired(&credentials) {
        match refresh(credentials).await {
            Ok(refreshed) => refreshed,
            Err(failure) => return ("oauth", ProviderFetchOutcome::Failure(failure)),
        }
    } else {
        let account_scope = pre_binding.primary.clone();
        (credentials, account_scope, Some(pre_binding))
    };

    if header_route {
        return (
            "setup-token",
            header(credentials, account_scope, cache_binding).await,
        );
    }

    let gate_binding = ProviderCacheBinding::primary(account_scope.clone());
    oauth(credentials, account_scope, cache_binding, gate_binding).await
}

fn claude_unauthorized_message(source: &ClaudeCredentialSource) -> &'static str {
    match source {
        ClaudeCredentialSource::Desktop => {
            "Claude Desktop login was rejected. Sign in to Claude Desktop again."
        }
        ClaudeCredentialSource::ConfigDir(_) => CLAUDE_CONFIG_DIR_REJECTED_ERROR,
        _ => "Claude OAuth token expired or invalid. Run `claude` to re-authenticate.",
    }
}

fn claude_denied_message(source: &ClaudeCredentialSource) -> &'static str {
    match source {
        ClaudeCredentialSource::Desktop => {
            "Claude OAuth usage was denied. Sign in to Claude Desktop again."
        }
        ClaudeCredentialSource::ConfigDir(_) => CLAUDE_CONFIG_DIR_REJECTED_ERROR,
        _ => "Claude OAuth usage was denied. Run `claude logout && claude login` to grant user:profile.",
    }
}

async fn fetch_claude_oauth_usage_request(
    credentials: &ClaudeCredentials,
    account: &ClaudeAccount,
    account_scope: AccountScope,
    cache_binding: Option<ProviderCacheBinding>,
    gate_binding: ProviderCacheBinding,
) -> (&'static str, ProviderFetchOutcome) {
    let client = match provider_http_client_builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
    {
        Ok(client) => client,
        Err(_) => {
            return (
                "oauth",
                ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                    "Claude usage client could not be created.",
                )),
            );
        }
    };

    let response = match client
        .get(CLAUDE_USAGE_URL)
        .bearer_auth(&credentials.access_token)
        .header(reqwest::header::ACCEPT, "application/json")
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .header(reqwest::header::USER_AGENT, claude_user_agent())
        .header("anthropic-beta", "oauth-2025-04-20")
        .send()
        .await
    {
        Ok(response) => response,
        Err(error) => {
            return (
                "oauth",
                ProviderFetchOutcome::Failure(ProviderFetchFailure::from_send_error(
                    "Claude usage request failed. Retrying automatically.",
                    cache_binding,
                    &error,
                )),
            );
        }
    };
    let status = response.status().as_u16();
    let retry_after = (status == 429)
        .then(|| parse_retry_after(response.headers().get(reqwest::header::RETRY_AFTER)))
        .flatten();
    let body = match read_response_body(status, true, || async {
        response.text().await.map_err(|error| {
            TransportErrorFacts::from_reqwest(&error, TransportPhase::ResponseBody)
        })
    })
    .await
    {
        Ok(body) => body,
        Err(ResponseReadFailure::Transient(diagnostic)) => {
            if status == 429 {
                with_gate(account.key(), |gate| {
                    gate.record_rate_limit(gate_binding, retry_after, Utc::now())
                });
            }
            return (
                "oauth",
                ProviderFetchOutcome::Failure(ProviderFetchFailure::transient(
                    "Claude usage request failed. Retrying automatically.",
                    cache_binding,
                    diagnostic,
                )),
            );
        }
        Err(ResponseReadFailure::Terminal(401)) => {
            return (
                "oauth",
                ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                    claude_unauthorized_message(&credentials.source),
                )),
            );
        }
        Err(ResponseReadFailure::Terminal(403)) => {
            return (
                "oauth",
                ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                    claude_denied_message(&credentials.source),
                )),
            );
        }
        Err(ResponseReadFailure::Terminal(status)) => {
            return (
                "oauth",
                ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(format!(
                    "Claude usage API rejected the request (status {status})."
                ))),
            );
        }
    };

    if status == 403 {
        if body.contains("user:profile") {
            return (
                "setup-token",
                claude_header_snapshot(
                    credentials,
                    account,
                    Utc::now(),
                    Ok(account_scope),
                    cache_binding,
                )
                .await,
            );
        }
        return (
            "oauth",
            ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(claude_denied_message(
                &credentials.source,
            ))),
        );
    }

    let usage: ClaudeUsageResponse = match serde_json::from_str(&body) {
        Ok(usage) => usage,
        Err(_) => {
            return (
                "oauth",
                ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                    "Claude usage response could not be decoded.",
                )),
            );
        }
    };
    let now = Utc::now();
    let windows = claude_windows(&usage, now);
    if windows.is_empty() {
        return (
            "oauth",
            ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                "Claude usage API returned no usable quota windows.",
            )),
        );
    }
    with_gate(account.key(), ClaudeUsageGate::clear);

    let profile = if account.wants_profile() {
        Some(
            claude_profile_identity(
                &client,
                account.key(),
                &credentials.access_token,
                &account_scope,
            )
            .await,
        )
    } else {
        None
    };
    let stored_plan = first_non_empty([
        credentials.subscription_type.as_deref(),
        credentials.rate_limit_tier.as_deref(),
    ])
    .map(clean_plan);
    // The primary keeps its stored plan label (its payload is unchanged by
    // multi-account support); every other card prefers the live profile plan,
    // which is the only plan a Desktop login carries at all.
    let plan = match account {
        ClaudeAccount::Primary { .. } => stored_plan,
        _ => profile
            .as_ref()
            .and_then(|profile| profile.plan.clone())
            .or(stored_plan),
    };

    (
        "oauth",
        ProviderFetchOutcome::Success {
            snapshot: AgentUsageSnapshot {
                account_key: None,
                merge_scope: profile
                    .as_ref()
                    .and_then(|profile| profile.scopes.as_ref())
                    .map(|(scope, _)| scope.clone()),
                client_id: "claude".to_string(),
                source: "oauth".to_string(),
                updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
                identity: Some(AgentIdentity { email: None, plan }),
                account_scope: Ok(account_scope),
                history_scope: claude_account_history_scope(account, credentials, profile.as_ref()),
                windows,
                credits: claude_credits(usage.extra_usage.as_ref()),
                error: None,
                transport_diagnostic: None,
            },
            cache_binding,
        },
    )
}

/// Fallback for inference-only tokens (`claude setup-token`): the oauth/usage
/// endpoint requires `user:profile`, but a minimal `/v1/messages` request the
/// token *can* make returns `anthropic-ratelimit-unified-*` headers carrying the
/// same Session/Weekly windows. Reads headers on 200 AND 429 (an over-limit
/// token still returns them). Does NOT arm the oauth/usage rate-limit gate.
/// Cache for the header-probe windows. The probe is a real `/v1/messages`
/// inference (it spends the very budget it measures), so reuse the result across
/// the frequent quota polls (60s popover / 300s tray) instead of probing on
/// every refresh. Keyed on the token so a changed token re-probes.
/// `(fetched_at, token, windows)` — the token keys the entry so a changed token
/// re-probes rather than serving another account's cached windows.
/// One entry per Claude account (`account_key_component`), each still keyed
/// on its token.
type ClaudeHeaderCacheEntry = (DateTime<Utc>, String, Vec<UsageWindow>);
type ClaudeHeaderCache = HashMap<Option<String>, ClaudeHeaderCacheEntry>;
static CLAUDE_HEADER_CACHE: LazyLock<Mutex<ClaudeHeaderCache>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));
const CLAUDE_HEADER_TTL_SECS: i64 = 300;

/// Refresh the relative `reset_text` on cached header windows so a 300s-cached
/// probe doesn't show a frozen countdown. Returns None if any window's reset has
/// already passed — the cache is then stale, so the caller re-probes for fresh
/// utilization instead of serving post-reset numbers.
fn refresh_cached_windows(windows: &[UsageWindow], now: DateTime<Utc>) -> Option<Vec<UsageWindow>> {
    let mut refreshed = Vec::with_capacity(windows.len());
    for window in windows {
        let mut window = window.clone();
        if let Some(reset) = window.resets_at.as_deref().and_then(parse_datetime) {
            if now >= reset {
                return None;
            }
            window.reset_text = Some(reset_text(reset, now));
        }
        refreshed.push(window);
    }
    Some(refreshed)
}

/// The cached header windows of `account`, if they were probed with this
/// exact token within the TTL and no reset has passed since.
fn claude_cached_header_windows(
    cache: &Mutex<ClaudeHeaderCache>,
    account: Option<&str>,
    access_token: &str,
    now: DateTime<Utc>,
) -> Option<Vec<UsageWindow>> {
    let guard = cache.lock().unwrap_or_else(|e| e.into_inner());
    let (fetched_at, token, windows) =
        guard.get(&account_key_component(account).map(str::to_string))?;
    if token != access_token || (now - *fetched_at).num_seconds() >= CLAUDE_HEADER_TTL_SECS {
        return None;
    }
    refresh_cached_windows(windows, now)
}

fn store_claude_header_windows(
    cache: &Mutex<ClaudeHeaderCache>,
    account: Option<&str>,
    access_token: &str,
    now: DateTime<Utc>,
    windows: Vec<UsageWindow>,
) {
    cache.lock().unwrap_or_else(|e| e.into_inner()).insert(
        account_key_component(account).map(str::to_string),
        (now, access_token.to_string(), windows),
    );
}

async fn fetch_claude_via_headers(
    credentials: &ClaudeCredentials,
    account: Option<&str>,
    attempt_binding: Option<ProviderCacheBinding>,
) -> Result<Vec<UsageWindow>, ProviderFetchFailure> {
    let access_token = credentials.access_token.as_str();
    if let Some(cached) =
        claude_cached_header_windows(&CLAUDE_HEADER_CACHE, account, access_token, Utc::now())
    {
        return Ok(cached);
    }

    let client = provider_http_client_builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|_| {
            ProviderFetchFailure::terminal("Claude header-probe client could not be created.")
        })?;

    let response = client
        .post(CLAUDE_MESSAGES_URL)
        .bearer_auth(access_token)
        .header(reqwest::header::ACCEPT, "application/json")
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .header(reqwest::header::USER_AGENT, claude_user_agent())
        .header("anthropic-version", "2023-06-01")
        .header("anthropic-beta", "oauth-2025-04-20")
        .json(&serde_json::json!({
            "model": CLAUDE_PROBE_MODEL,
            "max_tokens": 1,
            "messages": [{ "role": "user", "content": "hi" }],
        }))
        .send()
        .await
        .map_err(|error| {
            ProviderFetchFailure::from_send_error(
                "Claude header probe failed. Retrying automatically.",
                attempt_binding.clone(),
                &error,
            )
        })?;

    let status = response.status().as_u16();
    let windows = parse_unified_ratelimit_windows(response.headers(), Utc::now());
    if (200..=299).contains(&status) || status == 429 {
        if windows.is_empty() {
            if status == 429 {
                return Err(ProviderFetchFailure::transient(
                    "Claude header probe is rate limited. Retrying automatically.",
                    attempt_binding,
                    SafeTransportDiagnostic::rate_limited(status),
                ));
            }
            return Err(ProviderFetchFailure::terminal(
                "Claude header probe returned no usable rate-limit headers.",
            ));
        }
        store_claude_header_windows(
            &CLAUDE_HEADER_CACHE,
            account,
            access_token,
            Utc::now(),
            windows.clone(),
        );
        return Ok(windows);
    }
    if (500..=599).contains(&status) {
        return Err(ProviderFetchFailure::transient(
            "Claude header probe failed. Retrying automatically.",
            attempt_binding,
            SafeTransportDiagnostic::server_error(status),
        ));
    }
    Err(ProviderFetchFailure::terminal(
        claude_header_rejection_message(&credentials.source, status),
    ))
}

fn claude_header_rejection_message(source: &ClaudeCredentialSource, status: u16) -> String {
    match (source, status) {
        (ClaudeCredentialSource::Desktop | ClaudeCredentialSource::ConfigDir(_), 401 | 403) => {
            claude_unauthorized_message(source).to_string()
        }
        (_, 401 | 403) => "Claude setup-token expired or lacks access.".to_string(),
        _ => format!("Claude header probe rejected the request (status {status})."),
    }
}

/// The header route proves no `user:profile` scope, so it never has a
/// profile identity: a Desktop card here records no history and never merges.
async fn claude_header_snapshot(
    credentials: &ClaudeCredentials,
    account: &ClaudeAccount,
    now: DateTime<Utc>,
    account_scope: Result<AccountScope, AccountScopeError>,
    cache_binding: Option<ProviderCacheBinding>,
) -> ProviderFetchOutcome {
    let windows =
        match fetch_claude_via_headers(credentials, account.key(), cache_binding.clone()).await {
            Ok(windows) => windows,
            Err(failure) => return ProviderFetchOutcome::Failure(failure),
        };
    ProviderFetchOutcome::Success {
        snapshot: AgentUsageSnapshot {
            account_key: None,
            merge_scope: None,
            client_id: "claude".to_string(),
            source: "setup-token".to_string(),
            updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
            identity: Some(AgentIdentity {
                email: None,
                plan: first_non_empty([
                    credentials.subscription_type.as_deref(),
                    credentials.rate_limit_tier.as_deref(),
                ])
                .map(clean_plan),
            }),
            account_scope,
            history_scope: claude_account_history_scope(account, credentials, None),
            windows,
            credits: None,
            error: None,
            transport_diagnostic: None,
        },
        cache_binding,
    }
}

fn claude_account_history_scope(
    account: &ClaudeAccount,
    credentials: &ClaudeCredentials,
    profile: Option<&ClaudeProfileIdentity>,
) -> Result<HistoryScope, AccountScopeError> {
    claude_account_history_scope_with(
        account,
        credentials,
        profile,
        agent_account_scope::resolve_history_scope,
    )
}

/// Who a Claude card's durable samples belong to, decided per card:
///
/// - primary: the per-installation constant on every route, unchanged (every
///   existing series is keyed on it). Claude's usage payload carries no owner
///   ID, and a lineage — which a CLI refresh-token rotation moves — is what
///   stranded the old series;
/// - configured directory D: authoritative `config-dir:<sha256(D)>`, but only
///   when the credential was read from D's own `.credentials.json` — any other
///   source would record someone else's numbers under D (macOS inv. A7). Keyed
///   on the path, as on macOS: renaming the directory starts a new series;
/// - Claude Desktop: authoritative `profile:<account>\0<org>` from this
///   fetch's binding-keyed profile; without one, no history (the card still
///   shows, only samples are withheld).
///
/// `Err` is refusal, never a fallback to the primary's constant: that would
/// merge another account's samples into the primary's series.
fn claude_account_history_scope_with<R>(
    account: &ClaudeAccount,
    credentials: &ClaudeCredentials,
    profile: Option<&ClaudeProfileIdentity>,
    resolve: R,
) -> Result<HistoryScope, AccountScopeError>
where
    R: FnOnce(&str, Option<(AuthoritativeIdKind, &str)>) -> Result<HistoryScope, AccountScopeError>,
{
    match account {
        ClaudeAccount::Primary { .. } => resolve("claude", None),
        ClaudeAccount::ConfigDir(dir) => match &credentials.source {
            ClaudeCredentialSource::ConfigDir(source_dir) if source_dir == Path::new(dir) => {
                let evidence = format!("config-dir:{}", sha256_hex_exact(dir.as_bytes()));
                resolve(
                    "claude",
                    Some((AuthoritativeIdKind::OpaqueId, evidence.as_str())),
                )
            }
            _ => Err(AccountScopeError::NoTrustedEvidence),
        },
        ClaudeAccount::Desktop => match (&credentials.source, profile) {
            (ClaudeCredentialSource::Desktop, Some(profile)) => profile
                .scopes
                .as_ref()
                .map(|(_, history)| history.clone())
                .ok_or(AccountScopeError::NoTrustedEvidence),
            _ => Err(AccountScopeError::NoTrustedEvidence),
        },
    }
}

/// SHA-256 of the exact bytes, lower-case hex. Unlike `sha256_hex` it never
/// trims: two directories differing only in surrounding whitespace must not
/// share a history series.
fn sha256_hex_exact(value: &[u8]) -> String {
    Sha256::digest(value)
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}

const CLAUDE_PROFILE_URL: &str = "https://api.anthropic.com/api/oauth/profile";
/// A subscription or account changes far more slowly than the 60s/300s polls.
const CLAUDE_PROFILE_TTL_SECS: i64 = 3600;
/// Negative-cache time for a 429 without Retry-After, a 5xx, a timeout or a
/// transport failure. A 4xx other than 429 is cached for the full TTL.
const CLAUDE_PROFILE_RETRY_SECS: i64 = 300;
/// Short next to the 30s usage timeout: a slow profile endpoint costs a
/// missing merge identity, never a delayed quota payload.
const CLAUDE_PROFILE_TIMEOUT_SECS: u64 = 5;

/// What one binding-keyed `/api/oauth/profile` answer proved. The raw UUIDs
/// never leave `claude_profile_identity_from`; only these HMAC scopes do.
#[derive(Debug, Clone)]
struct ClaudeProfileIdentity {
    /// `(merge scope, history scope)` of `profile:<account>\0<org>`, both from
    /// the authoritative resolver. `None` when either UUID is missing or
    /// malformed, or the resolver failed: the card is then never merged.
    scopes: Option<(AccountScope, HistoryScope)>,
    plan: Option<String>,
}

/// Only the fields TokenBar uses. No `Debug`, and no email or name field
/// exists to be logged.
#[derive(Deserialize)]
struct ClaudeProfileResponse {
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    account: Option<ClaudeProfileAccount>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    organization: Option<ClaudeProfileOrganization>,
}

#[derive(Deserialize)]
struct ClaudeProfileAccount {
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    uuid: Option<String>,
}

#[derive(Deserialize)]
struct ClaudeProfileOrganization {
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    uuid: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    organization_type: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    rate_limit_tier: Option<String>,
}

/// `(account_key_component, binding primary scope)`: a profile answer is
/// reused only for the same account AND the same credential binding, so a
/// credential swapped under one account key never inherits the old identity.
type ClaudeProfileSlot = (Option<String>, String);
/// `(valid_until, identity)`; `None` is a cached failure.
type ClaudeProfileCacheEntry = (DateTime<Utc>, Option<ClaudeProfileIdentity>);
type ClaudeProfileCache = HashMap<ClaudeProfileSlot, ClaudeProfileCacheEntry>;
static CLAUDE_PROFILE_CACHE: LazyLock<Mutex<ClaudeProfileCache>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));

/// The merge/history identity a profile answer proved for one credential
/// lineage, kept without a TTL: an account+org behind an unchanged binding
/// cannot change, so it stays valid while the token is expired or the fetch
/// fails (Claude Desktop closed is the common case). A changed binding
/// (re-login, rotation) is a different slot and stays unknown until a fresh
/// profile succeeds. In memory only.
///
/// ponytail: not persisted, so after an app restart the first poll with an
/// expired Desktop token cannot merge (two cards) until Desktop renews its
/// token and a profile lookup succeeds; persist it next to the account-scope
/// metadata if that window matters.
type ClaudeIdentityCache = HashMap<ClaudeProfileSlot, (AccountScope, HistoryScope)>;
static CLAUDE_IDENTITY_CACHE: LazyLock<Mutex<ClaudeIdentityCache>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));

async fn claude_profile_identity(
    client: &reqwest::Client,
    account: Option<&str>,
    access_token: &str,
    binding: &AccountScope,
) -> ClaudeProfileIdentity {
    claude_profile_identity_with(
        &CLAUDE_PROFILE_CACHE,
        &CLAUDE_IDENTITY_CACHE,
        claude_profile_slot(account, binding),
        Utc::now(),
        || async {
            let now = Utc::now();
            let answered = tokio::time::timeout(
                std::time::Duration::from_secs(CLAUDE_PROFILE_TIMEOUT_SECS),
                claude_profile_request(client, access_token),
            )
            .await;
            match answered {
                Ok(Ok(profile)) => (
                    Some(claude_profile_identity_from(
                        profile,
                        agent_account_scope::resolve_authoritative,
                        agent_account_scope::resolve_history_scope,
                    )),
                    CLAUDE_PROFILE_TTL_SECS,
                ),
                Ok(Err((status, retry_after))) => {
                    (None, claude_profile_retry_secs(status, retry_after, now))
                }
                Err(_) => (None, CLAUDE_PROFILE_RETRY_SECS),
            }
        },
    )
    .await
}

fn claude_profile_slot(account: Option<&str>, binding: &AccountScope) -> ClaudeProfileSlot {
    (
        account_key_component(account).map(str::to_string),
        binding.as_str().to_string(),
    )
}

/// The plan comes from the TTL'd answer cache; the identity comes from the
/// lineage-keyed `identity_cache`, which a fresh answer updates (set when it
/// names both UUIDs, cleared when it does not) and a failed lookup leaves
/// alone.
async fn claude_profile_identity_with<F, Fut>(
    plan_cache: &Mutex<ClaudeProfileCache>,
    identity_cache: &Mutex<ClaudeIdentityCache>,
    slot: ClaudeProfileSlot,
    now: DateTime<Utc>,
    fetch: F,
) -> ClaudeProfileIdentity
where
    F: FnOnce() -> Fut,
    Fut: Future<Output = (Option<ClaudeProfileIdentity>, i64)>,
{
    let remember_slot = slot.clone();
    let answered = claude_profile_cached_with(plan_cache, slot.clone(), now, || async move {
        let (answer, valid_secs) = fetch().await;
        if let Some(answer) = &answer {
            remember_claude_identity(identity_cache, remember_slot, answer.scopes.clone());
        }
        (answer, valid_secs)
    })
    .await;
    ClaudeProfileIdentity {
        scopes: remembered_claude_identity(identity_cache, &slot),
        plan: answered.and_then(|answer| answer.plan),
    }
}

fn remember_claude_identity(
    cache: &Mutex<ClaudeIdentityCache>,
    slot: ClaudeProfileSlot,
    scopes: Option<(AccountScope, HistoryScope)>,
) {
    let mut guard = cache.lock().unwrap_or_else(|e| e.into_inner());
    match scopes {
        Some(scopes) => {
            guard.insert(slot, scopes);
        }
        None => {
            guard.remove(&slot);
        }
    }
}

fn remembered_claude_identity(
    cache: &Mutex<ClaudeIdentityCache>,
    slot: &ClaudeProfileSlot,
) -> Option<(AccountScope, HistoryScope)> {
    cache
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .get(slot)
        .cloned()
}

/// Serve a live cache entry for exactly this slot, else run `fetch` and cache
/// what it returns (a failure included) for the seconds it names.
async fn claude_profile_cached_with<F, Fut>(
    cache: &Mutex<ClaudeProfileCache>,
    slot: ClaudeProfileSlot,
    now: DateTime<Utc>,
    fetch: F,
) -> Option<ClaudeProfileIdentity>
where
    F: FnOnce() -> Fut,
    Fut: Future<Output = (Option<ClaudeProfileIdentity>, i64)>,
{
    {
        let guard = cache.lock().unwrap_or_else(|e| e.into_inner());
        if let Some((valid_until, identity)) = guard.get(&slot) {
            if now < *valid_until {
                return identity.clone();
            }
        }
    }
    let (identity, valid_secs) = fetch().await;
    cache.lock().unwrap_or_else(|e| e.into_inner()).insert(
        slot,
        (
            now + chrono::Duration::seconds(valid_secs),
            identity.clone(),
        ),
    );
    identity
}

/// `Err((status, Retry-After))` for any non-success answer, with status 0 for
/// a transport or decode failure. Never carries a body.
async fn claude_profile_request(
    client: &reqwest::Client,
    access_token: &str,
) -> Result<ClaudeProfileResponse, (u16, Option<DateTime<Utc>>)> {
    let response = client
        .get(CLAUDE_PROFILE_URL)
        .bearer_auth(access_token)
        .header(reqwest::header::ACCEPT, "application/json")
        .header(reqwest::header::USER_AGENT, claude_user_agent())
        .header("anthropic-beta", "oauth-2025-04-20")
        .send()
        .await
        .map_err(|_| (0, None))?;
    let status = response.status().as_u16();
    if !(200..=299).contains(&status) {
        let retry_after = (status == 429)
            .then(|| parse_retry_after(response.headers().get(reqwest::header::RETRY_AFTER)))
            .flatten();
        return Err((status, retry_after));
    }
    response.json().await.map_err(|_| (0, None))
}

/// How long a failed profile lookup is cached: a 4xx (other than 429) for the
/// full TTL, a 429 until its Retry-After (at most the TTL), anything else for
/// `CLAUDE_PROFILE_RETRY_SECS`. Independent of the usage 429 gate.
fn claude_profile_retry_secs(
    status: u16,
    retry_after: Option<DateTime<Utc>>,
    now: DateTime<Utc>,
) -> i64 {
    match status {
        429 => retry_after
            .map(|until| (until - now).num_seconds())
            .unwrap_or(CLAUDE_PROFILE_RETRY_SECS)
            .clamp(1, CLAUDE_PROFILE_TTL_SECS),
        400..=499 => CLAUDE_PROFILE_TTL_SECS,
        _ => CLAUDE_PROFILE_RETRY_SECS,
    }
}

/// Reduce a profile answer to its HMAC scopes and plan. Both UUIDs are
/// required and format-checked; the tagged evidence `profile:<acct>\0<org>`
/// cannot collide with a `config-dir:` input.
fn claude_profile_identity_from<A, H>(
    profile: ClaudeProfileResponse,
    resolve_account: A,
    resolve_history: H,
) -> ClaudeProfileIdentity
where
    A: FnOnce(&str, AuthoritativeIdKind, &str) -> Result<AccountScope, AccountScopeError>,
    H: FnOnce(&str, Option<(AuthoritativeIdKind, &str)>) -> Result<HistoryScope, AccountScopeError>,
{
    let account = profile
        .account
        .and_then(|account| account.uuid)
        .and_then(claude_profile_uuid);
    let organization = profile.organization;
    let plan = organization.as_ref().and_then(claude_profile_plan);
    let org = organization
        .and_then(|organization| organization.uuid)
        .and_then(claude_profile_uuid);
    let scopes = account.zip(org).and_then(|(account, org)| {
        let evidence = format!("profile:{account}\0{org}");
        let merge = resolve_account("claude", AuthoritativeIdKind::OpaqueId, &evidence).ok()?;
        let history =
            resolve_history("claude", Some((AuthoritativeIdKind::OpaqueId, &evidence))).ok()?;
        Some((merge, history))
    });
    ClaudeProfileIdentity { scopes, plan }
}

/// A canonical 8-4-4-4-12 hex UUID, lower-cased; anything else is `None`.
fn claude_profile_uuid(value: String) -> Option<String> {
    let bytes = value.as_bytes();
    let valid = bytes.len() == 36
        && bytes.iter().enumerate().all(|(index, byte)| match index {
            8 | 13 | 18 | 23 => *byte == b'-',
            _ => byte.is_ascii_hexdigit(),
        });
    valid.then(|| value.to_ascii_lowercase())
}

/// `claude_max` + `default_claude_max_5x` -> `Max 5x`; `claude_pro` -> `Pro`.
/// The multiplier only exists on the rate-limit tier (ported from macOS).
fn claude_profile_plan(org: &ClaudeProfileOrganization) -> Option<String> {
    let kind = org
        .organization_type
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())?;
    let base = clean_plan(kind.strip_prefix("claude_").unwrap_or(kind));
    let multiplier = org
        .rate_limit_tier
        .as_deref()
        .and_then(|tier| tier.rsplit('_').next())
        .filter(|part| {
            part.len() > 1
                && part.ends_with('x')
                && part[..part.len() - 1].chars().all(|c| c.is_ascii_digit())
        });
    Some(match multiplier {
        Some(multiplier) => format!("{base} {multiplier}"),
        None => base,
    })
}

/// Codex is one of the two routes with an authoritative owner ID today. Its
/// history scope must consume the same `ChatGPT-Account-Id` the cache binding
/// corroborates on, so two accounts on one installation keep two series.
fn codex_history_scope(credentials: &CodexCredentials) -> Result<HistoryScope, AccountScopeError> {
    codex_history_scope_with(credentials, agent_account_scope::resolve_history_scope)
}

fn codex_history_scope_with<R>(
    credentials: &CodexCredentials,
    resolve: R,
) -> Result<HistoryScope, AccountScopeError>
where
    R: FnOnce(&str, Option<(AuthoritativeIdKind, &str)>) -> Result<HistoryScope, AccountScopeError>,
{
    resolve(
        "codex",
        credentials
            .account_id
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(|account_id| (AuthoritativeIdKind::OpaqueId, account_id)),
    )
}

fn load_codex_credentials() -> Result<CodexCredentials, String> {
    load_codex_credentials_from(&codex_home().join("auth.json"))
}

fn load_codex_credentials_from(auth_path: &Path) -> Result<CodexCredentials, String> {
    // Only an absent file means "not set up". `read_to_string` also fails for a
    // permission problem, a directory at this path, or invalid UTF-8, and every
    // one of those belongs to a configured account whose credential is broken:
    // mapping them to the marker would hand them `source: "unconfigured"`, which
    // takes the card out of tab navigation and tells the user to log in again
    // (macOS #345; ported from macOS).
    let raw = fs::read_to_string(auth_path).map_err(|error| {
        if error.kind() == std::io::ErrorKind::NotFound {
            CODEX_UNCONFIGURED_ERROR.to_string()
        } else {
            CODEX_CREDENTIALS_UNREADABLE_ERROR.to_string()
        }
    })?;
    let raw_json: Value =
        serde_json::from_str(&raw).map_err(|e| format!("decode Codex auth.json: {}", e))?;

    if raw_json
        .get("OPENAI_API_KEY")
        .and_then(Value::as_str)
        .is_some_and(|key| !key.trim().is_empty())
    {
        return Err(
            "Codex is using API-key auth; OAuth usage limits require `codex login`.".to_string(),
        );
    }

    let tokens = raw_json
        .get("tokens")
        .and_then(Value::as_object)
        .ok_or_else(|| "Codex auth.json exists but contains no OAuth tokens.".to_string())?;
    let access_token = string_key(tokens, "access_token", "accessToken")
        .ok_or_else(|| "Codex auth.json has no access token.".to_string())?;
    let refresh_token = string_key(tokens, "refresh_token", "refreshToken");
    let id_token = string_key(tokens, "id_token", "idToken");
    let account_id = string_key(tokens, "account_id", "accountId");
    let last_refresh = raw_json
        .get("last_refresh")
        .and_then(Value::as_str)
        .and_then(parse_datetime);

    Ok(CodexCredentials {
        access_token,
        refresh_token,
        id_token,
        account_id,
        last_refresh,
        auth_path: auth_path.to_path_buf(),
        raw_json,
        scope_slot: CredentialSlot {
            semantic_source: "codex-auth-json",
            canonical_location: agent_account_scope::canonical_file_location(
                auth_path,
                Some("tokens"),
            )
            .map_err(|_| "Codex auth location cannot be scoped safely.".to_string())?,
        },
    })
}

/// Marker error for "no Claude credential is configured at all" (as opposed to a
/// credential that exists but failed). `fetch_claude` turns this into a snapshot
/// with `source == "unconfigured"`, so the UI shows a setup prompt rather than a
/// red error.
const CLAUDE_UNCONFIGURED_ERROR: &str = "Claude OAuth credentials not found. Run `claude` to authenticate, or set CLAUDE_CODE_OAUTH_TOKEN / add a `tokenbar-claude-oauth-token` Keychain item to use a setup-token.";
const CLAUDE_CREDENTIALS_LOAD_ERROR: &str = "Claude credentials could not be loaded.";

/// Full-login credentials: structured `claudeAiOauth` blobs (Keychain
/// `Claude Code-credentials`, then `~/.claude/.credentials.json`) plus the
/// TokenBar env override. Only a genuinely missing higher-priority store falls
/// through; the explicit #26 logout shape stops full-login precedence.
fn load_claude_login_credentials() -> ClaudeLoginResolution {
    match load_claude_credentials_from_environment() {
        Ok(Some(credentials)) => return ClaudeLoginResolution::Ready(credentials),
        Ok(None) => {}
        Err(_) => return ClaudeLoginResolution::Terminal,
    }
    load_stored_claude_login_with(
        load_claude_credentials_from_keychain,
        || match fs::read_to_string(claude_credentials_path()) {
            Ok(raw) => Ok(Some(raw)),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(_) => Err("Claude credentials file could not be read.".to_string()),
        },
    )
}

fn load_stored_claude_login_with<LoadKeychain, LoadFile>(
    load_keychain: LoadKeychain,
    load_file: LoadFile,
) -> ClaudeLoginResolution
where
    LoadKeychain: FnOnce() -> Result<Option<String>, String>,
    LoadFile: FnOnce() -> Result<Option<String>, String>,
{
    match load_keychain() {
        Ok(Some(raw)) => {
            return resolve_stored_claude_login(&raw, ClaudeCredentialSource::Keychain);
        }
        Ok(None) => {}
        Err(_) => return ClaudeLoginResolution::Terminal,
    }
    match load_file() {
        Ok(Some(raw)) => resolve_stored_claude_login(&raw, ClaudeCredentialSource::File),
        Ok(None) => ClaudeLoginResolution::Absent,
        Err(_) => ClaudeLoginResolution::Terminal,
    }
}

fn resolve_stored_claude_login(raw: &str, source: ClaudeCredentialSource) -> ClaudeLoginResolution {
    let raw_root: Value = match serde_json::from_str(raw) {
        Ok(root) => root,
        Err(_) => return ClaudeLoginResolution::Terminal,
    };
    let explicitly_logged_out = raw_root
        .get("claudeAiOauth")
        .and_then(Value::as_object)
        .is_some_and(|oauth| {
            oauth.contains_key("refreshToken")
                && match oauth.get("accessToken") {
                    None | Some(Value::Null) => true,
                    Some(Value::String(token)) => token.trim().is_empty(),
                    _ => false,
                }
        });
    if explicitly_logged_out {
        return ClaudeLoginResolution::ExplicitLogout;
    }
    match parse_claude_credentials_data(raw, source) {
        Ok(credentials) => ClaudeLoginResolution::Ready(credentials),
        Err(_) => ClaudeLoginResolution::Terminal,
    }
}

/// `CLAUDE_CODE_OAUTH_TOKEN` as Claude Code itself resolves it: this process's
/// own environment (covers `launchctl setenv` / terminal launch), then a
/// login-shell harvest of the user's `~/.zshrc` (so a plain export a
/// Finder-launched GUI app never inherits is still found). Per Claude Code's
/// auth precedence this outranks a stored subscription `/login`.
async fn resolve_claude_code_oauth_token() -> Option<ResolvedClaudeToken> {
    if let Some(access_token) = claude_direct_env_token() {
        return Some(ResolvedClaudeToken {
            access_token,
            scope_slot: CredentialSlot {
                semantic_source: "claude-code-environment",
                canonical_location: "CLAUDE_CODE_OAUTH_TOKEN".to_string(),
            },
        });
    }
    harvest_shell_env_token()
        .await
        .map(|access_token| ResolvedClaudeToken {
            access_token,
            scope_slot: CredentialSlot {
                semantic_source: "claude-code-login-shell",
                canonical_location: "CLAUDE_CODE_OAUTH_TOKEN".to_string(),
            },
        })
}

/// The `tokenbar-claude-oauth-token` Keychain item (a TokenBar-specific setup
/// token). A last-resort fallback, below the stored `/login`.
fn resolve_claude_keychain_token() -> Result<Option<ResolvedClaudeToken>, String> {
    load_claude_raw_token_from_keychain().map(|token| {
        token.map(|access_token| ResolvedClaudeToken {
            access_token,
            scope_slot: CredentialSlot {
                semantic_source: "claude-setup-keychain",
                canonical_location: CLAUDE_RAW_TOKEN_KEYCHAIN_SERVICE.to_string(),
            },
        })
    })
}

fn load_claude_credentials_from_environment() -> Result<Option<ClaudeCredentials>, String> {
    let token = [
        "TOKENBAR_CLAUDE_OAUTH_TOKEN",
        "TOKCAT_CLAUDE_OAUTH_TOKEN",
        "CODEXBAR_CLAUDE_OAUTH_TOKEN",
    ]
    .into_iter()
    .find_map(|name| {
        std::env::var(name)
            .ok()
            .map(|value| value.trim().to_string())
            .filter(|value| !value.is_empty())
            .map(|value| (name, value))
    });
    let Some((source_name, access_token)) = token else {
        return Ok(None);
    };
    let scopes = std::env::var("TOKENBAR_CLAUDE_OAUTH_SCOPES")
        .or_else(|_| std::env::var("TOKCAT_CLAUDE_OAUTH_SCOPES"))
        .or_else(|_| std::env::var("CODEXBAR_CLAUDE_OAUTH_SCOPES"))
        .unwrap_or_default()
        .split([',', ' '])
        .map(str::trim)
        .filter(|scope| !scope.is_empty())
        .map(str::to_string)
        .collect();
    Ok(Some(ClaudeCredentials {
        access_token,
        refresh_token: None,
        expires_at: None,
        scopes,
        rate_limit_tier: None,
        subscription_type: None,
        source: ClaudeCredentialSource::Environment,
        raw_root: None,
        keychain_account: None,
        scope_slot: CredentialSlot {
            semantic_source: "claude-environment",
            canonical_location: source_name.to_string(),
        },
    }))
}

fn parse_claude_credentials_data(
    raw: &str,
    source: ClaudeCredentialSource,
) -> Result<ClaudeCredentials, String> {
    let raw_root: Value =
        serde_json::from_str(raw).map_err(|e| format!("decode Claude OAuth credentials: {}", e))?;
    let root: ClaudeCredentialsRoot =
        serde_json::from_str(raw).map_err(|e| format!("decode Claude OAuth credentials: {}", e))?;
    let oauth = root
        .claude_ai_oauth
        .ok_or_else(|| "Claude OAuth credentials are missing claudeAiOauth.".to_string())?;
    let access_token = oauth
        .access_token
        .map(|token| token.trim().to_string())
        .filter(|token| !token.is_empty())
        .ok_or_else(|| "Claude OAuth credentials have no access token.".to_string())?;
    let expires_at = oauth
        .expires_at
        .and_then(|millis| Utc.timestamp_millis_opt(millis as i64).single());
    Ok(ClaudeCredentials {
        access_token,
        refresh_token: oauth
            .refresh_token
            .map(|token| token.trim().to_string())
            .filter(|token| !token.is_empty()),
        expires_at,
        scopes: oauth.scopes.unwrap_or_default(),
        rate_limit_tier: oauth.rate_limit_tier,
        subscription_type: oauth.subscription_type,
        scope_slot: claude_login_scope_slot(&source)?,
        source,
        raw_root: Some(raw_root),
        keychain_account: None,
    })
}

fn claude_login_scope_slot(source: &ClaudeCredentialSource) -> Result<CredentialSlot, String> {
    match source {
        ClaudeCredentialSource::Keychain => Ok(CredentialSlot {
            semantic_source: "claude-login-keychain",
            canonical_location: CLAUDE_KEYCHAIN_SERVICE.to_string(),
        }),
        ClaudeCredentialSource::File => Ok(CredentialSlot {
            semantic_source: "claude-login-file",
            canonical_location: agent_account_scope::canonical_file_location(
                &claude_credentials_path(),
                Some("claudeAiOauth"),
            )
            .map_err(|_| "Claude credential location cannot be scoped safely.".to_string())?,
        }),
        ClaudeCredentialSource::ConfigDir(dir) => Ok(CredentialSlot {
            semantic_source: CLAUDE_CONFIG_DIR_FILE_SOURCE,
            canonical_location: agent_account_scope::canonical_file_location(
                &dir.join(CLAUDE_CONFIG_DIR_CREDENTIALS_FILE),
                Some("claudeAiOauth"),
            )
            .map_err(|_| CLAUDE_CONFIG_DIR_READ_ERROR.to_string())?,
        }),
        ClaudeCredentialSource::Environment | ClaudeCredentialSource::Desktop => {
            Err("this credential source requires an explicit account-scope slot".to_string())
        }
    }
}

/// `config.json` keys holding Claude Desktop's safeStorage-encrypted token
/// cache, in the order they are tried. Desktop app-2.16120.0 (observed
/// 2026-10-01) writes its login to `oauth:tokenCacheV2` and leaves
/// `oauth:tokenCache` holding an encrypted `{}`; older builds only have
/// `oauth:tokenCache`.
const CLAUDE_DESKTOP_TOKEN_CACHE_KEY_V2: &str = "oauth:tokenCacheV2";
const CLAUDE_DESKTOP_TOKEN_CACHE_KEY: &str = "oauth:tokenCache";
const CLAUDE_DESKTOP_TOKEN_CACHE_KEYS: [&str; 2] = [
    CLAUDE_DESKTOP_TOKEN_CACHE_KEY_V2,
    CLAUDE_DESKTOP_TOKEN_CACHE_KEY,
];
const CLAUDE_DESKTOP_READ_ERROR: &str = "Claude Desktop login could not be read.";
const CLAUDE_DESKTOP_READ_RETRY_ERROR: &str =
    "Claude Desktop login could not be read. Retrying automatically.";
const CLAUDE_DESKTOP_EXPIRED_ERROR: &str =
    "Claude Desktop login has expired. Open Claude Desktop to renew it.";
const CLAUDE_DESKTOP_REFRESH_ERROR: &str = "Claude Desktop credentials cannot be refreshed.";
const CLAUDE_READ_ONLY_EXPIRY_SKEW_SECS: i64 = 60;
/// Nesting levels below the decrypted root searched for a token object.
const CLAUDE_DESKTOP_MAX_DEPTH: usize = 3;
const CLAUDE_DESKTOP_MAX_REPORTED_KEYS: usize = 10;

/// The Claude Desktop login (Windows only; its own card). `Ok(None)` means no
/// Desktop login is stored; `Err` carries a failure with a fixed display
/// string. Only this function resolves the real `%APPDATA%\Claude` paths.
#[cfg(target_os = "windows")]
fn load_claude_desktop_login() -> Result<Option<ClaudeCredentials>, ProviderFetchFailure> {
    let Some(root) = dirs::config_dir() else {
        return Ok(None);
    };
    let root = root.join("Claude");
    load_claude_desktop_credentials_from(&root.join("config.json"), &root.join("Local State"))
}

#[cfg(not(target_os = "windows"))]
fn load_claude_desktop_login() -> Result<Option<ClaudeCredentials>, ProviderFetchFailure> {
    Ok(None)
}

#[cfg(target_os = "windows")]
fn load_claude_desktop_credentials_from(
    config_path: &Path,
    local_state_path: &Path,
) -> Result<Option<ClaudeCredentials>, ProviderFetchFailure> {
    use crate::win_safe_storage;

    let caches = read_claude_desktop_token_cache(config_path)?;
    if caches.is_empty() {
        return Ok(None);
    }
    let local_state = match fs::read_to_string(local_state_path) {
        Ok(raw) => raw,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return Err(ProviderFetchFailure::terminal(CLAUDE_DESKTOP_READ_ERROR));
        }
        Err(_) => return Err(claude_read_retry_failure(CLAUDE_DESKTOP_READ_RETRY_ERROR)),
    };
    let local_state: Value = serde_json::from_str(&local_state)
        .map_err(|_| claude_read_retry_failure(CLAUDE_DESKTOP_READ_RETRY_ERROR))?;
    let terminal = |_| ProviderFetchFailure::terminal(CLAUDE_DESKTOP_READ_ERROR);
    let key = win_safe_storage::load_key(&local_state).map_err(terminal)?;
    let selected = select_claude_desktop_token_cache(caches, |cache_key, value| {
        let mut plaintext = win_safe_storage::decrypt(&key, value).map_err(terminal)?;
        let parsed = parse_claude_desktop_token_cache(&plaintext, config_path, cache_key);
        win_safe_storage::wipe(&mut plaintext);
        Ok(parsed)
    });
    drop(key);
    selected
}

/// Try each present token cache in preference order and return the first one
/// that holds a token. A cache whose plaintext is an empty JSON object or
/// array holds no login — Claude Desktop 2.16120.0 leaves `oauth:tokenCache`
/// as an encrypted `{}` while signed in, and writes `{}` to both keys when the
/// user signs out — so it is skipped, and when every present cache is empty
/// the result is `Ok(None)`: no Desktop card, exactly as with the keys
/// missing. A non-empty cache with no token candidate (or non-JSON) also falls
/// through, but is remembered: with no token anywhere, the first such error is
/// reported. A decrypt failure stops.
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
fn select_claude_desktop_token_cache<T>(
    caches: Vec<(&'static str, String)>,
    mut try_cache: T,
) -> Result<Option<ClaudeCredentials>, ProviderFetchFailure>
where
    T: FnMut(
        &'static str,
        &str,
    ) -> Result<Result<Option<ClaudeCredentials>, String>, ProviderFetchFailure>,
{
    let mut first_error = None;
    for (cache_key, value) in caches {
        match try_cache(cache_key, &value)? {
            Ok(Some(credentials)) => return Ok(Some(credentials)),
            Ok(None) => {}
            Err(display) => {
                first_error.get_or_insert(display);
            }
        }
    }
    match first_error {
        Some(display) => Err(ProviderFetchFailure::terminal(display)),
        None => Ok(None),
    }
}

/// A read or JSON-parse failure of a credential file another program
/// rewrites in place (Claude Desktop's files, a config directory's
/// `.credentials.json`) is treated as a race and retried rather than reported
/// as a broken login. No account binding is provable without the token, so
/// the failure carries none.
fn claude_read_retry_failure(display: &'static str) -> ProviderFetchFailure {
    ProviderFetchFailure::transient(
        display,
        None,
        SafeTransportDiagnostic::from_facts(TransportErrorFacts {
            is_timeout: false,
            is_connect: false,
            is_dns: false,
            is_tls: false,
            phase: TransportPhase::Request,
            raw_os_code: None,
        }),
    )
}

const CLAUDE_CONFIG_DIR_CREDENTIALS_FILE: &str = ".credentials.json";
/// `semantic_source` of a credential read from a configured directory's own
/// `.credentials.json`; distinct from the primary's `claude-login-file` so
/// the two never share a lineage slot, and the one value that lets a config
/// directory card record durable history.
const CLAUDE_CONFIG_DIR_FILE_SOURCE: &str = "claude-config-dir-file";
/// Claude Code's own login file is a few hundred bytes; anything larger is
/// not one and is refused before parsing.
const CLAUDE_CONFIG_DIR_CREDENTIALS_MAX_BYTES: u64 = 64 * 1024;
/// No usable login in the directory. Reported as a normal (`oauth`) card, not
/// the setup prompt: there is no setup-token fallback for an extra account.
const CLAUDE_CONFIG_DIR_UNCONFIGURED_ERROR: &str =
    "No Claude Code login found for this config directory. Run `claude` with CLAUDE_CONFIG_DIR set to that directory to sign in.";
const CLAUDE_CONFIG_DIR_READ_ERROR: &str =
    "Claude Code login for this config directory could not be read.";
const CLAUDE_CONFIG_DIR_READ_RETRY_ERROR: &str =
    "Claude Code login for this config directory could not be read. Retrying automatically.";
const CLAUDE_CONFIG_DIR_REJECTED_ERROR: &str =
    "Claude Code login for this config directory was rejected. Run `claude` with CLAUDE_CONFIG_DIR set to that directory to sign in again.";
const CLAUDE_CONFIG_DIR_EXPIRED_ERROR: &str =
    "Claude Code login for this config directory has expired. Run `claude` with CLAUDE_CONFIG_DIR set to that directory to renew it.";
const CLAUDE_READ_ONLY_REFRESH_ERROR: &str = "This Claude login cannot be refreshed by TokenBar.";

/// Load a configured directory's login from `<dir>\.credentials.json` and
/// from nothing else (plan assumption U1: Claude Code on Windows keeps its
/// login there for `CLAUDE_CONFIG_DIR`). The primary's chain — environment
/// tokens, shell harvest, keychain, `~/.claude` — belongs to the primary; a
/// missing file here is this account's error, never a fall-through.
fn load_claude_config_dir_credentials(
    dir: &str,
) -> Result<ClaudeCredentials, ProviderFetchFailure> {
    let dir = PathBuf::from(dir);
    let raw = read_claude_config_dir_credentials(&dir.join(CLAUDE_CONFIG_DIR_CREDENTIALS_FILE))?;
    if serde_json::from_str::<Value>(&raw).is_err() {
        // A torn read of a file Claude Code is rewriting.
        return Err(claude_read_retry_failure(
            CLAUDE_CONFIG_DIR_READ_RETRY_ERROR,
        ));
    }
    match resolve_stored_claude_login(&raw, ClaudeCredentialSource::ConfigDir(dir)) {
        ClaudeLoginResolution::Ready(credentials) => Ok(credentials),
        ClaudeLoginResolution::Absent | ClaudeLoginResolution::ExplicitLogout => Err(
            ProviderFetchFailure::terminal(CLAUDE_CONFIG_DIR_UNCONFIGURED_ERROR),
        ),
        ClaudeLoginResolution::Terminal => {
            Err(ProviderFetchFailure::terminal(CLAUDE_CONFIG_DIR_READ_ERROR))
        }
    }
}

/// How long one config directory's credential read may take before the card
/// reports a transient failure. A directory on a stalled network drive would
/// otherwise hold the whole provider poll, which runs every provider on one
/// joined task (security review R5).
const CLAUDE_CONFIG_DIR_READ_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(10);

/// Config directories (folded) whose credential read is still running on the
/// blocking pool, including reads whose caller already timed out.
static CLAUDE_CONFIG_DIR_READS_IN_FLIGHT: LazyLock<Mutex<HashSet<String>>> =
    LazyLock::new(|| Mutex::new(HashSet::new()));

/// Removes its directory from the in-flight set when the blocking read ends
/// (returns, panics, or is never run).
struct ConfigDirReadInFlight(String);

impl Drop for ConfigDirReadInFlight {
    fn drop(&mut self) {
        CLAUDE_CONFIG_DIR_READS_IN_FLIGHT
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .remove(&self.0);
    }
}

/// Run the synchronous credential read on the blocking pool with a timeout.
/// A timed-out read is reported as the existing transient "Retrying
/// automatically" failure; the blocking thread finishes on its own and its
/// result is discarded. `timeout` stops the wait, not the read, so while an
/// earlier read of the same directory is still running no new one starts:
/// the poll gets the same retry failure, and a stalled drive holds at most
/// one blocking thread per directory.
async fn load_claude_config_dir_credentials_bounded<L>(
    dir: String,
    timeout: std::time::Duration,
    load: L,
) -> Result<ClaudeCredentials, ProviderFetchFailure>
where
    L: FnOnce(&str) -> Result<ClaudeCredentials, ProviderFetchFailure> + Send + 'static,
{
    let key = crate::claude_config_dirs::duplicate_key(&dir);
    if !CLAUDE_CONFIG_DIR_READS_IN_FLIGHT
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .insert(key.clone())
    {
        return Err(claude_read_retry_failure(CLAUDE_CONFIG_DIR_READ_RETRY_ERROR));
    }
    let in_flight = ConfigDirReadInFlight(key);
    let read = move || {
        let _in_flight = in_flight;
        load(&dir)
    };
    match tokio::time::timeout(timeout, tokio::task::spawn_blocking(read)).await {
        Ok(Ok(loaded)) => loaded,
        Ok(Err(_)) | Err(_) => Err(claude_read_retry_failure(CLAUDE_CONFIG_DIR_READ_RETRY_ERROR)),
    }
}

fn read_claude_config_dir_credentials(path: &Path) -> Result<String, ProviderFetchFailure> {
    use std::io::Read as _;
    let file = match fs::File::open(path) {
        Ok(file) => file,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return Err(ProviderFetchFailure::terminal(
                CLAUDE_CONFIG_DIR_UNCONFIGURED_ERROR,
            ));
        }
        Err(_) => {
            return Err(claude_read_retry_failure(
                CLAUDE_CONFIG_DIR_READ_RETRY_ERROR,
            ))
        }
    };
    let mut raw = String::new();
    file.take(CLAUDE_CONFIG_DIR_CREDENTIALS_MAX_BYTES + 1)
        .read_to_string(&mut raw)
        .map_err(|_| claude_read_retry_failure(CLAUDE_CONFIG_DIR_READ_RETRY_ERROR))?;
    if raw.len() as u64 > CLAUDE_CONFIG_DIR_CREDENTIALS_MAX_BYTES {
        return Err(ProviderFetchFailure::terminal(CLAUDE_CONFIG_DIR_READ_ERROR));
    }
    Ok(raw)
}

/// The encrypted token-cache values in Claude Desktop's config.json, as
/// `(key, value)` in `CLAUDE_DESKTOP_TOKEN_CACHE_KEYS` order. A missing file,
/// or every key missing, null or empty, means Desktop is not logged in (empty
/// list). A key holding a non-string is skipped; if nothing usable remains
/// and such a key exists, the login is reported unreadable.
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
fn read_claude_desktop_token_cache(
    config_path: &Path,
) -> Result<Vec<(&'static str, String)>, ProviderFetchFailure> {
    let raw = match fs::read_to_string(config_path) {
        Ok(raw) => raw,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(_) => return Err(claude_read_retry_failure(CLAUDE_DESKTOP_READ_RETRY_ERROR)),
    };
    let root: Value = serde_json::from_str(&raw)
        .map_err(|_| claude_read_retry_failure(CLAUDE_DESKTOP_READ_RETRY_ERROR))?;
    let mut caches = Vec::new();
    let mut malformed = false;
    for cache_key in CLAUDE_DESKTOP_TOKEN_CACHE_KEYS {
        match root.get(cache_key) {
            None | Some(Value::Null) => {}
            Some(Value::String(value)) if value.trim().is_empty() => {}
            Some(Value::String(value)) => caches.push((cache_key, value.trim().to_string())),
            Some(_) => malformed = true,
        }
    }
    if caches.is_empty() && malformed {
        return Err(ProviderFetchFailure::terminal(CLAUDE_DESKTOP_READ_ERROR));
    }
    Ok(caches)
}

struct ClaudeDesktopCandidate {
    access_token: String,
    refresh_token: Option<String>,
    expires_at: Option<DateTime<Utc>>,
    scopes: Vec<String>,
    /// Carries a refresh-token or expiry key next to the token.
    has_token_metadata: bool,
}

impl ClaudeDesktopCandidate {
    fn rank(&self) -> (bool, bool, Option<DateTime<Utc>>) {
        (
            self.scopes.iter().any(|scope| scope == "user:profile"),
            self.has_token_metadata,
            self.expires_at,
        )
    }
}

/// Tolerant parse of the decrypted token cache, whose shape Claude Desktop
/// does not document. Picks the most plausible token object: one granting
/// `user:profile` (the usage endpoint needs it), then one with token metadata
/// over a bare token, then the latest expiry (none ranks lowest). `Ok(None)`
/// for an empty object or array: the cache holds no login.
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
fn parse_claude_desktop_token_cache(
    plaintext: &[u8],
    config_path: &Path,
    cache_key: &'static str,
) -> Result<Option<ClaudeCredentials>, String> {
    let root: Value =
        serde_json::from_slice(plaintext).map_err(|_| CLAUDE_DESKTOP_READ_ERROR.to_string())?;
    let empty = match &root {
        Value::Object(object) => object.is_empty(),
        Value::Array(items) => items.is_empty(),
        _ => false,
    };
    if empty {
        return Ok(None);
    }
    let mut candidates = Vec::new();
    collect_claude_desktop_candidates(&root, 0, &mut candidates);
    let Some(best) = candidates
        .into_iter()
        .max_by_key(ClaudeDesktopCandidate::rank)
    else {
        return Err(claude_desktop_unrecognized_error(cache_key, &root));
    };
    Ok(Some(ClaudeCredentials {
        access_token: best.access_token,
        refresh_token: best.refresh_token,
        expires_at: best.expires_at,
        scopes: best.scopes,
        rate_limit_tier: None,
        subscription_type: None,
        source: ClaudeCredentialSource::Desktop,
        raw_root: None,
        keychain_account: None,
        scope_slot: CredentialSlot {
            semantic_source: "claude-desktop-safestorage",
            canonical_location: agent_account_scope::canonical_file_location(
                config_path,
                Some(cache_key),
            )
            .map_err(|_| CLAUDE_DESKTOP_READ_ERROR.to_string())?,
        },
    }))
}

fn collect_claude_desktop_candidates(
    value: &Value,
    depth: usize,
    candidates: &mut Vec<ClaudeDesktopCandidate>,
) {
    let children: Box<dyn Iterator<Item = &Value>> = match value {
        Value::Object(object) => {
            candidates.extend(claude_desktop_candidate(object));
            Box::new(object.values())
        }
        Value::Array(items) => Box::new(items.iter()),
        _ => return,
    };
    if depth < CLAUDE_DESKTOP_MAX_DEPTH {
        for child in children {
            collect_claude_desktop_candidates(child, depth + 1, candidates);
        }
    }
}

fn claude_desktop_candidate(
    object: &serde_json::Map<String, Value>,
) -> Option<ClaudeDesktopCandidate> {
    let non_empty = |keys: &[&str]| {
        keys.iter().find_map(|key| {
            object
                .get(*key)
                .and_then(Value::as_str)
                .map(str::trim)
                .filter(|value| !value.is_empty())
                .map(str::to_string)
        })
    };
    let refresh_token = non_empty(&["refreshToken", "refresh_token"]);
    let expiry = ["expiresAt", "expires_at", "expiry"]
        .iter()
        .find_map(|key| object.get(*key));
    let has_token_metadata = refresh_token.is_some() || expiry.is_some();
    // A bare `token` key is too generic to trust on its own.
    let access_token = non_empty(&["accessToken", "access_token"])
        .or_else(|| has_token_metadata.then(|| non_empty(&["token"])).flatten())?;
    let scopes = match object.get("scopes").or_else(|| object.get("scope")) {
        Some(Value::Array(items)) => items
            .iter()
            .filter_map(Value::as_str)
            .map(str::to_string)
            .collect(),
        Some(Value::String(scopes)) => scopes.split_whitespace().map(str::to_string).collect(),
        _ => Vec::new(),
    };
    Some(ClaudeDesktopCandidate {
        access_token,
        refresh_token,
        expires_at: expiry.and_then(parse_claude_desktop_expiry),
        scopes,
        has_token_metadata,
    })
}

/// Epoch milliseconds when above 1e12, else epoch seconds; or RFC 3339.
fn parse_claude_desktop_expiry(value: &Value) -> Option<DateTime<Utc>> {
    match value {
        Value::Number(number) => {
            let raw = number.as_f64()?;
            let millis = if raw > 1e12 { raw } else { raw * 1000.0 };
            Utc.timestamp_millis_opt(millis as i64).single()
        }
        Value::String(text) => parse_datetime(text.trim()),
        _ => None,
    }
}

/// Names only object keys that look like identifiers (letters and `_`, at
/// most 40), so neither a token nor an account/UUID key can reach the message.
/// `cache_key` is one of `CLAUDE_DESKTOP_TOKEN_CACHE_KEYS`, a literal.
fn claude_desktop_unrecognized_error(cache_key: &'static str, root: &Value) -> String {
    fn collect<'a>(value: &'a Value, depth: usize, keys: &mut std::collections::BTreeSet<&'a str>) {
        let children: Box<dyn Iterator<Item = &Value>> = match value {
            Value::Object(object) => {
                keys.extend(object.keys().map(String::as_str).filter(|key| {
                    (1..=40).contains(&key.len())
                        && key
                            .bytes()
                            .all(|byte| byte.is_ascii_alphabetic() || byte == b'_')
                }));
                Box::new(object.values())
            }
            Value::Array(items) => Box::new(items.iter()),
            _ => return,
        };
        if depth < CLAUDE_DESKTOP_MAX_DEPTH {
            for child in children {
                collect(child, depth + 1, keys);
            }
        }
    }
    let mut keys = std::collections::BTreeSet::new();
    collect(root, 0, &mut keys);
    let listed: Vec<&str> = keys
        .into_iter()
        .take(CLAUDE_DESKTOP_MAX_REPORTED_KEYS)
        .collect();
    let listed = if listed.is_empty() {
        "none".to_string()
    } else {
        listed.join(", ")
    };
    format!("Claude Desktop login format is not recognized (cache: {cache_key}, keys: {listed}).")
}

#[cfg(target_os = "macos")]
fn keychain_item_not_found(status: &std::process::ExitStatus) -> bool {
    status.code() == Some(44)
}

fn load_claude_credentials_from_keychain() -> Result<Option<String>, String> {
    load_claude_credentials_from_keychain_item(None)
}

#[cfg(target_os = "macos")]
fn load_claude_credentials_from_keychain_item(
    account: Option<&str>,
) -> Result<Option<String>, String> {
    let mut command = std::process::Command::new("/usr/bin/security");
    command.args(["find-generic-password", "-s", CLAUDE_KEYCHAIN_SERVICE]);
    if let Some(account) = account {
        command.args(["-a", account]);
    }
    let output = command
        .arg("-w")
        .output()
        .map_err(|e| format!("read Claude Keychain credentials: {}", e))?;
    if !output.status.success() {
        return if keychain_item_not_found(&output.status) {
            Ok(None)
        } else {
            Err("Claude Keychain credentials could not be read.".to_string())
        };
    }
    let raw = String::from_utf8(output.stdout)
        .map_err(|_| "Claude Keychain credentials are not UTF-8 JSON.".to_string())?;
    let raw = raw.trim_matches(['\r', '\n']).to_string();
    if raw.trim().is_empty() {
        return Err("Claude Keychain credentials are empty.".to_string());
    }
    Ok(Some(raw))
}

#[cfg(not(target_os = "macos"))]
fn load_claude_credentials_from_keychain_item(
    _account: Option<&str>,
) -> Result<Option<String>, String> {
    Ok(None)
}

/// Build credentials from a bare access token (no refresh/expiry/scope metadata).
/// Setup-token delivery paths use these credentials only for the header probe.
fn claude_credentials_from_access_token(token: ResolvedClaudeToken) -> ClaudeCredentials {
    ClaudeCredentials {
        access_token: token.access_token,
        refresh_token: None,
        expires_at: None,
        scopes: Vec::new(),
        rate_limit_tier: None,
        subscription_type: None,
        // A bare setup-token has no refresh token and no backing store to write
        // to, so treat it as read-only — save_claude_credentials skips it.
        source: ClaudeCredentialSource::Environment,
        raw_root: None,
        keychain_account: None,
        scope_slot: token.scope_slot,
    }
}

/// C — `CLAUDE_CODE_OAUTH_TOKEN` from this process's own environment (covers
/// `launchctl setenv` and terminal-launched runs).
fn claude_direct_env_token() -> Option<String> {
    claude_token_from_lookup(|key| std::env::var(key).ok())
}

fn claude_token_from_lookup(lookup: impl Fn(&str) -> Option<String>) -> Option<String> {
    lookup("CLAUDE_CODE_OAUTH_TOKEN")
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
}

/// Cache for the shell-harvested token — harvesting spawns a full interactive
/// login shell, so we do it at most once per TTL rather than per poll.
static CLAUDE_HARVEST_CACHE: Mutex<Option<(DateTime<Utc>, Option<String>)>> = Mutex::new(None);
// A found token rarely changes → cache it for an hour. Because the harvest now
// runs for every user (to mirror Claude Code's CLAUDE_CODE_OAUTH_TOKEN-before-
// /login precedence), a miss is also cached for a while so we don't re-spawn a
// login shell on every poll; a freshly-added `~/.zshrc` export is picked up
// within this window, or immediately on app restart (which clears the cache).
const CLAUDE_HARVEST_TTL_SECS: i64 = 3600;
const CLAUDE_HARVEST_NEGATIVE_TTL_SECS: i64 = 1800;

/// D — harvest `CLAUDE_CODE_OAUTH_TOKEN` from the user's login shell, so a plain
/// `~/.zshrc` export is picked up even though a Finder/login-item GUI app does
/// not inherit shell environments. Cached; returns None on timeout/miss so the
/// keychain fallback can still fire.
async fn harvest_shell_env_token() -> Option<String> {
    // Scope the guard so it is dropped before the `.await` below (never hold a
    // std Mutex across an await). Recover a poisoned lock (like `with_gate`) so a
    // stray panic can't permanently disable the cache and reintroduce a per-poll
    // shell spawn.
    {
        let guard = CLAUDE_HARVEST_CACHE
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if let Some((fetched_at, token)) = guard.as_ref() {
            let ttl = if token.is_some() {
                CLAUDE_HARVEST_TTL_SECS
            } else {
                CLAUDE_HARVEST_NEGATIVE_TTL_SECS
            };
            if (Utc::now() - *fetched_at).num_seconds() < ttl {
                return token.clone();
            }
        }
    }
    let token = harvest_shell_env_token_uncached().await;
    {
        let mut guard = CLAUDE_HARVEST_CACHE
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        *guard = Some((Utc::now(), token.clone()));
    }
    token
}

#[cfg(target_os = "macos")]
async fn harvest_shell_env_token_uncached() -> Option<String> {
    // Interactive (-i) so ~/.zshrc is sourced (login -l alone runs ~/.zprofile
    // only). Null-delimited markers isolate the value from any rc stdout chatter;
    // rc noise (p10k/gitstatus warnings) goes to stderr, which we discard.
    let shell = detect_login_shell();
    let script = "printf '\\0__TB_OAT_S__\\0%s\\0__TB_OAT_E__\\0' \"$CLAUDE_CODE_OAUTH_TOKEN\"";
    let future = tokio::process::Command::new(&shell)
        .args(["-l", "-i", "-c", script])
        .stdin(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        // On the 5s timeout the future is dropped; kill the child so a hanging rc
        // (e.g. a blocking prompt) doesn't leave an orphaned login shell running.
        .kill_on_drop(true)
        .output();
    let output = tokio::time::timeout(std::time::Duration::from_secs(5), future)
        .await
        .ok()?
        .ok()?;
    let stdout = String::from_utf8_lossy(&output.stdout);
    let start_marker = "\0__TB_OAT_S__\0";
    let end_marker = "\0__TB_OAT_E__\0";
    let start = stdout.find(start_marker)? + start_marker.len();
    let rest = &stdout[start..];
    let end = rest.find(end_marker)?;
    let token = rest[..end].trim().to_string();
    (!token.is_empty()).then_some(token)
}

#[cfg(not(target_os = "macos"))]
async fn harvest_shell_env_token_uncached() -> Option<String> {
    None
}

/// Resolve the user's login shell for the harvest. `$SHELL` is usually unset for
/// a launchd-spawned GUI app, so fall back to Directory Services.
#[cfg(target_os = "macos")]
fn detect_login_shell() -> String {
    if let Ok(shell) = std::env::var("SHELL") {
        let shell = shell.trim();
        if !shell.is_empty() {
            return shell.to_string();
        }
    }
    if let Some(user) = current_username() {
        if let Ok(output) = std::process::Command::new("/usr/bin/dscl")
            .args([".", "-read", &format!("/Users/{}", user), "UserShell"])
            .output()
        {
            if output.status.success() {
                if let Ok(text) = String::from_utf8(output.stdout) {
                    // "UserShell: /bin/zsh"
                    if let Some(path) = text.split_whitespace().nth(1) {
                        if !path.is_empty() {
                            return path.to_string();
                        }
                    }
                }
            }
        }
    }
    "/bin/zsh".to_string()
}

#[cfg(target_os = "macos")]
fn current_username() -> Option<String> {
    if let Ok(user) = std::env::var("USER") {
        let user = user.trim();
        if !user.is_empty() {
            return Some(user.to_string());
        }
    }
    let output = std::process::Command::new("/usr/bin/id")
        .arg("-un")
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }
    let user = String::from_utf8(output.stdout).ok()?.trim().to_string();
    (!user.is_empty()).then_some(user)
}

/// B — a RAW setup-token stored in the `tokenbar-claude-oauth-token` Keychain
/// service. Works regardless of launch method (unlike the env var), which is why
/// it's the reliable fallback for a Finder/login-item GUI app.
#[cfg(target_os = "macos")]
fn load_claude_raw_token_from_keychain() -> Result<Option<String>, String> {
    let output = std::process::Command::new("/usr/bin/security")
        .args([
            "find-generic-password",
            "-s",
            CLAUDE_RAW_TOKEN_KEYCHAIN_SERVICE,
            "-w",
        ])
        .output()
        .map_err(|e| format!("read TokenBar Claude token from Keychain: {}", e))?;
    if !output.status.success() {
        return if keychain_item_not_found(&output.status) {
            Ok(None)
        } else {
            Err("TokenBar Claude Keychain token could not be read.".to_string())
        };
    }
    let raw = String::from_utf8(output.stdout)
        .map_err(|_| "TokenBar Claude Keychain token is not UTF-8.".to_string())?;
    let raw = raw.trim().to_string();
    if raw.is_empty() {
        return Err("TokenBar Claude Keychain token is empty.".to_string());
    }
    Ok(Some(raw))
}

#[cfg(not(target_os = "macos"))]
fn load_claude_raw_token_from_keychain() -> Result<Option<String>, String> {
    Ok(None)
}

async fn refresh_codex_credentials(
    auth_path: &Path,
) -> Result<(CodexCredentials, ProviderCacheBinding), ProviderFetchFailure> {
    let refresh = agent_account_scope::begin_refresh("codex").map_err(|_| {
        ProviderFetchFailure::terminal("Codex credential refresh lock is unavailable.")
    })?;
    refresh_codex_credentials_with(
        auth_path,
        &refresh,
        request_codex_refresh,
        save_codex_credentials,
        |_| Ok(()),
    )
    .await
}

async fn request_codex_refresh(
    refresh_token: String,
    attempt_binding: ProviderCacheBinding,
) -> Result<Value, ProviderFetchFailure> {
    let client = provider_http_client_builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|_| {
            ProviderFetchFailure::terminal("Codex refresh client could not be created.")
        })?;
    let body = serde_json::json!({
        "client_id": CODEX_CLIENT_ID,
        "grant_type": "refresh_token",
        "refresh_token": refresh_token,
        "scope": "openid profile email"
    });
    let response = client
        .post(CODEX_REFRESH_URL)
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .json(&body)
        .send()
        .await
        .map_err(|error| {
            ProviderFetchFailure::from_send_error(
                "Codex token refresh failed. Retrying automatically.",
                Some(attempt_binding.clone()),
                &error,
            )
        })?;
    let status = response.status().as_u16();
    let body = read_response_body(status, false, || async {
        response.text().await.map_err(|error| {
            TransportErrorFacts::from_reqwest(&error, TransportPhase::ResponseBody)
        })
    })
    .await
    .map_err(|failure| match failure {
        ResponseReadFailure::Transient(diagnostic) => ProviderFetchFailure::transient(
            "Codex token refresh failed. Retrying automatically.",
            Some(attempt_binding),
            diagnostic,
        ),
        ResponseReadFailure::Terminal(_) => ProviderFetchFailure::terminal(
            "Codex OAuth refresh failed. Run `codex` to log in again.",
        ),
    })?;
    serde_json::from_str(&body).map_err(|_| {
        ProviderFetchFailure::terminal("Codex OAuth refresh response could not be decoded.")
    })
}

fn resolve_codex_cache_binding_with<R: RefreshScopeTransaction + ?Sized>(
    credentials: &CodexCredentials,
    refresh: &R,
) -> Result<ProviderCacheBinding, AccountScopeError> {
    let primary = refresh.resolve_current(
        credentials.scope_slot.semantic_source,
        &credentials.scope_slot.canonical_location,
        credentials.scope_marker(),
    )?;
    let corroborating = credentials
        .account_id
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(|account_id| {
            agent_account_scope::resolve_authoritative(
                "codex",
                AuthoritativeIdKind::OpaqueId,
                account_id,
            )
        })
        .transpose()?;
    Ok(ProviderCacheBinding::new(primary, corroborating))
}

async fn refresh_codex_credentials_with<R, Request, RequestFuture, Save, Checkpoint>(
    auth_path: &Path,
    refresh: &R,
    request: Request,
    save: Save,
    mut checkpoint: Checkpoint,
) -> Result<(CodexCredentials, ProviderCacheBinding), ProviderFetchFailure>
where
    R: RefreshScopeTransaction + ?Sized,
    Request: FnOnce(String, ProviderCacheBinding) -> RequestFuture,
    RequestFuture: std::future::Future<Output = Result<Value, ProviderFetchFailure>>,
    Save: FnOnce(&CodexCredentials) -> Result<CodexCredentialWriteReceipt, String>,
    Checkpoint: FnMut(RefreshCheckpoint) -> Result<(), ProviderFetchFailure>,
{
    let credentials =
        load_codex_credentials_from(auth_path).map_err(ProviderFetchFailure::terminal)?;
    checkpoint(RefreshCheckpoint::Reloaded)?;
    let pre_binding = resolve_codex_cache_binding_with(&credentials, refresh).map_err(|_| {
        ProviderFetchFailure::terminal("Codex account identity could not be verified.")
    })?;
    if !codex_credentials_needs_refresh(&credentials.access_token, credentials.last_refresh) {
        return Ok((credentials, pre_binding));
    }

    let refresh_token = credentials
        .refresh_token
        .as_deref()
        .map(str::trim)
        .filter(|token| !token.is_empty())
        .ok_or_else(|| ProviderFetchFailure::terminal("Codex auth.json has no refresh token."))?
        .to_string();
    let old_marker = credentials.scope_marker().to_vec();
    let json = request(refresh_token, pre_binding.clone()).await?;
    checkpoint(RefreshCheckpoint::NetworkReturned)?;

    let response = json.as_object().ok_or_else(|| {
        ProviderFetchFailure::terminal("Codex OAuth refresh response was not a JSON object.")
    })?;
    let access_token = string_key(response, "access_token", "accessToken").ok_or_else(|| {
        ProviderFetchFailure::terminal(
            "Codex OAuth refresh response contained no usable access token.",
        )
    })?;
    let refreshed = CodexCredentials {
        access_token,
        refresh_token: string_key(response, "refresh_token", "refreshToken")
            .or(credentials.refresh_token),
        id_token: string_key(response, "id_token", "idToken").or(credentials.id_token),
        account_id: credentials.account_id,
        last_refresh: Some(Utc::now()),
        auth_path: credentials.auth_path,
        raw_json: credentials.raw_json,
        scope_slot: credentials.scope_slot,
    };
    let write_receipt = save(&refreshed).map_err(|_| {
        ProviderFetchFailure::terminal("Codex refreshed credentials could not be saved.")
    })?;
    checkpoint(RefreshCheckpoint::CredentialsPersisted)?;
    let post_primary = match refresh.transfer(
        refreshed.scope_slot.semantic_source,
        &refreshed.scope_slot.canonical_location,
        &old_marker,
        refreshed.scope_marker(),
    ) {
        Ok(account_scope) => account_scope,
        Err(_) => {
            let _ = rollback_codex_credentials_if_unchanged(&write_receipt);
            return Err(ProviderFetchFailure::terminal(
                "Codex credential lineage could not be preserved.",
            ));
        }
    };
    checkpoint(RefreshCheckpoint::MetadataHandled)?;
    // The account ID is unchanged by refresh and was already verified in the
    // pre-request binding. Reuse it instead of performing a second fallible
    // metadata resolution after credentials and lineage are durable.
    let post_binding = ProviderCacheBinding::new(post_primary, pre_binding.corroborating);
    Ok((refreshed, post_binding))
}

async fn refresh_claude_credentials(
    original: &ClaudeCredentials,
) -> Result<
    (
        ClaudeCredentials,
        AccountScope,
        Option<ProviderCacheBinding>,
    ),
    ProviderFetchFailure,
> {
    let refresh = agent_account_scope::begin_refresh("claude").map_err(|_| {
        ProviderFetchFailure::terminal("Claude credential refresh lock is unavailable.")
    })?;
    refresh_claude_credentials_with(
        original,
        &refresh,
        reload_claude_credentials,
        request_claude_refresh,
        save_claude_credentials,
        |_| Ok(()),
    )
    .await
}

async fn request_claude_refresh(
    refresh_token: String,
    attempt_binding: ProviderCacheBinding,
) -> Result<ClaudeRefreshResponse, ProviderFetchFailure> {
    let client = provider_http_client_builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|_| {
            ProviderFetchFailure::terminal("Claude refresh client could not be created.")
        })?;
    let response = client
        .post(CLAUDE_REFRESH_URL)
        .header(reqwest::header::ACCEPT, "application/json")
        .header(
            reqwest::header::CONTENT_TYPE,
            "application/x-www-form-urlencoded",
        )
        .body(form_urlencoded(&[
            ("grant_type", "refresh_token"),
            ("refresh_token", &refresh_token),
            ("client_id", CLAUDE_CLIENT_ID),
        ]))
        .send()
        .await
        .map_err(|error| {
            ProviderFetchFailure::from_send_error(
                "Claude OAuth refresh failed. Retrying automatically.",
                Some(attempt_binding.clone()),
                &error,
            )
        })?;
    let status = response.status().as_u16();
    let body = read_response_body(status, false, || async {
        response.text().await.map_err(|error| {
            TransportErrorFacts::from_reqwest(&error, TransportPhase::ResponseBody)
        })
    })
    .await
    .map_err(|failure| match failure {
        ResponseReadFailure::Transient(diagnostic) => ProviderFetchFailure::transient(
            "Claude OAuth refresh failed. Retrying automatically.",
            Some(attempt_binding),
            diagnostic,
        ),
        ResponseReadFailure::Terminal(_) => ProviderFetchFailure::terminal(
            "Claude OAuth refresh failed. Run `claude` to re-authenticate.",
        ),
    })?;
    serde_json::from_str(&body).map_err(|_| {
        ProviderFetchFailure::terminal("Claude OAuth refresh response could not be decoded.")
    })
}

async fn refresh_claude_credentials_with<R, Reload, Request, RequestFuture, Save, Checkpoint>(
    original: &ClaudeCredentials,
    refresh: &R,
    reload: Reload,
    request: Request,
    save: Save,
    mut checkpoint: Checkpoint,
) -> Result<
    (
        ClaudeCredentials,
        AccountScope,
        Option<ProviderCacheBinding>,
    ),
    ProviderFetchFailure,
>
where
    R: RefreshScopeTransaction + ?Sized,
    Reload: FnOnce(&ClaudeCredentials) -> Result<ClaudeCredentials, String>,
    Request: FnOnce(String, ProviderCacheBinding) -> RequestFuture,
    RequestFuture:
        std::future::Future<Output = Result<ClaudeRefreshResponse, ProviderFetchFailure>>,
    Save: FnOnce(&ClaudeCredentials) -> Result<(), String>,
    Checkpoint: FnMut(RefreshCheckpoint) -> Result<(), ProviderFetchFailure>,
{
    let credentials = reload(original).map_err(ProviderFetchFailure::terminal)?;
    checkpoint(RefreshCheckpoint::Reloaded)?;
    let marker = credentials.scope_marker().ok_or_else(|| {
        ProviderFetchFailure::terminal("Claude credential has no trusted account marker.")
    })?;
    let pre_scope = refresh
        .resolve_current(
            credentials.scope_slot.semantic_source,
            &credentials.scope_slot.canonical_location,
            marker,
        )
        .map_err(|_| {
            ProviderFetchFailure::terminal("Claude account identity could not be verified.")
        })?;
    let pre_binding = ProviderCacheBinding::primary(pre_scope.clone());
    if !claude_credentials_expired(&credentials) {
        return Ok((credentials, pre_scope, Some(pre_binding)));
    }

    let refresh_token = credentials
        .refresh_token
        .as_deref()
        .filter(|token| !token.is_empty())
        .ok_or_else(|| {
            ProviderFetchFailure::terminal(
                "Claude OAuth token is expired and has no refresh token. Run `claude`.",
            )
        })?
        .to_string();
    let old_marker = refresh_token.as_bytes().to_vec();
    let token_response = request(refresh_token, pre_binding).await?;
    checkpoint(RefreshCheckpoint::NetworkReturned)?;
    let access_token = token_response.access_token.trim();
    if access_token.is_empty() {
        return Err(ProviderFetchFailure::terminal(
            "Claude OAuth refresh response has no access token.",
        ));
    }
    let refreshed = ClaudeCredentials {
        access_token: access_token.to_string(),
        refresh_token: token_response
            .refresh_token
            .as_deref()
            .map(str::trim)
            .filter(|token| !token.is_empty())
            .map(str::to_string)
            .or_else(|| credentials.refresh_token.clone()),
        expires_at: Some(Utc::now() + chrono::Duration::seconds(token_response.expires_in)),
        scopes: credentials.scopes.clone(),
        rate_limit_tier: credentials.rate_limit_tier.clone(),
        subscription_type: credentials.subscription_type.clone(),
        source: credentials.source,
        raw_root: credentials.raw_root.clone(),
        keychain_account: credentials.keychain_account.clone(),
        scope_slot: credentials.scope_slot.clone(),
    };
    let new_marker = refreshed.scope_marker().ok_or_else(|| {
        ProviderFetchFailure::terminal("Claude refreshed credential has no trusted marker.")
    })?;
    let scope = refresh
        .transfer(
            refreshed.scope_slot.semantic_source,
            &refreshed.scope_slot.canonical_location,
            &old_marker,
            new_marker,
        )
        .map_err(|_| {
            ProviderFetchFailure::terminal("Claude credential lineage could not be preserved.")
        })?;
    checkpoint(RefreshCheckpoint::MetadataHandled)?;
    let persisted = save(&refreshed).is_ok();
    checkpoint(RefreshCheckpoint::CredentialsPersisted)?;
    let cache_binding = if persisted {
        Some(ProviderCacheBinding::primary(
            refresh
                .resolve_current(
                    refreshed.scope_slot.semantic_source,
                    &refreshed.scope_slot.canonical_location,
                    new_marker,
                )
                .map_err(|_| {
                    ProviderFetchFailure::terminal(
                        "Claude account identity could not be verified after refresh.",
                    )
                })?,
        ))
    } else {
        None
    };
    Ok((refreshed, scope, cache_binding))
}

fn reload_claude_credentials(original: &ClaudeCredentials) -> Result<ClaudeCredentials, String> {
    match original.source {
        ClaudeCredentialSource::Keychain => {
            let account = claude_keychain_account().ok_or_else(|| {
                "Claude Keychain account could not be captured during refresh.".to_string()
            })?;
            let raw =
                load_claude_credentials_from_keychain_item(Some(&account))?.ok_or_else(|| {
                    "Claude Keychain credentials disappeared during refresh.".to_string()
                })?;
            let mut credentials =
                parse_claude_credentials_data(&raw, ClaudeCredentialSource::Keychain)?;
            credentials.keychain_account = Some(account);
            Ok(credentials)
        }
        ClaudeCredentialSource::File => {
            let raw = fs::read_to_string(claude_credentials_path())
                .map_err(|e| format!("reload Claude credentials file: {e}"))?;
            parse_claude_credentials_data(&raw, ClaudeCredentialSource::File)
        }
        ClaudeCredentialSource::Environment => {
            Err("Claude environment credentials cannot be refreshed in place.".to_string())
        }
        ClaudeCredentialSource::Desktop => Err(CLAUDE_DESKTOP_REFRESH_ERROR.to_string()),
        ClaudeCredentialSource::ConfigDir(_) => Err(CLAUDE_READ_ONLY_REFRESH_ERROR.to_string()),
    }
}

/// Merge the rotated access/refresh tokens back into the credentials store they
/// came from, preserving every other field the Claude CLI wrote.
fn save_claude_credentials(credentials: &ClaudeCredentials) -> Result<(), String> {
    match credentials.source {
        ClaudeCredentialSource::Keychain => save_claude_credentials_to_keychain(credentials),
        ClaudeCredentialSource::File => {
            save_claude_credentials_to_file(credentials, &claude_credentials_path())
        }
        ClaudeCredentialSource::Environment => Ok(()),
        // Never write to Claude Desktop's files.
        ClaudeCredentialSource::Desktop => Err(CLAUDE_DESKTOP_REFRESH_ERROR.to_string()),
        // Never write to a configured directory's store either.
        ClaudeCredentialSource::ConfigDir(_) => Err(CLAUDE_READ_ONLY_REFRESH_ERROR.to_string()),
    }
}

fn save_claude_credentials_to_file(
    credentials: &ClaudeCredentials,
    path: &Path,
) -> Result<(), String> {
    let current_raw = fs::read_to_string(path)
        .map_err(|e| format!("read current Claude credentials file: {e}"))?;
    let data = merge_claude_credentials_json(credentials, &current_raw)?;
    atomic_write(path, &data)
}

/// Replace `path` atomically: write a sibling temp file, then rename over the
/// target. A crash or partial write leaves the original credentials intact
/// rather than a truncated file that would break both TokenBar and the Claude
/// CLI (the rename is atomic within one filesystem).
fn atomic_write(path: &Path, data: &str) -> Result<(), String> {
    let parent = path.parent().ok_or_else(|| {
        format!(
            "credentials path {} has no parent directory",
            path.display()
        )
    })?;
    fs::create_dir_all(parent).map_err(|e| format!("create {}: {}", parent.display(), e))?;

    let file_name = path
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("credentials");
    // Per-write-unique temp name (pid + a monotonic seq). The O_EXCL open below
    // must never collide with an orphan a crashed earlier write left at a fixed
    // path, or every later write-back in this long-lived process would fail with
    // AlreadyExists and silently stop persisting rotated tokens.
    static TMP_SEQ: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    let seq = TMP_SEQ.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let tmp = parent.join(format!(".{}.tmp.{}.{}", file_name, std::process::id(), seq));

    // Stage into the temp, fsync it, then atomically replace the target. Create with
    // O_EXCL + 0600 up front: the mode-at-creation closes the umask-default
    // window a write-then-chmod leaves the secret readable in, and O_EXCL
    // refuses to follow a symlink pre-seeded at the temp path.
    let staged = (|| -> Result<(), String> {
        use std::io::Write as _;
        let mut opts = fs::OpenOptions::new();
        opts.write(true).create_new(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt as _;
            opts.mode(0o600);
        }
        let mut file = opts
            .open(&tmp)
            .map_err(|e| format!("create {}: {}", tmp.display(), e))?;
        file.write_all(data.as_bytes())
            .map_err(|e| format!("write {}: {}", tmp.display(), e))?;
        // Flush data to disk before the rename so a power loss can't leave the
        // renamed file pointing at never-written blocks — the crash-safety this
        // function's doc-comment promises.
        file.sync_all()
            .map_err(|e| format!("sync {}: {}", tmp.display(), e))
    })();
    // Any failure after the temp exists removes it, so a transient write error
    // can't strand an orphan that wedges the next write.
    if let Err(error) = staged {
        let _ = fs::remove_file(&tmp);
        return Err(error);
    }
    if let Err(error) = tokscale_core::fs_atomic::replace_file(&tmp, path) {
        let _ = fs::remove_file(&tmp);
        return Err(format!("replace {}: {}", path.display(), error));
    }
    // Persist the rename itself so it survives a power loss right afterward.
    #[cfg(unix)]
    if let Ok(dir) = fs::File::open(parent) {
        let _ = dir.sync_all();
    }
    Ok(())
}

/// Merge rotated tokens into the current credentials JSON only when the
/// `claudeAiOauth` object still matches the one captured at refresh reload.
/// Top-level siblings come from `current_raw`, so unrelated concurrent writes
/// survive. Pure so both file and Keychain decisions are fixture-testable.
fn merge_claude_credentials_json(
    credentials: &ClaudeCredentials,
    current_raw: &str,
) -> Result<String, String> {
    let expected_oauth = credentials
        .raw_root
        .as_ref()
        .and_then(|root| root.get("claudeAiOauth"))
        .and_then(Value::as_object)
        .ok_or_else(|| "Reloaded Claude credentials have no claudeAiOauth object.".to_string())?;
    let mut current_root: Value = serde_json::from_str(current_raw)
        .map_err(|e| format!("decode current Claude credentials: {e}"))?;
    let current_oauth = current_root
        .get("claudeAiOauth")
        .and_then(Value::as_object)
        .ok_or_else(|| "Current Claude credentials have no claudeAiOauth object.".to_string())?;
    if current_oauth != expected_oauth {
        return Err(
            "Claude credentials changed during refresh; refusing stale write-back.".to_string(),
        );
    }

    let oauth = current_root
        .get_mut("claudeAiOauth")
        .and_then(Value::as_object_mut)
        .ok_or_else(|| "Current Claude credentials have no claudeAiOauth object.".to_string())?;
    oauth.insert(
        "accessToken".to_string(),
        Value::String(credentials.access_token.clone()),
    );
    if let Some(refresh) = &credentials.refresh_token {
        oauth.insert("refreshToken".to_string(), Value::String(refresh.clone()));
    }
    if let Some(expires_at) = credentials.expires_at {
        oauth.insert(
            "expiresAt".to_string(),
            Value::Number(expires_at.timestamp_millis().into()),
        );
    }
    serde_json::to_string(&current_root).map_err(|e| format!("encode Claude credentials: {}", e))
}

fn prepare_claude_keychain_write<'a>(
    credentials: &'a ClaudeCredentials,
    current_account: Option<&str>,
    current_raw: &str,
) -> Result<(&'a str, String), String> {
    let captured_account = credentials.keychain_account.as_deref().ok_or_else(|| {
        "Claude Keychain refresh has no captured account; refusing write-back.".to_string()
    })?;
    if current_account != Some(captured_account) {
        return Err(
            "Claude Keychain account changed during refresh; refusing write-back.".to_string(),
        );
    }
    let data = merge_claude_credentials_json(credentials, current_raw)?;
    Ok((captured_account, data))
}

#[cfg(target_os = "macos")]
fn save_claude_credentials_to_keychain(credentials: &ClaudeCredentials) -> Result<(), String> {
    let captured_account = credentials.keychain_account.as_deref().ok_or_else(|| {
        "Claude Keychain refresh has no captured account; refusing write-back.".to_string()
    })?;
    let current_raw = load_claude_credentials_from_keychain_item(Some(captured_account))?
        .ok_or_else(|| "Claude Keychain credentials disappeared during refresh.".to_string())?;
    let current_account = claude_keychain_account();
    let (account, data) =
        prepare_claude_keychain_write(credentials, current_account.as_deref(), &current_raw)?;

    // NOTE: security(1) has no compare-and-swap operation. The exact-item read,
    // account guard, and target comparison close the network-wait race and the
    // write always stays pinned to the captured account. A same-item mutation
    // between this check and `-U` remains the existing CLI platform limitation.
    // `-w <data>` also puts the JSON on argv briefly; the item is already
    // same-user-readable while the Keychain is unlocked. Move to SecItem only if
    // either platform assumption changes.
    let status = std::process::Command::new("/usr/bin/security")
        .args([
            "add-generic-password",
            "-U",
            "-s",
            CLAUDE_KEYCHAIN_SERVICE,
            "-a",
            account,
            "-w",
            &data,
        ])
        .status()
        .map_err(|e| format!("write Claude Keychain credentials: {}", e))?;
    if !status.success() {
        return Err("security add-generic-password failed for Claude credentials.".to_string());
    }
    Ok(())
}

#[cfg(not(target_os = "macos"))]
fn save_claude_credentials_to_keychain(_credentials: &ClaudeCredentials) -> Result<(), String> {
    Err("Keychain writes are only supported on macOS.".to_string())
}

/// Read the account name the Claude Keychain item is stored under so the
/// write-back updates that same item instead of creating a duplicate.
#[cfg(target_os = "macos")]
fn claude_keychain_account() -> Option<String> {
    let output = std::process::Command::new("/usr/bin/security")
        .args(["find-generic-password", "-s", CLAUDE_KEYCHAIN_SERVICE])
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }
    let text = String::from_utf8_lossy(&output.stdout);
    // Attribute line looks like: `    "acct"<blob>="alice"`
    for line in text.lines() {
        let line = line.trim_start();
        if let Some(rest) = line.strip_prefix("\"acct\"") {
            if let Some(eq) = rest.find('=') {
                let value = rest[eq + 1..].trim();
                // security renders a non-printable acct as `0x<hex>  "ascii"`;
                // the string-scrape can't recover the real bytes, so treat it as
                // unresolved (fail closed) rather than returning a corrupt
                // account that `add-generic-password -U` would spawn a duplicate
                // "Claude Code-credentials" item under.
                if value.starts_with("0x") {
                    return None;
                }
                let value = value.trim_matches('"');
                if !value.is_empty() && value != "<NULL>" {
                    return Some(value.to_string());
                }
            }
        }
    }
    None
}

#[cfg(not(target_os = "macos"))]
fn claude_keychain_account() -> Option<String> {
    None
}

fn save_codex_credentials(
    credentials: &CodexCredentials,
) -> Result<CodexCredentialWriteReceipt, String> {
    let expected_tokens = credentials
        .raw_json
        .get("tokens")
        .ok_or_else(|| "Codex tokens missing from the loaded credentials.".to_string())?;
    let mut raw = load_codex_credentials_from(&credentials.auth_path)
        .map_err(|e| format!("reload Codex auth.json before saving: {}", e))?
        .raw_json;
    let current_tokens = raw
        .get("tokens")
        .ok_or_else(|| "Codex tokens disappeared before saving.".to_string())?;
    if current_tokens != expected_tokens {
        return Err("Codex tokens changed during refresh.".to_string());
    }
    let previous_root = raw.clone();
    let tokens = raw
        .get_mut("tokens")
        .and_then(Value::as_object_mut)
        .ok_or_else(|| "Codex tokens are not an object while saving.".to_string())?;

    tokens.insert(
        "access_token".to_string(),
        Value::String(credentials.access_token.clone()),
    );
    if let Some(refresh_token) = &credentials.refresh_token {
        tokens.insert(
            "refresh_token".to_string(),
            Value::String(refresh_token.clone()),
        );
    }
    if let Some(id_token) = &credentials.id_token {
        tokens.insert("id_token".to_string(), Value::String(id_token.clone()));
    }
    if let Some(account_id) = &credentials.account_id {
        tokens.insert("account_id".to_string(), Value::String(account_id.clone()));
    }
    raw["last_refresh"] = Value::String(Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true));
    let data =
        serde_json::to_string_pretty(&raw).map_err(|e| format!("encode Codex auth.json: {}", e))?;
    atomic_write(&credentials.auth_path, &data)
        .map_err(|e| format!("save Codex auth.json: {}", e))?;
    Ok(CodexCredentialWriteReceipt {
        path: credentials.auth_path.clone(),
        previous_root,
        persisted_root: raw,
    })
}

/// Restore the pre-refresh Codex root only while this refresh still owns the
/// exact root it persisted. External Codex writers do not share TokenBar's
/// refresh lock, so the compare-to-rename interval remains a known residual
/// window rather than a filesystem compare-and-swap.
fn rollback_codex_credentials_if_unchanged(
    receipt: &CodexCredentialWriteReceipt,
) -> Result<bool, String> {
    let current_raw = fs::read_to_string(&receipt.path)
        .map_err(|e| format!("read Codex auth.json before rollback: {}", e))?;
    let current_root: Value = serde_json::from_str(&current_raw)
        .map_err(|e| format!("decode Codex auth.json before rollback: {}", e))?;
    if current_root != receipt.persisted_root {
        return Ok(false);
    }
    let previous_data = serde_json::to_string_pretty(&receipt.previous_root)
        .map_err(|e| format!("encode Codex auth.json rollback: {}", e))?;
    atomic_write(&receipt.path, &previous_data)
        .map_err(|e| format!("rollback Codex auth.json: {}", e))?;
    Ok(true)
}

fn enrich_snapshot(snapshot: &mut AgentUsageSnapshot, now: i64) {
    enrich_snapshot_with(snapshot, now, |active_keys, observations, now| {
        crate::agent_quota_history::record_observations_and_evaluate(
            active_keys,
            observations,
            now,
            stranded_series_fold,
        )
    });
}

/// Every provider's input to the one-time schema-3 history fold (see
/// `agent_quota_history::fold_stranded_series`), resolved here — before the
/// history lock — because it needs the installation key and the account-scope
/// metadata lock.
///
/// Built for all five providers that recorded history before schema 3, not
/// for the one being recorded: whichever provider writes first under this
/// build is the only transaction that folds, and a provider left out of it
/// would stay stranded. Kiro and later providers never recorded before
/// schema 3, so they have nothing stranded and are not in it. Passed lazily: once this
/// process has seen the store past schema 3, the history module stops calling
/// it (`FOLD_SETTLED`).
///
/// claude, copilot and grok have only ever keyed history on a credential
/// lineage on Windows, so every series of theirs folds. codex and antigravity
/// also keyed on an authoritative owner ID when they had one (ChatGPT account
/// ID; the local IDE's email) — those series already carry the exact key the
/// writer now produces, so only their lineage-scoped series fold.
///
/// Best effort: a provider whose inputs cannot be resolved right now is left
/// out and its old series stay where they are. Refusing the write instead would
/// withhold every provider's history for as long as the failure lasts.
fn stranded_series_fold() -> Vec<StrandedSeriesFold> {
    stranded_series_fold_with(
        |provider| agent_account_scope::resolve_history_scope(provider, None),
        agent_account_scope::resolve_lineage_scopes,
    )
}

fn stranded_series_fold_with(
    constant: impl Fn(&str) -> Result<HistoryScope, AccountScopeError>,
    lineage_scopes: impl Fn(&str) -> Result<Vec<AccountScope>, AccountScopeError>,
) -> Vec<StrandedSeriesFold> {
    const EVERY_SCOPE: [&str; 3] = ["claude", "copilot", "grok"];
    const LINEAGE_ONLY: [&str; 2] = ["codex", "antigravity"];
    let every = EVERY_SCOPE.into_iter().filter_map(|provider| {
        let target = constant(provider).ok()?;
        Some(StrandedSeriesFold {
            provider_id: provider,
            target,
            lineage_scopes: None,
        })
    });
    let lineage = LINEAGE_ONLY.into_iter().filter_map(|provider| {
        let target = constant(provider).ok()?;
        let scopes = lineage_scopes(provider).ok()?;
        Some(StrandedSeriesFold {
            provider_id: provider,
            target,
            lineage_scopes: Some(
                scopes
                    .iter()
                    .map(|scope| scope.as_str().to_string())
                    .collect(),
            ),
        })
    });
    every.chain(lineage).collect()
}

fn enrich_snapshot_with<F>(snapshot: &mut AgentUsageSnapshot, now: i64, mut record: F)
where
    F: FnMut(
        &[SeriesKey],
        &[QuotaObservation],
        i64,
    ) -> Result<Vec<BatchObservationResult>, HistoryError>,
{
    let mut card_ids = HashSet::new();
    let mut window_keys = HashSet::new();
    snapshot.windows.retain(|window| {
        let card_is_unique = !card_ids.contains(&window.card_id);
        let key_is_unique = window
            .window_key
            .as_ref()
            .is_none_or(|window_key| !window_keys.contains(window_key));
        if !card_is_unique || !key_is_unique {
            return false;
        }
        card_ids.insert(window.card_id.clone());
        if let Some(window_key) = window.window_key.as_ref() {
            window_keys.insert(window_key.clone());
        }
        true
    });

    // Deliberately still keyed on `account_scope`: this is the sole suppressor
    // of durable history for an identity that was never verified.
    // `history_scope` resolves whenever the installation key is readable, so
    // re-keying this guard on it would start recording per-account history for
    // an unverified identity.
    let Ok(_account_scope) = snapshot.account_scope.as_ref() else {
        for window in &mut snapshot.windows {
            if window.window_key.is_some() {
                window.unavailable("accountScope");
            }
        }
        return;
    };
    let Ok(history_scope) = snapshot.history_scope.as_ref() else {
        for window in &mut snapshot.windows {
            if window.window_key.is_some() {
                window.unavailable("accountScope");
            }
        }
        return;
    };
    let mut active_keys = Vec::new();
    let mut observations = Vec::new();
    let mut mapped_indices = Vec::new();

    for (index, window) in snapshot.windows.iter_mut().enumerate() {
        let Some(window_key) = window.window_key.as_deref() else {
            // The provider already classified this card as windowIdentity.
            continue;
        };
        let key = SeriesKey::new(snapshot.client_id.clone(), history_scope, window_key);
        active_keys.push(key.clone());
        if matches!(window.pace_status.state, PaceState::Unavailable) {
            // Emission protects existing history from capacity eviction, but
            // missing reset and other typed early rejects never record a sample.
            continue;
        }
        let Some(reset_at) = window
            .resets_at
            .as_deref()
            .and_then(parse_datetime)
            .map(|reset| reset.timestamp())
        else {
            window.unavailable("invalidEvidence");
            continue;
        };
        if reset_at <= now
            || !window.used_percent.is_finite()
            || !(0.0..=100.0).contains(&window.used_percent)
        {
            window.unavailable("invalidEvidence");
            continue;
        }
        observations.push(QuotaObservation {
            key,
            reset_at: Some(reset_at),
            used_percent: window.used_percent,
            provider: window.provider_duration,
            contract: window.contract_duration,
        });
        mapped_indices.push(index);
    }

    if active_keys.is_empty() {
        return;
    }

    let results = match record(&active_keys, &observations, now) {
        Ok(results) if results.len() == mapped_indices.len() => results,
        Ok(_) => {
            for index in mapped_indices {
                snapshot.windows[index].unavailable("history");
            }
            return;
        }
        Err(error) => {
            let reason = if error == HistoryError::StoreCapacity {
                "storeCapacity"
            } else {
                "history"
            };
            for index in mapped_indices {
                snapshot.windows[index].unavailable(reason);
            }
            return;
        }
    };

    for (index, result) in mapped_indices.into_iter().zip(results) {
        let window = &mut snapshot.windows[index];
        match result {
            Ok((
                HistoryOutcome::Ready {
                    duration_seconds,
                    source,
                    ..
                },
                historical,
                complete_cycles,
            )) => {
                window.duration_seconds = Some(duration_seconds);
                window.duration_source = Some(source);
                window.window_minutes = Some(duration_seconds / 60);
                match historical {
                    Some(pace) if historical_pace_is_coherent(&pace) => {
                        window.pace_status = PaceStatusPayload {
                            state: PaceState::Available,
                            window_key: window.window_key.clone(),
                            duration_seconds: Some(duration_seconds),
                            duration_source: Some(source),
                            complete_cycles,
                            reason: None,
                        };
                        window.historical_pace = Some(historical_pace_payload(pace));
                    }
                    Some(_) => {
                        window.unavailable("history");
                    }
                    None => {
                        window.pace_status = PaceStatusPayload {
                            state: PaceState::LearningHistory,
                            window_key: window.window_key.clone(),
                            duration_seconds: Some(duration_seconds),
                            duration_source: Some(source),
                            complete_cycles,
                            reason: None,
                        };
                        window.historical_pace = None;
                    }
                }
            }
            Ok((HistoryOutcome::LearningDuration, None, _)) => {
                window.duration_seconds = None;
                window.duration_source = Some(DurationSource::Observed);
                window.window_minutes = None;
                window.pace_status = PaceStatusPayload {
                    state: PaceState::LearningDuration,
                    window_key: window.window_key.clone(),
                    duration_seconds: None,
                    duration_source: Some(DurationSource::Observed),
                    complete_cycles: 0,
                    reason: None,
                };
                window.historical_pace = None;
            }
            Ok((HistoryOutcome::Unavailable(reason), _, _)) => {
                window.unavailable(duration_unavailable_reason(reason));
            }
            Err(error) => {
                window.unavailable(if error == HistoryError::StoreCapacity {
                    "storeCapacity"
                } else {
                    "history"
                });
            }
            Ok((HistoryOutcome::LearningDuration, Some(_), _)) => {
                window.unavailable("history");
            }
        }
    }
}

fn duration_unavailable_reason(reason: DurationUnavailableReason) -> &'static str {
    match reason {
        DurationUnavailableReason::MissingReset => "missingReset",
        DurationUnavailableReason::InvalidEvidence => "invalidEvidence",
    }
}

fn historical_pace_is_coherent(pace: &HistoricalPace) -> bool {
    pace.expected_percent.is_finite()
        && (0.0..=100.0).contains(&pace.expected_percent)
        && pace
            .eta_seconds
            .is_none_or(|eta| eta.is_finite() && eta >= 0.0)
        && pace
            .run_out_probability
            .is_none_or(|probability| probability.is_finite() && (0.0..=1.0).contains(&probability))
        && (pace.eta_seconds.is_none() == pace.will_last_to_reset)
}

fn historical_pace_payload(pace: HistoricalPace) -> HistoricalPacePayload {
    HistoricalPacePayload {
        expected_used_percent: pace.expected_percent,
        eta_seconds: pace.eta_seconds,
        will_last_to_reset: pace.will_last_to_reset,
        run_out_probability: pace.run_out_probability,
    }
}

fn codex_windows(
    rate_limit: Option<&CodexRateLimit>,
    additional_rate_limits: Option<&[CodexAdditionalRateLimit]>,
    now: DateTime<Utc>,
) -> Vec<UsageWindow> {
    let mut windows = Vec::new();
    let mut emitted_card_ids = HashSet::new();
    if let Some(rate_limit) = rate_limit {
        let mut main = [
            ("primary", rate_limit.primary_window.clone()),
            ("secondary", rate_limit.secondary_window.clone()),
        ];
        main.sort_by_key(|(_, window)| {
            window
                .as_ref()
                .map_or(2, |window| match window.limit_window_seconds {
                    18_000 => 0,
                    604_800 => 1,
                    _ => 2,
                })
        });
        for (slot, window) in main
            .into_iter()
            .filter_map(|(slot, window)| window.map(|window| (slot, window)))
        {
            let semantic = match window.limit_window_seconds {
                18_000 => Some(("Session", "main.session.v1")),
                604_800 => Some(("Weekly", "main.weekly.v1")),
                _ => None,
            };
            let (label, window_key) = semantic.unwrap_or(("Unknown", ""));
            let card_id = if window_key.is_empty() {
                format!("row.main.{slot}.v1")
            } else {
                window_key.to_string()
            };
            let Some(mapped) = map_window_with_identity(
                label,
                window,
                now,
                card_id.clone(),
                (!window_key.is_empty()).then(|| window_key.to_string()),
            ) else {
                continue;
            };
            if !emitted_card_ids.insert(card_id) {
                continue;
            }
            windows.push(mapped);
        }
    }

    let mut anonymous_slots = HashSet::new();
    for extra in additional_rate_limits.unwrap_or(&[]) {
        let source = additional_limit_source(extra);
        let digest = source.map(sha256_hex);
        let Some(rate_limit) = extra.rate_limit.as_ref() else {
            continue;
        };
        for (slot, window) in [
            ("primary", rate_limit.primary_window.clone()),
            ("secondary", rate_limit.secondary_window.clone()),
        ]
        .into_iter()
        .filter_map(|(slot, window)| window.map(|window| (slot, window)))
        {
            let Some(digest) = digest.as_deref() else {
                let Some(mapped) = map_window_with_identity(
                    "Unknown",
                    window,
                    now,
                    format!("row.additional.unknown.{slot}.v1"),
                    None,
                ) else {
                    continue;
                };
                if anonymous_slots.insert(slot) {
                    windows.push(mapped);
                }
                continue;
            };
            let label = additional_limit_label(extra);
            let window_key = format!("additional.{digest}.{slot}.v1");
            let Some(mapped) = map_window_with_identity(
                &label,
                window,
                now,
                window_key.clone(),
                Some(window_key.clone()),
            ) else {
                continue;
            };
            if !emitted_card_ids.insert(window_key) {
                continue;
            }
            windows.push(mapped);
        }
    }
    windows
}

fn claude_windows(usage: &ClaudeUsageResponse, now: DateTime<Utc>) -> Vec<UsageWindow> {
    let mut windows = Vec::new();
    push_claude_window(
        &mut windows,
        "Session",
        "session.v1",
        DurationEvidence::contract(300 * 60),
        usage.five_hour.as_ref(),
        now,
    );
    push_claude_window(
        &mut windows,
        "Weekly",
        "weekly.v1",
        DurationEvidence::contract(7 * 24 * 60 * 60),
        usage.seven_day.as_ref(),
        now,
    );
    push_claude_window(
        &mut windows,
        "OAuth Apps",
        "oauth_apps.weekly.v1",
        DurationEvidence::contract(7 * 24 * 60 * 60),
        usage.seven_day_oauth_apps.as_ref(),
        now,
    );
    push_claude_window(
        &mut windows,
        "Sonnet",
        "sonnet.weekly.v1",
        DurationEvidence::contract(7 * 24 * 60 * 60),
        usage.seven_day_sonnet.as_ref(),
        now,
    );
    push_claude_window(
        &mut windows,
        "Opus",
        "opus.weekly.v1",
        DurationEvidence::contract(7 * 24 * 60 * 60),
        usage.seven_day_opus.as_ref(),
        now,
    );
    push_claude_window(
        &mut windows,
        "Designs",
        "design.weekly.v1",
        DurationEvidence::contract(7 * 24 * 60 * 60),
        usage.design_window(),
        now,
    );
    push_claude_window(
        &mut windows,
        "Daily Routines",
        "routines.weekly.v1",
        DurationEvidence::contract(7 * 24 * 60 * 60),
        usage.routines_window(),
        now,
    );
    append_claude_scoped_windows(&mut windows, usage.limits.as_deref(), now);
    if let Some(extra) = claude_extra_usage_window(usage.extra_usage.as_ref()) {
        windows.push(extra);
    }
    windows
}

impl ClaudeUsageResponse {
    fn design_window(&self) -> Option<&ClaudeWindow> {
        [
            self.seven_day_design.as_ref(),
            self.seven_day_claude_design.as_ref(),
            self.claude_design.as_ref(),
            self.design.as_ref(),
            self.seven_day_omelette.as_ref(),
            self.omelette.as_ref(),
            self.omelette_promotional.as_ref(),
        ]
        .into_iter()
        .flatten()
        .find(|window| window.has_valid_utilization())
    }

    fn routines_window(&self) -> Option<&ClaudeWindow> {
        [
            self.seven_day_routines.as_ref(),
            self.seven_day_claude_routines.as_ref(),
            self.claude_routines.as_ref(),
            self.routines.as_ref(),
            self.routine.as_ref(),
            self.seven_day_cowork.as_ref(),
            self.cowork.as_ref(),
        ]
        .into_iter()
        .flatten()
        .find(|window| window.has_valid_utilization())
    }
}

fn push_claude_window(
    windows: &mut Vec<UsageWindow>,
    label: &str,
    window_key: &str,
    contract_duration: DurationEvidence,
    window: Option<&ClaudeWindow>,
    now: DateTime<Utc>,
) {
    if let Some(mapped) = window
        .and_then(|window| map_claude_window(label, window_key, contract_duration, window, now))
    {
        windows.push(mapped);
    }
}

fn map_claude_window(
    label: &str,
    window_key: &str,
    contract_duration: DurationEvidence,
    window: &ClaudeWindow,
    now: DateTime<Utc>,
) -> Option<UsageWindow> {
    let used = window.utilization?;
    let resets_at = window.resets_at.as_deref().and_then(parse_datetime);
    UsageWindow::try_from_provider_used_percent(label.to_string(), used, resets_at, now).map(
        |window| {
            window.with_identity(
                window_key,
                Some(window_key.to_string()),
                None,
                Some(contract_duration),
            )
        },
    )
}

fn append_claude_scoped_windows(
    windows: &mut Vec<UsageWindow>,
    limits: Option<&[ClaudeLimitEntry]>,
    now: DateTime<Utc>,
) {
    // Flat labels are the provider's human model names; compare them with the
    // scoped display name rather than the id slug, which may include a
    // namespace or version.
    let flat_model_slugs = windows
        .iter()
        .map(|window| claude_slug(&window.label))
        .collect::<HashSet<_>>();
    let mut emitted_slugs = HashSet::new();
    for entry in limits.unwrap_or(&[]) {
        // Do not filter on `is_active`: live enforceable limits can report false.
        if entry.group.as_deref() != Some("weekly")
            || entry.kind.as_deref() != Some("weekly_scoped")
        {
            continue;
        }
        let Some(percent) = entry
            .percent
            .filter(|percent| percent.is_finite() && (0.0..=100.0).contains(percent))
        else {
            continue;
        };
        let Some(model) = entry.scope.as_ref().and_then(|scope| scope.model.as_ref()) else {
            continue;
        };
        let Some(display_name) = model
            .display_name
            .as_deref()
            .map(str::trim)
            .filter(|display_name| !display_name.is_empty())
        else {
            continue;
        };
        let display_name_slug = claude_slug(display_name);
        let model_id_slug = model
            .id
            .as_deref()
            .map(str::trim)
            .filter(|id| !id.is_empty())
            .map(claude_slug);
        if model_id_slug
            .as_deref()
            .is_some_and(claude_is_all_models_slug)
            || claude_is_all_models_slug(&display_name_slug)
        {
            continue;
        }
        if flat_model_slugs.contains(&display_name_slug) {
            continue;
        }
        // Identity comes from the display name, never the model id, even though
        // the id looks like the more stable choice. The live payload reports
        // `scope.model.id: null` while the field exists, so Anthropic populating
        // it later would silently move this window's `card_id` and window key —
        // dropping the user's persisted gauge selection (Swift matches
        // `clientId|cardId` exactly) and restarting its quota-history series —
        // with no visible change to the label. A display-name rename is the only
        // way identity moves now, and that one is at least visible to the user.
        if display_name_slug.is_empty() {
            continue;
        }
        let slug = display_name_slug;
        if !emitted_slugs.insert(slug.clone()) {
            continue;
        }
        // A scoped entry that succeeds a legacy flat field inherits that
        // field's semantic key and label. Anthropic moving a quota out of
        // `seven_day_*` and into `limits[]` is the migration this mapper exists
        // to support, and minting a new identity for it would cost the user the
        // same persisted selection and history the flat lane already owns —
        // for an otherwise unchanged quota. The flat-window guard above means
        // this branch only runs once the flat field is actually gone.
        let (window_key, label, is_model) = CLAUDE_SCOPED_FLAT_SUCCESSORS
            .iter()
            .find(|(model_slug, _, _, _)| *model_slug == slug)
            .map_or_else(
                || {
                    (
                        format!("weekly_scoped.{slug}.v1"),
                        format!("{display_name} only"),
                        true,
                    )
                },
                |(_, key, label, is_model)| ((*key).to_string(), (*label).to_string(), *is_model),
            );
        let resets_at = entry.resets_at.as_deref().and_then(parse_datetime);
        if let Some(window) = UsageWindow::try_from_provider_used_percent(
            label, percent, resets_at, now,
        )
        .map(|window| {
            let window = window.with_identity(
                window_key.clone(),
                Some(window_key),
                None,
                Some(DurationEvidence::contract(7 * 24 * 60 * 60)),
            );
            // The same slug the identity is derived from, so a consumer
            // filtering usage by scope and a consumer keying history by
            // window cannot disagree about which model this is. Designs and
            // Daily Routines narrow a quota to a product surface, not a model;
            // scoping them would filter usage to a model id no message carries.
            if is_model {
                window.with_model_scope(slug.clone())
            } else {
                window
            }
        }) {
            windows.push(window);
        }
    }
}

/// Model-name slugs that already have a flat-field lane, paired with the
/// semantic key and label that lane owns. Keeping these frozen is what lets a
/// quota move from `seven_day_*` into `limits[]` without the user losing a
/// pinned gauge or its learned pace. Entries here must match the identities
/// emitted by `claude_windows()` for the corresponding flat fields.
///
/// The fourth field says whether the slug names a model, which decides
/// whether the window carries a `model_scope`: this table is the one place a
/// successor slug is classified, so there is no second list to drift.
const CLAUDE_SCOPED_FLAT_SUCCESSORS: &[(&str, &str, &str, bool)] = &[
    ("sonnet", "sonnet.weekly.v1", "Sonnet", true),
    ("opus", "opus.weekly.v1", "Opus", true),
    ("designs", "design.weekly.v1", "Designs", false),
    ("daily-routines", "routines.weekly.v1", "Daily Routines", false),
];

fn claude_is_all_models_slug(slug: &str) -> bool {
    slug == "all-models" || slug.ends_with("-all-models")
}

fn claude_slug(value: &str) -> String {
    let mut slug = String::new();
    let mut pending_separator = false;
    for character in value.chars() {
        if character.is_alphanumeric() {
            if pending_separator && !slug.is_empty() {
                slug.push('-');
            }
            slug.extend(character.to_lowercase());
            pending_separator = false;
        } else if !slug.is_empty() {
            pending_separator = true;
        }
    }
    slug
}

/// Parse the `anthropic-ratelimit-unified-{5h,7d}-{utilization,reset}` response
/// headers into Session/Weekly usage windows. Pure — no network or I/O.
///
/// Unlike the oauth/usage JSON body (`utilization` 0..100, RFC3339 reset), these
/// headers use a 0..1 fraction and a Unix-epoch-seconds reset. This is the
/// fallback source for inference-only `claude setup-token` tokens.
fn parse_unified_ratelimit_windows(
    headers: &reqwest::header::HeaderMap,
    now: DateTime<Utc>,
) -> Vec<UsageWindow> {
    let read_f64 = |name: &str| -> Option<f64> {
        headers.get(name)?.to_str().ok()?.trim().parse::<f64>().ok()
    };
    let read_i64 = |name: &str| -> Option<i64> {
        headers.get(name)?.to_str().ok()?.trim().parse::<i64>().ok()
    };
    let mut windows = Vec::new();
    if let Some(window) = unified_ratelimit_window_with_identity(
        "Session",
        "session.v1",
        DurationEvidence::contract(300 * 60),
        read_f64("anthropic-ratelimit-unified-5h-utilization"),
        read_i64("anthropic-ratelimit-unified-5h-reset"),
        now,
    ) {
        windows.push(window);
    }
    if let Some(window) = unified_ratelimit_window_with_identity(
        "Weekly",
        "weekly.v1",
        DurationEvidence::contract(7 * 24 * 60 * 60),
        read_f64("anthropic-ratelimit-unified-7d-utilization"),
        read_i64("anthropic-ratelimit-unified-7d-reset"),
        now,
    ) {
        windows.push(window);
    }
    windows
}

/// Build one window from a unified-ratelimit header pair. Gated on utilization
/// (mirrors `map_claude_window`); reset is optional. `utilization_fraction` is
/// 0..1 (scaled ×100); `reset_epoch_seconds` is Unix seconds (like the Codex
/// `map_window` epoch handling).
fn unified_ratelimit_window_with_identity(
    label: &str,
    window_key: &str,
    contract_duration: DurationEvidence,
    utilization_fraction: Option<f64>,
    reset_epoch_seconds: Option<i64>,
    now: DateTime<Utc>,
) -> Option<UsageWindow> {
    let used = utilization_fraction? * 100.0;
    let resets_at = reset_epoch_seconds
        .filter(|seconds| *seconds > 0)
        .and_then(|seconds| Utc.timestamp_opt(seconds, 0).single());
    UsageWindow::try_from_provider_used_percent(label.to_string(), used, resets_at, now).map(
        |window| {
            window.with_identity(
                window_key,
                Some(window_key.to_string()),
                None,
                Some(contract_duration),
            )
        },
    )
}

#[cfg(test)]
fn unified_ratelimit_window(
    label: &str,
    utilization_fraction: Option<f64>,
    reset_epoch_seconds: Option<i64>,
    now: DateTime<Utc>,
) -> Option<UsageWindow> {
    let (window_key, duration) = if label.eq_ignore_ascii_case("Session") {
        ("session.v1", DurationEvidence::contract(300 * 60))
    } else {
        ("weekly.v1", DurationEvidence::contract(7 * 24 * 60 * 60))
    };
    unified_ratelimit_window_with_identity(
        label,
        window_key,
        duration,
        utilization_fraction,
        reset_epoch_seconds,
        now,
    )
}

fn claude_extra_usage_window(extra: Option<&ClaudeExtraUsage>) -> Option<UsageWindow> {
    let extra = extra?;
    if !extra.is_enabled {
        return None;
    }
    let used = extra.utilization.or_else(|| {
        let used = extra.used_credits?;
        let limit = extra.monthly_limit?;
        if limit > 0.0 {
            Some((used / limit) * 100.0)
        } else {
            None
        }
    })?;
    let reset_text = match (extra.used_credits, extra.monthly_limit) {
        (Some(used), Some(limit)) => Some(format!(
            "Monthly cap: {} / {}",
            format_currency_minor_units(used, extra.currency.as_deref()),
            format_currency_minor_units(limit, extra.currency.as_deref())
        )),
        _ => None,
    };
    let mut window = UsageWindow::try_from_provider_used_percent(
        "Extra usage".to_string(),
        used,
        None,
        Utc::now(),
    )?
    .with_identity(
        "extra_usage.v1",
        Some("extra_usage.v1".to_string()),
        None,
        None,
    );
    window.reset_text = reset_text;
    Some(window)
}

fn claude_credits(extra: Option<&ClaudeExtraUsage>) -> Option<CreditsSnapshot> {
    let extra = extra?;
    if !extra.is_enabled {
        return None;
    }
    let remaining = match (extra.monthly_limit, extra.used_credits) {
        (Some(limit), Some(used)) => Some(((limit - used) / 100.0).max(0.0)),
        _ => None,
    };
    Some(CreditsSnapshot {
        remaining,
        unlimited: false,
    })
}

fn format_currency_minor_units(value: f64, currency: Option<&str>) -> String {
    let major = value / 100.0;
    match currency.unwrap_or("USD").trim().to_uppercase().as_str() {
        "USD" => format!("${:.2}", major),
        code if !code.is_empty() => format!("{:.2} {}", major, code),
        _ => format!("${:.2}", major),
    }
}

fn additional_limit_label(limit: &CodexAdditionalRateLimit) -> String {
    let source = first_non_empty([
        limit.limit_name.as_deref(),
        limit.metered_feature.as_deref(),
    ])
    .unwrap_or("Codex extra limit");
    let lower = source.to_lowercase();
    if lower.contains("spark") {
        return "Codex Spark".to_string();
    }
    clean_limit_label(source)
}

fn first_non_empty(values: [Option<&str>; 2]) -> Option<&str> {
    values
        .into_iter()
        .flatten()
        .map(str::trim)
        .find(|value| !value.is_empty())
}

fn clean_limit_label(value: &str) -> String {
    value
        .replace(['_', '-'], " ")
        .split_whitespace()
        .map(|part| {
            if part.eq_ignore_ascii_case("gpt") {
                "GPT".to_string()
            } else if part.eq_ignore_ascii_case("codex") {
                "Codex".to_string()
            } else {
                let mut chars = part.chars();
                match chars.next() {
                    Some(first) => format!("{}{}", first.to_uppercase(), chars.as_str()),
                    None => String::new(),
                }
            }
        })
        .collect::<Vec<_>>()
        .join(" ")
}

fn map_window_with_identity(
    label: &str,
    window: CodexWindow,
    now: DateTime<Utc>,
    card_id: impl Into<String>,
    window_key: Option<String>,
) -> Option<UsageWindow> {
    let resets_at = (window.reset_at != 0)
        .then(|| Utc.timestamp_opt(window.reset_at, 0).single())
        .flatten();
    let provider_duration = (window.limit_window_seconds != 0)
        .then(|| DurationEvidence::provider(window.reset_at, window.limit_window_seconds));
    UsageWindow::try_from_provider_used_percent(
        label.to_string(),
        window.used_percent,
        resets_at,
        now,
    )
    .map(|window| window.with_identity(card_id, window_key, provider_duration, None))
}

fn additional_limit_source(limit: &CodexAdditionalRateLimit) -> Option<String> {
    first_non_empty([
        limit.metered_feature.as_deref(),
        limit.limit_name.as_deref(),
    ])
    .map(str::to_string)
}

fn sha256_hex(value: String) -> String {
    let digest = Sha256::digest(value.trim().as_bytes());
    digest.iter().map(|byte| format!("{byte:02x}")).collect()
}

pub(crate) fn reset_text(reset: DateTime<Utc>, now: DateTime<Utc>) -> String {
    let seconds = (reset - now).num_seconds();
    if seconds <= 0 {
        return "Resets now".to_string();
    }
    let minutes = (seconds + 59) / 60;
    if minutes < 60 {
        return format!("Resets in {}m", minutes);
    }
    let hours = minutes / 60;
    let mins = minutes % 60;
    // Anything spanning a day or more reads in days+hours so the weekly windows
    // stay consistent across agents (Claude reported 47h, Codex 2d — unify both
    // to days); sub-day windows (sessions) keep the hours/minutes form.
    if hours < 24 {
        if mins > 0 {
            return format!("Resets in {}h {}m", hours, mins);
        }
        return format!("Resets in {}h", hours);
    }
    let days = hours / 24;
    let rem_hours = hours % 24;
    if rem_hours > 0 {
        format!("Resets in {}d {}h", days, rem_hours)
    } else {
        format!("Resets in {}d", days)
    }
}

fn codex_home() -> PathBuf {
    std::env::var_os("CODEX_HOME")
        .map(PathBuf::from)
        .filter(|p| !p.as_os_str().is_empty())
        .or_else(|| crate::user_home_dir().map(|home| home.join(".codex")))
        .unwrap_or_else(|| PathBuf::from(".codex"))
}

fn claude_credentials_path() -> PathBuf {
    crate::user_home_dir()
        .map(|home| home.join(".claude/.credentials.json"))
        .unwrap_or_else(|| PathBuf::from(".claude/.credentials.json"))
}

fn codex_credentials_needs_refresh(
    access_token: &str,
    last_refresh: Option<DateTime<Utc>>,
) -> bool {
    codex_credentials_needs_refresh_at(access_token, last_refresh, Utc::now())
}

fn codex_credentials_needs_refresh_at(
    access_token: &str,
    last_refresh: Option<DateTime<Utc>>,
    now: DateTime<Utc>,
) -> bool {
    if let Some(expires_at) = jwt_expiration(access_token) {
        return expires_at
            <= now + chrono::Duration::minutes(CODEX_ACCESS_TOKEN_REFRESH_WINDOW_MINUTES);
    }

    let Some(last_refresh) = last_refresh else {
        return true;
    };
    (now - last_refresh).num_days() > CODEX_TOKEN_REFRESH_INTERVAL_DAYS
}

fn jwt_expiration(token: &str) -> Option<DateTime<Utc>> {
    let seconds = jwt_payload(token)?.get("exp")?.as_i64()?;
    Utc.timestamp_opt(seconds, 0).single()
}

/// TokenBar cannot renew a read-only token, so it stops using one a minute
/// early rather than sending a token that expires in flight.
fn claude_read_only_credentials_expired(
    credentials: &ClaudeCredentials,
    now: DateTime<Utc>,
) -> bool {
    credentials.expires_at.is_some_and(|expires_at| {
        now + chrono::Duration::seconds(CLAUDE_READ_ONLY_EXPIRY_SKEW_SECS) >= expires_at
    })
}

/// The terminal message for an expired read-only credential, or `None` for a
/// source TokenBar refreshes itself. Fixed strings: a configured directory's
/// card is already labelled with its basename, so the message names neither
/// the path nor a relative command.
fn claude_read_only_expired_message(source: &ClaudeCredentialSource) -> Option<&'static str> {
    match source {
        ClaudeCredentialSource::Desktop => Some(CLAUDE_DESKTOP_EXPIRED_ERROR),
        ClaudeCredentialSource::ConfigDir(_) => Some(CLAUDE_CONFIG_DIR_EXPIRED_ERROR),
        _ => None,
    }
}

fn claude_credentials_expired(credentials: &ClaudeCredentials) -> bool {
    credentials
        .expires_at
        .is_some_and(|expires_at| Utc::now() >= expires_at)
}

pub(crate) fn parse_datetime(value: &str) -> Option<DateTime<Utc>> {
    DateTime::parse_from_rfc3339(value)
        .map(|dt| dt.with_timezone(&Utc))
        .ok()
}

static CLAUDE_USER_AGENT: LazyLock<String> = LazyLock::new(detect_claude_user_agent);

fn claude_user_agent() -> &'static str {
    CLAUDE_USER_AGENT.as_str()
}

fn detect_claude_user_agent() -> String {
    let mut command = std::process::Command::new("claude");
    command.arg("--version");
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt as _;
        command.creation_flags(windows_sys::Win32::System::Threading::CREATE_NO_WINDOW);
    }
    command
        .output()
        .ok()
        .filter(|output| output.status.success())
        .and_then(|output| claude_user_agent_from_stdout(&output.stdout))
        .unwrap_or_else(|| "claude-code/2.1.0".to_string())
}

fn claude_user_agent_from_stdout(stdout: &[u8]) -> Option<String> {
    std::str::from_utf8(stdout)
        .ok()?
        .split_whitespace()
        .next()
        .filter(|version| !version.is_empty())
        .map(|version| format!("claude-code/{version}"))
}

fn form_urlencoded(params: &[(&str, &str)]) -> String {
    params
        .iter()
        .map(|(key, value)| format!("{}={}", percent_encode(key), percent_encode(value)))
        .collect::<Vec<_>>()
        .join("&")
}

pub(crate) fn percent_encode(value: &str) -> String {
    let mut encoded = String::new();
    for byte in value.bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                encoded.push(byte as char);
            }
            b' ' => encoded.push('+'),
            _ => encoded.push_str(&format!("%{:02X}", byte)),
        }
    }
    encoded
}

fn string_key(
    map: &serde_json::Map<String, Value>,
    snake_case: &str,
    camel_case: &str,
) -> Option<String> {
    [snake_case, camel_case]
        .into_iter()
        .filter_map(|key| map.get(key).and_then(Value::as_str))
        .map(str::trim)
        .find(|value| !value.is_empty())
        .map(str::to_string)
}

fn jwt_payload(token: &str) -> Option<Value> {
    let payload = token.split('.').nth(1)?;
    let mut encoded = payload.replace('-', "+").replace('_', "/");
    while encoded.len() % 4 != 0 {
        encoded.push('=');
    }
    use base64::Engine;
    let data = base64::engine::general_purpose::STANDARD
        .decode(encoded)
        .ok()?;
    serde_json::from_slice(&data).ok()
}

fn jwt_email(token: &str) -> Option<String> {
    let payload = jwt_payload(token)?;
    payload
        .get("email")
        .and_then(Value::as_str)
        .or_else(|| {
            payload
                .get("https://api.openai.com/profile")
                .and_then(Value::as_object)
                .and_then(|profile| profile.get("email"))
                .and_then(Value::as_str)
        })
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
}

fn jwt_plan(token: &str) -> Option<String> {
    let payload = jwt_payload(token)?;
    payload
        .get("chatgpt_plan_type")
        .and_then(Value::as_str)
        .or_else(|| {
            payload
                .get("https://api.openai.com/auth")
                .and_then(Value::as_object)
                .and_then(|auth| auth.get("chatgpt_plan_type"))
                .and_then(Value::as_str)
        })
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
}

pub(crate) fn clean_plan(value: impl AsRef<str>) -> String {
    value
        .as_ref()
        .split(['_', '-'])
        .filter(|part| !part.is_empty())
        .map(|part| {
            let mut chars = part.chars();
            match chars.next() {
                Some(first) => format!("{}{}", first.to_uppercase(), chars.as_str()),
                None => String::new(),
            }
        })
        .collect::<Vec<_>>()
        .join(" ")
}

pub(crate) fn deserialize_optional_raw<'de, D, T>(deserializer: D) -> Result<Option<T>, D::Error>
where
    D: serde::Deserializer<'de>,
    T: serde::de::DeserializeOwned,
{
    let raw = Option::<Box<serde_json::value::RawValue>>::deserialize(deserializer)?;
    Ok(raw.and_then(|raw| serde_json::from_str(raw.get()).ok()))
}

fn deserialize_optional_non_empty_string<'de, D>(
    deserializer: D,
) -> Result<Option<String>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = Option::<Value>::deserialize(deserializer)?;
    Ok(value
        .as_ref()
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string))
}

fn deserialize_optional_f64<'de, D>(deserializer: D) -> Result<Option<f64>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = Option::<Value>::deserialize(deserializer)?;
    Ok(match value {
        Some(Value::Number(n)) => n.as_f64(),
        Some(Value::String(s)) => s.parse::<f64>().ok(),
        _ => None,
    })
}

fn deserialize_optional_claude_limits<'de, D>(
    deserializer: D,
) -> Result<Option<Vec<ClaudeLimitEntry>>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = Option::<Value>::deserialize(deserializer)?;
    Ok(value.and_then(|value| {
        value.as_array().map(|entries| {
            entries
                .iter()
                .filter_map(|entry| serde_json::from_value(entry.clone()).ok())
                .collect()
        })
    }))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::agent_account_scope::test_support::TestRefreshScope;

    /// A read failure other than an absent file belongs to an account that IS
    /// configured, so it must not reach the marker: `required_card_source` would
    /// hand it `unconfigured` and the Codex card would leave the tab bar while
    /// telling the user to run `codex`. A directory at the path is the reliably
    /// reproducible member of that set. Ported from macOS.
    #[test]
    fn a_codex_auth_json_that_exists_but_cannot_be_read_keeps_its_card() {
        let root = std::env::temp_dir().join(format!(
            "tb-codex-unreadable-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let unreadable = root.join("auth.json");
        fs::create_dir_all(&unreadable).unwrap();
        assert!(unreadable.is_dir(), "the fixture must not be a regular file");

        let display = load_codex_credentials_from(&unreadable).unwrap_err();
        assert_eq!(display, CODEX_CREDENTIALS_UNREADABLE_ERROR);
        assert_ne!(display, CODEX_UNCONFIGURED_ERROR);
        assert_eq!(
            required_card_source(
                &ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(display)),
                CODEX_UNCONFIGURED_ERROR,
            ),
            "oauth",
            "an unreadable credential is a configured account, and keeps its tab"
        );

        let absent = root.join("missing").join("auth.json");
        assert_eq!(
            load_codex_credentials_from(&absent).unwrap_err(),
            CODEX_UNCONFIGURED_ERROR
        );
        let _ = fs::remove_dir_all(&root);
    }

    /// The control the `unconfigured` arm needs: everything that is not
    /// "there is no credential" stays `oauth`, so a configured-but-failing
    /// card keeps its tab. The transient case is the one that would hurt
    /// most — a card that could not be reached must not read as one that was
    /// never set up, and the display alone cannot tell them apart, which is
    /// why the arm matches the variant too. Ported from macOS's
    /// `required_card_source_keeps_oauth_for_everything_except_absence`.
    #[test]
    fn required_card_source_keeps_oauth_for_everything_except_absence() {
        let marker = CODEX_UNCONFIGURED_ERROR;
        let transient_with_marker = ProviderFetchOutcome::Failure(ProviderFetchFailure::transient(
            marker,
            None,
            SafeTransportDiagnostic::server_error(503),
        ));
        let other_terminal = ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
            "Codex auth.json exists but contains no OAuth tokens.",
        ));
        for outcome in [
            transient_with_marker,
            other_terminal,
            ProviderFetchOutcome::Absent,
        ] {
            assert_eq!(required_card_source(&outcome, marker), "oauth");
        }
    }

    /// The two required cards must not share a marker: matching Antigravity's
    /// absence against Codex's message (or the reverse) would hand one
    /// provider the other's verdict. Ported from macOS's
    /// `required_card_markers_are_not_interchangeable`.
    #[test]
    fn required_card_markers_are_not_interchangeable() {
        let antigravity = ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
            agent_antigravity::ANTIGRAVITY_UNCONFIGURED_ERROR,
        ));
        assert_eq!(
            required_card_source(&antigravity, agent_antigravity::ANTIGRAVITY_UNCONFIGURED_ERROR),
            "unconfigured"
        );
        assert_eq!(
            required_card_source(&antigravity, CODEX_UNCONFIGURED_ERROR),
            "oauth"
        );
    }

    #[test]
    fn claude_user_agent_uses_first_version_token() {
        assert_eq!(
            claude_user_agent_from_stdout(b"2.1.5 (Claude Code)\r\n").as_deref(),
            Some("claude-code/2.1.5")
        );
        assert_eq!(claude_user_agent_from_stdout(b" \r\n"), None);
        assert_eq!(claude_user_agent_from_stdout(&[0xff]), None);
    }

    #[test]
    fn parses_retry_after_seconds_and_http_date() {
        let header = reqwest::header::HeaderValue::from_static("120");
        let parsed = parse_retry_after(Some(&header)).unwrap();
        let delta = (parsed - Utc::now()).num_seconds();
        assert!((118..=120).contains(&delta), "delta was {}", delta);

        let header = reqwest::header::HeaderValue::from_static("Fri, 21 Nov 2025 09:00:00 GMT");
        let parsed = parse_retry_after(Some(&header)).unwrap();
        assert_eq!(parsed.timestamp(), 1_763_715_600);

        let header = reqwest::header::HeaderValue::from_static("bogus");
        assert!(parse_retry_after(Some(&header)).is_none());
        assert!(parse_retry_after(None).is_none());
    }

    #[test]
    fn string_key_uses_first_valid_snake_or_camel_alias() {
        let cases = [
            (
                "snake priority",
                serde_json::json!({
                    "snake_key": " snake-value ",
                    "camelKey": "camel-value"
                }),
                Some("snake-value"),
            ),
            (
                "snake missing",
                serde_json::json!({ "camelKey": " camel-value " }),
                Some("camel-value"),
            ),
            (
                "snake null",
                serde_json::json!({ "snake_key": null, "camelKey": "camel-value" }),
                Some("camel-value"),
            ),
            (
                "snake empty",
                serde_json::json!({ "snake_key": "", "camelKey": "camel-value" }),
                Some("camel-value"),
            ),
            (
                "snake whitespace",
                serde_json::json!({ "snake_key": " \t\n ", "camelKey": "camel-value" }),
                Some("camel-value"),
            ),
            (
                "snake non-string",
                serde_json::json!({
                    "snake_key": { "unexpected": true },
                    "camelKey": "camel-value"
                }),
                Some("camel-value"),
            ),
            (
                "both invalid",
                serde_json::json!({ "snake_key": false, "camelKey": "   " }),
                None,
            ),
        ];

        for (label, value, expected) in cases {
            let map = value.as_object().unwrap();
            assert_eq!(
                string_key(map, "snake_key", "camelKey").as_deref(),
                expected,
                "{label}"
            );
        }
    }

    fn codex_test_access_token(exp: i64) -> String {
        use base64::Engine as _;

        let payload = serde_json::to_vec(&serde_json::json!({ "exp": exp })).unwrap();
        format!(
            "header.{}.signature",
            base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(payload)
        )
    }

    #[test]
    fn codex_refresh_prefers_access_token_expiry_over_stale_last_refresh() {
        let now = Utc.timestamp_opt(1_758_080_400, 0).single().unwrap();
        let stale_last_refresh = Some(now - chrono::Duration::days(9));

        assert!(!codex_credentials_needs_refresh_at(
            &codex_test_access_token((now + chrono::Duration::minutes(6)).timestamp()),
            stale_last_refresh,
            now,
        ));
        assert!(codex_credentials_needs_refresh_at(
            &codex_test_access_token((now + chrono::Duration::minutes(5)).timestamp()),
            stale_last_refresh,
            now,
        ));
        assert!(codex_credentials_needs_refresh_at(
            &codex_test_access_token((now - chrono::Duration::seconds(1)).timestamp()),
            Some(now - chrono::Duration::days(1)),
            now,
        ));
    }

    #[test]
    fn codex_refresh_falls_back_to_last_refresh_without_valid_expiry() {
        let now = Utc.timestamp_opt(1_758_080_400, 0).single().unwrap();

        assert!(!codex_credentials_needs_refresh_at(
            "not-a-jwt",
            Some(now - chrono::Duration::days(8)),
            now,
        ));
        assert!(codex_credentials_needs_refresh_at(
            "not-a-jwt",
            Some(now - chrono::Duration::days(9)),
            now,
        ));
        assert!(codex_credentials_needs_refresh_at("not-a-jwt", None, now));
        assert!(jwt_expiration("header.eyJleHAiOiJub3QtYS1udW1iZXIifQ.signature").is_none());
    }

    #[test]
    fn claude_refresh_response_ignores_invalid_optional_refresh_token() {
        let cases = [
            (
                "valid",
                serde_json::json!({
                    "access_token": "new-access",
                    "refresh_token": " new-refresh ",
                    "expires_in": 3600
                }),
                Some("new-refresh"),
            ),
            (
                "missing",
                serde_json::json!({ "access_token": "new-access", "expires_in": 3600 }),
                None,
            ),
            (
                "null",
                serde_json::json!({
                    "access_token": "new-access",
                    "refresh_token": null,
                    "expires_in": 3600
                }),
                None,
            ),
            (
                "empty",
                serde_json::json!({
                    "access_token": "new-access",
                    "refresh_token": "",
                    "expires_in": 3600
                }),
                None,
            ),
            (
                "whitespace",
                serde_json::json!({
                    "access_token": "new-access",
                    "refresh_token": " \t\n ",
                    "expires_in": 3600
                }),
                None,
            ),
            (
                "non-string",
                serde_json::json!({
                    "access_token": "new-access",
                    "refresh_token": { "unexpected": true },
                    "expires_in": 3600
                }),
                None,
            ),
        ];

        for (label, value, expected) in cases {
            let response: ClaudeRefreshResponse = serde_json::from_value(value).unwrap();
            assert_eq!(response.access_token, "new-access", "{label}");
            assert_eq!(response.expires_in, 3_600, "{label}");
            assert_eq!(response.refresh_token.as_deref(), expected, "{label}");
        }
        assert!(serde_json::from_value::<ClaudeRefreshResponse>(
            serde_json::json!({ "expires_in": 3600 })
        )
        .is_err());
        assert!(serde_json::from_value::<ClaudeRefreshResponse>(
            serde_json::json!({ "access_token": "new-access" })
        )
        .is_err());
    }

    #[test]
    fn claude_gate_is_binding_scoped_and_expires() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scope = TestRefreshScope::new("claude", "local-gate");
        let binding_a = ProviderCacheBinding::primary(
            scope
                .resolve_current("fixture", "account-a", b"marker-a")
                .unwrap(),
        );
        let binding_b = ProviderCacheBinding::primary(
            scope
                .resolve_current("fixture", "account-b", b"marker-b")
                .unwrap(),
        );
        let mut gate = ClaudeUsageGate::default();

        gate.record_rate_limit(binding_a.clone(), None, now);
        let until = gate.blocked_until_for(&binding_a, now).unwrap();
        assert_eq!((until - now).num_seconds(), 300);

        assert!(gate.blocked_until_for(&binding_b, now).is_none());
        assert!(gate.blocked_until_for(&binding_a, now).is_none());

        gate.record_rate_limit(
            binding_a.clone(),
            Some(now + chrono::Duration::seconds(60)),
            now,
        );
        assert!(gate
            .blocked_until_for(&binding_a, now + chrono::Duration::seconds(61))
            .is_none());
        gate.clear();
        assert!(gate.blocked_until_for(&binding_a, now).is_none());
        scope.cleanup();
    }

    #[tokio::test]
    async fn claude_login_usage_gates_current_binding_before_refresh_and_request() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scope = TestRefreshScope::new("claude", "routed-gate");
        let binding_a = ProviderCacheBinding::primary(
            scope
                .resolve_current("fixture", "account-a", b"marker-a")
                .unwrap(),
        );
        let binding_b = ProviderCacheBinding::primary(
            scope
                .resolve_current("fixture", "account-b", b"marker-b")
                .unwrap(),
        );
        let mut credentials = ClaudeCredentials {
            access_token: "claude-access".to_string(),
            refresh_token: Some("claude-refresh".to_string()),
            expires_at: Some(Utc::now() - chrono::Duration::minutes(1)),
            scopes: vec!["user:profile".to_string()],
            rate_limit_tier: None,
            subscription_type: None,
            source: ClaudeCredentialSource::File,
            raw_root: None,
            keychain_account: None,
            scope_slot: CredentialSlot {
                semantic_source: "fixture",
                canonical_location: "fixture".to_string(),
            },
        };
        let mut gate = ClaudeUsageGate::default();
        gate.record_rate_limit(binding_a.clone(), None, now);
        let refresh_calls = std::cell::Cell::new(0);
        let header_calls = std::cell::Cell::new(0);
        let usage_calls = std::cell::Cell::new(0);

        let (source, outcome) = fetch_claude_login_usage_with(
            credentials.clone(),
            binding_a.clone(),
            now,
            |binding, at| gate.blocked_until_for(binding, at),
            |credentials| {
                refresh_calls.set(refresh_calls.get() + 1);
                let binding = binding_a.clone();
                async move { Ok((credentials, binding.primary.clone(), Some(binding))) }
            },
            |_, _, _| async {
                header_calls.set(header_calls.get() + 1);
                claude_test_success_outcome()
            },
            |_, _, _, _| async {
                usage_calls.set(usage_calls.get() + 1);
                ("oauth", claude_test_success_outcome())
            },
        )
        .await;
        assert_eq!(source, "oauth");
        assert!(matches!(
            outcome,
            ProviderFetchOutcome::Failure(ProviderFetchFailure::Transient {
                attempt_binding: Some(ref binding),
                transport_diagnostic: SafeTransportDiagnostic {
                    category: TransportCategory::RateLimited,
                    status: Some(429),
                    ..
                },
                ..
            }) if binding == &binding_a
        ));
        assert_eq!(refresh_calls.get(), 0);
        assert_eq!(header_calls.get(), 0);
        assert_eq!(usage_calls.get(), 0);

        credentials.expires_at = None;
        credentials.scopes = vec!["org:create_api_key".to_string()];
        gate.record_rate_limit(binding_a.clone(), None, now);
        let (source, outcome) = fetch_claude_login_usage_with(
            credentials.clone(),
            binding_a.clone(),
            now,
            |binding, at| gate.blocked_until_for(binding, at),
            |credentials| {
                refresh_calls.set(refresh_calls.get() + 1);
                let binding = binding_a.clone();
                async move { Ok((credentials, binding.primary.clone(), Some(binding))) }
            },
            |_, _, _| async {
                header_calls.set(header_calls.get() + 1);
                claude_test_success_outcome()
            },
            |_, _, _, _| async {
                usage_calls.set(usage_calls.get() + 1);
                ("oauth", claude_test_success_outcome())
            },
        )
        .await;
        assert_eq!(source, "setup-token");
        assert!(matches!(outcome, ProviderFetchOutcome::Success { .. }));
        assert_eq!(refresh_calls.get(), 0);
        assert_eq!(header_calls.get(), 1);
        assert_eq!(usage_calls.get(), 0);
        assert!(gate.blocked_until_for(&binding_a, now).is_some());

        credentials.scopes = vec!["user:profile".to_string()];
        gate.record_rate_limit(binding_a.clone(), None, now);
        let (source, outcome) = fetch_claude_login_usage_with(
            credentials,
            binding_b.clone(),
            now,
            |binding, at| gate.blocked_until_for(binding, at),
            |credentials| {
                refresh_calls.set(refresh_calls.get() + 1);
                let binding = binding_b.clone();
                async move { Ok((credentials, binding.primary.clone(), Some(binding))) }
            },
            |_, _, _| async {
                header_calls.set(header_calls.get() + 1);
                claude_test_success_outcome()
            },
            |_, _, _, _| async {
                usage_calls.set(usage_calls.get() + 1);
                ("oauth", claude_test_success_outcome())
            },
        )
        .await;
        assert_eq!(source, "oauth");
        assert!(matches!(outcome, ProviderFetchOutcome::Success { .. }));
        assert_eq!(refresh_calls.get(), 0);
        assert_eq!(header_calls.get(), 1);
        assert_eq!(usage_calls.get(), 1);
        scope.cleanup();
    }

    fn cache_test_snapshot(
        client_id: &str,
        account_scope: Result<AccountScope, AccountScopeError>,
        now: DateTime<Utc>,
    ) -> AgentUsageSnapshot {
        AgentUsageSnapshot {
            account_key: None,
            merge_scope: None,
            client_id: client_id.to_string(),
            source: "oauth".to_string(),
            updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
            identity: Some(AgentIdentity {
                email: Some("fixture@example.invalid".to_string()),
                plan: Some("Fixture".to_string()),
            }),
            history_scope: account_scope
                .as_ref()
                .map(|scope| HistoryScope::for_test(scope.as_str()))
                .map_err(|error| *error),
            account_scope,
            windows: vec![UsageWindow::from_provider_used_percent(
                "Session".to_string(),
                20.0,
                Some(now + chrono::Duration::hours(5)),
                now,
            )
            .with_identity(
                "main.session.v1",
                Some("main.session.v1".to_string()),
                None,
                Some(DurationEvidence::contract(300 * 60)),
            )],
            credits: Some(CreditsSnapshot {
                remaining: Some(-2.5),
                unlimited: false,
            }),
            error: None,
            transport_diagnostic: None,
        }
    }

    fn claude_test_login_credentials() -> ClaudeCredentials {
        ClaudeCredentials {
            access_token: "claude-access".to_string(),
            refresh_token: Some("claude-refresh".to_string()),
            expires_at: None,
            scopes: vec!["user:profile".to_string()],
            rate_limit_tier: None,
            subscription_type: None,
            source: ClaudeCredentialSource::File,
            raw_root: None,
            keychain_account: None,
            scope_slot: CredentialSlot {
                semantic_source: "fixture",
                canonical_location: "fixture".to_string(),
            },
        }
    }

    fn claude_test_setup_token() -> ResolvedClaudeToken {
        ResolvedClaudeToken {
            access_token: "setup-access".to_string(),
            scope_slot: CredentialSlot {
                semantic_source: "fixture-setup",
                canonical_location: "fixture-setup".to_string(),
            },
        }
    }

    fn claude_test_success_outcome() -> ProviderFetchOutcome {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        ProviderFetchOutcome::Success {
            snapshot: cache_test_snapshot("claude", Err(AccountScopeError::NoTrustedEvidence), now),
            cache_binding: None,
        }
    }

    #[tokio::test]
    async fn claude_login_precedence_falls_through_only_for_absent_credentials() {
        {
            let primary_calls = std::cell::Cell::new(0);
            let setup_loads = std::cell::Cell::new(0);
            let setup_calls = std::cell::Cell::new(0);
            let (source, outcome) = fetch_claude_login_or_setup_with(
                resolve_stored_claude_login("{", ClaudeCredentialSource::File),
                |_| async {
                    primary_calls.set(primary_calls.get() + 1);
                    ("oauth", claude_test_success_outcome())
                },
                || {
                    setup_loads.set(setup_loads.get() + 1);
                    Ok(Some(claude_test_setup_token()))
                },
                |_| async {
                    setup_calls.set(setup_calls.get() + 1);
                    ("setup-token", claude_test_success_outcome())
                },
            )
            .await;
            assert_eq!(source, "oauth");
            assert!(matches!(
                outcome,
                ProviderFetchOutcome::Failure(ProviderFetchFailure::Terminal { .. })
            ));
            assert_eq!(primary_calls.get(), 0);
            assert_eq!(setup_loads.get(), 0);
            assert_eq!(setup_calls.get(), 0);
        }

        for (label, primary_outcome) in [
            (
                "401",
                ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                    "Claude OAuth token expired or invalid. Run `claude` to re-authenticate.",
                )),
            ),
            (
                "transient",
                ProviderFetchOutcome::Failure(ProviderFetchFailure::transient(
                    "Claude usage request failed. Retrying automatically.",
                    None,
                    SafeTransportDiagnostic::server_error(503),
                )),
            ),
        ] {
            let primary_calls = std::cell::Cell::new(0);
            let setup_loads = std::cell::Cell::new(0);
            let setup_calls = std::cell::Cell::new(0);
            let (source, outcome) = fetch_claude_login_or_setup_with(
                ClaudeLoginResolution::Ready(claude_test_login_credentials()),
                |_| {
                    primary_calls.set(primary_calls.get() + 1);
                    async move { ("oauth", primary_outcome) }
                },
                || {
                    setup_loads.set(setup_loads.get() + 1);
                    Ok(Some(claude_test_setup_token()))
                },
                |_| async {
                    setup_calls.set(setup_calls.get() + 1);
                    ("setup-token", claude_test_success_outcome())
                },
            )
            .await;
            assert_eq!(source, "oauth", "{label}");
            match label {
                "401" => assert!(matches!(
                    outcome,
                    ProviderFetchOutcome::Failure(ProviderFetchFailure::Terminal { .. })
                )),
                _ => assert!(matches!(
                    outcome,
                    ProviderFetchOutcome::Failure(ProviderFetchFailure::Transient { .. })
                )),
            }
            assert_eq!(primary_calls.get(), 1, "{label}");
            assert_eq!(setup_loads.get(), 0, "{label}");
            assert_eq!(setup_calls.get(), 0, "{label}");
        }

        {
            let primary_calls = std::cell::Cell::new(0);
            let setup_loads = std::cell::Cell::new(0);
            let setup_calls = std::cell::Cell::new(0);
            let logged_out = resolve_stored_claude_login(
                r#"{"claudeAiOauth":{"refreshToken":"stale"}}"#,
                ClaudeCredentialSource::File,
            );
            assert!(matches!(logged_out, ClaudeLoginResolution::ExplicitLogout));
            let (source, outcome) = fetch_claude_login_or_setup_with(
                logged_out,
                |_| async {
                    primary_calls.set(primary_calls.get() + 1);
                    ("oauth", claude_test_success_outcome())
                },
                || {
                    setup_loads.set(setup_loads.get() + 1);
                    Ok(Some(claude_test_setup_token()))
                },
                |_| async {
                    setup_calls.set(setup_calls.get() + 1);
                    ("setup-token", claude_test_success_outcome())
                },
            )
            .await;
            assert_eq!(source, "setup-token");
            assert!(matches!(outcome, ProviderFetchOutcome::Success { .. }));
            assert_eq!(primary_calls.get(), 0);
            assert_eq!(setup_loads.get(), 1);
            assert_eq!(setup_calls.get(), 1);
        }
    }

    #[tokio::test]
    async fn claude_keychain_logout_blocks_stale_file_login_and_allows_setup_token() {
        const VALID_FILE_LOGIN: &str =
            r#"{"claudeAiOauth":{"accessToken":"file-access","refreshToken":"file-refresh"}}"#;

        for malformed in [
            r#"{"claudeAiOauth":{}}"#,
            r#"{"claudeAiOauth":{"accessToken":null}}"#,
        ] {
            assert!(matches!(
                resolve_stored_claude_login(malformed, ClaudeCredentialSource::Keychain),
                ClaudeLoginResolution::Terminal
            ));
        }

        let missing_file_loads = std::cell::Cell::new(0);
        let missing = load_stored_claude_login_with(
            || Ok(None),
            || {
                missing_file_loads.set(missing_file_loads.get() + 1);
                Ok(Some(VALID_FILE_LOGIN.to_string()))
            },
        );
        assert!(matches!(
            missing,
            ClaudeLoginResolution::Ready(ClaudeCredentials {
                source: ClaudeCredentialSource::File,
                ..
            })
        ));
        assert_eq!(missing_file_loads.get(), 1);

        let file_loads = std::cell::Cell::new(0);
        let login = load_stored_claude_login_with(
            || {
                Ok(Some(
                    r#"{"claudeAiOauth":{"refreshToken":"stale-file-shape"}}"#.to_string(),
                ))
            },
            || {
                file_loads.set(file_loads.get() + 1);
                Ok(Some(VALID_FILE_LOGIN.to_string()))
            },
        );
        assert!(matches!(login, ClaudeLoginResolution::ExplicitLogout));
        assert_eq!(file_loads.get(), 0);

        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scope = TestRefreshScope::new("claude", "keychain-explicit-logout");
        let binding = ProviderCacheBinding::primary(
            scope
                .resolve_current("fixture", "logged-in-account", b"marker")
                .unwrap(),
        );
        let mut gate = ClaudeUsageGate::default();
        gate.record_rate_limit(binding.clone(), None, now);
        assert!(gate.blocked_until_for(&binding, now).is_some());
        for resolved in ["oauth", "setup-token"] {
            clear_claude_gate_if_unconfigured(resolved, &mut gate);
            assert!(
                gate.blocked_until_for(&binding, now).is_some(),
                "{resolved}"
            );
        }
        clear_claude_gate_if_unconfigured("unconfigured", &mut gate);
        assert!(gate.blocked_until_for(&binding, now).is_none());

        let oauth_calls = std::cell::Cell::new(0);
        let setup_loads = std::cell::Cell::new(0);
        let setup_calls = std::cell::Cell::new(0);
        let (source, outcome) = fetch_claude_login_or_setup_with(
            login,
            |_| async {
                oauth_calls.set(oauth_calls.get() + 1);
                ("oauth", claude_test_success_outcome())
            },
            || {
                setup_loads.set(setup_loads.get() + 1);
                Ok(Some(claude_test_setup_token()))
            },
            |_| async {
                setup_calls.set(setup_calls.get() + 1);
                ("setup-token", claude_test_success_outcome())
            },
        )
        .await;
        assert_eq!(source, "setup-token");
        assert!(matches!(outcome, ProviderFetchOutcome::Success { .. }));
        assert_eq!(oauth_calls.get(), 0);
        assert_eq!(setup_loads.get(), 1);
        assert_eq!(setup_calls.get(), 1);
        scope.cleanup();
    }

    fn claude_test_desktop_credentials(expires_at: Option<DateTime<Utc>>) -> ClaudeCredentials {
        ClaudeCredentials {
            access_token: "desktop-access".to_string(),
            refresh_token: Some("desktop-refresh".to_string()),
            expires_at,
            scopes: vec!["user:profile".to_string()],
            rate_limit_tier: None,
            subscription_type: None,
            source: ClaudeCredentialSource::Desktop,
            raw_root: None,
            keychain_account: None,
            scope_slot: CredentialSlot {
                semantic_source: "fixture-desktop",
                canonical_location: "fixture-desktop".to_string(),
            },
        }
    }

    fn terminal_display(outcome: &ProviderFetchOutcome) -> Option<&str> {
        match outcome {
            ProviderFetchOutcome::Failure(ProviderFetchFailure::Terminal { display }) => {
                Some(display)
            }
            _ => None,
        }
    }

    // ---- Multi-account Claude cards (S1) ----------------------------------

    /// A synthetic successful Claude fetch for `key`, with the given merge
    /// identity and history scope.
    fn claude_account_success(
        key: Option<&str>,
        account_scope: AccountScope,
        merge_scope: Option<AccountScope>,
        history: &str,
        now: DateTime<Utc>,
    ) -> ClaudeAccountFetch {
        let mut snapshot = cache_test_snapshot("claude", Ok(account_scope.clone()), now);
        snapshot.history_scope = Ok(HistoryScope::for_test(history));
        snapshot.merge_scope = merge_scope;
        ClaudeAccountFetch {
            account_key: key.map(str::to_string),
            failure_source: "oauth",
            outcome: ProviderFetchOutcome::Success {
                snapshot,
                cache_binding: Some(ProviderCacheBinding::primary(account_scope)),
            },
            remembered_scope: None,
        }
    }

    fn claude_config_dir_credentials(
        dir: &str,
        expires_at: Option<DateTime<Utc>>,
    ) -> ClaudeCredentials {
        ClaudeCredentials {
            access_token: "config-dir-access".to_string(),
            refresh_token: Some("config-dir-refresh".to_string()),
            expires_at,
            scopes: vec!["user:profile".to_string()],
            rate_limit_tier: None,
            subscription_type: None,
            source: ClaudeCredentialSource::ConfigDir(PathBuf::from(dir)),
            raw_root: None,
            keychain_account: None,
            scope_slot: CredentialSlot {
                semantic_source: CLAUDE_CONFIG_DIR_FILE_SOURCE,
                canonical_location: "fixture-config-dir".to_string(),
            },
        }
    }

    fn keys_of(snapshots: &[AgentUsageSnapshot]) -> Vec<Option<&str>> {
        snapshots
            .iter()
            .map(|snapshot| snapshot.account_key.as_deref())
            .collect()
    }

    #[test]
    fn account_key_component_is_the_one_untrimmed_rule() {
        assert_eq!(account_key_component(None), None);
        assert_eq!(account_key_component(Some("")), None);
        assert_eq!(account_key_component(Some("  ")), Some("  "));
        assert_ne!(
            account_slot("claude", Some(r"C:\x\dir")),
            account_slot("claude", Some(r"C:\x\dir "))
        );
        assert_eq!(
            account_slot("claude", Some("")),
            account_slot("claude", None)
        );
        assert_eq!(ClaudeAccount::Primary { identify: true }.key(), None);
        assert_eq!(
            ClaudeAccount::ConfigDir(r"C:\x\dir".to_string()).key(),
            Some(r"C:\x\dir")
        );
        assert_eq!(
            ClaudeAccount::Desktop.key(),
            Some(CLAUDE_DESKTOP_ACCOUNT_KEY)
        );
        // The sentinel is not absolute, so no registered directory equals it.
        assert!(!CLAUDE_DESKTOP_ACCOUNT_KEY.contains(':'));
    }

    #[test]
    fn claude_account_requests_add_desktop_as_its_own_card() {
        let kinds = |requests: Vec<ClaudeAccountRequest>| -> Vec<String> {
            requests
                .into_iter()
                .map(|request| match request {
                    ClaudeAccountRequest::Primary { identify } => format!("primary:{identify}"),
                    ClaudeAccountRequest::ConfigDir(dir) => format!("dir:{dir}"),
                    ClaudeAccountRequest::Desktop(Ok(_)) => "desktop".to_string(),
                    ClaudeAccountRequest::Desktop(Err(_)) => "desktop-error".to_string(),
                })
                .collect()
        };
        // No extra account and no Desktop login: the primary alone, and it
        // asks nobody for a profile.
        assert_eq!(
            kinds(claude_account_requests(Vec::new(), Ok(None))),
            ["primary:false"]
        );
        assert!(!ClaudeAccount::Primary { identify: false }.wants_profile());
        // Only Desktop signed in: the primary (setup prompt) AND a Desktop card.
        assert_eq!(
            kinds(claude_account_requests(
                Vec::new(),
                Ok(Some(claude_test_desktop_credentials(None)))
            )),
            ["primary:true", "desktop"]
        );
        // An unreadable Desktop login is a Desktop error card; nothing to merge.
        assert_eq!(
            kinds(claude_account_requests(
                Vec::new(),
                Err(ProviderFetchFailure::terminal(CLAUDE_DESKTOP_READ_ERROR))
            )),
            ["primary:false", "desktop-error"]
        );
        assert_eq!(
            kinds(claude_account_requests(
                vec![r"C:\a".to_string(), r"C:\b".to_string()],
                Ok(Some(claude_test_desktop_credentials(None)))
            )),
            ["primary:true", r"dir:C:\a", r"dir:C:\b", "desktop"]
        );
    }

    /// G5-style: with no extra account and no Desktop login the published
    /// Claude card is exactly what the single-account path produced.
    #[test]
    fn primary_payload_is_byte_identical_without_extra_accounts() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scope = TestRefreshScope::new("claude", "primary-byte-identical");
        let binding = scope
            .resolve_current("fixture", "primary", b"marker")
            .unwrap();
        let fetch = claude_account_success(None, binding, None, "primary-history", now);
        let old_path_outcome = match &fetch.outcome {
            ProviderFetchOutcome::Success {
                snapshot,
                cache_binding,
            } => ProviderFetchOutcome::Success {
                snapshot: snapshot.clone(),
                cache_binding: cache_binding.clone(),
            },
            _ => unreachable!(),
        };

        let published = publish_claude_accounts_with(
            &Mutex::new(ProviderLastGoodCache::default()),
            now,
            vec![fetch],
            |_| {},
        );
        let single = apply_provider_outcome_with(
            &Mutex::new(ProviderLastGoodCache::default()),
            "claude",
            "oauth",
            now,
            old_path_outcome,
            |_| {},
        )
        .unwrap();

        assert_eq!(published.len(), 1);
        let published_json = serde_json::to_string(&published[0]).unwrap();
        assert_eq!(published_json, serde_json::to_string(&single).unwrap());
        let value: Value = serde_json::from_str(&published_json).unwrap();
        let keys: Vec<&str> = value
            .as_object()
            .unwrap()
            .keys()
            .map(String::as_str)
            .collect();
        assert_eq!(
            keys,
            [
                "accountScope",
                "clientId",
                "credits",
                "error",
                "historyScope",
                "identity",
                "source",
                "updatedAt",
                "windows"
            ],
            "no accountKey and no merge identity on the primary's wire"
        );
        assert!(published_json.starts_with(r#"{"clientId":"claude","source":"oauth","#));
        scope.cleanup();
    }

    #[test]
    fn account_key_is_serialized_only_for_extra_cards() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let cache = Mutex::new(ProviderLastGoodCache::default());
        let card = apply_account_outcome_with(
            &cache,
            "claude",
            Some(r"C:\Users\me\.claude-work"),
            "oauth",
            now,
            ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                CLAUDE_CONFIG_DIR_UNCONFIGURED_ERROR,
            )),
            |_| {},
        )
        .unwrap();
        let value = serde_json::to_value(&card).unwrap();
        assert_eq!(value["accountKey"], r"C:\Users\me\.claude-work");
        assert_eq!(
            value["source"], "oauth",
            "an extra account is never the setup prompt"
        );
    }

    #[test]
    fn per_account_last_good_slots_are_isolated() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scope = TestRefreshScope::new("claude", "per-account-last-good");
        let binding = ProviderCacheBinding::primary(
            scope
                .resolve_current("fixture", "shared", b"marker")
                .unwrap(),
        );
        let cache = Mutex::new(ProviderLastGoodCache::default());
        let transient = || {
            ProviderFetchOutcome::Failure(ProviderFetchFailure::transient(
                "Claude usage request failed. Retrying automatically.",
                Some(binding.clone()),
                timeout_diagnostic(),
            ))
        };

        // The primary succeeds and is cached under its own slot.
        apply_account_outcome_with(
            &cache,
            "claude",
            None,
            "oauth",
            now,
            ProviderFetchOutcome::Success {
                snapshot: cache_test_snapshot("claude", Ok(binding.primary.clone()), now),
                cache_binding: Some(binding.clone()),
            },
            |_| {},
        );
        // Another account failing terminally clears only its own slot.
        apply_account_outcome_with(
            &cache,
            "claude",
            Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
            "oauth",
            now,
            ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                CLAUDE_DESKTOP_EXPIRED_ERROR,
            )),
            |_| {},
        );
        // The other account never recovers the primary's snapshot...
        let desktop = apply_account_outcome_with(
            &cache,
            "claude",
            Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
            "oauth",
            now,
            transient(),
            |_| {},
        )
        .unwrap();
        assert!(desktop.windows.is_empty());
        assert_eq!(
            desktop.account_key.as_deref(),
            Some(CLAUDE_DESKTOP_ACCOUNT_KEY)
        );
        // ...and the primary still recovers its own.
        let primary =
            apply_account_outcome_with(&cache, "claude", None, "oauth", now, transient(), |_| {})
                .unwrap();
        assert_eq!(primary.windows.len(), 1);
        assert_eq!(primary.account_key, None);
        scope.cleanup();
    }

    #[test]
    fn per_account_gates_never_wipe_each_other() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scope = TestRefreshScope::new("claude", "per-account-gate");
        let binding_a = ProviderCacheBinding::primary(
            scope.resolve_current("fixture", "a", b"marker-a").unwrap(),
        );
        let binding_b = ProviderCacheBinding::primary(
            scope.resolve_current("fixture", "b", b"marker-b").unwrap(),
        );
        let gates = Mutex::new(ClaudeUsageGates::new());
        let extra = Some(r"C:\claude\work");
        with_gate_in(&gates, extra, |gate| {
            gate.record_rate_limit(binding_a.clone(), None, now)
        });
        // The primary checking a different binding resets only its own gate.
        assert!(
            with_gate_in(&gates, None, |gate| gate.blocked_until_for(&binding_b, now)).is_none()
        );
        with_gate_in(&gates, None, ClaudeUsageGate::clear);
        with_gate_in(
            &gates,
            Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
            ClaudeUsageGate::clear,
        );
        assert!(with_gate_in(&gates, extra, |gate| gate
            .blocked_until_for(&binding_a, now))
        .is_some());
        // The primary's own unconfigured clear also stays in its slot.
        with_gate_in(&gates, None, |gate| {
            clear_claude_gate_if_unconfigured("unconfigured", gate)
        });
        assert!(with_gate_in(&gates, extra, |gate| gate
            .blocked_until_for(&binding_a, now))
        .is_some());
        scope.cleanup();
    }

    #[test]
    fn header_cache_is_per_account_and_token_checked() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let cache = Mutex::new(ClaudeHeaderCache::new());
        let window = UsageWindow::from_provider_used_percent(
            "Session".to_string(),
            10.0,
            Some(now + chrono::Duration::hours(2)),
            now,
        );
        store_claude_header_windows(&cache, None, "primary-token", now, vec![window.clone()]);
        store_claude_header_windows(
            &cache,
            Some(r"C:\claude\work"),
            "extra-token",
            now,
            vec![window.clone(), window],
        );
        let primary = claude_cached_header_windows(&cache, None, "primary-token", now).unwrap();
        assert_eq!(
            primary.len(),
            1,
            "the extra's probe did not replace the primary's"
        );
        assert_eq!(
            claude_cached_header_windows(&cache, Some(r"C:\claude\work"), "extra-token", now)
                .unwrap()
                .len(),
            2
        );
        assert!(claude_cached_header_windows(
            &cache,
            Some(r"C:\claude\work"),
            "primary-token",
            now
        )
        .is_none());
        assert!(claude_cached_header_windows(
            &cache,
            Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
            "primary-token",
            now
        )
        .is_none());
    }

    #[test]
    fn purge_drops_only_removed_config_dirs() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scope = TestRefreshScope::new("claude", "purge-removed");
        let binding = ProviderCacheBinding::primary(
            scope
                .resolve_current("fixture", "purge", b"marker")
                .unwrap(),
        );
        let last_good = Mutex::new(ProviderLastGoodCache::default());
        let gates = Mutex::new(ClaudeUsageGates::new());
        let headers = Mutex::new(ClaudeHeaderCache::new());
        let profiles = Mutex::new(ClaudeProfileCache::new());
        let identities = Mutex::new(ClaudeIdentityCache::new());
        let (kept, removed) = (r"C:\claude\kept", r"C:\claude\removed");
        let accounts = [
            None,
            Some(kept),
            Some(removed),
            Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
        ];
        for account in accounts {
            apply_account_outcome_with(
                &last_good,
                "claude",
                account,
                "oauth",
                now,
                ProviderFetchOutcome::Success {
                    snapshot: cache_test_snapshot("claude", Ok(binding.primary.clone()), now),
                    cache_binding: Some(binding.clone()),
                },
                |_| {},
            );
            with_gate_in(&gates, account, |gate| {
                gate.record_rate_limit(binding.clone(), None, now)
            });
            store_claude_header_windows(&headers, account, "token", now, Vec::new());
            profiles.lock().unwrap().insert(
                (account.map(str::to_string), "binding".to_string()),
                (now, None),
            );
            identities.lock().unwrap().insert(
                (account.map(str::to_string), "binding".to_string()),
                (
                    binding.primary.clone(),
                    HistoryScope::for_test("purge-history"),
                ),
            );
        }
        purge_removed_claude_accounts_in(
            &ClaudeAccountCaches {
                last_good: &last_good,
                gates: &gates,
                headers: &headers,
                profiles: &profiles,
                identities: &identities,
            },
            &[kept.to_string()],
        );
        let expected = |present: Vec<Option<String>>| {
            let mut present = present;
            present.sort();
            present
        };
        let remaining = expected(vec![
            None,
            Some(kept.to_string()),
            Some(CLAUDE_DESKTOP_ACCOUNT_KEY.to_string()),
        ]);
        let mut last_good_keys: Vec<Option<String>> = lock_last_good(&last_good)
            .entries
            .keys()
            .map(|(_, key)| key.clone())
            .collect();
        last_good_keys.sort();
        assert_eq!(last_good_keys, remaining);
        assert_eq!(
            expected(gates.lock().unwrap().keys().cloned().collect()),
            remaining
        );
        assert_eq!(
            expected(headers.lock().unwrap().keys().cloned().collect()),
            remaining
        );
        assert_eq!(
            expected(
                identities
                    .lock()
                    .unwrap()
                    .keys()
                    .map(|(key, _)| key.clone())
                    .collect()
            ),
            remaining
        );
        assert_eq!(
            expected(
                profiles
                    .lock()
                    .unwrap()
                    .keys()
                    .map(|(key, _)| key.clone())
                    .collect()
            ),
            remaining
        );
        scope.cleanup();
    }

    #[test]
    fn merge_drops_only_a_later_card_with_the_same_profile() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scope = TestRefreshScope::new("claude", "merge-pass");
        let credential = |name: &str| {
            scope
                .resolve_current("fixture", name, name.as_bytes())
                .unwrap()
        };
        let profile = |evidence: &str| {
            scope
                .resolve_authoritative("claude", AuthoritativeIdKind::OpaqueId, evidence)
                .unwrap()
        };
        let team = profile("profile:team");
        let max = profile("profile:max");
        let run = |fetched: Vec<ClaudeAccountFetch>| {
            publish_claude_accounts_with(
                &Mutex::new(ProviderLastGoodCache::default()),
                now,
                fetched,
                |_| {},
            )
        };
        let dir = r"C:\claude\work";

        // Same account on CLI and Desktop: one card, the primary's.
        let merged = run(vec![
            claude_account_success(None, credential("cli"), Some(team.clone()), "p", now),
            claude_account_success(
                Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
                credential("desktop"),
                Some(team.clone()),
                "d",
                now,
            ),
        ]);
        assert_eq!(keys_of(&merged), [None]);

        // Different accounts: both cards, in input order.
        let both = run(vec![
            claude_account_success(None, credential("cli"), Some(team.clone()), "p", now),
            claude_account_success(Some(dir), credential("dir"), Some(max.clone()), "c", now),
            claude_account_success(
                Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
                credential("desktop"),
                Some(max.clone()),
                "d",
                now,
            ),
        ]);
        assert_eq!(
            keys_of(&both),
            [None, Some(dir)],
            "Desktop shares the config dir's account, and the config dir ranks first"
        );

        // Unknown identity on either side never merges.
        let unknown = run(vec![
            claude_account_success(None, credential("cli"), None, "p", now),
            claude_account_success(
                Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
                credential("desktop"),
                Some(team.clone()),
                "d",
                now,
            ),
        ]);
        assert_eq!(keys_of(&unknown), [None, Some(CLAUDE_DESKTOP_ACCOUNT_KEY)]);
        let failed_primary = run(vec![
            ClaudeAccountFetch {
                account_key: None,
                failure_source: "oauth",
                outcome: ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                    "Claude OAuth token expired or invalid. Run `claude` to re-authenticate.",
                )),
                remembered_scope: None,
            },
            claude_account_success(
                Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
                credential("desktop"),
                Some(team),
                "d",
                now,
            ),
        ]);
        assert_eq!(
            keys_of(&failed_primary),
            [None, Some(CLAUDE_DESKTOP_ACCOUNT_KEY)]
        );
        scope.cleanup();
    }

    /// Within one identity a success beats a failure; ties go to precedence.
    #[test]
    fn merge_keeps_a_successful_card_over_a_failed_one_of_the_same_account() {
        let now = Utc::now();
        let scope = TestRefreshScope::new("claude", "merge-success-wins");
        let credential = |name: &str| {
            scope
                .resolve_current("fixture", name, name.as_bytes())
                .unwrap()
        };
        let x = scope
            .resolve_authoritative("claude", AuthoritativeIdKind::OpaqueId, "profile:x")
            .unwrap();
        let dir_a = r"C:\claude\a";
        let dir_b = r"C:\claude\b";
        let expired = |key: &str, remembered: &AccountScope| ClaudeAccountFetch {
            account_key: Some(key.to_string()),
            failure_source: "oauth",
            outcome: ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                CLAUDE_CONFIG_DIR_EXPIRED_ERROR,
            )),
            remembered_scope: Some(remembered.clone()),
        };

        // Config dir A expired (remembered X) + Desktop succeeding as X:
        // Desktop kept, A dropped with no history and no last-good.
        let cache = Mutex::new(ProviderLastGoodCache::default());
        let recorded = std::cell::RefCell::new(Vec::<String>::new());
        let published = publish_claude_accounts_with(
            &cache,
            now,
            vec![
                claude_account_success(None, credential("cli"), None, "primary-history", now),
                expired(dir_a, &x),
                claude_account_success(
                    Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
                    credential("desktop"),
                    Some(x.clone()),
                    "desktop-history",
                    now,
                ),
            ],
            |snapshot| {
                enrich_snapshot_with(snapshot, now.timestamp(), |active, observations, _| {
                    recorded
                        .borrow_mut()
                        .extend(active.iter().map(|key| key.account_scope.clone()));
                    Ok(observations
                        .iter()
                        .map(|_| Ok((HistoryOutcome::LearningDuration, None, 0)))
                        .collect())
                })
            },
        );
        assert_eq!(
            keys_of(&published),
            [None, Some(CLAUDE_DESKTOP_ACCOUNT_KEY)]
        );
        assert!(published.iter().all(|card| card.error.is_none()));
        let mut slots: Vec<AccountSlot> = lock_last_good(&cache).entries.keys().cloned().collect();
        slots.sort();
        assert_eq!(
            slots,
            [
                account_slot("claude", None),
                account_slot("claude", Some(CLAUDE_DESKTOP_ACCOUNT_KEY))
            ]
        );
        assert!(!lock_last_good(&cache)
            .entries
            .contains_key(&account_slot("claude", Some(dir_a))));
        let mut scopes = recorded.into_inner();
        scopes.sort();
        scopes.dedup();
        assert_eq!(scopes, ["desktop-history", "primary-history"]);

        // Two failures of one account: the earliest by precedence stays.
        let failures = publish_claude_accounts_with(
            &Mutex::new(ProviderLastGoodCache::default()),
            now,
            vec![
                expired(dir_a, &x),
                expired(dir_b, &x),
                expired(CLAUDE_DESKTOP_ACCOUNT_KEY, &x),
            ],
            |_| {},
        );
        assert_eq!(keys_of(&failures), [Some(dir_a)]);

        // Two successes: the earliest (the primary) stays.
        let successes = publish_claude_accounts_with(
            &Mutex::new(ProviderLastGoodCache::default()),
            now,
            vec![
                expired(dir_a, &x),
                claude_account_success(None, credential("cli"), Some(x.clone()), "p", now),
                claude_account_success(Some(dir_b), credential("b"), Some(x.clone()), "b", now),
            ],
            |_| {},
        );
        assert_eq!(keys_of(&successes), [None]);
        scope.cleanup();
    }

    /// A directory removed from the registry while its fetch was in flight is
    /// not published, and nothing that fetch wrote survives.
    #[test]
    fn a_directory_removed_mid_run_is_dropped_and_its_state_purged() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scope = TestRefreshScope::new("claude", "removed-mid-run");
        let credential = |name: &str| {
            scope
                .resolve_current("fixture", name, name.as_bytes())
                .unwrap()
        };
        let (kept, removed) = (r"C:\claude\a", r"C:\claude\b");
        let last_good = Mutex::new(ProviderLastGoodCache::default());
        let gates = Mutex::new(ClaudeUsageGates::new());
        let headers = Mutex::new(ClaudeHeaderCache::new());
        let profiles = Mutex::new(ClaudeProfileCache::new());
        let identities = Mutex::new(ClaudeIdentityCache::new());
        let caches = ClaudeAccountCaches {
            last_good: &last_good,
            gates: &gates,
            headers: &headers,
            profiles: &profiles,
            identities: &identities,
        };
        let run = || {
            vec![
                claude_account_success(None, credential("cli"), None, "p", now),
                claude_account_success(Some(kept), credential("a"), None, "a", now),
                claude_account_success(Some(removed), credential("b"), None, "b", now),
            ]
        };
        // What both extra fetches wrote while in flight.
        for account in [Some(kept), Some(removed)] {
            with_gate_in(&gates, account, ClaudeUsageGate::clear);
            store_claude_header_windows(&headers, account, "token", now, Vec::new());
            profiles.lock().unwrap().insert(
                (account.map(str::to_string), "binding".to_string()),
                (now, None),
            );
            identities.lock().unwrap().insert(
                (account.map(str::to_string), "binding".to_string()),
                (credential("x"), HistoryScope::for_test("x")),
            );
        }

        // The registry did not move: every card is published.
        let unchanged = settle_claude_run_with(
            &caches,
            7,
            &[kept.to_string(), removed.to_string()],
            7,
            now,
            Err(AccountScopeError::NoTrustedEvidence),
            run(),
            |_| {},
        );
        assert_eq!(keys_of(&unchanged), [None, Some(kept), Some(removed)]);

        // B removed mid-run (generation 7 -> 8).
        let published = settle_claude_run_with(
            &caches,
            7,
            &[kept.to_string()],
            8,
            now,
            Err(AccountScopeError::NoTrustedEvidence),
            run(),
            |_| {},
        );
        // Every config-dir card of a run the registry moved under is dropped,
        // the still-registered A included; it refetches next poll.
        assert_eq!(keys_of(&published), [None]);
        let b = Some(removed.to_string());
        assert!(!lock_last_good(&last_good)
            .entries
            .contains_key(&account_slot("claude", Some(removed))));
        assert!(!gates.lock().unwrap().contains_key(&b));
        assert!(!headers.lock().unwrap().contains_key(&b));
        assert!(!profiles.lock().unwrap().keys().any(|(key, _)| key == &b));
        assert!(!identities.lock().unwrap().keys().any(|(key, _)| key == &b));
        let a = Some(kept.to_string());
        assert!(lock_last_good(&last_good)
            .entries
            .contains_key(&account_slot("claude", Some(kept))));
        assert!(headers.lock().unwrap().contains_key(&a));
        assert!(identities.lock().unwrap().keys().any(|(key, _)| key == &a));
        scope.cleanup();
    }

    /// The setter's registry replace and purge run as one step under the state
    /// lock: two setters in a row leave caches that match the final registry,
    /// and the lock is held while the registry is replaced.
    #[test]
    fn setter_replaces_and_purges_under_the_state_lock() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scope = TestRefreshScope::new("claude", "setter-state-lock");
        let binding = ProviderCacheBinding::primary(
            scope
                .resolve_current("fixture", "setter", b"marker")
                .unwrap(),
        );
        let state_lock = Mutex::new(());
        let last_good = Mutex::new(ProviderLastGoodCache::default());
        let gates = Mutex::new(ClaudeUsageGates::new());
        let headers = Mutex::new(ClaudeHeaderCache::new());
        let profiles = Mutex::new(ClaudeProfileCache::new());
        let identities = Mutex::new(ClaudeIdentityCache::new());
        let caches = ClaudeAccountCaches {
            last_good: &last_good,
            gates: &gates,
            headers: &headers,
            profiles: &profiles,
            identities: &identities,
        };
        let (a, b) = (r"C:\claude\a", r"C:\claude\b");
        for account in [Some(a), Some(b)] {
            apply_account_outcome_with(
                &last_good,
                "claude",
                account,
                "oauth",
                now,
                ProviderFetchOutcome::Success {
                    snapshot: cache_test_snapshot("claude", Ok(binding.primary.clone()), now),
                    cache_binding: Some(binding.clone()),
                },
                |_| {},
            );
            with_gate_in(&gates, account, ClaudeUsageGate::clear);
        }
        let setter = |dirs: Vec<&str>| {
            replace_claude_config_dirs_in(&state_lock, &caches, || {
                assert!(
                    state_lock.try_lock().is_err(),
                    "the registry is replaced under the state lock"
                );
                Ok::<_, ()>(((), dirs.into_iter().map(str::to_string).collect()))
            })
        };

        // Setter 1 keeps A and B; setter 2 removes B. Each purges against the
        // registry it installed, so the final caches match the final registry.
        setter(vec![a, b]).unwrap();
        setter(vec![a]).unwrap();
        assert!(state_lock.try_lock().is_ok(), "released afterwards");
        let keys: Vec<Option<String>> = lock_last_good(&last_good)
            .entries
            .keys()
            .map(|(_, key)| key.clone())
            .collect();
        assert_eq!(keys, [Some(a.to_string())]);
        let mut gate_keys: Vec<Option<String>> = gates.lock().unwrap().keys().cloned().collect();
        gate_keys.sort();
        assert_eq!(gate_keys, [Some(a.to_string())]);

        // A failed replace purges nothing.
        assert_eq!(
            replace_claude_config_dirs_in(&state_lock, &caches, || Err::<((), Vec<String>), _>(
                "invalidJson"
            )),
            Err("invalidJson")
        );
        assert_eq!(lock_last_good(&last_good).entries.len(), 1);
        scope.cleanup();
    }

    /// A directory removed and re-added under the same path mid-run may hold a
    /// different login: its stale fetch is neither published nor applied.
    #[test]
    fn a_directory_re_added_mid_run_does_not_publish_its_stale_fetch() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scope = TestRefreshScope::new("claude", "re-added-mid-run");
        let credential = |name: &str| {
            scope
                .resolve_current("fixture", name, name.as_bytes())
                .unwrap()
        };
        let dir_b = r"C:\claude\b";
        let last_good = Mutex::new(ProviderLastGoodCache::default());
        let gates = Mutex::new(ClaudeUsageGates::new());
        let headers = Mutex::new(ClaudeHeaderCache::new());
        let profiles = Mutex::new(ClaudeProfileCache::new());
        let identities = Mutex::new(ClaudeIdentityCache::new());
        let caches = ClaudeAccountCaches {
            last_good: &last_good,
            gates: &gates,
            headers: &headers,
            profiles: &profiles,
            identities: &identities,
        };
        let run = || {
            vec![
                claude_account_success(None, credential("cli"), None, "p", now),
                claude_account_success(Some(dir_b), credential("b-old"), None, "b", now),
                claude_account_success(
                    Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
                    credential("desktop"),
                    None,
                    "d",
                    now,
                ),
            ]
        };
        let settle = |current_generation| {
            settle_claude_run_with(
                &caches,
                3,
                &[dir_b.to_string()],
                current_generation,
                now,
                Err(AccountScopeError::NoTrustedEvidence),
                run(),
                |_| {},
            )
        };

        // Removed and re-added (generation 3 -> 5), B still registered.
        let published = settle(5);
        assert_eq!(
            keys_of(&published),
            [None, Some(CLAUDE_DESKTOP_ACCOUNT_KEY)]
        );
        assert!(!lock_last_good(&last_good)
            .entries
            .contains_key(&account_slot("claude", Some(dir_b))));

        // Unchanged generation publishes every card.
        let published = settle(3);
        assert_eq!(
            keys_of(&published),
            [None, Some(dir_b), Some(CLAUDE_DESKTOP_ACCOUNT_KEY)]
        );
        scope.cleanup();
    }

    /// The primary card names its constant history scope on every outcome so
    /// the C# window card can join its stored series; other cards' failures
    /// do not, and an error card records nothing.
    #[test]
    fn primary_card_always_carries_its_constant_history_scope() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let last_good = Mutex::new(ProviderLastGoodCache::default());
        let gates = Mutex::new(ClaudeUsageGates::new());
        let headers = Mutex::new(ClaudeHeaderCache::new());
        let profiles = Mutex::new(ClaudeProfileCache::new());
        let identities = Mutex::new(ClaudeIdentityCache::new());
        let caches = ClaudeAccountCaches {
            last_good: &last_good,
            gates: &gates,
            headers: &headers,
            profiles: &profiles,
            identities: &identities,
        };
        let dir = r"C:\claude\work";
        let failure = |key: Option<&str>, source: &'static str, failure| ClaudeAccountFetch {
            account_key: key.map(str::to_string),
            failure_source: source,
            outcome: ProviderFetchOutcome::Failure(failure),
            remembered_scope: None,
        };
        let observations = std::cell::Cell::new(0usize);
        let recorder_calls = std::cell::Cell::new(0usize);
        for primary in [
            failure(
                None,
                "unconfigured",
                ProviderFetchFailure::terminal(CLAUDE_UNCONFIGURED_ERROR),
            ),
            failure(
                None,
                "oauth",
                ProviderFetchFailure::terminal(
                    "Claude OAuth token expired or invalid. Run `claude` to re-authenticate.",
                ),
            ),
            failure(
                None,
                "oauth",
                ProviderFetchFailure::transient(
                    "Claude usage request failed. Retrying automatically.",
                    None,
                    timeout_diagnostic(),
                ),
            ),
        ] {
            let published = settle_claude_run_with(
                &caches,
                1,
                &[dir.to_string()],
                1,
                now,
                Ok(HistoryScope::for_test("claude-constant")),
                vec![
                    primary,
                    failure(
                        Some(dir),
                        "oauth",
                        ProviderFetchFailure::terminal(CLAUDE_CONFIG_DIR_EXPIRED_ERROR),
                    ),
                    failure(
                        Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
                        "oauth",
                        ProviderFetchFailure::terminal(CLAUDE_DESKTOP_EXPIRED_ERROR),
                    ),
                ],
                |snapshot| {
                    enrich_snapshot_with(snapshot, now.timestamp(), |_, recorded, _| {
                        recorder_calls.set(recorder_calls.get() + 1);
                        observations.set(observations.get() + recorded.len());
                        Ok(Vec::new())
                    })
                },
            );
            assert_eq!(
                keys_of(&published),
                [None, Some(dir), Some(CLAUDE_DESKTOP_ACCOUNT_KEY)]
            );
            let wire: Vec<Value> = published
                .iter()
                .map(|card| serde_json::to_value(card).unwrap())
                .collect();
            assert_eq!(
                wire[0]["historyScope"],
                serde_json::json!({"scope": "claude-constant"}),
                "{}",
                wire[0]
            );
            assert!(wire[0].get("accountKey").is_none());
            for extra in &wire[1..] {
                assert_eq!(
                    extra["historyScope"],
                    serde_json::json!({"error": "no trusted account evidence"}),
                    "{extra}"
                );
            }
        }
        assert_eq!(observations.get(), 0, "an error card records nothing");
        assert_eq!(recorder_calls.get(), 0);
        assert!(lock_last_good(&last_good).entries.is_empty());

        // The primary's last-good fallback keeps the scope it was recorded
        // under; a success is untouched.
        let scope = TestRefreshScope::new("claude", "primary-history-scope");
        let binding =
            ProviderCacheBinding::primary(scope.resolve_current("fixture", "cli", b"cli").unwrap());
        let mut snapshot = cache_test_snapshot("claude", Ok(binding.primary.clone()), now);
        snapshot.history_scope = Ok(HistoryScope::for_test("claude-constant"));
        let run = |outcome| {
            settle_claude_run_with(
                &caches,
                1,
                &[],
                1,
                now,
                Ok(HistoryScope::for_test("claude-constant")),
                vec![ClaudeAccountFetch {
                    account_key: None,
                    failure_source: "oauth",
                    outcome,
                    remembered_scope: None,
                }],
                |_| {},
            )
        };
        let success = run(ProviderFetchOutcome::Success {
            snapshot,
            cache_binding: Some(binding.clone()),
        });
        let fallback = run(ProviderFetchOutcome::Failure(
            ProviderFetchFailure::transient(
                "Claude usage request failed. Retrying automatically.",
                Some(binding),
                timeout_diagnostic(),
            ),
        ));
        for card in [&success[0], &fallback[0]] {
            assert_eq!(
                card.history_scope,
                Ok(HistoryScope::for_test("claude-constant"))
            );
        }
        assert_eq!(fallback[0].windows.len(), 1, "served from last-good");
        scope.cleanup();
    }

    /// Revision 2: the merge runs on raw results, before history recording and
    /// last-good. A merged-away Desktop card records nothing.
    #[test]
    fn merged_away_card_records_no_history_and_touches_no_last_good() {
        let now = Utc::now();
        let scope = TestRefreshScope::new("claude", "merge-before-enrich");
        let shared = scope
            .resolve_authoritative("claude", AuthoritativeIdKind::OpaqueId, "profile:shared")
            .unwrap();
        let cache = Mutex::new(ProviderLastGoodCache::default());
        let recorded = std::cell::RefCell::new(Vec::<String>::new());
        let fetched = vec![
            claude_account_success(
                None,
                scope.resolve_current("fixture", "cli", b"cli").unwrap(),
                Some(shared.clone()),
                "primary-history",
                now,
            ),
            claude_account_success(
                Some(CLAUDE_DESKTOP_ACCOUNT_KEY),
                scope
                    .resolve_current("fixture", "desktop", b"desktop")
                    .unwrap(),
                Some(shared),
                "profile:desktop-history",
                now,
            ),
        ];

        let published = publish_claude_accounts_with(&cache, now, fetched, |snapshot| {
            enrich_snapshot_with(snapshot, now.timestamp(), |active, observations, _| {
                recorded
                    .borrow_mut()
                    .extend(active.iter().map(|key| key.account_scope.clone()));
                Ok(observations
                    .iter()
                    .map(|_| Ok((HistoryOutcome::LearningDuration, None, 0)))
                    .collect())
            })
        });

        assert_eq!(published.len(), 1, "one Claude card");
        assert_eq!(published[0].account_key, None);
        let recorded = recorded.into_inner();
        assert!(!recorded.is_empty());
        assert!(recorded.iter().all(|scope| scope == "primary-history"));
        assert!(!recorded.iter().any(|scope| scope.starts_with("profile:")));
        let slots: Vec<AccountSlot> = lock_last_good(&cache).entries.keys().cloned().collect();
        assert_eq!(slots, [account_slot("claude", None)]);
        scope.cleanup();
    }

    #[tokio::test]
    async fn bounded_join_keeps_input_order_and_caps_in_flight() {
        let in_flight = std::cell::Cell::new(0usize);
        let peak = std::cell::Cell::new(0usize);
        let finished = std::cell::RefCell::new(Vec::new());
        let work: Vec<Pin<Box<dyn Future<Output = usize> + '_>>> = (0..7usize)
            .map(|index| {
                let (in_flight, peak, finished) = (&in_flight, &peak, &finished);
                Box::pin(async move {
                    in_flight.set(in_flight.get() + 1);
                    peak.set(peak.get().max(in_flight.get()));
                    // Earlier inputs take longer, so completion order is reversed.
                    for _ in 0..(10 - index) {
                        tokio::task::yield_now().await;
                    }
                    in_flight.set(in_flight.get() - 1);
                    finished.borrow_mut().push(index);
                    index
                }) as Pin<Box<dyn Future<Output = usize> + '_>>
            })
            .collect();

        let results = join_bounded_ordered(work, MAX_ACCOUNT_FETCHES_IN_FLIGHT).await;

        assert_eq!(results, (0..7).collect::<Vec<_>>());
        assert_eq!(peak.get(), MAX_ACCOUNT_FETCHES_IN_FLIGHT);
        assert_ne!(
            *finished.borrow(),
            (0..7).collect::<Vec<_>>(),
            "ran concurrently"
        );
        assert!(
            join_bounded_ordered(Vec::<Pin<Box<dyn Future<Output = ()>>>>::new(), 4)
                .await
                .is_empty()
        );
    }

    #[tokio::test]
    async fn expired_config_dir_login_never_refreshes() {
        // The generic refresh check reads the wall clock, so `now` must too.
        let now = Utc::now();
        let dir = r"C:\Users\me\.claude-work";
        let scope = TestRefreshScope::new("claude", "config-dir-expiry");
        let binding = ProviderCacheBinding::primary(
            scope
                .resolve_current(
                    CLAUDE_CONFIG_DIR_FILE_SOURCE,
                    "fixture-config-dir",
                    b"config-dir-refresh",
                )
                .unwrap(),
        );
        for expires_in in [-3_600, 30] {
            let refresh_calls = std::cell::Cell::new(0);
            let oauth_calls = std::cell::Cell::new(0);
            let (_, outcome) = fetch_claude_login_usage_with(
                claude_config_dir_credentials(
                    dir,
                    Some(now + chrono::Duration::seconds(expires_in)),
                ),
                binding.clone(),
                now,
                |_, _| None,
                |credentials| {
                    refresh_calls.set(refresh_calls.get() + 1);
                    let binding = binding.clone();
                    async move { Ok((credentials, binding.primary.clone(), Some(binding))) }
                },
                |_, _, _| async { claude_test_success_outcome() },
                |_, _, _, _| async {
                    oauth_calls.set(oauth_calls.get() + 1);
                    ("oauth", claude_test_success_outcome())
                },
            )
            .await;
            assert_eq!(refresh_calls.get(), 0, "{expires_in}");
            assert_eq!(oauth_calls.get(), 0, "{expires_in}");
            assert_eq!(
                terminal_display(&outcome),
                Some(CLAUDE_CONFIG_DIR_EXPIRED_ERROR),
                "{expires_in}"
            );
        }

        // The refresh path itself refuses before any request, and nothing is
        // written back to the directory's store.
        let request_calls = std::cell::Cell::new(0);
        let failure = refresh_claude_credentials_with(
            &claude_config_dir_credentials(dir, Some(now - chrono::Duration::hours(1))),
            &scope,
            reload_claude_credentials,
            |_, _| {
                request_calls.set(request_calls.get() + 1);
                async { Err(ProviderFetchFailure::terminal("request must not be called")) }
            },
            save_claude_credentials,
            |_| Ok(()),
        )
        .await
        .unwrap_err();
        assert!(matches!(
            failure,
            ProviderFetchFailure::Terminal { ref display } if display == CLAUDE_READ_ONLY_REFRESH_ERROR
        ));
        assert_eq!(request_calls.get(), 0);
        assert_eq!(
            save_claude_credentials(&claude_config_dir_credentials(dir, None)),
            Err(CLAUDE_READ_ONLY_REFRESH_ERROR.to_string())
        );
        assert!(ClaudeCredentialSource::ConfigDir(PathBuf::from(dir)).is_read_only());
        assert!(!ClaudeCredentialSource::File.is_read_only());
        scope.cleanup();
    }

    fn config_dir_fixture(tag: &str, contents: Option<&[u8]>) -> PathBuf {
        let dir =
            std::env::temp_dir().join(format!("tb-claude-config-dir-{tag}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        if let Some(contents) = contents {
            fs::write(dir.join(CLAUDE_CONFIG_DIR_CREDENTIALS_FILE), contents).unwrap();
        }
        dir
    }

    #[test]
    fn config_dir_login_reads_only_its_own_file() {
        const FIXTURE: &str = r#"{"claudeAiOauth":{"accessToken":"dir-access","refreshToken":"dir-refresh","scopes":["user:profile"]}}"#;
        let dir = config_dir_fixture("own-file", Some(FIXTURE.as_bytes()));
        let dir_text = dir.to_str().unwrap().to_string();

        // Primary-only credential sources set in the environment are ignored.
        let saved: Vec<(&str, Option<std::ffi::OsString>)> =
            ["TOKENBAR_CLAUDE_OAUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN"]
                .into_iter()
                .map(|name| (name, std::env::var_os(name)))
                .collect();
        for (name, _) in &saved {
            std::env::set_var(name, "env-token-must-not-be-used");
        }
        let loaded = load_claude_config_dir_credentials(&dir_text);
        for (name, value) in saved {
            match value {
                Some(value) => std::env::set_var(name, value),
                None => std::env::remove_var(name),
            }
        }

        let credentials = loaded.unwrap();
        assert_eq!(credentials.access_token, "dir-access");
        assert_eq!(
            credentials.source,
            ClaudeCredentialSource::ConfigDir(dir.clone())
        );
        assert_eq!(
            credentials.scope_slot.semantic_source,
            CLAUDE_CONFIG_DIR_FILE_SOURCE
        );
        assert_eq!(credentials.scope_marker(), Some(b"dir-refresh".as_slice()));

        // Its lineage slot is its own: resolving it leaves the primary's
        // binding exactly where it was.
        let scope = TestRefreshScope::new("claude", "config-dir-slot");
        let primary_before = scope
            .resolve_current("claude-login-file", "primary-location", b"primary-refresh")
            .unwrap();
        let extra = scope
            .resolve_current(
                credentials.scope_slot.semantic_source,
                &credentials.scope_slot.canonical_location,
                credentials.scope_marker().unwrap(),
            )
            .unwrap();
        let primary_after = scope
            .resolve_current("claude-login-file", "primary-location", b"primary-refresh")
            .unwrap();
        assert_eq!(primary_before, primary_after);
        assert_ne!(extra, primary_after);
        scope.cleanup();
        let _ = fs::remove_dir_all(&dir);
    }

    /// R5: a config-directory read that stalls (a hung network drive) turns
    /// into the transient retry failure after the timeout instead of holding
    /// the joined provider poll; a read that finishes in time is returned
    /// unchanged. Control: the same loader without a stall.
    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn a_stalled_config_dir_read_times_out_into_the_retry_failure() {
        let stalled = load_claude_config_dir_credentials_bounded(
            "C:\\stalled".to_string(),
            std::time::Duration::from_millis(50),
            |_| {
                std::thread::sleep(std::time::Duration::from_millis(500));
                Err(ProviderFetchFailure::terminal(CLAUDE_CONFIG_DIR_UNCONFIGURED_ERROR))
            },
        )
        .await;
        assert!(matches!(
            stalled,
            Err(ProviderFetchFailure::Transient { display, .. })
                if display == CLAUDE_CONFIG_DIR_READ_RETRY_ERROR
        ));

        let prompt = load_claude_config_dir_credentials_bounded(
            "C:\\prompt".to_string(),
            std::time::Duration::from_secs(5),
            |_| Err(ProviderFetchFailure::terminal(CLAUDE_CONFIG_DIR_UNCONFIGURED_ERROR)),
        )
        .await;
        assert!(matches!(
            prompt,
            Err(ProviderFetchFailure::Terminal { display })
                if display == CLAUDE_CONFIG_DIR_UNCONFIGURED_ERROR
        ));
    }

    /// While a timed-out read of a directory is still running, a later poll of
    /// the same directory (in any case or separator) starts no second read; a
    /// different directory is unaffected, and once the stalled read ends the
    /// directory is read again.
    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn a_stalled_config_dir_read_is_not_started_twice() {
        use std::sync::atomic::{AtomicBool, Ordering};
        use std::sync::Arc;
        let (release, released) = std::sync::mpsc::channel::<()>();
        let stalled = load_claude_config_dir_credentials_bounded(
            "C:\\dedup\\a".to_string(),
            std::time::Duration::from_millis(50),
            move |_| {
                let _ = released.recv();
                Err(ProviderFetchFailure::terminal(CLAUDE_CONFIG_DIR_UNCONFIGURED_ERROR))
            },
        )
        .await;
        assert!(matches!(stalled, Err(ProviderFetchFailure::Transient { .. })));

        let started = Arc::new(AtomicBool::new(false));
        let flag = started.clone();
        let second = load_claude_config_dir_credentials_bounded(
            "c:/DEDUP/A".to_string(),
            std::time::Duration::from_secs(5),
            move |_| {
                flag.store(true, Ordering::SeqCst);
                Err(ProviderFetchFailure::terminal(CLAUDE_CONFIG_DIR_UNCONFIGURED_ERROR))
            },
        )
        .await;
        assert!(matches!(
            second,
            Err(ProviderFetchFailure::Transient { display, .. })
                if display == CLAUDE_CONFIG_DIR_READ_RETRY_ERROR
        ));
        assert!(!started.load(Ordering::SeqCst), "a second read started while the first still ran");

        // Control: another directory is read normally.
        let other = load_claude_config_dir_credentials_bounded(
            "C:\\dedup\\b".to_string(),
            std::time::Duration::from_secs(5),
            |_| Err(ProviderFetchFailure::terminal(CLAUDE_CONFIG_DIR_UNCONFIGURED_ERROR)),
        )
        .await;
        assert!(matches!(other, Err(ProviderFetchFailure::Terminal { .. })));

        // Once the stalled read ends (its guard drops on the blocking thread
        // right after), the directory is read again.
        release.send(()).unwrap();
        let mut read_again = false;
        for _ in 0..200 {
            let result = load_claude_config_dir_credentials_bounded(
                "C:\\dedup\\a".to_string(),
                std::time::Duration::from_secs(5),
                |_| Err(ProviderFetchFailure::terminal(CLAUDE_CONFIG_DIR_UNCONFIGURED_ERROR)),
            )
            .await;
            if matches!(result, Err(ProviderFetchFailure::Terminal { .. })) {
                read_again = true;
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
        assert!(read_again, "the directory was never read again after the stall ended");
    }

    #[test]
    fn config_dir_login_failures_are_classified() {
        let kind = |result: Result<ClaudeCredentials, ProviderFetchFailure>| match result {
            Err(ProviderFetchFailure::Terminal { display }) => format!("terminal:{display}"),
            Err(ProviderFetchFailure::Transient { display, .. }) => format!("transient:{display}"),
            Ok(_) => "ok".to_string(),
        };
        let load = |tag: &str, contents: Option<&[u8]>| {
            let dir = config_dir_fixture(tag, contents);
            let result = load_claude_config_dir_credentials(dir.to_str().unwrap());
            let _ = fs::remove_dir_all(&dir);
            kind(result)
        };
        assert_eq!(
            load("missing", None),
            format!("terminal:{CLAUDE_CONFIG_DIR_UNCONFIGURED_ERROR}")
        );
        assert_eq!(
            load(
                "logged-out",
                Some(br#"{"claudeAiOauth":{"refreshToken":"stale"}}"#)
            ),
            format!("terminal:{CLAUDE_CONFIG_DIR_UNCONFIGURED_ERROR}")
        );
        assert_eq!(
            load("torn", Some(br#"{"claudeAiOauth":{"accessT"#)),
            format!("transient:{CLAUDE_CONFIG_DIR_READ_RETRY_ERROR}")
        );
        assert_eq!(
            load("no-token", Some(br#"{"claudeAiOauth":{}}"#)),
            format!("terminal:{CLAUDE_CONFIG_DIR_READ_ERROR}")
        );
        let oversized = format!(
            r#"{{"claudeAiOauth":{{"accessToken":"a"}},"pad":"{}"}}"#,
            "x".repeat(CLAUDE_CONFIG_DIR_CREDENTIALS_MAX_BYTES as usize)
        );
        assert_eq!(
            load("oversized", Some(oversized.as_bytes())),
            format!("terminal:{CLAUDE_CONFIG_DIR_READ_ERROR}")
        );
        assert_eq!(
            load("valid", Some(br#"{"claudeAiOauth":{"accessToken":"a"}}"#)),
            "ok"
        );
    }

    #[test]
    fn each_card_resolves_its_own_history_scope() {
        let dir = r"C:\Users\me\.claude-work";
        let calls = std::cell::RefCell::new(Vec::<Option<String>>::new());
        let resolve = |provider: &str, authoritative: Option<(AuthoritativeIdKind, &str)>| {
            assert_eq!(provider, "claude");
            calls
                .borrow_mut()
                .push(authoritative.map(|(kind, id)| format!("{kind:?}:{id}")));
            Ok(HistoryScope::for_test("resolved"))
        };

        // Primary: the per-installation constant.
        assert!(claude_account_history_scope_with(
            &ClaudeAccount::Primary { identify: true },
            &claude_test_login_credentials(),
            None,
            resolve,
        )
        .is_ok());
        // Config dir, credential from its own file: authoritative on the
        // exact path digest.
        assert!(claude_account_history_scope_with(
            &ClaudeAccount::ConfigDir(dir.to_string()),
            &claude_config_dir_credentials(dir, None),
            None,
            resolve,
        )
        .is_ok());
        assert_eq!(
            calls.borrow().clone(),
            [
                None,
                Some(format!(
                    "OpaqueId:config-dir:{}",
                    sha256_hex_exact(dir.as_bytes())
                ))
            ]
        );
        // Config dir with a credential from anywhere else: refused.
        assert_eq!(
            claude_account_history_scope_with(
                &ClaudeAccount::ConfigDir(dir.to_string()),
                &claude_test_login_credentials(),
                None,
                resolve,
            ),
            Err(AccountScopeError::NoTrustedEvidence)
        );
        assert_eq!(
            claude_account_history_scope_with(
                &ClaudeAccount::ConfigDir(dir.to_string()),
                &claude_config_dir_credentials(r"C:\Users\me\.claude-other", None),
                None,
                resolve,
            ),
            Err(AccountScopeError::NoTrustedEvidence)
        );
        // Desktop: the profile's history scope, or nothing.
        let scope = TestRefreshScope::new("claude", "desktop-history");
        let profile = ClaudeProfileIdentity {
            scopes: Some((
                scope
                    .resolve_authoritative("claude", AuthoritativeIdKind::OpaqueId, "profile:x")
                    .unwrap(),
                HistoryScope::for_test("desktop-profile-history"),
            )),
            plan: Some("Max".to_string()),
        };
        assert_eq!(
            claude_account_history_scope_with(
                &ClaudeAccount::Desktop,
                &claude_test_desktop_credentials(None),
                Some(&profile),
                resolve,
            ),
            Ok(HistoryScope::for_test("desktop-profile-history"))
        );
        for unknown in [
            None,
            Some(&ClaudeProfileIdentity {
                scopes: None,
                plan: Some("Max".to_string()),
            }),
        ] {
            assert_eq!(
                claude_account_history_scope_with(
                    &ClaudeAccount::Desktop,
                    &claude_test_desktop_credentials(None),
                    unknown,
                    resolve,
                ),
                Err(AccountScopeError::NoTrustedEvidence)
            );
        }
        assert_eq!(calls.borrow().len(), 2, "no other branch resolved anything");
        assert_ne!(
            sha256_hex_exact(dir.as_bytes()),
            sha256_hex_exact(format!("{dir} ").as_bytes()),
            "the digest is not trimmed"
        );
        scope.cleanup();
    }

    fn parse_profile(json: &str) -> ClaudeProfileResponse {
        serde_json::from_str(json).unwrap()
    }

    #[test]
    fn profile_identity_requires_both_uuids_and_compares_only_hmac_scopes() {
        let scope = TestRefreshScope::new("claude", "profile-identity");
        let identity = |json: &str| {
            claude_profile_identity_from(
                parse_profile(json),
                |provider, kind, id| scope.resolve_authoritative(provider, kind, id),
                |provider, authoritative| scope.resolve_history(provider, authoritative),
            )
        };
        const ACCOUNT: &str = "0f8e2b1c-3d4a-4b5c-8d6e-7f8091a2b3c4";
        const ORG: &str = "11111111-2222-4333-8444-555555555555";
        let full = |account: &str, org: &str| {
            format!(
                r#"{{"account":{{"uuid":"{account}","email_address":"x@example.invalid"}},
                    "organization":{{"uuid":"{org}","organization_type":"claude_max",
                    "rate_limit_tier":"default_claude_max_20x"}}}}"#
            )
        };

        let lower = identity(&full(ACCOUNT, ORG));
        let upper = identity(&full(&ACCOUNT.to_uppercase(), &ORG.to_uppercase()));
        assert_eq!(lower.plan.as_deref(), Some("Max 20x"));
        let (merge, history) = lower.scopes.clone().unwrap();
        assert_eq!(upper.scopes.as_ref().map(|(merge, _)| merge), Some(&merge));
        // Opaque: neither UUID appears in either scope.
        for text in [merge.as_str(), history.as_str()] {
            assert!(!text.contains(ACCOUNT) && !text.contains(ORG));
        }
        // A different org is a different account.
        let other_org = identity(&full(ACCOUNT, "11111111-2222-4333-8444-666666666666"));
        assert_ne!(other_org.scopes.unwrap().0, merge);
        // Tagged evidence: the same text as a config-dir input never collides.
        assert_ne!(
            scope
                .resolve_authoritative(
                    "claude",
                    AuthoritativeIdKind::OpaqueId,
                    &format!("config-dir:{ACCOUNT}\0{ORG}")
                )
                .unwrap(),
            merge
        );

        // Both UUIDs are required, and both must be well-formed.
        for json in [
            format!(r#"{{"account":{{"uuid":"{ACCOUNT}"}}}}"#),
            format!(r#"{{"organization":{{"uuid":"{ORG}"}}}}"#),
            full("not-a-uuid", ORG),
            full(&format!(" {ACCOUNT}"), ORG),
            full(ACCOUNT, &ORG.replace('-', "")),
            r#"{"account":{"uuid":7},"organization":null}"#.to_string(),
            "{}".to_string(),
        ] {
            assert!(identity(&json).scopes.is_none(), "{json}");
        }
        // A resolver failure is also "unknown", never a guess.
        let failed = claude_profile_identity_from(
            parse_profile(&full(ACCOUNT, ORG)),
            |_, _, _| Err(AccountScopeError::MetadataRead),
            |provider, authoritative| scope.resolve_history(provider, authoritative),
        );
        assert!(failed.scopes.is_none());
        assert_eq!(failed.plan.as_deref(), Some("Max 20x"));
        scope.cleanup();
    }

    #[tokio::test]
    async fn profile_cache_is_binding_keyed() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scope = TestRefreshScope::new("claude", "profile-cache");
        let known = ClaudeProfileIdentity {
            scopes: Some((
                scope
                    .resolve_authoritative("claude", AuthoritativeIdKind::OpaqueId, "profile:a")
                    .unwrap(),
                HistoryScope::for_test("history-a"),
            )),
            plan: Some("Team".to_string()),
        };
        let cache = Mutex::new(ClaudeProfileCache::new());
        let fetches = std::cell::Cell::new(0);
        let slot = |account: Option<&str>, binding: &str| {
            (account.map(str::to_string), binding.to_string())
        };
        let lookup = |slot, at, answer: Option<ClaudeProfileIdentity>, ttl| {
            let (cache, fetches) = (&cache, &fetches);
            async move {
                claude_profile_cached_with(cache, slot, at, || async {
                    fetches.set(fetches.get() + 1);
                    (answer, ttl)
                })
                .await
            }
        };

        let first = lookup(
            slot(None, "binding-a"),
            now,
            Some(known.clone()),
            CLAUDE_PROFILE_TTL_SECS,
        )
        .await;
        assert!(first.unwrap().scopes.is_some());
        // Same account, same binding: served from cache.
        let again = lookup(slot(None, "binding-a"), now, None, CLAUDE_PROFILE_TTL_SECS).await;
        assert!(again.unwrap().scopes.is_some());
        assert_eq!(fetches.get(), 1);
        // Same account, changed binding: the old identity is not used.
        let changed = lookup(
            slot(None, "binding-b"),
            now,
            None,
            CLAUDE_PROFILE_RETRY_SECS,
        )
        .await;
        assert!(changed.is_none());
        // Another account with the old binding string: not used either.
        let other = lookup(
            slot(Some(CLAUDE_DESKTOP_ACCOUNT_KEY), "binding-a"),
            now,
            None,
            1,
        )
        .await;
        assert!(other.is_none());
        assert_eq!(fetches.get(), 3);
        // A cached failure is served until it expires, then retried.
        let later = now + chrono::Duration::seconds(CLAUDE_PROFILE_RETRY_SECS - 1);
        assert!(
            lookup(slot(None, "binding-b"), later, Some(known.clone()), 1)
                .await
                .is_none()
        );
        assert_eq!(fetches.get(), 3);
        let expired = now + chrono::Duration::seconds(CLAUDE_PROFILE_RETRY_SECS);
        assert!(lookup(slot(None, "binding-b"), expired, Some(known), 1)
            .await
            .is_some());
        assert_eq!(fetches.get(), 4);
        scope.cleanup();
    }

    /// Poll 1 learns the identities; poll 2 finds Claude Desktop's token
    /// expired. Same binding: merged away with no card and no history.
    /// Changed binding: unknown, so the expired card shows.
    #[tokio::test]
    async fn remembered_identity_merges_an_expired_desktop_card_only_for_its_binding() {
        let now = Utc::now();
        let scope = TestRefreshScope::new("claude", "remembered-identity");
        let shared = scope
            .resolve_authoritative("claude", AuthoritativeIdKind::OpaqueId, "profile:shared")
            .unwrap();
        let answer = ClaudeProfileIdentity {
            scopes: Some((shared.clone(), HistoryScope::for_test("profile:shared"))),
            plan: Some("Team".to_string()),
        };
        let plans = Mutex::new(ClaudeProfileCache::new());
        let identities = Mutex::new(ClaudeIdentityCache::new());
        let cli_binding = scope.resolve_current("fixture", "cli", b"cli").unwrap();
        let desktop_binding = scope
            .resolve_current("fixture", "desktop", b"desktop")
            .unwrap();
        let desktop_slot =
            |binding: &AccountScope| claude_profile_slot(Some(CLAUDE_DESKTOP_ACCOUNT_KEY), binding);

        // Poll 1: both profile lookups succeed.
        for (account, binding) in [
            (None, &cli_binding),
            (Some(CLAUDE_DESKTOP_ACCOUNT_KEY), &desktop_binding),
        ] {
            let learned = claude_profile_identity_with(
                &plans,
                &identities,
                claude_profile_slot(account, binding),
                now,
                || async { (Some(answer.clone()), CLAUDE_PROFILE_TTL_SECS) },
            )
            .await;
            assert_eq!(learned.scopes.map(|(merge, _)| merge), Some(shared.clone()));
        }

        let expired_desktop = |remembered_scope: Option<AccountScope>| ClaudeAccountFetch {
            account_key: Some(CLAUDE_DESKTOP_ACCOUNT_KEY.to_string()),
            failure_source: "oauth",
            outcome: ProviderFetchOutcome::Failure(ProviderFetchFailure::terminal(
                CLAUDE_DESKTOP_EXPIRED_ERROR,
            )),
            remembered_scope,
        };
        let poll = |desktop: ClaudeAccountFetch, recorded: &std::cell::RefCell<Vec<String>>| {
            let cache = Mutex::new(ProviderLastGoodCache::default());
            let published = publish_claude_accounts_with(
                &cache,
                now,
                vec![
                    claude_account_success(
                        None,
                        cli_binding.clone(),
                        Some(shared.clone()),
                        "primary-history",
                        now,
                    ),
                    desktop,
                ],
                |snapshot| {
                    enrich_snapshot_with(snapshot, now.timestamp(), |active, observations, _| {
                        recorded
                            .borrow_mut()
                            .extend(active.iter().map(|key| key.account_scope.clone()));
                        Ok(observations
                            .iter()
                            .map(|_| Ok((HistoryOutcome::LearningDuration, None, 0)))
                            .collect())
                    })
                },
            );
            let slots: Vec<AccountSlot> = lock_last_good(&cache).entries.keys().cloned().collect();
            (published, slots)
        };

        // Poll 2, same Desktop binding, token expired: one card, nothing
        // recorded or cached for Desktop.
        let recorded = std::cell::RefCell::new(Vec::new());
        let remembered = remembered_claude_identity(&identities, &desktop_slot(&desktop_binding))
            .map(|(merge, _)| merge);
        let (published, slots) = poll(expired_desktop(remembered), &recorded);
        assert_eq!(keys_of(&published), [None]);
        assert!(published.iter().all(|card| card.error.is_none()));
        assert!(recorded
            .borrow()
            .iter()
            .all(|scope| scope == "primary-history"));
        assert_eq!(slots, [account_slot("claude", None)]);

        // Poll 2, Desktop re-logged-in (new binding) and expired: unknown, so
        // its expired message is shown.
        let rotated = scope
            .resolve_current("fixture", "desktop", b"desktop-rotated")
            .unwrap();
        let recorded = std::cell::RefCell::new(Vec::new());
        let remembered = remembered_claude_identity(&identities, &desktop_slot(&rotated))
            .map(|(merge, _)| merge);
        assert!(remembered.is_none());
        let (published, _) = poll(expired_desktop(remembered), &recorded);
        assert_eq!(
            keys_of(&published),
            [None, Some(CLAUDE_DESKTOP_ACCOUNT_KEY)]
        );
        assert_eq!(
            published[1].error.as_deref(),
            Some(CLAUDE_DESKTOP_EXPIRED_ERROR)
        );
        assert!(!recorded
            .borrow()
            .iter()
            .any(|scope| scope.starts_with("profile:")));

        // A different remembered identity also keeps the expired card.
        let other = scope
            .resolve_authoritative("claude", AuthoritativeIdKind::OpaqueId, "profile:other")
            .unwrap();
        let (published, _) = poll(expired_desktop(Some(other)), &std::cell::RefCell::default());
        assert_eq!(
            keys_of(&published),
            [None, Some(CLAUDE_DESKTOP_ACCOUNT_KEY)]
        );
        scope.cleanup();
    }

    #[tokio::test]
    async fn plan_label_expires_independently_of_the_remembered_identity() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scope = TestRefreshScope::new("claude", "plan-vs-identity");
        let merge = scope
            .resolve_authoritative("claude", AuthoritativeIdKind::OpaqueId, "profile:a")
            .unwrap();
        let answer = ClaudeProfileIdentity {
            scopes: Some((merge.clone(), HistoryScope::for_test("profile:a"))),
            plan: Some("Max".to_string()),
        };
        let plans = Mutex::new(ClaudeProfileCache::new());
        let identities = Mutex::new(ClaudeIdentityCache::new());
        let slot = claude_profile_slot(Some(CLAUDE_DESKTOP_ACCOUNT_KEY), &merge);
        let fetches = std::cell::Cell::new(0);
        let lookup = |at, answer: Option<ClaudeProfileIdentity>, ttl| {
            let (plans, identities, fetches, slot) = (&plans, &identities, &fetches, slot.clone());
            async move {
                claude_profile_identity_with(plans, identities, slot, at, || async {
                    fetches.set(fetches.get() + 1);
                    (answer, ttl)
                })
                .await
            }
        };

        let first = lookup(now, Some(answer.clone()), CLAUDE_PROFILE_TTL_SECS).await;
        assert_eq!(first.plan.as_deref(), Some("Max"));
        // After the plan TTL a failed lookup drops the plan, not the identity.
        let later = now + chrono::Duration::seconds(CLAUDE_PROFILE_TTL_SECS);
        let failed = lookup(later, None, CLAUDE_PROFILE_RETRY_SECS).await;
        assert_eq!(fetches.get(), 2);
        assert_eq!(failed.plan, None);
        assert_eq!(failed.scopes.map(|(scope, _)| scope), Some(merge.clone()));
        // A fresh answer that no longer proves an identity clears it.
        let after_retry = later + chrono::Duration::seconds(CLAUDE_PROFILE_RETRY_SECS);
        let unproven = ClaudeProfileIdentity {
            scopes: None,
            plan: Some("Max".to_string()),
        };
        let cleared = lookup(after_retry, Some(unproven), CLAUDE_PROFILE_TTL_SECS).await;
        assert!(cleared.scopes.is_none());
        assert_eq!(cleared.plan.as_deref(), Some("Max"));
        assert!(remembered_claude_identity(&identities, &slot).is_none());
        scope.cleanup();
    }

    #[test]
    fn profile_failures_are_negatively_cached_apart_from_the_usage_gate() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        assert_eq!(
            claude_profile_retry_secs(401, None, now),
            CLAUDE_PROFILE_TTL_SECS
        );
        assert_eq!(
            claude_profile_retry_secs(403, None, now),
            CLAUDE_PROFILE_TTL_SECS
        );
        assert_eq!(
            claude_profile_retry_secs(429, None, now),
            CLAUDE_PROFILE_RETRY_SECS
        );
        assert_eq!(
            claude_profile_retry_secs(429, Some(now + chrono::Duration::seconds(90)), now),
            90
        );
        assert_eq!(
            claude_profile_retry_secs(429, Some(now + chrono::Duration::days(30)), now),
            CLAUDE_PROFILE_TTL_SECS
        );
        assert_eq!(
            claude_profile_retry_secs(429, Some(now - chrono::Duration::seconds(5)), now),
            1
        );
        assert_eq!(
            claude_profile_retry_secs(503, None, now),
            CLAUDE_PROFILE_RETRY_SECS
        );
        assert_eq!(
            claude_profile_retry_secs(0, None, now),
            CLAUDE_PROFILE_RETRY_SECS
        );
        assert!(CLAUDE_PROFILE_TIMEOUT_SECS <= 5);
    }

    #[test]
    fn config_dir_rejections_name_the_config_directory() {
        let source = ClaudeCredentialSource::ConfigDir(PathBuf::from(r"C:\claude\work"));
        assert_eq!(
            claude_unauthorized_message(&source),
            CLAUDE_CONFIG_DIR_REJECTED_ERROR
        );
        assert_eq!(
            claude_denied_message(&source),
            CLAUDE_CONFIG_DIR_REJECTED_ERROR
        );
        assert_eq!(
            claude_header_rejection_message(&source, 401),
            CLAUDE_CONFIG_DIR_REJECTED_ERROR
        );
    }

    #[tokio::test]
    async fn claude_desktop_token_is_never_refreshed() {
        // The generic refresh check reads the wall clock, so `now` must too.
        let now = Utc::now();
        let scope = TestRefreshScope::new("claude", "desktop-expiry");
        let binding = ProviderCacheBinding::primary(
            scope
                .resolve_current("fixture-desktop", "fixture-desktop", b"desktop-refresh")
                .unwrap(),
        );
        for (expires_in, expired) in [(-3_600, true), (30, true), (120, false)] {
            let refresh_calls = std::cell::Cell::new(0);
            let oauth_calls = std::cell::Cell::new(0);
            let credentials =
                claude_test_desktop_credentials(Some(now + chrono::Duration::seconds(expires_in)));
            let (_, outcome) = fetch_claude_login_usage_with(
                credentials,
                binding.clone(),
                now,
                |_, _| None,
                |credentials| {
                    refresh_calls.set(refresh_calls.get() + 1);
                    let binding = binding.clone();
                    async move { Ok((credentials, binding.primary.clone(), Some(binding))) }
                },
                |_, _, _| async { claude_test_success_outcome() },
                |_, _, _, _| async {
                    oauth_calls.set(oauth_calls.get() + 1);
                    ("oauth", claude_test_success_outcome())
                },
            )
            .await;
            assert_eq!(refresh_calls.get(), 0, "{expires_in}");
            if expired {
                assert_eq!(
                    terminal_display(&outcome),
                    Some(CLAUDE_DESKTOP_EXPIRED_ERROR),
                    "{expires_in}"
                );
                assert_eq!(oauth_calls.get(), 0, "{expires_in}");
            } else {
                assert!(matches!(outcome, ProviderFetchOutcome::Success { .. }));
                assert_eq!(oauth_calls.get(), 1, "{expires_in}");
            }
        }

        // The refresh path itself refuses a Desktop credential before any
        // network request, and nothing is written back.
        let request_calls = std::cell::Cell::new(0);
        let failure = refresh_claude_credentials_with(
            &claude_test_desktop_credentials(Some(Utc::now() - chrono::Duration::hours(1))),
            &scope,
            reload_claude_credentials,
            |_, _| {
                request_calls.set(request_calls.get() + 1);
                async { Err(ProviderFetchFailure::terminal("request must not be called")) }
            },
            save_claude_credentials,
            |_| Ok(()),
        )
        .await
        .unwrap_err();
        assert!(matches!(
            failure,
            ProviderFetchFailure::Terminal { ref display } if display == CLAUDE_DESKTOP_REFRESH_ERROR
        ));
        assert_eq!(request_calls.get(), 0);
        assert_eq!(
            save_claude_credentials(&claude_test_desktop_credentials(None)),
            Err(CLAUDE_DESKTOP_REFRESH_ERROR.to_string())
        );
        scope.cleanup();
    }

    fn desktop_fixture_config_path() -> PathBuf {
        std::env::temp_dir()
            .join("tb-claude-desktop-fixture")
            .join("config.json")
    }

    fn parse_desktop(json: &str) -> Result<ClaudeCredentials, String> {
        parse_claude_desktop_token_cache(
            json.as_bytes(),
            &desktop_fixture_config_path(),
            CLAUDE_DESKTOP_TOKEN_CACHE_KEY,
        )
        .map(|credentials| credentials.expect("fixture is not an empty cache"))
    }

    #[test]
    fn claude_desktop_token_cache_parses_known_shapes() {
        let flat_camel = parse_desktop(
            r#"{"accessToken":"a1","refreshToken":"r1","expiresAt":1900000000000,
                "scopes":["user:profile","user:inference"]}"#,
        )
        .unwrap();
        assert_eq!(flat_camel.access_token, "a1");
        assert_eq!(flat_camel.refresh_token.as_deref(), Some("r1"));
        assert_eq!(
            flat_camel.expires_at,
            Utc.timestamp_opt(1_900_000_000, 0).single()
        );
        assert_eq!(flat_camel.scopes, ["user:profile", "user:inference"]);
        assert_eq!(flat_camel.source, ClaudeCredentialSource::Desktop);
        assert!(flat_camel.raw_root.is_none());
        assert_eq!(
            flat_camel.scope_slot.semantic_source,
            "claude-desktop-safestorage"
        );
        assert!(flat_camel
            .scope_slot
            .canonical_location
            .ends_with("config.json\0oauth:tokenCache"));
        assert_eq!(flat_camel.scope_marker(), Some(&b"r1"[..]));

        let flat_snake = parse_desktop(
            r#"{"access_token":"a2","expires_at":1900000000,"scope":"user:profile user:inference"}"#,
        )
        .unwrap();
        assert_eq!(flat_snake.access_token, "a2");
        assert_eq!(flat_snake.refresh_token, None);
        assert_eq!(
            flat_snake.expires_at,
            Utc.timestamp_opt(1_900_000_000, 0).single()
        );
        assert_eq!(flat_snake.scopes, ["user:profile", "user:inference"]);
        assert_eq!(flat_snake.scope_marker(), Some(&b"a2"[..]));

        let nested = parse_desktop(
            r#"{"client:user:profile":{"token":"a3","refreshToken":"r3",
                "expiry":"2030-01-01T00:00:00Z"}}"#,
        )
        .unwrap();
        assert_eq!(nested.access_token, "a3");
        assert_eq!(nested.expires_at, parse_datetime("2030-01-01T00:00:00Z"));

        let latest = parse_desktop(
            r#"{"x":{"accessToken":"older","expiresAt":1800000000000},
                "y":{"accessToken":"newest","expiresAt":1900000000000},
                "z":{"accessToken":"bare-without-metadata"}}"#,
        )
        .unwrap();
        assert_eq!(latest.access_token, "newest");

        // A token that can reach the usage endpoint beats a later-expiring
        // one without `user:profile`.
        let profile = parse_desktop(
            r#"{"x":{"accessToken":"profile","expiresAt":1800000000000,
                     "scopes":["user:profile","user:inference"]},
                "y":{"accessToken":"inference-only","expiresAt":1900000000000,
                     "scopes":["user:inference"]}}"#,
        )
        .unwrap();
        assert_eq!(profile.access_token, "profile");

        let deep = parse_desktop(r#"{"a":{"b":{"c":{"accessToken":"depth-3"}}}}"#).unwrap();
        assert_eq!(deep.access_token, "depth-3");
        assert!(parse_desktop(r#"{"a":{"b":{"c":{"d":{"accessToken":"depth-4"}}}}}"#).is_err());

        // A bare `token` key alone is not trusted as an access token.
        assert!(parse_desktop(r#"{"token":"generic"}"#).is_err());
        assert_eq!(
            parse_desktop("not json").unwrap_err(),
            CLAUDE_DESKTOP_READ_ERROR
        );
    }

    #[test]
    fn claude_desktop_unrecognized_format_names_keys_but_never_values() {
        let error = parse_desktop(
            r#"{"0b8f3c1e-5a4d-4c2b-9e7f-1a2b3c4d5e6f":{"note":"secret-value-123",
                "idToken":"secret-id-token"},"oauth:tokenCache":"secret-blob",
                "k1":"x","a_b":"y"}"#,
        )
        .unwrap_err();
        assert_eq!(
            error,
            "Claude Desktop login format is not recognized (cache: oauth:tokenCache, keys: a_b, idToken, note)."
        );
        // The cache key named is the literal key read, not decrypted content.
        for leaked in ["0b8f3c1e", "secret", "k1"] {
            assert!(!error.contains(leaked), "{leaked}");
        }

        let many: serde_json::Map<String, Value> = ('a'..='l')
            .map(|letter| (format!("key_{letter}"), Value::Bool(true)))
            .collect();
        let error = parse_desktop(&Value::Object(many).to_string()).unwrap_err();
        assert!(error.contains("key_j"));
        assert!(!error.contains("key_k"));

        assert_eq!(
            parse_desktop("[1]").unwrap_err(),
            "Claude Desktop login format is not recognized (cache: oauth:tokenCache, keys: none)."
        );
        // An empty object or array is no login, not an unrecognized format.
        for empty in ["{}", "[]", " { } "] {
            assert!(matches!(
                parse_claude_desktop_token_cache(
                    empty.as_bytes(),
                    &desktop_fixture_config_path(),
                    CLAUDE_DESKTOP_TOKEN_CACHE_KEY,
                ),
                Ok(None)
            ));
        }
    }

    /// Key selection without crypto: each cache value here is the plaintext
    /// JSON itself, and the "decrypt" step passes it through.
    #[test]
    fn claude_desktop_prefers_token_cache_v2_and_falls_back_to_v1() {
        let dir =
            std::env::temp_dir().join(format!("tb_claude_desktop_select_{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join("config.json");
        let valid = |token: &str| format!(r#"{{"accessToken":"{token}","refreshToken":"r"}}"#);
        let select = |config: Value| {
            fs::write(&path, config.to_string()).unwrap();
            let caches = read_claude_desktop_token_cache(&path).unwrap();
            select_claude_desktop_token_cache(caches, |cache_key, value| {
                Ok(parse_claude_desktop_token_cache(
                    value.as_bytes(),
                    &path,
                    cache_key,
                ))
            })
        };
        let token = |result: Result<Option<ClaudeCredentials>, ProviderFetchFailure>| {
            let credentials = result.unwrap().unwrap();
            let record = credentials
                .scope_slot
                .canonical_location
                .rsplit('\0')
                .next()
                .unwrap()
                .to_string();
            (credentials.access_token, record)
        };

        // V2 present: V2 used, and the slot names the key actually read.
        assert_eq!(
            token(select(serde_json::json!({
                "oauth:tokenCache": valid("v1-token"),
                "oauth:tokenCacheV2": valid("v2-token"),
            }))),
            ("v2-token".to_string(), "oauth:tokenCacheV2".to_string())
        );
        // V2 absent: V1 used.
        assert_eq!(
            token(select(
                serde_json::json!({ "oauth:tokenCache": valid("v1-token") })
            )),
            ("v1-token".to_string(), "oauth:tokenCache".to_string())
        );
        // Desktop 2.16120.0: V1 holds `{}`, V2 holds the login.
        assert_eq!(
            token(select(serde_json::json!({
                "oauth:tokenCache": "{}",
                "oauth:tokenCacheV2": valid("v2-token"),
            }))),
            ("v2-token".to_string(), "oauth:tokenCacheV2".to_string())
        );
        // V2 left empty (`{}`), V1 still valid.
        assert_eq!(
            token(select(serde_json::json!({
                "oauth:tokenCache": valid("v1-token"),
                "oauth:tokenCacheV2": "{}",
            }))),
            ("v1-token".to_string(), "oauth:tokenCache".to_string())
        );
        // Signed out of Desktop 2.16120.0: both caches hold `{}`. No login,
        // so no Desktop card — not a format error.
        assert!(matches!(
            select(serde_json::json!({
                "oauth:tokenCache": "{}",
                "oauth:tokenCacheV2": "{}",
            })),
            Ok(None)
        ));
        assert!(matches!(
            select(serde_json::json!({ "oauth:tokenCacheV2": "{}" })),
            Ok(None)
        ));
        // A non-empty cache without a token keeps the named format error,
        // even after an empty one.
        let unrecognized = |cache: &str| {
            (
                false,
                format!(
                    "Claude Desktop login format is not recognized (cache: {cache}, keys: unknown)."
                ),
            )
        };
        assert_eq!(
            select(serde_json::json!({ "oauth:tokenCacheV2": r#"{"unknown":1}"# }))
                .map_err(desktop_failure_kind)
                .unwrap_err(),
            unrecognized("oauth:tokenCacheV2")
        );
        assert_eq!(
            select(serde_json::json!({
                "oauth:tokenCache": r#"{"unknown":1}"#,
                "oauth:tokenCacheV2": "{}",
            }))
            .map_err(desktop_failure_kind)
            .unwrap_err(),
            unrecognized("oauth:tokenCache")
        );
        // A decrypt failure stops instead of trying the other key.
        let calls = std::cell::Cell::new(0);
        let failure = select_claude_desktop_token_cache(
            vec![
                (CLAUDE_DESKTOP_TOKEN_CACHE_KEY_V2, "x".to_string()),
                (CLAUDE_DESKTOP_TOKEN_CACHE_KEY, "y".to_string()),
            ],
            |_, _| {
                calls.set(calls.get() + 1);
                Err(ProviderFetchFailure::terminal(CLAUDE_DESKTOP_READ_ERROR))
            },
        );
        assert!(failure.is_err());
        assert_eq!(calls.get(), 1);
        fs::remove_dir_all(&dir).unwrap();
    }

    /// A signed-out Desktop (every cache `{}`) is Absent: the run plans the
    /// primary alone, which asks for no profile, so a single-account payload
    /// stays byte-identical.
    #[test]
    fn signed_out_claude_desktop_adds_no_card_and_no_profile_request() {
        let caches = vec![
            (CLAUDE_DESKTOP_TOKEN_CACHE_KEY_V2, "{}".to_string()),
            (CLAUDE_DESKTOP_TOKEN_CACHE_KEY, "{}".to_string()),
        ];
        let desktop = select_claude_desktop_token_cache(caches, |cache_key, value| {
            Ok(parse_claude_desktop_token_cache(
                value.as_bytes(),
                &desktop_fixture_config_path(),
                cache_key,
            ))
        });
        assert!(matches!(desktop, Ok(None)));
        let requests = claude_account_requests(Vec::new(), desktop);
        assert_eq!(requests.len(), 1);
        assert!(matches!(
            requests[0],
            ClaudeAccountRequest::Primary { identify: false }
        ));
        assert!(!ClaudeAccount::Primary { identify: false }.wants_profile());
    }

    /// `(transient, display)` so desktop load results can be compared.
    fn desktop_failure_kind(failure: ProviderFetchFailure) -> (bool, String) {
        match failure {
            ProviderFetchFailure::Transient { display, .. } => (true, display),
            ProviderFetchFailure::Terminal { display } => (false, display),
        }
    }

    #[test]
    fn claude_desktop_header_probe_rejection_names_claude_desktop() {
        for status in [401, 403] {
            assert_eq!(
                claude_header_rejection_message(&ClaudeCredentialSource::Desktop, status),
                "Claude Desktop login was rejected. Sign in to Claude Desktop again."
            );
            assert_eq!(
                claude_header_rejection_message(&ClaudeCredentialSource::Environment, status),
                "Claude setup-token expired or lacks access."
            );
        }
        assert_eq!(
            claude_header_rejection_message(&ClaudeCredentialSource::Desktop, 418),
            "Claude header probe rejected the request (status 418)."
        );
    }

    #[test]
    fn claude_desktop_config_without_a_token_cache_is_absent() {
        let dir =
            std::env::temp_dir().join(format!("tb_claude_desktop_config_{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join("config.json");
        let read =
            |path: &Path| read_claude_desktop_token_cache(path).map_err(desktop_failure_kind);
        assert_eq!(read(&path), Ok(Vec::new()));
        let retry = (true, CLAUDE_DESKTOP_READ_RETRY_ERROR.to_string());
        let v1 = |value: &str| vec![(CLAUDE_DESKTOP_TOKEN_CACHE_KEY, value.to_string())];
        let v2 = |value: &str| vec![(CLAUDE_DESKTOP_TOKEN_CACHE_KEY_V2, value.to_string())];
        for (raw, expected) in [
            ("{}", Ok(Vec::new())),
            (r#"{"oauth:tokenCache":null}"#, Ok(Vec::new())),
            (r#"{"oauth:tokenCache":"  "}"#, Ok(Vec::new())),
            (r#"{"oauth:tokenCache":" djEw "}"#, Ok(v1("djEw"))),
            (r#"{"oauth:tokenCacheV2":"djEwV2"}"#, Ok(v2("djEwV2"))),
            // V2 is tried first; V1 stays available as the fallback.
            (
                r#"{"oauth:tokenCache":"djEwV1","oauth:tokenCacheV2":"djEwV2"}"#,
                Ok(vec![
                    (CLAUDE_DESKTOP_TOKEN_CACHE_KEY_V2, "djEwV2".to_string()),
                    (CLAUDE_DESKTOP_TOKEN_CACHE_KEY, "djEwV1".to_string()),
                ]),
            ),
            (
                r#"{"oauth:tokenCache":"djEwV1","oauth:tokenCacheV2":""}"#,
                Ok(v1("djEwV1")),
            ),
            (
                r#"{"oauth:tokenCache":"djEwV1","oauth:tokenCacheV2":7}"#,
                Ok(v1("djEwV1")),
            ),
            (
                r#"{"oauth:tokenCache":5}"#,
                Err((false, CLAUDE_DESKTOP_READ_ERROR.to_string())),
            ),
            // A half-written file (Claude Desktop rewriting it) is retried.
            (r#"{"oauth:tokenCa"#, Err(retry.clone())),
        ] {
            fs::write(&path, raw).unwrap();
            assert_eq!(read(&path), expected, "{raw}");
        }

        // An I/O error other than NotFound is retried too.
        fs::remove_file(&path).unwrap();
        fs::create_dir(&path).unwrap();
        assert_eq!(read(&path), Err(retry));
        fs::remove_dir_all(&dir).unwrap();
    }

    /// End to end on synthetic files: DPAPI-wrapped key in `Local State`, a
    /// v10 value in `config.json`, parsed into Desktop credentials.
    #[cfg(target_os = "windows")]
    #[test]
    fn crypto_claude_desktop_loader_reads_synthetic_files() {
        use crate::win_safe_storage::test_support::{local_state_for, v10_value};

        let dir =
            std::env::temp_dir().join(format!("tb_claude_desktop_loader_{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        let config = dir.join("config.json");
        let local_state = dir.join("Local State");
        let key = [0x21u8; 32];
        let plaintext =
            br#"{"entry":{"accessToken":"synthetic-access","refreshToken":"synthetic-refresh","expiresAt":1900000000000}}"#;
        fs::write(&local_state, local_state_for(&key).to_string()).unwrap();
        fs::write(
            &config,
            serde_json::json!({ "oauth:tokenCache": v10_value(&key, &[7u8; 12], plaintext) })
                .to_string(),
        )
        .unwrap();

        let credentials = load_claude_desktop_credentials_from(&config, &local_state)
            .unwrap()
            .unwrap();
        assert_eq!(credentials.access_token, "synthetic-access");
        assert_eq!(
            credentials.refresh_token.as_deref(),
            Some("synthetic-refresh")
        );
        assert_eq!(credentials.source, ClaudeCredentialSource::Desktop);

        // Desktop 2.16120.0 shape: an encrypted `{}` under `oauth:tokenCache`
        // and the login under `oauth:tokenCacheV2`.
        let v2_plaintext = br#"{"entry":{"accessToken":"synthetic-v2-access","refreshToken":"synthetic-v2-refresh"}}"#;
        fs::write(
            &config,
            serde_json::json!({
                "oauth:tokenCache": v10_value(&key, &[8u8; 12], b"{}"),
                "oauth:tokenCacheV2": v10_value(&key, &[9u8; 12], v2_plaintext),
            })
            .to_string(),
        )
        .unwrap();
        let credentials = load_claude_desktop_credentials_from(&config, &local_state)
            .unwrap()
            .unwrap();
        assert_eq!(credentials.access_token, "synthetic-v2-access");
        assert!(credentials
            .scope_slot
            .canonical_location
            .ends_with("\0oauth:tokenCacheV2"));

        // Signed out: Desktop leaves both keys holding an encrypted `{}`.
        fs::write(
            &config,
            serde_json::json!({
                "oauth:tokenCache": v10_value(&key, &[10u8; 12], b"{}"),
                "oauth:tokenCacheV2": v10_value(&key, &[11u8; 12], b"{}"),
            })
            .to_string(),
        )
        .unwrap();
        assert!(matches!(
            load_claude_desktop_credentials_from(&config, &local_state),
            Ok(None)
        ));
        // Restore a login for the failure cases below.
        fs::write(
            &config,
            serde_json::json!({ "oauth:tokenCacheV2": v10_value(&key, &[9u8; 12], v2_plaintext) })
                .to_string(),
        )
        .unwrap();

        let load = || {
            load_claude_desktop_credentials_from(&config, &local_state)
                .map(|credentials| credentials.is_some())
                .map_err(desktop_failure_kind)
        };
        let terminal = Err((false, CLAUDE_DESKTOP_READ_ERROR.to_string()));

        // A value sealed under another key is a named terminal failure.
        fs::write(&local_state, local_state_for(&[0x22u8; 32]).to_string()).unwrap();
        assert_eq!(load(), terminal);
        // A half-written Local State is retried.
        fs::write(&local_state, r#"{"os_crypt":"#).unwrap();
        assert_eq!(
            load(),
            Err((true, CLAUDE_DESKTOP_READ_RETRY_ERROR.to_string()))
        );
        fs::remove_file(&local_state).unwrap();
        assert_eq!(load(), terminal);
        fs::write(&config, "{}").unwrap();
        assert!(matches!(
            load_claude_desktop_credentials_from(&config, &local_state),
            Ok(None)
        ));
        fs::remove_dir_all(&dir).unwrap();
    }

    fn timeout_diagnostic() -> SafeTransportDiagnostic {
        SafeTransportDiagnostic::from_facts(TransportErrorFacts::synthetic(
            true,
            false,
            TransportPhase::Request,
            None,
        ))
    }

    #[test]
    fn kiro_success_is_cached_and_survives_a_same_binding_transient() {
        // Ported from macOS. A Kiro success has a non-empty window, so
        // `usable_success("kiro")` admits it to the last-good cache; a later
        // same-binding transient failure then replays that card instead of
        // returning a bare error. Drop "kiro" from `usable_success` and the
        // fallback below carries no window.
        let scope = TestRefreshScope::new("kiro", "kiro-last-good");
        let account_scope = scope
            .resolve_current("fixture", "account-a", b"marker-a")
            .unwrap();
        let binding = ProviderCacheBinding::primary(account_scope.clone());
        let cache = Mutex::new(ProviderLastGoodCache::default());
        let fresh_at = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let failure_at = fresh_at + chrono::Duration::minutes(1);

        apply_provider_outcome_with(
            &cache,
            "kiro",
            "oauth",
            fresh_at,
            ProviderFetchOutcome::Success {
                snapshot: cache_test_snapshot("kiro", Ok(account_scope), fresh_at),
                cache_binding: Some(binding.clone()),
            },
            |_| {},
        )
        .unwrap();

        let fallback = apply_provider_outcome_with(
            &cache,
            "kiro",
            "oauth",
            failure_at,
            ProviderFetchOutcome::Failure(ProviderFetchFailure::transient(
                "Kiro usage request failed. Retrying automatically.",
                Some(binding.clone()),
                timeout_diagnostic(),
            )),
            |_| {},
        )
        .unwrap();
        assert_eq!(
            fallback.windows.len(),
            1,
            "a kiro transient must replay the cached window"
        );
        assert!(fallback.error.is_some());
        assert!(matches!(
            fallback.account_scope,
            Err(AccountScopeError::NoTrustedEvidence)
        ));
        scope.cleanup();
    }

    #[test]
    fn opencode_go_success_is_cached_and_survives_a_same_binding_transient() {
        // Ported from macOS. Regression for the OpenCode Go provider:
        // `usable_success` must admit client_id "opencode" so a successful Go
        // fetch enters the last-good cache and a later transient failure keeps
        // the last-good card. Dropping "opencode" from `usable_success` turns
        // this red: the success is never cached, so the transient failure
        // returns a bare error instead.
        let scope = TestRefreshScope::new("opencode", "opencode-last-good");
        let account_scope = scope
            .resolve_current("fixture", "account-go", b"marker-go")
            .unwrap();
        let binding = ProviderCacheBinding::primary(account_scope.clone());
        let cache = Mutex::new(ProviderLastGoodCache::default());
        let fresh_at = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let failure_at = fresh_at + chrono::Duration::minutes(1);

        let fresh = apply_provider_outcome_with(
            &cache,
            "opencode",
            "api",
            fresh_at,
            ProviderFetchOutcome::Success {
                snapshot: cache_test_snapshot("opencode", Ok(account_scope), fresh_at),
                cache_binding: Some(binding.clone()),
            },
            |_| {},
        )
        .unwrap();
        assert!(fresh.error.is_none());
        assert_eq!(fresh.windows.len(), 1);
        // The success reached the cache under the opencode slot.
        assert!(lock_last_good(&cache)
            .entries
            .contains_key(&account_slot("opencode", None)));

        let fallback = apply_provider_outcome_with(
            &cache,
            "opencode",
            "api",
            failure_at,
            ProviderFetchOutcome::Failure(ProviderFetchFailure::transient(
                "OpenCode Go usage request failed. Retrying automatically.",
                Some(binding.clone()),
                timeout_diagnostic(),
            )),
            |_| {},
        )
        .unwrap();
        // The last-good window is preserved; the current error rides on top.
        assert_eq!(fallback.updated_at, fresh.updated_at);
        assert_eq!(fallback.windows.len(), 1);
        assert_eq!(fallback.windows[0].label_for_test(), "Session");
        assert!(fallback.error.is_some());
        scope.cleanup();
    }

    #[test]
    fn copilot_malformed_optional_reset_remains_success_and_keeps_last_good() {
        let scope = TestRefreshScope::new("copilot", "lossy-optional-reset");
        let account_scope = scope
            .resolve_current("fixture", "account-a", b"marker-a")
            .unwrap();
        let binding = ProviderCacheBinding::primary(account_scope.clone());
        let cache = Mutex::new(ProviderLastGoodCache::default());
        let fresh_at = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();

        apply_provider_outcome_with(
            &cache,
            "copilot",
            "oauth",
            fresh_at,
            ProviderFetchOutcome::Success {
                snapshot: cache_test_snapshot("copilot", Ok(account_scope.clone()), fresh_at),
                cache_binding: Some(binding.clone()),
            },
            |_| {},
        )
        .unwrap();

        let response_at = fresh_at + chrono::Duration::minutes(1);
        let decoded = agent_copilot::decode_usage_response(
            r#"{
                "quota_reset_date": {"credential":"token-secret"},
                "quota_snapshots": {
                    "premium_interactions": {
                        "entitlement": 100,
                        "remaining": 60,
                        "percent_remaining": 60
                    }
                }
            }"#,
            response_at,
        );
        let outcome = match decoded {
            Ok((plan, windows)) => ProviderFetchOutcome::Success {
                snapshot: AgentUsageSnapshot {
                    account_key: None,
                    merge_scope: None,
                    client_id: "copilot".to_string(),
                    source: "oauth".to_string(),
                    updated_at: response_at.to_rfc3339_opts(SecondsFormat::Millis, true),
                    identity: Some(AgentIdentity { email: None, plan }),
                    history_scope: Ok(HistoryScope::for_test(account_scope.as_str())),
                    account_scope: Ok(account_scope),
                    windows,
                    credits: None,
                    error: None,
                    transport_diagnostic: None,
                },
                cache_binding: Some(binding),
            },
            Err(failure) => ProviderFetchOutcome::Failure(failure),
        };
        let snapshot =
            apply_provider_outcome_with(&cache, "copilot", "oauth", response_at, outcome, |_| {})
                .unwrap();

        assert!(snapshot.error.is_none());
        assert_eq!(snapshot.windows.len(), 1);
        assert!((snapshot.windows[0].remaining_percent - 60.0).abs() < 0.01);
        assert!(snapshot.windows[0].resets_at.is_none());
        let cached = lock_last_good(&cache).entries[&account_slot("copilot", None)]
            .snapshot
            .clone();
        assert_eq!(cached.updated_at, snapshot.updated_at);
        assert_eq!(cached.windows.len(), 1);
        assert!(cached.error.is_none());
        assert!(cached.transport_diagnostic.is_none());
        scope.cleanup();
    }

    #[test]
    fn last_good_same_binding_fallback_preserves_clean_snapshot_without_enrichment() {
        let scope = TestRefreshScope::new("codex", "last-good-same-binding");
        let account_scope = scope
            .resolve_current("fixture", "account-a", b"marker-a")
            .unwrap();
        let binding = ProviderCacheBinding::primary(account_scope.clone());
        let cache = Mutex::new(ProviderLastGoodCache::default());
        let fresh_at = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let failure_at = fresh_at + chrono::Duration::minutes(1);
        let enrich_calls = std::cell::Cell::new(0);

        let fresh = apply_provider_outcome_with(
            &cache,
            "codex",
            "oauth",
            fresh_at,
            ProviderFetchOutcome::Success {
                snapshot: cache_test_snapshot("codex", Ok(account_scope), fresh_at),
                cache_binding: Some(binding.clone()),
            },
            |snapshot| {
                enrich_calls.set(enrich_calls.get() + 1);
                snapshot.windows[0].pace_status = PaceStatusPayload {
                    state: PaceState::Available,
                    window_key: Some("main.session.v1".to_string()),
                    duration_seconds: Some(300 * 60),
                    duration_source: Some(DurationSource::Contract),
                    complete_cycles: 6,
                    reason: None,
                };
                snapshot.windows[0].historical_pace = Some(HistoricalPacePayload {
                    expected_used_percent: 35.0,
                    eta_seconds: Some(1_800.0),
                    will_last_to_reset: false,
                    run_out_probability: Some(0.42),
                });
            },
        )
        .unwrap();
        assert_eq!(enrich_calls.get(), 1);

        let fallback = apply_provider_outcome_with(
            &cache,
            "codex",
            "oauth",
            failure_at,
            ProviderFetchOutcome::Failure(ProviderFetchFailure::transient(
                "Codex usage request failed. Retrying automatically.",
                Some(binding.clone()),
                timeout_diagnostic(),
            )),
            |_| enrich_calls.set(enrich_calls.get() + 1),
        )
        .unwrap();
        assert_eq!(enrich_calls.get(), 1);
        assert_eq!(fallback.updated_at, fresh.updated_at);
        assert_eq!(fallback.source, fresh.source);
        assert_eq!(
            fallback.identity.as_ref().unwrap().plan.as_deref(),
            Some("Fixture")
        );
        assert_eq!(fallback.windows.len(), 1);
        assert_eq!(fallback.windows[0].pace_status.complete_cycles, 6);
        assert_eq!(
            fallback.windows[0]
                .historical_pace
                .as_ref()
                .map(|pace| pace.expected_used_percent),
            Some(35.0)
        );
        assert_eq!(
            fallback
                .credits
                .as_ref()
                .and_then(|credits| credits.remaining),
            Some(-2.5)
        );
        assert!(matches!(
            fallback.account_scope,
            Err(AccountScopeError::NoTrustedEvidence)
        ));
        assert!(fallback.error.is_some());
        assert_eq!(
            fallback
                .transport_diagnostic
                .map(|diagnostic| diagnostic.category),
            Some(TransportCategory::Timeout)
        );

        let cached = lock_last_good(&cache)
            .entries
            .get(&account_slot("codex", None))
            .unwrap()
            .snapshot
            .clone();
        assert!(cached.error.is_none());
        assert!(cached.transport_diagnostic.is_none());
        drop(cached);

        let fallback_again = apply_provider_outcome_with(
            &cache,
            "codex",
            "oauth",
            failure_at + chrono::Duration::minutes(1),
            ProviderFetchOutcome::Failure(ProviderFetchFailure::transient(
                "Codex usage request failed. Retrying automatically.",
                Some(binding.clone()),
                SafeTransportDiagnostic::server_error(503),
            )),
            |_| enrich_calls.set(enrich_calls.get() + 1),
        )
        .unwrap();
        assert_eq!(enrich_calls.get(), 1);
        assert_eq!(fallback_again.updated_at, fresh.updated_at);
        assert_eq!(fallback_again.windows[0].pace_status.complete_cycles, 6);

        let dns_fallback = apply_provider_outcome_with(
            &cache,
            "codex",
            "oauth",
            failure_at + chrono::Duration::minutes(2),
            ProviderFetchOutcome::Failure(ProviderFetchFailure::transient(
                "Codex usage request failed. Retrying automatically.",
                Some(binding),
                SafeTransportDiagnostic::from_facts(TransportErrorFacts {
                    is_timeout: false,
                    is_connect: true,
                    is_dns: true,
                    is_tls: false,
                    phase: TransportPhase::Request,
                    raw_os_code: None,
                }),
            )),
            |_| enrich_calls.set(enrich_calls.get() + 1),
        )
        .unwrap();
        assert_eq!(enrich_calls.get(), 1);
        assert_eq!(dns_fallback.updated_at, fresh.updated_at);
        assert_eq!(dns_fallback.windows[0].pace_status.complete_cycles, 6);
        assert_eq!(
            dns_fallback
                .transport_diagnostic
                .map(|diagnostic| diagnostic.category),
            Some(TransportCategory::Dns)
        );
        scope.cleanup();
    }

    #[test]
    fn last_good_mismatch_unbound_terminal_and_absent_clear_cache() {
        let scope = TestRefreshScope::new("codex", "last-good-clear");
        let scope_a = scope
            .resolve_current("fixture", "account-a", b"marker-a")
            .unwrap();
        let scope_b = scope
            .resolve_current("fixture", "account-b", b"marker-b")
            .unwrap();
        let binding_a = ProviderCacheBinding::primary(scope_a.clone());
        let binding_b = ProviderCacheBinding::primary(scope_b);
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();

        for failure in [
            ProviderFetchFailure::transient("mismatch", Some(binding_b), timeout_diagnostic()),
            ProviderFetchFailure::transient("unbound", None, timeout_diagnostic()),
            ProviderFetchFailure::terminal("terminal"),
        ] {
            let cache = Mutex::new(ProviderLastGoodCache::default());
            apply_provider_outcome_with(
                &cache,
                "codex",
                "oauth",
                now,
                ProviderFetchOutcome::Success {
                    snapshot: cache_test_snapshot("codex", Ok(scope_a.clone()), now),
                    cache_binding: Some(binding_a.clone()),
                },
                |_| {},
            );
            let result = apply_provider_outcome_with(
                &cache,
                "codex",
                "oauth",
                now + chrono::Duration::seconds(1),
                ProviderFetchOutcome::Failure(failure),
                |_| panic!("failure must not enrich"),
            )
            .unwrap();
            assert!(result.windows.is_empty());
            assert!(!lock_last_good(&cache)
                .entries
                .contains_key(&account_slot("codex", None)));
        }

        let cache = Mutex::new(ProviderLastGoodCache::default());
        apply_provider_outcome_with(
            &cache,
            "codex",
            "oauth",
            now,
            ProviderFetchOutcome::Success {
                snapshot: cache_test_snapshot("codex", Ok(scope_a), now),
                cache_binding: Some(binding_a),
            },
            |_| {},
        );
        assert!(apply_provider_outcome_with(
            &cache,
            "codex",
            "oauth",
            now,
            ProviderFetchOutcome::Absent,
            |_| panic!("absent must not enrich"),
        )
        .is_none());
        assert!(!lock_last_good(&cache)
            .entries
            .contains_key(&account_slot("codex", None)));
        scope.cleanup();
    }

    #[test]
    fn uncacheable_or_invalid_success_clears_prior_last_good() {
        let scope = TestRefreshScope::new("antigravity", "last-good-uncacheable");
        let account_scope = scope
            .resolve_current("fixture", "account-a", b"marker-a")
            .unwrap();
        let binding = ProviderCacheBinding::primary(account_scope.clone());
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();

        let cache = Mutex::new(ProviderLastGoodCache::default());
        apply_provider_outcome_with(
            &cache,
            "antigravity",
            "oauth",
            now,
            ProviderFetchOutcome::Success {
                snapshot: cache_test_snapshot("antigravity", Ok(account_scope.clone()), now),
                cache_binding: Some(binding.clone()),
            },
            |_| {},
        );
        let anonymous = apply_provider_outcome_with(
            &cache,
            "antigravity",
            "local",
            now,
            ProviderFetchOutcome::Success {
                snapshot: cache_test_snapshot(
                    "antigravity",
                    Err(AccountScopeError::NoTrustedEvidence),
                    now,
                ),
                cache_binding: None,
            },
            |_| {},
        )
        .unwrap();
        assert_eq!(anonymous.windows.len(), 1);
        assert!(!lock_last_good(&cache)
            .entries
            .contains_key(&account_slot("antigravity", None)));

        apply_provider_outcome_with(
            &cache,
            "antigravity",
            "oauth",
            now,
            ProviderFetchOutcome::Success {
                snapshot: cache_test_snapshot("antigravity", Ok(account_scope.clone()), now),
                cache_binding: Some(binding.clone()),
            },
            |_| {},
        );
        let mut empty = cache_test_snapshot("antigravity", Ok(account_scope.clone()), now);
        empty.windows.clear();
        let live_empty = apply_provider_outcome_with(
            &cache,
            "antigravity",
            "oauth",
            now,
            ProviderFetchOutcome::Success {
                snapshot: empty,
                cache_binding: Some(binding.clone()),
            },
            |_| {},
        )
        .unwrap();
        assert!(live_empty.windows.is_empty());
        assert!(!lock_last_good(&cache)
            .entries
            .contains_key(&account_slot("antigravity", None)));

        apply_provider_outcome_with(
            &cache,
            "antigravity",
            "oauth",
            now,
            ProviderFetchOutcome::Success {
                snapshot: cache_test_snapshot("antigravity", Ok(account_scope), now),
                cache_binding: Some(binding),
            },
            |_| {},
        );
        let enrich_calls = std::cell::Cell::new(0);
        let invalid = apply_provider_outcome_with(
            &cache,
            "antigravity",
            "oauth",
            now,
            ProviderFetchOutcome::Success {
                snapshot: cache_test_snapshot(
                    "antigravity",
                    Err(AccountScopeError::MetadataRead),
                    now,
                ),
                cache_binding: None,
            },
            |_| enrich_calls.set(enrich_calls.get() + 1),
        )
        .unwrap();
        assert!(invalid.windows.is_empty());
        assert_eq!(enrich_calls.get(), 0);
        assert!(!lock_last_good(&cache)
            .entries
            .contains_key(&account_slot("antigravity", None)));
        scope.cleanup();
    }

    #[tokio::test]
    async fn status_before_body_enforces_terminal_transient_and_claude_exception() {
        use std::cell::Cell;

        for status in [401, 403, 418, 429, 500, 503] {
            let reads = Cell::new(0);
            let result = read_response_body(status, false, || async {
                reads.set(reads.get() + 1);
                Ok("sensitive body".to_string())
            })
            .await;
            assert_eq!(reads.get(), 0, "status {status} must not read body");
            match status {
                429 | 500 | 503 => {
                    assert!(matches!(result, Err(ResponseReadFailure::Transient(_))))
                }
                _ => assert_eq!(result, Err(ResponseReadFailure::Terminal(status))),
            }
        }

        let reads = Cell::new(0);
        let body = read_response_body(200, false, || async {
            reads.set(reads.get() + 1);
            Ok("success".to_string())
        })
        .await
        .unwrap();
        assert_eq!(body, "success");
        assert_eq!(reads.get(), 1);

        let reads = Cell::new(0);
        let failure = read_response_body(200, false, || async {
            reads.set(reads.get() + 1);
            Err(TransportErrorFacts::synthetic(
                false,
                false,
                TransportPhase::ResponseBody,
                Some(54),
            ))
        })
        .await
        .unwrap_err();
        assert_eq!(reads.get(), 1);
        assert!(matches!(
            failure,
            ResponseReadFailure::Transient(SafeTransportDiagnostic {
                category: TransportCategory::ConnectionReset,
                os_code: Some(54),
                ..
            })
        ));

        let reads = Cell::new(0);
        let body = read_response_body(403, true, || async {
            reads.set(reads.get() + 1);
            Ok("missing user:profile".to_string())
        })
        .await
        .unwrap();
        assert_eq!(body, "missing user:profile");
        assert_eq!(reads.get(), 1);

        let reads = Cell::new(0);
        let failure = read_response_body(403, true, || async {
            reads.set(reads.get() + 1);
            Err(TransportErrorFacts::synthetic(
                true,
                false,
                TransportPhase::ResponseBody,
                None,
            ))
        })
        .await
        .unwrap_err();
        assert_eq!(reads.get(), 1);
        assert_eq!(failure, ResponseReadFailure::Terminal(403));
    }

    #[tokio::test]
    async fn verified_binding_failure_prevents_every_provider_request() {
        for provider in ["codex", "claude", "grok", "copilot", "antigravity"] {
            let sends = std::cell::Cell::new(0);
            let result: Result<(), &str> =
                request_after_verified_binding(Err::<(), _>("scope unavailable"), |()| async {
                    sends.set(sends.get() + 1);
                    Ok(())
                })
                .await;
            assert_eq!(result, Err("scope unavailable"), "{provider}");
            assert_eq!(sends.get(), 0, "{provider}");
        }
    }

    #[derive(Debug)]
    struct SensitiveTestError(&'static str);

    impl std::fmt::Display for SensitiveTestError {
        fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            formatter.write_str(self.0)
        }
    }

    impl std::error::Error for SensitiveTestError {}

    #[derive(Debug)]
    struct NestedTestError {
        source: Box<dyn std::error::Error + Send + Sync>,
    }

    impl NestedTestError {
        fn new(source: impl std::error::Error + Send + Sync + 'static) -> Self {
            Self {
                source: Box::new(source),
            }
        }
    }

    impl std::fmt::Display for NestedTestError {
        fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            formatter.write_str("nested transport failure")
        }
    }

    impl std::error::Error for NestedTestError {
        fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
            Some(self.source.as_ref())
        }
    }

    #[derive(Debug, Clone, Copy)]
    struct InjectedDnsFailureResolver;

    impl reqwest::dns::Resolve for InjectedDnsFailureResolver {
        fn resolve(&self, _name: reqwest::dns::Name) -> reqwest::dns::Resolving {
            Box::pin(async {
                Err(Box::new(DnsResolutionError::new(SensitiveTestError(
                    "token-secret user@example.invalid /private/credential/path",
                )))
                    as Box<dyn std::error::Error + Send + Sync>)
            })
        }
    }

    #[tokio::test]
    async fn typed_gai_adapter_preserves_loopback_addresses_without_network_io() {
        let name: reqwest::dns::Name = "127.0.0.1".parse().unwrap();
        let addresses = reqwest::dns::Resolve::resolve(&TypedGaiResolver, name)
            .await
            .unwrap()
            .collect::<Vec<_>>();
        assert!(!addresses.is_empty());
        assert!(addresses.iter().all(|address| address.ip().is_loopback()));
        assert!(provider_http_client_builder().build().is_ok());
    }

    #[tokio::test]
    async fn injected_typed_dns_failure_is_classified_without_source_disclosure() {
        let client = reqwest::Client::builder()
            .no_proxy()
            .dns_resolver(InjectedDnsFailureResolver)
            .timeout(std::time::Duration::from_secs(2))
            .build()
            .unwrap();
        let error = client
            .get("http://account-123.example.invalid/private/path?token=token-secret")
            .send()
            .await
            .unwrap_err();
        let diagnostic = SafeTransportDiagnostic::from_facts(TransportErrorFacts::from_reqwest(
            &error,
            TransportPhase::Request,
        ));
        assert_eq!(diagnostic.category, TransportCategory::Dns);
        let wire = serde_json::to_string(&diagnostic).unwrap();
        assert_eq!(wire, r#"{"category":"dns"}"#);
        for secret in [
            "token-secret",
            "user@example.invalid",
            "account-123",
            "example.invalid",
            "/private/path",
            "/private/credential/path",
        ] {
            assert!(!wire.contains(secret));
        }
    }

    #[test]
    fn nested_typed_sources_are_found_without_text_classification() {
        let dns_error = NestedTestError::new(std::io::Error::other(DnsResolutionError::new(
            SensitiveTestError("token-secret"),
        )));
        let dns_facts = transport_source_facts(&dns_error);
        assert!(dns_facts.is_dns);
        assert!(!dns_facts.is_tls);
        assert_eq!(dns_facts.raw_os_code, None);

        let tls_error = NestedTestError::new(std::io::Error::other(rustls::Error::General(
            "token-secret".to_string(),
        )));
        let tls_facts = transport_source_facts(&tls_error);
        assert!(!tls_facts.is_dns);
        assert!(tls_facts.is_tls);
        assert_eq!(tls_facts.raw_os_code, None);

        let os_error =
            NestedTestError::new(std::io::Error::other(std::io::Error::from_raw_os_error(61)));
        assert_eq!(transport_source_facts(&os_error).raw_os_code, Some(61));
    }

    #[tokio::test]
    async fn loopback_plaintext_on_tls_endpoint_is_classified_as_tls() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};

        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(async move {
            let (mut stream, _) = listener.accept().await.unwrap();
            let mut client_hello = [0_u8; 1024];
            let _ = stream.read(&mut client_hello).await;
            let _ = stream
                .write_all(b"HTTP/1.1 200 OK\r\ncontent-length: 0\r\n\r\n")
                .await;
            tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        });
        let client = provider_http_client_builder()
            .no_proxy()
            .timeout(std::time::Duration::from_secs(3))
            .build()
            .unwrap();
        let error = tokio::time::timeout(
            std::time::Duration::from_secs(5),
            client.get(format!("https://{address}/")).send(),
        )
        .await
        .expect("loopback TLS request timed out")
        .unwrap_err();
        let facts = TransportErrorFacts::from_reqwest(&error, TransportPhase::Request);
        assert!(facts.is_tls);
        assert_eq!(
            SafeTransportDiagnostic::from_facts(facts).category,
            TransportCategory::Tls
        );
        server.await.unwrap();
    }

    #[test]
    fn transport_diagnostic_precedence_and_generic_categories_are_stable() {
        let facts =
            |is_timeout, is_connect, is_dns, is_tls, phase, raw_os_code| TransportErrorFacts {
                is_timeout,
                is_connect,
                is_dns,
                is_tls,
                phase,
                raw_os_code,
            };
        let category = |facts| SafeTransportDiagnostic::from_facts(facts).category;

        assert_eq!(
            category(facts(
                true,
                true,
                true,
                true,
                TransportPhase::Request,
                Some(61),
            )),
            TransportCategory::Timeout
        );
        assert_eq!(
            category(facts(
                false,
                true,
                true,
                true,
                TransportPhase::Request,
                Some(61),
            )),
            TransportCategory::ConnectionRefused
        );
        assert_eq!(
            category(facts(
                false,
                true,
                true,
                true,
                TransportPhase::Request,
                Some(54),
            )),
            TransportCategory::ConnectionReset
        );
        assert_eq!(
            category(facts(
                false,
                true,
                true,
                true,
                TransportPhase::Request,
                None,
            )),
            TransportCategory::Dns
        );
        assert_eq!(
            category(facts(
                false,
                true,
                false,
                true,
                TransportPhase::Request,
                None,
            )),
            TransportCategory::Tls
        );
        assert_eq!(
            category(facts(
                false,
                true,
                false,
                false,
                TransportPhase::Request,
                None,
            )),
            TransportCategory::Connect
        );
        assert_eq!(
            category(facts(
                false,
                false,
                false,
                false,
                TransportPhase::Request,
                None,
            )),
            TransportCategory::Request
        );
        assert_eq!(
            category(facts(
                false,
                false,
                false,
                false,
                TransportPhase::ResponseBody,
                None,
            )),
            TransportCategory::ResponseBody
        );
    }

    #[test]
    fn structured_transport_diagnostic_serializes_only_allowlisted_fields() {
        let diagnostic = SafeTransportDiagnostic::from_facts(TransportErrorFacts::synthetic(
            false,
            true,
            TransportPhase::Request,
            Some(61),
        ));
        let wire = serde_json::to_string(&diagnostic).unwrap();
        assert_eq!(wire, r#"{"category":"connectionRefused","osCode":61}"#);
        for secret in [
            "token-secret",
            "Authorization",
            "https://example.invalid/path?query=secret#fragment",
            "user@example.invalid",
            "account-123",
            "/private/credential/path",
        ] {
            assert!(!wire.contains(secret));
        }
        assert_eq!(
            serde_json::to_value(SafeTransportDiagnostic::rate_limited(429)).unwrap(),
            serde_json::json!({ "category": "rateLimited", "status": 429 })
        );
        assert_eq!(
            serde_json::to_value(SafeTransportDiagnostic::server_error(503)).unwrap(),
            serde_json::json!({ "category": "serverError", "status": 503 })
        );
    }

    #[test]
    fn codex_credit_is_usable_only_when_balance_is_finite() {
        let credits = |balance, unlimited| CodexCredits { balance, unlimited };

        assert_eq!(
            finite_codex_balance(Some(&credits(Some(12.5), false))),
            Some(12.5)
        );
        assert_eq!(
            finite_codex_balance(Some(&credits(Some(-2.5), false))),
            Some(-2.5),
            "finite negative balances preserve the existing present-credit semantics"
        );
        assert_eq!(
            finite_codex_balance(Some(&credits(Some(0.0), false))),
            Some(0.0)
        );
        assert_eq!(
            finite_codex_balance(Some(&credits(Some(f64::NAN), false))),
            None
        );
        assert_eq!(
            finite_codex_balance(Some(&credits(Some(f64::INFINITY), false))),
            None
        );
        assert_eq!(
            finite_codex_balance(Some(&credits(None, true))),
            None,
            "unlimited without a balance is not usable credit"
        );
        assert_eq!(finite_codex_balance(None), None);
    }

    #[test]
    fn maps_codex_primary_and_secondary_windows() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let rate_limit = CodexRateLimit {
            primary_window: Some(CodexWindow {
                used_percent: 8.0,
                reset_at: 1_700_005_400,
                limit_window_seconds: 18_000,
            }),
            secondary_window: Some(CodexWindow {
                used_percent: 35.0,
                reset_at: 1_700_172_800,
                limit_window_seconds: 604_800,
            }),
        };
        let windows = codex_windows(Some(&rate_limit), None, now);
        assert_eq!(windows.len(), 2);
        assert_eq!(windows[0].label, "Session");
        assert_eq!(windows[0].remaining_percent, 92.0);
        assert_eq!(windows[0].window_minutes, Some(300));
        assert_eq!(windows[1].label, "Weekly");
        assert_eq!(windows[1].remaining_percent, 65.0);
        assert_eq!(windows[1].window_minutes, Some(10_080));
    }

    #[test]
    fn stage0_freezes_codex_duration_roles_and_unknown_window_baseline() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let reversed = CodexRateLimit {
            primary_window: Some(CodexWindow {
                used_percent: 35.0,
                reset_at: 1_700_172_800,
                limit_window_seconds: 604_800,
            }),
            secondary_window: Some(CodexWindow {
                used_percent: 8.0,
                reset_at: 1_700_005_400,
                limit_window_seconds: 18_000,
            }),
        };
        let windows = codex_windows(Some(&reversed), None, now);
        assert_eq!(windows.len(), 2);
        assert_eq!(windows[0].label, "Session", "codex.main.18000.session");
        assert_eq!(windows[0].card_id, "main.session.v1");
        assert_eq!(windows[0].window_key.as_deref(), Some("main.session.v1"));
        assert_eq!(windows[0].window_minutes, Some(300));
        assert_eq!(windows[1].label, "Weekly", "codex.main.604800.weekly");
        assert_eq!(windows[1].card_id, "main.weekly.v1");
        assert_eq!(windows[1].window_key.as_deref(), Some("main.weekly.v1"));
        assert_eq!(windows[1].window_minutes, Some(10_080));

        let unknown_rate_limit = CodexRateLimit {
            primary_window: Some(CodexWindow {
                used_percent: 10.0,
                reset_at: now.timestamp() + 3_600,
                limit_window_seconds: 3_600,
            }),
            secondary_window: None,
        };
        let unknown = codex_windows(Some(&unknown_rate_limit), None, now);
        assert_eq!(unknown.len(), 1);
        let unknown = &unknown[0];
        assert_eq!(unknown.card_id, "row.main.primary.v1");
        assert_eq!(unknown.window_key, None);
        assert_eq!(unknown.window_minutes, None);
        assert_eq!(unknown.pace_status.state, PaceState::Unavailable);
        assert_eq!(
            unknown.pace_status.reason.as_deref(),
            Some("windowIdentity")
        );
        let wire = serde_json::to_value(unknown).unwrap();
        assert_eq!(wire["cardId"], "row.main.primary.v1");
        assert!(wire["paceStatus"].get("windowKey").is_none());
        assert_eq!(wire["paceStatus"]["state"], "unavailable");
        assert_eq!(wire["paceStatus"]["reason"], "windowIdentity");
    }

    #[test]
    fn serializes_nested_historical_pace_without_legacy_scalars() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let mut window = UsageWindow::from_used_percent(
            "Weekly".to_string(),
            60.0,
            Some(now + chrono::Duration::hours(12)),
            now,
            Some(10_080),
        )
        .with_identity(
            "weekly.v1",
            Some("weekly.v1".to_string()),
            None,
            Some(DurationEvidence::contract(10_080 * 60)),
        );
        window.pace_status.state = PaceState::Available;
        window.historical_pace = Some(HistoricalPacePayload {
            expected_used_percent: 55.0,
            eta_seconds: Some(3_600.0),
            will_last_to_reset: false,
            run_out_probability: Some(0.8),
        });

        let value = serde_json::to_value(&window).unwrap();
        assert!(value.get("historicalPace").is_some());
        assert!(value.get("historicalExpectedPercent").is_none());
        assert!(value.get("runOutProbability").is_none());
        let historical = value.get("historicalPace").unwrap();
        assert_eq!(historical["expectedUsedPercent"], 55.0);
        assert_eq!(historical["etaSeconds"], 3_600.0);
        assert_eq!(historical["willLastToReset"], false);
        assert_eq!(historical["runOutProbability"], 0.8);
    }

    #[test]
    fn stage1_credential_markers_follow_the_frozen_provider_routes() {
        let slot = CredentialSlot {
            semantic_source: "fixture",
            canonical_location: "fixture".to_string(),
        };
        let codex = CodexCredentials {
            access_token: "codex-access".to_string(),
            refresh_token: Some("codex-refresh".to_string()),
            id_token: None,
            account_id: None,
            last_refresh: None,
            auth_path: PathBuf::new(),
            raw_json: Value::Null,
            scope_slot: slot.clone(),
        };
        assert_eq!(codex.scope_marker(), b"codex-refresh");
        let mut codex_access_only = codex.clone();
        codex_access_only.refresh_token = None;
        assert_eq!(codex_access_only.scope_marker(), b"codex-access");

        let claude_login = ClaudeCredentials {
            access_token: "claude-access".to_string(),
            refresh_token: Some("claude-refresh".to_string()),
            expires_at: None,
            scopes: Vec::new(),
            rate_limit_tier: None,
            subscription_type: None,
            source: ClaudeCredentialSource::File,
            raw_root: None,
            keychain_account: None,
            scope_slot: slot.clone(),
        };
        assert_eq!(
            claude_login.scope_marker(),
            Some(b"claude-refresh".as_slice())
        );
        let mut login_without_refresh = claude_login.clone();
        login_without_refresh.refresh_token = None;
        assert_eq!(login_without_refresh.scope_marker(), None);

        let claude_setup = ClaudeCredentials {
            source: ClaudeCredentialSource::Environment,
            scope_slot: slot,
            ..login_without_refresh
        };
        assert_eq!(
            claude_setup.scope_marker(),
            Some(b"claude-access".as_slice())
        );
    }

    #[test]
    fn provider_cache_binding_requires_structural_exact_match() {
        let scope_store = TestRefreshScope::new("codex", "binding-exact-match");
        let primary_a = scope_store
            .resolve_current("fixture", "primary-a", b"primary-a")
            .unwrap();
        let primary_b = scope_store
            .resolve_current("fixture", "primary-b", b"primary-b")
            .unwrap();
        let corroborating_a = scope_store
            .resolve_current("fixture", "corroborating-a", b"corroborating-a")
            .unwrap();
        let corroborating_b = scope_store
            .resolve_current("fixture", "corroborating-b", b"corroborating-b")
            .unwrap();

        let full = ProviderCacheBinding::new(primary_a.clone(), Some(corroborating_a.clone()));
        assert_eq!(full, full.clone());
        assert_ne!(
            full,
            ProviderCacheBinding::new(primary_b, Some(corroborating_a.clone()))
        );
        assert_ne!(
            full,
            ProviderCacheBinding::new(primary_a.clone(), Some(corroborating_b))
        );
        assert_ne!(full, ProviderCacheBinding::primary(primary_a));
        scope_store.cleanup();
    }

    #[test]
    fn maps_codex_additional_model_limits() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let extra = CodexAdditionalRateLimit {
            limit_name: Some("gpt-5.2-codex-spark".to_string()),
            metered_feature: None,
            rate_limit: Some(CodexRateLimit {
                primary_window: Some(CodexWindow {
                    used_percent: 41.0,
                    reset_at: 1_700_003_600,
                    limit_window_seconds: 18_000,
                }),
                secondary_window: None,
            }),
        };
        let windows = codex_windows(None, Some(&[extra]), now);
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].label, "Codex Spark");
        assert_eq!(windows[0].remaining_percent, 59.0);
    }

    #[test]
    fn stage0_freezes_codex_additional_identity_baseline() {
        let metered_only = CodexAdditionalRateLimit {
            limit_name: None,
            metered_feature: Some("gpt-5.2-codex-spark".to_string()),
            rate_limit: None,
        };
        assert_eq!(
            additional_limit_label(&metered_only),
            "Codex Spark",
            "codex.additional.metered-feature.primary"
        );

        let named = CodexAdditionalRateLimit {
            limit_name: Some("named-limit".to_string()),
            metered_feature: Some("metered-feature".to_string()),
            rate_limit: None,
        };
        assert_eq!(
            additional_limit_label(&named),
            "Named Limit",
            "display label remains separate from the metered-feature identity"
        );

        let anonymous = CodexAdditionalRateLimit {
            limit_name: None,
            metered_feature: None,
            rate_limit: None,
        };
        assert_eq!(additional_limit_source(&anonymous), None);

        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let both_slots = CodexAdditionalRateLimit {
            limit_name: Some("named-limit".to_string()),
            metered_feature: Some(" metered-feature ".to_string()),
            rate_limit: Some(CodexRateLimit {
                primary_window: Some(CodexWindow {
                    used_percent: 10.0,
                    reset_at: 1_700_003_600,
                    limit_window_seconds: 18_000,
                }),
                secondary_window: Some(CodexWindow {
                    used_percent: 20.0,
                    reset_at: 1_700_086_400,
                    limit_window_seconds: 604_800,
                }),
            }),
        };
        assert_eq!(
            additional_limit_source(&both_slots).as_deref(),
            Some("metered-feature")
        );
        let windows = codex_windows(None, Some(&[both_slots]), now);
        assert_eq!(
            windows.len(),
            2,
            "codex.additional.primary-secondary emits both semantic slots"
        );
        let digest = sha256_hex("metered-feature".to_string());
        let primary_key = format!("additional.{digest}.primary.v1");
        let secondary_key = format!("additional.{digest}.secondary.v1");
        assert_eq!(windows[0].card_id, primary_key);
        assert_eq!(
            windows[0].window_key.as_deref(),
            Some(windows[0].card_id.as_str())
        );
        assert_eq!(windows[1].card_id, secondary_key);
        assert_eq!(
            windows[1].window_key.as_deref(),
            Some(windows[1].card_id.as_str())
        );
    }

    #[test]
    fn codex_unknown_and_anonymous_windows_are_structural_and_skip_history() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let unknown_main = CodexRateLimit {
            primary_window: Some(CodexWindow {
                used_percent: 5.0,
                reset_at: now.timestamp() + 3_600,
                limit_window_seconds: 3_600,
            }),
            secondary_window: None,
        };
        let anonymous = |primary_used: f64, secondary_used: f64| CodexAdditionalRateLimit {
            limit_name: None,
            metered_feature: None,
            rate_limit: Some(CodexRateLimit {
                primary_window: Some(CodexWindow {
                    used_percent: primary_used,
                    reset_at: now.timestamp() + 7_200,
                    limit_window_seconds: 7_200,
                }),
                secondary_window: Some(CodexWindow {
                    used_percent: secondary_used,
                    reset_at: now.timestamp() + 86_400,
                    limit_window_seconds: 86_400,
                }),
            }),
        };
        let windows = codex_windows(
            Some(&unknown_main),
            Some(&[anonymous(10.0, 20.0), anonymous(30.0, 40.0)]),
            now,
        );
        assert_eq!(windows.len(), 3);
        assert_eq!(
            windows
                .iter()
                .map(|window| window.card_id.as_str())
                .collect::<Vec<_>>(),
            vec![
                "row.main.primary.v1",
                "row.additional.unknown.primary.v1",
                "row.additional.unknown.secondary.v1"
            ]
        );
        assert_eq!(
            windows
                .iter()
                .map(|window| window.used_percent)
                .collect::<Vec<_>>(),
            vec![5.0, 10.0, 20.0],
            "duplicate anonymous slots keep the provider-order first row"
        );
        for window in &windows[1..] {
            assert_eq!(window.label_for_test(), "Unknown");
            assert_ne!(window.label_for_test(), "Codex extra limit");
        }
        for window in &windows {
            assert_eq!(window.window_key, None);
            assert_eq!(window.pace_status.state, PaceState::Unavailable);
            assert_eq!(window.pace_status.reason.as_deref(), Some("windowIdentity"));
        }

        let scope = TestRefreshScope::new("codex", "unknown-windows");
        let account_scope = scope
            .resolve_current("fixture", "unknown-windows", b"marker")
            .unwrap();
        let mut snapshot = AgentUsageSnapshot {
            account_key: None,
            merge_scope: None,
            client_id: "codex".to_string(),
            source: "fixture".to_string(),
            updated_at: String::new(),
            identity: None,
            history_scope: Ok(HistoryScope::for_test(account_scope.as_str())),
            account_scope: Ok(account_scope),
            windows,
            credits: None,
            error: None,
            transport_diagnostic: None,
        };
        let history_calls = std::cell::Cell::new(0);
        enrich_snapshot_with(&mut snapshot, now.timestamp(), |_, _, _| {
            history_calls.set(history_calls.get() + 1);
            Ok(Vec::new())
        });
        assert_eq!(history_calls.get(), 0);

        let wire = serde_json::to_value(&snapshot).unwrap();
        let rows = wire["windows"].as_array().unwrap();
        assert_eq!(rows.len(), 3);
        for row in rows {
            assert!(row["paceStatus"].get("windowKey").is_none());
            assert_eq!(row["paceStatus"]["state"], "unavailable");
            assert_eq!(row["paceStatus"]["reason"], "windowIdentity");
        }
        scope.cleanup();
    }

    #[test]
    fn parses_claude_credentials_file() {
        let raw = r#"{
            "claudeAiOauth": {
                "accessToken": "access",
                "refreshToken": "refresh",
                "expiresAt": 1700000000000,
                "scopes": ["user:profile"],
                "rateLimitTier": "max",
                "subscriptionType": "pro"
            }
        }"#;
        let credentials = parse_claude_credentials_data(raw, ClaudeCredentialSource::File).unwrap();
        assert_eq!(credentials.access_token, "access");
        assert_eq!(credentials.refresh_token.as_deref(), Some("refresh"));
        assert_eq!(credentials.scopes, vec!["user:profile"]);
        assert_eq!(credentials.subscription_type.as_deref(), Some("pro"));
    }

    #[test]
    fn merge_claude_credentials_rotates_tokens_and_preserves_other_fields() {
        let raw = r#"{
            "claudeAiOauth": {
                "accessToken": "old-access",
                "refreshToken": "old-refresh",
                "expiresAt": 1700000000000,
                "scopes": ["user:profile"],
                "subscriptionType": "pro"
            }
        }"#;
        let mut credentials =
            parse_claude_credentials_data(raw, ClaudeCredentialSource::File).unwrap();
        credentials.access_token = "new-access".to_string();
        credentials.refresh_token = Some("new-refresh".to_string());
        credentials.expires_at = Utc.timestamp_millis_opt(1_700_009_999_000).single();

        let merged = merge_claude_credentials_json(&credentials, raw).unwrap();
        let reparsed =
            parse_claude_credentials_data(&merged, ClaudeCredentialSource::File).unwrap();
        assert_eq!(reparsed.access_token, "new-access");
        assert_eq!(reparsed.refresh_token.as_deref(), Some("new-refresh"));
        assert_eq!(
            reparsed.expires_at,
            Utc.timestamp_millis_opt(1_700_009_999_000).single()
        );
        // Untouched fields the Claude CLI wrote survive the merge.
        assert_eq!(reparsed.subscription_type.as_deref(), Some("pro"));
        assert_eq!(reparsed.scopes, vec!["user:profile"]);
    }

    #[test]
    fn claude_keychain_write_decision_pins_account_and_rejects_target_mismatch() {
        let raw_a = r#"{
            "claudeAiOauth": {
                "accessToken": "old-access",
                "refreshToken": "old-refresh",
                "expiresAt": 0
            },
            "sibling": "a"
        }"#;
        let mut credentials =
            parse_claude_credentials_data(raw_a, ClaudeCredentialSource::Keychain).unwrap();
        credentials.keychain_account = Some("account-a".to_string());
        credentials.access_token = "new-access".to_string();
        credentials.refresh_token = Some("new-refresh".to_string());

        let (account, merged) =
            prepare_claude_keychain_write(&credentials, Some("account-a"), raw_a).unwrap();
        assert_eq!(account, "account-a");
        assert_eq!(
            serde_json::from_str::<Value>(&merged).unwrap()["claudeAiOauth"]["accessToken"],
            "new-access"
        );

        assert!(prepare_claude_keychain_write(&credentials, Some("account-b"), raw_a).is_err());
        assert!(prepare_claude_keychain_write(&credentials, None, raw_a).is_err());

        let raw_changed_target = r#"{
            "claudeAiOauth": {
                "accessToken": "account-b-access",
                "refreshToken": "account-b-refresh",
                "expiresAt": 0
            },
            "sibling": "b"
        }"#;
        assert!(
            prepare_claude_keychain_write(&credentials, Some("account-a"), raw_changed_target,)
                .is_err()
        );
    }

    #[test]
    fn atomic_write_replaces_existing_file_contents() {
        let dir = std::env::temp_dir().join(format!("tb_atomic_{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join(".credentials.json");
        fs::write(&path, "old").unwrap();

        atomic_write(&path, "new").unwrap();
        assert_eq!(fs::read_to_string(&path).unwrap(), "new");
        // No temp turds left in the directory.
        let leftovers: Vec<_> = fs::read_dir(&dir)
            .unwrap()
            .filter_map(|e| e.ok())
            .filter(|e| e.file_name().to_string_lossy().contains(".tmp."))
            .collect();
        assert!(leftovers.is_empty(), "temp file not cleaned up");

        let _ = fs::remove_dir_all(&dir);
    }

    #[cfg(target_os = "windows")]
    fn open_without_delete_sharing(path: &Path) -> fs::File {
        use std::os::windows::fs::OpenOptionsExt as _;
        use windows_sys::Win32::Storage::FileSystem::{FILE_SHARE_READ, FILE_SHARE_WRITE};

        let mut options = fs::OpenOptions::new();
        options
            .read(true)
            .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE);
        options.open(path).unwrap()
    }

    #[cfg(target_os = "windows")]
    fn atomic_temp_path(dir: &Path) -> Option<PathBuf> {
        fs::read_dir(dir)
            .unwrap()
            .filter_map(|entry| entry.ok())
            .find(|entry| entry.file_name().to_string_lossy().contains(".tmp."))
            .map(|entry| entry.path())
    }

    #[cfg(target_os = "windows")]
    fn lock_staged_atomic_temp(dir: &Path) -> Option<fs::File> {
        use std::os::windows::fs::OpenOptionsExt as _;

        let mut options = fs::OpenOptions::new();
        options.read(true).share_mode(0);
        options.open(atomic_temp_path(dir)?).ok()
    }

    #[cfg(target_os = "windows")]
    #[test]
    fn atomic_write_retries_transient_windows_destination_lock() {
        use std::time::{Duration, Instant};

        let dir = std::env::temp_dir().join(format!(
            "tb_atomic_windows_transient_{}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join("auth.json");
        fs::write(&path, "old").unwrap();
        let destination_lock = open_without_delete_sharing(&path);

        let writer_path = path.clone();
        let writer = std::thread::spawn(move || atomic_write(&writer_path, "new"));
        let deadline = Instant::now() + Duration::from_secs(1);
        let staged_temp_lock = loop {
            if let Some(file) = lock_staged_atomic_temp(&dir) {
                break Some(file);
            }
            if writer.is_finished() || Instant::now() >= deadline {
                break None;
            }
            std::thread::sleep(Duration::from_millis(1));
        };
        let staged_temp_locked = staged_temp_lock.is_some();
        if staged_temp_locked {
            std::thread::sleep(Duration::from_millis(20));
        }
        let waited_for_retry = !writer.is_finished();
        drop(staged_temp_lock);
        drop(destination_lock);
        let result = writer.join().expect("atomic writer thread panicked");

        assert!(
            staged_temp_locked,
            "atomic write never completed temp-file staging"
        );
        assert!(
            waited_for_retry,
            "atomic write did not retry the sharing denial"
        );
        result.unwrap();
        assert_eq!(fs::read_to_string(&path).unwrap(), "new");
        assert!(atomic_temp_path(&dir).is_none(), "temp file not cleaned up");
        let _ = fs::remove_dir_all(&dir);
    }

    #[cfg(target_os = "windows")]
    #[test]
    fn atomic_write_exhausts_windows_retry_budget_without_losing_original() {
        use std::time::{Duration, Instant};

        let dir = std::env::temp_dir().join(format!(
            "tb_atomic_windows_persistent_{}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join("auth.json");
        fs::write(&path, "old").unwrap();
        let destination_lock = open_without_delete_sharing(&path);

        let started = Instant::now();
        let result = atomic_write(&path, "new");
        let elapsed = started.elapsed();
        drop(destination_lock);

        assert!(result.is_err(), "persistent sharing denial must fail");
        assert!(
            elapsed >= Duration::from_millis(80),
            "atomic write returned before exhausting the retry budget: {elapsed:?}"
        );
        assert_eq!(fs::read_to_string(&path).unwrap(), "old");
        assert!(atomic_temp_path(&dir).is_none(), "temp file not cleaned up");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn maps_claude_oauth_windows() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let usage = ClaudeUsageResponse {
            five_hour: Some(ClaudeWindow {
                utilization: Some(8.0),
                resets_at: Some("2023-11-14T23:13:20Z".to_string()),
            }),
            seven_day: Some(ClaudeWindow {
                utilization: Some(23.0),
                resets_at: Some("2023-11-17T22:13:20Z".to_string()),
            }),
            seven_day_oauth_apps: None,
            seven_day_opus: None,
            seven_day_sonnet: Some(ClaudeWindow {
                utilization: Some(3.0),
                resets_at: None,
            }),
            seven_day_design: Some(ClaudeWindow {
                utilization: Some(0.0),
                resets_at: None,
            }),
            seven_day_routines: None,
            extra_usage: None,
            ..Default::default()
        };
        let windows = claude_windows(&usage, now);
        assert_eq!(windows.len(), 4);
        assert_eq!(windows[0].label, "Session");
        assert_eq!(windows[0].remaining_percent, 92.0);
        assert_eq!(windows[1].label, "Weekly");
        assert_eq!(windows[1].remaining_percent, 77.0);
        assert_eq!(windows[2].label, "Sonnet");
        assert_eq!(windows[2].remaining_percent, 97.0);
        assert_eq!(windows[3].label, "Designs");
        assert_eq!(windows[3].remaining_percent, 100.0);
    }

    #[test]
    fn maps_claude_fable_scoped_weekly_limit() {
        let raw = r#"{
            "limits": [{
                "kind": "weekly_scoped",
                "group": "weekly",
                "percent": 12.5,
                "resets_at": "2026-08-10T00:00:00Z",
                "scope": {"model": {
                    "id": "claude/fable.5:promo",
                    "display_name": "Fable"
                }}
            }]
        }"#;
        let usage: ClaudeUsageResponse = serde_json::from_str(raw).unwrap();
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let windows = claude_windows(&usage, now);
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].label, "Fable only");
        assert_eq!(windows[0].card_id, "weekly_scoped.fable.v1");
        assert_eq!(windows[0].used_percent, 12.5);
        assert_eq!(
            windows[0].resets_at.as_deref(),
            Some("2026-08-10T00:00:00.000Z")
        );
        // The scope reaches the consumer, and it is the DISPLAY-NAME slug.
        // `scope.model.id` here is "claude/fable.5:promo" — deliberately
        // unlike the slug, so an implementation that switched to the id
        // would fail rather than coincide. The live payload reports that id
        // as null anyway, which is why identity comes from the display name.
        assert_eq!(windows[0].model_scope.as_deref(), Some("fable"));
        let wire = serde_json::to_value(&windows[0]).expect("serialize window");
        assert_eq!(wire["modelScope"], "fable");
    }

    /// A window nobody scoped must say so by ABSENCE, not by an empty string.
    ///
    /// "Designs" and "Daily Routines" are narrow windows whose scope is not a
    /// model, and the flat `seven_day_*` fields declare no scope at all. A
    /// consumer filtering usage by scope has to be able to tell "not scoped"
    /// from "scoped to something", and the key is omitted rather than emitted
    /// null so the distinction survives the wire.
    #[test]
    fn an_unscoped_claude_window_carries_no_model_scope() {
        let usage = ClaudeUsageResponse {
            seven_day: Some(ClaudeWindow {
                utilization: Some(23.0),
                resets_at: None,
            }),
            seven_day_design: Some(ClaudeWindow {
                utilization: Some(0.0),
                resets_at: None,
            }),
            ..Default::default()
        };
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let windows = claude_windows(&usage, now);
        assert!(!windows.is_empty());
        for window in &windows {
            assert_eq!(window.model_scope, None, "{}", window.label);
            let wire = serde_json::to_value(window).expect("serialize window");
            assert!(
                wire.get("modelScope").is_none(),
                "{} emitted a modelScope key",
                window.label
            );
        }
    }

    #[test]
    fn maps_claude_scoped_limit_even_when_inactive() {
        let raw = r#"{
            "limits": [{
                "kind": "weekly_scoped",
                "group": "weekly",
                "percent": 12.5,
                "resets_at": "2026-08-10T00:00:00Z",
                "is_active": false,
                "scope": {"model": {
                    "id": "claude/fable.5:promo",
                    "display_name": "Fable"
                }}
            }]
        }"#;
        let usage: ClaudeUsageResponse = serde_json::from_str(raw).unwrap();
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        assert_eq!(claude_windows(&usage, now).len(), 1);
    }

    #[test]
    fn skips_claude_scoped_all_models_limit() {
        let raw = r#"{
            "limits": [{
                "kind": "weekly_scoped",
                "group": "weekly",
                "percent": 12.5,
                "scope": {"model": {
                    "id": "claude/all-models",
                    "display_name": "All Models"
                }}
            }]
        }"#;
        let usage: ClaudeUsageResponse = serde_json::from_str(raw).unwrap();
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        assert!(claude_windows(&usage, now).is_empty());
    }

    #[test]
    fn deduplicates_claude_scoped_opus_against_flat_window() {
        let raw = r#"{
            "seven_day_opus": {"utilization": 25},
            "limits": [{
                "kind": "weekly_scoped",
                "group": "weekly",
                "percent": 80,
                "scope": {"model": {
                    "id": "claude/opus.5",
                    "display_name": "Opus"
                }}
            }]
        }"#;
        let usage: ClaudeUsageResponse = serde_json::from_str(raw).unwrap();
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let windows = claude_windows(&usage, now);
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].label, "Opus");
        assert!(!windows[0].label.ends_with(" only"));
        assert_eq!(windows[0].used_percent, 25.0);
    }

    /// The migration case: once Anthropic drops a flat field, the scoped entry
    /// must take over rather than leaving the model with no window at all — and
    /// it must inherit the flat lane's identity, or the handover costs the user
    /// their pinned gauge and the window's learned pace for a quota that never
    /// actually changed.
    #[test]
    fn maps_claude_scoped_opus_when_flat_window_absent() {
        let raw = r#"{
            "limits": [{
                "kind": "weekly_scoped",
                "group": "weekly",
                "percent": 80,
                "scope": {"model": {
                    "id": "claude/opus.5",
                    "display_name": "Opus"
                }}
            }]
        }"#;
        let usage: ClaudeUsageResponse = serde_json::from_str(raw).unwrap();
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let windows = claude_windows(&usage, now);
        assert_eq!(windows.len(), 1);
        // Identical to what the flat `seven_day_opus` lane emits, so a pinned
        // gauge and its history survive the handover untouched.
        assert_eq!(windows[0].label, "Opus");
        assert_eq!(windows[0].card_id, "opus.weekly.v1");
        assert_eq!(windows[0].window_key.as_deref(), Some("opus.weekly.v1"));
        assert_eq!(windows[0].used_percent, 80.0);
    }

    /// `CLAUDE_SCOPED_FLAT_SUCCESSORS` is hand-written, so it can drift from the
    /// identities `claude_windows()` actually emits for the flat fields. Drive
    /// both lanes with the same quota and require the identity to be identical:
    /// if either side is renamed or re-keyed without the other, this fails.
    #[test]
    fn claude_scoped_successors_match_their_flat_lane_identities() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let cases = [
            ("seven_day_sonnet", "Sonnet"),
            ("seven_day_opus", "Opus"),
            ("seven_day_design", "Designs"),
            ("seven_day_routines", "Daily Routines"),
        ];

        for (flat_field, display_name) in cases {
            let flat = claude_windows(
                &serde_json::from_str::<ClaudeUsageResponse>(&format!(
                    r#"{{"{flat_field}": {{"utilization": 40}}}}"#
                ))
                .unwrap(),
                now,
            );
            let scoped = claude_windows(
                &serde_json::from_str::<ClaudeUsageResponse>(&format!(
                    r#"{{"limits": [{{"kind": "weekly_scoped", "group": "weekly",
                         "percent": 40,
                         "scope": {{"model": {{"id": null,
                                               "display_name": "{display_name}"}}}}}}]}}"#
                ))
                .unwrap(),
                now,
            );

            assert_eq!(flat.len(), 1, "flat lane for {flat_field}");
            assert_eq!(scoped.len(), 1, "scoped lane for {display_name}");
            assert_eq!(
                scoped[0].card_id, flat[0].card_id,
                "card id drifted for {display_name}"
            );
            assert_eq!(
                scoped[0].window_key, flat[0].window_key,
                "window key drifted for {display_name}"
            );
            assert_eq!(
                scoped[0].label, flat[0].label,
                "label drifted for {display_name}"
            );
        }
    }

    /// Designs and Daily Routines successors keep their flat lane's identity
    /// but carry no model scope: they narrow a quota to a product surface,
    /// not a model, and a scope would filter their usage to a model id no
    /// message carries. Control: a model successor (Sonnet) and a
    /// non-successor (Fable) are still scoped.
    #[test]
    fn a_designs_or_routines_successor_carries_no_model_scope() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let scoped = |display_name: &str| {
            claude_windows(
                &serde_json::from_str::<ClaudeUsageResponse>(&format!(
                    r#"{{"limits": [{{"kind": "weekly_scoped", "group": "weekly",
                         "percent": 40,
                         "scope": {{"model": {{"id": null,
                                               "display_name": "{display_name}"}}}}}}]}}"#
                ))
                .unwrap(),
                now,
            )
        };

        for (display_name, card_id) in [
            ("Designs", "design.weekly.v1"),
            ("Daily Routines", "routines.weekly.v1"),
        ] {
            let windows = scoped(display_name);
            assert_eq!(windows.len(), 1, "{display_name}");
            assert_eq!(windows[0].card_id, card_id, "{display_name}");
            assert_eq!(windows[0].model_scope, None, "{display_name}");
            let wire = serde_json::to_value(&windows[0]).expect("serialize window");
            assert!(wire.get("modelScope").is_none(), "{display_name}");
        }

        let sonnet = scoped("Sonnet");
        assert_eq!(sonnet[0].card_id, "sonnet.weekly.v1");
        assert_eq!(sonnet[0].model_scope.as_deref(), Some("sonnet"));
        let fable = scoped("Fable");
        assert_eq!(fable[0].card_id, "weekly_scoped.fable.v1");
        assert_eq!(fable[0].model_scope.as_deref(), Some("fable"));
    }

    /// End-to-end shape check against a real `oauth/usage` response captured
    /// 2026-08-04 (percentages and timestamps replaced with neutral test
    /// values; every field, including the ones we do not parse, kept verbatim).
    ///
    /// This pins three things the synthetic fixtures above cannot, because the
    /// live payload differs from what the reference implementations led us to
    /// expect:
    ///   - the account-wide weekly entry is `kind: "weekly_all"` with a null
    ///     scope, not an all-models scope, so `kind` is what actually keeps it
    ///     out of the scoped lane;
    ///   - the real Fable entry carries `scope.model.id: null`, so identity
    ///     falls back to the display name;
    ///   - the real Fable entry carries `resets_at: null` and must still
    ///     produce a window.
    #[test]
    fn maps_live_claude_usage_payload_shape() {
        let raw = r#"{
            "five_hour": {"utilization": 55.0, "resets_at": "2026-08-10T20:40:00.197695+00:00",
                          "limit_dollars": null, "used_dollars": null, "remaining_dollars": null},
            "seven_day": {"utilization": 33.0, "resets_at": "2026-08-12T10:00:00.197716+00:00",
                          "limit_dollars": null, "used_dollars": null, "remaining_dollars": null},
            "seven_day_oauth_apps": null, "seven_day_opus": null, "seven_day_sonnet": null,
            "seven_day_cowork": null, "seven_day_omelette": null, "tangelo": null,
            "iguana_necktie": null, "omelette_promotional": null, "nimbus_quill": null,
            "cinder_cove": null, "amber_ladder": null,
            "extra_usage": {"is_enabled": false, "monthly_limit": null, "used_credits": null,
                            "utilization": null, "currency": null, "decimal_places": null,
                            "disabled_reason": null, "user_disabled": true,
                            "spend_limit_reached": false, "credits_ever_enabled": true,
                            "daily": null, "weekly": null},
            "limits": [
                {"kind": "session", "group": "session", "percent": 55, "severity": "normal",
                 "resets_at": "2026-08-10T20:40:00.197695+00:00", "scope": null, "is_active": true},
                {"kind": "weekly_all", "group": "weekly", "percent": 33, "severity": "normal",
                 "resets_at": "2026-08-12T10:00:00.197716+00:00", "scope": null, "is_active": false},
                {"kind": "weekly_scoped", "group": "weekly", "percent": 0, "severity": "normal",
                 "resets_at": null,
                 "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null},
                 "is_active": false}
            ],
            "spend": {"used": {"amount_minor": 0, "currency": "USD", "exponent": 2},
                      "limit": null, "percent": 0, "severity": "normal", "enabled": false,
                      "disabled_reason": null, "cap": null, "balance": null, "auto_reload": null,
                      "disclaimer": "Usage credits cover you when you hit your plan limits.",
                      "can_purchase_credits": false, "can_toggle": false},
            "member_dashboard_available": false
        }"#;
        let usage: ClaudeUsageResponse = serde_json::from_str(raw).unwrap();
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let windows = claude_windows(&usage, now);

        // Session and Weekly come from the flat fields; the `session` and
        // `weekly_all` entries in `limits[]` describe the same two quotas and
        // must not add duplicates.
        assert_eq!(
            windows
                .iter()
                .map(|window| window.label.as_str())
                .collect::<Vec<_>>(),
            ["Session", "Weekly", "Fable only"]
        );
        assert_eq!(windows[0].used_percent, 55.0);
        assert_eq!(windows[1].used_percent, 33.0);

        let fable = &windows[2];
        assert_eq!(fable.card_id, "weekly_scoped.fable.v1");
        assert_eq!(fable.used_percent, 0.0);
        assert_eq!(fable.remaining_percent, 100.0);
        assert_eq!(fable.resets_at, None);
    }

    /// Per-entry tolerance: one unusable element must not discard its valid
    /// siblings, which is what separates element-wise parsing from decoding the
    /// array as a whole.
    #[test]
    fn keeps_valid_claude_scoped_limit_beside_malformed_sibling() {
        let raw = r#"{
            "limits": [
                42,
                {"kind": "weekly_scoped", "group": "weekly", "percent": "oops",
                 "scope": {"model": {"display_name": "Broken"}}},
                {"kind": "weekly_scoped", "group": "weekly", "percent": 12.5,
                 "scope": {"model": {
                    "id": "claude/fable.5:promo", "display_name": "Fable"
                 }}}
            ]
        }"#;
        let usage: ClaudeUsageResponse = serde_json::from_str(raw).unwrap();
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let windows = claude_windows(&usage, now);
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].label, "Fable only");
        assert_eq!(windows[0].used_percent, 12.5);
    }

    #[test]
    fn ignores_malformed_claude_scoped_limits() {
        let raw = r#"{
            "limits": [
                42,
                {"kind": "session", "group": "weekly", "percent": 10,
                 "scope": {"model": {"display_name": "Session"}}},
                {"kind": "weekly_scoped", "group": "monthly", "percent": 10,
                 "scope": {"model": {"display_name": "Monthly"}}},
                {"kind": "weekly_scoped", "group": "weekly", "percent": "NaN",
                 "scope": {"model": {"display_name": "Infinite"}}},
                {"kind": "weekly_scoped", "group": "weekly", "percent": 10,
                 "scope": {"model": {"display_name": "   "}}}
            ]
        }"#;
        let usage: ClaudeUsageResponse = serde_json::from_str(raw).unwrap();
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        assert!(claude_windows(&usage, now).is_empty());
    }

    #[test]
    fn deduplicates_duplicate_claude_scoped_limit_slugs_first_wins() {
        let raw = r#"{
            "limits": [
                {"kind": "weekly_scoped", "group": "weekly", "percent": 12,
                 "scope": {"model": {
                    "id": "claude/fable.5:promo", "display_name": "Fable"
                 }}},
                {"kind": "weekly_scoped", "group": "weekly", "percent": 99,
                 "scope": {"model": {
                    "id": "claude/fable.5:promo-v2", "display_name": "Fable"
                 }}}
            ]
        }"#;
        let usage: ClaudeUsageResponse = serde_json::from_str(raw).unwrap();
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let windows = claude_windows(&usage, now);
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].used_percent, 12.0);
        assert_eq!(windows[0].label, "Fable only");
    }

    /// Regression: `scope.model.id` is null in the live payload but the field
    /// exists, so Anthropic populating it later must not move the window's
    /// identity. A moved `card_id` silently drops the user's persisted gauge
    /// selection (Swift matches `clientId|cardId` exactly) and restarts the
    /// quota-history series, with no visible change to the label.
    #[test]
    fn claude_scoped_identity_survives_model_id_appearing() {
        let with_null_id = r#"{
            "limits": [{"kind": "weekly_scoped", "group": "weekly", "percent": 7,
                        "scope": {"model": {"id": null, "display_name": "Fable"}}}]
        }"#;
        let with_populated_id = r#"{
            "limits": [{"kind": "weekly_scoped", "group": "weekly", "percent": 7,
                        "scope": {"model": {
                            "id": "claude/fable.5:promo", "display_name": "Fable"
                        }}}]
        }"#;
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();

        let before = claude_windows(
            &serde_json::from_str::<ClaudeUsageResponse>(with_null_id).unwrap(),
            now,
        );
        let after = claude_windows(
            &serde_json::from_str::<ClaudeUsageResponse>(with_populated_id).unwrap(),
            now,
        );

        assert_eq!(before.len(), 1);
        assert_eq!(after.len(), 1);
        assert_eq!(before[0].card_id, "weekly_scoped.fable.v1");
        assert_eq!(after[0].card_id, before[0].card_id);
        assert_eq!(after[0].window_key, before[0].window_key);
    }

    #[test]
    fn stage4_claude_json_and_headers_share_canonical_duration_contracts() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let reset = Some("2026-07-24T00:00:00Z".to_string());
        let window = |utilization| ClaudeWindow {
            utilization: Some(utilization),
            resets_at: reset.clone(),
        };
        let usage = ClaudeUsageResponse {
            five_hour: Some(window(5.0)),
            seven_day: Some(window(10.0)),
            seven_day_oauth_apps: Some(window(15.0)),
            seven_day_sonnet: Some(window(20.0)),
            seven_day_opus: Some(window(25.0)),
            ..Default::default()
        };
        let windows = claude_windows(&usage, now);
        let contracts = windows
            .iter()
            .map(|window| {
                (
                    window.card_id.as_str(),
                    window.pace_status.window_key.as_deref(),
                    window.duration_seconds,
                    window.duration_source,
                    window.pace_status.state,
                )
            })
            .collect::<Vec<_>>();
        assert_eq!(
            contracts,
            vec![
                (
                    "session.v1",
                    Some("session.v1"),
                    Some(18_000),
                    Some(DurationSource::Contract),
                    PaceState::LearningHistory,
                ),
                (
                    "weekly.v1",
                    Some("weekly.v1"),
                    Some(604_800),
                    Some(DurationSource::Contract),
                    PaceState::LearningHistory,
                ),
                (
                    "oauth_apps.weekly.v1",
                    Some("oauth_apps.weekly.v1"),
                    Some(604_800),
                    Some(DurationSource::Contract),
                    PaceState::LearningHistory,
                ),
                (
                    "sonnet.weekly.v1",
                    Some("sonnet.weekly.v1"),
                    Some(604_800),
                    Some(DurationSource::Contract),
                    PaceState::LearningHistory,
                ),
                (
                    "opus.weekly.v1",
                    Some("opus.weekly.v1"),
                    Some(604_800),
                    Some(DurationSource::Contract),
                    PaceState::LearningHistory,
                ),
            ]
        );

        let headers = header_map(&[
            ("anthropic-ratelimit-unified-5h-utilization", "0.11"),
            ("anthropic-ratelimit-unified-5h-reset", "1783111200"),
            ("anthropic-ratelimit-unified-7d-utilization", "0.6"),
            ("anthropic-ratelimit-unified-7d-reset", "1783504800"),
        ]);
        let header_windows = parse_unified_ratelimit_windows(&headers, now);
        assert_eq!(header_windows.len(), 2);
        for (window, expected_key, expected_duration) in [
            (&header_windows[0], "session.v1", 18_000),
            (&header_windows[1], "weekly.v1", 604_800),
        ] {
            assert_eq!(window.card_id, expected_key);
            assert_eq!(window.pace_status.window_key.as_deref(), Some(expected_key));
            assert_eq!(window.duration_seconds, Some(expected_duration));
            assert_eq!(window.duration_source, Some(DurationSource::Contract));
            assert_eq!(window.pace_status.state, PaceState::LearningHistory);
        }
    }

    #[test]
    fn decodes_claude_alias_windows_without_duplicate_error() {
        let raw = r#"{
            "five_hour": { "utilization": 5, "resets_at": "2026-05-28T14:00:00Z" },
            "seven_day": { "utilization": 23, "resets_at": "2026-05-31T14:00:00Z" },
            "seven_day_sonnet": { "utilization": 3, "resets_at": null },
            "seven_day_omelette": { "utilization": 0, "resets_at": null },
            "omelette_promotional": { "utilization": 0, "resets_at": null },
            "seven_day_cowork": { "utilization": 0, "resets_at": null }
        }"#;
        let usage: ClaudeUsageResponse = serde_json::from_str(raw).unwrap();
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let windows = claude_windows(&usage, now);
        assert_eq!(
            windows.iter().map(|w| w.label.as_str()).collect::<Vec<_>>(),
            vec!["Session", "Weekly", "Sonnet", "Designs", "Daily Routines"]
        );
    }

    #[test]
    fn stage4_claude_weekly_alias_groups_share_canonical_contracts() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let design_aliases = [
            "seven_day_design",
            "seven_day_claude_design",
            "claude_design",
            "design",
            "seven_day_omelette",
            "omelette",
            "omelette_promotional",
        ];
        for alias in design_aliases {
            let raw =
                format!(r#"{{"{alias}":{{"utilization":12,"resets_at":"2026-07-24T00:00:00Z"}}}}"#);
            let usage: ClaudeUsageResponse = serde_json::from_str(&raw).unwrap();
            let windows = claude_windows(&usage, now);
            assert_eq!(windows.len(), 1, "claude.design.aliases: {alias}");
            assert_eq!(
                windows[0].label, "Designs",
                "claude.design.aliases: {alias}"
            );
            assert_eq!(windows[0].card_id, "design.weekly.v1", "{alias}");
            assert_eq!(
                windows[0].pace_status.window_key.as_deref(),
                Some("design.weekly.v1"),
                "{alias}"
            );
            assert_eq!(windows[0].duration_seconds, Some(604_800), "{alias}");
            assert_eq!(
                windows[0].duration_source,
                Some(DurationSource::Contract),
                "{alias}"
            );
            assert_eq!(windows[0].pace_status.state, PaceState::LearningHistory);
        }

        let routines_aliases = [
            "seven_day_routines",
            "seven_day_claude_routines",
            "claude_routines",
            "routines",
            "routine",
            "seven_day_cowork",
            "cowork",
        ];
        for alias in routines_aliases {
            let raw =
                format!(r#"{{"{alias}":{{"utilization":12,"resets_at":"2026-07-24T00:00:00Z"}}}}"#);
            let usage: ClaudeUsageResponse = serde_json::from_str(&raw).unwrap();
            let windows = claude_windows(&usage, now);
            assert_eq!(windows.len(), 1, "claude.routines.aliases: {alias}");
            assert_eq!(
                windows[0].label, "Daily Routines",
                "claude.routines.aliases: {alias}"
            );
            assert_eq!(windows[0].card_id, "routines.weekly.v1", "{alias}");
            assert_eq!(
                windows[0].pace_status.window_key.as_deref(),
                Some("routines.weekly.v1"),
                "{alias}"
            );
            assert_eq!(windows[0].duration_seconds, Some(604_800), "{alias}");
            assert_eq!(
                windows[0].duration_source,
                Some(DurationSource::Contract),
                "{alias}"
            );
            assert_eq!(windows[0].pace_status.state, PaceState::LearningHistory);
        }
    }

    #[test]
    fn stage0_freezes_claude_named_windows_and_invalid_baseline() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let raw = r#"{
            "five_hour": { "utilization": 5, "resets_at": "2026-07-18T00:00:00Z" },
            "seven_day": { "utilization": 10, "resets_at": "2026-07-19T00:00:00Z" },
            "seven_day_oauth_apps": { "utilization": 15, "resets_at": "2026-07-20T00:00:00Z" },
            "seven_day_sonnet": { "utilization": 20, "resets_at": "2026-07-21T00:00:00Z" },
            "seven_day_opus": { "utilization": 25, "resets_at": "2026-07-22T00:00:00Z" }
        }"#;
        let usage: ClaudeUsageResponse = serde_json::from_str(raw).unwrap();
        let windows = claude_windows(&usage, now);
        let mapped: Vec<_> = windows
            .iter()
            .map(|window| (window.label.as_str(), window.window_minutes))
            .collect();
        assert_eq!(
            mapped,
            vec![
                ("Session", Some(300)),
                ("Weekly", Some(10_080)),
                ("OAuth Apps", Some(10_080)),
                ("Sonnet", Some(10_080)),
                ("Opus", Some(10_080)),
            ],
            "claude.named-window-contracts"
        );

        let out_of_range = UsageWindow::from_used_percent(
            "Out of range".to_string(),
            150.0,
            Some(now - chrono::Duration::seconds(1)),
            now,
            Some(-1),
        );
        assert_eq!(
            out_of_range.used_percent, 100.0,
            "invalid.out-of-range captures the current clamping baseline"
        );
        assert!(
            out_of_range.resets_at.is_some(),
            "invalid.expired-reset captures the current emitted baseline"
        );
        assert_eq!(
            out_of_range.window_minutes, None,
            "invalid.contradictory-duration is not emitted as legacy duration"
        );

        let non_finite =
            UsageWindow::from_used_percent("Non-finite".to_string(), f64::NAN, None, now, None);
        assert!(
            non_finite.used_percent.is_nan(),
            "invalid.non-finite captures the current emitted baseline"
        );
    }

    #[test]
    fn stage4_claude_extra_usage_is_active_without_recording_an_observation() {
        let window = claude_extra_usage_window(Some(&ClaudeExtraUsage {
            is_enabled: true,
            monthly_limit: Some(10_000.0),
            used_credits: Some(2_500.0),
            utilization: None,
            currency: Some("USD".to_string()),
        }))
        .unwrap();
        assert_eq!(window.label, "Extra usage");
        assert_eq!(window.card_id, "extra_usage.v1");
        assert_eq!(window.used_percent, 25.0);
        assert!(window.resets_at.is_none());
        assert_eq!(
            window.pace_status.window_key.as_deref(),
            Some("extra_usage.v1")
        );
        assert_eq!(window.pace_status.state, PaceState::Unavailable);
        assert_eq!(window.pace_status.reason.as_deref(), Some("missingReset"));
        assert!(window.duration_seconds.is_none());
        assert!(window.historical_pace.is_none());

        let scope = TestRefreshScope::new("claude", "extra-usage");
        let account_scope = scope
            .resolve_current("fixture", "extra-usage", b"extra-usage-marker")
            .unwrap();
        let expected_scope = account_scope.as_str().to_string();
        let mut snapshot = AgentUsageSnapshot {
            account_key: None,
            merge_scope: None,
            client_id: "claude".to_string(),
            source: "oauth".to_string(),
            updated_at: String::new(),
            identity: None,
            history_scope: Ok(HistoryScope::for_test(account_scope.as_str())),
            account_scope: Ok(account_scope),
            windows: vec![window],
            credits: None,
            error: None,
            transport_diagnostic: None,
        };
        let calls = std::cell::Cell::new(0);
        enrich_snapshot_with(&mut snapshot, 1_700_000_000, |active, observations, _| {
            calls.set(calls.get() + 1);
            assert_eq!(
                active,
                &[SeriesKey::new(
                    "claude",
                    &HistoryScope::for_test(&expected_scope),
                    "extra_usage.v1"
                )]
            );
            assert!(observations.is_empty());
            Ok(Vec::new())
        });
        assert_eq!(calls.get(), 1);
        assert_eq!(
            snapshot.windows[0].pace_status.state,
            PaceState::Unavailable
        );
        assert_eq!(
            snapshot.windows[0].pace_status.reason.as_deref(),
            Some("missingReset")
        );
        scope.cleanup();
    }

    #[test]
    fn stage4_emitted_unavailable_series_survives_capacity_admission() {
        let scope = TestRefreshScope::new("claude", "emitted-capacity");
        let account_scope = scope
            .resolve_current("fixture", "capacity", b"capacity-marker")
            .unwrap();
        let account_scope_value = account_scope.as_str().to_string();
        let history_path = scope
            .root()
            .join(crate::agent_quota_history::HISTORY_FILE_NAME);
        let seed_now = 1_800_000_000_i64;
        let seed_reset = seed_now + 86_400;
        let history_scope = HistoryScope::for_test(&account_scope_value);
        let weekly_key = SeriesKey::new("claude", &history_scope, "weekly.v1");
        let mut seeded_keys = vec![weekly_key.clone()];
        seeded_keys.extend(
            (0..crate::agent_quota_history::MAX_SERIES - 1).map(|index| {
                SeriesKey::new("claude", &history_scope, format!("zzzz.{index:04}.v1"))
            }),
        );
        for (sample_index, sampled_at) in [
            seed_now,
            seed_now + 86_400 / 5,
            seed_now + 2 * 86_400 / 5,
            seed_now + 3 * 86_400 / 5,
            seed_now + 4 * 86_400 / 5,
            seed_reset - 1,
        ]
        .into_iter()
        .enumerate()
        {
            let seeded_observations = seeded_keys
                .iter()
                .cloned()
                .map(|key| QuotaObservation {
                    key,
                    reset_at: Some(seed_reset),
                    used_percent: 10.0 + sample_index as f64 * 10.0,
                    provider: None,
                    contract: Some(DurationEvidence::contract(86_400)),
                })
                .collect::<Vec<_>>();
            let seeded = crate::agent_quota_history::record_observations_at_path_and_evaluate(
                &seeded_keys,
                &seeded_observations,
                sampled_at,
                &history_path,
            )
            .unwrap();
            assert_eq!(seeded.len(), crate::agent_quota_history::MAX_SERIES);
        }

        let now = seed_reset + 15 * 60 + 1;
        let now_date = Utc.timestamp_opt(now, 0).single().unwrap();
        let mut weekly =
            UsageWindow::from_provider_used_percent("Weekly".to_string(), 20.0, None, now_date)
                .with_identity(
                    "weekly.v1",
                    Some("weekly.v1".to_string()),
                    None,
                    Some(DurationEvidence::contract(86_400)),
                );
        weekly.unavailable("missingReset");
        let new_window = UsageWindow::from_provider_used_percent(
            "New quota".to_string(),
            5.0,
            Some(Utc.timestamp_opt(now + 86_400, 0).single().unwrap()),
            now_date,
        )
        .with_identity(
            "new.v1",
            Some("new.v1".to_string()),
            None,
            Some(DurationEvidence::contract(86_400)),
        );
        let mut snapshot = AgentUsageSnapshot {
            account_key: None,
            merge_scope: None,
            client_id: "claude".to_string(),
            source: "oauth".to_string(),
            updated_at: String::new(),
            identity: None,
            history_scope: Ok(HistoryScope::for_test(account_scope.as_str())),
            account_scope: Ok(account_scope),
            windows: vec![weekly, new_window],
            credits: None,
            error: None,
            transport_diagnostic: None,
        };

        enrich_snapshot_with(
            &mut snapshot,
            now,
            |active, observations, transaction_now| {
                assert_eq!(active.len(), 2);
                assert!(active.contains(&weekly_key));
                assert_eq!(observations.len(), 1);
                assert_eq!(observations[0].key.window_key, "new.v1");
                crate::agent_quota_history::record_observations_at_path_and_evaluate(
                    active,
                    observations,
                    transaction_now,
                    &history_path,
                )
            },
        );

        let store: Value = serde_json::from_slice(&fs::read(&history_path).unwrap()).unwrap();
        let series = store["series"].as_array().unwrap();
        assert_eq!(series.len(), crate::agent_quota_history::MAX_SERIES);
        assert!(series.iter().any(|entry| {
            entry["providerId"] == "claude"
                && entry["accountScope"] == account_scope_value
                && entry["windowKey"] == "weekly.v1"
        }));
        assert_eq!(
            snapshot.windows[0].pace_status.reason.as_deref(),
            Some("missingReset")
        );
        scope.cleanup();
    }

    fn header_map(pairs: &[(&'static str, &'static str)]) -> reqwest::header::HeaderMap {
        let mut headers = reqwest::header::HeaderMap::new();
        for (name, value) in pairs {
            headers.insert(
                reqwest::header::HeaderName::from_static(name),
                reqwest::header::HeaderValue::from_static(value),
            );
        }
        headers
    }

    #[test]
    fn parses_unified_ratelimit_headers() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let headers = header_map(&[
            ("anthropic-ratelimit-unified-5h-utilization", "0.11"),
            ("anthropic-ratelimit-unified-5h-reset", "1783111200"),
            ("anthropic-ratelimit-unified-7d-utilization", "0.6"),
            ("anthropic-ratelimit-unified-7d-reset", "1783504800"),
        ]);
        let windows = parse_unified_ratelimit_windows(&headers, now);
        assert_eq!(windows.len(), 2);
        assert_eq!(windows[0].label, "Session");
        assert!((windows[0].used_percent - 11.0).abs() < 1e-9);
        assert!((windows[0].remaining_percent - 89.0).abs() < 1e-9);
        assert_eq!(windows[0].window_minutes, Some(300));
        assert!(windows[0].resets_at.is_some());
        assert!(windows[0].reset_text.is_some());
        assert_eq!(windows[1].label, "Weekly");
        assert!((windows[1].used_percent - 60.0).abs() < 1e-9);
        assert!((windows[1].remaining_percent - 40.0).abs() < 1e-9);
        assert_eq!(windows[1].window_minutes, Some(10_080));
    }

    #[test]
    fn unified_reset_text_is_relative() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let reset = 1_700_000_000 + 3600; // now + 1h
        let window = unified_ratelimit_window("Session", Some(0.5), Some(reset), now).unwrap();
        assert!((window.used_percent - 50.0).abs() < 1e-9);
        assert!(window.reset_text.as_deref().unwrap().contains("1h"));
    }

    #[test]
    fn unified_windows_skip_missing_and_unparseable() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        // empty -> nothing
        assert!(parse_unified_ratelimit_windows(&header_map(&[]), now).is_empty());

        // only 5h -> just Session
        let windows = parse_unified_ratelimit_windows(
            &header_map(&[("anthropic-ratelimit-unified-5h-utilization", "0.2")]),
            now,
        );
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].label, "Session");

        // unparseable 5h + valid 7d -> just Weekly
        let windows = parse_unified_ratelimit_windows(
            &header_map(&[
                ("anthropic-ratelimit-unified-5h-utilization", "abc"),
                ("anthropic-ratelimit-unified-7d-utilization", "0.4"),
            ]),
            now,
        );
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].label, "Weekly");

        // utilization present, reset absent -> window with no reset fields
        let window = unified_ratelimit_window("Weekly", Some(0.4), None, now).unwrap();
        assert!(window.resets_at.is_none());
        assert!(window.reset_text.is_none());
    }

    #[test]
    fn unified_window_rejects_invalid_fraction_before_wire() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let zero = unified_ratelimit_window("Session", Some(0.0), None, now).unwrap();
        assert!((zero.used_percent - 0.0).abs() < 1e-9);
        assert!((zero.remaining_percent - 100.0).abs() < 1e-9);
        let full = unified_ratelimit_window("Session", Some(1.0), None, now).unwrap();
        assert!((full.used_percent - 100.0).abs() < 1e-9);
        assert!((full.remaining_percent - 0.0).abs() < 1e-9);

        assert!(unified_ratelimit_window("Session", Some(1.5), None, now).is_none());
        assert!(unified_ratelimit_window("Session", Some(f64::NAN), None, now).is_none());
        assert!(parse_unified_ratelimit_windows(
            &header_map(&[
                ("anthropic-ratelimit-unified-5h-utilization", "NaN"),
                ("anthropic-ratelimit-unified-5h-reset", "1700003600"),
            ]),
            now,
        )
        .is_empty());

        // None utilization -> no window
        assert!(unified_ratelimit_window("Session", None, Some(1_783_111_200), now).is_none());
    }

    #[test]
    fn provider_adapters_reject_invalid_percentages_before_wire() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        assert!(map_claude_window(
            "Session",
            "session.v1",
            DurationEvidence::contract(300 * 60),
            &ClaudeWindow {
                utilization: Some(150.0),
                resets_at: None,
            },
            now,
        )
        .is_none());
        assert!(claude_extra_usage_window(Some(&ClaudeExtraUsage {
            is_enabled: true,
            monthly_limit: None,
            used_credits: None,
            utilization: Some(f64::NAN),
            currency: None,
        }))
        .is_none());
        assert!(map_window_with_identity(
            "Weekly",
            CodexWindow {
                used_percent: -1.0,
                reset_at: 1_700_003_600,
                limit_window_seconds: 604_800,
            },
            now,
            "main.weekly.v1",
            Some("main.weekly.v1".to_string()),
        )
        .is_none());

        let valid_duplicate = codex_windows(
            Some(&CodexRateLimit {
                primary_window: Some(CodexWindow {
                    used_percent: 150.0,
                    reset_at: 1_700_003_600,
                    limit_window_seconds: 18_000,
                }),
                secondary_window: Some(CodexWindow {
                    used_percent: 20.0,
                    reset_at: 1_700_003_600,
                    limit_window_seconds: 18_000,
                }),
            }),
            None,
            now,
        );
        assert_eq!(valid_duplicate.len(), 1);
        assert_eq!(valid_duplicate[0].card_id, "main.session.v1");
        assert_eq!(valid_duplicate[0].used_percent, 20.0);
    }

    #[test]
    fn provider_payloads_isolate_malformed_percentage_rows() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        for invalid in ["1e400", r#""NaN""#] {
            let codex: CodexUsageResponse = serde_json::from_str(&format!(
                r#"{{
                    "rate_limit": {{
                        "primary_window": {{
                            "used_percent": {invalid},
                            "reset_at": 1700003600,
                            "limit_window_seconds": 18000
                        }},
                        "secondary_window": {{
                            "used_percent": 20,
                            "reset_at": 1700003600,
                            "limit_window_seconds": 18000
                        }}
                    }}
                }}"#
            ))
            .unwrap();
            let codex_windows = codex_windows(codex.rate_limit.as_ref(), None, now);
            assert_eq!(codex_windows.len(), 1);
            assert_eq!(codex_windows[0].card_id, "main.session.v1");
            assert_eq!(codex_windows[0].used_percent, 20.0);

            let claude: ClaudeUsageResponse = serde_json::from_str(&format!(
                r#"{{
                    "five_hour": {{
                        "utilization": {invalid},
                        "resets_at": "2023-11-15T00:13:20Z"
                    }},
                    "seven_day": {{
                        "utilization": 20,
                        "resets_at": "2023-11-21T22:13:20Z"
                    }},
                    "seven_day_design": {{
                        "utilization": {invalid},
                        "resets_at": "2023-11-21T22:13:20Z"
                    }},
                    "design": {{
                        "utilization": 30,
                        "resets_at": "2023-11-21T22:13:20Z"
                    }},
                    "seven_day_routines": {{
                        "utilization": {invalid},
                        "resets_at": "2023-11-21T22:13:20Z"
                    }},
                    "routines": {{
                        "utilization": 40,
                        "resets_at": "2023-11-21T22:13:20Z"
                    }},
                    "extra_usage": {{
                        "is_enabled": true,
                        "utilization": {invalid}
                    }}
                }}"#
            ))
            .unwrap();
            let claude_windows = claude_windows(&claude, now);
            assert_eq!(claude_windows.len(), 3);
            assert!(claude_windows
                .iter()
                .any(|window| window.card_id == "weekly.v1" && window.used_percent == 20.0));
            assert!(claude_windows
                .iter()
                .any(|window| window.card_id == "design.weekly.v1" && window.used_percent == 30.0));
            assert!(claude_windows.iter().any(
                |window| window.card_id == "routines.weekly.v1" && window.used_percent == 40.0
            ));
        }
    }

    #[test]
    fn reads_claude_code_oauth_token_via_lookup() {
        let token = claude_token_from_lookup(|key| match key {
            "CLAUDE_CODE_OAUTH_TOKEN" => Some("  sk-ant-oat01-test  ".to_string()),
            _ => None,
        });
        assert_eq!(token.as_deref(), Some("sk-ant-oat01-test"));
        assert!(claude_token_from_lookup(|_| None).is_none());
        assert!(claude_token_from_lookup(|_| Some("   ".to_string())).is_none());
    }

    #[test]
    fn refreshes_or_expires_cached_windows() {
        let base = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let window =
            unified_ratelimit_window("Session", Some(0.2), Some(1_700_000_000 + 3600), base)
                .unwrap();

        // 30 min later, still before the reset: reset_text recomputed to the
        // shorter countdown (not the frozen original).
        let later = base + chrono::Duration::seconds(1800);
        let refreshed = refresh_cached_windows(std::slice::from_ref(&window), later).unwrap();
        assert_eq!(refreshed.len(), 1);
        assert!(refreshed[0].reset_text.as_deref().unwrap().contains("30m"));

        // Past the reset: stale -> expire (None) so the caller re-probes.
        let after = base + chrono::Duration::seconds(3700);
        assert!(refresh_cached_windows(std::slice::from_ref(&window), after).is_none());
    }

    struct RecordingRefreshScope<'a> {
        inner: &'a TestRefreshScope,
        resolves: Mutex<usize>,
        transfers: Mutex<Vec<(Vec<u8>, Vec<u8>)>>,
    }

    impl<'a> RecordingRefreshScope<'a> {
        fn new(inner: &'a TestRefreshScope) -> Self {
            Self {
                inner,
                resolves: Mutex::new(0),
                transfers: Mutex::new(Vec::new()),
            }
        }

        fn resolve_count(&self) -> usize {
            *self.resolves.lock().unwrap()
        }

        fn transfers(&self) -> Vec<(Vec<u8>, Vec<u8>)> {
            self.transfers.lock().unwrap().clone()
        }
    }

    impl RefreshScopeTransaction for RecordingRefreshScope<'_> {
        fn resolve_current(
            &self,
            semantic_source: &str,
            canonical_location: &str,
            marker: &[u8],
        ) -> Result<AccountScope, AccountScopeError> {
            *self.resolves.lock().unwrap() += 1;
            self.inner
                .resolve_current(semantic_source, canonical_location, marker)
        }

        fn transfer(
            &self,
            semantic_source: &str,
            canonical_location: &str,
            old_marker: &[u8],
            new_marker: &[u8],
        ) -> Result<AccountScope, AccountScopeError> {
            self.transfers
                .lock()
                .unwrap()
                .push((old_marker.to_vec(), new_marker.to_vec()));
            self.inner
                .transfer(semantic_source, canonical_location, old_marker, new_marker)
        }
    }

    struct MetadataFailingRefreshScope<'a> {
        inner: &'a TestRefreshScope,
    }

    impl RefreshScopeTransaction for MetadataFailingRefreshScope<'_> {
        fn resolve_current(
            &self,
            semantic_source: &str,
            canonical_location: &str,
            marker: &[u8],
        ) -> Result<AccountScope, AccountScopeError> {
            self.inner
                .resolve_current(semantic_source, canonical_location, marker)
        }

        fn transfer(
            &self,
            semantic_source: &str,
            canonical_location: &str,
            old_marker: &[u8],
            new_marker: &[u8],
        ) -> Result<AccountScope, AccountScopeError> {
            self.inner.fail_metadata_save();
            self.inner
                .transfer(semantic_source, canonical_location, old_marker, new_marker)
        }
    }

    fn checkpoint_at(
        target: Option<RefreshCheckpoint>,
    ) -> impl FnMut(RefreshCheckpoint) -> Result<(), ProviderFetchFailure> {
        move |checkpoint| {
            if Some(checkpoint) == target {
                Err(ProviderFetchFailure::terminal("injected crash"))
            } else {
                Ok(())
            }
        }
    }

    async fn codex_test_response(
        refresh_token: String,
        _attempt_binding: ProviderCacheBinding,
    ) -> Result<Value, ProviderFetchFailure> {
        assert_eq!(refresh_token, "codex-old-refresh");
        Ok(serde_json::json!({
            "access_token": "codex-new-access",
            "refresh_token": "codex-new-refresh"
        }))
    }

    fn setup_codex_refresh(
        tag: &str,
    ) -> (TestRefreshScope, PathBuf, AccountScope, Vec<u8>, String) {
        let scope = TestRefreshScope::new("codex", tag);
        let path = scope.root().join("codex/auth.json");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(
            &path,
            serde_json::to_vec_pretty(&serde_json::json!({
                "tokens": {
                    "access_token": " codex-old-access ",
                    "refresh_token": " codex-old-refresh ",
                    "id_token": " codex-old-id "
                }
            }))
            .unwrap(),
        )
        .unwrap();
        let credentials = load_codex_credentials_from(&path).unwrap();
        let location = credentials.scope_slot.canonical_location.clone();
        let old_scope = scope
            .resolve_current(
                credentials.scope_slot.semantic_source,
                &location,
                credentials.scope_marker(),
            )
            .unwrap();
        let metadata = scope.metadata_bytes();
        (scope, path, old_scope, metadata, location)
    }

    async fn run_codex_refresh<R: RefreshScopeTransaction + ?Sized>(
        scope: &R,
        path: &Path,
        crash: Option<RefreshCheckpoint>,
    ) -> Result<(CodexCredentials, ProviderCacheBinding), ProviderFetchFailure> {
        refresh_codex_credentials_with(
            path,
            scope,
            codex_test_response,
            save_codex_credentials,
            checkpoint_at(crash),
        )
        .await
    }

    #[tokio::test]
    async fn codex_refresh_rejects_concurrent_account_switch_without_touching_b() {
        const B_BYTES: &[u8] = br#"{
  "tokens": {
    "access_token": "account-b-access",
    "refresh_token": "account-b-refresh",
    "id_token": "account-b-id",
    "account_id": "account-b"
  },
  "sibling": {"writer": "b", "revision": 2}
}
"#;
        let (scope, path, _, metadata_before, _) = setup_codex_refresh("codex-target-switch");
        let recording = RecordingRefreshScope::new(&scope);
        let request_path = path.clone();

        let failure = refresh_codex_credentials_with(
            &path,
            &recording,
            move |refresh_token, _attempt_binding| async move {
                assert_eq!(refresh_token, "codex-old-refresh");
                fs::write(&request_path, B_BYTES).unwrap();
                Ok(serde_json::json!({
                    "access_token": "codex-new-access",
                    "refresh_token": "codex-new-refresh"
                }))
            },
            save_codex_credentials,
            checkpoint_at(None),
        )
        .await
        .unwrap_err();

        assert!(matches!(failure, ProviderFetchFailure::Terminal { .. }));
        assert!(recording.transfers().is_empty());
        assert_eq!(scope.metadata_bytes(), metadata_before);
        let stored_bytes = fs::read(&path).unwrap();
        assert_eq!(stored_bytes, B_BYTES);
        assert!(!String::from_utf8_lossy(&stored_bytes).contains("codex-new"));
        let stored = load_codex_credentials_from(&path).unwrap();
        assert_eq!(stored.access_token, "account-b-access");
        assert_eq!(stored.refresh_token.as_deref(), Some("account-b-refresh"));
        assert_eq!(stored.account_id.as_deref(), Some("account-b"));
        scope.cleanup();
    }

    #[tokio::test]
    async fn codex_refresh_patches_unchanged_target_and_preserves_siblings() {
        let (scope, path, old_scope, _, location) = setup_codex_refresh("codex-target-unchanged");
        let original = serde_json::json!({
            "tokens": {
                "access_token": "codex-old-access",
                "refresh_token": "codex-old-refresh",
                "id_token": "codex-old-id",
                "token_sibling": {"keep": true}
            },
            "sibling": {"writer": "before", "revision": 1}
        });
        fs::write(&path, serde_json::to_vec_pretty(&original).unwrap()).unwrap();
        let current = serde_json::json!({
            "tokens": original["tokens"].clone(),
            "sibling": {"writer": "codex-cli", "revision": 2},
            "unrelated": [1, 2, 3]
        });
        let current_bytes = serde_json::to_vec_pretty(&current).unwrap();
        let request_path = path.clone();

        let (refreshed, post_binding) = refresh_codex_credentials_with(
            &path,
            &scope,
            move |refresh_token, _attempt_binding| async move {
                assert_eq!(refresh_token, "codex-old-refresh");
                fs::write(&request_path, current_bytes).unwrap();
                Ok(serde_json::json!({
                    "access_token": "codex-new-access",
                    "refresh_token": "codex-new-refresh"
                }))
            },
            save_codex_credentials,
            checkpoint_at(None),
        )
        .await
        .unwrap();

        assert_eq!(refreshed.access_token, "codex-new-access");
        assert_eq!(post_binding.primary, old_scope);
        let stored: Value = serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
        assert_eq!(stored["tokens"]["access_token"], "codex-new-access");
        assert_eq!(stored["tokens"]["refresh_token"], "codex-new-refresh");
        assert_eq!(stored["tokens"]["id_token"], "codex-old-id");
        assert_eq!(stored["tokens"]["token_sibling"]["keep"], true);
        assert_eq!(stored["sibling"]["writer"], "codex-cli");
        assert_eq!(stored["sibling"]["revision"], 2);
        assert_eq!(stored["unrelated"], serde_json::json!([1, 2, 3]));
        assert_eq!(
            scope
                .resolve_current("codex-auth-json", &location, b"codex-old-refresh")
                .unwrap(),
            old_scope
        );
        assert_eq!(
            scope
                .resolve_current("codex-auth-json", &location, b"codex-new-refresh")
                .unwrap(),
            old_scope
        );
        scope.cleanup();
    }

    #[tokio::test]
    async fn codex_refresh_rejects_concurrent_logout_without_restoring_a() {
        const LOGGED_OUT_BYTES: &[u8] = br#"{
  "sibling": {"writer": "logout", "revision": 2}
}
"#;
        let (scope, path, _, metadata_before, _) = setup_codex_refresh("codex-target-logout");
        let recording = RecordingRefreshScope::new(&scope);
        let request_path = path.clone();

        let failure = refresh_codex_credentials_with(
            &path,
            &recording,
            move |refresh_token, _attempt_binding| async move {
                assert_eq!(refresh_token, "codex-old-refresh");
                fs::write(&request_path, LOGGED_OUT_BYTES).unwrap();
                Ok(serde_json::json!({
                    "access_token": "codex-new-access",
                    "refresh_token": "codex-new-refresh"
                }))
            },
            save_codex_credentials,
            checkpoint_at(None),
        )
        .await
        .unwrap_err();

        assert!(matches!(failure, ProviderFetchFailure::Terminal { .. }));
        assert!(recording.transfers().is_empty());
        assert_eq!(scope.metadata_bytes(), metadata_before);
        let stored_bytes = fs::read(&path).unwrap();
        assert_eq!(stored_bytes, LOGGED_OUT_BYTES);
        assert!(!String::from_utf8_lossy(&stored_bytes).contains("codex-new"));
        let stored: Value = serde_json::from_slice(&stored_bytes).unwrap();
        assert!(stored.get("tokens").is_none());
        scope.cleanup();
    }

    #[tokio::test]
    async fn codex_refresh_canonicalizes_tokens_and_preserves_unrotated_marker() {
        for (tag, refresh_value) in [
            ("missing", None),
            ("null", Some(Value::Null)),
            ("empty", Some(Value::String(String::new()))),
            ("whitespace", Some(Value::String(" \t\n ".to_string()))),
            (
                "non-string",
                Some(serde_json::json!({ "unexpected": true })),
            ),
        ] {
            let (scope, path, old_scope, _, _) =
                setup_codex_refresh(&format!("codex-canonical-{tag}"));
            let recording = RecordingRefreshScope::new(&scope);
            let mut response = serde_json::json!({
                "access_token": { "unexpected": true },
                "accessToken": " codex-new-access ",
                "id_token": " \t\n ",
                "idToken": " codex-new-id "
            });
            if let Some(refresh_value) = refresh_value {
                response
                    .as_object_mut()
                    .unwrap()
                    .insert("refresh_token".to_string(), refresh_value);
            }

            let (refreshed, post_binding) = refresh_codex_credentials_with(
                &path,
                &recording,
                move |refresh_token, _attempt_binding| async move {
                    assert_eq!(refresh_token, "codex-old-refresh");
                    Ok(response)
                },
                save_codex_credentials,
                checkpoint_at(None),
            )
            .await
            .unwrap();

            assert_eq!(refreshed.access_token, "codex-new-access", "{tag}");
            assert_eq!(
                refreshed.refresh_token.as_deref(),
                Some("codex-old-refresh"),
                "{tag}"
            );
            assert_eq!(refreshed.id_token.as_deref(), Some("codex-new-id"), "{tag}");
            assert_eq!(post_binding.primary, old_scope, "{tag}");
            assert_eq!(post_binding.corroborating, None, "{tag}");
            assert_eq!(
                recording.transfers(),
                vec![(b"codex-old-refresh".to_vec(), b"codex-old-refresh".to_vec())],
                "{tag}"
            );

            let stored: Value = serde_json::from_str(&fs::read_to_string(&path).unwrap()).unwrap();
            assert_eq!(
                stored["tokens"]["refresh_token"],
                Value::String("codex-old-refresh".to_string()),
                "{tag}"
            );
            let reloaded = load_codex_credentials_from(&path).unwrap();
            assert_eq!(reloaded.access_token, refreshed.access_token, "{tag}");
            assert_eq!(reloaded.refresh_token, refreshed.refresh_token, "{tag}");
            assert_eq!(reloaded.id_token, refreshed.id_token, "{tag}");
            assert_eq!(reloaded.scope_marker(), refreshed.scope_marker(), "{tag}");
            assert_eq!(
                scope
                    .resolve_current(
                        reloaded.scope_slot.semantic_source,
                        &reloaded.scope_slot.canonical_location,
                        reloaded.scope_marker(),
                    )
                    .unwrap(),
                old_scope,
                "{tag}"
            );
            scope.cleanup();
        }
    }

    #[tokio::test]
    async fn codex_refresh_rejects_invalid_success_schema_before_state_or_usage() {
        for (tag, response) in [
            ("array", serde_json::json!([])),
            ("empty-object", serde_json::json!({})),
            ("null", Value::Null),
            ("bool", Value::Bool(true)),
            ("string", Value::String("codex-new-access".to_string())),
            (
                "blank-access",
                serde_json::json!({ "access_token": " \t\n " }),
            ),
            (
                "invalid-aliases",
                serde_json::json!({
                    "access_token": false,
                    "accessToken": [],
                    "refreshToken": " codex-new-refresh "
                }),
            ),
        ] {
            let (scope, path, old_scope, metadata_before, location) =
                setup_codex_refresh(&format!("codex-invalid-schema-{tag}"));
            let credentials_before = fs::read(&path).unwrap();
            let recording = RecordingRefreshScope::new(&scope);
            let save_calls = std::cell::Cell::new(0);
            let usage_calls = std::cell::Cell::new(0);

            let refresh_result = refresh_codex_credentials_with(
                &path,
                &recording,
                move |refresh_token, _attempt_binding| async move {
                    assert_eq!(refresh_token, "codex-old-refresh");
                    Ok(response)
                },
                |_| -> Result<CodexCredentialWriteReceipt, String> {
                    save_calls.set(save_calls.get() + 1);
                    Err("unexpected save".to_string())
                },
                checkpoint_at(None),
            )
            .await;
            let result: Result<(), ProviderFetchFailure> =
                request_after_verified_binding(refresh_result, |_| async {
                    usage_calls.set(usage_calls.get() + 1);
                    Ok(())
                })
                .await;

            assert!(
                matches!(result, Err(ProviderFetchFailure::Terminal { .. })),
                "{tag}"
            );
            assert!(recording.transfers().is_empty(), "{tag}");
            assert_eq!(save_calls.get(), 0, "{tag}");
            assert_eq!(usage_calls.get(), 0, "{tag}");
            assert_eq!(fs::read(&path).unwrap(), credentials_before, "{tag}");
            assert_eq!(scope.metadata_bytes(), metadata_before, "{tag}");
            let reloaded = load_codex_credentials_from(&path).unwrap();
            assert_eq!(reloaded.access_token, "codex-old-access", "{tag}");
            assert_eq!(
                reloaded.refresh_token.as_deref(),
                Some("codex-old-refresh"),
                "{tag}"
            );
            assert!(reloaded.last_refresh.is_none(), "{tag}");
            assert_eq!(
                scope
                    .resolve_current("codex-auth-json", &location, reloaded.scope_marker())
                    .unwrap(),
                old_scope,
                "{tag}"
            );
            scope.cleanup();
        }
    }

    #[tokio::test]
    async fn codex_refresh_crash_boundaries_and_scope_gate_use_production_sequence() {
        // These checkpoints model process stops, not a cross-resource transaction:
        // after credential persistence, metadata may still be the pre-refresh bytes.
        for boundary in [
            RefreshCheckpoint::Reloaded,
            RefreshCheckpoint::NetworkReturned,
            RefreshCheckpoint::CredentialsPersisted,
            RefreshCheckpoint::MetadataHandled,
        ] {
            let (scope, path, old_scope, before, location) = setup_codex_refresh("codex-crash");
            let failure = run_codex_refresh(&scope, &path, Some(boundary))
                .await
                .unwrap_err();
            assert!(matches!(
                failure,
                ProviderFetchFailure::Terminal { ref display } if display == "injected crash"
            ));
            let credentials_persisted = matches!(
                boundary,
                RefreshCheckpoint::CredentialsPersisted | RefreshCheckpoint::MetadataHandled
            );
            let stored = load_codex_credentials_from(&path).unwrap();
            assert_eq!(
                stored.refresh_token.as_deref(),
                Some(if credentials_persisted {
                    "codex-new-refresh"
                } else {
                    "codex-old-refresh"
                })
            );
            if boundary == RefreshCheckpoint::MetadataHandled {
                assert_ne!(scope.metadata_bytes(), before);
                assert_eq!(
                    scope
                        .resolve_current("codex-auth-json", &location, b"codex-old-refresh")
                        .unwrap(),
                    old_scope
                );
                assert_eq!(
                    scope
                        .resolve_current("codex-auth-json", &location, b"codex-new-refresh")
                        .unwrap(),
                    old_scope
                );
            } else {
                assert_eq!(scope.metadata_bytes(), before);
            }
            scope.cleanup();
        }

        let (scope, path, old_scope, before, location) = setup_codex_refresh("codex-metadata-fail");
        let auth_before = fs::read(&path).unwrap();
        let failing = MetadataFailingRefreshScope { inner: &scope };
        let failure = run_codex_refresh(&failing, &path, None).await.unwrap_err();
        assert!(matches!(failure, ProviderFetchFailure::Terminal { .. }));
        assert_eq!(scope.metadata_bytes(), before);
        assert_eq!(fs::read(&path).unwrap(), auth_before);
        let persisted = load_codex_credentials_from(&path).unwrap();
        assert_eq!(persisted.access_token, "codex-old-access");
        assert_eq!(
            persisted.refresh_token.as_deref(),
            Some("codex-old-refresh")
        );
        assert_eq!(
            scope
                .resolve_current("codex-auth-json", &location, persisted.scope_marker())
                .unwrap(),
            old_scope
        );
        scope.cleanup();

        const CONCURRENT_LOGIN_BYTES: &[u8] = br#"{
  "tokens": {
    "access_token": "concurrent-access",
    "refresh_token": "concurrent-refresh",
    "id_token": "concurrent-id",
    "account_id": "concurrent-account"
  },
  "sibling": {"writer": "codex-cli", "revision": 3}
}
"#;
        let (scope, path, _, metadata_before, _) =
            setup_codex_refresh("codex-metadata-fail-concurrent-login");
        let failing = MetadataFailingRefreshScope { inner: &scope };
        let save_path = path.clone();
        let failure = refresh_codex_credentials_with(
            &path,
            &failing,
            codex_test_response,
            move |credentials| {
                let receipt = save_codex_credentials(credentials)?;
                fs::write(&save_path, CONCURRENT_LOGIN_BYTES)
                    .map_err(|error| format!("inject concurrent Codex login: {error}"))?;
                Ok(receipt)
            },
            checkpoint_at(None),
        )
        .await
        .unwrap_err();
        assert!(matches!(failure, ProviderFetchFailure::Terminal { .. }));
        assert_eq!(scope.metadata_bytes(), metadata_before);
        assert_eq!(fs::read(&path).unwrap(), CONCURRENT_LOGIN_BYTES);
        scope.cleanup();

        let (scope, path, _, metadata_before, _) = setup_codex_refresh("codex-save-fail");
        let auth_before = fs::read(&path).unwrap();
        let recording = RecordingRefreshScope::new(&scope);
        let failure = refresh_codex_credentials_with(
            &path,
            &recording,
            codex_test_response,
            |_| Err("injected save failure".to_string()),
            checkpoint_at(None),
        )
        .await
        .unwrap_err();
        assert!(matches!(failure, ProviderFetchFailure::Terminal { .. }));
        assert!(recording.transfers().is_empty());
        assert_eq!(scope.metadata_bytes(), metadata_before);
        assert_eq!(fs::read(&path).unwrap(), auth_before);
        scope.cleanup();

        let (scope, path, old_scope, _, location) = setup_codex_refresh("codex-success");
        let recording = RecordingRefreshScope::new(&scope);
        let (_, post_binding) = refresh_codex_credentials_with(
            &path,
            &recording,
            codex_test_response,
            save_codex_credentials,
            checkpoint_at(None),
        )
        .await
        .unwrap();
        assert_eq!(recording.resolve_count(), 1);
        assert_eq!(post_binding.primary, old_scope);
        assert_eq!(post_binding.corroborating, None);
        assert_eq!(
            scope
                .resolve_current("codex-auth-json", &location, b"codex-new-refresh")
                .unwrap(),
            old_scope
        );
        scope.cleanup();
    }

    #[tokio::test]
    async fn codex_refresh_transient_uses_lock_reloaded_binding_not_outer_binding() {
        let (scope, path, inner_scope, _, location) = setup_codex_refresh("codex-lock-binding");
        let outer_scope = scope
            .resolve_current("codex-auth-json", &location, b"outer-refresh-a")
            .unwrap();
        assert_ne!(outer_scope, inner_scope);
        let expected = ProviderCacheBinding::primary(inner_scope);
        let request_expected = expected.clone();

        let failure = refresh_codex_credentials_with(
            &path,
            &scope,
            move |refresh_token, attempt_binding| async move {
                assert_eq!(refresh_token, "codex-old-refresh");
                assert_eq!(attempt_binding, request_expected);
                Err(ProviderFetchFailure::transient(
                    "Codex token refresh failed. Retrying automatically.",
                    Some(attempt_binding),
                    SafeTransportDiagnostic::from_facts(TransportErrorFacts::synthetic(
                        true,
                        false,
                        TransportPhase::Request,
                        None,
                    )),
                ))
            },
            save_codex_credentials,
            checkpoint_at(None),
        )
        .await
        .unwrap_err();

        match failure {
            ProviderFetchFailure::Transient {
                attempt_binding, ..
            } => assert_eq!(attempt_binding, Some(expected)),
            ProviderFetchFailure::Terminal { .. } => panic!("timeout must remain transient"),
        }
        scope.cleanup();
    }

    async fn claude_test_response(
        refresh_token: String,
        _attempt_binding: ProviderCacheBinding,
    ) -> Result<ClaudeRefreshResponse, ProviderFetchFailure> {
        assert_eq!(refresh_token, "claude-old-refresh");
        Ok(ClaudeRefreshResponse {
            access_token: " claude-new-access ".to_string(),
            refresh_token: Some("claude-new-refresh".to_string()),
            expires_in: 3_600,
        })
    }

    fn setup_claude_refresh(
        tag: &str,
    ) -> (
        TestRefreshScope,
        PathBuf,
        ClaudeCredentials,
        AccountScope,
        Vec<u8>,
        String,
    ) {
        let scope = TestRefreshScope::new("claude", tag);
        let path = scope.root().join("claude/.credentials.json");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        let raw = serde_json::json!({
            "claudeAiOauth": {
                "accessToken": "claude-old-access",
                "refreshToken": "claude-old-refresh",
                "expiresAt": 0
            }
        })
        .to_string();
        fs::write(&path, &raw).unwrap();
        let mut credentials =
            parse_claude_credentials_data(&raw, ClaudeCredentialSource::File).unwrap();
        credentials.scope_slot = CredentialSlot {
            semantic_source: "claude-login-file",
            canonical_location: agent_account_scope::canonical_file_location(
                &path,
                Some("claudeAiOauth"),
            )
            .unwrap(),
        };
        let location = credentials.scope_slot.canonical_location.clone();
        let old_scope = scope
            .resolve_current(
                credentials.scope_slot.semantic_source,
                &location,
                credentials.scope_marker().unwrap(),
            )
            .unwrap();
        let metadata = scope.metadata_bytes();
        (scope, path, credentials, old_scope, metadata, location)
    }

    async fn run_claude_refresh(
        scope: &TestRefreshScope,
        path: &Path,
        original: &ClaudeCredentials,
        crash: Option<RefreshCheckpoint>,
    ) -> Result<
        (
            ClaudeCredentials,
            AccountScope,
            Option<ProviderCacheBinding>,
        ),
        ProviderFetchFailure,
    > {
        let reload_path = path.to_path_buf();
        let save_path = path.to_path_buf();
        refresh_claude_credentials_with(
            original,
            scope,
            move |template| {
                let raw = fs::read_to_string(&reload_path)
                    .map_err(|error| format!("reload Claude test credentials: {error}"))?;
                let mut credentials =
                    parse_claude_credentials_data(&raw, ClaudeCredentialSource::File)?;
                credentials.scope_slot = template.scope_slot.clone();
                Ok(credentials)
            },
            claude_test_response,
            move |credentials| save_claude_credentials_to_file(credentials, &save_path),
            checkpoint_at(crash),
        )
        .await
    }

    fn stored_claude_refresh_token(path: &Path) -> Option<String> {
        parse_claude_credentials_data(
            &fs::read_to_string(path).unwrap(),
            ClaudeCredentialSource::File,
        )
        .unwrap()
        .refresh_token
    }

    #[tokio::test]
    async fn claude_file_refresh_rejects_concurrent_target_change_without_touching_b() {
        const B_BYTES: &[u8] = br#"{
  "claudeAiOauth": {
    "accessToken": "account-b-access",
    "refreshToken": "account-b-refresh",
    "expiresAt": 4102444800000
  },
  "sibling": {"writer": "b", "revision": 2}
}
"#;
        let (scope, path, original, old_scope, _, _) =
            setup_claude_refresh("claude-file-target-race");
        let reload_path = path.clone();
        let request_path = path.clone();
        let save_path = path.clone();
        let save_failed = std::rc::Rc::new(std::cell::Cell::new(false));
        let observed_save_failure = std::rc::Rc::clone(&save_failed);

        let (refreshed, scope_outcome, cache_binding) = refresh_claude_credentials_with(
            &original,
            &scope,
            move |template| {
                let raw = fs::read_to_string(&reload_path)
                    .map_err(|error| format!("reload Claude test credentials: {error}"))?;
                let mut credentials =
                    parse_claude_credentials_data(&raw, ClaudeCredentialSource::File)?;
                credentials.scope_slot = template.scope_slot.clone();
                Ok(credentials)
            },
            move |refresh_token, _attempt_binding| async move {
                assert_eq!(refresh_token, "claude-old-refresh");
                fs::write(&request_path, B_BYTES).unwrap();
                Ok(ClaudeRefreshResponse {
                    access_token: "claude-new-access".to_string(),
                    refresh_token: Some("claude-new-refresh".to_string()),
                    expires_in: 3_600,
                })
            },
            move |credentials| {
                let result = save_claude_credentials_to_file(credentials, &save_path);
                observed_save_failure.set(result.is_err());
                result
            },
            checkpoint_at(None),
        )
        .await
        .unwrap();

        assert_eq!(refreshed.access_token, "claude-new-access");
        assert_eq!(scope_outcome, old_scope);
        assert_eq!(cache_binding, None);
        assert!(save_failed.get());
        assert_eq!(fs::read(&path).unwrap(), B_BYTES);
        scope.cleanup();
    }

    #[tokio::test]
    async fn claude_file_refresh_preserves_concurrent_top_level_sibling() {
        const CURRENT_WITH_NEW_SIBLING: &str = r#"{
            "claudeAiOauth": {
                "accessToken": "claude-old-access",
                "refreshToken": "claude-old-refresh",
                "expiresAt": 0
            },
            "sibling": {"writer": "claude-cli", "revision": 2}
        }"#;
        let (scope, path, original, old_scope, _, _) =
            setup_claude_refresh("claude-file-sibling-race");
        let reload_path = path.clone();
        let request_path = path.clone();
        let save_path = path.clone();

        let (refreshed, scope_outcome, cache_binding) = refresh_claude_credentials_with(
            &original,
            &scope,
            move |template| {
                let raw = fs::read_to_string(&reload_path)
                    .map_err(|error| format!("reload Claude test credentials: {error}"))?;
                let mut credentials =
                    parse_claude_credentials_data(&raw, ClaudeCredentialSource::File)?;
                credentials.scope_slot = template.scope_slot.clone();
                Ok(credentials)
            },
            move |refresh_token, _attempt_binding| async move {
                assert_eq!(refresh_token, "claude-old-refresh");
                fs::write(&request_path, CURRENT_WITH_NEW_SIBLING).unwrap();
                Ok(ClaudeRefreshResponse {
                    access_token: "claude-new-access".to_string(),
                    refresh_token: Some("claude-new-refresh".to_string()),
                    expires_in: 3_600,
                })
            },
            move |credentials| save_claude_credentials_to_file(credentials, &save_path),
            checkpoint_at(None),
        )
        .await
        .unwrap();

        assert_eq!(refreshed.access_token, "claude-new-access");
        assert_eq!(scope_outcome, old_scope);
        assert_eq!(
            cache_binding,
            Some(ProviderCacheBinding::primary(old_scope.clone()))
        );
        let stored: Value = serde_json::from_str(&fs::read_to_string(&path).unwrap()).unwrap();
        assert_eq!(stored["claudeAiOauth"]["accessToken"], "claude-new-access");
        assert_eq!(
            stored["claudeAiOauth"]["refreshToken"],
            "claude-new-refresh"
        );
        assert_eq!(stored["sibling"]["writer"], "claude-cli");
        assert_eq!(stored["sibling"]["revision"], 2);
        scope.cleanup();
    }

    #[tokio::test]
    async fn claude_refresh_invalid_new_refresh_preserves_old_marker_and_store() {
        for (tag, refresh_value) in [
            ("claude-invalid-refresh-empty", serde_json::json!("")),
            (
                "claude-invalid-refresh-non-string",
                serde_json::json!({ "unexpected": true }),
            ),
        ] {
            let (scope, path, original, old_scope, _, location) = setup_claude_refresh(tag);
            let response: ClaudeRefreshResponse = serde_json::from_value(serde_json::json!({
                "access_token": "claude-new-access",
                "refresh_token": refresh_value,
                "expires_in": 3600
            }))
            .unwrap();
            let reload_path = path.clone();
            let save_path = path.clone();
            let (refreshed, scope_outcome, cache_binding) = refresh_claude_credentials_with(
                &original,
                &scope,
                move |template| {
                    let raw = fs::read_to_string(&reload_path)
                        .map_err(|error| format!("reload Claude test credentials: {error}"))?;
                    let mut credentials =
                        parse_claude_credentials_data(&raw, ClaudeCredentialSource::File)?;
                    credentials.scope_slot = template.scope_slot.clone();
                    Ok(credentials)
                },
                move |refresh_token, _attempt_binding| async move {
                    assert_eq!(refresh_token, "claude-old-refresh");
                    Ok(response)
                },
                move |credentials| save_claude_credentials_to_file(credentials, &save_path),
                checkpoint_at(None),
            )
            .await
            .unwrap();

            assert_eq!(refreshed.access_token, "claude-new-access");
            assert_eq!(
                refreshed.refresh_token.as_deref(),
                Some("claude-old-refresh")
            );
            assert_eq!(scope_outcome, old_scope);
            assert_eq!(
                cache_binding,
                Some(ProviderCacheBinding::primary(old_scope.clone()))
            );
            assert_eq!(
                scope
                    .resolve_current("claude-login-file", &location, b"claude-old-refresh")
                    .unwrap(),
                old_scope
            );
            assert_eq!(
                stored_claude_refresh_token(&path).as_deref(),
                Some("claude-old-refresh")
            );
            let stored: Value = serde_json::from_str(&fs::read_to_string(&path).unwrap()).unwrap();
            assert_eq!(
                stored["claudeAiOauth"]["refreshToken"],
                Value::String("claude-old-refresh".to_string())
            );
            scope.cleanup();
        }
    }

    #[tokio::test]
    async fn claude_refresh_blank_access_token_is_terminal_before_metadata_or_save() {
        let (scope, path, original, old_scope, before, location) =
            setup_claude_refresh("claude-blank-access");
        let store_before = fs::read(&path).unwrap();
        let reload_path = path.clone();
        let save_calls = std::cell::Cell::new(0);

        let failure = refresh_claude_credentials_with(
            &original,
            &scope,
            move |template| {
                let raw = fs::read_to_string(&reload_path)
                    .map_err(|error| format!("reload Claude test credentials: {error}"))?;
                let mut credentials =
                    parse_claude_credentials_data(&raw, ClaudeCredentialSource::File)?;
                credentials.scope_slot = template.scope_slot.clone();
                Ok(credentials)
            },
            |refresh_token, _attempt_binding| async move {
                assert_eq!(refresh_token, "claude-old-refresh");
                Ok(ClaudeRefreshResponse {
                    access_token: " \t\n ".to_string(),
                    refresh_token: Some("claude-new-refresh".to_string()),
                    expires_in: 3_600,
                })
            },
            |_| {
                save_calls.set(save_calls.get() + 1);
                Ok(())
            },
            checkpoint_at(None),
        )
        .await
        .unwrap_err();

        assert!(matches!(
            failure,
            ProviderFetchFailure::Terminal { ref display }
                if display == "Claude OAuth refresh response has no access token."
        ));
        assert_eq!(save_calls.get(), 0);
        assert_eq!(scope.metadata_bytes(), before);
        assert_eq!(fs::read(&path).unwrap(), store_before);
        assert_eq!(
            scope
                .resolve_current("claude-login-file", &location, b"claude-old-refresh")
                .unwrap(),
            old_scope
        );
        scope.cleanup();
    }

    #[tokio::test]
    async fn claude_refresh_crash_boundaries_and_scope_gate_use_production_sequence() {
        for boundary in [
            RefreshCheckpoint::Reloaded,
            RefreshCheckpoint::NetworkReturned,
            RefreshCheckpoint::MetadataHandled,
            RefreshCheckpoint::CredentialsPersisted,
        ] {
            let (scope, path, original, old_scope, before, location) =
                setup_claude_refresh("claude-crash");
            let failure = run_claude_refresh(&scope, &path, &original, Some(boundary))
                .await
                .unwrap_err();
            assert!(matches!(
                failure,
                ProviderFetchFailure::Terminal { ref display } if display == "injected crash"
            ));
            assert_eq!(
                stored_claude_refresh_token(&path).as_deref(),
                Some(if boundary == RefreshCheckpoint::CredentialsPersisted {
                    "claude-new-refresh"
                } else {
                    "claude-old-refresh"
                })
            );
            if matches!(
                boundary,
                RefreshCheckpoint::Reloaded | RefreshCheckpoint::NetworkReturned
            ) {
                assert_eq!(scope.metadata_bytes(), before);
            } else {
                assert_ne!(scope.metadata_bytes(), before);
                assert_eq!(
                    scope
                        .resolve_current("claude-login-file", &location, b"claude-old-refresh")
                        .unwrap(),
                    old_scope
                );
                assert_eq!(
                    scope
                        .resolve_current("claude-login-file", &location, b"claude-new-refresh")
                        .unwrap(),
                    old_scope
                );
            }
            scope.cleanup();
        }

        let (scope, path, original, old_scope, before, location) =
            setup_claude_refresh("claude-metadata-fail");
        scope.fail_metadata_save();
        let failure = run_claude_refresh(&scope, &path, &original, None)
            .await
            .unwrap_err();
        assert!(matches!(failure, ProviderFetchFailure::Terminal { .. }));
        assert_eq!(scope.metadata_bytes(), before);
        assert_eq!(
            stored_claude_refresh_token(&path).as_deref(),
            Some("claude-old-refresh")
        );
        assert_eq!(
            scope
                .resolve_current("claude-login-file", &location, b"claude-old-refresh")
                .unwrap(),
            old_scope
        );
        scope.cleanup();

        let (scope, path, original, old_scope, _, location) =
            setup_claude_refresh("claude-save-fail");
        let reload_path = path.clone();
        let (refreshed, scope_outcome, cache_binding) = refresh_claude_credentials_with(
            &original,
            &scope,
            move |template| {
                let raw = fs::read_to_string(&reload_path)
                    .map_err(|error| format!("reload Claude test credentials: {error}"))?;
                let mut credentials =
                    parse_claude_credentials_data(&raw, ClaudeCredentialSource::File)?;
                credentials.scope_slot = template.scope_slot.clone();
                Ok(credentials)
            },
            claude_test_response,
            |_| Err("injected save failure".to_string()),
            checkpoint_at(None),
        )
        .await
        .unwrap();
        assert_eq!(refreshed.access_token, "claude-new-access");
        assert_eq!(scope_outcome, old_scope);
        assert_eq!(cache_binding, None);
        assert_eq!(
            stored_claude_refresh_token(&path).as_deref(),
            Some("claude-old-refresh")
        );
        assert_eq!(
            scope
                .resolve_current("claude-login-file", &location, b"claude-new-refresh")
                .unwrap(),
            old_scope
        );
        scope.cleanup();

        let (scope, path, original, old_scope, _, location) =
            setup_claude_refresh("claude-success");
        let (_, scope_outcome, cache_binding) = run_claude_refresh(&scope, &path, &original, None)
            .await
            .unwrap();
        assert_eq!(scope_outcome, old_scope);
        assert_eq!(
            cache_binding,
            Some(ProviderCacheBinding::primary(old_scope.clone()))
        );
        assert_eq!(
            scope
                .resolve_current("claude-login-file", &location, b"claude-new-refresh")
                .unwrap(),
            old_scope
        );
        scope.cleanup();
    }

    #[tokio::test]
    async fn claude_refresh_transient_uses_lock_reloaded_binding_not_outer_binding() {
        let (scope, path, mut original, inner_scope, _, location) =
            setup_claude_refresh("claude-lock-binding");
        original.refresh_token = Some("outer-refresh-a".to_string());
        let outer_scope = scope
            .resolve_current("claude-login-file", &location, b"outer-refresh-a")
            .unwrap();
        assert_ne!(outer_scope, inner_scope);
        let expected = ProviderCacheBinding::primary(inner_scope);
        let request_expected = expected.clone();
        let reload_path = path.clone();

        let failure = refresh_claude_credentials_with(
            &original,
            &scope,
            move |template| {
                let raw = fs::read_to_string(&reload_path)
                    .map_err(|error| format!("reload Claude test credentials: {error}"))?;
                let mut credentials =
                    parse_claude_credentials_data(&raw, ClaudeCredentialSource::File)?;
                credentials.scope_slot = template.scope_slot.clone();
                Ok(credentials)
            },
            move |refresh_token, attempt_binding| async move {
                assert_eq!(refresh_token, "claude-old-refresh");
                assert_eq!(attempt_binding, request_expected);
                Err(ProviderFetchFailure::transient(
                    "Claude OAuth refresh failed. Retrying automatically.",
                    Some(attempt_binding),
                    SafeTransportDiagnostic::from_facts(TransportErrorFacts::synthetic(
                        true,
                        false,
                        TransportPhase::Request,
                        None,
                    )),
                ))
            },
            |_| Ok(()),
            checkpoint_at(None),
        )
        .await
        .unwrap_err();

        match failure {
            ProviderFetchFailure::Transient {
                attempt_binding, ..
            } => assert_eq!(attempt_binding, Some(expected)),
            ProviderFetchFailure::Terminal { .. } => panic!("timeout must remain transient"),
        }
        scope.cleanup();
    }

    #[test]
    fn stage4_codex_and_claude_matrix_assigns_semantic_keys() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let rate_limit = CodexRateLimit {
            primary_window: Some(CodexWindow {
                used_percent: 8.0,
                reset_at: now.timestamp() + 18_000,
                limit_window_seconds: 18_000,
            }),
            secondary_window: Some(CodexWindow {
                used_percent: 35.0,
                reset_at: now.timestamp() + 604_800,
                limit_window_seconds: 604_800,
            }),
        };
        let codex = codex_windows(Some(&rate_limit), None, now);
        assert_eq!(
            codex[0].pace_status.window_key.as_deref(),
            Some("main.session.v1")
        );
        assert_eq!(
            codex[1].pace_status.window_key.as_deref(),
            Some("main.weekly.v1")
        );

        let claude = ClaudeUsageResponse {
            five_hour: Some(ClaudeWindow {
                utilization: Some(10.0),
                resets_at: Some("2026-07-18T00:00:00Z".to_string()),
            }),
            seven_day: Some(ClaudeWindow {
                utilization: Some(20.0),
                resets_at: Some("2026-07-19T00:00:00Z".to_string()),
            }),
            ..Default::default()
        };
        let claude = claude_windows(&claude, now);
        assert_eq!(
            claude[0].pace_status.window_key.as_deref(),
            Some("session.v1")
        );
        assert_eq!(
            claude[1].pace_status.window_key.as_deref(),
            Some("weekly.v1")
        );
        assert_eq!(claude[0].window_minutes_for_test(), Some(300));
        assert_eq!(claude[1].window_minutes_for_test(), Some(10_080));
    }

    #[test]
    fn stage4_duplicate_snapshot_rows_are_removed_before_history_and_wire() {
        let scope = TestRefreshScope::new("stage4", "duplicate-rows");
        let account_scope = scope
            .resolve_current("fixture", "duplicate", b"duplicate-marker")
            .unwrap();
        let now = 1_700_000_000;
        let reset = Utc.timestamp_opt(now + 86_400, 0).single().unwrap();
        let make_window = |label: &str, card_id: &str, window_key: &str, used: f64| {
            UsageWindow::from_provider_used_percent(
                label.to_string(),
                used,
                Some(reset),
                Utc.timestamp_opt(now, 0).single().unwrap(),
            )
            .with_identity(
                card_id,
                Some(window_key.to_string()),
                None,
                Some(DurationEvidence::contract(86_400)),
            )
        };
        let mut snapshot = AgentUsageSnapshot {
            account_key: None,
            merge_scope: None,
            client_id: "fixture".to_string(),
            source: "fixture".to_string(),
            updated_at: String::new(),
            identity: None,
            history_scope: Ok(HistoryScope::for_test(account_scope.as_str())),
            account_scope: Ok(account_scope),
            windows: vec![
                make_window("First", "shared-card.v1", "first.v1", 10.0),
                make_window("Duplicate key", "second-card.v1", "first.v1", 20.0),
                make_window("Duplicate card", "shared-card.v1", "third.v1", 30.0),
            ],
            credits: None,
            error: None,
            transport_diagnostic: None,
        };

        enrich_snapshot_with(&mut snapshot, now, |active, observations, _| {
            assert_eq!(active.len(), 1);
            assert_eq!(observations.len(), 1);
            assert_eq!(active[0].window_key, "first.v1");
            assert_eq!(observations[0].used_percent, 10.0);
            Ok(vec![Ok((HistoryOutcome::LearningDuration, None, 0))])
        });

        assert_eq!(snapshot.windows.len(), 1);
        assert_eq!(snapshot.windows[0].label_for_test(), "First");
        let wire = serde_json::to_value(&snapshot).unwrap();
        let rows = wire["windows"].as_array().unwrap();
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0]["cardId"], "shared-card.v1");
        assert_eq!(rows[0]["paceStatus"]["windowKey"], "first.v1");
        scope.cleanup();
    }

    #[test]
    fn stage4_chained_identity_collisions_keep_only_actual_uniques() {
        let scope = TestRefreshScope::new("stage4", "chained-collisions");
        let account_scope = scope
            .resolve_current("fixture", "chained", b"chained-marker")
            .unwrap();
        let now = 1_700_000_000;
        let reset = Utc.timestamp_opt(now + 86_400, 0).single().unwrap();
        let make_window = |label: &str, card_id: &str, window_key: &str, used: f64| {
            UsageWindow::from_provider_used_percent(
                label.to_string(),
                used,
                Some(reset),
                Utc.timestamp_opt(now, 0).single().unwrap(),
            )
            .with_identity(
                card_id,
                Some(window_key.to_string()),
                None,
                Some(DurationEvidence::contract(86_400)),
            )
        };
        let mut snapshot = AgentUsageSnapshot {
            account_key: None,
            merge_scope: None,
            client_id: "fixture".to_string(),
            source: "fixture".to_string(),
            updated_at: String::new(),
            identity: None,
            history_scope: Ok(HistoryScope::for_test(account_scope.as_str())),
            account_scope: Ok(account_scope),
            windows: vec![
                make_window("A/X", "a.v1", "x.v1", 10.0),
                make_window("A/Y", "a.v1", "y.v1", 20.0),
                make_window("C/Y", "c.v1", "y.v1", 30.0),
                make_window("B/X", "b.v1", "x.v1", 40.0),
                make_window("B/Z", "b.v1", "z.v1", 50.0),
            ],
            credits: None,
            error: None,
            transport_diagnostic: None,
        };

        enrich_snapshot_with(&mut snapshot, now, |active, observations, _| {
            assert_eq!(active.len(), 3);
            assert_eq!(observations.len(), 3);
            assert_eq!(active[0].window_key, "x.v1");
            assert_eq!(active[1].window_key, "y.v1");
            assert_eq!(active[2].window_key, "z.v1");
            assert_eq!(
                observations
                    .iter()
                    .map(|observation| observation.used_percent)
                    .collect::<Vec<_>>(),
                vec![10.0, 30.0, 50.0]
            );
            Ok(vec![
                Ok((HistoryOutcome::LearningDuration, None, 0)),
                Ok((HistoryOutcome::LearningDuration, None, 0)),
                Ok((HistoryOutcome::LearningDuration, None, 0)),
            ])
        });

        assert_eq!(snapshot.windows.len(), 3);
        assert_eq!(
            snapshot
                .windows
                .iter()
                .map(UsageWindow::label_for_test)
                .collect::<Vec<_>>(),
            vec!["A/X", "C/Y", "B/Z"]
        );
        let wire = serde_json::to_value(&snapshot).unwrap();
        let rows = wire["windows"].as_array().unwrap();
        assert_eq!(
            rows.iter()
                .map(|row| row["cardId"].as_str().unwrap())
                .collect::<Vec<_>>(),
            vec!["a.v1", "c.v1", "b.v1"]
        );
        assert_eq!(
            rows.iter()
                .map(|row| row["paceStatus"]["windowKey"].as_str().unwrap())
                .collect::<Vec<_>>(),
            vec!["x.v1", "y.v1", "z.v1"]
        );
        scope.cleanup();
    }

    #[test]
    fn stage4_batch_maps_results_once_without_network() {
        let scope = TestRefreshScope::new("stage4", "batch-map");
        let account_scope = scope
            .resolve_current("fixture", "batch", b"batch-marker")
            .unwrap();
        let now = 1_700_000_000;
        let reset = Utc.timestamp_opt(now + 86_400, 0).single().unwrap();
        let mut snapshot = AgentUsageSnapshot {
            account_key: None,
            merge_scope: None,
            client_id: "fixture".to_string(),
            source: "fixture".to_string(),
            updated_at: String::new(),
            identity: None,
            history_scope: Ok(HistoryScope::for_test(account_scope.as_str())),
            account_scope: Ok(account_scope),
            windows: vec![
                UsageWindow::from_provider_used_percent(
                    "First".to_string(),
                    20.0,
                    Some(reset),
                    Utc.timestamp_opt(now, 0).single().unwrap(),
                )
                .with_identity(
                    "first.v1",
                    Some("first.v1".to_string()),
                    None,
                    Some(DurationEvidence::contract(86_400)),
                ),
                UsageWindow::from_provider_used_percent(
                    "Second".to_string(),
                    40.0,
                    Some(reset),
                    Utc.timestamp_opt(now, 0).single().unwrap(),
                )
                .with_identity(
                    "second.v1",
                    Some("second.v1".to_string()),
                    None,
                    Some(DurationEvidence::contract(86_400)),
                ),
            ],
            credits: None,
            error: None,
            transport_diagnostic: None,
        };
        let calls = std::cell::Cell::new(0);
        enrich_snapshot_with(&mut snapshot, now, |active, observations, _| {
            calls.set(calls.get() + 1);
            assert_eq!(active.len(), 2);
            assert_eq!(observations.len(), 2);
            assert_eq!(active[0].window_key, "first.v1");
            assert_eq!(active[1].window_key, "second.v1");
            Ok(vec![
                Ok((HistoryOutcome::LearningDuration, None, 0)),
                Ok((
                    HistoryOutcome::Ready {
                        duration_seconds: 86_400,
                        source: DurationSource::Contract,
                        sampled: true,
                    },
                    Some(HistoricalPace {
                        expected_percent: 42.0,
                        eta_seconds: Some(900.0),
                        will_last_to_reset: false,
                        run_out_probability: Some(0.25),
                    }),
                    4,
                )),
            ])
        });
        assert_eq!(
            calls.get(),
            1,
            "one snapshot means one batch and no new request"
        );
        assert_eq!(
            snapshot.windows[0].pace_status.state,
            PaceState::LearningDuration
        );
        assert_eq!(snapshot.windows[1].pace_status.state, PaceState::Available);
        assert_eq!(snapshot.windows[1].window_minutes_for_test(), Some(1_440));
        let wire = serde_json::to_value(&snapshot).unwrap();
        assert_eq!(wire["windows"][0]["paceStatus"]["completeCycles"], 0);
        assert_eq!(wire["windows"][1]["paceStatus"]["completeCycles"], 4);
        scope.cleanup();
    }

    #[test]
    fn stage4_learning_history_uses_batch_complete_cycles() {
        let scope = TestRefreshScope::new("stage4", "learning-history");
        let account_scope = scope
            .resolve_current("fixture", "learning", b"learning-marker")
            .unwrap();
        let now = 1_700_000_000;
        let reset = Utc.timestamp_opt(now + 86_400, 0).single().unwrap();
        let mut snapshot = AgentUsageSnapshot {
            account_key: None,
            merge_scope: None,
            client_id: "fixture".to_string(),
            source: "fixture".to_string(),
            updated_at: String::new(),
            identity: None,
            history_scope: Ok(HistoryScope::for_test(account_scope.as_str())),
            account_scope: Ok(account_scope),
            windows: vec![UsageWindow::from_provider_used_percent(
                "Weekly".to_string(),
                20.0,
                Some(reset),
                Utc.timestamp_opt(now, 0).single().unwrap(),
            )
            .with_identity(
                "weekly.v1",
                Some("weekly.v1".to_string()),
                None,
                Some(DurationEvidence::contract(86_400)),
            )],
            credits: None,
            error: None,
            transport_diagnostic: None,
        };

        enrich_snapshot_with(&mut snapshot, now, |_, _, _| {
            Ok(vec![Ok((
                HistoryOutcome::Ready {
                    duration_seconds: 86_400,
                    source: DurationSource::Contract,
                    sampled: true,
                },
                None,
                2,
            ))])
        });

        assert_eq!(
            snapshot.windows[0].pace_status.state,
            PaceState::LearningHistory
        );
        assert_eq!(snapshot.windows[0].pace_status.complete_cycles, 2);
        let wire = serde_json::to_value(&snapshot).unwrap();
        assert_eq!(wire["windows"][0]["paceStatus"]["completeCycles"], 2);
        scope.cleanup();
    }

    #[test]
    fn stage4_incoherent_historical_result_is_typed_unavailable() {
        let scope = TestRefreshScope::new("stage4", "incoherent-history");
        let account_scope = scope
            .resolve_current("fixture", "incoherent", b"incoherent-marker")
            .unwrap();
        let now = 1_700_000_000;
        let reset = Utc.timestamp_opt(now + 86_400, 0).single().unwrap();
        let mut snapshot = AgentUsageSnapshot {
            account_key: None,
            merge_scope: None,
            client_id: "fixture".to_string(),
            source: "fixture".to_string(),
            updated_at: String::new(),
            identity: None,
            history_scope: Ok(HistoryScope::for_test(account_scope.as_str())),
            account_scope: Ok(account_scope),
            windows: vec![UsageWindow::from_provider_used_percent(
                "Weekly".to_string(),
                20.0,
                Some(reset),
                Utc.timestamp_opt(now, 0).single().unwrap(),
            )
            .with_identity(
                "weekly.v1",
                Some("weekly.v1".to_string()),
                None,
                Some(DurationEvidence::contract(86_400)),
            )],
            credits: None,
            error: None,
            transport_diagnostic: None,
        };

        enrich_snapshot_with(&mut snapshot, now, |_, _, _| {
            Ok(vec![Ok((
                HistoryOutcome::Ready {
                    duration_seconds: 86_400,
                    source: DurationSource::Contract,
                    sampled: true,
                },
                Some(HistoricalPace {
                    expected_percent: 42.0,
                    eta_seconds: Some(900.0),
                    will_last_to_reset: true,
                    run_out_probability: Some(0.25),
                }),
                4,
            ))])
        });

        assert_eq!(
            snapshot.windows[0].pace_status.state,
            PaceState::Unavailable
        );
        assert_eq!(
            snapshot.windows[0].pace_status.reason.as_deref(),
            Some("history")
        );
        let wire = serde_json::to_value(&snapshot).unwrap();
        assert_eq!(wire["windows"][0]["paceStatus"]["state"], "unavailable");
        assert!(wire["windows"][0].get("historicalPace").is_none());
        scope.cleanup();
    }

    #[test]
    fn stage4_historical_eta_and_will_last_are_exactly_coherent() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let reset = now + chrono::Duration::days(1);
        let base =
            UsageWindow::from_provider_used_percent("Daily".to_string(), 30.0, Some(reset), now)
                .with_identity(
                    "daily.v1",
                    Some("daily.v1".to_string()),
                    None,
                    Some(DurationEvidence::contract(86_400)),
                );
        let cases = [
            (
                "will-last",
                HistoricalPace {
                    expected_percent: 42.0,
                    eta_seconds: None,
                    will_last_to_reset: true,
                    run_out_probability: Some(0.1),
                },
                true,
            ),
            (
                "will-run-out",
                HistoricalPace {
                    expected_percent: 42.0,
                    eta_seconds: Some(900.0),
                    will_last_to_reset: false,
                    run_out_probability: Some(0.25),
                },
                true,
            ),
            (
                "will-last-with-eta",
                HistoricalPace {
                    expected_percent: 42.0,
                    eta_seconds: Some(900.0),
                    will_last_to_reset: true,
                    run_out_probability: Some(0.25),
                },
                false,
            ),
            (
                "will-run-out-without-eta",
                HistoricalPace {
                    expected_percent: 42.0,
                    eta_seconds: None,
                    will_last_to_reset: false,
                    run_out_probability: Some(0.25),
                },
                false,
            ),
        ];

        for (label, pace, expected) in cases {
            assert_eq!(historical_pace_is_coherent(&pace), expected, "{label}");
            let mut window = base.clone();
            window.pace_status.state = PaceState::Available;
            window.historical_pace = Some(historical_pace_payload(pace));
            assert_eq!(serde_json::to_value(&window).is_ok(), expected, "{label}");
        }
    }

    fn histid_window(used_percent: f64, reset_at: i64, sampled_at: i64) -> UsageWindow {
        UsageWindow::from_provider_used_percent(
            "Session".to_string(),
            used_percent,
            Some(Utc.timestamp_opt(reset_at, 0).single().unwrap()),
            Utc.timestamp_opt(sampled_at, 0).single().unwrap(),
        )
        .with_identity(
            "session.v1",
            Some("session.v1".to_string()),
            None,
            Some(DurationEvidence::contract(5 * 3_600)),
        )
    }

    /// A sibling application rotating the shared OAuth refresh token used to
    /// mint a fresh lineage, a fresh account scope, a fresh `SeriesKey` and
    /// therefore a history that restarts from zero (ported from macOS
    /// 76e0ff38).
    #[test]
    fn histid_a_history_survives_three_external_credential_rotations() {
        let scope = TestRefreshScope::new("claude", "histid-rotation");
        let history_path = scope
            .root()
            .join(crate::agent_quota_history::HISTORY_FILE_NAME);
        let start = 1_800_000_000_i64;
        let reset = start + 5 * 3_600;
        let mut account_scopes = Vec::new();
        let mut observed_keys = Vec::new();

        for (index, marker) in [
            b"rotation-one".as_slice(),
            b"rotation-two".as_slice(),
            b"rotation-three".as_slice(),
        ]
        .into_iter()
        .enumerate()
        {
            let account_scope = scope
                .resolve_current("fixture", "histid-rotation", marker)
                .unwrap();
            let history_scope = scope.resolve_history("claude", None).unwrap();
            account_scopes.push(account_scope.as_str().to_string());
            let sampled_at = start + index as i64 * 900;
            let mut snapshot = AgentUsageSnapshot {
                account_key: None,
                merge_scope: None,
                client_id: "claude".to_string(),
                source: "oauth".to_string(),
                updated_at: String::new(),
                identity: None,
                account_scope: Ok(account_scope),
                history_scope: Ok(history_scope),
                windows: vec![histid_window(10.0 + index as f64 * 10.0, reset, sampled_at)],
                credits: None,
                error: None,
                transport_diagnostic: None,
            };
            enrich_snapshot_with(&mut snapshot, sampled_at, |active, observations, now| {
                observed_keys.extend(active.iter().cloned());
                crate::agent_quota_history::record_observations_at_path_and_evaluate(
                    active,
                    observations,
                    now,
                    &history_path,
                )
            });
        }

        // Precondition: the three markers really did fragment the account scope.
        assert_ne!(account_scopes[0], account_scopes[1]);
        assert_ne!(account_scopes[1], account_scopes[2]);
        assert_ne!(account_scopes[0], account_scopes[2]);

        assert_eq!(observed_keys.len(), 3);
        assert!(observed_keys.windows(2).all(|pair| pair[0] == pair[1]));
        assert!(account_scopes
            .iter()
            .all(|value| value != &observed_keys[0].account_scope));

        let store: Value = serde_json::from_slice(&fs::read(&history_path).unwrap()).unwrap();
        let series = store["series"].as_array().unwrap();
        assert_eq!(series.len(), 1);
        assert_eq!(series[0]["samples"].as_array().unwrap().len(), 3);
        scope.cleanup();
    }

    /// The account-scope guard is the sole suppressor. The Antigravity local
    /// route reaches here with non-empty windows and `Err(NoTrustedEvidence)`
    /// (`agent_antigravity.rs` `parse_user_status`, admitted by
    /// `apply_provider_outcome_with`), while its history scope resolves fine.
    #[test]
    fn histid_a_unverified_account_records_no_history_even_with_a_resolvable_history_scope() {
        let scope = TestRefreshScope::new("antigravity", "histid-fail-closed");
        let history_scope = scope.resolve_history("antigravity", None).unwrap();
        let start = 1_800_000_000_i64;
        let mut snapshot = AgentUsageSnapshot {
            account_key: None,
            merge_scope: None,
            client_id: "antigravity".to_string(),
            source: "cli".to_string(),
            updated_at: String::new(),
            identity: None,
            account_scope: Err(AccountScopeError::NoTrustedEvidence),
            history_scope: Ok(history_scope),
            windows: vec![histid_window(20.0, start + 5 * 3_600, start)],
            credits: None,
            error: None,
            transport_diagnostic: None,
        };
        let calls = std::cell::Cell::new(0);
        enrich_snapshot_with(&mut snapshot, start, |_, _, _| {
            calls.set(calls.get() + 1);
            Ok(Vec::new())
        });
        assert_eq!(calls.get(), 0);
        assert_eq!(
            snapshot.windows[0].pace_status.reason.as_deref(),
            Some("accountScope")
        );
        assert_eq!(
            snapshot.windows[0].pace_status.state,
            PaceState::Unavailable
        );
        scope.cleanup();
    }

    fn histid_codex_credentials(account_id: Option<&str>) -> CodexCredentials {
        CodexCredentials {
            access_token: "codex-access".to_string(),
            refresh_token: Some("codex-refresh".to_string()),
            id_token: None,
            account_id: account_id.map(str::to_string),
            last_refresh: None,
            auth_path: PathBuf::new(),
            raw_json: Value::Null,
            scope_slot: CredentialSlot {
                semantic_source: "fixture",
                canonical_location: "fixture".to_string(),
            },
        }
    }

    /// Codex resolves authoritatively and must keep doing so. Its history
    /// scope has to stay byte-equal to the account scope the cache binding
    /// corroborates on — which is also what keeps every series Windows already
    /// recorded under a ChatGPT account ID on its exact key.
    #[test]
    fn histid_a_codex_history_scope_consumes_the_authoritative_account_id() {
        let scope = TestRefreshScope::new("codex", "histid-codex");
        let resolve = |provider: &str, authoritative: Option<(AuthoritativeIdKind, &str)>| {
            scope.resolve_history(provider, authoritative)
        };

        let one = codex_history_scope_with(&histid_codex_credentials(Some("acct-1")), resolve)
            .expect("authoritative history scope");
        let expected = scope
            .resolve_authoritative("codex", AuthoritativeIdKind::OpaqueId, "acct-1")
            .unwrap();
        assert_eq!(one.as_str(), expected.as_str());

        let two = codex_history_scope_with(&histid_codex_credentials(Some("acct-2")), resolve)
            .expect("second authoritative history scope");
        assert_ne!(
            SeriesKey::new("codex", &one, "main.weekly.v1"),
            SeriesKey::new("codex", &two, "main.weekly.v1")
        );

        let constant = scope.resolve_history("codex", None).unwrap();
        for absent in [None, Some(""), Some("   ")] {
            let fallback = codex_history_scope_with(&histid_codex_credentials(absent), resolve)
                .expect("account-id-less Codex must fall back to the constant, not error");
            assert_eq!(fallback.as_str(), constant.as_str());
            assert_ne!(fallback.as_str(), one.as_str());
            assert_ne!(fallback.as_str(), two.as_str());
        }
        scope.cleanup();
    }

    /// The fold inputs name every provider, and only codex and antigravity —
    /// the two that ever keyed history on an authoritative owner ID — are
    /// restricted to their lineage scopes. The lineage set is exactly the scope
    /// the credential route handed out; the authoritative scope is not in it.
    #[test]
    fn stranded_series_fold_restricts_only_the_authoritative_providers_to_lineage_scopes() {
        let scope = TestRefreshScope::new("codex", "fold-inputs");
        let lineage = scope
            .resolve_current("fixture", "fold-inputs", b"marker")
            .unwrap();
        let authoritative = scope
            .resolve_authoritative("codex", AuthoritativeIdKind::OpaqueId, "acct-1")
            .unwrap();

        let folds = stranded_series_fold_with(
            |provider| scope.resolve_history(provider, None),
            |provider| scope.resolve_lineage(provider),
        );

        let providers = folds
            .iter()
            .map(|fold| fold.provider_id)
            .collect::<Vec<_>>();
        assert_eq!(
            providers,
            ["claude", "copilot", "grok", "codex", "antigravity"]
        );
        for fold in &folds {
            assert_eq!(
                fold.target,
                scope.resolve_history(fold.provider_id, None).unwrap()
            );
            match fold.provider_id {
                "codex" => {
                    let scopes = fold.lineage_scopes.as_ref().expect("codex is lineage-only");
                    assert_eq!(scopes.len(), 1);
                    assert!(scopes.contains(lineage.as_str()));
                    assert!(!scopes.contains(authoritative.as_str()));
                }
                "antigravity" => {
                    assert_eq!(
                        fold.lineage_scopes.as_ref().map(|scopes| scopes.len()),
                        Some(0)
                    );
                }
                _ => assert!(fold.lineage_scopes.is_none()),
            }
        }

        // A provider whose inputs cannot be resolved is left out, not guessed.
        let without_lineage = stranded_series_fold_with(
            |provider| scope.resolve_history(provider, None),
            |_| Err(AccountScopeError::MetadataCorrupt),
        );
        assert_eq!(
            without_lineage
                .iter()
                .map(|fold| fold.provider_id)
                .collect::<Vec<_>>(),
            ["claude", "copilot", "grok"]
        );
        scope.cleanup();
    }

    #[test]
    fn stage4_scope_error_is_sticky_and_skips_history() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let mut snapshot = AgentUsageSnapshot {
            account_key: None,
            merge_scope: None,
            client_id: "fixture".to_string(),
            source: "fixture".to_string(),
            updated_at: String::new(),
            identity: None,
            // `Ok` on purpose: it keeps this test proving that the account-scope
            // guard alone suppresses history.
            history_scope: Ok(HistoryScope::for_test("stage4-history-scope")),
            account_scope: Err(AccountScopeError::MetadataWrite),
            windows: vec![
                UsageWindow::from_provider_used_percent(
                    "Session".to_string(),
                    20.0,
                    Some(now + chrono::Duration::hours(5)),
                    now,
                )
                .with_identity(
                    "session.v1",
                    Some("session.v1".to_string()),
                    None,
                    Some(DurationEvidence::contract(300 * 60)),
                ),
                UsageWindow::from_provider_used_percent(
                    "Unknown".to_string(),
                    30.0,
                    Some(now + chrono::Duration::hours(5)),
                    now,
                )
                .with_identity("row.unknown.v1", None, None, None),
            ],
            credits: None,
            error: None,
            transport_diagnostic: None,
        };
        let calls = std::cell::Cell::new(0);
        enrich_snapshot_with(&mut snapshot, now.timestamp(), |_, _, _| {
            calls.set(calls.get() + 1);
            Ok(Vec::new())
        });
        assert_eq!(calls.get(), 0);
        assert_eq!(
            snapshot.windows[0].pace_status.reason.as_deref(),
            Some("accountScope")
        );
        assert_eq!(
            snapshot.windows[0].pace_status.state,
            PaceState::Unavailable
        );
        assert_eq!(
            snapshot.windows[1].pace_status.reason.as_deref(),
            Some("windowIdentity")
        );
        assert!(snapshot.windows[1].pace_status.window_key.is_none());
        assert!(serde_json::to_value(&snapshot).is_ok());
    }

    #[test]
    fn stage4_wire_rejects_internal_nested_drift_and_preserves_observed_learning() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let reset = now + chrono::Duration::days(1);
        let base =
            UsageWindow::from_provider_used_percent("Daily".to_string(), 30.0, Some(reset), now)
                .with_identity(
                    "daily.v1",
                    Some("daily.v1".to_string()),
                    None,
                    Some(DurationEvidence::contract(86_400)),
                );

        let mut key_drift = base.clone();
        key_drift.pace_status.window_key = Some("other.v1".to_string());
        assert!(serde_json::to_value(&key_drift).is_err());

        let mut duration_drift = base.clone();
        duration_drift.pace_status.duration_seconds = Some(3_600);
        assert!(serde_json::to_value(&duration_drift).is_err());

        let mut source_drift = base.clone();
        source_drift.pace_status.duration_source = Some(DurationSource::Provider);
        assert!(serde_json::to_value(&source_drift).is_err());

        let mut minutes_drift = base.clone();
        minutes_drift.window_minutes = Some(1);
        assert!(serde_json::to_value(&minutes_drift).is_err());

        let mut learning =
            UsageWindow::from_provider_used_percent("Learning".to_string(), 30.0, Some(reset), now)
                .with_identity("learning.v1", Some("learning.v1".to_string()), None, None);
        learning.duration_source = Some(DurationSource::Observed);
        learning.pace_status.duration_source = Some(DurationSource::Observed);
        let wire = serde_json::to_value(&learning).unwrap();
        assert_eq!(wire["paceStatus"]["state"], "learningDuration");
        assert_eq!(wire["paceStatus"]["durationSource"], "observed");
        assert!(wire["paceStatus"].get("durationSeconds").is_none());
    }

    #[test]
    fn stage4_wire_rejects_available_without_historical_pace() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let mut window = UsageWindow::from_provider_used_percent(
            "Weekly".to_string(),
            30.0,
            Some(now + chrono::Duration::days(7)),
            now,
        )
        .with_identity(
            "weekly.v1",
            Some("weekly.v1".to_string()),
            None,
            Some(DurationEvidence::contract(7 * 24 * 60 * 60)),
        );
        window.pace_status.state = PaceState::Available;
        window.historical_pace = None;
        assert!(serde_json::to_value(&window).is_err());
    }

    /// Both scopes cross the wire in the same two-case shape — `{ "scope" }` or
    /// `{ "error" }`, never both — because the C# window card joins a live
    /// agent to its stored series on `historyScope.scope`, and a shape the
    /// decoder does not recognise would arrive as null and silently fall back
    /// to first-wins. The Err half is also pinned inside
    /// `provider_quota_pace_v3_fixture_locks_production_serializer`.
    #[test]
    fn account_and_history_scopes_serialize_as_two_case_objects() {
        let scope = TestRefreshScope::new("claude", "scope-wire");
        let account_scope = scope
            .resolve_current("fixture", "scope-wire", b"marker")
            .unwrap();
        let history_scope = scope.resolve_history("claude", None).unwrap();
        let account_value = account_scope.as_str().to_string();
        let history_value = history_scope.as_str().to_string();
        assert_ne!(account_value, history_value);
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let mut snapshot = cache_test_snapshot("claude", Ok(account_scope), now);
        snapshot.history_scope = Ok(history_scope);

        let ok = serde_json::to_value(&snapshot).unwrap();
        assert_eq!(
            ok["accountScope"],
            serde_json::json!({ "scope": account_value })
        );
        assert_eq!(
            ok["historyScope"],
            serde_json::json!({ "scope": history_value })
        );

        snapshot.account_scope = Err(AccountScopeError::MetadataWrite);
        snapshot.history_scope = Err(AccountScopeError::InvalidInstallationKey);
        let err = serde_json::to_value(&snapshot).unwrap();
        assert_eq!(
            err["accountScope"],
            serde_json::json!({ "error": "account-scope metadata could not be saved" })
        );
        assert_eq!(
            err["historyScope"],
            serde_json::json!({ "error": "installation key failed validation" })
        );
        scope.cleanup();
    }

    #[test]
    fn provider_quota_pace_v3_fixture_locks_production_serializer() {
        fn window(
            card_id: &str,
            label: &str,
            used_percent: f64,
            resets_at: Option<&str>,
            window_key: Option<&str>,
            state: PaceState,
            duration_seconds: Option<i64>,
            duration_source: Option<DurationSource>,
            complete_cycles: usize,
            reason: Option<&str>,
            historical_pace: Option<HistoricalPacePayload>,
        ) -> UsageWindow {
            UsageWindow {
                card_id: card_id.to_string(),
                label: label.to_string(),
                used_percent,
                remaining_percent: 100.0 - used_percent,
                resets_at: resets_at.map(|value| value.to_string()),
                reset_text: None,
                window_minutes: duration_seconds.map(|seconds| seconds / 60),
                window_key: window_key.map(|value| value.to_string()),
                duration_seconds,
                duration_source,
                provider_duration: None,
                contract_duration: None,
                pace_status: PaceStatusPayload {
                    state,
                    window_key: window_key.map(|value| value.to_string()),
                    duration_seconds,
                    duration_source,
                    complete_cycles,
                    reason: reason.map(|value| value.to_string()),
                },
                historical_pace,
                model_scope: None,
            }
        }

        let payload = AgentUsagePayload {
            generated_at: "2026-07-10T12:00:00.000Z".to_string(),
            publication_generation: 1,
            agents: vec![AgentUsageSnapshot {
                account_key: None,
                merge_scope: None,
                client_id: "provider-fixture.invalid".to_string(),
                source: "fixture.invalid".to_string(),
                updated_at: "2026-07-10T12:00:00.000Z".to_string(),
                identity: None,
                account_scope: Err(AccountScopeError::NoTrustedEvidence),
                history_scope: Err(AccountScopeError::NoTrustedEvidence),
                windows: vec![
                    window(
                        "ahead.invalid",
                        "Ahead quota",
                        72.0,
                        Some("2026-07-10T15:00:00Z"),
                        Some("quota.ahead.invalid"),
                        PaceState::Available,
                        Some(18_000),
                        Some(DurationSource::Provider),
                        5,
                        None,
                        Some(HistoricalPacePayload {
                            expected_used_percent: 32.0,
                            eta_seconds: Some(3_600.0),
                            will_last_to_reset: false,
                            run_out_probability: Some(0.75),
                        }),
                    ),
                    window(
                        "behind.invalid",
                        "Behind quota",
                        28.0,
                        Some("2026-07-15T12:00:00Z"),
                        Some("quota.behind.invalid"),
                        PaceState::Available,
                        Some(604_800),
                        Some(DurationSource::Contract),
                        7,
                        None,
                        Some(HistoricalPacePayload {
                            expected_used_percent: 56.0,
                            eta_seconds: None,
                            will_last_to_reset: true,
                            run_out_probability: Some(0.2),
                        }),
                    ),
                    window(
                        "learning-history.invalid",
                        "Learning history",
                        40.0,
                        Some("2026-07-10T15:00:00Z"),
                        Some("quota.learning-history.invalid"),
                        PaceState::LearningHistory,
                        Some(18_000),
                        Some(DurationSource::Provider),
                        2,
                        None,
                        None,
                    ),
                    window(
                        "learning-duration.invalid",
                        "Learning duration",
                        40.0,
                        Some("2026-07-10T15:00:00Z"),
                        Some("quota.learning-duration.invalid"),
                        PaceState::LearningDuration,
                        None,
                        Some(DurationSource::Observed),
                        0,
                        None,
                        None,
                    ),
                    window(
                        "missing-reset.invalid",
                        "Missing reset",
                        50.0,
                        None,
                        Some("quota.missing-reset.invalid"),
                        PaceState::Unavailable,
                        None,
                        None,
                        0,
                        Some("missingReset"),
                        None,
                    ),
                    window(
                        "shared-first.invalid",
                        "Shared label",
                        10.0,
                        Some("2026-07-10T15:00:00Z"),
                        Some("quota.shared-first.invalid"),
                        PaceState::LearningHistory,
                        Some(18_000),
                        Some(DurationSource::Provider),
                        2,
                        None,
                        None,
                    ),
                    window(
                        "shared-second.invalid",
                        "Shared label",
                        20.0,
                        Some("2026-07-10T15:00:00Z"),
                        Some("quota.shared-second.invalid"),
                        PaceState::LearningHistory,
                        Some(18_000),
                        Some(DurationSource::Provider),
                        2,
                        None,
                        None,
                    ),
                ],
                credits: None,
                error: None,
                transport_diagnostic: None,
            }],
            opencode_subscriptions: Vec::new(),
        };

        let fixture_path = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../Fixtures/CrossCheck/provider-quota-pace-v3.json");
        let fixture: Value = serde_json::from_str(
            &fs::read_to_string(&fixture_path)
                .unwrap_or_else(|error| panic!("read {}: {error}", fixture_path.display())),
        )
        .unwrap_or_else(|error| panic!("decode {}: {error}", fixture_path.display()));
        assert_eq!(fixture["schemaVersion"], 3);
        let mut serialized = serde_json::to_value(payload).unwrap();
        assert_eq!(serialized["publicationGeneration"], 1);
        serialized
            .as_object_mut()
            .expect("payload serializes as an object")
            .remove("publicationGeneration");
        // Asserted before it is stripped, the way `publicationGeneration` is
        // above. Stripping a field without first stating its shape leaves the
        // wire contract untested on this side, and this particular field fails
        // silently: `AgentUsageSnapshot.AccountScope` is nullable in C#, so a
        // shape the decoder does not recognise arrives as null and the window
        // card falls back to picking whichever stored series comes first —
        // which is the defect this field was added to remove.
        // This fixture's snapshot resolves no credential, so it exercises the
        // Err half. The Ok half is pinned by
        // `account_and_history_scopes_serialize_as_two_case_objects`.
        assert_eq!(
            serialized["agents"][0]["accountScope"],
            serde_json::json!({ "error": "no trusted account evidence" })
        );
        assert_eq!(
            serialized["agents"][0]["historyScope"],
            serde_json::json!({ "error": "no trusted account evidence" })
        );

        // `accountScope` is a Windows-only wire addition (the account-dimension
        // fix, PR #81 structural review): it did not exist when this fixture
        // was authored as the Swift↔C# cross-check oracle input, and adding it
        // to the shared fixture would require re-baselining that byte-for-byte
        // parity contract (crosscheck/README.md's pinned hashes) for a field
        // the Swift side has no reason to emit here. Stripped from the
        // comparison the same way `publicationGeneration` is above, rather
        // than folded into the shared fixture.
        if let Some(agents) = serialized.get_mut("agents").and_then(Value::as_array_mut) {
            for agent in agents {
                if let Some(object) = agent.as_object_mut() {
                    object.remove("accountScope");
                    // Windows-only for the same reason, added with HistoryScope.
                    object.remove("historyScope");
                }
            }
        }
        assert_eq!(fixture["payload"], serialized);
    }
}

/// W5a: the Kiro card through its production entry (`fetch_kiro_with`) and the
/// provider join (`run_with`). Every dependency is test-owned: the credential
/// is a fixture file, account-scope rows go to a `TestRefreshScope` temp root,
/// history goes to a recorder that opens no store, and the request goes to a
/// loopback mock. Nothing here reads the real profile or the network.
#[cfg(test)]
mod kiro_tests {
    use super::kiro_deps::{Enrich, KiroLoad, ResolveCredential, ResolveHistoryScope};
    use super::*;
    use crate::agent_account_scope::test_support::TestRefreshScope;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::Arc;

    const SENTINEL_TOKEN: &str = "kiro-sentinel-access-7f3a91";
    const SENTINEL_REFRESH: &str = "kiro-sentinel-refresh-c04e5d";
    const FIXTURE_ARN: &str = "arn:aws:codewhisperer:us-east-1:000000000000:profile/FIXTURE";
    const USAGE_OK: &str = r#"{
        "subscriptionInfo": {"subscriptionTitle": "KIRO PRO"},
        "nextDateReset": 4000000000,
        "usageBreakdownList": [{"currentUsageWithPrecision": 25.0, "usageLimitWithPrecision": 100.0}]
    }"#;

    /// Test-owned values behind one `KiroDeps`.
    struct Harness {
        dir: PathBuf,
        scope: Arc<TestRefreshScope>,
        cache: Mutex<ProviderLastGoodCache>,
        enrich_calls: Arc<AtomicUsize>,
        recorded_observations: Arc<AtomicUsize>,
        load: Box<dyn Fn(DateTime<Utc>) -> KiroLoad>,
        resolve_credential: Box<ResolveCredential>,
        resolve_history: Box<ResolveHistoryScope>,
        enrich: Box<Enrich>,
        url: String,
    }

    impl Harness {
        /// `token_file`: the fixture's contents, or `None` for no file at all.
        fn new(tag: &str, token_file: Option<String>, url: String) -> Self {
            let dir = std::env::temp_dir().join(format!(
                "tb-kiro-fetch-{tag}-{}-{}",
                std::process::id(),
                Utc::now().timestamp_nanos_opt().unwrap()
            ));
            fs::create_dir_all(&dir).unwrap();
            // Written at a literal path, read back through the production
            // `ide_token_path_from(home)`.
            if let Some(contents) = token_file {
                let cache = dir.join(".aws").join("sso").join("cache");
                fs::create_dir_all(&cache).unwrap();
                fs::write(cache.join("kiro-auth-token.json"), contents).unwrap();
            }
            let home = dir.clone();
            let scope = Arc::new(TestRefreshScope::new("kiro", tag));
            let enrich_calls = Arc::new(AtomicUsize::new(0));
            let recorded_observations = Arc::new(AtomicUsize::new(0));
            let credential_scope = Arc::clone(&scope);
            let history_scope = Arc::clone(&scope);
            let calls = Arc::clone(&enrich_calls);
            let observed = Arc::clone(&recorded_observations);
            Self {
                dir,
                cache: Mutex::new(ProviderLastGoodCache::default()),
                enrich_calls,
                recorded_observations,
                load: Box::new(move |now| {
                    let load = crate::kiro_integrations::kiro_credential_from_ide_home(&home, now);
                    Box::pin(async move { load })
                }),
                resolve_credential: Box::new(move |provider, source, location, marker| {
                    assert_eq!(provider, "kiro");
                    credential_scope.resolve_current(source, location, marker)
                }),
                resolve_history: Box::new(move |provider, authoritative| {
                    history_scope.resolve_history(provider, authoritative)
                }),
                enrich: Box::new(move |snapshot, now| {
                    calls.fetch_add(1, Ordering::SeqCst);
                    enrich_snapshot_with(snapshot, now, |_, observations, _| {
                        observed.fetch_add(observations.len(), Ordering::SeqCst);
                        Ok(vec![])
                    });
                }),
                scope,
                url,
            }
        }

        fn deps(&self) -> KiroDeps<'_> {
            KiroDeps::for_test(
                &*self.load,
                &*self.resolve_credential,
                &*self.resolve_history,
                &self.url,
                &self.cache,
                &*self.enrich,
            )
        }

        fn enrich_calls(&self) -> usize {
            self.enrich_calls.load(Ordering::SeqCst)
        }
    }

    impl Drop for Harness {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.dir);
            self.scope.cleanup();
        }
    }

    fn fixture_token_file() -> String {
        serde_json::json!({
            "accessToken": SENTINEL_TOKEN,
            "refreshToken": SENTINEL_REFRESH,
            "profileArn": FIXTURE_ARN,
            "expiresAt": "2099-01-01T00:00:00Z",
            "authMethod": "social",
            "provider": "Fixture"
        })
        .to_string()
    }

    /// Loopback stand-in for `/getUsageLimits`: answers one connection per
    /// entry of `responses`, in order, and returns every request head it saw —
    /// including any connection beyond the scripted ones.
    async fn usage_mock(
        responses: Vec<(u16, &'static str)>,
    ) -> (String, tokio::task::JoinHandle<Vec<String>>) {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://{}/getUsageLimits", listener.local_addr().unwrap());
        let server = tokio::spawn(async move {
            async fn read_head(stream: &mut tokio::net::TcpStream) -> String {
                let mut head = Vec::new();
                let mut buf = [0_u8; 1024];
                while !head.windows(4).any(|w| w == b"\r\n\r\n") {
                    let read = stream.read(&mut buf).await.unwrap();
                    if read == 0 {
                        break;
                    }
                    head.extend_from_slice(&buf[..read]);
                }
                String::from_utf8(head).unwrap()
            }
            let mut heads = Vec::new();
            for (status, body) in responses {
                let (mut stream, _) = listener.accept().await.unwrap();
                heads.push(read_head(&mut stream).await);
                let response = format!(
                    "HTTP/1.1 {status} Fixture\r\ncontent-type: application/json\r\ncontent-length: {}\r\nconnection: close\r\n\r\n{body}",
                    body.len()
                );
                stream.write_all(response.as_bytes()).await.unwrap();
            }
            if let Ok(Ok((mut stream, _))) =
                tokio::time::timeout(std::time::Duration::from_millis(200), listener.accept()).await
            {
                heads.push(read_head(&mut stream).await);
            }
            heads
        });
        (url, server)
    }

    fn expected_query() -> String {
        let mut url = reqwest::Url::parse("http://fixture/getUsageLimits").unwrap();
        url.query_pairs_mut().append_pair("profileArn", FIXTURE_ARN);
        url.query().unwrap().to_string()
    }

    /// A: fixture token file -> one `kiro` card; the mock saw exactly one GET
    /// carrying the fixture's token and ARN and nothing else of the fixture.
    /// F: account-scope rows land in the temp root; enrich ran exactly once.
    #[tokio::test]
    async fn fetch_kiro_with_fixture_sends_only_token_and_arn_to_the_usage_url() {
        let (url, server) = usage_mock(vec![(200, USAGE_OK)]).await;
        let harness = Harness::new("fixture", Some(fixture_token_file()), url);

        let snapshot = fetch_kiro_with(&harness.deps())
            .await
            .expect("a signed-in Kiro yields a card");
        assert_eq!(snapshot.client_id, "kiro");
        assert_eq!(snapshot.source, "oauth");
        assert!(snapshot.error.is_none(), "{:?}", snapshot.error);
        assert_eq!(snapshot.windows.len(), 1);
        assert!(snapshot.account_scope.is_ok());
        assert_eq!(
            snapshot.history_scope.as_ref().ok(),
            harness.scope.resolve_history("kiro", None).as_ref().ok()
        );
        assert_eq!(
            snapshot.identity.as_ref().and_then(|i| i.plan.as_deref()),
            Some("KIRO PRO")
        );

        let heads = server.await.unwrap();
        assert_eq!(heads.len(), 1, "exactly one request: {heads:?}");
        let head = &heads[0];
        let request_line = head.lines().next().unwrap();
        assert_eq!(
            request_line,
            format!("GET /getUsageLimits?{} HTTP/1.1", expected_query())
        );
        let authorization: Vec<&str> = head
            .lines()
            .filter(|line| line.to_ascii_lowercase().starts_with("authorization:"))
            .collect();
        assert_eq!(
            authorization,
            vec![format!("authorization: Bearer {SENTINEL_TOKEN}").as_str()]
        );
        for other in [SENTINEL_REFRESH, "2099-01-01", "social", "Fixture"] {
            assert!(!head.contains(other), "{other} leaked into {head}");
        }

        // F: the binding was written under the temp root, as HMACs only.
        let metadata = harness.scope.metadata_bytes();
        assert!(!metadata.is_empty());
        let metadata = String::from_utf8_lossy(&metadata);
        assert!(!metadata.contains(SENTINEL_TOKEN));
        assert_eq!(harness.enrich_calls(), 1);
        assert_eq!(harness.recorded_observations.load(Ordering::SeqCst), 1);
    }

    /// B + F: no token file is Absent (no card, no request, no enrich).
    #[tokio::test]
    async fn fetch_kiro_with_no_token_file_is_absent() {
        let (url, server) = usage_mock(vec![]).await;
        let harness = Harness::new("absent", None, url);
        assert!(fetch_kiro_with(&harness.deps()).await.is_none());
        assert_eq!(harness.enrich_calls(), 0);
        assert!(server.await.unwrap().is_empty(), "Absent sends nothing");
    }

    /// C (R5-1): success, then a 503 for the same token -> the cached window
    /// is replayed with the error, instead of a bare error card. Fails if
    /// `usable_success` does not admit "kiro".
    #[tokio::test]
    async fn kiro_transient_after_success_replays_the_cached_window() {
        let (url, server) = usage_mock(vec![(200, USAGE_OK), (503, "")]).await;
        let harness = Harness::new("transient", Some(fixture_token_file()), url);

        let fresh = fetch_kiro_with(&harness.deps()).await.unwrap();
        assert_eq!(fresh.windows.len(), 1);
        assert!(fresh.error.is_none());

        let fallback = fetch_kiro_with(&harness.deps()).await.unwrap();
        assert_eq!(
            fallback.windows.len(),
            1,
            "a kiro transient must replay the cached window"
        );
        assert_eq!(
            fallback.error.as_deref(),
            Some("Kiro usage request failed. Retrying automatically.")
        );
        assert!(fallback.transport_diagnostic.is_some());
        assert_eq!(server.await.unwrap().len(), 2);
        // F: the transient replays the cache without enriching again.
        assert_eq!(harness.enrich_calls(), 1);
    }

    /// D: the access token never reaches the published snapshot, on success or
    /// on either kind of failure.
    #[tokio::test]
    async fn kiro_token_never_appears_in_snapshot_or_error_text() {
        let (url, server) = usage_mock(vec![(200, USAGE_OK), (401, ""), (503, "")]).await;
        let harness = Harness::new("sentinel", Some(fixture_token_file()), url);
        let mut displays = Vec::new();
        for _ in 0..3 {
            let snapshot = fetch_kiro_with(&harness.deps()).await.unwrap();
            let json = serde_json::to_string(&snapshot).unwrap();
            for secret in [SENTINEL_TOKEN, SENTINEL_REFRESH] {
                assert!(!json.contains(secret), "{secret} in {json}");
            }
            displays.push(snapshot.error.clone());
        }
        assert_eq!(
            displays,
            vec![
                None,
                Some("Kiro credentials expired or lack access.".to_string()),
                // The 401 cleared the cache, so this transient has no fallback.
                Some("Kiro usage request failed. Retrying automatically.".to_string()),
            ]
        );
        assert_eq!(server.await.unwrap().len(), 3);
        assert_eq!(harness.enrich_calls(), 1);
    }

    fn stub(client_id: &str) -> AgentUsageSnapshot {
        empty_error_snapshot(client_id, "stub", Utc::now(), "stub".to_string(), None)
    }

    /// E: the join publishes every provider's card, in order, kiro after grok,
    /// and the subscriptions come from the fetcher set rather than the profile.
    #[tokio::test]
    async fn run_joins_every_quota_provider() {
        let stubs = Fetchers {
            codex: || Box::pin(async { stub("codex") }),
            claude_accounts: || Box::pin(async { vec![stub("claude"), stub("claude")] }),
            antigravity: || Box::pin(async { stub("antigravity") }),
            copilot: || Box::pin(async { Some(stub("copilot")) }),
            grok: || Box::pin(async { Some(stub("grok")) }),
            kiro: || Box::pin(async { Some(stub("kiro")) }),
            opencode_go: || Box::pin(async { Some(stub("opencode")) }),
            grok_bot: || Box::pin(async { Some(stub("grok-bot")) }),
            subscriptions: || vec!["StubSubscription".to_string()],
        };
        let payload = run_with(&stubs, 7).await;
        let ids: Vec<&str> = payload
            .agents
            .iter()
            .map(|agent| agent.client_id.as_str())
            .collect();
        assert_eq!(
            ids,
            [
                "codex",
                "claude",
                "claude",
                "antigravity",
                "copilot",
                "grok",
                "kiro",
                "opencode",
                "grok-bot"
            ]
        );
        assert_eq!(payload.opencode_subscriptions, ["StubSubscription"]);
        assert_eq!(payload.publication_generation, 7);
    }
}

/// W5b: the OpenCode Go card through its production entry
/// (`fetch_opencode_go_with`). Same harness shape as `kiro_tests`: the key is a
/// fixture `auth.json` read by the production loader, account-scope rows go to
/// a `TestRefreshScope` temp root, history goes to a recorder that opens no
/// store, and the request goes to a loopback mock. Nothing here reads the real
/// profile or the network.
#[cfg(test)]
mod opencode_go_tests {
    use super::kiro_deps::{Enrich, ResolveCredential, ResolveHistoryScope};
    use super::*;
    use crate::agent_account_scope::test_support::TestRefreshScope;
    use crate::opencode_integrations::OpenCodeGoCredentialLoad;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::Arc;

    const SENTINEL_KEY: &str = "opencode-go-sentinel-key-5be2d8";
    /// Another provider's entry in the same `auth.json`; it must never leave
    /// the file through this card.
    const COPILOT_SENTINEL: &str = "copilot-sentinel-refresh-91aa0c";
    const USAGE_OK: &str = r#"{
        "usage": {
            "rolling": {"percent": 47.0, "resetsAt": "2099-01-01T05:00:00Z"},
            "weekly":  {"percent": 63.0, "resetsAt": "2099-01-07T00:00:00Z"},
            "monthly": {"percent": 28.0, "resetsAt": "2099-02-01T00:00:00Z"}
        }
    }"#;

    /// Test-owned values behind one `OpenCodeGoDeps`.
    struct Harness {
        dir: PathBuf,
        scope: Arc<TestRefreshScope>,
        cache: Mutex<ProviderLastGoodCache>,
        enrich_calls: Arc<AtomicUsize>,
        recorded_observations: Arc<AtomicUsize>,
        load: Box<dyn Fn() -> OpenCodeGoCredentialLoad>,
        resolve_credential: Box<ResolveCredential>,
        resolve_history: Box<ResolveHistoryScope>,
        enrich: Box<Enrich>,
        url: String,
    }

    impl Harness {
        /// `auth_json`: the fixture's contents, or `None` for no file at all.
        fn new(tag: &str, auth_json: Option<String>, url: String) -> Self {
            let dir = std::env::temp_dir().join(format!(
                "tb-opencode-go-fetch-{tag}-{}-{}",
                std::process::id(),
                Utc::now().timestamp_nanos_opt().unwrap()
            ));
            fs::create_dir_all(&dir).unwrap();
            let auth = dir.join("auth.json");
            if let Some(contents) = auth_json {
                fs::write(&auth, contents).unwrap();
            }
            let scope = Arc::new(TestRefreshScope::new("opencode", tag));
            let enrich_calls = Arc::new(AtomicUsize::new(0));
            let recorded_observations = Arc::new(AtomicUsize::new(0));
            let credential_scope = Arc::clone(&scope);
            let history_scope = Arc::clone(&scope);
            let calls = Arc::clone(&enrich_calls);
            let observed = Arc::clone(&recorded_observations);
            Self {
                dir,
                cache: Mutex::new(ProviderLastGoodCache::default()),
                enrich_calls,
                recorded_observations,
                load: Box::new(move || {
                    crate::opencode_integrations::opencode_go_credential_at(&auth)
                }),
                resolve_credential: Box::new(move |provider, source, location, marker| {
                    assert_eq!(provider, "opencode");
                    credential_scope.resolve_current(source, location, marker)
                }),
                resolve_history: Box::new(move |provider, authoritative| {
                    history_scope.resolve_history(provider, authoritative)
                }),
                enrich: Box::new(move |snapshot, now| {
                    calls.fetch_add(1, Ordering::SeqCst);
                    enrich_snapshot_with(snapshot, now, |_, observations, _| {
                        observed.fetch_add(observations.len(), Ordering::SeqCst);
                        Ok(vec![])
                    });
                }),
                scope,
                url,
            }
        }

        fn deps(&self) -> OpenCodeGoDeps<'_> {
            OpenCodeGoDeps::for_test(
                &*self.load,
                &*self.resolve_credential,
                &*self.resolve_history,
                &self.url,
                &self.cache,
                &*self.enrich,
            )
        }

        fn enrich_calls(&self) -> usize {
            self.enrich_calls.load(Ordering::SeqCst)
        }
    }

    impl Drop for Harness {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.dir);
            self.scope.cleanup();
        }
    }

    fn fixture_auth_json() -> String {
        serde_json::json!({
            "opencode-go": { "type": "api", "key": SENTINEL_KEY },
            "github-copilot": {
                "type": "oauth",
                "refresh": COPILOT_SENTINEL,
                "access": COPILOT_SENTINEL,
                "expires": 0
            }
        })
        .to_string()
    }

    /// Loopback stand-in for `/zen/go/v1/usage`: answers one connection per
    /// entry of `responses`, in order, and returns every request head it saw —
    /// including any connection beyond the scripted ones.
    async fn usage_mock(
        responses: Vec<(u16, &'static str)>,
    ) -> (String, tokio::task::JoinHandle<Vec<String>>) {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://{}/zen/go/v1/usage", listener.local_addr().unwrap());
        let server = tokio::spawn(async move {
            async fn read_head(stream: &mut tokio::net::TcpStream) -> String {
                let mut head = Vec::new();
                let mut buf = [0_u8; 1024];
                while !head.windows(4).any(|w| w == b"\r\n\r\n") {
                    let read = stream.read(&mut buf).await.unwrap();
                    if read == 0 {
                        break;
                    }
                    head.extend_from_slice(&buf[..read]);
                }
                String::from_utf8(head).unwrap()
            }
            let mut heads = Vec::new();
            for (status, body) in responses {
                let (mut stream, _) = listener.accept().await.unwrap();
                heads.push(read_head(&mut stream).await);
                let response = format!(
                    "HTTP/1.1 {status} Fixture\r\ncontent-type: application/json\r\ncontent-length: {}\r\nconnection: close\r\n\r\n{body}",
                    body.len()
                );
                stream.write_all(response.as_bytes()).await.unwrap();
            }
            if let Ok(Ok((mut stream, _))) =
                tokio::time::timeout(std::time::Duration::from_millis(200), listener.accept()).await
            {
                heads.push(read_head(&mut stream).await);
            }
            heads
        });
        (url, server)
    }

    /// A: fixture `auth.json` -> one `opencode` card; the mock saw exactly one
    /// GET carrying the fixture's Go key and nothing else of the file (the
    /// Copilot entry beside it included).
    /// F: account-scope rows land in the temp root; enrich ran exactly once.
    #[tokio::test]
    async fn fetch_opencode_go_with_fixture_sends_only_the_key_to_the_usage_url() {
        let (url, server) = usage_mock(vec![(200, USAGE_OK)]).await;
        let harness = Harness::new("fixture", Some(fixture_auth_json()), url);

        let snapshot = fetch_opencode_go_with(&harness.deps())
            .await
            .expect("a signed-in OpenCode Go yields a card");
        assert_eq!(snapshot.client_id, "opencode");
        assert_eq!(snapshot.source, "api");
        assert!(snapshot.error.is_none(), "{:?}", snapshot.error);
        let labels: Vec<_> = snapshot
            .windows
            .iter()
            .map(|w| w.label_for_test())
            .collect();
        assert_eq!(labels, ["Rolling", "Weekly", "Monthly"]);
        assert!(snapshot.account_scope.is_ok());
        assert_eq!(
            snapshot.history_scope.as_ref().ok(),
            harness
                .scope
                .resolve_history("opencode", None)
                .as_ref()
                .ok()
        );
        assert_eq!(
            snapshot.identity.as_ref().and_then(|i| i.plan.as_deref()),
            Some("Go")
        );

        let heads = server.await.unwrap();
        assert_eq!(heads.len(), 1, "exactly one request: {heads:?}");
        let head = &heads[0];
        assert_eq!(
            head.lines().next().unwrap(),
            "GET /zen/go/v1/usage HTTP/1.1"
        );
        let authorization: Vec<&str> = head
            .lines()
            .filter(|line| line.to_ascii_lowercase().starts_with("authorization:"))
            .collect();
        assert_eq!(
            authorization,
            vec![format!("authorization: Bearer {SENTINEL_KEY}").as_str()]
        );
        for other in [COPILOT_SENTINEL, "github-copilot", "oauth"] {
            assert!(!head.contains(other), "{other} leaked into {head}");
        }

        // F: the binding was written under the temp root, as HMACs only.
        let metadata = harness.scope.metadata_bytes();
        assert!(!metadata.is_empty());
        let metadata = String::from_utf8_lossy(&metadata);
        assert!(!metadata.contains(SENTINEL_KEY));
        assert!(!metadata.contains(COPILOT_SENTINEL));
        assert_eq!(harness.enrich_calls(), 1);
        assert_eq!(harness.recorded_observations.load(Ordering::SeqCst), 3);
    }

    /// B + F: no `auth.json` is Absent (no card, no request, no enrich).
    #[tokio::test]
    async fn fetch_opencode_go_with_no_auth_file_is_absent() {
        let (url, server) = usage_mock(vec![]).await;
        let harness = Harness::new("absent", None, url);
        assert!(fetch_opencode_go_with(&harness.deps()).await.is_none());
        assert_eq!(harness.enrich_calls(), 0);
        assert!(server.await.unwrap().is_empty(), "Absent sends nothing");
    }

    /// C (R5-1): success, then a 503 for the same key -> the cached windows are
    /// replayed with the error, instead of a bare error card. Fails if
    /// `usable_success` does not admit "opencode".
    #[tokio::test]
    async fn opencode_go_transient_after_success_replays_the_cached_windows() {
        let (url, server) = usage_mock(vec![(200, USAGE_OK), (503, "")]).await;
        let harness = Harness::new("transient", Some(fixture_auth_json()), url);

        let fresh = fetch_opencode_go_with(&harness.deps()).await.unwrap();
        assert_eq!(fresh.windows.len(), 3);
        assert!(fresh.error.is_none());

        let fallback = fetch_opencode_go_with(&harness.deps()).await.unwrap();
        assert_eq!(
            fallback.windows.len(),
            3,
            "an opencode transient must replay the cached windows"
        );
        assert_eq!(
            fallback.error.as_deref(),
            Some("OpenCode Go usage request failed. Retrying automatically.")
        );
        assert!(fallback.transport_diagnostic.is_some());
        assert_eq!(server.await.unwrap().len(), 2);
        // F: the transient replays the cache without enriching again.
        assert_eq!(harness.enrich_calls(), 1);
    }

    /// D: neither the Go key nor the other provider's entry in the same file
    /// reaches the published snapshot, on success or on either kind of failure.
    #[tokio::test]
    async fn opencode_go_key_never_appears_in_snapshot_or_error_text() {
        let (url, server) = usage_mock(vec![(200, USAGE_OK), (401, ""), (503, "")]).await;
        let harness = Harness::new("sentinel", Some(fixture_auth_json()), url);
        let mut displays = Vec::new();
        for _ in 0..3 {
            let snapshot = fetch_opencode_go_with(&harness.deps()).await.unwrap();
            let json = serde_json::to_string(&snapshot).unwrap();
            for secret in [SENTINEL_KEY, COPILOT_SENTINEL] {
                assert!(!json.contains(secret), "{secret} in {json}");
            }
            displays.push(snapshot.error.clone());
        }
        assert_eq!(
            displays,
            vec![
                None,
                Some("OpenCode Go API key expired or lacks access.".to_string()),
                // The 401 cleared the cache, so this transient has no fallback.
                Some("OpenCode Go usage request failed. Retrying automatically.".to_string()),
            ]
        );
        assert_eq!(server.await.unwrap().len(), 3);
        assert_eq!(harness.enrich_calls(), 1);
    }
}

/// W6a: the Grok Bot card through its production entry (`fetch_grokbot_with`).
/// Every dependency is test-owned, as in `kiro_tests`: the config root is a temp
/// dir holding a fixture Cursor `state.vscdb` (and, for the guard, a Grok Bot
/// install), account-scope rows go to a `TestRefreshScope` temp root, history
/// goes to a recorder, and the request goes to a loopback mock.
#[cfg(test)]
mod grokbot_tests {
    use super::kiro_deps::{Enrich, ResolveCredential, ResolveHistoryScope};
    use super::*;
    use crate::agent_account_scope::test_support::TestRefreshScope;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::Arc;

    const SENTINEL_TOKEN: &str = "grokbot-sentinel-session-9b21e4";
    const USER_ID: &str = "user_FixtureAbc1234567890xyz";
    const PROFILE_PRIVATE: &str = "profile-private-canary";
    const USAGE_OK: &str = r#"{
        "usagePercent": 25,
        "currentPeriodStart": "2026-09-08T12:00:00Z",
        "nextResetTimestampUtc": "2099-01-01T00:00:00Z",
        "grokPlanLabel": "SuperGrok"
    }"#;

    struct Harness {
        dir: PathBuf,
        scope: Arc<TestRefreshScope>,
        cache: Mutex<ProviderLastGoodCache>,
        enrich_calls: Arc<AtomicUsize>,
        resolve_credential: Box<ResolveCredential>,
        resolve_history: Box<ResolveHistoryScope>,
        enrich: Box<Enrich>,
        url: String,
    }

    impl Harness {
        fn new(tag: &str, url: String) -> Self {
            let dir = std::env::temp_dir().join(format!(
                "tb-grokbot-fetch-{tag}-{}-{}",
                std::process::id(),
                Utc::now().timestamp_nanos_opt().unwrap()
            ));
            fs::create_dir_all(&dir).unwrap();
            // Written at a literal path, read back through the production
            // path derivation from the config root.
            crate::agent_grokbot::tests::write_state_db(
                &dir.join("Cursor")
                    .join("User")
                    .join("globalStorage")
                    .join("state.vscdb"),
                &[
                    ("cursorAuth/accessToken", &format!("\"{SENTINEL_TOKEN}\"")),
                    ("glass.lastSignedInAuthId", &format!("glass-{USER_ID}")),
                    (
                        "cursorAuth/cachedScopedProfile",
                        &format!("{{\"userId\":\"{USER_ID}\",\"email\":\"{PROFILE_PRIVATE}\"}}"),
                    ),
                ],
            );
            let scope = Arc::new(TestRefreshScope::new("grok-bot", tag));
            let enrich_calls = Arc::new(AtomicUsize::new(0));
            let credential_scope = Arc::clone(&scope);
            let history_scope = Arc::clone(&scope);
            let calls = Arc::clone(&enrich_calls);
            Self {
                dir,
                cache: Mutex::new(ProviderLastGoodCache::default()),
                enrich_calls,
                resolve_credential: Box::new(move |provider, source, location, marker| {
                    assert_eq!(provider, "grok-bot");
                    credential_scope.resolve_current(source, location, marker)
                }),
                resolve_history: Box::new(move |provider, authoritative| {
                    history_scope.resolve_history(provider, authoritative)
                }),
                enrich: Box::new(move |snapshot, now| {
                    calls.fetch_add(1, Ordering::SeqCst);
                    enrich_snapshot_with(snapshot, now, |_, _, _| Ok(vec![]));
                }),
                scope,
                url,
            }
        }

        /// A Grok Bot install: `sand-secrets.json` exists (here, as whatever
        /// `make` creates at that path).
        fn install_grok_bot(&self, make: impl FnOnce(&Path)) {
            let folder = self.dir.join("Grok Bot");
            fs::create_dir_all(&folder).unwrap();
            make(&folder.join("sand-secrets.json"));
        }

        fn deps(&self) -> GrokBotDeps<'_> {
            GrokBotDeps::for_test(
                self.dir.clone(),
                &*self.resolve_credential,
                &*self.resolve_history,
                &self.url,
                &self.cache,
                &*self.enrich,
            )
        }

        fn enrich_calls(&self) -> usize {
            self.enrich_calls.load(Ordering::SeqCst)
        }
    }

    impl Drop for Harness {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.dir);
            self.scope.cleanup();
        }
    }

    /// Loopback stand-in: answers one connection per scripted raw response, in
    /// order, and returns every request head it saw — including any connection
    /// beyond the scripted ones.
    async fn mock(responses: Vec<String>) -> (String, tokio::task::JoinHandle<Vec<String>>) {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!(
            "http://{}/api/dashboard/get-sand-usage-status",
            listener.local_addr().unwrap()
        );
        let server = tokio::spawn(async move {
            async fn read_head(stream: &mut tokio::net::TcpStream) -> String {
                let mut head = Vec::new();
                let mut buf = [0_u8; 1024];
                while !head.windows(4).any(|w| w == b"\r\n\r\n") {
                    let read = stream.read(&mut buf).await.unwrap();
                    if read == 0 {
                        break;
                    }
                    head.extend_from_slice(&buf[..read]);
                }
                String::from_utf8(head).unwrap()
            }
            let mut heads = Vec::new();
            for response in responses {
                let (mut stream, _) = listener.accept().await.unwrap();
                heads.push(read_head(&mut stream).await);
                stream.write_all(response.as_bytes()).await.unwrap();
            }
            if let Ok(Ok((mut stream, _))) =
                tokio::time::timeout(std::time::Duration::from_millis(200), listener.accept()).await
            {
                heads.push(read_head(&mut stream).await);
            }
            heads
        });
        (url, server)
    }

    fn reply(status: u16, body: &str) -> String {
        format!(
            "HTTP/1.1 {status} Fixture\r\ncontent-type: application/json\r\ncontent-length: {}\r\nconnection: close\r\n\r\n{body}",
            body.len()
        )
    }

    fn header_lines<'a>(head: &'a str, name: &str) -> Vec<&'a str> {
        head.lines()
            .filter(|line| {
                line.to_ascii_lowercase()
                    .starts_with(&format!("{}:", name.to_ascii_lowercase()))
            })
            .collect()
    }

    /// (1) + (6): fixture Cursor DB -> one `grok-bot` card; the mock saw exactly
    /// one POST carrying the cookie built from the fixture, macOS's Origin and
    /// Referer, and nothing else of the fixture. Rows land in the temp root and
    /// enrich ran once.
    #[tokio::test]
    async fn fetch_grokbot_with_cursor_fixture_sends_only_the_session_cookie() {
        let (url, server) = mock(vec![reply(200, USAGE_OK)]).await;
        let harness = Harness::new("fixture", url);

        let snapshot = fetch_grokbot_with(&harness.deps())
            .await
            .expect("a signed-in Cursor yields a card");
        assert_eq!(snapshot.client_id, "grok-bot");
        assert_eq!(snapshot.source, "oauth");
        assert!(snapshot.error.is_none(), "{:?}", snapshot.error);
        assert_eq!(snapshot.windows.len(), 1);
        assert_eq!(snapshot.windows[0].card_id, "weekly.v1");
        assert!(snapshot.account_scope.is_ok());
        let owner = serde_json::json!([USER_ID, null]).to_string();
        assert_eq!(
            snapshot.history_scope.as_ref().ok(),
            harness
                .scope
                .resolve_history("grok-bot", Some((AuthoritativeIdKind::OpaqueId, &owner)))
                .as_ref()
                .ok()
        );
        assert_eq!(
            snapshot.identity.as_ref().and_then(|i| i.plan.as_deref()),
            Some("SuperGrok")
        );

        let heads = server.await.unwrap();
        assert_eq!(heads.len(), 1, "exactly one request: {heads:?}");
        let head = &heads[0];
        assert_eq!(
            head.lines().next().unwrap(),
            "POST /api/dashboard/get-sand-usage-status HTTP/1.1"
        );
        assert_eq!(
            header_lines(head, "cookie"),
            vec![
                format!("cookie: WorkosCursorSessionToken={USER_ID}%3A%3A{SENTINEL_TOKEN}")
                    .as_str()
            ]
        );
        assert_eq!(
            header_lines(head, "origin"),
            vec!["origin: https://cursor.com"]
        );
        assert_eq!(
            header_lines(head, "referer"),
            vec!["referer: https://cursor.com/dashboard"]
        );
        assert!(header_lines(head, "authorization").is_empty());
        assert!(header_lines(head, "x-cursor-team-id").is_empty());
        assert!(
            !head.contains(PROFILE_PRIVATE),
            "profile leaked into {head}"
        );

        let metadata = harness.scope.metadata_bytes();
        assert!(!metadata.is_empty());
        let metadata = String::from_utf8_lossy(&metadata);
        for private in [SENTINEL_TOKEN, USER_ID] {
            assert!(!metadata.contains(private));
        }
        assert_eq!(harness.enrich_calls(), 1);
    }

    /// No Cursor DB and no Grok Bot install -> Absent: no card, no request.
    #[tokio::test]
    async fn fetch_grokbot_with_no_login_is_absent() {
        let (url, server) = mock(vec![]).await;
        let harness = Harness::new("absent", url);
        fs::remove_dir_all(harness.dir.join("Cursor")).unwrap();
        assert!(fetch_grokbot_with(&harness.deps()).await.is_none());
        assert_eq!(harness.enrich_calls(), 0);
        assert!(server.await.unwrap().is_empty(), "Absent sends nothing");
    }

    /// (2) R6-12: a Grok Bot install -> the fixed terminal and ZERO requests,
    /// although the Cursor fixture alongside it is usable (control: the first
    /// test). Both shapes of the file — arbitrary content, and a directory that
    /// cannot be read as a file at all — give the same text, so the guard
    /// decides on existence alone.
    #[tokio::test]
    async fn installed_grok_bot_is_terminal_and_sends_nothing() {
        let make: [fn(&Path); 2] = [
            |path| fs::write(path, SENTINEL_TOKEN).unwrap(),
            |path| fs::create_dir_all(path).unwrap(),
        ];
        for (index, make) in make.into_iter().enumerate() {
            let (url, server) = mock(vec![]).await;
            let harness = Harness::new(&format!("installed-{index}"), url);
            harness.install_grok_bot(make);
            let snapshot = fetch_grokbot_with(&harness.deps())
                .await
                .expect("an installed Grok Bot shows its state");
            assert_eq!(snapshot.client_id, "grok-bot");
            assert_eq!(
                snapshot.error.as_deref(),
                Some(crate::agent_grokbot::GROK_BOT_DESKTOP_UNSUPPORTED)
            );
            assert!(snapshot.windows.is_empty());
            assert_eq!(harness.enrich_calls(), 0);
            assert!(
                server.await.unwrap().is_empty(),
                "the Cursor login must not be sent while Grok Bot is installed"
            );
        }
    }

    /// (3) R6-15: a 302 to a second loopback host is not followed — the second
    /// host receives nothing, so neither the cookie nor any request reaches it.
    #[tokio::test]
    async fn redirect_is_not_followed() {
        let (second_url, second) = mock(vec![]).await;
        let redirect = format!(
            "HTTP/1.1 302 Found\r\nlocation: {second_url}\r\ncontent-length: 0\r\nconnection: close\r\n\r\n"
        );
        let (url, first) = mock(vec![redirect]).await;
        let harness = Harness::new("redirect", url);
        let snapshot = fetch_grokbot_with(&harness.deps()).await.unwrap();
        assert!(
            second.await.unwrap().is_empty(),
            "the redirect target must receive no request"
        );
        assert_eq!(first.await.unwrap().len(), 1);
        assert_eq!(
            snapshot.error.as_deref(),
            Some("Grok Bot usage API returned 302.")
        );
    }

    /// (4): the session token never reaches the published snapshot, on success
    /// or on either kind of failure. Success then 503 also proves the
    /// `usable_success("grok-bot")` arm at the production entry: the cached
    /// weekly window is replayed.
    #[tokio::test]
    async fn token_never_appears_in_snapshot_and_transient_replays_the_card() {
        let (url, server) = mock(vec![
            reply(200, USAGE_OK),
            reply(503, ""),
            reply(401, ""),
            reply(503, ""),
        ])
        .await;
        let harness = Harness::new("sentinel", url);
        let mut results = Vec::new();
        for _ in 0..4 {
            let snapshot = fetch_grokbot_with(&harness.deps()).await.unwrap();
            let json = serde_json::to_string(&snapshot).unwrap();
            for secret in [SENTINEL_TOKEN, USER_ID] {
                assert!(!json.contains(secret), "{secret} in {json}");
            }
            results.push((snapshot.error.clone(), snapshot.windows.len()));
        }
        let retry = "Grok Bot usage request failed. Retrying automatically.".to_string();
        assert_eq!(
            results,
            vec![
                (None, 1),
                (Some(retry.clone()), 1),
                (
                    Some(
                        "Cursor login expired. Open Cursor and sign in again, then refresh."
                            .to_string()
                    ),
                    0
                ),
                // The 401 cleared the cache, so this transient has no fallback.
                (Some(retry), 0),
            ]
        );
        assert_eq!(server.await.unwrap().len(), 4);
        assert_eq!(harness.enrich_calls(), 1);
    }

    /// Ported from macOS `grokbot_adapter_preserves_only_same_request_transients`:
    /// only a transient for the same request binding keeps the cached card.
    /// Also fails if `usable_success` does not admit "grok-bot" (nothing cached).
    #[test]
    fn grokbot_adapter_preserves_only_same_request_transients() {
        let now = Utc.timestamp_opt(1_789_041_600, 0).single().unwrap();
        let resolver = TestRefreshScope::new("grok-bot", "grokbot-transients");
        let scope = resolver
            .resolve_current("fixture", "account-a", b"personal")
            .unwrap();
        let binding = ProviderCacheBinding::primary(scope.clone());
        let other = ProviderCacheBinding::primary(
            resolver
                .resolve_current("fixture", "account-a", b"team")
                .unwrap(),
        );
        let diagnostic = || {
            SafeTransportDiagnostic::from_facts(TransportErrorFacts::synthetic(
                true,
                false,
                TransportPhase::Request,
                None,
            ))
        };
        let failures = [
            (
                "same request",
                Err(ProviderFetchFailure::transient(
                    "retry",
                    Some(binding.clone()),
                    diagnostic(),
                )),
                true,
            ),
            (
                "different team",
                Err(ProviderFetchFailure::transient(
                    "retry",
                    Some(other),
                    diagnostic(),
                )),
                false,
            ),
            (
                "unbound",
                Err(ProviderFetchFailure::transient("retry", None, diagnostic())),
                false,
            ),
            (
                "expired login",
                Err(ProviderFetchFailure::terminal("sign in again")),
                false,
            ),
            (
                "malformed meter",
                agent_grokbot::map_response("{}", now)
                    .map(Some)
                    .map_err(ProviderFetchFailure::terminal),
                false,
            ),
            ("signed out", Ok(None), false),
            (
                "no included allowance",
                agent_grokbot::map_response(
                    r#"{"hasNonZeroIncludedLimit":false,"usagePercent":0,
                        "nextResetTimestampUtc":"2026-09-15T12:00:00Z"}"#,
                    now,
                )
                .map(Some)
                .map_err(ProviderFetchFailure::terminal),
                false,
            ),
        ];
        for (label, failure, keep) in failures {
            let cache = Mutex::new(ProviderLastGoodCache::default());
            let mut data = agent_grokbot::map_response(
                r#"{
                "usagePercent": 25,
                "currentPeriodStart": "2026-09-08T12:00:00Z",
                "nextResetTimestampUtc": "2026-09-15T12:00:00Z"
            }"#,
                now,
            )
            .unwrap();
            data.account_scope = Ok(scope.clone());
            data.history_scope = Ok(HistoryScope::for_test("bot-history-a"));
            data.cache_binding = Some(binding.clone());
            let fresh = apply_provider_outcome_with(
                &cache,
                "grok-bot",
                "oauth",
                now,
                grokbot_outcome(Ok(Some(data)), now),
                |_| {},
            )
            .unwrap();
            assert_eq!(fresh.windows[0].card_id, "weekly.v1");
            assert_eq!(fresh.windows[0].remaining_percent, 75.0);
            assert_eq!(lock_last_good(&cache).entries.len(), 1, "{label}");
            let later = now + chrono::Duration::minutes(1);
            let result = apply_provider_outcome_with(
                &cache,
                "grok-bot",
                "oauth",
                later,
                grokbot_outcome(failure, later),
                |_| panic!("failure must not enrich history"),
            );
            if keep {
                let fallback = result.unwrap();
                assert_eq!(fallback.updated_at, fresh.updated_at);
                assert_eq!(fallback.windows.len(), 1);
                assert!(fallback.error.is_some());
                assert!(fallback.account_scope.is_err());
            } else {
                assert!(
                    result.is_none_or(|snapshot| snapshot.windows.is_empty()),
                    "{label}"
                );
                assert!(lock_last_good(&cache).entries.is_empty(), "{label}");
            }
        }
        resolver.cleanup();
    }
}
