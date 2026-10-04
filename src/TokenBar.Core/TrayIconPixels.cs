namespace TokenBar.Core;

/// <summary>Pixel fix-ups applied to every tray icon bitmap before it becomes
/// an HICON (TrayIconHandle in the app).</summary>
public static class TrayIconPixels
{
    /// <summary>The channel value a visible pure-black pixel is lifted to.
    /// Measured on the x64 test machine (188) at 200% scale on a light
    /// taskbar: the light sand frame drawn in pure black (0,0,0) reached an
    /// effective alpha of 0.189 (its opaque interior showed as background),
    /// and the same frame at (16,16,16) reached 0.878, equal to the dark
    /// frame. Building the icon with CreateIconIndirect and an all-zero AND
    /// mask instead of GetHicon did not help (still 0.189). Why the shell
    /// treats pure black like a color key is not established.</summary>
    public const byte NearBlack = 16;

    /// <summary>Lifts every visible pure-black pixel of a 32bpp BGRA buffer
    /// (System.Drawing's Format32bppArgb byte order) to
    /// <see cref="NearBlack"/>, keeping its alpha. Fully transparent pixels
    /// are left alone. Returns how many pixels changed.</summary>
    public static int LiftPureBlack(Span<byte> bgra)
    {
        var lifted = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] > 0 && bgra[i] == 0 && bgra[i + 1] == 0 && bgra[i + 2] == 0)
            {
                bgra[i] = NearBlack;
                bgra[i + 1] = NearBlack;
                bgra[i + 2] = NearBlack;
                lifted++;
            }
        }

        return lifted;
    }
}
