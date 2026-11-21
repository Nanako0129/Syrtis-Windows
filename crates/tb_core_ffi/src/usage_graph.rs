//! Contribution-graph payload for the popover.
//!
//! Session parsing, dedup, and pricing are delegated to the vendored
//! `tokscale-core` crate (see `vendor/tokscale-core`), which covers every
//! supported agent and ships mature LiteLLM/OpenRouter pricing. This module
//! is now a thin adapter: it drives `tokscale-core`'s local graph report and
//! maps the resulting `GraphResult` back onto the camelCase JSON shape the
//! frontend already consumes (`src/lib/types.ts` `UsagePayload`).

use std::collections::BTreeMap;

use serde::Serialize;
use serde_json::Value;

const VERSION: &str = concat!("tokenbar-core/", env!("CARGO_PKG_VERSION"));

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TokenBreakdown {
    pub input: i64,
    pub output: i64,
    pub cache_read: i64,
    pub cache_write: i64,
    pub reasoning: i64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ClientContribution {
    client: String,
    model_id: String,
    provider_id: String,
    tokens: TokenBreakdown,
    cost: f64,
    messages: i32,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
struct DailyTotals {
    tokens: i64,
    cost: f64,
    messages: i32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct DailyContribution {
    date: String,
    totals: DailyTotals,
    intensity: u8,
    token_breakdown: TokenBreakdown,
    clients: Vec<ClientContribution>,
    turns_by_client: BTreeMap<String, i64>,
}

#[derive(Debug, Clone, Serialize)]
struct DateRange {
    start: String,
    end: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct YearSummary {
    year: String,
    total_tokens: i64,
    total_cost: f64,
    range: DateRange,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct DataSummary {
    total_tokens: i64,
    total_cost: f64,
    total_days: i32,
    active_days: i32,
    average_per_day: f64,
    max_cost_in_single_day: f64,
    clients: Vec<String>,
    models: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ExportMeta {
    generated_at: String,
    version: String,
    date_range: DateRange,
    pricing_mode: String,
    cost_coverage: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct TokenContributionData {
    meta: ExportMeta,
    summary: DataSummary,
    years: Vec<YearSummary>,
    contributions: Vec<DailyContribution>,
}

/// Build the contribution-graph payload for `year` (empty string = all time).
///
/// The existing path remains best-effort and retains its pricing fallback.
pub(crate) fn run(context: &crate::LocalSourceContext, year: &str) -> Result<Value, String> {
    let (graph, contract) = compute(context, year, false)?;
    to_payload(graph, contract)
}

/// Build a per-call local-first graph. The engine entry structurally bypasses
/// pricing resolution and therefore cannot invoke an outbound pricing loader.
pub(crate) fn run_local_first(
    context: &crate::LocalSourceContext,
    year: &str,
) -> Result<Value, String> {
    let (graph, contract) = compute(context, year, true)?;
    to_payload(graph, contract)
}

/// The engine's graph and its pricing/coverage contract for `year`, before
/// mapping (split out of [`run`] so a later caller can add remote records to
/// the graph). The contract travels beside the graph because the engine's
/// `GraphResultWithContract` has no public constructor to rebuild it from
/// parts once the graph has been edited.
pub(crate) fn compute(
    context: &crate::LocalSourceContext,
    year: &str,
    local_only: bool,
) -> Result<(tokscale_core::GraphResult, tokscale_core::GraphMetaContract), String> {
    let year = normalize_year(year)?;
    let options = context.report_options(year, None);

    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|e| format!("build runtime: {}", e))?;
    let report = if local_only {
        runtime.block_on(
            tokscale_core::generate_local_graph_report_local_only_with_source_context(
                context.resolved(),
                options,
            ),
        )?
    } else {
        runtime.block_on(
            tokscale_core::generate_local_graph_report_with_source_context(
                context.resolved(),
                options,
            ),
        )?
    };
    Ok(report.into_parts())
}

/// The payload of an engine graph: [`map_graph`], serialized.
pub(crate) fn to_payload(
    graph: tokscale_core::GraphResult,
    contract: tokscale_core::GraphMetaContract,
) -> Result<Value, String> {
    let payload = map_graph(graph, contract);
    serde_json::to_value(payload).map_err(|e| format!("serialize usage graph: {}", e))
}

pub(crate) fn normalize_year(year: &str) -> Result<Option<String>, String> {
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

/// Map `tokscale-core`'s `GraphResult` onto the frontend-facing payload.
/// Field renames: tokscale uses flat `date_range_start/end` and
/// `range_start/end`, the frontend expects nested `{ start, end }`. Extra
/// tokscale fields (`active_time_ms`, `time_metrics`, `processing_time_ms`)
/// are intentionally dropped. The reported `version` is branded as tokenbar.
fn map_graph(
    graph: tokscale_core::GraphResult,
    contract: tokscale_core::GraphMetaContract,
) -> TokenContributionData {
    TokenContributionData {
        meta: ExportMeta {
            generated_at: graph.meta.generated_at,
            version: VERSION.to_string(),
            date_range: DateRange {
                start: graph.meta.date_range_start,
                end: graph.meta.date_range_end,
            },
            pricing_mode: contract.pricing_mode.as_wire().to_string(),
            cost_coverage: contract.cost_coverage.as_wire().to_string(),
        },
        summary: DataSummary {
            total_tokens: graph.summary.total_tokens,
            total_cost: graph.summary.total_cost,
            total_days: graph.summary.total_days,
            active_days: graph.summary.active_days,
            average_per_day: graph.summary.average_per_day,
            max_cost_in_single_day: graph.summary.max_cost_in_single_day,
            clients: graph.summary.clients,
            models: graph.summary.models,
        },
        years: graph
            .years
            .into_iter()
            .map(|y| YearSummary {
                year: y.year,
                total_tokens: y.total_tokens,
                total_cost: y.total_cost,
                range: DateRange {
                    start: y.range_start,
                    end: y.range_end,
                },
            })
            .collect(),
        contributions: graph
            .contributions
            .into_iter()
            .map(map_contribution)
            .collect(),
    }
}

fn map_contribution(day: tokscale_core::DailyContribution) -> DailyContribution {
    DailyContribution {
        date: day.date,
        totals: DailyTotals {
            tokens: day.totals.tokens,
            cost: day.totals.cost,
            messages: day.totals.messages,
        },
        intensity: day.intensity,
        token_breakdown: map_breakdown(&day.token_breakdown),
        clients: day
            .clients
            .into_iter()
            .map(|c| ClientContribution {
                client: c.client,
                model_id: c.model_id,
                provider_id: c.provider_id,
                tokens: map_breakdown(&c.tokens),
                cost: c.cost,
                messages: c.messages,
            })
            .collect(),
        turns_by_client: day.turns_by_client,
    }
}

fn map_breakdown(breakdown: &tokscale_core::TokenBreakdown) -> TokenBreakdown {
    TokenBreakdown {
        input: breakdown.input,
        output: breakdown.output,
        cache_read: breakdown.cache_read,
        cache_write: breakdown.cache_write,
        reasoning: breakdown.reasoning,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn engine_day(
        date: &str,
        messages: i32,
        turns_by_client: BTreeMap<String, i64>,
    ) -> tokscale_core::DailyContribution {
        tokscale_core::DailyContribution {
            date: date.to_string(),
            totals: tokscale_core::DailyTotals {
                messages,
                ..Default::default()
            },
            intensity: 0,
            token_breakdown: tokscale_core::TokenBreakdown::default(),
            clients: Vec::new(),
            active_time_ms: None,
            turns_by_client,
        }
    }

    #[test]
    fn maps_graph_meta_to_exact_camel_case_mode_and_coverage() {
        let graph = tokscale_core::build_graph_result_from_messages(&[], None);
        let payload = map_graph(
            graph,
            tokscale_core::GraphMetaContract {
                pricing_mode: tokscale_core::GraphPricingMode::LocalOnly,
                cost_coverage: tokscale_core::CostCoverage::Partial,
            },
        );
        let wire = serde_json::to_value(payload).unwrap();
        assert_eq!(wire["meta"]["pricingMode"], "localOnly");
        assert_eq!(wire["meta"]["costCoverage"], "partial");
        assert!(wire["meta"].get("pricing_mode").is_none());
        assert!(wire["meta"].get("cost_coverage").is_none());
    }

    #[test]
    fn invalid_year_is_rejected_before_engine_scan() {
        let context = crate::LocalSourceContext::capture(
            Some(std::env::temp_dir().join(format!(
                "tokenbar-usage-graph-invalid-year-{}",
                std::process::id()
            ))),
            false,
            tokscale_core::ScannerSettings::default(),
        )
        .unwrap();
        let error = run_local_first(&context, "26").unwrap_err();
        assert!(error.contains("invalid year filter"));
    }

    #[test]
    fn maps_daily_turns_by_exact_client_and_keeps_message_only_days_empty() {
        let mapped = map_contribution(engine_day(
            "2026-08-08",
            4,
            BTreeMap::from([("cc-mirror/foo".to_string(), 1), ("claude".to_string(), 2)]),
        ));

        assert_eq!(mapped.turns_by_client.get("cc-mirror/foo"), Some(&1));
        assert_eq!(mapped.turns_by_client.get("claude"), Some(&2));
        assert_eq!(mapped.turns_by_client.values().sum::<i64>(), 3);
        let wire = serde_json::to_value(&mapped).unwrap();
        assert_eq!(wire["turnsByClient"]["cc-mirror/foo"], 1);
        assert!(wire.get("turns_by_client").is_none());

        let message_only = map_contribution(engine_day("2026-08-09", 5, BTreeMap::new()));
        assert_eq!(message_only.totals.messages, 5);
        assert!(message_only.turns_by_client.is_empty());
        assert_eq!(
            serde_json::to_value(message_only).unwrap()["turnsByClient"],
            serde_json::json!({})
        );
    }
}
