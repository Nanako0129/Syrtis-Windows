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
/// reads them) and are read again only for a re-move (below); a store that
/// carries the marker never reads them.
/// </para>
/// <para>
/// A probe that fails or runs past <see cref="ProbeTimeout"/> stops the move:
/// nothing is written, this session gets an empty in-memory store (no
/// account, toggle off, no roaming value), and the next launch tries again
/// (security review S15). While the store does not persist,
/// <see cref="AntigravityAutoCapture"/> refuses Capture and turning automatic
/// capture on (review F1).
/// </para>
/// <para>
/// A local file without the marker (a corrupt one SettingsStore set aside,
/// <c>null</c>, <c>{}</c>) has not been moved to, whatever else it holds: the
/// move runs again (review F2), the probe still gating every account. That
/// re-move never carries the roaming toggle (review D1): the user may have
/// turned automatic capture off on this PC since the first move, and the
/// roaming value is stale, so the toggle stays off and unanswered. Only a
/// first move (no local file, and no quarantined one, has ever been there:
/// review R2) carries it.
/// </para>
/// <para>
/// A move whose save fails ends like a failed probe (review R1): this session
/// gets an empty in-memory store, no roaming value is used, and the next
/// launch tries again.
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
        // A corrupt file is moved to <path>.corrupt by the SettingsStore
        // constructor; it still proves a local file existed (review R2).
        var existed = File.Exists(path) || File.Exists(path + SettingsStore.QuarantineSuffix);
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

        if (roaming.LoadFailed)
        {
            // The roaming file exists but could not be read (SettingsStore
            // only sets LoadFailed then; a missing file is a normal "nothing
            // to move"). Moving now would write the marker over empty values
            // and skip the move for good, so this ends like a failed probe:
            // nothing written, empty in-memory session, retry next launch.
            log("antigravity settings move: roaming settings unreadable; retrying at next launch");
            return SettingsStore.InMemory(log);
        }

        if (existed)
        {
            log("antigravity local settings had no move marker; moving again without the automatic-capture toggle");
        }

        var moved = Migrate(roaming, presence, log, probeTimeout ?? ProbeTimeout, carryToggle: !existed);
        if (moved is null)
        {
            return SettingsStore.InMemory(log);
        }

        local.SetMany(moved);
        if (!local.Persists)
        {
            log("antigravity settings move: the local store could not be saved; retrying at next launch");
            return SettingsStore.InMemory(log);
        }

        return local;
    }

    /// <summary>Every value the move writes, the marker included; null when a
    /// probe failed, threw or timed out. <paramref name="carryToggle"/> is
    /// false for a re-move (review D1): the toggle is then never written.</summary>
    private static Dictionary<string, JsonElement>? Migrate(
        SettingsStore roaming, Func<string, string> presence, Action<string> log, TimeSpan timeout,
        bool carryToggle)
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
        if (carryToggle && roaming.GetBool(AntigravityAutoCapture.EnabledKey, false))
        {
            values[AntigravityAutoCapture.EnabledKey] = JsonSerializer.SerializeToElement(true);
        }

        if (roaming.GetString(AntigravityAutoCapture.CurrentKey) is { } binding
            && AntigravityAutoCapture.ParseBinding(binding) is { Key: var key }
            && here.Any(a => a.Key == key))
        {
            values[AntigravityAutoCapture.CurrentKey] = JsonSerializer.SerializeToElement(binding);
        }

        return values;
    }
}
