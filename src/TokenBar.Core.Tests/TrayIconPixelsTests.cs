namespace TokenBar.Core.Tests;

// Every tray bitmap passes through TrayIconPixels.LiftPureBlack on its way to
// an HICON (TrayIconHandle, which the WinUI-only app owns; this project
// cannot load System.Drawing). On a light taskbar a visible pure-black pixel
// read as near-transparent (188, 200%: effective alpha 0.189, 0.878 lifted).
public class TrayIconPixelsTests
{
    private static byte[] Pixels(params (byte B, byte G, byte R, byte A)[] pixels) =>
        [.. pixels.SelectMany(p => new[] { p.B, p.G, p.R, p.A })];

    private static bool HasVisiblePureBlack(byte[] bgra) =>
        bgra.Chunk(4).Any(p => p[3] > 0 && p[0] == 0 && p[1] == 0 && p[2] == 0);

    [Fact]
    public void NoVisiblePureBlackPixelIsLeft()
    {
        // A light frame as drawn: opaque and partly transparent black ink.
        var frame = Pixels((0, 0, 0, 255), (0, 0, 0, 90), (0, 0, 0, 1));

        Assert.True(HasVisiblePureBlack(frame)); // what reached the HICON before
        Assert.Equal(3, TrayIconPixels.LiftPureBlack(frame));
        Assert.False(HasVisiblePureBlack(frame));
        Assert.Equal(
            Pixels(
                (TrayIconPixels.NearBlack, TrayIconPixels.NearBlack, TrayIconPixels.NearBlack, 255),
                (TrayIconPixels.NearBlack, TrayIconPixels.NearBlack, TrayIconPixels.NearBlack, 90),
                (TrayIconPixels.NearBlack, TrayIconPixels.NearBlack, TrayIconPixels.NearBlack, 1)),
            frame);
    }

    // Alpha is never touched, transparent pixels stay as they are, and any
    // pixel that is not pure black (the dark sets' white, gauge colors) is
    // left alone.
    [Fact]
    public void OnlyVisiblePureBlackChanges()
    {
        var frame = Pixels((0, 0, 0, 0), (255, 255, 255, 200), (1, 0, 0, 255), (0, 128, 255, 255));
        var before = frame.ToArray();

        Assert.Equal(0, TrayIconPixels.LiftPureBlack(frame));
        Assert.Equal(before, frame);
    }
}
