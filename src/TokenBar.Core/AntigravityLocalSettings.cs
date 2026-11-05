using System.Text.Json;

namespace TokenBar.Core;

/// <summary>
/// Where Antigravity's machine-bound settings live: a store under
/// <c>%LOCALAPPDATA%</c>, never the roaming <c>settings.json</c> (slice W item
/// 13, MA-19). The captured accounts' credentials are
/// <c>CRED_PERSIST_LOCAL_MACHINE</c>, so the list naming them, the removed
/// keys, agy's current binding and the automatic-capture toggle (its answer is
/// the consent) belong to this PC too.
/// <para>
/// One-time move, per machine (maintainer decision): an account moves only
/// when its Credential Manager entry exists on this PC; the binding only when
/// its key moved; removed keys as they are (they only suppress); the toggle as
/// on only when at least one account moved, otherwise it stays unanswered
/// (absent, off). Every moved key and <see cref="MigratedKey"/> are written in
/// one atomic save (<see cref="SettingsStore.SetMany"/>), before the caller
/// builds anything that reads them. "Already moved" means the marker. The
/// roaming keys are left where they are for one release (an older build still
/// reads them) and are never read again once the marker is written.
/// </para>
/// <para>
/// A probe that fails or runs past <see cref="ProbeTimeout"/> stops the move:
/// nothing is written, this session gets an empty in-memory store (no
/// account, toggle off, no roaming value), and the next launch tries again
/// (security review S15).
/// </para>
/// </summary>
public static class AntigravityLocalSettings
{
    /// <summary>The move's done marker (a bool, true) in the local store.</summary>
    public const string MigratedKey = "tokenbar.antigravity.movedFromRoaming";

    /// <summary>The Credential Manager probe's answers
    /// (<c>tb_antigravity_account_presence</c>).</summary>
    public const string Present = "present";
    public const string Absent = "absent";

    /// <summary><c>%LOCALAPPDATA%\TokenBar\antigravity.json</c>, next to
    /// <see cref="CursorProfileStore.DefaultPath"/>.</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenBar", "antigravity.json");

    /// <summary>How long all the probes together may take. Each is one
    /// CredReadW and normally takes milliseconds; the launch waits for them
    /// (once per machine), so a stalled Credential Manager is bounded here. A
    /// timed-out probe keeps its thread-pool thread until it returns.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The store Antigravity reads and writes this session.
    /// <paramref name="presence"/> answers <see cref="Present"/>,
    /// <see cref="Absent"/> or anything else (failed) for one account key; it
    /// runs on the thread pool, never on the caller's thread.
    /// <paramref name="roaming"/> is only read.</summary>
    public static SettingsStore Open(
        string path,
        SettingsStore roaming,
        Func<string, string> presence,
        Action<string> log,
        TimeSpan? probeTimeout = null)
    {
        var existed = File.Exists(path);
        var local = new SettingsStore(path, log);
        if (local.GetBool(MigratedKey, false))
        {
            return local;
        }

        if (local.LoadFailed)
        {
            // The file is there but could not be read: this store is empty and
            // never writes, so nothing is lost and the next launch reads it.
            log("antigravity local settings unreadable; Antigravity accounts are empty this session");
            return local;
        }

        if (existed)
        {
            // There was a file without the marker: SettingsStore quarantined
            // it as corrupt (every save of ours carries the marker). Once a
            // move has happened the roaming keys are never read again, so
            // start empty here.
            log("antigravity local settings were unreadable and set aside; starting empty");
            local.SetMany(new Dictionary<string, JsonElement> { [MigratedKey] = JsonSerializer.SerializeToElement(true) });
            return local;
        }

        var moved = Migrate(roaming, presence, log, probeTimeout ?? ProbeTimeout);
        if (moved is null)
        {
            return SettingsStore.InMemory(log);
        }

        local.SetMany(moved);
        return local;
    }

    /// <summary>Every value the move writes, the marker included; null when a
    /// probe failed, threw or timed out.</summary>
    public static Dictionary<string, JsonElement>? Migrate(
        SettingsStore roaming, Func<string, string> presence, Action<string> log, TimeSpan timeout)
    {
        // Read exactly as today's readers read them: an unreadable value is
        // what they already treat it as (no accounts, no removed key, no
        // binding, toggle off).
        var accounts = AntigravityAccounts.Decode(roaming.GetString(AntigravityAccounts.Key));
        var probes = Task.Run(() => accounts.Select(a => presence(a.Key)).ToList());
        List<string> answers;
        try
        {
            if (!probes.Wait(timeout))
            {
                log("antigravity settings move: credential probe timed out; retrying at next launch");
                _ = probes.ContinueWith(
                    static done => _ = done.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return null;
            }

            answers = probes.Result;
        }
        catch (AggregateException ex)
        {
            log($"antigravity settings move: credential probe failed: {ex.InnerException?.GetType().Name}; retrying at next launch");
            return null;
        }

        if (answers.Any(a => a is not (Present or Absent)))
        {
            log("antigravity settings move: credential probe failed; retrying at next launch");
            return null;
        }

        var here = accounts.Where((_, i) => answers[i] == Present).ToList();
        var values = new Dictionary<string, JsonElement>
        {
            [MigratedKey] = JsonSerializer.SerializeToElement(true),
        };
        if (roaming.GetString(AntigravityAccounts.RemovedKeysKey) is { } removed)
        {
            values[AntigravityAccounts.RemovedKeysKey] = JsonSerializer.SerializeToElement(removed);
        }

        if (here.Count == 0)
        {
            return values;
        }

        values[AntigravityAccounts.Key] = JsonSerializer.SerializeToElement(AntigravityAccounts.PayloadJson(here));
        if (roaming.GetBool(AntigravityAutoCapture.EnabledKey, false))
        {
            values[AntigravityAutoCapture.EnabledKey] = JsonSerializer.SerializeToElement(true);
        }

        if (roaming.GetString(AntigravityAutoCapture.CurrentKey) is { } binding
            && BindingKey(binding) is { } key
            && here.Any(a => a.Key == key))
        {
            values[AntigravityAutoCapture.CurrentKey] = JsonSerializer.SerializeToElement(binding);
        }

        return values;
    }

    /// <summary>The key of a stored <c>{"key","marker"}</c> binding, read as
    /// <see cref="AntigravityAutoCapture"/> reads it; null when it would not
    /// load.</summary>
    private static string? BindingKey(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(raw) is { } stored
                && stored.TryGetValue("key", out var key)
                && stored.TryGetValue("marker", out var marker)
                && !string.IsNullOrEmpty(key)
                && !string.IsNullOrEmpty(marker)
                    ? key
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
