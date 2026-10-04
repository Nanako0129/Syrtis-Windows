namespace TokenBar.App;

/// <summary>--dump-tray-icons: renders every gauge style × level × theme and
/// a set of title samples to %TEMP%\tray-icons for remote visual
/// verification (the offscreen-render-and-scp loop the D3D spike
/// established).</summary>
internal static class TrayIconGallery
{
    public static void Dump()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tray-icons");
        Directory.CreateDirectory(dir);
        double[] levels = [100, 60, 30, 20, 8];
        foreach (var style in new[]
            { QuotaIconStyle.Bars, QuotaIconStyle.Ring, QuotaIconStyle.Popsicle })
        {
            foreach (var dark in new[] { true, false })
            {
                foreach (var level in levels)
                {
                    using var bmp = TrayIconRenderer.RenderGauge(
                        style, level, dark, IconColoring.WarningOnly);
                    bmp.Save(Path.Combine(
                        dir, $"{style}-{level:F0}-{(dark ? "dark" : "light")}.png"));
                }

                // The two non-live glyphs: no reading (faded + slash, #419)
                // and a stale reading (grey fill, #8).
                var theme = dark ? "dark" : "light";
                using (var none = TrayIconRenderer.RenderGauge(
                    style, null, dark, IconColoring.WarningOnly))
                {
                    none.Save(Path.Combine(dir, $"{style}-noreading-{theme}.png"));
                }

                using var stale = TrayIconRenderer.RenderGauge(
                    style, 20, dark, IconColoring.WarningOnly, stale: true);
                stale.Save(Path.Combine(dir, $"{style}-20-stale-{theme}.png"));
            }
        }

        string[] titles =
            ["12.3K", "$5.20", "$889.13", "$4637.49", "1.5B", "57%", "8%", "—/m", "999"];
        for (var i = 0; i < titles.Length; i++)
        {
            var color = titles[i].EndsWith('%')
                ? TrayIconRenderer.GaugeColor(double.Parse(titles[i].TrimEnd('%')))
                : (System.Drawing.Color?)null;
            // Through IconTitle, same as the live tray path.
            using var bmp = TrayIconRenderer.RenderTitle(
                TokenBar.Core.TrayModes.IconTitle(titles[i]), color, dark: true);
            bmp.Save(Path.Combine(dir, $"title-{i}.png"));
        }

        // A stale quota-left value takes the stale grey (#420).
        foreach (var dark in new[] { true, false })
        {
            using var bmp = TrayIconRenderer.RenderTitle(
                TokenBar.Core.TrayModes.IconTitle("8%"), TrayIconRenderer.StaleInk(dark), dark);
            bmp.Save(Path.Combine(dir, $"title-8-stale-{(dark ? "dark" : "light")}.png"));
        }

        DumpBlackInk(dir);
        DevLog.Write($"tray icon gallery dumped to {dir} (titles: {string.Join(' ', titles)})");
    }

    /// <summary>Frame 0 of each animated style, both themes, through the
    /// tray's own path (TrayAnimator.ComposeFrame, then TrayIconHandle.From),
    /// counting visible pixels and visible pure-black ones at each step:
    /// the composed canvas, the canvas after the lift, and the HICON read
    /// back (Icon.ToBitmap). Written to black-ink.txt with the PNGs.</summary>
    private static void DumpBlackInk(string dir)
    {
        var report = new List<string> { "per step: visible pixels / visible pure-black pixels (darkest visible pixel's max channel)" };
        foreach (var (name, light) in new[]
        {
            ("sand0", "anim-sand0-light"), ("sand0", "anim-sand0"),
            ("cat2", "anim-cat2-light"), ("cat2", "anim-cat2"),
            ("parrot", "anim-parrot-light"), ("parrot", "anim-parrot"),
        })
        {
            var file = Path.Combine(AppContext.BaseDirectory, "Assets", light, "frame-000.png");
            if (!File.Exists(file))
            {
                report.Add($"{light}: missing {file}");
                continue;
            }

            using var canvas = TrayAnimator.ComposeFrame(file);
            var composed = Count(canvas);
            canvas.Save(Path.Combine(dir, $"ink-{light}-composed.png"));
            var hicon = TrayIconHandle.From(canvas);
            var lifted = Count(canvas);
            canvas.Save(Path.Combine(dir, $"ink-{light}-lifted.png"));
            string readBack;
            using (var icon = System.Drawing.Icon.FromHandle(hicon))
            using (var back = icon.ToBitmap())
            {
                readBack = Count(back);
                back.Save(Path.Combine(dir, $"ink-{light}-hicon.png"));
            }

            _ = DestroyIcon(hicon);
            report.Add($"{light} [{canvas.PixelFormat}]: composed {composed}; lifted {lifted}; hicon {readBack}");
        }

        File.WriteAllLines(Path.Combine(dir, "black-ink.txt"), report);
    }

    private static string Count(System.Drawing.Bitmap bitmap)
    {
        int visible = 0, black = 0, minRgb = 255;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.A == 0)
                {
                    continue;
                }

                visible++;
                minRgb = Math.Min(minRgb, Math.Max(c.R, Math.Max(c.G, c.B)));
                if (c.R == 0 && c.G == 0 && c.B == 0)
                {
                    black++;
                }
            }
        }

        return $"{visible}/{black} (min {minRgb})";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);
}
