//! The four dashboard payloads (graph, models, hourly, agents) with the usage
//! of paired producers folded in: the `tb_*_devices` entry points. Port of
//! macOS `remote_pages.rs` (Syrtis `f8a1a0ae`) with its structure, names and
//! limits unchanged; the Windows differences are the graph's local-only path
//! (`graph_cached` / `graph_compute`, the cache `tb_graph` uses), the graph
//! payload's pricing contract ([`merged_contract`]) and the injectable remote
//! part of each page (`*_with`), which tests and fixtures use.
//!
//! Where each number comes from:
//! - the local part is exactly what the existing report modules produce
//!   (`usage_graph`, `model_report`, `hourly_report`, `agents_report`), from
//!   the engine; with only the local source selected, the existing entry
//!   point's body runs unchanged and nothing remote starts;
//! - each producer's part is computed on the producer by the same engine and
//!   arrives as validated records through `Runtime::pull` /
//!   `Runtime::last_good` (one selected producer at a time, never
//!   `Agent::pull_all`, which pulls every peer and returns them summed);
//! - the producers' parts are summed by the agent's frozen `merge::combine`.
//!
//! This module checks each producer's records against the request (dates and
//! hour buckets inside the asked range, record counts, memory), converts the
//! combined records into payload rows, adds them to the local payload with
//! integer saturating arithmetic, recomputes the derived values (graph
//! intensity, summary and years; model throughput; totals), and reports one
//! `devices` row per selected source. Nothing here is cached: no remote data
//! reaches `GRAPH_CACHE` or any snapshot.
//!
//! Producer strings (client, model, provider, agent, the node label) are
//! only checked by the agent (length and NFC): they may hold bidi or control
//! characters, and the app must show them as plain text only.

use std::{
    collections::{BTreeMap, BTreeSet, HashMap},
    sync::{LazyLock, Mutex, PoisonError},
    time::Duration,
};

use chrono::{DateTime, Days, NaiveDate};
use serde::Deserialize;
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use syrtis_agent::{
    merge::{classify, combine, FailureClass, PartStatus, PeerData, PeerPart, Report},
    peer_store::PeerRecord,
    report::{
        decode_response, AgentsRecord, Endpoint, GraphRecord, HourlyRecord, ModelsRecord,
        ReportRecord, MAX_RANGE_DAYS,
    },
    report_peer::{decode_reservation, FetchError, Records, ReportQuery},
    runtime::PullError,
    security::{decode_fixed_hex, MemoryLedger, MemoryReservation},
};

use crate::remote_agent::RemoteAgent;
use tokscale_core::{CostCoverage, GraphMetaContract, GraphPricingMode};

/// Most producers one call may select.
pub(crate) const PEERS_MAX: usize = 16;
/// Most records one producer may contribute to one call; more and its whole
/// part is left out.
pub(crate) const PEER_RECORDS_MAX: usize = 32_768;
/// Most records all selected producers together may contribute; the producer
/// whose part would pass it is left out whole.
pub(crate) const TOTAL_RECORDS_MAX: usize = 131_072;

/// One `refresh` at a time: the four pages queue here rather than compete
/// for the runtime's two fetch slots.
static SERIAL: Mutex<()> = Mutex::new(());
/// The memory charged for decoding cached bodies and for holding each
/// producer's records until the fold ends. The runtime's own ledger is
/// private to it, so this module keeps one per process with the same cap.
static LEDGER: LazyLock<MemoryLedger> = LazyLock::new(MemoryLedger::default);

/// `devices_json`: which sources to fold.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct Selection {
    pub local: bool,
    /// Producer statics, in the caller's order.
    pub peers: Vec<[u8; 32]>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct SelectionJson {
    local: bool,
    peers: Vec<String>,
}

/// `{"local": bool, "peers": ["<64 lowercase hex>", …]}`: at least one
/// source, at most [`PEERS_MAX`] peers, none twice. Anything else `invalid`.
pub(crate) fn parse_selection(text: &str) -> Result<Selection, String> {
    let parsed: SelectionJson = serde_json::from_str(text).map_err(|_| "invalid")?;
    if parsed.peers.len() > PEERS_MAX || (!parsed.local && parsed.peers.is_empty()) {
        return Err("invalid".into());
    }
    let mut peers = Vec::with_capacity(parsed.peers.len());
    for peer in &parsed.peers {
        let key = decode_fixed_hex::<32>(peer).map_err(|_| "invalid")?;
        if peers.contains(&key) {
            return Err("invalid".into());
        }
        peers.push(key);
    }
    Ok(Selection {
        local: parsed.local,
        peers,
    })
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum Mode {
    /// The local part and each producer's last good body; no connection.
    Cached,
    /// Pull each producer; a failure falls back as `merge::classify` says.
    Refresh,
}

pub(crate) fn parse_mode(text: &str) -> Result<Mode, String> {
    match text {
        "cached" => Ok(Mode::Cached),
        "refresh" => Ok(Mode::Refresh),
        _ => Err("invalid".into()),
    }
}

/// The remote date range of a page for `year` (empty: all time) and whether
/// it is narrower than the local one: a year is that calendar year; all time
/// is the [`MAX_RANGE_DAYS`] days up to `today` included (the end is
/// tomorrow, exclusive).
pub(crate) fn remote_range(year: &str, today: NaiveDate) -> Result<(String, String, bool), String> {
    let span = Days::new(MAX_RANGE_DAYS as u64);
    let (start, end, limited) = match crate::usage_graph::normalize_year(year)? {
        Some(year) => {
            let year: i32 = year.parse().map_err(|_| "invalid")?;
            let start = NaiveDate::from_ymd_opt(year, 1, 1).ok_or("invalid")?;
            let end = NaiveDate::from_ymd_opt(year + 1, 1, 1).ok_or("invalid")?;
            (start, end, false)
        }
        None => {
            let end = today.checked_add_days(Days::new(1)).ok_or("invalid")?;
            (end.checked_sub_days(span).ok_or("invalid")?, end, true)
        }
    };
    Ok((start.to_string(), end.to_string(), limited))
}

/// Whether the engine's remote query validation accepts `query` (its bundled
/// tzdb knows the timezone, the dates and clients are well formed): the same
/// check a producer runs first, run here before anything is sent.
pub(crate) fn query_accepted(query: &ReportQuery) -> bool {
    tokscale_core::aggregate_remote_usage_v1(
        &[],
        &tokscale_core::RemoteUsageQueryV1 {
            clients: query.clients.clone(),
            start_date: query.start_date.clone(),
            end_date_exclusive: query.end_date_exclusive.clone(),
            timezone: query.timezone.clone(),
            tzdb_revision: tokscale_core::REMOTE_TZDB_REVISION.to_owned(),
        },
    )
    .is_ok()
}

// ------------------------------------------------------------- gather ---

/// What one selected producer gave before validation.
pub(crate) enum Got {
    Fresh(Report),
    /// The last good body, decoded (`None`: none, or no memory to decode).
    Cached(Option<Report>),
    /// Refused or invalid reply: no data.
    Failed(FailureClass),
    /// Another record with the same host and port is newer (deferred item 3).
    Superseded,
    /// The query is not one the engine accepts.
    Incompatible,
    /// No agent, or the peer store could not be read.
    Unavailable,
}

pub(crate) struct Pulled {
    pub peer: [u8; 32],
    pub grant: [u8; 16],
    /// The record's `host:port` (`None`: no such record).
    pub address: Option<String>,
    /// The name the producer last gave (`PeerRecord::node_label`, already
    /// checked by the agent), read after the pull so a first answer's name is
    /// the one shown.
    pub name: Option<String>,
    pub got: Got,
    /// The body this page used, fresh or cached, said the producer's price
    /// table was missing or corrupt (`false`: it did not, or no body).
    pub cost_incomplete: bool,
    /// [`RemoteAgent::ever_answered`].
    pub ever_answered: bool,
    /// [`RemoteAgent::last_success_at`] after the pull.
    pub last_success: Option<i64>,
    /// Why the pull failed, before `merge::classify` folds the failures
    /// together (see [`reason_of`]); `None` when it did not fail that way.
    pub reason: Option<&'static str>,
}

/// The remote side of a page.
pub(crate) struct Remote {
    pub records: Option<Records>,
    pub devices: Vec<Value>,
    pub saturated: bool,
}

fn fingerprint(key: &[u8; 32]) -> String {
    syrtis_agent::security::hex_encode(&Sha256::digest(key)[..8])
}

/// The producer's address as the receiver stored it at pairing.
fn address(record: &PeerRecord) -> String {
    if record.host.contains(':') {
        format!("[{}]:{}", record.host, record.port)
    } else {
        format!("{}:{}", record.host, record.port)
    }
}

/// A producer's `devices` row. `label` is what the screens show: the name
/// the producer gave, else the address (producers' rows also carry the
/// address on its own, see [`assemble`]). `reason` says why the pull failed when
/// the state alone does not (`unreachable`, `timeout`, `busy`, `stopped`,
/// `error`); `totals` is that producer's part of the merged page, one entry
/// per client, and `null` when no part of it was merged.
fn device(
    id: String,
    label: Option<String>,
    state: &str,
    last_success: Option<i64>,
    reason: Option<&str>,
    totals: Option<Value>,
) -> Value {
    json!({"id": id, "label": label, "state": state, "lastSuccessAt": last_success,
           "reason": reason, "totals": totals})
}

/// The local row. Without producers it is the four keys the local-only
/// output has always had; with them it also carries the local `totals`.
pub(crate) fn local_device(totals: Option<Value>) -> Value {
    match totals {
        None => json!({"id": "local", "label": null, "state": "local", "lastSuccessAt": null}),
        Some(totals) => device("local".into(), None, "local", None, None, Some(totals)),
    }
}

/// One client's share of a device's part of a page.
#[derive(Default)]
struct ClientTotal {
    messages: i64,
    tokens: i64,
    cost: f64,
}

type ByClient = BTreeMap<String, ClientTotal>;

fn add_total(by: &mut ByClient, client: &str, messages: i64, tokens: i64, cost: f64) {
    let total = by.entry(client.to_owned()).or_default();
    total.messages = total.messages.saturating_add(messages);
    total.tokens = total.tokens.saturating_add(tokens);
    total.cost += cost;
}

fn totals_value(by: ByClient) -> Value {
    Value::Array(
        by.into_iter()
            .map(|(client, t)| {
                json!({"client": client, "messages": t.messages, "tokens": t.tokens,
                       "costUsd": t.cost})
            })
            .collect(),
    )
}

fn breakdown_total(t: &tokscale_core::TokenBreakdown) -> i64 {
    [t.input, t.output, t.cache_read, t.cache_write, t.reasoning]
        .into_iter()
        .fold(0, i64::saturating_add)
}

/// The local graph's per-client totals, taken before any producer is folded.
pub(crate) fn graph_totals(graph: &tokscale_core::GraphResult) -> Value {
    let mut by = ByClient::new();
    for day in &graph.contributions {
        for c in &day.clients {
            add_total(
                &mut by,
                &c.client,
                i64::from(c.messages),
                breakdown_total(&c.tokens),
                c.cost,
            );
        }
    }
    totals_value(by)
}

/// The local model report's per-client totals, taken before the fold.
pub(crate) fn models_totals(report: &tokscale_core::ModelReport) -> Value {
    let mut by = ByClient::new();
    for e in &report.entries {
        let tokens = [e.input, e.output, e.cache_read, e.cache_write, e.reasoning]
            .into_iter()
            .fold(0, i64::saturating_add);
        add_total(
            &mut by,
            &e.client,
            i64::from(e.message_count),
            tokens,
            e.cost,
        );
    }
    totals_value(by)
}

/// One producer's per-client totals from its admitted records (graph and
/// models pages; the other two pages carry none).
fn records_totals(records: &Records) -> Option<Value> {
    let mut by = ByClient::new();
    let as_i64 = |v: u64| i64::try_from(v).unwrap_or(i64::MAX);
    match records {
        Records::Graph(rs) => rs.iter().for_each(|r| {
            add_total(
                &mut by,
                &r.client,
                as_i64(r.message_count),
                as_i64(r.total_tokens),
                usd(r.cost_nano_usd),
            )
        }),
        Records::Models(rs) => rs.iter().for_each(|r| {
            add_total(
                &mut by,
                &r.client,
                as_i64(r.message_count),
                as_i64(r.total_tokens),
                usd(r.cost_nano_usd),
            )
        }),
        Records::Hourly(_) | Records::Agents(_) => return None,
    }
    Some(totals_value(by))
}

/// The failure a pull ended in, before `merge::classify` folds it into
/// three classes: the distinction the status line needs. Failures with no
/// line of their own (too large, no memory, invalid query, protocol, invalid
/// body) have none.
fn reason_of(error: &PullError) -> Option<&'static str> {
    match error {
        PullError::NoPeer => Some("stopped"),
        PullError::Fetch(error) => match error {
            FetchError::Transport(_) => Some("unreachable"),
            FetchError::Timeout => Some("timeout"),
            FetchError::Busy | FetchError::TooMany => Some("busy"),
            FetchError::Revoked
            | FetchError::Expired
            | FetchError::Unusable
            | FetchError::CheckGrantStatus => Some("stopped"),
            FetchError::Internal | FetchError::Rejected => Some("error"),
            _ => None,
        },
    }
}

/// Pulls (or reads the last good body of) every selected producer, one at a
/// time, then [`assemble`]. Only `refresh` takes [`SERIAL`]: a `cached` call
/// connects to nothing and must not wait behind a pull.
pub(crate) fn gather(
    agent: &RemoteAgent,
    peers: &[[u8; 32]],
    mode: Mode,
    query: &ReportQuery,
) -> Remote {
    if peers.is_empty() {
        return Remote {
            records: None,
            devices: Vec::new(),
            saturated: false,
        };
    }
    let _serial =
        (mode == Mode::Refresh).then(|| SERIAL.lock().unwrap_or_else(PoisonError::into_inner));
    let records = agent.peer_records();
    let accepted = query_accepted(query);
    let mut pulled = Vec::with_capacity(peers.len());
    for peer in peers {
        let record = records
            .as_ref()
            .ok()
            .and_then(|all| all.iter().find(|r| r.responder_static == *peer));
        let (got, reason, cost_incomplete) = match &records {
            Err(_) => (Got::Unavailable, None, false),
            Ok(_) if !accepted => (Got::Incompatible, None, false),
            Ok(all) if record.is_some_and(|record| superseded(record, all)) => {
                (Got::Superseded, None, false)
            }
            Ok(_) => fetch(agent, peer, mode, query),
        };
        pulled.push(Pulled {
            peer: *peer,
            grant: record.map_or([0; 16], |record| record.grant_id),
            address: record.map(address),
            name: None,
            got,
            cost_incomplete,
            ever_answered: agent.ever_answered(peer),
            last_success: agent.last_success_at(peer, query),
            reason,
        });
    }
    // A successful pull stores the producer's current name on its record, so
    // the names are read after the loop, not from the read before it.
    if let Ok(after) = agent.peer_records() {
        for item in &mut pulled {
            item.name = after
                .iter()
                .find(|r| r.responder_static == item.peer)
                .and_then(|r| r.node_label.clone());
        }
    }
    let mut remote = assemble(query, pulled);
    if records.is_err() {
        // Deferred item (2): an unreadable peer store is said, not shown as
        // "no devices".
        remote.devices.insert(
            0,
            device("peers".into(), None, "unavailable", None, None, None),
        );
    }
    remote
}

/// Deferred item (3): a record is superseded when another record names the
/// same host and port and was created later (the producer reset its
/// identity and was paired again). Its grant may still be active and its
/// cache still hold up to 30 days, so it is neither asked nor read.
fn superseded(record: &PeerRecord, all: &[PeerRecord]) -> bool {
    all.iter().any(|other| {
        other.responder_static != record.responder_static
            && other.host == record.host
            && other.port == record.port
            && other.created_at_ms > record.created_at_ms
    })
}

fn fetch(
    agent: &RemoteAgent,
    peer: &[u8; 32],
    mode: Mode,
    query: &ReportQuery,
) -> (Got, Option<&'static str>, bool) {
    let cached = || match agent
        .last_good_peer(peer, query)
        .and_then(|body| decode(query.endpoint, body.body()))
    {
        Some((report, incomplete)) => (Got::Cached(Some(report)), incomplete),
        None => (Got::Cached(None), false),
    };
    match mode {
        Mode::Cached => {
            let (got, incomplete) = cached();
            (got, None, incomplete)
        }
        Mode::Refresh => match agent.pull_peer(peer, query) {
            None => (Got::Unavailable, None, false),
            Some(Ok(fetched)) => (
                Got::Fresh(Report {
                    records: fetched.records,
                    saturated: fetched.saturated,
                }),
                None,
                fetched.cost_incomplete,
            ),
            Some(Err(error)) => failed(error, cached),
        },
    }
}

/// What a failed pull leaves: code 6 (the producer does not speak this
/// receiver's report schema) is the `incompatible` state, not a failure the
/// status line explains; no answer falls back to the last good body (read
/// through `cached`); anything else is `merge::classify`'s class.
fn failed(
    error: PullError,
    cached: impl FnOnce() -> (Got, bool),
) -> (Got, Option<&'static str>, bool) {
    if matches!(error, PullError::Fetch(FetchError::Incompatible)) {
        return (Got::Incompatible, None, false);
    }
    let reason = reason_of(&error);
    match classify(error) {
        FailureClass::NoAnswer => {
            let (got, incomplete) = cached();
            (got, reason, incomplete)
        }
        class => (Got::Failed(class), reason, false),
    }
}

/// A cached body, decoded by the frozen decoder after its decode cost is
/// reserved, with its `costIncomplete`; `None` when the reservation or the
/// decode fails.
fn decode(endpoint: Endpoint, body: &[u8]) -> Option<(Report, bool)> {
    fn typed<R: ReportRecord>(body: &[u8], wrap: fn(Vec<R>) -> Records) -> Option<(Report, bool)> {
        let response = decode_response::<R>(body, None).ok()?;
        Some((
            Report {
                records: wrap(response.records),
                saturated: response.saturated,
            },
            response.cost_incomplete,
        ))
    }
    let cost = decode_reservation(body, endpoint).ok()?;
    let _reservation = LEDGER.reserve(cost)?;
    match endpoint {
        Endpoint::Graph => typed::<GraphRecord>(body, Records::Graph),
        Endpoint::Models => typed::<ModelsRecord>(body, Records::Models),
        Endpoint::Hourly => typed::<HourlyRecord>(body, Records::Hourly),
        Endpoint::Agents => typed::<AgentsRecord>(body, Records::Agents),
    }
}

/// The largest UTC offset an hourly record may carry: 14 hours, the widest
/// offset any zone in the IANA database uses (UTC+14:00).
const UTC_OFFSET_MAX_SECONDS: u32 = 14 * 3_600;

/// Why a producer's part was left out before `combine`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Refusal {
    /// Records outside the asked range, or not of the endpoint.
    OutOfRange,
    /// Over [`PEER_RECORDS_MAX`] / [`TOTAL_RECORDS_MAX`], or no memory.
    Limit,
}

/// The pure half of [`gather`]: checks each producer's records against
/// `query`, the record limits and the memory ledger, hands the parts that
/// pass to `merge::combine`, and builds the `devices` rows in the order of
/// `pulled`.
pub(crate) fn assemble(query: &ReportQuery, pulled: Vec<Pulled>) -> Remote {
    let mut total = 0usize;
    let mut held: Vec<MemoryReservation> = Vec::new();
    let mut refused: HashMap<[u8; 32], Refusal> = HashMap::new();
    let mut parts = Vec::new();
    /// A `devices` row before the merge outcome is known.
    struct Row {
        peer: [u8; 32],
        name: Option<String>,
        address: Option<String>,
        cost_incomplete: bool,
        ever_answered: bool,
        fixed: Option<&'static str>,
        last_success: Option<i64>,
        reason: Option<&'static str>,
    }
    // Each admitted producer's own totals, kept beside the records `combine`
    // sums so the rows can be checked against the merged page.
    let mut totals: HashMap<[u8; 32], Value> = HashMap::new();
    let mut rows = Vec::with_capacity(pulled.len());
    for item in pulled {
        let fixed = match &item.got {
            Got::Superseded => Some("stale"),
            Got::Incompatible => Some("incompatible"),
            Got::Unavailable => Some("unavailable"),
            _ => None,
        };
        rows.push(Row {
            peer: item.peer,
            name: item.name.clone(),
            address: item.address.clone(),
            cost_incomplete: item.cost_incomplete,
            ever_answered: item.ever_answered,
            fixed,
            last_success: item.last_success,
            reason: item.reason,
        });
        let data = match item.got {
            Got::Fresh(report) => PeerData::Fresh(report),
            Got::Cached(Some(report)) => PeerData::LastGood(report),
            Got::Cached(None) => PeerData::None,
            Got::Failed(FailureClass::InvalidReply) => PeerData::InvalidReply,
            Got::Failed(_) => PeerData::Refused,
            Got::Superseded | Got::Incompatible | Got::Unavailable => continue,
        };
        if let PeerData::Fresh(report) | PeerData::LastGood(report) = &data {
            match admit(query, &report.records, &mut total) {
                Ok(reservation) => {
                    held.push(reservation);
                    if let Some(value) = records_totals(&report.records) {
                        totals.insert(item.peer, value);
                    }
                }
                Err(refusal) => {
                    refused.insert(item.peer, refusal);
                    continue;
                }
            }
        }
        parts.push(PeerPart {
            producer_static: item.peer,
            grant_id: item.grant,
            data,
            error: None,
        });
    }
    let combined = combine(query.endpoint, None, parts);
    let status: HashMap<[u8; 32], PartStatus> = combined
        .peers
        .iter()
        .map(|outcome| (outcome.producer_static, outcome.status))
        .collect();
    let devices = rows
        .into_iter()
        .map(
            |Row {
                 peer,
                 name,
                 address,
                 cost_incomplete,
                 ever_answered,
                 fixed,
                 last_success,
                 reason,
             }| {
                let state = fixed.unwrap_or_else(|| match refused.get(&peer) {
                    Some(Refusal::OutOfRange) => "invalidReply",
                    Some(Refusal::Limit) => "overLimit",
                    None => state_of(status.get(&peer).copied(), last_success),
                });
                let last_success = if state == "stale" && fixed.is_none() {
                    None
                } else {
                    last_success
                };
                // Only a part `combine` summed has totals.
                let merged = matches!(state, "fresh" | "lastGood" | "stale") && fixed.is_none();
                // The name is the producer's own free text; the address is the
                // fallback and is always kept in its own field.
                let label = name.or_else(|| address.clone());
                let mut row = device(
                    fingerprint(&peer),
                    label,
                    state,
                    last_success,
                    reason,
                    if merged { totals.remove(&peer) } else { None },
                );
                // Producers' rows only: `address` is the record's `host:port`
                // whatever the name; `costIncomplete` is true only when the
                // body this page merged said so; `everAnswered` is whether
                // this producer ever gave this receiver a body.
                row["address"] = json!(address);
                row["costIncomplete"] = json!(merged && cost_incomplete);
                row["everAnswered"] = json!(ever_answered);
                row
            },
        )
        .collect();
    drop(held);
    Remote {
        records: combined.records,
        devices,
        saturated: combined.saturated,
    }
}

/// The fixed state of a part `combine` considered. `LastGood` with no known
/// fresh time in this process (a body cached before a restart) is `stale`,
/// never `lastGood` or `fresh`.
pub(crate) fn state_of(status: Option<PartStatus>, last_success: Option<i64>) -> &'static str {
    match status {
        Some(PartStatus::Fresh) => "fresh",
        Some(PartStatus::LastGood) if last_success.is_some() => "lastGood",
        Some(PartStatus::LastGood) => "stale",
        Some(PartStatus::Refused) => "refused",
        Some(PartStatus::InvalidReply) | Some(PartStatus::Invalid) => "invalidReply",
        Some(PartStatus::Unavailable) | Some(PartStatus::Duplicate) | None => "unavailable",
    }
}

/// One producer's records against the request (the frozen record validation
/// does not check them against it): every graph date in
/// `[startDate, endDateExclusive)`; every hour bucket's local wall time
/// ([`local_ms`], the time its slot is built from) inside that range's
/// milliseconds, so a producer cannot move its numbers into slots of another
/// date with an offset (the frozen validation does not bound
/// `utcOffsetSeconds`); then the record limits; then a reservation for
/// holding them.
///
/// No slack is needed: the engine buckets a message by its local date in
/// the query timezone, keeps it only when that date is in the range, and
/// takes as the bucket start an instant whose local date and hour are the
/// message's and whose offset is the one it reports (`bucket_start` in
/// `tokscale-core/src/remote_report.rs`), so a correct record's local wall
/// time is on a requested date.
fn admit(
    query: &ReportQuery,
    records: &Records,
    total: &mut usize,
) -> Result<MemoryReservation, Refusal> {
    let count = match records {
        Records::Graph(records) => {
            let inside = |date: &str| {
                date >= query.start_date.as_str() && date < query.end_date_exclusive.as_str()
            };
            if !records.iter().all(|record| inside(&record.date)) {
                return Err(Refusal::OutOfRange);
            }
            records.len()
        }
        Records::Hourly(records) => {
            let window = day_ms(&query.start_date)
                .zip(day_ms(&query.end_date_exclusive))
                .ok_or(Refusal::OutOfRange)?;
            let inside = |record: &HourlyRecord| {
                // The offset is checked on its own too: the frozen validation
                // does not bound it, and a bucket from another year plus an
                // offset of a year would otherwise land inside the range.
                record.utc_offset_seconds.unsigned_abs() <= UTC_OFFSET_MAX_SECONDS
                    && local_ms(record).is_some_and(|local| (window.0..window.1).contains(&local))
                    && hour_slot(record).is_some()
            };
            if !records.iter().all(inside) {
                return Err(Refusal::OutOfRange);
            }
            records.len()
        }
        Records::Models(records) => records.len(),
        Records::Agents(records) => records.len(),
    };
    let next = total.checked_add(count).ok_or(Refusal::Limit)?;
    if count > PEER_RECORDS_MAX || next > TOTAL_RECORDS_MAX {
        return Err(Refusal::Limit);
    }
    let reservation = LEDGER.reserve(held_bytes(records)).ok_or(Refusal::Limit)?;
    *total = next;
    Ok(reservation)
}

/// UTC midnight of a `YYYY-MM-DD` date, in Unix milliseconds.
fn day_ms(date: &str) -> Option<i64> {
    NaiveDate::parse_from_str(date, "%Y-%m-%d")
        .ok()?
        .and_hms_opt(0, 0, 0)?
        .and_utc()
        .timestamp_millis()
        .into()
}

/// What holding `records` costs: each record and its strings.
fn held_bytes(records: &Records) -> usize {
    fn text(value: &Option<String>) -> usize {
        value.as_ref().map_or(0, String::len)
    }
    let (count, size, strings) = match records {
        Records::Graph(r) => (
            r.len(),
            std::mem::size_of::<GraphRecord>(),
            r.iter()
                .map(|r| r.date.len() + r.client.len() + text(&r.model) + text(&r.provider))
                .sum::<usize>(),
        ),
        Records::Models(r) => (
            r.len(),
            std::mem::size_of::<ModelsRecord>(),
            r.iter()
                .map(|r| r.client.len() + text(&r.model) + text(&r.provider))
                .sum(),
        ),
        Records::Hourly(r) => (
            r.len(),
            std::mem::size_of::<HourlyRecord>(),
            r.iter()
                .map(|r| r.client.len() + text(&r.model) + text(&r.provider))
                .sum(),
        ),
        Records::Agents(r) => (
            r.len(),
            std::mem::size_of::<AgentsRecord>(),
            r.iter()
                .map(|r| r.agent.len() + r.client.len() + text(&r.model) + text(&r.provider))
                .sum(),
        ),
    };
    count.saturating_mul(size).saturating_add(strings)
}

/// The local `"YYYY-MM-DD HH:00"` slot of an hour bucket in the query's
/// timezone: the producer computed `bucketStartUnixMs` and the offset in that
/// timezone, so their sum is the local wall time. Checked: `None` on any
/// overflow.
pub(crate) fn hour_slot(record: &HourlyRecord) -> Option<String> {
    let local = local_ms(record)?;
    Some(
        DateTime::from_timestamp_millis(local)?
            .naive_utc()
            .format("%Y-%m-%d %H:00")
            .to_string(),
    )
}

/// `bucketStartUnixMs + utcOffsetSeconds`: the bucket's local wall time as
/// if it were UTC, in milliseconds. Checked: `None` on any overflow.
fn local_ms(record: &HourlyRecord) -> Option<i64> {
    let offset_ms = i64::from(record.utc_offset_seconds).checked_mul(1_000)?;
    record.bucket_start_unix_ms.checked_add(offset_ms)
}

// --------------------------------------------------------- arithmetic ---

fn to_i64(value: u64, saturated: &mut bool) -> i64 {
    i64::try_from(value).unwrap_or_else(|_| {
        *saturated = true;
        i64::MAX
    })
}

fn add_i64(target: &mut i64, value: u64, saturated: &mut bool) {
    let value = to_i64(value, saturated);
    *target = target.checked_add(value).unwrap_or_else(|| {
        *saturated = true;
        i64::MAX
    });
}

fn add_i32(target: &mut i32, value: u64, saturated: &mut bool) {
    let value = i32::try_from(value).unwrap_or_else(|_| {
        *saturated = true;
        i32::MAX
    });
    *target = target.checked_add(value).unwrap_or_else(|| {
        *saturated = true;
        i32::MAX
    });
}

/// `costNanoUsd` in US dollars: the one conversion to floating point, after
/// every integer sum.
fn usd(nano: u64) -> f64 {
    nano as f64 / 1e9
}

/// `None` and the empty string are the same model or provider: the local
/// reports carry an absent one as `""`.
fn text(value: &Option<String>) -> &str {
    value.as_deref().unwrap_or("")
}

// --------------------------------------------------------------- graph ---

/// The engine's intensity rule (`calculate_intensities` in
/// `tokscale-core/src/aggregator.rs`, private there): each day's cost
/// against the highest day's, in quarters. Pinned to the engine by the
/// parity test.
pub(crate) fn intensities(days: &mut [tokscale_core::DailyContribution]) {
    let max_cost = days.iter().map(|day| day.totals.cost).fold(0.0, f64::max);
    if max_cost == 0.0 {
        return;
    }
    for day in days.iter_mut() {
        let ratio = day.totals.cost / max_cost;
        day.intensity = if ratio >= 0.75 {
            4
        } else if ratio >= 0.5 {
            3
        } else if ratio >= 0.25 {
            2
        } else if ratio > 0.0 {
            1
        } else {
            0
        };
    }
}

/// An empty engine graph (no local source selected).
pub(crate) fn empty_graph() -> tokscale_core::GraphResult {
    tokscale_core::GraphResult {
        meta: tokscale_core::GraphMeta {
            generated_at: chrono::Utc::now().to_rfc3339(),
            version: String::new(),
            date_range_start: String::new(),
            date_range_end: String::new(),
            processing_time_ms: 0,
        },
        summary: tokscale_core::calculate_summary(&[]),
        years: Vec::new(),
        contributions: Vec::new(),
        time_metrics: None,
    }
}

/// Adds graph records to the day they name: the row of the same client,
/// model and provider (or a new row), the day's breakdown, totals and turns
/// (or a new day); then intensity, summary, years and the date range again.
pub(crate) fn fold_graph(
    graph: &mut tokscale_core::GraphResult,
    records: &[GraphRecord],
    saturated: &mut bool,
) {
    let days = &mut graph.contributions;
    let mut index: HashMap<String, usize> = days
        .iter()
        .enumerate()
        .map(|(at, day)| (day.date.clone(), at))
        .collect();
    // (day, client, model, provider) -> row, so a day with many rows is not
    // scanned once per record.
    let mut rows: HashMap<(usize, String, String, String), usize> = HashMap::new();
    for (at, day) in days.iter().enumerate() {
        for (row, entry) in day.clients.iter().enumerate() {
            rows.entry((
                at,
                entry.client.clone(),
                entry.model_id.clone(),
                entry.provider_id.clone(),
            ))
            .or_insert(row);
        }
    }
    for record in records {
        let at = *index.entry(record.date.clone()).or_insert_with(|| {
            days.push(tokscale_core::DailyContribution {
                date: record.date.clone(),
                totals: Default::default(),
                intensity: 0,
                token_breakdown: Default::default(),
                clients: Vec::new(),
                active_time_ms: None,
                turns_by_client: Default::default(),
            });
            days.len() - 1
        });
        let day = &mut days[at];
        let (model, provider) = (text(&record.model), text(&record.provider));
        let key = (
            at,
            record.client.clone(),
            model.to_owned(),
            provider.to_owned(),
        );
        let row = match rows.get(&key) {
            Some(row) => *row,
            None => {
                rows.insert(key, day.clients.len());
                day.clients.push(tokscale_core::ClientContribution {
                    client: record.client.clone(),
                    model_id: model.to_owned(),
                    provider_id: provider.to_owned(),
                    tokens: Default::default(),
                    cost: 0.0,
                    messages: 0,
                });
                day.clients.len() - 1
            }
        };
        let cost = usd(record.cost_nano_usd);
        for tokens in [&mut day.clients[row].tokens, &mut day.token_breakdown] {
            add_i64(&mut tokens.input, record.input_tokens, saturated);
            add_i64(&mut tokens.output, record.output_tokens, saturated);
            add_i64(&mut tokens.cache_read, record.cache_read_tokens, saturated);
            add_i64(
                &mut tokens.cache_write,
                record.cache_write_tokens,
                saturated,
            );
            add_i64(&mut tokens.reasoning, record.reasoning_tokens, saturated);
        }
        day.clients[row].cost += cost;
        add_i32(
            &mut day.clients[row].messages,
            record.message_count,
            saturated,
        );
        add_i64(&mut day.totals.tokens, record.total_tokens, saturated);
        day.totals.cost += cost;
        add_i32(&mut day.totals.messages, record.message_count, saturated);
        if record.turn_count > 0 {
            add_i64(
                day.turns_by_client
                    .entry(record.client.clone())
                    .or_insert(0),
                record.turn_count,
                saturated,
            );
        }
    }
    days.sort_by(|a, b| a.date.cmp(&b.date));
    intensities(days);
    graph.summary = tokscale_core::calculate_summary(&graph.contributions);
    graph.years = tokscale_core::calculate_years(&graph.contributions);
    graph.meta.date_range_start = graph
        .contributions
        .first()
        .map(|d| d.date.clone())
        .unwrap_or_default();
    graph.meta.date_range_end = graph
        .contributions
        .last()
        .map(|d| d.date.clone())
        .unwrap_or_default();
}

// -------------------------------------------------------------- models ---

/// The engine's model order: cost descending, NaN last.
fn by_cost(a: f64, b: f64) -> std::cmp::Ordering {
    match (a.is_nan(), b.is_nan()) {
        (true, true) => std::cmp::Ordering::Equal,
        (true, false) => std::cmp::Ordering::Greater,
        (false, true) => std::cmp::Ordering::Less,
        (false, false) => b.partial_cmp(&a).unwrap_or(std::cmp::Ordering::Equal),
    }
}

/// Adds model records to the row of the same client, model and provider (or
/// a new row), keeping the duration and timed tokens until after the sum so
/// `msPer1kTokens` is the merged rate; re-sorts and recomputes the totals.
/// Returns, per entry in the new order, whether it holds any remote part:
/// those rows get no `costEstimate` (it would price only the local tokens).
pub(crate) fn fold_models(
    report: &mut tokscale_core::ModelReport,
    records: &[ModelsRecord],
    saturated: &mut bool,
) -> Vec<bool> {
    let mut rows: Vec<(tokscale_core::ModelUsage, bool)> = report
        .entries
        .drain(..)
        .map(|entry| (entry, false))
        .collect();
    let mut index: HashMap<(String, String, String), usize> = HashMap::new();
    for (at, (row, _)) in rows.iter().enumerate() {
        index
            .entry((row.client.clone(), row.model.clone(), row.provider.clone()))
            .or_insert(at);
    }
    for record in records {
        let (model, provider) = (text(&record.model), text(&record.provider));
        let key = (record.client.clone(), model.to_owned(), provider.to_owned());
        let at = match index.get(&key) {
            Some(at) => *at,
            None => {
                index.insert(key, rows.len());
                rows.push((
                    tokscale_core::ModelUsage {
                        client: record.client.clone(),
                        merged_clients: None,
                        workspace_key: None,
                        workspace_label: None,
                        session_id: None,
                        model: model.to_owned(),
                        provider: provider.to_owned(),
                        input: 0,
                        output: 0,
                        cache_read: 0,
                        cache_write: 0,
                        reasoning: 0,
                        message_count: 0,
                        cost: 0.0,
                        performance: Default::default(),
                    },
                    true,
                ));
                rows.len() - 1
            }
        };
        let (row, touched) = &mut rows[at];
        *touched = true;
        add_i64(&mut row.input, record.input_tokens, saturated);
        add_i64(&mut row.output, record.output_tokens, saturated);
        add_i64(&mut row.cache_read, record.cache_read_tokens, saturated);
        add_i64(&mut row.cache_write, record.cache_write_tokens, saturated);
        add_i64(&mut row.reasoning, record.reasoning_tokens, saturated);
        add_i32(&mut row.message_count, record.message_count, saturated);
        row.cost += usd(record.cost_nano_usd);
        let performance = &mut row.performance;
        add_i64(
            &mut performance.total_duration_ms,
            record.duration_millis,
            saturated,
        );
        add_i64(
            &mut performance.timed_tokens,
            record.timed_tokens,
            saturated,
        );
        add_i32(
            &mut performance.sample_count,
            record.sample_count,
            saturated,
        );
        // The engine's `ModelPerformance::finalize` rate.
        performance.ms_per_1k_tokens =
            (performance.timed_tokens > 0 && performance.total_duration_ms > 0).then(|| {
                performance.total_duration_ms as f64 * 1000.0 / performance.timed_tokens as f64
            });
    }
    rows.sort_by(|(a, _), (b, _)| by_cost(a.cost, b.cost));
    let touched = rows.iter().map(|(_, touched)| *touched).collect();
    report.entries = rows.into_iter().map(|(row, _)| row).collect();
    let entries = &report.entries;
    let sum = |field: fn(&tokscale_core::ModelUsage) -> i64| {
        entries
            .iter()
            .fold(0i64, |total, entry| total.saturating_add(field(entry)))
    };
    report.total_input = sum(|e| e.input);
    report.total_output = sum(|e| e.output);
    report.total_cache_read = sum(|e| e.cache_read);
    report.total_cache_write = sum(|e| e.cache_write);
    report.total_messages = entries
        .iter()
        .map(|e| e.message_count)
        .fold(0, i32::saturating_add);
    report.total_cost = entries.iter().map(|e| e.cost).sum::<f64>() + 0.0;
    touched
}

// -------------------------------------------------------------- hourly ---

/// Adds hourly records to their local hour slot (or a new slot): tokens,
/// counts and cost, and the slot's clients and models as sorted unions.
pub(crate) fn fold_hourly(
    report: &mut tokscale_core::HourlyReport,
    records: &[HourlyRecord],
    saturated: &mut bool,
) {
    let entries = &mut report.entries;
    let mut index: HashMap<String, usize> = HashMap::new();
    for (at, entry) in entries.iter().enumerate() {
        index.entry(entry.hour.clone()).or_insert(at);
    }
    // The clients and models of each touched slot, written out once, sorted.
    let mut sets: HashMap<usize, (BTreeSet<String>, BTreeSet<String>)> = HashMap::new();
    for record in records {
        // `admit` refused any record without a slot.
        let Some(hour) = hour_slot(record) else {
            continue;
        };
        let at = match index.get(&hour) {
            Some(at) => *at,
            None => {
                index.insert(hour.clone(), entries.len());
                entries.push(tokscale_core::HourlyUsage {
                    hour,
                    clients: Vec::new(),
                    models: Vec::new(),
                    input: 0,
                    output: 0,
                    cache_read: 0,
                    cache_write: 0,
                    message_count: 0,
                    turn_count: 0,
                    reasoning: 0,
                    cost: 0.0,
                });
                entries.len() - 1
            }
        };
        let entry = &mut entries[at];
        add_i64(&mut entry.input, record.input_tokens, saturated);
        add_i64(&mut entry.output, record.output_tokens, saturated);
        add_i64(&mut entry.cache_read, record.cache_read_tokens, saturated);
        add_i64(&mut entry.cache_write, record.cache_write_tokens, saturated);
        add_i64(&mut entry.reasoning, record.reasoning_tokens, saturated);
        add_i32(&mut entry.message_count, record.message_count, saturated);
        add_i32(&mut entry.turn_count, record.turn_count, saturated);
        entry.cost += usd(record.cost_nano_usd);
        let (clients, models) = sets.entry(at).or_insert_with(|| {
            (
                entry.clients.iter().cloned().collect(),
                entry.models.iter().cloned().collect(),
            )
        });
        clients.insert(record.client.clone());
        if let Some(model) = &record.model {
            models.insert(model.clone());
        }
    }
    for (at, (clients, models)) in sets {
        entries[at].clients = clients.into_iter().collect();
        entries[at].models = models.into_iter().collect();
    }
    entries.sort_by(|a, b| a.hour.cmp(&b.hour));
    report.total_cost = entries.iter().map(|e| e.cost).sum::<f64>() + 0.0;
}

// -------------------------------------------------------------- agents ---

/// Adds agent records to the entry of the same agent (or a new one), the
/// clients as a sorted union; re-sorts as the engine does (cost, then token
/// total, descending) and recomputes the totals.
pub(crate) fn fold_agents(
    report: &mut tokscale_core::AgentReport,
    records: &[AgentsRecord],
    saturated: &mut bool,
) {
    let entries = &mut report.entries;
    let mut index: HashMap<String, usize> = HashMap::new();
    for (at, entry) in entries.iter().enumerate() {
        index.entry(entry.agent.clone()).or_insert(at);
    }
    // The clients of each entry that gained one, written out once, sorted (an
    // entry that gained none keeps its order, as before).
    let mut sets: HashMap<usize, BTreeSet<String>> = HashMap::new();
    for record in records {
        let at = match index.get(&record.agent) {
            Some(at) => *at,
            None => {
                index.insert(record.agent.clone(), entries.len());
                entries.push(tokscale_core::AgentReportEntry {
                    agent: record.agent.clone(),
                    clients: Vec::new(),
                    input: 0,
                    output: 0,
                    cache_read: 0,
                    cache_write: 0,
                    reasoning: 0,
                    cost: 0.0,
                    messages: 0,
                });
                entries.len() - 1
            }
        };
        let entry = &mut entries[at];
        add_i64(&mut entry.input, record.input_tokens, saturated);
        add_i64(&mut entry.output, record.output_tokens, saturated);
        add_i64(&mut entry.cache_read, record.cache_read_tokens, saturated);
        add_i64(&mut entry.cache_write, record.cache_write_tokens, saturated);
        add_i64(&mut entry.reasoning, record.reasoning_tokens, saturated);
        add_i32(&mut entry.messages, record.message_count, saturated);
        entry.cost += usd(record.cost_nano_usd);
        let known = sets.get(&at).map_or_else(
            || entry.clients.contains(&record.client),
            |set| set.contains(&record.client),
        );
        if !known {
            sets.entry(at)
                .or_insert_with(|| entry.clients.iter().cloned().collect())
                .insert(record.client.clone());
        }
    }
    for (at, clients) in sets {
        entries[at].clients = clients.into_iter().collect();
    }
    let total = |e: &tokscale_core::AgentReportEntry| {
        e.input
            .saturating_add(e.output)
            .saturating_add(e.cache_read)
            .saturating_add(e.cache_write)
            .saturating_add(e.reasoning)
    };
    entries.sort_by(|a, b| {
        b.cost
            .partial_cmp(&a.cost)
            .unwrap_or(std::cmp::Ordering::Equal)
            .then(total(b).cmp(&total(a)))
    });
    report.total_cost = entries.iter().map(|e| e.cost).sum::<f64>() + 0.0;
    report.total_messages = entries
        .iter()
        .map(|e| e.messages)
        .fold(0, i32::saturating_add);
}

// --------------------------------------------------------------- pages ---

/// The new keys on every device-aware payload.
fn decorate(
    mut data: Value,
    devices: Vec<Value>,
    sources: usize,
    saturated: bool,
    limited: bool,
) -> Result<Value, String> {
    let object = data.as_object_mut().ok_or("internal")?;
    object.insert("devices".into(), Value::Array(devices));
    object.insert("duplicateWarning".into(), Value::Bool(sources >= 2));
    object.insert("saturated".into(), Value::Bool(saturated));
    object.insert("remoteRangeLimited".into(), Value::Bool(limited));
    Ok(data)
}

/// The arguments of one device-aware page call.
pub(crate) struct Call<'a> {
    pub agent: &'a RemoteAgent,
    pub context: &'a crate::LocalSourceContext,
    pub year: &'a str,
    pub clients: Option<Vec<String>>,
    pub selection: Selection,
    pub timezone: &'a str,
    pub mode: Mode,
    pub today: NaiveDate,
}

impl Call<'_> {
    /// The query sent to each producer, with whether its range is narrower
    /// than the local one. Clients are sorted and unique, as the request
    /// requires.
    fn query(&self, endpoint: Endpoint) -> Result<(ReportQuery, bool), String> {
        let (start_date, end_date_exclusive, limited) = remote_range(self.year, self.today)?;
        let mut clients = self.clients.clone().unwrap_or_default();
        clients.sort();
        clients.dedup();
        Ok((
            ReportQuery {
                endpoint,
                start_date,
                end_date_exclusive,
                timezone: self.timezone.to_owned(),
                clients,
            },
            limited,
        ))
    }

    fn sources(&self) -> usize {
        usize::from(self.selection.local) + self.selection.peers.len()
    }

    fn devices(&self, remote: Vec<Value>, local_totals: Option<Value>) -> Vec<Value> {
        let mut devices = Vec::with_capacity(remote.len() + 1);
        if self.selection.local {
            devices.push(local_device(local_totals));
        }
        devices.extend(remote);
        devices
    }

    /// Only the local source: the existing body, then the new keys.
    fn local_only(&self, data: Value) -> Result<Value, String> {
        decorate(data, self.devices(Vec::new(), None), 1, false, false)
    }

    /// The remote part of a page, when any producer is selected, from
    /// `source` (the production source is [`Call::gather`]).
    fn remote(
        &self,
        endpoint: Endpoint,
        source: impl FnOnce(&ReportQuery) -> Remote,
    ) -> Result<(Remote, bool), String> {
        let (query, limited) = self.query(endpoint)?;
        Ok((source(&query), limited))
    }

    /// The production remote source: [`gather`] over the selected producers.
    fn gather(&self, query: &ReportQuery) -> Remote {
        gather(self.agent, &self.selection.peers, self.mode, query)
    }
}

/// The merged graph's `meta.pricingMode` and `meta.costCoverage` (Windows
/// only; the macOS payload has no such fields to keep true).
///
/// - `pricingMode`: the local contract's when the local source is selected
///   (the local part is computed as `tb_graph` computes it,
///   `usage_graph::compute(…, false)`, so `bestEffort` in practice);
///   `bestEffort` when only producers are selected.
/// - `costCoverage`: a fold over the sources that contributed records. The
///   local contract's coverage takes part when the local source is selected.
///   A producer takes part when its records were merged (its row has
///   non-empty `totals`): `complete` when its `costIncomplete` is false,
///   otherwise `partial` when it contributed any cost (a `costUsd` above
///   zero), otherwise `none`. A producer that contributed nothing
///   (unavailable, refused, left out, or an empty part) does not take part.
///   All `complete` → `complete`; all `none` → `none`; anything else →
///   `partial`; no source took part (only producers selected and none
///   merged) → `complete`, as the engine's own fold answers for no messages.
pub(crate) fn merged_contract(
    local: Option<GraphMetaContract>,
    devices: &[Value],
) -> GraphMetaContract {
    let remote = devices.iter().filter_map(|row| {
        let totals = row["totals"]
            .as_array()
            .filter(|totals| !totals.is_empty())?;
        Some(if row["costIncomplete"] != Value::Bool(true) {
            CostCoverage::Complete
        } else if totals
            .iter()
            .any(|total| total["costUsd"].as_f64().is_some_and(|cost| cost > 0.0))
        {
            CostCoverage::Partial
        } else {
            CostCoverage::None
        })
    });
    let mut coverages = local.map(|c| c.cost_coverage).into_iter().chain(remote);
    let cost_coverage = match coverages.next() {
        None => CostCoverage::Complete,
        Some(first) if coverages.all(|c| c == first) => first,
        Some(_) => CostCoverage::Partial,
    };
    GraphMetaContract {
        pricing_mode: local.map_or(GraphPricingMode::BestEffort, |c| c.pricing_mode),
        cost_coverage,
    }
}

pub(crate) fn graph(call: &Call<'_>) -> Result<Value, String> {
    graph_with(call, |query| call.gather(query))
}

/// [`graph`] with its remote part from `source`. Only the local source: the
/// existing `tb_graph` body (its cache, else its compute), unchanged. See
/// [`merged_contract`] for the merged payload's `meta` contract.
pub(crate) fn graph_with(
    call: &Call<'_>,
    source: impl FnOnce(&ReportQuery) -> Remote,
) -> Result<Value, String> {
    if call.selection.peers.is_empty() {
        let data = match crate::graph_cached(
            call.context,
            call.year,
            Duration::from_secs(crate::ONESHOT_MAX_AGE_SECS),
        ) {
            Some(data) => data,
            None => crate::graph_compute(call.context, call.year)?,
        };
        return call.local_only(data);
    }
    let (mut graph, local_contract) = if call.selection.local {
        let (graph, contract) = crate::usage_graph::compute(call.context, call.year, false)?;
        (graph, Some(contract))
    } else {
        crate::usage_graph::normalize_year(call.year)?;
        (empty_graph(), None)
    };
    let local_totals = call.selection.local.then(|| graph_totals(&graph));
    let (remote, limited) = call.remote(Endpoint::Graph, source)?;
    let mut saturated = remote.saturated;
    if let Some(Records::Graph(records)) = &remote.records {
        fold_graph(&mut graph, records, &mut saturated);
    }
    let contract = merged_contract(local_contract, &remote.devices);
    let data = crate::usage_graph::to_payload(graph, contract)?;
    decorate(
        data,
        call.devices(remote.devices, local_totals),
        call.sources(),
        saturated,
        limited,
    )
}

pub(crate) fn models(call: &Call<'_>) -> Result<Value, String> {
    models_with(call, |query| call.gather(query))
}

pub(crate) fn models_with(
    call: &Call<'_>,
    source: impl FnOnce(&ReportQuery) -> Remote,
) -> Result<Value, String> {
    if call.selection.peers.is_empty() {
        return call.local_only(crate::model_report::run(call.context, call.year)?);
    }
    let mut report = if call.selection.local {
        crate::model_report::compute(call.context, call.year)?
    } else {
        crate::usage_graph::normalize_year(call.year)?;
        tokscale_core::ModelReport {
            entries: Vec::new(),
            total_input: 0,
            total_output: 0,
            total_cache_read: 0,
            total_cache_write: 0,
            total_messages: 0,
            total_cost: 0.0,
            processing_time_ms: 0,
        }
    };
    let local_totals = call.selection.local.then(|| models_totals(&report));
    let (remote, limited) = call.remote(Endpoint::Models, source)?;
    let mut saturated = remote.saturated;
    let touched = match &remote.records {
        Some(Records::Models(records)) => fold_models(&mut report, records, &mut saturated),
        _ => vec![false; report.entries.len()],
    };
    let pricing = tokscale_core::pricing::PricingService::load_cached_any_age();
    let data = models_payload(report, &touched, pricing.as_ref())?;
    decorate(
        data,
        call.devices(remote.devices, local_totals),
        call.sources(),
        saturated,
        limited,
    )
}

/// The mapped model report, where a row holding any remote part has no
/// `costEstimate`: the estimate prices only local tokens with the local
/// table, so `cost / costEstimate` would misread a summed row (and a
/// producer could make the owner's row look implausible).
pub(crate) fn models_payload(
    report: tokscale_core::ModelReport,
    touched: &[bool],
    pricing: Option<&tokscale_core::pricing::PricingService>,
) -> Result<Value, String> {
    let mut data = crate::model_report::map_report(report, pricing);
    for (entry, touched) in data.entries.iter_mut().zip(touched) {
        if *touched {
            entry.cost_estimate = None;
        }
    }
    serde_json::to_value(data).map_err(|e| format!("serialize model report: {e}"))
}

pub(crate) fn hourly(call: &Call<'_>) -> Result<Value, String> {
    hourly_with(call, |query| call.gather(query))
}

pub(crate) fn hourly_with(
    call: &Call<'_>,
    source: impl FnOnce(&ReportQuery) -> Remote,
) -> Result<Value, String> {
    if call.selection.peers.is_empty() {
        return call.local_only(crate::hourly_report::run(
            call.context,
            call.year,
            call.clients.clone(),
        )?);
    }
    let mut report = if call.selection.local {
        crate::hourly_report::compute(call.context, call.year, call.clients.clone())?
    } else {
        crate::usage_graph::normalize_year(call.year)?;
        tokscale_core::HourlyReport {
            entries: Vec::new(),
            total_cost: 0.0,
            processing_time_ms: 0,
        }
    };
    let (remote, limited) = call.remote(Endpoint::Hourly, source)?;
    let mut saturated = remote.saturated;
    if let Some(Records::Hourly(records)) = &remote.records {
        fold_hourly(&mut report, records, &mut saturated);
    }
    let data = serde_json::to_value(crate::hourly_report::map_report(report))
        .map_err(|e| format!("serialize hourly report: {e}"))?;
    decorate(
        data,
        call.devices(remote.devices, None),
        call.sources(),
        saturated,
        limited,
    )
}

pub(crate) fn agents(call: &Call<'_>) -> Result<Value, String> {
    agents_with(call, |query| call.gather(query))
}

pub(crate) fn agents_with(
    call: &Call<'_>,
    source: impl FnOnce(&ReportQuery) -> Remote,
) -> Result<Value, String> {
    if call.selection.peers.is_empty() {
        return call.local_only(crate::agents_report::run(
            call.context,
            call.year,
            call.clients.clone(),
        )?);
    }
    let mut report = if call.selection.local {
        crate::agents_report::compute(call.context, call.year, call.clients.clone())?
    } else {
        crate::usage_graph::normalize_year(call.year)?;
        tokscale_core::AgentReport {
            entries: Vec::new(),
            total_cost: 0.0,
            total_messages: 0,
            processing_time_ms: 0,
        }
    };
    let (remote, limited) = call.remote(Endpoint::Agents, source)?;
    let mut saturated = remote.saturated;
    if let Some(Records::Agents(records)) = &remote.records {
        fold_agents(&mut report, records, &mut saturated);
    }
    let data = serde_json::to_value(crate::agents_report::map_report(report))
        .map_err(|e| format!("serialize agents report: {e}"))?;
    decorate(
        data,
        call.devices(remote.devices, None),
        call.sources(),
        saturated,
        limited,
    )
}

/// Today in the process's timezone, which is the one the app passes as
/// `timezone`.
pub(crate) fn today() -> NaiveDate {
    chrono::Local::now().date_naive()
}

#[cfg(test)]
mod tests {
    //! Hermetic. Every agent lives in a temp home with a protected owner-only
    //! DACL (`remote_agent::tests::TempHome`), removed by its exact path,
    //! with every input injected; every listener binds `127.0.0.1`. The tests
    //! that read the app's own local sources (`LocalSourceContext::process`,
    //! the graph cache) run in the env-cleared child of
    //! `roots_acceptance::run_in_child_at`, whose `HOME` is an empty fixture
    //! root; on Windows that child still scans the real RoamingAppData and
    //! LocalAppData known folders for some clients, so every local session
    //! here is dated 2021 and asserted on, and the fixtures assert that their
    //! local totals are empty. The pure tests build their inputs. Nothing
    //! opens the real agent root.

    use super::*;
    use crate::remote_agent::tests::{
        digest, free_port, inputs, start_at, TempHome, TestClock, PERIOD,
    };
    use crate::remote_agent::MakeInputs;
    use crate::roots_acceptance::{child_root, run_in_child_at, run_in_child_with};
    use std::{
        ffi::{c_char, CStr, CString},
        fs,
        net::TcpListener,
        path::{Path, PathBuf},
        sync::{
            atomic::{AtomicUsize, Ordering},
            Arc,
        },
        time::{SystemTime, UNIX_EPOCH},
    };
    use syrtis_agent::{agent::ScannerSettings, peer_store::PeerState, security::hex_encode};

    // ----------------------------------------------------------- harness ---

    /// The full path of a test of this module, for the env-cleared child.
    fn path_of(test: &str) -> String {
        format!("remote_pages::tests::{test}")
    }

    fn make(home: &Path, clock: &Arc<TestClock>, settings: ScannerSettings) -> MakeInputs {
        let (home, clock) = (home.to_path_buf(), Arc::clone(clock));
        Arc::new(move || Ok(inputs(&home, &clock, settings.clone())))
    }

    fn agent_at(home: &Path, clock: &Arc<TestClock>) -> Arc<RemoteAgent> {
        let agent = Arc::new(RemoteAgent::default());
        start_at(&agent, home, clock, ScannerSettings::default());
        agent
    }

    /// One Claude Code assistant line.
    fn claude(id: &str, at: &str, model: &str, input: u64, output: u64, sidechain: bool) -> String {
        let side = if sidechain {
            r#""isSidechain":true,"agentId":"x","#
        } else {
            ""
        };
        format!(
            r#"{{"type":"assistant",{side}"timestamp":"{at}","requestId":"req_{id}","message":{{"id":"msg_{id}","model":"{model}","usage":{{"input_tokens":{input},"output_tokens":{output}}}}}}}"#
        )
    }

    /// Writes a Claude Code session below `home`'s `.claude` (a profile
    /// home: the child's `HOME`, or a producer's scan `home`).
    fn write_claude(home: &Path, file: &str, lines: &[String]) {
        let dir = home.join(".claude").join("projects").join("fixture");
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join(file), lines.join("\n") + "\n").unwrap();
    }

    /// A session in a producer's scan home (`remote_agent::tests::roots`:
    /// `<home>\profile`).
    fn write_session(home: &Path, file: &str, lines: &[String]) {
        write_claude(&home.join("profile"), file, lines);
    }

    /// A producer with sharing on, an approved scope and a listener.
    fn producer(home: &Path, clock: &Arc<TestClock>) -> Arc<RemoteAgent> {
        share(agent_at(home, clock), None)
    }

    /// Listener on (with `label`), the disclosed scope approved, sharing on.
    ///
    /// `free_port` releases its listener, so a test running beside this one
    /// can take the port before the agent binds it (the listen command then
    /// answers `internal`): a few fresh ports are tried.
    fn share(agent: Arc<RemoteAgent>, label: Option<&str>) -> Arc<RemoteAgent> {
        let listened = (0..5).find_map(|_| {
            agent
                .listen_configure("127.0.0.1", free_port(), label.map(Into::into))
                .ok()
        });
        assert!(listened.is_some(), "no free port could be listened on");
        agent.scope_approve(&digest(&agent)).unwrap();
        agent.sharing_set(true).unwrap();
        agent
    }

    /// Offer → claim → approve → poll; returns the producer static.
    fn pair(producer: &RemoteAgent, receiver: &RemoteAgent) -> [u8; 32] {
        let offer = producer.offer_create(600).unwrap();
        let claim = receiver
            .pair_claim(offer["pairUrl"].as_str().unwrap())
            .unwrap();
        let pending = producer.pending_list().unwrap()[0]["pendingId"]
            .as_str()
            .unwrap()
            .to_owned();
        producer.decide(true, &pending, 1).unwrap();
        let paired = receiver
            .pair_poll(claim["handle"].as_u64().unwrap())
            .unwrap();
        decode_fixed_hex::<32>(paired["producerStatic"].as_str().unwrap()).unwrap()
    }

    /// A listener that counts nothing but whether anyone connected.
    fn trap() -> TcpListener {
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        listener.set_nonblocking(true).unwrap();
        listener
    }

    fn connected(listener: &TcpListener) -> bool {
        listener.accept().is_ok()
    }

    /// A usable record in `receiver`'s peer store for a producer that does
    /// not exist, at `port`; `created_at_ms` as given.
    fn fake_peer(receiver: &RemoteAgent, key: u8, port: u16, created_at_ms: i64) -> [u8; 32] {
        fake_peer_as(receiver, [key; 32], key, port, created_at_ms)
    }

    /// [`fake_peer`] with a given responder static.
    fn fake_peer_as(
        receiver: &RemoteAgent,
        responder: [u8; 32],
        key: u8,
        port: u16,
        created_at_ms: i64,
    ) -> [u8; 32] {
        receiver
            .with_agent(|agent| {
                let identity = agent
                    .runtime()
                    .store()
                    .state()
                    .unwrap()
                    .identity_public()
                    .unwrap();
                agent
                    .runtime()
                    .peers()
                    .upsert(PeerRecord {
                        auth_epoch: 1,
                        created_at_ms,
                        expires_at_ms: i64::MAX / 2,
                        grant_id: [key; 16],
                        host: "127.0.0.1".into(),
                        initiator_static: identity,
                        node_label: None,
                        pending_id: [key; 16],
                        port,
                        responder_static: responder,
                        source_scope_generation: 1,
                        state: PeerState::Active,
                    })
                    .unwrap()
            })
            .unwrap();
        responder
    }

    /// The year every integration call asks (see the module comment).
    const YEAR: &str = "2021";

    fn call<'a>(
        agent: &'a RemoteAgent,
        context: &'a crate::LocalSourceContext,
        local: bool,
        peers: &[[u8; 32]],
        timezone: &'a str,
        mode: Mode,
    ) -> Call<'a> {
        Call {
            agent,
            context,
            year: YEAR,
            clients: None,
            selection: Selection {
                local,
                peers: peers.to_vec(),
            },
            timezone,
            mode,
            today: NaiveDate::from_ymd_opt(2021, 6, 1).unwrap(),
        }
    }

    /// A context for calls that select no local source (it is never read).
    fn unused_context() -> crate::LocalSourceContext {
        crate::LocalSourceContext::capture(
            Some(PathBuf::from(r"C:\nonexistent-sa4c3")),
            false,
            Default::default(),
        )
        .unwrap()
    }

    fn states(data: &Value) -> Vec<(String, String)> {
        data["devices"]
            .as_array()
            .unwrap()
            .iter()
            .map(|d| {
                (
                    d["id"].as_str().unwrap().to_owned(),
                    d["state"].as_str().unwrap().to_owned(),
                )
            })
            .collect()
    }

    fn day<'a>(graph: &'a Value, date: &str) -> &'a Value {
        graph["contributions"]
            .as_array()
            .unwrap()
            .iter()
            .find(|d| d["date"] == date)
            .unwrap_or(&Value::Null)
    }

    fn find<'a>(page: &'a Value, key: &str, value: &str) -> &'a Value {
        page["entries"]
            .as_array()
            .unwrap()
            .iter()
            .find(|e| e[key] == value)
            .unwrap_or(&Value::Null)
    }

    fn take(raw: *mut c_char) -> Value {
        let text = unsafe { CStr::from_ptr(raw) }.to_str().unwrap().to_owned();
        unsafe { crate::tb_free(raw) };
        serde_json::from_str(&text).unwrap()
    }

    fn c(text: &str) -> CString {
        CString::new(text).unwrap()
    }

    // ------------------------------------------------- pure: arguments ---

    #[test]
    fn selection_and_mode_are_validated() {
        let a = "a".repeat(64);
        assert_eq!(
            parse_selection(&format!(r#"{{"local":true,"peers":["{a}"]}}"#)),
            Ok(Selection {
                local: true,
                peers: vec![[0xaa; 32]]
            })
        );
        for bad in [
            r#"{"local":false,"peers":[]}"#.to_owned(),
            format!(r#"{{"local":true,"peers":["{a}","{a}"]}}"#),
            format!(r#"{{"local":true,"peers":["{}"]}}"#, "A".repeat(64)),
            r#"{"local":true,"peers":["ab"]}"#.to_owned(),
            r#"{"local":true,"peers":[],"x":1}"#.to_owned(),
            format!(
                r#"{{"local":false,"peers":[{}]}}"#,
                (0..17)
                    .map(|i| format!("\"{}\"", hex_encode(&[i as u8; 32])))
                    .collect::<Vec<_>>()
                    .join(",")
            ),
        ] {
            assert_eq!(parse_selection(&bad), Err("invalid".into()), "{bad}");
        }
        assert_eq!(parse_mode("cached"), Ok(Mode::Cached));
        assert_eq!(parse_mode("refresh"), Ok(Mode::Refresh));
        assert_eq!(parse_mode("Refresh"), Err("invalid".into()));
    }

    /// Acceptance (6): all time asks the 370 days up to today and says the
    /// remote range is limited; a year asks that calendar year.
    #[test]
    fn all_time_asks_the_last_370_days_and_a_year_its_calendar_year() {
        let today = NaiveDate::from_ymd_opt(2026, 11, 12).unwrap();
        let (start, end, limited) = remote_range("", today).unwrap();
        assert_eq!((end.as_str(), limited), ("2026-11-13", true));
        let days = NaiveDate::parse_from_str(&end, "%Y-%m-%d").unwrap()
            - NaiveDate::parse_from_str(&start, "%Y-%m-%d").unwrap();
        assert_eq!(days.num_days(), 370);
        assert_eq!(
            remote_range("2024", today).unwrap(),
            ("2024-01-01".into(), "2025-01-01".into(), false)
        );
        assert!(remote_range("24", today).is_err());
    }

    #[test]
    fn the_engine_query_check_refuses_an_unknown_timezone() {
        let query = |timezone: &str| ReportQuery {
            endpoint: Endpoint::Graph,
            start_date: "2025-01-01".into(),
            end_date_exclusive: "2026-01-01".into(),
            timezone: timezone.into(),
            clients: Vec::new(),
        };
        assert!(query_accepted(&query("UTC")));
        assert!(query_accepted(&query("Asia/Tokyo")));
        assert!(!query_accepted(&query("Not/AZone")));
    }

    // ---------------------------------------------- pure: state and fold ---

    /// Acceptance (1c), the fixed mapping row by row.
    #[test]
    fn state_mapping_is_fixed() {
        assert_eq!(state_of(Some(PartStatus::Fresh), Some(1)), "fresh");
        assert_eq!(state_of(Some(PartStatus::LastGood), Some(1)), "lastGood");
        assert_eq!(state_of(Some(PartStatus::LastGood), None), "stale");
        assert_eq!(state_of(Some(PartStatus::Unavailable), None), "unavailable");
        assert_eq!(state_of(Some(PartStatus::Refused), Some(1)), "refused");
        assert_eq!(
            state_of(Some(PartStatus::InvalidReply), None),
            "invalidReply"
        );
        assert_eq!(state_of(Some(PartStatus::Invalid), None), "invalidReply");
    }

    fn graph_record(date: &str, input: u64, cost: u64) -> GraphRecord {
        GraphRecord {
            date: date.into(),
            client: "claude".into(),
            model: Some("m".into()),
            provider: Some("p".into()),
            input_tokens: input,
            output_tokens: 0,
            cache_read_tokens: 0,
            cache_write_tokens: 0,
            reasoning_tokens: 0,
            total_tokens: input,
            message_count: 1,
            turn_count: 1,
            cost_nano_usd: cost,
        }
    }

    fn hourly_record(bucket: i64, offset: i32, input: u64) -> HourlyRecord {
        HourlyRecord {
            bucket_start_unix_ms: bucket,
            utc_offset_seconds: offset,
            client: "claude".into(),
            model: Some("m".into()),
            provider: Some("p".into()),
            input_tokens: input,
            output_tokens: 0,
            cache_read_tokens: 0,
            cache_write_tokens: 0,
            reasoning_tokens: 0,
            total_tokens: input,
            message_count: 1,
            turn_count: 0,
            cost_nano_usd: 0,
        }
    }

    fn graph_query() -> ReportQuery {
        ReportQuery {
            endpoint: Endpoint::Graph,
            start_date: "2025-01-01".into(),
            end_date_exclusive: "2026-01-01".into(),
            timezone: "UTC".into(),
            clients: Vec::new(),
        }
    }

    fn pulled(key: u8, records: Records) -> Pulled {
        Pulled {
            peer: [key; 32],
            grant: [key; 16],
            address: Some(format!("10.0.0.{key}:4000")),
            name: None,
            got: Got::Fresh(Report {
                records,
                saturated: false,
            }),
            cost_incomplete: false,
            ever_answered: true,
            last_success: Some(7),
            reason: None,
        }
    }

    /// DR-2b2: code 6 is the `incompatible` state with no reason and no
    /// cached fallback; the other failures keep their class and reason.
    #[test]
    fn code_6_is_incompatible_and_other_failures_keep_their_class() {
        let cached = || -> (Got, bool) { panic!("code 6 must not read the cache") };
        let (got, reason, incomplete) = failed(PullError::Fetch(FetchError::Incompatible), cached);
        assert!(matches!(got, Got::Incompatible));
        assert_eq!((reason, incomplete), (None, false));
        // Control: no answer falls back to the cache (and its flag); a
        // rejection does not.
        let (got, reason, incomplete) = failed(PullError::Fetch(FetchError::Timeout), || {
            (Got::Cached(None), true)
        });
        assert!(matches!(got, Got::Cached(None)));
        assert_eq!((reason, incomplete), (Some("timeout"), true));
        let (got, reason, _) = failed(PullError::Fetch(FetchError::Rejected), cached);
        assert!(matches!(got, Got::Failed(FailureClass::Refused)));
        assert_eq!(reason, Some("error"));
    }

    /// DR-2b2: a row's `label` is the producer's name, else the address;
    /// `address` is always the record's; `costIncomplete` counts only for a
    /// part that was merged; `everAnswered` is passed through.
    #[test]
    fn dr2b2_label_address_and_flags_of_a_row() {
        let named = |key: u8, name: Option<&str>, incomplete: bool| {
            let mut item = pulled(key, Records::Graph(vec![graph_record("2025-03-10", 1, 0)]));
            item.name = name.map(Into::into);
            item.cost_incomplete = incomplete;
            item
        };
        // Same name and state on two devices: two rows, told apart by id.
        let mut refused = named(3, Some("Studio Mac"), true);
        refused.got = Got::Failed(FailureClass::Refused);
        let remote = assemble(
            &graph_query(),
            vec![
                named(1, Some("Studio Mac"), true),
                named(2, None, false),
                refused,
            ],
        );
        let rows = &remote.devices;
        assert_eq!(rows[0]["label"], "Studio Mac");
        assert_eq!(rows[0]["address"], "10.0.0.1:4000");
        assert_eq!(rows[0]["costIncomplete"], true);
        assert_eq!(rows[0]["everAnswered"], true);
        assert_eq!(rows[1]["label"], "10.0.0.2:4000", "no name: the address");
        assert_eq!(rows[1]["address"], "10.0.0.2:4000");
        assert_eq!(rows[1]["costIncomplete"], false);
        assert_ne!(rows[0]["id"], rows[1]["id"]);
        assert_eq!(rows[2]["state"], "refused");
        assert_eq!(rows[2]["costIncomplete"], false, "no part was merged");
        // The local-only rows are unchanged.
        assert_eq!(
            local_device(None),
            json!({"id": "local", "label": null, "state": "local", "lastSuccessAt": null})
        );
    }

    /// DR-2a R3: every pull failure maps to the reason the status line needs,
    /// and the failures without a line of their own have none.
    #[test]
    fn dr2a_reason_of_names_each_failure() {
        let fetch = |e| reason_of(&PullError::Fetch(e));
        let transport = FetchError::Transport(syrtis_agent::noise::NoiseError::Timeout);
        assert_eq!(fetch(transport), Some("unreachable"));
        assert_eq!(fetch(FetchError::Timeout), Some("timeout"));
        assert_eq!(fetch(FetchError::Busy), Some("busy"));
        assert_eq!(fetch(FetchError::TooMany), Some("busy"));
        for stopped in [
            FetchError::Revoked,
            FetchError::Expired,
            FetchError::Unusable,
            FetchError::CheckGrantStatus,
        ] {
            assert_eq!(fetch(stopped), Some("stopped"));
        }
        assert_eq!(reason_of(&PullError::NoPeer), Some("stopped"));
        assert_eq!(fetch(FetchError::Internal), Some("error"));
        assert_eq!(fetch(FetchError::Rejected), Some("error"));
        for none in [
            FetchError::TooLarge,
            FetchError::Memory,
            FetchError::InvalidQuery,
            FetchError::Protocol,
            FetchError::Invalid,
        ] {
            assert_eq!(fetch(none), None);
        }
    }

    /// DR-2a R1: each device row carries its part per client; the rows' sums
    /// are the merged page's totals (graph and models), and a part that was
    /// not merged has `null` totals.
    #[test]
    fn dr2a_device_totals_sum_to_the_merged_page() {
        fn rec(date: &str, client: &str, input: u64, cost: u64) -> GraphRecord {
            GraphRecord {
                client: client.into(),
                ..graph_record(date, input, cost)
            }
        }
        let sum = |rows: &[&Value], key: &str| {
            rows.iter()
                .flat_map(|r| r.as_array().unwrap())
                .map(|t| t[key].as_f64().unwrap())
                .sum::<f64>()
        };
        // Graph. The local part is folded into an empty graph first.
        let mut graph = empty_graph();
        let mut saturated = false;
        fold_graph(
            &mut graph,
            &[
                rec("2025-03-10", "claude", 100, 2_000_000_000),
                rec("2025-03-10", "codex", 40, 1_000_000_000),
            ],
            &mut saturated,
        );
        let local = graph_totals(&graph);
        let remote = assemble(
            &graph_query(),
            vec![
                pulled(
                    1,
                    Records::Graph(vec![
                        rec("2025-03-10", "claude", 7, 500_000_000),
                        rec("2025-03-11", "gemini", 9, 250_000_000),
                    ]),
                ),
                pulled(2, Records::Graph(vec![rec("2025-03-12", "codex", 5, 0)])),
                pulled(3, Records::Graph(vec![rec("2024-01-01", "codex", 99, 0)])),
            ],
        );
        let Some(Records::Graph(records)) = &remote.records else {
            panic!("no merged records")
        };
        fold_graph(&mut graph, records, &mut saturated);
        let rows = [
            &local,
            &remote.devices[0]["totals"],
            &remote.devices[1]["totals"],
        ];
        assert_eq!(remote.devices[2]["state"], "invalidReply");
        assert_eq!(remote.devices[2]["totals"], Value::Null, "not merged");
        let days = &graph.contributions;
        assert_eq!(
            sum(&rows, "tokens"),
            days.iter().map(|d| d.totals.tokens as f64).sum::<f64>()
        );
        assert_eq!(
            sum(&rows, "messages"),
            days.iter()
                .map(|d| f64::from(d.totals.messages))
                .sum::<f64>()
        );
        assert!(
            (sum(&rows, "costUsd") - days.iter().map(|d| d.totals.cost).sum::<f64>()).abs() < 1e-9
        );
        assert_eq!(remote.devices[0]["totals"][0]["client"], "claude");
        assert_eq!(remote.devices[0]["totals"][0]["tokens"], 7);
        assert_eq!(remote.devices[0]["totals"][1]["client"], "gemini");

        // Models.
        let models_query = ReportQuery {
            endpoint: Endpoint::Models,
            ..graph_query()
        };
        let mut report = tokscale_core::ModelReport {
            entries: vec![model_usage("m", 1_000, 1.5)],
            total_input: 0,
            total_output: 0,
            total_cache_read: 0,
            total_cache_write: 0,
            total_messages: 0,
            total_cost: 0.0,
            processing_time_ms: 0,
        };
        let local = models_totals(&report);
        let mut other = models_record("m2", 70, 500_000_000);
        other.client = "codex".into();
        let remote = assemble(
            &models_query,
            vec![
                pulled(
                    1,
                    Records::Models(vec![models_record("m", 30, 250_000_000)]),
                ),
                pulled(4, Records::Models(vec![other])),
            ],
        );
        let Some(Records::Models(records)) = &remote.records else {
            panic!("no merged records")
        };
        fold_models(&mut report, records, &mut saturated);
        let rows = [
            &local,
            &remote.devices[0]["totals"],
            &remote.devices[1]["totals"],
        ];
        assert_eq!(
            sum(&rows, "tokens"),
            report
                .entries
                .iter()
                .map(|e| (e.input + e.output) as f64)
                .sum::<f64>()
        );
        assert_eq!(sum(&rows, "messages"), f64::from(report.total_messages));
        assert!((sum(&rows, "costUsd") - report.total_cost).abs() < 1e-9);
    }

    /// Acceptance (4) and (9b): a producer whose records lie outside the asked
    /// range (a date of another year, an hour bucket near the i64 limit) is
    /// left out whole and marked `invalidReply`; the others are summed.
    #[test]
    fn a_producer_with_records_outside_the_range_is_left_out_alone() {
        let good = Records::Graph(vec![graph_record("2025-03-10", 5, 0)]);
        let also = Records::Graph(vec![graph_record("2025-03-10", 7, 0)]);
        // Sorted as the frozen validation requires, so only the range check
        // can refuse it.
        let outside = Records::Graph(vec![
            graph_record("2024-12-31", 1, 0),
            graph_record("2025-03-10", 1, 0),
        ]);
        assert!(
            syrtis_agent::report::validate_graph_records(match &outside {
                Records::Graph(r) => r,
                _ => unreachable!(),
            })
            .is_ok()
        );
        let remote = assemble(
            &graph_query(),
            vec![pulled(1, good), pulled(2, outside), pulled(3, also)],
        );
        assert_eq!(
            remote.records,
            Some(Records::Graph(
                vec![graph_record("2025-03-10", 12, 0)]
                    .into_iter()
                    .map(|mut r| {
                        r.message_count = 2;
                        r.turn_count = 2;
                        r
                    })
                    .collect()
            ))
        );
        let rows: Vec<_> = remote
            .devices
            .iter()
            .map(|d| d["state"].as_str().unwrap().to_owned())
            .collect();
        assert_eq!(rows, ["fresh", "invalidReply", "fresh"]);
        assert_eq!(remote.devices[0]["id"], fingerprint(&[1; 32]));
        assert_eq!(remote.devices[0]["label"], "10.0.0.1:4000");
        assert_eq!(remote.devices[0]["lastSuccessAt"], 7);

        let hourly = ReportQuery {
            endpoint: Endpoint::Hourly,
            ..graph_query()
        };
        let march = day_ms("2025-03-10").unwrap();
        let ok = Records::Hourly(vec![hourly_record(march, 0, 1)]);
        let extreme = Records::Hourly(vec![hourly_record(i64::MAX - 1, 3_600, 1)]);
        let early = Records::Hourly(vec![hourly_record(
            day_ms("2025-01-01").unwrap() - 3_600_000,
            0,
            1,
        )]);
        // 2025-12-31 23:00 at UTC-8: the bucket itself is past the range end,
        // its local wall time is not.
        let edge = Records::Hourly(vec![hourly_record(
            day_ms("2026-01-01").unwrap() + 7 * 3_600_000,
            -28_800,
            1,
        )]);
        let remote = assemble(
            &hourly,
            vec![
                pulled(1, ok),
                pulled(2, extreme),
                pulled(3, early),
                pulled(4, edge),
            ],
        );
        let rows: Vec<_> = remote
            .devices
            .iter()
            .map(|d| d["state"].as_str().unwrap().to_owned())
            .collect();
        assert_eq!(rows, ["fresh", "invalidReply", "invalidReply", "fresh"]);
        let Some(Records::Hourly(kept)) = remote.records else {
            panic!()
        };
        assert_eq!(kept.len(), 2);
    }

    /// Review fix 1: a bucket inside the range with an offset of a year moves
    /// that producer's numbers into the owner's older local slots; the
    /// producer is left out whole, the local slots and the other producer are
    /// unchanged.
    #[test]
    fn an_out_of_range_bucket_moved_into_range_by_its_offset_is_left_out() {
        let hourly = ReportQuery {
            endpoint: Endpoint::Hourly,
            ..graph_query()
        };
        let year = 365 * 86_400;
        let five_last_year = day_ms("2024-03-10").unwrap() + 5 * 3_600_000;
        assert_eq!(
            hour_slot(&hourly_record(five_last_year, year, 1)).unwrap(),
            "2025-03-10 05:00",
            "control: bucket plus offset lands inside the asked year"
        );
        let moved = Records::Hourly(vec![hourly_record(five_last_year, year, 1_000)]);
        let five = day_ms("2025-03-10").unwrap() + 5 * 3_600_000;
        let widest = Records::Hourly(vec![hourly_record(five, 14 * 3_600, 1)]);
        let remote = assemble(&hourly, vec![pulled(1, moved), pulled(2, widest)]);
        let rows: Vec<_> = remote
            .devices
            .iter()
            .map(|d| d["state"].as_str().unwrap().to_owned())
            .collect();
        assert_eq!(rows, ["invalidReply", "fresh"]);
    }

    #[test]
    fn an_in_range_bucket_with_an_offset_of_a_year_is_left_out() {
        let hourly = ReportQuery {
            endpoint: Endpoint::Hourly,
            ..graph_query()
        };
        let five = day_ms("2025-03-10").unwrap() + 5 * 3_600_000;
        let shifted = Records::Hourly(vec![hourly_record(five, -365 * 86_400, 1_000)]);
        assert_eq!(
            hour_slot(&hourly_record(five, -365 * 86_400, 1)).unwrap(),
            "2024-03-10 05:00",
            "control: the offset alone moves the slot a year back"
        );
        let honest = Records::Hourly(vec![hourly_record(five, 0, 1)]);
        let remote = assemble(&hourly, vec![pulled(1, shifted), pulled(2, honest)]);
        let rows: Vec<_> = remote
            .devices
            .iter()
            .map(|d| d["state"].as_str().unwrap().to_owned())
            .collect();
        assert_eq!(rows, ["invalidReply", "fresh"]);
        let Some(Records::Hourly(kept)) = remote.records else {
            panic!()
        };
        let slot = |hour: &str, input| tokscale_core::HourlyUsage {
            hour: hour.into(),
            clients: vec!["codex".into()],
            models: vec![],
            input,
            output: 0,
            cache_read: 0,
            cache_write: 0,
            message_count: 1,
            turn_count: 0,
            reasoning: 0,
            cost: 0.0,
        };
        let mut local = tokscale_core::HourlyReport {
            entries: vec![slot("2024-03-10 05:00", 100), slot("2025-03-10 05:00", 5)],
            total_cost: 0.0,
            processing_time_ms: 0,
        };
        fold_hourly(&mut local, &kept, &mut false);
        let inputs: Vec<_> = local
            .entries
            .iter()
            .map(|e| (e.hour.as_str(), e.input))
            .collect();
        assert_eq!(inputs, [("2024-03-10 05:00", 100), ("2025-03-10 05:00", 6)]);
    }

    /// Acceptance (9b): the record limits leave the producer out whole.
    #[test]
    fn a_producer_over_the_record_limits_is_left_out_whole() {
        let many = |count: usize| {
            Records::Graph(
                (0..count)
                    .map(|i| {
                        let mut r = graph_record("2025-03-10", 1, 0);
                        r.client = format!("c{i:06}");
                        r
                    })
                    .collect(),
            )
        };
        let remote = assemble(
            &graph_query(),
            vec![pulled(1, many(PEER_RECORDS_MAX + 1)), pulled(2, many(3))],
        );
        let rows: Vec<_> = remote
            .devices
            .iter()
            .map(|d| d["state"].as_str().unwrap().to_owned())
            .collect();
        assert_eq!(rows, ["overLimit", "fresh"]);
        let Some(Records::Graph(kept)) = remote.records else {
            panic!()
        };
        assert_eq!(kept.len(), 3);

        // Four at the per-producer cap fill the total; the fifth is out.
        let parts = (1..=5).map(|k| pulled(k, many(PEER_RECORDS_MAX))).collect();
        let remote = assemble(&graph_query(), parts);
        let rows: Vec<_> = remote
            .devices
            .iter()
            .map(|d| d["state"].as_str().unwrap().to_owned())
            .collect();
        assert_eq!(rows, ["fresh", "fresh", "fresh", "fresh", "overLimit"]);
    }

    /// Acceptance (8): sums that pass the payload's integer width saturate
    /// and say so.
    #[test]
    fn sums_past_the_payload_width_saturate_and_are_flagged() {
        let mut graph = empty_graph();
        let mut saturated = false;
        let mut big = graph_record("2025-03-10", (i64::MAX / 2) as u64, 0);
        big.message_count = u64::from(u32::MAX);
        fold_graph(&mut graph, &[big], &mut saturated);
        assert!(saturated, "a count above i32 must flag");
        let day = &graph.contributions[0];
        assert_eq!(day.token_breakdown.input, i64::MAX / 2);
        assert_eq!(day.totals.messages, i32::MAX);

        let mut saturated = false;
        fold_graph(
            &mut empty_graph(),
            &[graph_record("2025-03-10", u64::MAX, 0)],
            &mut saturated,
        );
        assert!(saturated, "a u64 above i64 must flag");

        let mut saturated = false;
        let mut near = graph_record("2025-03-10", (i64::MAX - 10) as u64, 0);
        near.message_count = 0;
        near.turn_count = 0;
        fold_graph(&mut graph, &[near], &mut saturated);
        assert!(saturated, "two near-limit values must flag");
        assert_eq!(graph.contributions[0].token_breakdown.input, i64::MAX);

        let mut saturated = false;
        fold_graph(
            &mut empty_graph(),
            &[graph_record("2025-03-10", 1, 0)],
            &mut saturated,
        );
        assert!(!saturated, "a small sum must not flag");
    }

    /// Acceptance (1e): the intensity copy gives, day by day, the engine's
    /// own intensity on local data (`aggregate_by_date` runs the engine's
    /// private rule). The costs sit on and around each quarter.
    #[test]
    fn intensity_matches_the_engine() {
        let costs = [
            0.0, 0.1, 0.24, 0.25, 0.26, 0.49, 0.5, 0.51, 0.74, 0.75, 0.76, 1.0,
        ];
        let messages = costs
            .iter()
            .enumerate()
            .map(|(day, cost)| {
                tokscale_core::UnifiedMessage::new(
                    "claude",
                    "m",
                    "p",
                    format!("s{day}"),
                    1_735_689_600_000 + day as i64 * 86_400_000,
                    tokscale_core::TokenBreakdown {
                        input: 1,
                        ..Default::default()
                    },
                    *cost,
                )
            })
            .collect();
        let engine = tokscale_core::aggregate_by_date(messages);
        assert_eq!(engine.len(), costs.len());
        let mut ours = engine.clone();
        for day in &mut ours {
            day.intensity = 9;
        }
        intensities(&mut ours);
        let pairs: Vec<_> = engine
            .iter()
            .zip(&ours)
            .map(|(e, o)| (e.intensity, o.intensity))
            .collect();
        assert!(pairs.iter().all(|(e, o)| e == o), "{pairs:?}");
        // Every level occurs, so a changed threshold shows.
        for level in 1..=4u8 {
            assert!(engine.iter().any(|d| d.intensity == level), "level {level}");
        }
    }

    fn model_usage(model: &str, input: i64, cost: f64) -> tokscale_core::ModelUsage {
        tokscale_core::ModelUsage {
            client: "opencode".into(),
            merged_clients: None,
            workspace_key: None,
            workspace_label: None,
            session_id: None,
            model: model.into(),
            provider: "deepseek".into(),
            input,
            output: 0,
            cache_read: 0,
            cache_write: 0,
            reasoning: 0,
            message_count: 1,
            cost,
            performance: tokscale_core::ModelPerformance {
                ms_per_1k_tokens: Some(1000.0),
                total_duration_ms: 1_000,
                timed_tokens: 1_000,
                sample_count: 1,
                token_coverage: 1.0,
            },
        }
    }

    fn models_record(model: &str, input: u64, cost_nano: u64) -> ModelsRecord {
        ModelsRecord {
            client: "opencode".into(),
            model: Some(model.into()),
            provider: Some("deepseek".into()),
            input_tokens: input,
            output_tokens: 0,
            cache_read_tokens: 0,
            cache_write_tokens: 0,
            reasoning_tokens: 0,
            total_tokens: input,
            message_count: 1,
            turn_count: 0,
            cost_nano_usd: cost_nano,
            duration_millis: 3_000,
            timed_tokens: 1_000,
            sample_count: 1,
        }
    }

    /// Acceptance (1d): a local row with an estimate summed with a remote row
    /// of the same client, model and provider that carries a very high cost
    /// has no `costEstimate` (Swift's `implausibleCostRatio` then has no
    /// denominator); a local-only row keeps its estimate. The merged
    /// throughput is the merged rate.
    #[test]
    fn a_row_with_a_remote_part_has_no_cost_estimate() {
        let mut litellm = std::collections::HashMap::new();
        for model in ["m", "other"] {
            litellm.insert(
                model.to_string(),
                tokscale_core::pricing::litellm::ModelPricing {
                    input_cost_per_token: Some(1e-6),
                    ..Default::default()
                },
            );
        }
        let pricing = tokscale_core::pricing::PricingService::new(litellm, Default::default());
        let mut report = tokscale_core::ModelReport {
            entries: vec![
                model_usage("m", 1_000_000, 1.0),
                model_usage("other", 1_000_000, 0.5),
            ],
            total_input: 0,
            total_output: 0,
            total_cache_read: 0,
            total_cache_write: 0,
            total_messages: 0,
            total_cost: 0.0,
            processing_time_ms: 0,
        };
        let mut saturated = false;
        let touched = fold_models(
            &mut report,
            &[models_record("m", 1_000, 5_000_000_000_000)],
            &mut saturated,
        );
        let mut data = models_payload(report, &touched, Some(&pricing)).unwrap();
        let merged = find(&data, "model", "m").clone();
        assert_eq!(merged["costEstimate"], Value::Null);
        assert_eq!(merged["input"], 1_001_000);
        assert_eq!(merged["cost"], 5001.0);
        // (1000 + 3000) ms over (1000 + 1000) timed tokens.
        assert_eq!(merged["msPer1kTokens"], 2000.0);
        assert_eq!(find(&data, "model", "other")["costEstimate"], 1.0);
        assert_eq!(data["totalInput"], 2_001_000);
        assert_eq!(data["entries"][0]["model"], "m", "re-sorted by cost");
        data.as_object_mut().unwrap().remove("pricingUpdatedAt");
        assert!(!saturated);
    }

    #[test]
    fn hour_slots_are_the_local_wall_hour_of_the_query_timezone() {
        // 2025-03-12T20:00Z in UTC+9 is 05:00 the next day.
        let utc = day_ms("2025-03-12").unwrap() + 20 * 3_600_000;
        assert_eq!(
            hour_slot(&hourly_record(utc, 32_400, 1)).unwrap(),
            "2025-03-13 05:00"
        );
        assert_eq!(
            hour_slot(&hourly_record(utc, 0, 1)).unwrap(),
            "2025-03-12 20:00"
        );
        assert_eq!(hour_slot(&hourly_record(i64::MAX, 1, 1)), None);
    }

    #[test]
    fn agents_and_hourly_fold_by_their_keys() {
        let mut agents = tokscale_core::AgentReport {
            entries: vec![tokscale_core::AgentReportEntry {
                agent: "Main".into(),
                clients: vec!["codex".into()],
                input: 10,
                output: 0,
                cache_read: 0,
                cache_write: 0,
                reasoning: 0,
                cost: 0.0,
                messages: 1,
            }],
            total_cost: 0.0,
            total_messages: 1,
            processing_time_ms: 0,
        };
        let record = |agent: &str, input| AgentsRecord {
            agent: agent.into(),
            client: "claude".into(),
            model: None,
            provider: None,
            input_tokens: input,
            output_tokens: 0,
            cache_read_tokens: 0,
            cache_write_tokens: 0,
            reasoning_tokens: 0,
            total_tokens: input,
            message_count: 2,
            turn_count: 0,
            cost_nano_usd: 0,
        };
        let mut saturated = false;
        fold_agents(
            &mut agents,
            &[record("Main", 5), record("reviewer", 100)],
            &mut saturated,
        );
        assert_eq!(
            agents.entries[0].agent, "reviewer",
            "re-sorted by tokens at equal cost"
        );
        let main = agents.entries.iter().find(|e| e.agent == "Main").unwrap();
        assert_eq!((main.input, main.messages), (15, 3));
        assert_eq!(main.clients, ["claude", "codex"]);
        assert_eq!(agents.total_messages, 5);
    }

    /// Review fix 3: one producer's 32,768 distinct records under one day,
    /// one model key space, one hour slot and one agent fold in time that
    /// grows about linearly. Bound: each page under `FOLD_BOUND`; the timings
    /// are printed (`SA3B-FOLD`).
    #[test]
    fn folds_of_32768_distinct_records_are_not_quadratic() {
        const FOLD_BOUND: Duration = Duration::from_secs(2);
        let n = PEER_RECORDS_MAX;
        let client = |i: usize| format!("c{i:06}");
        let graph_records: Vec<_> = (0..n)
            .map(|i| {
                let mut r = graph_record("2025-03-10", 1, 0);
                r.client = client(i);
                r
            })
            .collect();
        let model_records: Vec<_> = (0..n)
            .map(|i| {
                let mut r = models_record("m", 1, 0);
                r.client = client(i);
                r
            })
            .collect();
        let bucket = day_ms("2025-03-10").unwrap();
        let hourly_records: Vec<_> = (0..n)
            .map(|i| {
                let mut r = hourly_record(bucket, 0, 1);
                r.client = client(i);
                r.model = Some(format!("m{i:06}"));
                r
            })
            .collect();
        let agent_records: Vec<_> = (0..n)
            .map(|i| AgentsRecord {
                agent: "Main".into(),
                client: client(i),
                model: None,
                provider: None,
                input_tokens: 1,
                output_tokens: 0,
                cache_read_tokens: 0,
                cache_write_tokens: 0,
                reasoning_tokens: 0,
                total_tokens: 1,
                message_count: 1,
                turn_count: 0,
                cost_nano_usd: 0,
            })
            .collect();
        let mut saturated = false;
        let started = std::time::Instant::now();
        let mut graph = empty_graph();
        fold_graph(&mut graph, &graph_records, &mut saturated);
        let graph_time = started.elapsed();
        let started = std::time::Instant::now();
        let mut models = tokscale_core::ModelReport {
            entries: Vec::new(),
            total_input: 0,
            total_output: 0,
            total_cache_read: 0,
            total_cache_write: 0,
            total_messages: 0,
            total_cost: 0.0,
            processing_time_ms: 0,
        };
        fold_models(&mut models, &model_records, &mut saturated);
        let models_time = started.elapsed();
        let started = std::time::Instant::now();
        let mut hourly = tokscale_core::HourlyReport {
            entries: Vec::new(),
            total_cost: 0.0,
            processing_time_ms: 0,
        };
        fold_hourly(&mut hourly, &hourly_records, &mut saturated);
        let hourly_time = started.elapsed();
        let started = std::time::Instant::now();
        let mut agents = tokscale_core::AgentReport {
            entries: Vec::new(),
            total_cost: 0.0,
            total_messages: 0,
            processing_time_ms: 0,
        };
        fold_agents(&mut agents, &agent_records, &mut saturated);
        let agents_time = started.elapsed();
        println!(
            "SA3B-FOLD graph={graph_time:?} models={models_time:?} hourly={hourly_time:?} agents={agents_time:?}"
        );
        // Control: every record reached its page.
        assert_eq!(graph.contributions[0].clients.len(), n);
        assert_eq!(models.entries.len(), n);
        assert_eq!(
            (
                hourly.entries.len(),
                hourly.entries[0].clients.len(),
                hourly.entries[0].models.len()
            ),
            (1, n, n)
        );
        assert_eq!(
            (agents.entries.len(), agents.entries[0].clients.len()),
            (1, n)
        );
        assert!(hourly.entries[0].clients.windows(2).all(|w| w[0] < w[1]));
        assert!(agents.entries[0].clients.windows(2).all(|w| w[0] < w[1]));
        for (page, time) in [
            ("graph", graph_time),
            ("models", models_time),
            ("hourly", hourly_time),
            ("agents", agents_time),
        ] {
            assert!(time < FOLD_BOUND, "{page} fold took {time:?}");
        }
    }

    // ------------------------------------------------- graph contract ---

    fn contract(pricing_mode: GraphPricingMode, cost_coverage: CostCoverage) -> GraphMetaContract {
        GraphMetaContract {
            pricing_mode,
            cost_coverage,
        }
    }

    /// A merged producer row as `assemble` writes it: `totals` with one
    /// client of `cost`, and `costIncomplete`.
    fn merged_row(incomplete: bool, cost: f64) -> Value {
        json!({"id": "x", "state": "fresh", "costIncomplete": incomplete,
               "totals": [{"client": "claude", "messages": 1, "tokens": 1, "costUsd": cost}]})
    }

    /// The graph's `meta` contract, branch by branch ([`merged_contract`]).
    #[test]
    fn merged_graph_contract_folds_the_contributing_sources() {
        use CostCoverage::{Complete, None as NoCost, Partial};
        use GraphPricingMode::{BestEffort, LocalOnly};
        let local = |coverage| Some(contract(BestEffort, coverage));
        let not_merged = [
            json!({"id": "peers", "state": "unavailable", "totals": null}),
            json!({"id": "y", "state": "refused", "costIncomplete": false, "totals": null}),
            json!({"id": "z", "state": "fresh", "costIncomplete": true, "totals": []}),
        ];
        let coverage = |local, rows: &[Value]| merged_contract(local, rows).cost_coverage;

        // Only producers selected, none merged: `complete`, `bestEffort`.
        assert_eq!(
            merged_contract(None, &not_merged),
            contract(BestEffort, Complete)
        );
        assert_eq!(merged_contract(None, &[]), contract(BestEffort, Complete));
        // The local contract's mode when local is selected (even `localOnly`,
        // which the merged path never computes), `bestEffort` otherwise.
        assert_eq!(
            merged_contract(Some(contract(LocalOnly, Complete)), &[]).pricing_mode,
            LocalOnly
        );
        assert_eq!(
            merged_contract(None, &[merged_row(false, 0.0)]).pricing_mode,
            BestEffort
        );
        // A producer's own coverage: complete / partial / none.
        assert_eq!(coverage(None, &[merged_row(false, 0.0)]), Complete);
        assert_eq!(coverage(None, &[merged_row(true, 0.5)]), Partial);
        assert_eq!(coverage(None, &[merged_row(true, 0.0)]), NoCost);
        // Rows that merged nothing take no part.
        let mut rows = not_merged.to_vec();
        rows.push(merged_row(true, 0.0));
        assert_eq!(coverage(None, &rows), NoCost);
        // The fold.
        assert_eq!(
            coverage(local(Complete), &[merged_row(false, 1.0)]),
            Complete
        );
        assert_eq!(coverage(local(NoCost), &[merged_row(true, 0.0)]), NoCost);
        assert_eq!(coverage(local(Complete), &[merged_row(true, 0.0)]), Partial);
        assert_eq!(coverage(local(NoCost), &[merged_row(false, 0.0)]), Partial);
        assert_eq!(coverage(local(Partial), &[]), Partial);
        assert_eq!(coverage(local(NoCost), &not_merged), NoCost);
        assert_eq!(
            coverage(None, &[merged_row(false, 0.0), merged_row(true, 0.0)]),
            Partial
        );
    }

    /// Through the page: only producers, none merged (no agent runs, so the
    /// store is unreadable) is `complete` and `bestEffort`.
    #[test]
    fn a_graph_of_producers_with_nothing_merged_is_complete() {
        let receiver = RemoteAgent::default();
        let context = unused_context();
        let page = graph(&call(
            &receiver,
            &context,
            false,
            &[[0x11; 32]],
            "UTC",
            Mode::Cached,
        ))
        .unwrap();
        assert_eq!(
            states(&page),
            [
                ("peers".into(), "unavailable".into()),
                (fingerprint(&[0x11; 32]), "unavailable".into())
            ]
        );
        assert_eq!(page["meta"]["pricingMode"], "bestEffort");
        assert_eq!(page["meta"]["costCoverage"], "complete");
    }

    // ------------------------------------------------------ integration ---

    /// Acceptance (1): with only the local source, each new entry's data is
    /// the existing entry's, byte for byte, apart from the four new keys;
    /// the new keys say one local device and nothing else. Each pair is
    /// called back to back, so the graph's is served by the cache `tb_graph`
    /// filled.
    #[test]
    fn local_only_is_the_existing_output_byte_for_byte() {
        let Some(root) = child_root() else {
            run_in_child_at(
                &path_of("local_only_is_the_existing_output_byte_for_byte"),
                "sa4c3-local",
            );
            return;
        };
        write_claude(
            &root,
            "r.jsonl",
            &[
                claude(
                    "l1",
                    "2021-03-10T12:30:00.000Z",
                    "claude-3-5-sonnet",
                    100,
                    10,
                    false,
                ),
                claude(
                    "l2",
                    "2021-03-11T12:00:00.000Z",
                    "claude-3-5-sonnet",
                    50,
                    5,
                    false,
                ),
            ],
        );
        let (year, devices, tz, mode) = (
            c(YEAR),
            c(r#"{"local":true,"peers":[]}"#),
            c("UTC"),
            c("refresh"),
        );
        let clients = c("claude");
        use crate::remote_pages_ffi as new;
        let pairs = unsafe {
            [
                (
                    take(crate::tb_graph(year.as_ptr())),
                    take(new::tb_graph_devices(
                        year.as_ptr(),
                        devices.as_ptr(),
                        tz.as_ptr(),
                        mode.as_ptr(),
                    )),
                ),
                (
                    take(crate::tb_model_report(year.as_ptr())),
                    take(new::tb_model_report_devices(
                        year.as_ptr(),
                        devices.as_ptr(),
                        tz.as_ptr(),
                        mode.as_ptr(),
                    )),
                ),
                (
                    take(crate::tb_hourly_report(year.as_ptr(), clients.as_ptr())),
                    take(new::tb_hourly_report_devices(
                        year.as_ptr(),
                        clients.as_ptr(),
                        devices.as_ptr(),
                        tz.as_ptr(),
                        mode.as_ptr(),
                    )),
                ),
                (
                    take(crate::tb_agents_report(year.as_ptr(), clients.as_ptr())),
                    take(new::tb_agents_report_devices(
                        year.as_ptr(),
                        clients.as_ptr(),
                        devices.as_ptr(),
                        tz.as_ptr(),
                        mode.as_ptr(),
                    )),
                ),
            ]
        };
        for (old, mut new) in pairs {
            assert_eq!(old["ok"], true, "{old}");
            let data = new["data"].as_object_mut().unwrap();
            assert_eq!(
                data.remove("devices"),
                Some(
                    json!([{"id": "local", "label": null, "state": "local", "lastSuccessAt": null}])
                )
            );
            assert_eq!(data.remove("duplicateWarning"), Some(json!(false)));
            assert_eq!(data.remove("saturated"), Some(json!(false)));
            assert_eq!(data.remove("remoteRangeLimited"), Some(json!(false)));
            assert_eq!(old["data"].to_string(), new["data"].to_string());
            // Control: the fixture reached the report.
            assert!(old["data"].to_string().contains("claude"), "{old}");
        }
        let bad = c(r#"{"local":false,"peers":[]}"#);
        let reply = take(unsafe {
            new::tb_graph_devices(year.as_ptr(), bad.as_ptr(), tz.as_ptr(), mode.as_ptr())
        });
        assert_eq!(reply, json!({"ok": false, "err": "invalid"}));
    }

    /// Acceptance (2), (3), (5), (7), (1b), (1c), (4), (9c), (9e): two producers
    /// paired with a receiver, through the page functions. Plus the graph
    /// contract with local and a producer: `bestEffort`.
    #[test]
    fn producers_fold_into_the_four_pages() {
        let Some(root) = child_root() else {
            run_in_child_at(&path_of("producers_fold_into_the_four_pages"), "sa4c3-fold");
            return;
        };
        let clock = TestClock::new();
        let context = crate::LocalSourceContext::process().unwrap();
        let sonnet = "claude-3-5-sonnet";
        write_claude(
            &root,
            "r.jsonl",
            &[
                claude("l1", "2021-03-10T05:30:00.000Z", sonnet, 100, 10, false),
                claude("l2", "2021-03-11T06:00:00.000Z", sonnet, 50, 5, false),
            ],
        );
        let (a_home, b_home, r_home) = (TempHome::new(), TempHome::new(), TempHome::new());
        write_session(
            &a_home.0,
            "a.jsonl",
            &[
                claude("a1", "2021-03-10T05:45:00.000Z", sonnet, 7, 3, false),
                claude(
                    "a2",
                    "2021-03-12T20:30:00.000Z",
                    "claude-3-opus",
                    11,
                    1,
                    false,
                ),
            ],
        );
        write_session(
            &a_home.0,
            "agent-x.jsonl",
            &[claude(
                "a3",
                "2021-03-12T09:00:00.000Z",
                sonnet,
                13,
                2,
                true,
            )],
        );
        write_session(
            &b_home.0,
            "b.jsonl",
            &[claude(
                "b1",
                "2021-03-10T05:50:00.000Z",
                sonnet,
                1000,
                100,
                false,
            )],
        );
        let (a, b) = (producer(&a_home.0, &clock), producer(&b_home.0, &clock));
        let receiver = agent_at(&r_home.0, &clock);
        let (ka, kb) = (pair(&a, &receiver), pair(&b, &receiver));
        let unselected = trap();
        let kc = fake_peer(&receiver, 0xcc, unselected.local_addr().unwrap().port(), 1);

        // Local only, for the sums below.
        let local_graph =
            graph(&call(&receiver, &context, true, &[], "UTC", Mode::Cached)).unwrap();
        let local_models =
            models(&call(&receiver, &context, true, &[], "UTC", Mode::Cached)).unwrap();
        let local_hourly =
            hourly(&call(&receiver, &context, true, &[], "UTC", Mode::Cached)).unwrap();
        let local_agents =
            agents(&call(&receiver, &context, true, &[], "UTC", Mode::Cached)).unwrap();
        assert_eq!(
            day(&local_graph, "2021-03-10")["tokenBreakdown"]["input"],
            100,
            "control: local fixture read"
        );

        // (1b) Only A selected: A's numbers, not B's; the unselected fake
        // peer gets no connection.
        let page =
            |f: fn(&Call<'_>) -> Result<Value, String>, local, peers: &[[u8; 32]], tz, mode| {
                if mode == Mode::Refresh {
                    clock.advance(61);
                }
                f(&call(&receiver, &context, local, peers, tz, mode)).unwrap()
            };
        let g = page(graph, true, &[ka], "UTC", Mode::Refresh);
        assert_eq!(
            states(&g),
            [
                ("local".into(), "local".into()),
                (fingerprint(&ka), "fresh".into())
            ]
        );
        assert_eq!(g["duplicateWarning"], true, "(5) two sources");
        assert_eq!(g["remoteRangeLimited"], false);
        assert!(g["devices"][1]["lastSuccessAt"].is_i64());
        // Windows graph contract: local + a producer is `bestEffort`, never
        // the local-only mode.
        assert_eq!(g["meta"]["pricingMode"], "bestEffort");
        assert_eq!(
            g["meta"]["pricingMode"], local_graph["meta"]["pricingMode"],
            "the local contract's mode"
        );
        // DR-2a R1 through the real core: this PC's totals (taken before the
        // fold) and A's add up to the merged page.
        let rows_sum = |key: &str| {
            g["devices"]
                .as_array()
                .unwrap()
                .iter()
                .flat_map(|d| d["totals"].as_array().expect("every merged row has totals"))
                .map(|t| t[key].as_f64().unwrap())
                .sum::<f64>()
        };
        let days_sum = |key: &str| {
            g["contributions"]
                .as_array()
                .unwrap()
                .iter()
                .map(|d| d["totals"][key].as_f64().unwrap())
                .sum::<f64>()
        };
        assert_eq!(rows_sum("tokens"), days_sum("tokens"));
        assert_eq!(rows_sum("messages"), days_sum("messages"));
        assert!((rows_sum("costUsd") - days_sum("cost")).abs() < 1e-9);
        assert!(rows_sum("tokens") > 0.0, "control: the page has usage");
        assert_eq!(
            g["devices"][1]["label"],
            format!("127.0.0.1:{}", a.status().unwrap()["bindPort"])
        );
        // (2) overlapping day and row: local + A; A-only day; local-only day.
        let d10 = day(&g, "2021-03-10");
        assert_eq!(d10["tokenBreakdown"]["input"], 107);
        assert_eq!(d10["totals"]["messages"], 2);
        let row = d10["clients"]
            .as_array()
            .unwrap()
            .iter()
            .find(|r| r["client"] == "claude")
            .unwrap();
        assert_eq!(row["tokens"]["input"], 107, "{d10}");
        assert_eq!(day(&g, "2021-03-12")["tokenBreakdown"]["input"], 11 + 13);
        assert_eq!(day(&g, "2021-03-11")["tokenBreakdown"]["input"], 50);
        assert_eq!(
            g["summary"]["totalTokens"],
            local_graph["summary"]["totalTokens"].as_i64().unwrap() + 7 + 3 + 11 + 1 + 13 + 2
        );
        assert!(g["summary"]["models"]
            .as_array()
            .unwrap()
            .iter()
            .any(|m| m == "claude-3-opus"));
        assert!(
            !connected(&unselected),
            "(9e) the unselected peer got a connection"
        );

        let m = page(models, true, &[ka], "UTC", Mode::Refresh);
        let local_sonnet = find(&local_models, "model", sonnet);
        let merged = find(&m, "model", sonnet);
        assert_eq!(
            merged["input"],
            local_sonnet["input"].as_i64().unwrap() + 7 + 13,
            "{m}"
        );
        assert_eq!(find(&m, "model", "claude-3-opus")["input"], 11);
        assert_eq!(merged["costEstimate"], Value::Null);

        // Windows takes the local rows' hour slots from the system zone (no
        // `TZ`), the producers' from the query's (UTC here): the expected
        // input of each slot is the sum of what lands in it.
        let local = [
            (local_slot("2021-03-10T05:30:00Z"), 100),
            (local_slot("2021-03-11T06:00:00Z"), 50),
        ];
        let a_parts = [
            ("2021-03-10 05:00".to_owned(), 7),
            ("2021-03-12 20:00".to_owned(), 11),
            ("2021-03-12 09:00".to_owned(), 13),
        ];
        let b_parts = [("2021-03-10 05:00".to_owned(), 1000)];
        let h = page(hourly, true, &[ka], "UTC", Mode::Refresh);
        assert_slots(&local_hourly, &[&local]);
        assert_slots(&h, &[&local, &a_parts]);

        let ag = page(agents, true, &[ka], "UTC", Mode::Refresh);
        let main = find(&ag, "agent", "Main");
        assert_eq!(
            main["input"],
            find(&local_agents, "agent", "Main")["input"]
                .as_i64()
                .unwrap()
                + 7
                + 11
        );
        let sub: Vec<_> = ag["entries"]
            .as_array()
            .unwrap()
            .iter()
            .filter(|e| e["agent"] != "Main")
            .collect();
        assert_eq!(sub.len(), 1, "{ag}");
        assert_eq!(sub[0]["input"], 13, "A's sub-agent, absent locally");
        assert!(!connected(&unselected));

        // (3) Only B, no local: B alone.
        let only_b = page(graph, false, &[kb], "UTC", Mode::Refresh);
        assert_eq!(states(&only_b), [(fingerprint(&kb), "fresh".into())]);
        assert_eq!(only_b["duplicateWarning"], false, "(5) one source");
        assert_eq!(only_b["contributions"].as_array().unwrap().len(), 1);
        assert_eq!(day(&only_b, "2021-03-10")["tokenBreakdown"]["input"], 1000);
        // Local + A + B.
        let all = page(hourly, true, &[ka, kb], "UTC", Mode::Refresh);
        assert_slots(&all, &[&local, &a_parts, &b_parts]);

        // (7) Asia/Tokyo: A's 20:30Z message lands at 05:00 the next day.
        let tokyo = page(hourly, false, &[ka], "Asia/Tokyo", Mode::Refresh);
        assert_eq!(
            find(&tokyo, "hour", "2021-03-13 05:00")["input"],
            11,
            "{tokyo}"
        );
        assert_eq!(find(&tokyo, "hour", "2021-03-10 14:00")["input"], 7);
        let tokyo_graph = page(graph, false, &[ka], "Asia/Tokyo", Mode::Refresh);
        assert_eq!(
            day(&tokyo_graph, "2021-03-13")["tokenBreakdown"]["input"],
            11
        );

        // (1c) incompatible: nothing sent, local intact.
        let incompatible = page(graph, true, &[ka, kc], "Not/AZone", Mode::Refresh);
        assert_eq!(
            states(&incompatible)[1..],
            [
                (fingerprint(&ka), "incompatible".into()),
                (fingerprint(&kc), "incompatible".into())
            ]
        );
        assert!(!connected(&unselected), "an incompatible query was sent");
        assert_eq!(
            day(&incompatible, "2021-03-10")["tokenBreakdown"]["input"],
            100
        );

        // (1c) unavailable: cached mode, a query never fetched (all time).
        let mut never = call(&receiver, &context, false, &[ka], "UTC", Mode::Cached);
        never.year = "";
        let never = graph(&never).unwrap();
        assert_eq!(states(&never), [(fingerprint(&ka), "unavailable".into())]);
        assert_eq!(never["remoteRangeLimited"], true, "(6)");

        // (4) A refused (revoked), B fresh, local intact.
        let grant = a.grants().unwrap()[0]["grantId"]
            .as_str()
            .unwrap()
            .to_owned();
        a.revoke(&grant).unwrap();
        let refused = page(graph, true, &[ka, kb], "UTC", Mode::Refresh);
        assert_eq!(
            states(&refused)[1..],
            [
                (fingerprint(&ka), "refused".into()),
                (fingerprint(&kb), "fresh".into())
            ]
        );
        assert_eq!(day(&refused, "2021-03-10")["tokenBreakdown"]["input"], 1100);

        // (2) cached after B stops: B's last good body, `lastGood`, no
        // connection to the selected fake peer either.
        b.stop().unwrap();
        let cached = page(hourly, true, &[kb, kc], "UTC", Mode::Cached);
        assert_eq!(
            states(&cached)[1..],
            [
                (fingerprint(&kb), "lastGood".into()),
                (fingerprint(&kc), "unavailable".into())
            ]
        );
        assert_slots(&cached, &[&local, &b_parts]);
        // A body never fetched (B's models) is `unavailable`.
        let never_models = page(models, true, &[kb], "UTC", Mode::Cached);
        assert_eq!(
            states(&never_models)[1..],
            [(fingerprint(&kb), "unavailable".into())]
        );
        assert_eq!(
            find(&never_models, "model", sonnet)["input"],
            local_sonnet["input"]
        );
        assert!(!connected(&unselected), "cached mode connected");
        // Refresh with B stopped: no answer, so the last good body.
        let fallback = page(graph, false, &[kb], "UTC", Mode::Refresh);
        assert_eq!(states(&fallback), [(fingerprint(&kb), "lastGood".into())]);
        assert_eq!(
            day(&fallback, "2021-03-10")["tokenBreakdown"]["input"],
            1000
        );

        // (9c) After a restart of the process (a new RemoteAgent on the same
        // root), the cached body has no known age: `stale`, null time.
        drop(receiver);
        let restarted = agent_at(&r_home.0, &clock);
        let stale = graph(&call(
            &restarted,
            &context,
            false,
            &[kb],
            "UTC",
            Mode::Cached,
        ))
        .unwrap();
        // The producer has no price table here, so its cached body carries
        // `costIncomplete`, and the cache itself says it answered before.
        let address = format!(
            "127.0.0.1:{}",
            stale["devices"][0]["label"]
                .as_str()
                .unwrap()
                .rsplit(':')
                .next()
                .unwrap()
        );
        assert_eq!(
            stale["devices"],
            json!([{"id": fingerprint(&kb), "label": address, "address": address,
                    "state": "stale", "lastSuccessAt": null, "reason": null,
                    "costIncomplete": true, "everAnswered": true,
                    "totals": [{"client": "claude", "costUsd": 0.0, "messages": 1, "tokens": 1100}]}])
        );
        assert_eq!(day(&stale, "2021-03-10")["tokenBreakdown"]["input"], 1000);
        // Only B, which priced nothing and said so: `none`.
        assert_eq!(stale["meta"]["costCoverage"], "none");
        drop(a);
    }

    /// The `"YYYY-MM-DD HH:00"` slot of an instant in this process's zone,
    /// which is the one the engine buckets local rows in.
    fn local_slot(instant: &str) -> String {
        DateTime::parse_from_rfc3339(instant)
            .unwrap()
            .with_timezone(&chrono::Local)
            .format("%Y-%m-%d %H:00")
            .to_string()
    }

    /// The hourly page holds exactly the summed inputs of `parts`, by slot.
    fn assert_slots(page: &Value, parts: &[&[(String, i64)]]) {
        let mut expected = BTreeMap::<String, i64>::new();
        for (slot, input) in parts.iter().flat_map(|p| p.iter()) {
            *expected.entry(slot.clone()).or_default() += input;
        }
        let got: BTreeMap<String, i64> = page["entries"]
            .as_array()
            .unwrap()
            .iter()
            .map(|e| {
                (
                    e["hour"].as_str().unwrap().to_owned(),
                    e["input"].as_i64().unwrap(),
                )
            })
            .collect();
        assert_eq!(got, expected);
    }

    /// DR-2a R3 through the real pull: a producer nobody listens for is
    /// `unreachable` with no data; a pull past the producer's burst of two
    /// inside its 60 s grant refill is `busy` and keeps the first pull's body
    /// (`lastGood`, with its totals); a successful pull carries no reason,
    /// and a cached call none either.
    #[test]
    fn dr2a_reasons_survive_the_classification() {
        let clock = TestClock::new();
        let context = unused_context();
        let (a_home, r_home) = (TempHome::new(), TempHome::new());
        write_session(
            &a_home.0,
            "a.jsonl",
            &[claude(
                "a1",
                "2021-03-10T05:45:00.000Z",
                "claude-3-5-sonnet",
                7,
                3,
                false,
            )],
        );
        let a = producer(&a_home.0, &clock);
        let receiver = agent_at(&r_home.0, &clock);
        let ka = pair(&a, &receiver);
        let dead = fake_peer(&receiver, 0xdd, free_port(), 1);
        let reason_of_page = |peers: &[[u8; 32]], mode| {
            let page = graph(&call(&receiver, &context, false, peers, "UTC", mode)).unwrap();
            let row = page["devices"][0].clone();
            (
                row["state"].clone(),
                row["reason"].clone(),
                row["totals"].clone(),
            )
        };

        let (state, reason, totals) = reason_of_page(&[dead], Mode::Refresh);
        assert_eq!(
            (state, reason, totals),
            ("unavailable".into(), "unreachable".into(), Value::Null)
        );
        let (state, reason, totals) = reason_of_page(&[ka], Mode::Refresh);
        assert_eq!((state, reason), ("fresh".into(), Value::Null));
        assert_eq!(totals[0]["tokens"], 10);
        // The grant allows a burst of two, then one a minute.
        reason_of_page(&[ka], Mode::Refresh);
        let (state, reason, totals) = reason_of_page(&[ka], Mode::Refresh);
        assert_eq!((state, reason), ("lastGood".into(), "busy".into()));
        assert_eq!(totals[0]["tokens"], 10, "the kept body is still merged");
        let (_, reason, _) = reason_of_page(&[ka], Mode::Cached);
        assert_eq!(reason, Value::Null);
    }

    /// DR-2b2 through the real pull. Before the first answer a row shows the
    /// address and `everAnswered` is false; after it the row shows the name
    /// the producer sent (stored on the record, so a cached call shows it
    /// too), keeps the address in its own field, and `everAnswered` is true.
    /// `costIncomplete` is true for a producer with no price table and false
    /// once it has one; a producer nobody listens for has neither a name nor
    /// the flag.
    #[test]
    fn dr2b2_rows_carry_the_name_the_address_and_the_flags() {
        let clock = TestClock::new();
        let context = unused_context();
        let (a_home, r_home) = (TempHome::new(), TempHome::new());
        write_session(
            &a_home.0,
            "a.jsonl",
            &[claude(
                "a1",
                "2021-03-10T05:45:00.000Z",
                "claude-3-5-sonnet",
                1_000_000,
                0,
                false,
            )],
        );
        let a = agent_at(&a_home.0, &clock);
        let port = free_port();
        a.listen_configure("127.0.0.1", port, Some("Studio PC".into()))
            .unwrap();
        a.scope_approve(&digest(&a)).unwrap();
        a.sharing_set(true).unwrap();
        let receiver = agent_at(&r_home.0, &clock);
        let ka = pair(&a, &receiver);
        let dead = fake_peer(&receiver, 0xdd, free_port(), 1);
        let address = format!("127.0.0.1:{port}");
        let row = |peer: [u8; 32], mode| {
            let page = graph(&call(&receiver, &context, false, &[peer], "UTC", mode)).unwrap();
            page["devices"][0].clone()
        };

        let before = row(ka, Mode::Cached);
        assert_eq!(before["label"], address.as_str(), "{before}");
        assert_eq!(before["address"], address.as_str());
        assert_eq!(before["everAnswered"], false);
        assert_eq!(before["costIncomplete"], false);

        // No price table on the producer: its numbers say so.
        clock.advance(61);
        let first = row(ka, Mode::Refresh);
        assert_eq!(first["state"], "fresh", "{first}");
        assert_eq!(first["label"], "Studio PC", "the name of this answer");
        assert_eq!(first["address"], address.as_str());
        assert_eq!(first["everAnswered"], true);
        assert_eq!(first["costIncomplete"], true, "{first}");
        // The name and the flag live on: a cached call has no connection.
        let cached = row(ka, Mode::Cached);
        assert_eq!(cached["label"], "Studio PC");
        assert_eq!(cached["everAnswered"], true);
        assert_eq!(cached["costIncomplete"], true, "the cached body's flag");

        // A price table on the producer clears the flag.
        let source = a_home.0.join("pricing-src");
        fs::create_dir_all(&source).unwrap();
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_secs();
        let dataset = |data: Value| json!({"timestamp": now, "data": data}).to_string();
        fs::write(
            source.join("pricing-litellm.json"),
            dataset(json!({"claude-3-5-sonnet": {"input_cost_per_token": 0.000_001}})),
        )
        .unwrap();
        fs::write(source.join("pricing-openrouter.json"), dataset(json!({}))).unwrap();
        for name in crate::remote_agent::PRICING_FILES {
            fs::File::options()
                .write(true)
                .open(source.join(name))
                .unwrap()
                .set_modified(SystemTime::now() + Duration::from_secs(10))
                .unwrap();
        }
        std::thread::sleep(PERIOD * 5);
        clock.advance(61);
        let priced = row(ka, Mode::Refresh);
        assert_eq!(priced["state"], "fresh", "{priced}");
        assert_eq!(priced["costIncomplete"], false, "{priced}");

        // A producer that never answered: address as label, no flag.
        let unreachable = row(dead, Mode::Refresh);
        assert_eq!(unreachable["state"], "unavailable", "{unreachable}");
        assert_eq!(unreachable["label"], unreachable["address"]);
        assert_eq!(unreachable["everAnswered"], false);
        assert_eq!(unreachable["costIncomplete"], false);
    }

    /// Review fix 2: a producer that completes the handshake and then never
    /// answers holds a `refresh` in its fetch. Meanwhile a `cached` call for
    /// the same page and a management call (`peer_remove`) both return
    /// within `BOUND`; the refresh is still waiting when they do.
    #[test]
    fn a_silent_producer_blocks_neither_cached_calls_nor_management() {
        const BOUND: Duration = Duration::from_secs(2);
        let clock = TestClock::new();
        let r_home = TempHome::new();
        let receiver = agent_at(&r_home.0, &clock);
        let key = syrtis_agent::noise::StaticKey::generate().unwrap();
        let silent = *key.public();
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let port = listener.local_addr().unwrap().port();
        let (handshook, handshake) = std::sync::mpsc::channel();
        let (release, released) = std::sync::mpsc::channel::<()>();
        let server = std::thread::spawn(move || {
            let (stream, _) = listener.accept().unwrap();
            let session = syrtis_agent::noise::accept(stream, &key);
            handshook.send(session.is_ok()).unwrap();
            let _ = released.recv_timeout(Duration::from_secs(60));
            drop(session);
        });
        fake_peer_as(&receiver, silent, 0x51, port, 1);
        let other = fake_peer(&receiver, 0x52, free_port(), 1);

        let pulling = Arc::clone(&receiver);
        let refresh = std::thread::spawn(move || {
            let context = unused_context();
            graph(&call(
                &pulling,
                &context,
                false,
                &[silent],
                "UTC",
                Mode::Refresh,
            ))
            .unwrap()
        });
        assert!(
            handshake.recv_timeout(Duration::from_secs(10)).unwrap(),
            "the refresh did not complete the handshake"
        );
        let context = unused_context();
        let started = std::time::Instant::now();
        let cached = graph(&call(
            &receiver,
            &context,
            false,
            &[silent],
            "UTC",
            Mode::Cached,
        ))
        .unwrap();
        let cached_time = started.elapsed();
        let started = std::time::Instant::now();
        receiver.peer_remove(&hex_encode(&other)).unwrap();
        let remove_time = started.elapsed();
        let still_waiting = !refresh.is_finished();
        release.send(()).unwrap();
        server.join().unwrap();
        let refreshed = refresh.join().unwrap();
        println!("SA4C3-LOCK cached={cached_time:?} remove={remove_time:?}");
        assert!(still_waiting, "control: the refresh had already ended");
        assert_eq!(
            states(&cached),
            [(fingerprint(&silent), "unavailable".into())]
        );
        assert!(cached_time < BOUND, "cached call took {cached_time:?}");
        assert!(remove_time < BOUND, "peer_remove took {remove_time:?}");
        assert!(receiver
            .peer_records()
            .unwrap()
            .iter()
            .all(|r| r.responder_static != other));
        assert_ne!(states(&refreshed)[0].1, "fresh");
    }

    /// `op` (a stop, or a start whose inputs changed) called while a pull is
    /// inside its fetch. Both take the agent's write lock, so `op` must still
    /// be waiting while the producer stays silent, and must finish (no
    /// deadlock) once the producer's session is dropped, which ends the pull.
    /// The test releases the pull itself by closing the session; it never
    /// waits out `FETCH_DEADLINE`.
    fn op_waits_for_the_pull(
        home: &Path,
        op: impl FnOnce(Arc<RemoteAgent>, Arc<TestClock>) -> Value + Send + 'static,
        check: impl FnOnce(&RemoteAgent, Value),
    ) {
        let clock = TestClock::new();
        let receiver = agent_at(home, &clock);
        let key = syrtis_agent::noise::StaticKey::generate().unwrap();
        let silent = *key.public();
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let port = listener.local_addr().unwrap().port();
        let (handshook, handshake) = std::sync::mpsc::channel();
        let (release, released) = std::sync::mpsc::channel::<()>();
        let server = std::thread::spawn(move || {
            let (stream, _) = listener.accept().unwrap();
            let session = syrtis_agent::noise::accept(stream, &key);
            handshook.send(session.is_ok()).unwrap();
            let _ = released.recv_timeout(Duration::from_secs(60));
            drop(session);
            drop(listener);
        });
        fake_peer_as(&receiver, silent, 0x51, port, 1);
        let pulling = Arc::clone(&receiver);
        let refresh = std::thread::spawn(move || {
            let context = unused_context();
            graph(&call(
                &pulling,
                &context,
                false,
                &[silent],
                "UTC",
                Mode::Refresh,
            ))
            .unwrap()
        });
        assert!(
            handshake.recv_timeout(Duration::from_secs(10)).unwrap(),
            "the refresh did not complete the handshake"
        );
        let operating = Arc::clone(&receiver);
        let operation = std::thread::spawn(move || op(operating, clock));
        std::thread::sleep(Duration::from_millis(400));
        let early = operation.is_finished();
        let pull_open = !refresh.is_finished();
        release.send(()).unwrap();
        let deadline = std::time::Instant::now() + Duration::from_secs(20);
        while !(operation.is_finished() && refresh.is_finished()) {
            assert!(
                std::time::Instant::now() < deadline,
                "deadlock: the operation or the pull never ended"
            );
            std::thread::sleep(Duration::from_millis(20));
        }
        server.join().unwrap();
        let refreshed = refresh.join().unwrap();
        let result = operation.join().unwrap();
        assert!(pull_open, "control: the pull had already ended");
        assert!(
            !early,
            "the operation finished while the pull was in flight"
        );
        assert_ne!(states(&refreshed)[0].1, "fresh");
        check(&receiver, result);
    }

    #[test]
    fn stop_waits_for_a_pull_in_flight() {
        let home = TempHome::new();
        op_waits_for_the_pull(
            &home.0,
            |agent, _| agent.stop().unwrap(),
            |agent, result| {
                assert_eq!(result["running"], false);
                assert_eq!(agent.status().unwrap()["running"], false);
            },
        );
    }

    #[test]
    fn a_restart_waits_for_a_pull_in_flight() {
        let home = TempHome::new();
        let extra = home.0.join("extra");
        fs::create_dir_all(&extra).unwrap();
        let changed = ScannerSettings {
            extra_scan_paths: std::collections::BTreeMap::from([(
                "claude".to_string(),
                vec![extra],
            )]),
            ..ScannerSettings::default()
        };
        let restart_home = home.0.clone();
        op_waits_for_the_pull(
            &home.0,
            move |agent, clock| {
                let before = agent.scope_disclosure().unwrap()["ownerInputsSha256"].clone();
                agent.start(make(&restart_home, &clock, changed)).unwrap();
                json!({"before": before})
            },
            |agent, result| {
                let after = agent.scope_disclosure().unwrap()["ownerInputsSha256"].clone();
                assert_ne!(after, result["before"], "the inputs did not change");
                assert_eq!(agent.status().unwrap()["running"], true);
            },
        );
    }

    /// Deferred items (3) and (9d): a record superseded by a newer one at the
    /// same host and port is neither read (cached) nor asked (refresh).
    #[test]
    fn a_superseded_record_is_never_read_or_asked() {
        let clock = TestClock::new();
        let context = unused_context();
        let (a_home, r_home) = (TempHome::new(), TempHome::new());
        write_session(
            &a_home.0,
            "a.jsonl",
            &[claude("a1", "2021-03-10T05:45:00.000Z", "m", 7, 3, false)],
        );
        let a = producer(&a_home.0, &clock);
        let port = a.status().unwrap()["bindPort"].as_u64().unwrap() as u16;
        let receiver = agent_at(&r_home.0, &clock);
        let ka = pair(&a, &receiver);
        clock.advance(61);
        let first = graph(&call(
            &receiver,
            &context,
            false,
            &[ka],
            "UTC",
            Mode::Refresh,
        ))
        .unwrap();
        assert_eq!(
            states(&first),
            [(fingerprint(&ka), "fresh".into())],
            "control"
        );

        // The producer resets and is paired again at the same address: here a
        // newer record at that host and port.
        a.stop().unwrap();
        let trap = TcpListener::bind(("127.0.0.1", port)).unwrap();
        trap.set_nonblocking(true).unwrap();
        fake_peer(&receiver, 0xdd, port, i64::MAX / 4);
        for mode in [Mode::Cached, Mode::Refresh] {
            clock.advance(61);
            let page = graph(&call(&receiver, &context, false, &[ka], "UTC", mode)).unwrap();
            assert_eq!(
                states(&page),
                [(fingerprint(&ka), "stale".into())],
                "{mode:?}"
            );
            assert_eq!(
                page["contributions"],
                json!([]),
                "{mode:?} merged the old record"
            );
        }
        assert!(!connected(&trap), "the superseded record was asked");
    }

    /// Deferred item (2): a peer store that cannot be read is a row, not "no
    /// devices"; the local part is unaffected.
    #[test]
    fn an_unreadable_peer_store_is_said() {
        let Some(root) = child_root() else {
            run_in_child_at(&path_of("an_unreadable_peer_store_is_said"), "sa4c3-store");
            return;
        };
        let clock = TestClock::new();
        let context = crate::LocalSourceContext::process().unwrap();
        write_claude(
            &root,
            "r.jsonl",
            &[claude(
                "l1",
                "2021-03-10T05:30:00.000Z",
                "m",
                100,
                10,
                false,
            )],
        );
        let r_home = TempHome::new();
        drop(agent_at(&r_home.0, &clock));
        fs::write(r_home.0.join("a").join("peers.json"), b"{not json").unwrap();
        let receiver = Arc::new(RemoteAgent::default());
        assert!(receiver
            .start(make(&r_home.0, &clock, ScannerSettings::default()))
            .is_err());
        let peer = [0x11; 32];
        let page = graph(&call(
            &receiver,
            &context,
            true,
            &[peer],
            "UTC",
            Mode::Cached,
        ))
        .unwrap();
        assert_eq!(
            states(&page),
            [
                ("local".into(), "local".into()),
                ("peers".into(), "unavailable".into()),
                (fingerprint(&peer), "unavailable".into()),
            ]
        );
        assert_eq!(day(&page, "2021-03-10")["tokenBreakdown"]["input"], 100);
    }

    /// Counts `receiver`'s pulls in `pull_peer`; `then` runs after each one
    /// with its 1-based number.
    fn count_pulls(
        receiver: &RemoteAgent,
        then: impl Fn(usize) + Send + Sync + 'static,
    ) -> Arc<AtomicUsize> {
        let pulls = Arc::new(AtomicUsize::new(0));
        let counter = Arc::clone(&pulls);
        *receiver.after_pull.lock().unwrap() = Some(Arc::new(move || {
            then(counter.fetch_add(1, Ordering::SeqCst) + 1);
        }));
        pulls
    }

    /// The receiver's record of producer `ka`.
    fn record_of(agent: &RemoteAgent, ka: &[u8; 32]) -> PeerRecord {
        agent
            .peer_records()
            .unwrap()
            .into_iter()
            .find(|r| r.responder_static == *ka)
            .unwrap()
    }

    /// DR-3b (a), (d): producer A with receivers R and O. O pairs again:
    /// A's approval revokes O's older grant in the same commit (DR-3a), so
    /// A lists exactly one active grant for O, and the revoke renumbers R's
    /// `authEpoch`. R's next refresh is refused with code 2, asks Grant
    /// status (`active`), stores the new epoch and pulls again once: the
    /// page is `fresh` (with its time) on that same call, after two pulls.
    #[test]
    fn a_re_pairing_elsewhere_is_fresh_on_the_next_pull() {
        let clock = TestClock::new();
        let (a_home, r_home, o_home) = (TempHome::new(), TempHome::new(), TempHome::new());
        let context = unused_context();
        let a = producer(&a_home.0, &clock);
        let receiver = agent_at(&r_home.0, &clock);
        let other = agent_at(&o_home.0, &clock);
        let ka = pair(&a, &receiver);
        pair(&a, &other);
        let before = record_of(&receiver, &ka).auth_epoch;
        let o_old = hex_encode(&record_of(&other, &ka).grant_id);
        let grants = |agent: &RemoteAgent| agent.grants().unwrap().as_array().unwrap().clone();
        let o_print = grants(&a)
            .iter()
            .find(|g| g["grantId"] == o_old.as_str())
            .unwrap()["peerFingerprint"]
            .clone();

        pair(&a, &other);
        let o_new = hex_encode(&record_of(&other, &ka).grant_id);
        assert_ne!(o_new, o_old);
        let of_o: Vec<(String, String)> = grants(&a)
            .iter()
            .filter(|g| g["peerFingerprint"] == o_print)
            .map(|g| {
                (
                    g["grantId"].as_str().unwrap().to_owned(),
                    g["state"].as_str().unwrap().to_owned(),
                )
            })
            .collect();
        assert_eq!(
            of_o.iter().filter(|(_, state)| state == "active").count(),
            1,
            "{of_o:?}"
        );
        assert!(of_o.contains(&(o_new, "active".into())), "{of_o:?}");
        assert!(of_o.contains(&(o_old, "revoked".into())), "{of_o:?}");

        let pulls = count_pulls(&receiver, |_| {});
        clock.advance(61);
        let page = agents(&call(
            &receiver,
            &context,
            false,
            &[ka],
            "UTC",
            Mode::Refresh,
        ))
        .unwrap();
        assert_eq!(states(&page), [(fingerprint(&ka), "fresh".into())]);
        assert!(page["devices"][0]["lastSuccessAt"].is_i64());
        assert_eq!(pulls.load(Ordering::SeqCst), 2, "one refusal, one retry");
        assert!(
            record_of(&receiver, &ka).auth_epoch > before,
            "Grant status did not update the record"
        );
    }

    /// DR-3b (c): a code-2 refusal whose Grant status answers `revoked` (A
    /// revokes R's grant between R's pull and its Grant status, through the
    /// test seam) stays `refused`, marks the record revoked and is not
    /// retried: one pull.
    #[test]
    fn grant_status_revoked_is_refused_without_a_retry() {
        let clock = TestClock::new();
        let (a_home, r_home, o_home) = (TempHome::new(), TempHome::new(), TempHome::new());
        let context = unused_context();
        let a = producer(&a_home.0, &clock);
        let receiver = agent_at(&r_home.0, &clock);
        let other = agent_at(&o_home.0, &clock);
        let ka = pair(&a, &receiver);
        pair(&a, &other);
        // Renumbers R's `authEpoch`: R's next pull is refused with code 2.
        a.revoke(&hex_encode(&record_of(&other, &ka).grant_id))
            .unwrap();
        let r_grant = hex_encode(&record_of(&receiver, &ka).grant_id);
        let producer = Arc::clone(&a);
        let pulls = count_pulls(&receiver, move |n| {
            if n == 1 {
                producer.revoke(&r_grant).unwrap();
            }
        });
        clock.advance(61);
        let page = agents(&call(
            &receiver,
            &context,
            false,
            &[ka],
            "UTC",
            Mode::Refresh,
        ))
        .unwrap();
        assert_eq!(states(&page), [(fingerprint(&ka), "refused".into())]);
        assert_eq!(page["devices"][0]["reason"], "stopped");
        assert_eq!(pulls.load(Ordering::SeqCst), 1, "retried");
        assert_eq!(record_of(&receiver, &ka).state, PeerState::Revoked);
    }

    /// The producer's pricing copy older than an hour (by the dataset's own
    /// timestamp) still prices what it shares: a client that records no cost
    /// of its own (Claude Code) is priced by that copy (its price differs
    /// from the fresh copy's here, so the cost tells which one was used).
    #[test]
    fn shared_cost_with_fresh_and_hour_old_pricing() {
        let clock = TestClock::new();
        let (a_home, r_home) = (TempHome::new(), TempHome::new());
        let context = unused_context();
        write_session(
            &a_home.0,
            "a.jsonl",
            &[claude(
                "a1",
                "2021-03-10T05:45:00.000Z",
                "claude-3-5-sonnet",
                1_000_000,
                0,
                false,
            )],
        );
        let source = a_home.0.join("pricing-src");
        fs::create_dir_all(&source).unwrap();
        let write = |age: u64, bump: u64, input_price: f64| {
            let now = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_secs();
            let dataset = |data: Value| json!({"timestamp": now - age, "data": data}).to_string();
            fs::write(
                source.join("pricing-litellm.json"),
                dataset(json!({"claude-3-5-sonnet": {"input_cost_per_token": input_price}})),
            )
            .unwrap();
            fs::write(source.join("pricing-openrouter.json"), dataset(json!({}))).unwrap();
            for name in crate::remote_agent::PRICING_FILES {
                fs::File::options()
                    .write(true)
                    .open(source.join(name))
                    .unwrap()
                    .set_modified(SystemTime::now() + Duration::from_secs(bump))
                    .unwrap();
            }
        };
        write(0, 0, 0.000_001);
        let a = producer(&a_home.0, &clock);
        let receiver = agent_at(&r_home.0, &clock);
        let ka = pair(&a, &receiver);
        let cost = || {
            clock.advance(61);
            let page = models(&call(
                &receiver,
                &context,
                false,
                &[ka],
                "UTC",
                Mode::Refresh,
            ))
            .unwrap();
            assert_eq!(
                states(&page),
                [(fingerprint(&ka), "fresh".into())],
                "{page}"
            );
            page["totalCost"].as_f64().unwrap()
        };
        let fresh = cost();
        // A different price in the hour-old table: its cost shows that this
        // table, not the fresh one copied before it, priced the report.
        write(3_700, 10, 0.000_002);
        std::thread::sleep(PERIOD * 5);
        let stale = cost();
        println!("SA4C3-PRICING fresh={fresh} stale={stale}");
        assert!((fresh - 1.0).abs() < 1e-9, "fresh pricing: {fresh}");
        assert!((stale - 2.0).abs() < 1e-9, "hour-old pricing: {stale}");
    }

    // ------------------------------------------------------- fixtures ---
    //
    // JSON fixtures for the app's decoders (SA-4d), each the exact envelope
    // a Rust export or the production reply path (`remote_agent::ffi`)
    // produced, after [`normalize`]. All are produced inside one env-cleared
    // child (`TOKSCALE_PRICING_CACHE_ONLY`, an empty `HOME`, a fixture
    // `TOKSCALE_CONFIG_DIR`), so the local part and the pricing cache are the
    // same on every machine. `print_remote_fixtures` prints them for the
    // committed files; `remote_fixtures_are_current` compares them.

    /// Where the committed fixtures live.
    fn fixture_dir() -> PathBuf {
        Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("..")
            .join("src")
            .join("TokenBar.Core.Tests")
            .join("Fixtures")
            .join("remote")
    }

    /// The stand-in for every Pair URL. Never a real offer: an offer's URL
    /// carries its secret.
    const FAKE_PAIR_URL: &str = "syrtis://pair?offer=TEST-FIXTURE-NOT-A-REAL-OFFER";
    /// The stand-in for every time stamp (Unix seconds or milliseconds).
    const FIXED_TIME: i64 = 1_622_548_800;
    /// The stand-in for `meta.generatedAt`.
    const FIXED_GENERATED_AT: &str = "2021-06-01T12:00:00+00:00";

    /// Replaces the values of a fixture that differ from run to run, keeping
    /// each value's JSON type and nullness (a number becomes a fixed number,
    /// a string a fixed string, `null` stays `null`). It touches only these
    /// object keys, wherever they occur:
    ///
    /// - `generatedAt` (graph `meta.generatedAt`) → [`FIXED_GENERATED_AT`];
    /// - time stamps → [`FIXED_TIME`]: `pricingUpdatedAt` (models), and in the
    ///   `devices[]` rows `lastSuccessAt`; in the `tb_remote_*` replies
    ///   `expiresAt`, `createdAt`, `lastScanAt`;
    /// - ports → `40001`, `40002`, … in order of first appearance (the same
    ///   port, the same number): `bindPort`, `port`, and the port of
    ///   `devices[].address`;
    /// - `devices[].address` (`host:port`) → the host and the mapped port;
    ///   `devices[].label` when it equals the row's original `address` → the
    ///   same replacement;
    /// - hex identifiers → a hex string of the same length numbered in order
    ///   of first appearance (the same value, the same number), so a device's
    ///   id, fingerprint and static still match across replies:
    ///   `devices[].id` (only a 16-hex fingerprint; `local` and `peers` stay),
    ///   `fingerprint`, `identityFingerprint`, `producerFingerprint`,
    ///   `peerFingerprint`, `producerStatic`, `grantId`, `pendingId`,
    ///   `offerId`, `ownerInputsSha256`;
    /// - `pairUrl` → [`FAKE_PAIR_URL`]; `sas`, `sasCode` → every letter and
    ///   digit `0`;
    /// - the disclosure's paths (`home`, `configDir`, `dataDir`,
    ///   `xdgDataHome`, `sourceCacheDir`, and each path in `extraScanPaths`,
    ///   `excludedScanPaths`, `opencodeDbPaths`) → `C:\fixture\<key>` (`\<n>`
    ///   for the n-th path of a list).
    fn normalize(value: &mut Value) {
        #[derive(Default)]
        struct Maps {
            ports: Vec<u64>,
            hex: Vec<String>,
        }
        impl Maps {
            fn port(&mut self, port: u64) -> u64 {
                let at = match self.ports.iter().position(|p| *p == port) {
                    Some(at) => at,
                    None => {
                        self.ports.push(port);
                        self.ports.len() - 1
                    }
                };
                40_001 + at as u64
            }
            fn hex(&mut self, text: &str) -> String {
                let at = match self.hex.iter().position(|h| h == text) {
                    Some(at) => at,
                    None => {
                        self.hex.push(text.to_owned());
                        self.hex.len() - 1
                    }
                };
                format!("{:0width$x}", at + 1, width = text.len())
            }
            fn address(&mut self, address: &str) -> String {
                match address.rsplit_once(':') {
                    Some((host, port)) => match port.parse::<u64>() {
                        Ok(port) => format!("{host}:{}", self.port(port)),
                        Err(_) => address.to_owned(),
                    },
                    None => address.to_owned(),
                }
            }
        }
        const HEX_KEYS: [&str; 9] = [
            "fingerprint",
            "identityFingerprint",
            "producerFingerprint",
            "peerFingerprint",
            "producerStatic",
            "grantId",
            "pendingId",
            "offerId",
            "ownerInputsSha256",
        ];
        const PATH_KEYS: [&str; 5] = [
            "home",
            "configDir",
            "dataDir",
            "xdgDataHome",
            "sourceCacheDir",
        ];
        const PATH_LIST_KEYS: [&str; 3] =
            ["extraScanPaths", "excludedScanPaths", "opencodeDbPaths"];
        fn is_hex(text: &str) -> bool {
            !text.is_empty() && text.bytes().all(|b| b.is_ascii_hexdigit())
        }
        fn fixed_number(value: &mut Value, number: i64) {
            if value.is_number() {
                *value = json!(number);
            }
        }
        fn paths(value: &mut Value, key: &str) {
            match value {
                Value::String(text) => *text = format!(r"C:\fixture\{key}"),
                Value::Array(items) => {
                    for (at, item) in items.iter_mut().enumerate() {
                        if item.is_string() {
                            *item = json!(format!(r"C:\fixture\{key}\{at}"));
                        }
                    }
                }
                Value::Object(map) => map.values_mut().for_each(|v| paths(v, key)),
                _ => {}
            }
        }
        fn walk(value: &mut Value, maps: &mut Maps) {
            match value {
                Value::Array(items) => items.iter_mut().for_each(|item| walk(item, maps)),
                Value::Object(map) => {
                    // A device row: its label follows its address.
                    let original_address = map
                        .get("address")
                        .and_then(Value::as_str)
                        .map(str::to_owned);
                    for (key, field) in map.iter_mut() {
                        match key.as_str() {
                            "generatedAt" if field.is_string() => {
                                *field = json!(FIXED_GENERATED_AT)
                            }
                            "pricingUpdatedAt" | "lastSuccessAt" | "expiresAt" | "createdAt"
                            | "lastScanAt" => fixed_number(field, FIXED_TIME),
                            "bindPort" | "port" => {
                                if let Some(port) = field.as_u64() {
                                    *field = json!(maps.port(port));
                                }
                            }
                            "address" => {
                                if let Some(text) = field.as_str() {
                                    *field = json!(maps.address(text));
                                }
                            }
                            "label" => {
                                if let (Some(text), Some(address)) =
                                    (field.as_str(), original_address.as_deref())
                                {
                                    if text == address {
                                        *field = json!(maps.address(text));
                                    }
                                }
                            }
                            "id" => {
                                if let Some(text) = field.as_str() {
                                    if text.len() == 16 && is_hex(text) {
                                        *field = json!(maps.hex(text));
                                    }
                                }
                            }
                            "pairUrl" if field.is_string() => *field = json!(FAKE_PAIR_URL),
                            "sas" | "sasCode" => {
                                if let Some(text) = field.as_str() {
                                    *field = json!(text
                                        .chars()
                                        .map(|c| if c.is_ascii_alphanumeric() { '0' } else { c })
                                        .collect::<String>());
                                }
                            }
                            key if HEX_KEYS.contains(&key) => {
                                if let Some(text) = field.as_str() {
                                    *field = json!(maps.hex(text));
                                }
                            }
                            key if PATH_KEYS.contains(&key) || PATH_LIST_KEYS.contains(&key) => {
                                paths(field, key)
                            }
                            _ => walk(field, maps),
                        }
                    }
                }
                _ => {}
            }
        }
        walk(value, &mut Maps::default());
    }

    /// The normalizer keeps types and nulls: a `stale` row's null time stays
    /// null, a `lastGood` row's number stays a number; a fingerprint id stays
    /// a 16-hex string, `local` stays; a label equal to the address follows
    /// it, a name does not.
    #[test]
    fn the_fixture_normalizer_keeps_types_and_nulls() {
        let mut sample = json!({
            "meta": {"generatedAt": "2026-11-21T03:04:05.678+00:00", "version": "v"},
            "pricingUpdatedAt": null,
            "devices": [
                {"id": "local", "label": null, "state": "local", "lastSuccessAt": null},
                {"id": "0123456789abcdef", "label": "127.0.0.1:51234",
                 "address": "127.0.0.1:51234", "state": "stale", "lastSuccessAt": null,
                 "reason": null, "costIncomplete": true, "everAnswered": true, "totals": null},
                {"id": "fedcba9876543210", "label": "desk", "address": "127.0.0.1:51999",
                 "state": "lastGood", "lastSuccessAt": 1_763_000_000_i64, "reason": "unreachable",
                 "costIncomplete": false, "everAnswered": true,
                 "totals": [{"client": "claude", "messages": 1, "tokens": 2, "costUsd": 0.5}]}
            ],
            "offer": {"pairUrl": "syrtis://pair?offer=abc", "offerId": "00ff", "expiresAt": 5},
            "claim": {"sas": "1A2B-5678", "handle": 3},
            "status": {"bindPort": 51234, "lastScanAt": null, "identityFingerprint": "0123456789abcdef"},
        });
        normalize(&mut sample);
        let rows = &sample["devices"];
        assert_eq!(sample["meta"]["generatedAt"], FIXED_GENERATED_AT);
        assert_eq!(sample["meta"]["version"], "v", "an untouched key");
        assert_eq!(sample["pricingUpdatedAt"], Value::Null);
        assert_eq!(
            rows[0],
            json!({"id": "local", "label": null, "state": "local", "lastSuccessAt": null})
        );
        assert_eq!(
            rows[1]["lastSuccessAt"],
            Value::Null,
            "stale: null stays null"
        );
        assert_eq!(
            rows[2]["lastSuccessAt"],
            json!(FIXED_TIME),
            "lastGood: a number"
        );
        assert_eq!(rows[1]["id"], "0000000000000001");
        assert_eq!(rows[2]["id"], "0000000000000002");
        assert_eq!(rows[1]["address"], "127.0.0.1:40001");
        assert_eq!(
            rows[1]["label"], "127.0.0.1:40001",
            "the label follows the address"
        );
        assert_eq!(rows[2]["address"], "127.0.0.1:40002");
        assert_eq!(rows[2]["label"], "desk", "a name is kept");
        assert_eq!(rows[2]["totals"][0]["costUsd"], 0.5);
        assert_eq!(sample["offer"]["pairUrl"], FAKE_PAIR_URL);
        assert_eq!(sample["offer"]["offerId"], "0003");
        assert_eq!(sample["offer"]["expiresAt"], json!(FIXED_TIME));
        assert_eq!(sample["claim"], json!({"sas": "0000-0000", "handle": 3}));
        assert_eq!(
            sample["status"]["bindPort"], 40001,
            "the same port, the same number"
        );
        assert_eq!(sample["status"]["lastScanAt"], Value::Null);
        assert_eq!(
            sample["status"]["identityFingerprint"], "0000000000000001",
            "the same value, the same number"
        );
    }

    /// Every class a `tb_remote_*` or `tb_*_devices` entry can answer, for an
    /// error-envelope fixture each. Not reachable live on Windows:
    /// `pathTooLong`, `unsafeSocket` (no socket; `control` instead),
    /// `pollLimit` and `lostAttempt` (only after the frozen poll window or a
    /// lost claim race), `identityChanged` (a restart on another identity
    /// during a poll), `homeRequired` (test builds only).
    const ERROR_CLASSES: [&str; 25] = [
        "notRunning",
        "invalid",
        "unauthorized",
        "expired",
        "busy",
        "unsafeState",
        "internal",
        "unsupported",
        "control",
        "pathTooLong",
        "unsafeSocket",
        "homeRequired",
        "panic",
        "selfPairing",
        "invalidOffer",
        "offerUnusable",
        "lostAttempt",
        "incompatible",
        "rejected",
        "revoked",
        "aborted",
        "pollLimit",
        "transport",
        "identityChanged",
        "sourceContextUnavailable",
    ];

    /// The four pages, by fixture prefix: the FFI export, the page function.
    type Export = unsafe extern "C" fn(
        *const c_char,
        *const c_char,
        *const c_char,
        *const c_char,
        *const c_char,
    ) -> *mut c_char;

    unsafe extern "C" fn graph_export(
        year: *const c_char,
        _clients: *const c_char,
        devices: *const c_char,
        tz: *const c_char,
        mode: *const c_char,
    ) -> *mut c_char {
        unsafe { crate::remote_pages_ffi::tb_graph_devices(year, devices, tz, mode) }
    }

    unsafe extern "C" fn models_export(
        year: *const c_char,
        _clients: *const c_char,
        devices: *const c_char,
        tz: *const c_char,
        mode: *const c_char,
    ) -> *mut c_char {
        unsafe { crate::remote_pages_ffi::tb_model_report_devices(year, devices, tz, mode) }
    }

    type With =
        fn(&Call<'_>, Box<dyn FnOnce(&ReportQuery) -> Remote + '_>) -> Result<Value, String>;

    const PAGES: [(&str, Endpoint, Export, With); 4] = [
        ("graph", Endpoint::Graph, graph_export, |c, s| {
            graph_with(c, s)
        }),
        ("models", Endpoint::Models, models_export, |c, s| {
            models_with(c, s)
        }),
        (
            "hourly",
            Endpoint::Hourly,
            crate::remote_pages_ffi::tb_hourly_report_devices,
            |c, s| hourly_with(c, s),
        ),
        (
            "agents",
            Endpoint::Agents,
            crate::remote_pages_ffi::tb_agents_report_devices,
            |c, s| agents_with(c, s),
        ),
    ];

    /// One record of `endpoint` on 2021-03-10 12:00 UTC (inside the fixture
    /// year), of `client` and `model`, `input` tokens and `cost` nano-USD.
    fn record_of_endpoint(
        endpoint: Endpoint,
        client: &str,
        model: &str,
        input: u64,
        cost: u64,
    ) -> Records {
        let bucket = day_ms("2021-03-10").unwrap() + 12 * 3_600_000;
        match endpoint {
            Endpoint::Graph => Records::Graph(vec![GraphRecord {
                client: client.into(),
                model: Some(model.into()),
                cost_nano_usd: cost,
                ..graph_record("2021-03-10", input, cost)
            }]),
            Endpoint::Models => Records::Models(vec![ModelsRecord {
                client: client.into(),
                model: Some(model.into()),
                ..models_record(model, input, cost)
            }]),
            Endpoint::Hourly => Records::Hourly(vec![HourlyRecord {
                client: client.into(),
                model: Some(model.into()),
                cost_nano_usd: cost,
                ..hourly_record(bucket, 0, input)
            }]),
            Endpoint::Agents => Records::Agents(vec![AgentsRecord {
                agent: "Main".into(),
                client: client.into(),
                model: Some(model.into()),
                provider: Some("p".into()),
                input_tokens: input,
                output_tokens: 0,
                cache_read_tokens: 0,
                cache_write_tokens: 0,
                reasoning_tokens: 0,
                total_tokens: input,
                message_count: 1,
                turn_count: 0,
                cost_nano_usd: cost,
            }]),
        }
    }

    /// `count` records of `endpoint`, one client each (over the limits when
    /// `count` is).
    fn many_of_endpoint(endpoint: Endpoint, count: usize) -> Records {
        let one = |i: usize| record_of_endpoint(endpoint, &format!("c{i:06}"), "m", 1, 0);
        let mut all = one(0);
        for i in 1..count {
            match (&mut all, one(i)) {
                (Records::Graph(a), Records::Graph(b)) => a.extend(b),
                (Records::Models(a), Records::Models(b)) => a.extend(b),
                (Records::Hourly(a), Records::Hourly(b)) => a.extend(b),
                (Records::Agents(a), Records::Agents(b)) => a.extend(b),
                _ => unreachable!(),
            }
        }
        all
    }

    /// A synthetic producer part through the production `assemble`.
    fn part(key: u8, got: Got, last_success: Option<i64>, reason: Option<&'static str>) -> Pulled {
        Pulled {
            peer: [key; 32],
            grant: [key; 16],
            address: Some(format!("10.0.0.{key}:4000")),
            name: None,
            got,
            cost_incomplete: false,
            ever_answered: true,
            last_success,
            reason,
        }
    }

    fn fresh(records: Records) -> Got {
        Got::Fresh(Report {
            records,
            saturated: false,
        })
    }

    /// A page of synthetic producer parts: the production `*_with` over
    /// `assemble`, through the production reply path; producers only.
    fn synthetic(
        with: With,
        year: &str,
        parts: impl FnOnce(Endpoint) -> Vec<Pulled>,
        endpoint: Endpoint,
    ) -> Value {
        let context = unused_context();
        let agent = RemoteAgent::default();
        let pulled = parts(endpoint);
        let selection = Selection {
            local: false,
            peers: pulled.iter().map(|p| p.peer).collect(),
        };
        let call = Call {
            agent: &agent,
            context: &context,
            year,
            clients: None,
            selection,
            timezone: "UTC",
            mode: Mode::Refresh,
            today: NaiveDate::from_ymd_opt(2021, 6, 1).unwrap(),
        };
        take(crate::remote_agent::ffi("fixture", || {
            with(&call, Box::new(|query| assemble(query, pulled)))
        }))
    }

    /// Every fixture, normalized, by file name. Runs live agents over
    /// 127.0.0.1 and the process-wide [`crate::remote_agent::AGENT`]: only in
    /// the env-cleared child.
    fn fixtures() -> Vec<(String, Value)> {
        assert!(
            child_root().is_some(),
            "fixtures are made in the child only"
        );
        use crate::remote_agent::AGENT;
        use crate::remote_ffi as f;
        let out = std::cell::RefCell::new(Vec::<(String, Value)>::new());
        let add = |name: String, value: Value| out.borrow_mut().push((name, value));
        let clock = TestClock::new();
        let year = c(YEAR);
        let utc = c("UTC");
        let (cached, refresh) = (c("cached"), c("refresh"));
        let local_only = c(r#"{"local":true,"peers":[]}"#);

        // ---- the error envelopes, through the production reply path.
        for class in ERROR_CLASSES {
            let reply = if class == "panic" {
                take(crate::remote_agent::ffi("fixture", || {
                    panic!("fixture panic")
                }))
            } else {
                take(crate::remote_agent::ffi("fixture", || Err(class.into())))
            };
            add(format!("error-{class}.json"), reply);
        }

        // ---- local only: the empty fixture home (the control below).
        for (page, _, export, _) in PAGES {
            let reply = take(unsafe {
                export(
                    year.as_ptr(),
                    std::ptr::null(),
                    local_only.as_ptr(),
                    utc.as_ptr(),
                    cached.as_ptr(),
                )
            });
            let data = &reply["data"];
            let usage = match page {
                "graph" => &data["contributions"],
                _ => &data["entries"],
            };
            assert_eq!(
                usage,
                &json!([]),
                "{page}: local usage leaked into the fixture"
            );
            add(format!("{page}-local-only.json"), reply);
        }

        // ---- the receiver: the process-wide agent, as the app has it.
        add(
            "remote-status-not-running.json".into(),
            take(f::tb_remote_status()),
        );
        let r_home = TempHome::new();
        start_at(&AGENT, &r_home.0, &clock, ScannerSettings::default());
        add(
            "remote-status-running.json".into(),
            take(f::tb_remote_status()),
        );

        // Two producers with one session each day and model, at mid-day UTC.
        let (p1_home, p2_home) = (TempHome::new(), TempHome::new());
        write_session(
            &p1_home.0,
            "a.jsonl",
            &[
                claude(
                    "a1",
                    "2021-03-10T12:00:00.000Z",
                    "claude-3-5-sonnet",
                    7,
                    3,
                    false,
                ),
                claude(
                    "a2",
                    "2021-03-12T12:00:00.000Z",
                    "claude-3-opus",
                    11,
                    1,
                    false,
                ),
            ],
        );
        write_session(
            &p1_home.0,
            "agent-x.jsonl",
            &[claude(
                "a3",
                "2021-03-14T12:00:00.000Z",
                "claude-3-haiku",
                13,
                2,
                true,
            )],
        );
        write_session(
            &p2_home.0,
            "b.jsonl",
            &[claude(
                "b1",
                "2021-03-11T12:00:00.000Z",
                "claude-3-5-sonnet",
                1000,
                100,
                false,
            )],
        );
        let p1 = share(agent_at(&p1_home.0, &clock), Some("desk"));
        let p2 = producer(&p2_home.0, &clock);

        // P2 first rejects the receiver once (a live `rejected` poll).
        let offer = p2.offer_create(600).unwrap();
        let url = c(offer["pairUrl"].as_str().unwrap());
        let claim = take(unsafe { f::tb_remote_pair_claim(url.as_ptr()) });
        let pending = p2.pending_list().unwrap()[0]["pendingId"]
            .as_str()
            .unwrap()
            .to_owned();
        p2.decide(false, &pending, 1).unwrap();
        add(
            "remote-pair-poll-rejected.json".into(),
            take(f::tb_remote_pair_poll(
                claim["data"]["handle"].as_i64().unwrap(),
            )),
        );
        // Then both pair through the exports.
        let pair_global = |producer: &RemoteAgent, record: bool| {
            let offer = producer.offer_create(600).unwrap();
            let url = c(offer["pairUrl"].as_str().unwrap());
            let claim = take(unsafe { f::tb_remote_pair_claim(url.as_ptr()) });
            let pending = producer.pending_list().unwrap()[0]["pendingId"]
                .as_str()
                .unwrap()
                .to_owned();
            producer.decide(true, &pending, 1).unwrap();
            let paired = take(f::tb_remote_pair_poll(
                claim["data"]["handle"].as_i64().unwrap(),
            ));
            assert_eq!(paired["ok"], true);
            let key = paired["data"]["producerStatic"]
                .as_str()
                .unwrap()
                .to_owned();
            if record {
                add("remote-pair-claim.json".into(), claim);
                add("remote-pair-poll-paired.json".into(), paired);
            }
            key
        };
        let k1 = pair_global(&p1, true);
        let k2 = pair_global(&p2, false);

        let devices =
            |local: bool, key: &str| c(&format!(r#"{{"local":{local},"peers":["{key}"]}}"#));
        let (only_p1, local_p1, only_p2) =
            (devices(false, &k1), devices(true, &k1), devices(false, &k2));
        let live = |name: &str, export: Export, selection: &CString, state: &str, reason: Value| {
            clock.advance(61);
            let reply = take(unsafe {
                export(
                    year.as_ptr(),
                    std::ptr::null(),
                    selection.as_ptr(),
                    utc.as_ptr(),
                    refresh.as_ptr(),
                )
            });
            let rows = reply["data"]["devices"].as_array().unwrap();
            let row = rows.last().unwrap();
            assert_eq!(
                (&row["state"], &row["reason"]),
                (&json!(state), &reason),
                "{name}"
            );
            reply
        };
        let mut found = Vec::new();
        for (page, _, export, _) in PAGES {
            found.push((
                format!("{page}-peer-only.json"),
                live(page, export, &only_p1, "fresh", Value::Null),
            ));
            let reply = live(page, export, &local_p1, "fresh", Value::Null);
            // Control: no local usage entered (graph and models carry the
            // local totals; hourly and agents were checked empty above).
            if matches!(page, "graph" | "models") {
                assert_eq!(
                    reply["data"]["devices"][0]["totals"],
                    json!([]),
                    "{page}: local leak"
                );
            }
            found.push((format!("{page}-local-and-fresh-peer.json"), reply));
        }
        // P2 revoked: `refused`, `stopped`.
        let grant = p2.grants().unwrap()[0]["grantId"]
            .as_str()
            .unwrap()
            .to_owned();
        p2.revoke(&grant).unwrap();
        for (page, _, export, _) in PAGES {
            found.push((
                format!("{page}-peer-refused.json"),
                live(page, export, &only_p2, "refused", json!("stopped")),
            ));
        }
        // P1 stopped: no answer, so its last good body, `unreachable`.
        p1.stop().unwrap();
        for (page, _, export, _) in PAGES {
            found.push((
                format!("{page}-peer-last-good.json"),
                live(page, export, &only_p1, "lastGood", json!("unreachable")),
            ));
        }
        for (name, value) in found {
            add(name, value);
        }
        // The store lists records in the order of their random keys: sorted
        // by state here (active, then revoked) so the fixture is stable.
        let mut peers = take(f::tb_remote_peers());
        peers["data"]
            .as_array_mut()
            .unwrap()
            .sort_by_key(|peer| peer["state"].as_str().unwrap_or_default().to_owned());
        add("remote-peers.json".into(), peers);

        // An unreadable peer store: no agent runs.
        let stopped = RemoteAgent::default();
        let context = unused_context();
        for (page, _, _, with) in PAGES {
            let call = call(
                &stopped,
                &context,
                false,
                &[[0x11; 32]],
                "UTC",
                Mode::Cached,
            );
            let reply = take(crate::remote_agent::ffi("fixture", || {
                with(&call, Box::new(|query| call.gather(query)))
            }));
            add(format!("{page}-peer-store-unavailable.json"), reply);
        }

        // ---- every other state and reason: synthetic parts, `assemble`.
        for (page, endpoint, _, with) in PAGES {
            let states = |endpoint| {
                let one = || record_of_endpoint(endpoint, "claude", "m", 5, 1_000_000);
                let invalid = match endpoint {
                    Endpoint::Graph => Got::Fresh(Report {
                        records: Records::Graph(vec![graph_record("2020-12-31", 1, 0)]),
                        saturated: false,
                    }),
                    _ => Got::Failed(FailureClass::InvalidReply),
                };
                vec![
                    part(
                        1,
                        Got::Cached(Some(Report {
                            records: one(),
                            saturated: false,
                        })),
                        None,
                        None,
                    ),
                    part(2, Got::Superseded, Some(FIXED_TIME), None),
                    part(3, Got::Unavailable, None, None),
                    part(
                        4,
                        fresh(many_of_endpoint(endpoint, PEER_RECORDS_MAX + 1)),
                        Some(FIXED_TIME),
                        None,
                    ),
                    part(5, invalid, Some(FIXED_TIME), None),
                    part(6, Got::Incompatible, None, None),
                ]
            };
            let reply = synthetic(with, YEAR, states, endpoint);
            let got: Vec<_> = reply["data"]["devices"]
                .as_array()
                .unwrap()
                .iter()
                .map(|d| d["state"].as_str().unwrap().to_owned())
                .collect();
            assert_eq!(
                got,
                [
                    "stale",
                    "stale",
                    "unavailable",
                    "overLimit",
                    "invalidReply",
                    "incompatible"
                ],
                "{page}"
            );
            add(format!("{page}-states.json"), reply);

            let reasons = |endpoint| {
                let one = || record_of_endpoint(endpoint, "claude", "m", 5, 1_000_000);
                vec![
                    part(1, fresh(one()), Some(FIXED_TIME), None),
                    part(2, Got::Cached(None), None, Some("unreachable")),
                    part(3, Got::Cached(None), None, Some("timeout")),
                    part(
                        4,
                        Got::Cached(Some(Report {
                            records: one(),
                            saturated: false,
                        })),
                        Some(FIXED_TIME),
                        Some("busy"),
                    ),
                    part(5, Got::Failed(FailureClass::Refused), None, Some("stopped")),
                    part(6, Got::Failed(FailureClass::Refused), None, Some("error")),
                ]
            };
            let reply = synthetic(with, YEAR, reasons, endpoint);
            let got: Vec<_> = reply["data"]["devices"]
                .as_array()
                .unwrap()
                .iter()
                .map(|d| (d["state"].clone(), d["reason"].clone()))
                .collect();
            assert_eq!(
                got,
                [
                    (json!("fresh"), Value::Null),
                    (json!("unavailable"), json!("unreachable")),
                    (json!("unavailable"), json!("timeout")),
                    (json!("lastGood"), json!("busy")),
                    (json!("refused"), json!("stopped")),
                    (json!("refused"), json!("error")),
                ],
                "{page}"
            );
            add(format!("{page}-reasons.json"), reply);

            let saturated = |endpoint| {
                vec![part(
                    1,
                    fresh(record_of_endpoint(endpoint, "claude", "m", u64::MAX, 0)),
                    Some(FIXED_TIME),
                    None,
                )]
            };
            let reply = synthetic(with, YEAR, saturated, endpoint);
            assert_eq!(reply["data"]["saturated"], true, "{page}");
            add(format!("{page}-saturated.json"), reply);

            let all_time = |endpoint| {
                vec![part(
                    1,
                    fresh(record_of_endpoint(endpoint, "claude", "m", 5, 1_000_000)),
                    Some(FIXED_TIME),
                    None,
                )]
            };
            let reply = synthetic(with, "", all_time, endpoint);
            assert_eq!(reply["data"]["remoteRangeLimited"], true, "{page}");
            add(format!("{page}-all-time.json"), reply);
        }

        // ---- the producer side: the process-wide agent shares.
        assert_eq!(take(f::tb_remote_stop())["data"]["running"], false);
        drop(p1);
        drop(p2);
        let g_home = TempHome::new();
        start_at(&AGENT, &g_home.0, &clock, ScannerSettings::default());
        let host = c("127.0.0.1");
        let label = c("desk");
        add(
            "remote-listen-configure.json".into(),
            take(unsafe {
                f::tb_remote_listen_configure(host.as_ptr(), i32::from(free_port()), label.as_ptr())
            }),
        );
        let disclosure = take(f::tb_remote_scope_disclosure());
        let shown = c(disclosure["data"]["ownerInputsSha256"].as_str().unwrap());
        add("remote-scope-disclosure.json".into(), disclosure);
        add(
            "remote-scope-approve.json".into(),
            take(unsafe { f::tb_remote_scope_approve(shown.as_ptr()) }),
        );
        add(
            "remote-sharing-set.json".into(),
            take(f::tb_remote_sharing_set(1)),
        );
        add(
            "remote-status-sharing.json".into(),
            take(f::tb_remote_status()),
        );
        let (q1_home, q2_home, q3_home) = (TempHome::new(), TempHome::new(), TempHome::new());
        let receivers = [
            agent_at(&q1_home.0, &clock),
            agent_at(&q2_home.0, &clock),
            agent_at(&q3_home.0, &clock),
        ];
        let claim_global_offer = |receiver: &RemoteAgent, record: bool| {
            let offer = take(f::tb_remote_offer_create(600));
            assert_eq!(offer["ok"], true);
            let claim = receiver
                .pair_claim(offer["data"]["pairUrl"].as_str().unwrap())
                .unwrap();
            let pending = take(f::tb_remote_pending_list());
            let id = c(pending["data"][0]["pendingId"].as_str().unwrap());
            if record {
                add("remote-offer-create.json".into(), offer);
                add("remote-pending-list.json".into(), pending);
            }
            assert_eq!(
                take(unsafe { f::tb_remote_approve(id.as_ptr(), 1) })["ok"],
                true
            );
            receiver
                .pair_poll(claim["handle"].as_u64().unwrap())
                .unwrap();
        };
        claim_global_offer(&receivers[0], true);
        claim_global_offer(&receivers[1], false);
        // Q1's grant revoked.
        let grants = take(f::tb_remote_grants());
        let first = c(grants["data"][0]["grantId"].as_str().unwrap());
        assert_eq!(
            take(unsafe { f::tb_remote_revoke(first.as_ptr()) })["ok"],
            true
        );
        // A changed scope approved: Q2's grant is superseded.
        let extra = g_home.0.join("extra");
        fs::create_dir_all(&extra).unwrap();
        start_at(
            &AGENT,
            &g_home.0,
            &clock,
            ScannerSettings {
                extra_scan_paths: std::collections::BTreeMap::from([(
                    "claude".to_string(),
                    vec![extra],
                )]),
                ..ScannerSettings::default()
            },
        );
        let changed = take(f::tb_remote_scope_disclosure());
        let shown = c(changed["data"]["ownerInputsSha256"].as_str().unwrap());
        assert_eq!(
            take(unsafe { f::tb_remote_scope_approve(shown.as_ptr()) })["ok"],
            true
        );
        if take(f::tb_remote_status())["data"]["sharingEnabled"] != true {
            assert_eq!(take(f::tb_remote_sharing_set(1))["ok"], true);
        }
        claim_global_offer(&receivers[2], false);
        let grants = take(f::tb_remote_grants());
        let mut grant_states: Vec<_> = grants["data"]
            .as_array()
            .unwrap()
            .iter()
            .map(|g| g["state"].as_str().unwrap().to_owned())
            .collect();
        grant_states.sort();
        assert_eq!(grant_states, ["active", "revoked", "superseded"]);
        add("remote-grants.json".into(), grants);
        add("remote-stop.json".into(), take(f::tb_remote_stop()));
        drop(receivers);

        let mut out = out.into_inner();
        out.sort_by(|a, b| a.0.cmp(&b.0));
        out.dedup_by(|a, b| a.0 == b.0);
        for (name, value) in &mut out {
            assert!(
                !name.contains(['/', '\\', ' ']),
                "a fixture name must be a plain file name"
            );
            normalize(value);
        }
        out
    }

    /// Prints every fixture as `=== FIXTURE <name> ===`, its JSON, then
    /// `=== END ===`, for the committed files (split on the Mac). Run with
    /// `--ignored print_remote_fixtures --nocapture`.
    #[test]
    #[ignore]
    fn print_remote_fixtures() {
        if child_root().is_none() {
            let stdout = run_in_child_with(
                &path_of("print_remote_fixtures"),
                "sa4c3-print",
                &["--ignored"],
            );
            for line in stdout.lines() {
                println!("{line}");
            }
            return;
        }
        for (name, value) in fixtures() {
            println!("=== FIXTURE {name} ===");
            println!("{}", serde_json::to_string_pretty(&value).unwrap());
            println!("=== END ===");
        }
    }

    /// The committed fixtures are what the Rust code produces now (compared
    /// as JSON values), and there are no others.
    #[test]
    fn remote_fixtures_are_current() {
        if child_root().is_none() {
            run_in_child_at(&path_of("remote_fixtures_are_current"), "sa4c3-current");
            return;
        }
        let dir = fixture_dir();
        let made = fixtures();
        let mut stale = Vec::new();
        for (name, value) in &made {
            let committed = fs::read_to_string(dir.join(name))
                .ok()
                .and_then(|text| serde_json::from_str::<Value>(&text).ok());
            if committed.as_ref() != Some(value) {
                stale.push(name.clone());
            }
        }
        let mut extra: Vec<String> = fs::read_dir(&dir)
            .map(|entries| {
                entries
                    .flatten()
                    .map(|entry| entry.file_name().to_string_lossy().into_owned())
                    .filter(|name| !made.iter().any(|(made, _)| made == name))
                    .collect()
            })
            .unwrap_or_default();
        extra.sort();
        assert!(
            stale.is_empty() && extra.is_empty(),
            "remote fixtures are stale: {stale:?}; not produced any more: {extra:?}. \
             Regenerate with `cargo test -p tb_core_ffi -- --ignored print_remote_fixtures \
             --nocapture` and split the output into {}",
            "src/TokenBar.Core.Tests/Fixtures/remote/"
        );
        assert!(made.len() > 60, "control: the fixtures were made");
    }
}
