using System.Runtime.CompilerServices;

namespace TokenBar.Core.Tests;

// The native engine reads TOKSCALE_CONFIG_DIR from the process environment at
// call time, and several tests scan through LocalSourceContext::process(). With
// the variable unset the engine falls back to the real %APPDATA%\tokscale, so a
// test run on a machine with real data read and rewrote its cache shards
// (2026-11-03, 246 shards). A module initializer runs before any test code, so
// before any P/Invoke. A value already set (win188's lane dir, CI) is kept.
internal static class ConfigDirIsolation
{
    internal const string Var = "TOKSCALE_CONFIG_DIR";

    [ModuleInitializer]
    internal static void Isolate()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Var))) return;
        var dir = Path.Combine(Path.GetTempPath(), "tokenbar-tests-tokscale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(Var, dir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        };
    }
}

public class ConfigDirIsolationTests
{
    [Fact]
    public void TokscaleConfigDirIsSetAndIsNotTheRealOne()
    {
        var dir = Environment.GetEnvironmentVariable(ConfigDirIsolation.Var);
        Assert.False(string.IsNullOrEmpty(dir));
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tokscale");
        Assert.NotEqual(Path.GetFullPath(real).TrimEnd('\\', '/'), Path.GetFullPath(dir!).TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase);
    }
}
