using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Exercises the real native library (copied to the test output by
// src/Directory.Build.targets). Message count is environment-dependent —
// zero on a machine with no agent sessions — so only the seam is asserted.
public class ProbeTests
{
    [Fact]
    public void ProbeLoadsNativeLibraryAndReturnsOk()
    {
        var probe = TbCore.Probe();
        Assert.True(probe.Ok);
        Assert.True(probe.Messages >= 0);
        // The opencodeDb object decodes under the strict (required-member) options.
        Assert.NotNull(probe.OpencodeDb);
        Assert.NotEmpty(probe.OpencodeDb!.SettingsFile);
    }
}
