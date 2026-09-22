//! Per-model usage breakdown for the popover, backed by tokscale-core's
//! `get_model_report`. Mirrors the design of tokscale's TUI "Models" view
//! (`crates/tokscale-cli/src/tui/ui/models.rs`): one row per exact
//! `(client, provider, model)` with the token breakdown, message count, cost,
//! and throughput (ms/1K), sorted by cost on the frontend.
//!
//! Like `usage_graph`, this drives the async core on a short-lived
//! current-thread runtime (callers run it inside `spawn_blocking`) and maps the
//! result onto a camelCase JSON shape the frontend consumes directly.

use serde::Serialize;
use serde_json::Value;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ModelEntry {
    client: String,
    model: String,
    provider: String,
    input: i64,
    output: i64,
    cache_read: i64,
    cache_write: i64,
    reasoning: i64,
    total: i64,
    message_count: i32,
    cost: f64,
    /// Milliseconds per 1K tokens, when tokscale could time the model. `None`
    /// when no message in the rollup carried a usable duration.
    ms_per_1k_tokens: Option<f64>,
    /// What the local pricing table would charge for this row's tokens, or
    /// `None` when it cannot price them. Reported rather than judged here on
    /// purpose: the frontend folds provider-split rows back to `(client,
    /// model)` first, and whether a cost is implausible is decided on the
    /// folded row (`CostPlausibility` in TokenBar.Core), not on a component.
    cost_estimate: Option<f64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ModelReportData {
    entries: Vec<ModelEntry>,
    total_input: i64,
    total_output: i64,
    total_cache_read: i64,
    total_cache_write: i64,
    total_messages: i32,
    total_cost: f64,
    /// Unix-seconds time the LiteLLM pricing dataset was last fetched from
    /// upstream (the on-disk cache write time). `None` before the first fetch.
    /// Surfaced as the "prices updated …" hint in the Models view.
    pricing_updated_at: Option<u64>,
}

/// Build the per-model report for `year` (empty string = all time).
pub(crate) fn run(context: &crate::LocalSourceContext, year: &str) -> Result<Value, String> {
    let year = normalize_year(year)?;
    let data = load_report(context, report_options(context, year))?;
    serde_json::to_value(data).map_err(|e| format!("serialize model report: {}", e))
}

fn report_options(
    context: &crate::LocalSourceContext,
    year: Option<String>,
) -> tokscale_core::ReportOptions {
    let mut options = context.report_options(year, None);
    // Group before the FFI boundary: ClientModel would comma-join providers and
    // C# cannot recover the original rows from that pre-aggregated value.
    options.group_by = tokscale_core::GroupBy::ClientProviderModel;
    options
}

fn load_report(
    context: &crate::LocalSourceContext,
    options: tokscale_core::ReportOptions,
) -> Result<ModelReportData, String> {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|e| format!("build runtime: {}", e))?;
    let report = runtime.block_on(tokscale_core::get_model_report_with_source_context(
        context.resolved(),
        options,
    ))?;

    // Read-only and offline: this reads a file, it never fetches. A cold cache
    // (first run, offline) yields None, which disables the check rather than
    // judging against a table that isn't there.
    //
    // Why this reads the table the report was priced from. In production this
    // consumer's context is not cache-only, so the report's own pricing comes
    // from `PricingService::get_or_init()`, falling back to the public
    // `load_cached_any_age` (tokscale-core lib.rs, `select_local_parse_pricing`).
    // `get_or_init`'s fetch is persisted by `cache::save_cache`, which writes to
    // `get_cache_dir()` — the directory this call reads. So they agree because
    // the report already goes through this same public path, not because two
    // directory resolvers happen to match.
    //
    // Where they can diverge — all UNMEASURED:
    //   - `TOKSCALE_PRICING_CACHE_ONLY` set: the report instead reads the
    //     context's `pricing_config_dir`, frozen at capture and qualified
    //     against the capture-time cwd, while this reads `get_cache_dir()`
    //     from the environment at call time. A relative `TOKSCALE_CONFIG_DIR`
    //     plus a cwd change can point them at different directories. Following
    //     that branch exactly would need `load_cached_any_age_from_config_dir`,
    //     which is `pub(crate)` in tokscale-core.
    //   - The report prices from the in-memory service, this from the disk
    //     file: they differ if `save_cache` failed, if an OpenRouter fetch came
    //     back empty (it returns an empty map rather than an error), or if
    //     another process rewrote the file inside the in-memory table's hour.
    //   - Non-default contexts (remote, injected, test contexts).
    // In each case the estimate is None (the check stays off) or priced from a
    // different table. A false warning would then need that other table to
    // price the same model at least 50x lower than the one the report used;
    // that is judged unlikely, not measured.
    let pricing = tokscale_core::pricing::PricingService::load_cached_any_age();
    Ok(map_report(report, pricing.as_ref()))
}

fn normalize_year(year: &str) -> Result<Option<String>, String> {
    let trimmed = year.trim();
    if trimmed.is_empty() {
        return Ok(None);
    }
    if trimmed.len() == 4 && trimmed.chars().all(|c| c.is_ascii_digit()) {
        Ok(Some(trimmed.to_string()))
    } else {
        Err(format!("invalid year filter: {}", year))
    }
}

fn map_report(
    report: tokscale_core::ModelReport,
    pricing: Option<&tokscale_core::pricing::PricingService>,
) -> ModelReportData {
    ModelReportData {
        entries: report
            .entries
            .into_iter()
            .map(|e| {
                // saturating_add so #766's i64::MAX-clamped buckets (corrupt
                // Antigravity DB) can't overflow this FFI-exposed total in
                // debug/release (see agents_report.rs's map_report for the
                // same pattern).
                let total = e
                    .input
                    .saturating_add(e.output)
                    .saturating_add(e.cache_read)
                    .saturating_add(e.cache_write)
                    .saturating_add(e.reasoning);
                let cost_estimate = local_cost_estimate(pricing, &e);
                ModelEntry {
                    client: e.client,
                    model: e.model,
                    provider: e.provider,
                    input: e.input,
                    output: e.output,
                    cache_read: e.cache_read,
                    cache_write: e.cache_write,
                    reasoning: e.reasoning,
                    total,
                    message_count: e.message_count,
                    cost: e.cost,
                    ms_per_1k_tokens: e.performance.ms_per_1k_tokens,
                    cost_estimate,
                }
            })
            .collect(),
        total_input: report.total_input,
        total_output: report.total_output,
        total_cache_read: report.total_cache_read,
        total_cache_write: report.total_cache_write,
        total_messages: report.total_messages,
        total_cost: report.total_cost,
        pricing_updated_at: tokscale_core::pricing::pricing_cached_at(),
    }
}

/// What the local pricing table would charge for this row's tokens, or `None`
/// when it cannot price them.
///
/// This exists because clients that record their own per-message cost
/// (OpenCode, MiMo Code) have it taken verbatim: tokscale-core's
/// `apply_pricing_if_available` returns early on `has_authoritative_cost()`,
/// so no pricing table ever sees those rows. That default is right — the
/// client knows its own billing contract — but it also means a unit error
/// upstream (a per-1K rate applied per-token, a non-USD figure, a mispriced
/// custom provider) reaches the UI with nothing in between. Shipping the
/// estimate alongside the cost gives the frontend something to compare
/// against; what counts as implausible is decided there, after the
/// provider-split rows have been folded together.
///
/// A zero or non-finite estimate is reported as `None`: the table cannot
/// price these tokens at all, which is not evidence about the reported cost
/// and must not become a division by zero downstream.
///
/// Ported from TokenBar (macOS) `crates/tb_core_ffi/src/model_report.rs`,
/// including the reasoning below; the engine pin is the same on both.
fn local_cost_estimate(
    pricing: Option<&tokscale_core::pricing::PricingService>,
    entry: &tokscale_core::ModelUsage,
) -> Option<f64> {
    let pricing = pricing?;
    let usage = tokscale_core::TokenBreakdown {
        input: entry.input,
        output: entry.output,
        cache_read: entry.cache_read,
        cache_write: entry.cache_write,
        reasoning: entry.reasoning,
        // `ModelUsage` carries no 1h/5m split, so this has to assume one.
        // Assume ALL of it is 1h, which is the more expensive of the two:
        // this estimate is the denominator of a ratio compared against a
        // threshold, so only an UPPER bound on it is safe. Under-estimating
        // inflates the ratio and invents warnings; over-estimating only
        // deflates it and stays quiet, which is the right way to fail for a
        // guard against costs wrong by three orders of magnitude.
        //
        // The asymmetry is not theoretical. The macOS port first passed 0
        // here (price everything at the 5m rate), and that produced a real
        // false-positive path: a 1h write is charged at 2x base input,
        // derived from `input_cost_per_token` because no table publishes a 1h
        // key, while the 5m rate is the table's `cache_creation_input_token_cost`
        // — which some entries omit. The provider hint steers
        // `claude-haiku-4-5` to a resale entry with no such key, so a
        // 5m-priced estimate dropped that row's cache write while tokscale
        // still billed it, and a cache-heavy row with a small remainder
        // cleared 50x on a LOCALLY priced row.
        //
        // Pricing the whole write at 1h closes that path at the source: the
        // estimate includes those tokens even when the matched entry has no
        // 5m rate, because the 1h rate is derived from input rather than
        // looked up. Where the real split was 5m the estimate runs high by at
        // most 2.0/1.25 = 1.6x, which only makes the guard quieter.
        cache_write_1h: entry.cache_write,
    };
    let estimate =
        pricing.calculate_cost_with_provider(&entry.model, Some(&entry.provider), &usage);
    (estimate.is_finite() && estimate > 0.0).then_some(estimate)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// #766 clamps corrupt Antigravity varints to `i64::MAX` per bucket. Two
    /// such buckets in one model entry must saturate the mapped `total`, not
    /// overflow it (a plain `+` panics in debug / wraps in release).
    fn entry(
        input: i64,
        output: i64,
        cache_read: i64,
        cache_write: i64,
        reasoning: i64,
    ) -> tokscale_core::ModelUsage {
        tokscale_core::ModelUsage {
            client: "antigravity_cli".to_string(),
            merged_clients: None,
            workspace_key: None,
            workspace_label: None,
            session_id: None,
            model: "gemini-3-pro".to_string(),
            provider: "antigravity".to_string(),
            input,
            output,
            cache_read,
            cache_write,
            reasoning,
            message_count: 1,
            cost: 0.0,
            performance: tokscale_core::ModelPerformance::default(),
        }
    }

    fn wrap(entries: Vec<tokscale_core::ModelUsage>) -> tokscale_core::ModelReport {
        tokscale_core::ModelReport {
            entries,
            total_input: 0,
            total_output: 0,
            total_cache_read: 0,
            total_cache_write: 0,
            total_messages: 1,
            total_cost: 0.0,
            processing_time_ms: 0,
        }
    }

    #[test]
    fn total_saturates_on_overlarge_buckets() {
        let report = wrap(vec![entry(i64::MAX, i64::MAX, 0, 0, 0)]);
        let mapped = map_report(report, None);
        assert_eq!(mapped.entries[0].total, i64::MAX);
    }

    /// The two-MAX-field case above only pins `input`/`output` into the fold.
    /// Pin the other three fields too: nonzero `input`/`output`/`reasoning`
    /// plus a clamped `cache_write`, so `cache_read`/`cache_write` inclusion
    /// is independently exercised, not just present-but-untested.
    #[test]
    fn total_saturates_when_cache_write_is_overlarge() {
        let report = wrap(vec![entry(10, 20, i64::MAX, i64::MAX, 5)]);
        let mapped = map_report(report, None);
        assert_eq!(mapped.entries[0].total, i64::MAX);
    }

    /// The saturating cases can't catch a dropped operand (another MAX field
    /// keeps the total at MAX), so pin every field's inclusion with distinct
    /// powers of two: omitting any one operand changes the exact sum.
    #[test]
    fn total_includes_every_token_field() {
        let report = wrap(vec![entry(1, 2, 4, 8, 16)]);
        let mapped = map_report(report, None);
        assert_eq!(mapped.entries[0].total, 31);
    }

    #[test]
    fn producer_groups_same_client_model_by_exact_provider() {
        const CHILD_HOME: &str = "TB_MODEL_REPORT_PROVIDER_FIXTURE_HOME";
        if let Some(home) = std::env::var_os(CHILD_HOME) {
            run_provider_fixture(std::path::Path::new(&home));
            return;
        }

        let root = std::env::temp_dir().join(format!(
            "tb-core-ffi-model-provider-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let fixture_dir = root.join(".mux/sessions/provider-fixture");
        std::fs::create_dir_all(&fixture_dir).unwrap();
        std::fs::write(
            fixture_dir.join("session-usage.json"),
            r#"{
                "version": 1,
                "byModel": {
                    "openai:same-model": {
                        "input": { "tokens": 1, "cost_usd": 0.1 },
                        "output": { "tokens": 2 }
                    },
                    "nvidia:same-model": {
                        "input": { "tokens": 3, "cost_usd": 0.2 },
                        "output": { "tokens": 4 }
                    },
                    "same-model": {
                        "input": { "tokens": 5, "cost_usd": 0.3 },
                        "output": { "tokens": 6 }
                    }
                },
                "lastRequest": { "timestamp": 1700000000000 }
            }"#,
        )
        .unwrap();

        let status = std::process::Command::new(std::env::current_exe().unwrap())
            .arg("model_report::tests::producer_groups_same_client_model_by_exact_provider")
            .arg("--exact")
            .env(CHILD_HOME, &root)
            .env("HOME", &root)
            .env("TOKSCALE_CONFIG_DIR", root.join("tokscale-config"))
            .env("TOKSCALE_PRICING_CACHE_ONLY", "1")
            .status()
            .unwrap();
        std::fs::remove_dir_all(&root).unwrap();
        assert!(status.success(), "isolated provider fixture failed");
    }

    fn run_provider_fixture(home: &std::path::Path) {
        let context = crate::LocalSourceContext::capture(
            Some(home.to_path_buf()),
            false,
            tokscale_core::ScannerSettings::default(),
        )
        .unwrap();
        let mut options = report_options(&context, None);
        options.clients = Some(vec!["mux".to_string()]);
        assert_eq!(
            options.group_by,
            tokscale_core::GroupBy::ClientProviderModel
        );

        let report = load_report(&context, options).unwrap();
        assert_eq!(report.entries.len(), 3);
        assert!(report.entries.iter().all(|entry| entry.client == "mux"
            && entry.model == "same-model"
            && !entry.provider.contains(',')));

        let by_provider: std::collections::HashMap<_, _> = report
            .entries
            .iter()
            .map(|entry| (entry.provider.as_str(), entry))
            .collect();
        assert_eq!(
            (
                by_provider["openai"].input,
                by_provider["openai"].output,
                by_provider["openai"].total,
                by_provider["openai"].message_count,
            ),
            (1, 2, 3, 1)
        );
        assert_eq!(
            (
                by_provider["nvidia"].input,
                by_provider["nvidia"].output,
                by_provider["nvidia"].total,
                by_provider["nvidia"].message_count,
            ),
            (3, 4, 7, 1)
        );
        assert_eq!(
            (
                by_provider[""].input,
                by_provider[""].output,
                by_provider[""].total,
                by_provider[""].message_count,
            ),
            (5, 6, 11, 1)
        );
        assert!((by_provider["openai"].cost - 0.1).abs() < f64::EPSILON);
        assert!((by_provider["nvidia"].cost - 0.2).abs() < f64::EPSILON);
        assert!((by_provider[""].cost - 0.3).abs() < f64::EPSILON);
        assert_eq!(
            report.entries.iter().map(|entry| entry.total).sum::<i64>(),
            21
        );
        assert_eq!(report.total_input, 9);
        assert_eq!(report.total_output, 12);
        assert_eq!(report.total_messages, 3);
        assert!((report.total_cost - 0.6).abs() < f64::EPSILON);
    }

    // MARK: - Implausible-cost guard (ported from TokenBar macOS)

    /// A hermetic stand-in for the LiteLLM table: one priced model at
    /// $1.00 per 1M input tokens and nothing else, so every estimate below is
    /// a number this test states rather than one the shipping dataset supplies
    /// (which would drift with upstream and make the assertions meaningless).
    fn priced_service(model: &str) -> tokscale_core::pricing::PricingService {
        let mut litellm = std::collections::HashMap::new();
        litellm.insert(
            model.to_string(),
            tokscale_core::pricing::litellm::ModelPricing {
                input_cost_per_token: Some(1e-6),
                ..Default::default()
            },
        );
        tokscale_core::pricing::PricingService::new(litellm, std::collections::HashMap::new())
    }

    /// 1M input tokens, which the table above prices at exactly $1.00.
    fn priced_entry(model: &str, cost: f64) -> tokscale_core::ModelUsage {
        let mut e = entry(1_000_000, 0, 0, 0, 0);
        e.model = model.to_string();
        e.provider = "deepseek".to_string();
        e.cost = cost;
        e
    }

    #[test]
    fn priced_model_reports_the_table_estimate() {
        let service = priced_service("m");
        // Independent of `cost`: the estimate describes the tokens, and the
        // comparison against cost happens downstream, after the fold.
        for cost in [0.0, 1.0, 1000.0, f64::NAN] {
            assert_eq!(
                local_cost_estimate(Some(&service), &priced_entry("m", cost)),
                Some(1.0),
                "cost {cost} must not change the estimate"
            );
        }
    }

    #[test]
    fn estimate_scales_with_the_tokens() {
        // Pins that the tokens actually reach the pricing call: a version that
        // priced a fixed or empty breakdown would return Some(1.0) here too.
        let service = priced_service("m");
        let mut e = priced_entry("m", 1.0);
        e.input = 3_000_000;
        assert_eq!(local_cost_estimate(Some(&service), &e), Some(3.0));
    }

    /// The false-positive path found on macOS: an entry with an input rate
    /// but no 5m cache-write rate. Pricing the write at 5m drops it from the
    /// estimate while tokscale still bills it at 2x input, so a cache-heavy
    /// row with a small remainder clears 50x on a locally priced row.
    #[test]
    fn cache_write_is_estimated_even_without_a_5m_rate() {
        let mut litellm = std::collections::HashMap::new();
        litellm.insert(
            "no5m".to_string(),
            tokscale_core::pricing::litellm::ModelPricing {
                input_cost_per_token: Some(1e-6),
                // cache_creation_input_token_cost deliberately absent.
                ..Default::default()
            },
        );
        let service = tokscale_core::pricing::PricingService::new(
            litellm,
            std::collections::HashMap::new(),
        );

        let mut e = entry(100_000, 0, 0, 10_000_000, 0);
        e.model = "no5m".to_string();
        e.provider = "anthropic".to_string();
        e.cost = 0.0;

        // 100K input at 1e-6 is 0.10; 10M cache write at 2x input is 20.00.
        let estimate = local_cost_estimate(Some(&service), &e).expect("priced");
        assert!(estimate > 20.0, "the cache write must be in the estimate, got {estimate}");

        // What tokscale would charge if that write were entirely 1h — the
        // estimate must not sit below it, or the ratio inflates.
        let billed = service.calculate_cost_with_provider(
            "no5m",
            Some("anthropic"),
            &tokscale_core::TokenBreakdown {
                input: e.input,
                output: 0,
                cache_read: 0,
                cache_write: e.cache_write,
                reasoning: 0,
                cache_write_1h: e.cache_write,
            },
        );
        assert!(estimate >= billed, "estimate {estimate} must upper-bound the billed {billed}");
    }

    #[test]
    fn unpriceable_rows_report_no_estimate() {
        let service = priced_service("m");
        // A model the table does not carry: None, so the frontend cannot
        // divide by it.
        assert_eq!(local_cost_estimate(Some(&service), &priced_entry("other", 99_999.0)), None);
        // No cached table at all (first run, offline).
        assert_eq!(local_cost_estimate(None, &priced_entry("m", 1000.0)), None);
        // Zero tokens price to 0.0, which is not a usable denominator either.
        let mut empty = priced_entry("m", 1000.0);
        empty.input = 0;
        assert_eq!(local_cost_estimate(Some(&service), &empty), None);
    }

    #[test]
    fn estimate_reaches_the_serialized_entry() {
        // The cases above test the function directly; this one proves
        // map_report carries it onto the wire shape, under the key C# reads.
        let service = priced_service("m");
        let report = wrap(vec![priced_entry("m", 1000.0), priced_entry("other", 1.0)]);
        let mapped = map_report(report, Some(&service));
        assert_eq!(mapped.entries[0].cost_estimate, Some(1.0));
        assert_eq!(mapped.entries[1].cost_estimate, None);

        let json = serde_json::to_value(&mapped).unwrap();
        assert_eq!(json["entries"][0]["costEstimate"], serde_json::json!(1.0));
        assert!(json["entries"][1]["costEstimate"].is_null());
    }
}
