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

        DevLog.Write($"tray icon gallery dumped to {dir} (titles: {string.Join(' ', titles)})");
    }
}
