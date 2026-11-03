using System.Text.Json;
using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Added Cursor profiles (<c>--user-data-dir</c> folders, CMP-W): each an
/// uppercase GUID and the folder path, nothing else (never an account id,
/// email or label). Local-only by maintainer decision (WB5): the file lives
/// under <c>%LOCALAPPDATA%</c>, never in the roaming settings, so another PC's
/// folders and consent never arrive here. W1 defines the store; nothing reads
/// it into a core push until W2 wires it behind the disclosure (WB11).
/// </summary>
public sealed class CursorProfileStore(string path)
{
    /// <summary><c>%LOCALAPPDATA%\TokenBar\cursor-profiles.json</c>.</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenBar", "cursor-profiles.json");

    /// <summary>A new profile id: the form the core accepts (8-4-4-4-12
    /// uppercase hex), so two ids never name one folder on a case-insensitive
    /// volume.</summary>
    public static string NewId() => Guid.NewGuid().ToString("D").ToUpperInvariant();

    private sealed record Entry(string Id, string Root);

    /// <summary>The stored list; empty when the file is absent or unreadable.
    /// Null entries and entries whose id is not an uppercase GUID are dropped
    /// (the core would refuse the whole push for one).</summary>
    public IReadOnlyList<CursorProfile> Load()
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [];
            return entries
                .Where(e => e is not null && e.Id is not null && e.Root is not null && IsId(e.Id))
                .Select(e => new CursorProfile(e.Id, e.Root))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Replace the stored list (temp file, then a replacing move).</summary>
    public void Save(IReadOnlyList<CursorProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profiles.Select(p => new Entry(p.Id, p.Root))));
        File.Move(temp, path, overwrite: true);
    }

    private static bool IsId(string id) =>
        id.Length == 36
        && Guid.TryParseExact(id, "D", out _)
        && id == id.ToUpperInvariant();
}
