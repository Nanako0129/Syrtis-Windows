using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TokenBar.Core;
using TokenBar.Interop;

// C# side of the Swift↔C# fixture cross-check. Contract: crosscheck/README.md.
// Reads selected fixture JSON and writes the corresponding *.actual.json.

// The today*/relativeTime cases are local-tz dependent; the contract pins the
// run to Asia/Taipei so both harnesses agree.
if (TimeZoneInfo.Local.Id is not ("Asia/Taipei" or "Taipei Standard Time"))
{
    Console.Error.WriteLine($"TZ must be Asia/Taipei (got '{TimeZoneInfo.Local.Id}') — run with TZ=Asia/Taipei");
    return 1;
}

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("usage: TokenBar.CrossCheck <fixtures-dir> <out-dir> [format|usage-pace|provider-quota-pace-v3]");
    return 2;
}

var fixturesDir = args[0];
var outDir = args[1];
var selector = args.Length == 3 ? args[2] : "all";
Directory.CreateDirectory(outDir);

// Same options TbCore uses for the wire: Rust serde camelCase and strict
// required/nullable constructor semantics.
var wire = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true,
    Converters =
    {
        new JsonStringEnumConverter<PricingMode>(allowIntegerValues: false),
        new JsonStringEnumConverter<CostCoverage>(allowIntegerValues: false),
    },
};
var outOpts = new JsonSerializerOptions { WriteIndented = true };

switch (selector)
{
    case "all":
        WriteResults("usage-pace", RunUsagePace(Load("usage-pace")));
        WriteResults("format", RunFormat(Load("format")));
        break;
    case "usage-pace":
        WriteResults("usage-pace", RunUsagePace(Load("usage-pace")));
        break;
    case "format":
        WriteResults("format", RunFormat(Load("format")));
        break;
    case "provider-quota-pace-v3":
        RunProviderQuotaPaceV3();
        break;
    default:
        Console.Error.WriteLine($"error: unknown selector {selector}");
        return 2;
}

return 0;

JsonDocument Load(string name) =>
    JsonDocument.Parse(File.ReadAllText(Path.Combine(fixturesDir, name + ".json")));

void WriteResults(string name, object results) =>
    File.WriteAllText(
        Path.Combine(outDir, name + ".actual.json"),
        JsonSerializer.Serialize(results, outOpts));

// RFC3339 `now` — harness-side parse, strict invariant.
static DateTimeOffset ParseNow(JsonElement c) =>
    TryParseNow(c.GetProperty("now").GetString(), out var now)
        ? now
        : throw new FormatException("now must be RFC3339");

static bool TryParseNow(string? raw, out DateTimeOffset now)
{
    const string pattern =
        "^(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})" +
        "(?:\\.(?<fraction>[0-9]+))?(?<offset>[Zz]|[+-][0-9]{2}:[0-9]{2})$";
    var match = Regex.Match(raw ?? "", pattern, RegexOptions.CultureInvariant);
    if (!match.Success)
    {
        now = default;
        return false;
    }

    if (!DateTime.TryParseExact(
            match.Groups["date"].Value,
            "yyyy-MM-dd'T'HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var local))
    {
        now = default;
        return false;
    }

    var fraction = match.Groups["fraction"].Value;
    // Foundation keeps millisecond precision through the canonical 1-8 digit
    // fraction range; longer, undocumented parser quirks are out of contract.
    var milliseconds = int.Parse(
        (fraction.Length > 3 ? fraction[..3] : fraction).PadRight(3, '0'),
        CultureInfo.InvariantCulture);
    local = local.AddMilliseconds(milliseconds);

    var offset = match.Groups["offset"].Value;
    var offsetMinutes = 0;
    if (offset is not ("Z" or "z"))
    {
        // Foundation accepts valid two-digit offsets beyond DateTimeOffset's
        // +/-14:00 limit and treats out-of-range fields as a zero offset.
        var offsetHours = (offset[1] - '0') * 10 + offset[2] - '0';
        var offsetMinutePart = (offset[4] - '0') * 10 + offset[5] - '0';
        if (offsetHours <= 23 && offsetMinutePart <= 59)
        {
            offsetMinutes = offsetHours * 60 + offsetMinutePart;
            if (offset[0] == '-')
            {
                offsetMinutes = -offsetMinutes;
            }
        }
    }

    var utcTicks = local.Ticks - offsetMinutes * TimeSpan.TicksPerMinute;
    if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
    {
        now = default;
        return false;
    }

    now = new DateTimeOffset(utcTicks, TimeSpan.Zero);
    return true;
}

static string StageName(PaceStage stage) => stage switch
{
    PaceStage.OnTrack => "onTrack",
    PaceStage.SlightlyAhead => "slightlyAhead",
    PaceStage.Ahead => "ahead",
    PaceStage.FarAhead => "farAhead",
    PaceStage.SlightlyBehind => "slightlyBehind",
    PaceStage.Behind => "behind",
    PaceStage.FarBehind => "farBehind",
    _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
};

static string PaceStateName(UsagePaceState state) => state switch
{
    UsagePaceState.LearningDuration => "learningDuration",
    UsagePaceState.LearningHistory => "learningHistory",
    UsagePaceState.Available => "available",
    UsagePaceState.Unavailable => "unavailable",
    UsagePaceState.LegacyMissing => "legacyMissing",
    _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
};

static string? PaceReasonName(UsagePaceUnavailableReason? reason) => reason switch
{
    null => null,
    UsagePaceUnavailableReason.WindowIdentity => "windowIdentity",
    UsagePaceUnavailableReason.MissingReset => "missingReset",
    UsagePaceUnavailableReason.InvalidEvidence => "invalidEvidence",
    UsagePaceUnavailableReason.AccountScope => "accountScope",
    UsagePaceUnavailableReason.StoreCapacity => "storeCapacity",
    UsagePaceUnavailableReason.History => "history",
    UsagePaceUnavailableReason.NonRecurring => "nonRecurring",
    UsagePaceUnavailableReason.AccountFetchFailed => "accountFetchFailed",
    _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
};

static string? DurationSourceName(UsagePaceDurationSource? source) => source switch
{
    null => null,
    UsagePaceDurationSource.Provider => "provider",
    UsagePaceDurationSource.Contract => "contract",
    UsagePaceDurationSource.Observed => "observed",
    _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
};

static string BasisName(UsagePaceBasis basis) => basis switch
{
    UsagePaceBasis.Linear => "linear",
    UsagePaceBasis.Historical => "historical",
    _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, null),
};

static PaceMode? ParsePaceMode(string? mode) => mode switch
{
    "linear" => PaceMode.Linear,
    "historical" => PaceMode.Historical,
    "off" => PaceMode.Off,
    _ => null,
};

static bool TryDecodeWindow(string raw, JsonSerializerOptions wire, out UsageWindow window)
{
    try
    {
        window = JsonSerializer.Deserialize<UsageWindow>(raw, wire)
            ?? throw new JsonException("usage window decoded to null");
        return true;
    }
    catch (JsonException)
    {
        window = null!;
        return false;
    }
}

Dictionary<string, object?> RunUsagePace(JsonDocument doc)
{
    var results = new Dictionary<string, object?>();
    foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
    {
        var name = c.GetProperty("name").GetString()!;
        if (!TryDecodeWindow(c.GetProperty("window").GetRawText(), wire, out var window))
        {
            results[name] = new Dictionary<string, object?> { ["rejected"] = true };
            continue;
        }

        switch (c.GetProperty("kind").GetString())
        {
            case "runOutRisk":
                results[name] = UsagePace.RunOutRiskLabel(window);
                break;
            case "compute":
                var mode = c.GetProperty("mode").GetString() switch
                {
                    "linear" => PaceMode.Linear,
                    "historical" => PaceMode.Historical,
                    "off" => PaceMode.Off,
                    var m => throw new InvalidOperationException($"{name}: unknown mode '{m}'"),
                };
                var pace = UsagePace.Compute(window, mode, ParseNow(c));
                results[name] = pace is null ? null : new Dictionary<string, object?>
                {
                    ["stage"] = StageName(pace.Stage),
                    ["deltaPercent"] = pace.DeltaPercent,
                    ["expectedUsedPercent"] = pace.ExpectedUsedPercent,
                    ["actualUsedPercent"] = pace.ActualUsedPercent,
                    ["etaSeconds"] = pace.EtaSeconds,
                    ["willLastToReset"] = pace.WillLastToReset,
                    ["label"] = pace.Label,
                    ["etaText"] = pace.EtaText,
                };
                break;
            case var k:
                throw new InvalidOperationException($"{name}: unknown kind '{k}'");
        }
    }

    return results;
}

Dictionary<string, object?> RunFormat(JsonDocument doc)
{
    var graph = doc.RootElement.GetProperty("graph").Deserialize<UsagePayload>(wire)!;
    var results = new Dictionary<string, object?>();
    foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
    {
        var name = c.GetProperty("name").GetString()!;
        // today* take a local wall-clock DateTime in production; mirror that.
        DateTime LocalNow() => ParseNow(c).ToLocalTime().DateTime;
        results[name] = c.GetProperty("fn").GetString() switch
        {
            "compactTokens" => Format.CompactTokens(c.GetProperty("arg").GetInt64()),
            "exactTokens" => Format.ExactTokens(c.GetProperty("arg").GetInt64()),
            "usd" => Format.Usd(c.GetProperty("arg").GetDouble()),
            "monthDay" => Format.MonthDay(c.GetProperty("arg").GetString()!),
            "mmdd" => Format.Mmdd(c.GetProperty("arg").GetString()!),
            "relativeTime" => Format.RelativeTime(c.GetProperty("arg").GetUInt64(), ParseNow(c)),
            "todayKey" => Format.TodayKey(LocalNow()),
            "todayTokens" => Format.TodayTokens(graph, LocalNow()),
            "todayCost" => Format.TodayCost(graph, LocalNow()),
            "paceDurationText" => UsagePace.DurationText(c.GetProperty("arg").GetDouble()),
            var fn => throw new InvalidOperationException($"{name}: unknown fn '{fn}'"),
        };
    }

    return results;
}

void RunProviderQuotaPaceV3()
{
    var file = JsonSerializer.Deserialize<ProviderQuotaPaceFile>(
        File.ReadAllText(Path.Combine(fixturesDir, "provider-quota-pace-v3.json")),
        wire) ?? throw new JsonException("provider-quota-pace-v3 decoded to null");
    if (file.SchemaVersion != 3)
    {
        throw new InvalidOperationException("provider-quota-pace-v3 schemaVersion must be 3");
    }
    if (file.Cases.Any(static item => item is null))
    {
        throw new JsonException("provider-quota-pace-v3 cases cannot contain null");
    }

    var payload = file.Payload;
    var casesOut = new Dictionary<string, object?>();

    foreach (var c in file.Cases)
    {
        var name = c.Name;
        switch (c.Kind)
        {
            case "pace":
            {
                var mode = ParsePaceMode(c.Mode);
                AgentUsageSnapshot? agent = c.ClientId is null
                    ? null
                    : payload.Agents.FirstOrDefault(a => a.ClientId == c.ClientId);
                var window = agent is null || c.CardId is null
                    ? null
                    : agent.UniqueCardWindows.FirstOrDefault(w => w.CardId == c.CardId);

                if (c.ClientId is null || c.CardId is null || mode is null || window is null ||
                    !TryParseNow(c.Now, out var now))
                {
                    casesOut[name] = new Dictionary<string, object?>
                    {
                        ["error"] = "invalid pace case metadata",
                    };
                    continue;
                }

                var pace = UsagePace.Compute(window, mode.Value, now);
                casesOut[name] = pace is null
                    ? null
                    : PaceOutput(window, mode.Value, pace);
                break;
            }
            case "selection":
            {
                var selection = c.Selection;
                if (selection is null)
                {
                    casesOut[name] = new Dictionary<string, object?>
                    {
                        ["error"] = "selection case needs selection",
                    };
                    continue;
                }

                var canonical = QuotaResolver.CanonicalSelection(payload, selection);
                var resolved = QuotaResolver.Resolve(payload, selection);
                casesOut[name] = new Dictionary<string, object?>
                {
                    ["selection"] = selection,
                    ["canonicalSelection"] = canonical,
                    ["resolvedClientId"] = resolved?.ClientId,
                    ["resolvedCardId"] = resolved?.Window.CardId,
                };
                break;
            }
            case "legacy":
            {
                var raw = c.RawWindow;
                if (raw is null || !TryParseNow(c.Now, out var now))
                {
                    casesOut[name] = new Dictionary<string, object?>
                    {
                        ["error"] = "legacy case needs rawWindow and now",
                    };
                    continue;
                }

                if (!TryDecodeWindow(raw, wire, out var window))
                {
                    casesOut[name] = new Dictionary<string, object?> { ["rejected"] = true };
                    continue;
                }

                casesOut[name] = new Dictionary<string, object?>
                {
                    ["rejected"] = false,
                    ["state"] = PaceStateName(window.PaceStatus.State),
                    ["reason"] = PaceReasonName(window.PaceStatus.Reason),
                    ["durationSeconds"] = window.PaceStatus.DurationSeconds,
                    ["durationSource"] = DurationSourceName(window.PaceStatus.DurationSource),
                    ["completeCycles"] = window.PaceStatus.CompleteCycles,
                    ["windowMinutes"] = window.WindowMinutes,
                    ["historicalPace"] = UsagePace.Compute(window, PaceMode.Historical, now) is not null,
                    ["linearPace"] = UsagePace.Compute(window, PaceMode.Linear, now) is not null,
                };
                break;
            }
            case "malformed":
            {
                var raw = c.RawWindow;
                casesOut[name] = raw is null
                    ? new Dictionary<string, object?>
                    {
                        ["error"] = "malformed case needs rawWindow",
                    }
                    : new Dictionary<string, object?>
                    {
                        ["rejected"] = !TryDecodeWindow(raw, wire, out _),
                    };
                break;
            }
            case var kind:
                casesOut[name] = new Dictionary<string, object?>
                {
                    ["error"] = $"unknown kind {kind}",
                };
                break;
        }
    }

    WriteResults(
        "provider-quota-pace-v3",
        new Dictionary<string, object?>
        {
            ["schemaVersion"] = file.SchemaVersion,
            ["lifecycle"] = LifecycleRows(payload),
            ["cases"] = casesOut,
        });
}

static List<Dictionary<string, object?>> LifecycleRows(AgentUsagePayload payload)
{
    var rows = new List<Dictionary<string, object?>>();
    foreach (var agent in payload.Agents)
    {
        foreach (var window in agent.Windows)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["clientId"] = agent.ClientId,
                ["cardId"] = window.CardId,
                ["label"] = window.Label,
                ["state"] = PaceStateName(window.PaceStatus.State),
                ["reason"] = PaceReasonName(window.PaceStatus.Reason),
                ["durationSeconds"] = window.PaceStatus.DurationSeconds,
                ["durationSource"] = DurationSourceName(window.PaceStatus.DurationSource),
                ["completeCycles"] = window.PaceStatus.CompleteCycles,
                ["hasHistorical"] = window.HistoricalPace is not null,
            });
        }
    }

    return rows;
}

static Dictionary<string, object?> PaceOutput(
    UsageWindow window,
    PaceMode mode,
    UsagePace pace)
{
    var presentation = UsagePace.Presentation(window, mode, pace);
    return new Dictionary<string, object?>
    {
        ["basis"] = BasisName(pace.Basis),
        ["stage"] = StageName(pace.Stage),
        ["deltaPercent"] = pace.DeltaPercent,
        ["expectedUsedPercent"] = pace.ExpectedUsedPercent,
        ["actualUsedPercent"] = pace.ActualUsedPercent,
        ["etaSeconds"] = pace.EtaSeconds,
        ["willLastToReset"] = pace.WillLastToReset,
        ["label"] = pace.Label,
        ["etaText"] = presentation.EtaText,
        ["riskText"] = presentation.RiskText,
        ["isHistoricalDeficit"] = pace.IsHistoricalDeficit,
    };
}

sealed record ProviderQuotaPaceFile(
    int SchemaVersion,
    AgentUsagePayload Payload,
    IReadOnlyList<ProviderQuotaPaceCase> Cases);

sealed record ProviderQuotaPaceCase(
    string Name,
    string Kind,
    string? ClientId = null,
    string? CardId = null,
    string? Mode = null,
    string? Now = null,
    string? Selection = null,
    string? RawWindow = null);
