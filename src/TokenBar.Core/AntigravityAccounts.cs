using System.Text.Json;
using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Captured Antigravity accounts: extra Google logins copied from agy, each
/// fetched as its own Antigravity card after the primary. Port of macOS
/// <c>AntigravityAccounts.swift</c> (945dbcc2).
/// <para>
/// Settings holds only <c>{key, label}</c> per account. The credential lives in
/// a Credential Manager entry the core owns (<c>tb_antigravity_capture</c>);
/// the key is a hash of the Google account id and is never shown
/// (<see cref="AccountLabel.AntigravityLabel"/> resolves it to the label).
/// The core registry is in memory and empty at every launch, so the list is
/// installed before every agent-usage fetch (<see cref="AntigravityFetch"/>;
/// a no-op unless it changed) and after every change (<see cref="Mutate"/>).
/// </para>
/// </summary>
public static class AntigravityAccounts
{
    /// <summary>Same key and shape as macOS: a string holding
    /// <c>[{"key","label"}]</c>, which is also the setter's payload.</summary>
    public const string Key = "tokenbar.antigravity.accounts";

    /// <summary>Keys (hashes only) removed while automatic capture was on, as
    /// a JSON array string. Automatic capture skips them before any request;
    /// only a manual Capture of that account takes a key off.</summary>
    public const string RemovedKeysKey = "tokenbar.antigravity.removedKeys";

    public const string EmptyPayload = "[]";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<AntigravityAccount> Load(SettingsStore store) => Decode(store.GetString(Key));

    /// <summary>The stored value as a list; empty when absent or unreadable.</summary>
    public static IReadOnlyList<AntigravityAccount> Decode(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AntigravityAccount?>>(raw, Json)?
                .Where(a => a is { Key.Length: > 0, Label: not null })
                .Select(a => a!)
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The setter payload: <c>[{"key":…,"label":…}]</c>, keys in
    /// sorted order (macOS <c>.sortedKeys</c>).</summary>
    public static string PayloadJson(IReadOnlyList<AntigravityAccount> accounts) =>
        JsonSerializer.Serialize(accounts, Json);

    public static void Save(SettingsStore store, IReadOnlyList<AntigravityAccount> accounts) =>
        store.SetString(Key, PayloadJson(accounts));

    public static string? Label(SettingsStore store, string key) =>
        Load(store).FirstOrDefault(a => a.Key == key)?.Label;

    /// <summary>The account's 1-based position in the stored list; null when
    /// the key is not listed.</summary>
    public static int? Ordinal(SettingsStore store, string key) =>
        Load(store).Select((a, i) => (a.Key, N: i + 1)).FirstOrDefault(t => t.Key == key).N is var n and > 0 ? n : null;

    public static IReadOnlyList<string> RemovedKeys(SettingsStore store)
    {
        var raw = store.GetString(RemovedKeysKey);
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string?>>(raw)?.OfType<string>().ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void SaveRemovedKeys(SettingsStore store, IReadOnlyList<string> keys) =>
        store.SetString(RemovedKeysKey, JsonSerializer.Serialize(keys));

    /// <summary><paramref name="accounts"/> with <paramref name="captured"/>
    /// added, or its label refreshed when the same account was captured
    /// before.</summary>
    public static IReadOnlyList<AntigravityAccount> Adding(
        AntigravityAccount captured, IReadOnlyList<AntigravityAccount> accounts)
    {
        var index = accounts.ToList().FindIndex(a => a.Key == captured.Key);
        if (index < 0)
        {
            return [.. accounts, captured];
        }

        var updated = accounts.ToList();
        updated[index] = captured;
        return updated;
    }

    private static readonly object MutateGate = new();

    /// <summary>The one path every list change goes through: re-read the
    /// store, apply <paramref name="change"/>, save and install, under one
    /// lock, so two changes cannot interleave and none starts from a stale
    /// copy (a capture that finished after a remove).</summary>
    public static void Mutate(
        SettingsStore store,
        Action install,
        Func<IReadOnlyList<AntigravityAccount>, IReadOnlyList<AntigravityAccount>> change)
    {
        lock (MutateGate)
        {
            Save(store, change(Load(store)));
            install();
        }
    }

    /// <summary>The live installer for this process; set once at launch.</summary>
    public static AntigravityAccountsInstaller? Installer { get; set; }

    /// <summary>Raised when the cards should follow a change: a changed list
    /// reached the core, or agy's current account changed (dedup). The
    /// dashboard refetches its quota on it. Raised on the changing thread;
    /// handlers must not block.</summary>
    public static event Action? Changed;

    internal static void RaiseChanged() => Changed?.Invoke();
}

/// <summary>
/// Installs the stored list in the core when it differs from what this process
/// installed last (macOS <c>AntigravityAccounts.apply</c>). Serialized by one
/// lock and reading the store when it runs, so the last saved list wins. The
/// core starts empty, so "nothing installed yet" compares as <c>[]</c>: an
/// empty list costs no call. Only a list the core accepted is remembered, so a
/// failed install is retried by the next call.
/// </summary>
public sealed class AntigravityAccountsInstaller(
    Func<string> payload,
    Func<string, RootsResult> set,
    Action<string> log)
{
    private readonly object _gate = new();
    private string? _installed;

    /// <summary>Install after a list change (<see cref="AntigravityAccounts.Mutate"/>:
    /// a user action, or a capture that can land during an in-flight fetch),
    /// raising <see cref="AntigravityAccounts.Changed"/> when the core took a
    /// new list, so the pollers (and an in-flight shared fetch) follow.</summary>
    public void Install()
    {
        if (TryInstall())
        {
            AntigravityAccounts.RaiseChanged();
        }
    }

    /// <summary>Install from the shared fetch itself, before its
    /// <c>tb_agent_usage</c> call: raises nothing, because the fetch that is
    /// about to run already reflects this install (raising would owe that
    /// same fetch a needless second <c>tb_agent_usage</c> at every launch with
    /// a stored account, and after every retried failed install).</summary>
    public void InstallForFetch() => TryInstall();

    private bool TryInstall()
    {
        lock (_gate)
        {
            var next = payload();
            if (next == (_installed ?? AntigravityAccounts.EmptyPayload))
            {
                _installed = next;
                return false;
            }

            try
            {
                set(next);
            }
            catch (Exception ex)
            {
                // Type only: nothing from the payload (an email) is logged.
                log($"antigravityAccounts install failed: {ex.GetType().Name}");
                return false;
            }

            _installed = next;
            return true;
        }
    }
}

/// <summary>
/// What the shared agent-usage fetch does around <c>tb_agent_usage</c>, the
/// one funnel both quota consumers (tray and dashboard) read through: install
/// the stored list first, so the first fetch of a launch already carries the
/// captured cards; when automatic capture is on, run its pre-fetch step (the
/// capture attempt it starts is not awaited, so the fetch never waits on
/// Google); then apply <see cref="AntigravityDedup"/> to the result.
/// </summary>
public static class AntigravityFetch
{
    public static AgentUsagePayload Run(
        Func<AgentUsagePayload> fetch,
        AntigravityAccountsInstaller? installer,
        AntigravityAutoCapture? capture)
    {
        installer?.InstallForFetch();
        if (capture is { IsEnabled: true })
        {
            _ = capture.PrepareForFetch().GetAwaiter().GetResult();
        }

        var payload = fetch();
        if (capture is null)
        {
            return payload;
        }

        var (key, marker) = capture.Current;
        return AntigravityDedup.Apply(payload, key, marker);
    }
}

/// <summary>
/// One card for agy's current account (macOS <c>AntigravityDedup</c>). Once
/// agy's current account is known, it is also a captured account and would be
/// drawn twice. This drops the captured card and labels the primary with its
/// email ONLY when all hold: <c>currentKey</c> set; its marker set and not
/// <c>"present"</c>; the primary Antigravity snapshot (no account key) came
/// from the agy route, was fetched under that same marker and has no error; a
/// captured snapshot carries that key. Otherwise both are shown.
/// <para>
/// The primary keeps its own windows and values, and takes the captured
/// plan when it has none. When the captured snapshot
/// has no error and has windows, the merged primary adopts its pace status,
/// historical pace and duration per matching card id and records it as
/// <see cref="AgentUsageSnapshot.HistoryAccountKey"/>, so every stored-series
/// read for the card uses the captured account's scope
/// (<see cref="AgentUsageSnapshot.HistoryReadScope"/>). Idempotent: a merged
/// payload has no captured card left to merge.
/// </para>
/// </summary>
public static class AntigravityDedup
{
    public const string ClientId = AccountLabel.AntigravityClientId;

    public static AgentUsagePayload Apply(AgentUsagePayload payload, string? currentKey, string? currentMarker)
    {
        if (currentKey is null || currentMarker is null || currentMarker == "present")
        {
            return payload;
        }

        var agents = payload.Agents.ToList();
        var primaryIndex = agents.FindIndex(a => a.ClientId == ClientId && a.Account.AccountKey is null);
        if (primaryIndex < 0)
        {
            return payload;
        }

        var primary = agents[primaryIndex];
        if (primary.Source != "agy" || primary.AgyLoginMarker != currentMarker || primary.Error is not null)
        {
            return payload;
        }

        var capturedIndex = agents.FindIndex(a => a.ClientId == ClientId && a.AccountKey == currentKey);
        if (capturedIndex < 0)
        {
            return payload;
        }

        var captured = agents[capturedIndex];
        var merged = primary;
        // Email and plan are chosen independently. The agy route carries no
        // plan; the captured snapshot is the same account (marker-bound
        // above), so its plan labels the primary too, with or without an email.
        var email = captured.Identity?.Email ?? primary.Identity?.Email;
        var plan = primary.Identity?.Plan ?? captured.Identity?.Plan;
        if (captured.Identity is not null && (email is not null || plan is not null))
        {
            merged = merged with { Identity = new AgentIdentity(email, plan) };
        }

        agents[primaryIndex] = AdoptingHistory(merged, captured);
        agents.RemoveAt(capturedIndex);
        return payload with { Agents = agents };
    }

    /// <summary><paramref name="primary"/> with <paramref name="captured"/>'s
    /// pace per matching card id and its history identity, only when the
    /// captured snapshot has no error and offers windows; otherwise
    /// unchanged. A window <paramref name="captured"/> lacks keeps its own
    /// pace.</summary>
    public static AgentUsageSnapshot AdoptingHistory(AgentUsageSnapshot primary, AgentUsageSnapshot captured)
    {
        if (captured.Error is not null || captured.AccountKey is not { } key || captured.Windows.Count == 0)
        {
            return primary;
        }

        var theirs = captured.RawCardWindows;
        List<UsageWindow> windows = [.. primary.Windows.Select(window =>
            theirs.FirstOrDefault(t => t.CardId == window.CardId) is { } other
                ? ReplacingPace(window, other)
                : window)];
        return primary with { Windows = windows, HistoryAccountKey = key, HistoryAccountScope = captured.HistoryScope };
    }

    /// <summary><paramref name="mine"/> with <paramref name="other"/>'s pace
    /// status, historical pace and the duration they describe; usage, reset
    /// and label kept (macOS <c>replacingPace(from:)</c>). Only when this
    /// window's own duration is absent (the agy primary's, cleared by the
    /// engine's <c>accountScope</c> mark) or equal: a different duration is a
    /// different cycle whose pace must not be borrowed. The copied tuple
    /// passed the wire validation on <paramref name="other"/>; the
    /// constructor re-checks duration against pace, and a refusal keeps
    /// <paramref name="mine"/>, never throws.</summary>
    public static UsageWindow ReplacingPace(UsageWindow mine, UsageWindow other)
    {
        if ((mine.DurationSeconds is not null && mine.DurationSeconds != other.DurationSeconds)
            || other.DurationSeconds != other.PaceStatus.DurationSeconds
            || string.IsNullOrWhiteSpace(mine.CardId))
        {
            return mine;
        }

        try
        {
            return new UsageWindow(
                mine.Label, mine.UsedPercent, mine.RemainingPercent, mine.ResetsAt, mine.ResetText,
                other.WindowMinutes, mine.CardId, other.PaceStatus, other.HistoricalPace,
                other.DurationSeconds, mine.ModelScope);
        }
        catch (ArgumentException)
        {
            return mine;
        }
    }
}
