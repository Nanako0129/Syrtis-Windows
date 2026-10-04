using System.Text.Json;

namespace TokenBar.Core;

/// <summary>
/// The UserDefaults stand-in: a flat JSON dictionary of `tokenbar.*` keys.
/// Unpackaged WinUI apps have no ApplicationDataContainer, and a plain JSON
/// file is debuggable over SSH; the path is injected so tests (and macOS
/// test runs) never touch a real profile. Writes are atomic
/// (temp file + rename) so a crash mid-save can't tear the store.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonElement> _values;
    private readonly bool _loadFailed;

    /// <summary>Fires after a key's value actually changes (set to the same
    /// value is a no-op, mirroring the signature-gated observers the macOS
    /// side uses to avoid re-render loops). Raised on the writing thread.</summary>
    public event Action<string>? Changed;

    public SettingsStore(string path, Action<string>? log = null)
    {
        _path = path;
        _log = log;
        _values = Load(path, out _loadFailed);
    }

    private static Dictionary<string, JsonElement> Load(string path, out bool loadFailed)
    {
        loadFailed = false;
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(path)) ?? [];
            }
        }
        catch (JsonException)
        {
            // Genuine corruption: quarantine the unreadable file so it isn't
            // silently overwritten (it may be hand-recoverable), then rebuild
            // from defaults on the next write.
            Quarantine(path);
        }
        catch
        {
            // Transient read failure (an AV scan or sync tool holding the file
            // with a deny-read lock): the on-disk store is probably intact, so
            // DON'T let the next Save() clobber it with defaults — refuse to
            // persist for this session instead of destroying good settings.
            loadFailed = true;
        }

        return [];
    }

    private static void Quarantine(string path)
    {
        try
        {
            var corrupt = path + ".corrupt";
            File.Delete(corrupt); // no-op if absent; File.Move won't overwrite
            File.Move(path, corrupt);
        }
        catch
        {
            // Best effort; a later Save() overwrites the bad file if this fails.
        }
    }

    public string? GetString(string key, string? fallback = null)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : fallback;
        }
    }

    /// <summary>Presence and value kind, which <see cref="GetString"/> collapses:
    /// it returns its fallback both for an absent key and for a key present
    /// holding a non-string JSON value. Those are different outcomes for a codec
    /// that must not overwrite what it cannot read — a present non-string belongs
    /// to a foreign writer. Returns false when the key is absent; true with
    /// <paramref name="value"/> set for a present string; true with
    /// <paramref name="value"/> null for a present non-string.</summary>
    public bool TryGetString(string key, out string? value)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out var v))
            {
                value = null;
                return false;
            }

            value = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return true;
        }
    }

    public bool GetBool(string key, bool fallback)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var v)
                && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : fallback;
        }
    }

    /// <summary>A stored bool, or null when the key is absent or not a bool —
    /// the tri-state <see cref="GetBool"/> cannot express (UserDefaults
    /// <c>object(forKey:) as? Bool</c>).</summary>
    public bool? GetNullableBool(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var v)
                && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : null;
        }
    }

    public int GetInt(string key, int fallback)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var n) ? n : fallback;
        }
    }

    public double GetDouble(string key, double fallback)
    {
        lock (_gate)
        {
            // TryGetDouble, not GetDouble: a hand-edited out-of-range literal
            // (1e9999) parses into the store but throws on GetDouble.
            return _values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetDouble(out var d) ? d : fallback;
        }
    }

    public void SetString(string key, string value) =>
        Set(key, JsonSerializer.SerializeToElement(value));

    public void SetBool(string key, bool value) =>
        Set(key, JsonSerializer.SerializeToElement(value));

    public void SetInt(string key, int value) =>
        Set(key, JsonSerializer.SerializeToElement(value));

    public void SetDouble(string key, double value) =>
        Set(key, JsonSerializer.SerializeToElement(value));

    /// <summary>UserDefaults removeObject parity: reverts the key to
    /// whatever fallback its readers pass (e.g. the year filter's "all").</summary>
    public void Remove(string key)
    {
        lock (_gate)
        {
            if (!_values.Remove(key))
            {
                return;
            }

            Save();
        }

        Changed?.Invoke(key);
    }

    private void Set(string key, JsonElement value)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(key, out var old)
                && old.ValueKind == value.ValueKind
                && old.GetRawText() == value.GetRawText())
            {
                return;
            }

            _values[key] = value;
            Save();
        }

        Changed?.Invoke(key);
    }

    private void Save()
    {
        if (_loadFailed)
        {
            // The existing store couldn't be read at startup; writing now would
            // replace possibly-good on-disk settings with in-memory defaults.
            _log?.Invoke("settings save skipped: initial load failed, not clobbering the file");
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                new SortedDictionary<string, JsonElement>(_values), WriteOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Disk trouble (AV scan or a sync tool holding the file) must
            // not crash the caller: memory keeps the new value and the next
            // successful save heals the file.
            _log?.Invoke($"settings save failed: {ex.Message}");
        }
    }
}
