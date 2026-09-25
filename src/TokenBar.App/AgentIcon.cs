using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TokenBar.Core;

namespace TokenBar.App;

/// <summary>WinUI rendering for TokenBar.Core.AgentIcons: the brand-icon disc
/// shared by every per-client placement (dashboard client tabs, the agent
/// limits card header, the settings client-tab list). A client with no brand
/// icon falls back to the same plain brand-colour disc (<see cref="Ui.Disc"/>)
/// every one of those placements already used before this control existed.
///
/// Rendering, ported from AgentIconView.swift's body:
///  - Full icons (AgentIconView.swift:120-134): an Ellipse filled with an
///    ImageBrush, optionally backed by a second, full-size Ellipse in the
///    brand's backdrop colour. ImageBrush's own Stretch does the circular
///    crop the Swift side gets from `.clipShape(Circle())` — no separate clip
///    geometry needed.
///  - Mono icons (AgentIconView.swift:136-143): a brand-colour disc with a
///    smaller glyph Image centered on top, tinted white. SvgImageSource has
///    no recolour API, so the glyph is tinted by rewriting the SVG's root
///    `&lt;svg&gt;` tag to carry `fill="white"` before handing it a stream —
///    safe because none of the five mono glyphs sets its own `fill` on the
///    inner &lt;path&gt;, so the injected fill is inherited unchanged. That
///    read is async (SvgImageSource.SetSourceAsync has no sync overload), so
///    the glyph pops in fire-and-forget once decoded, same as any other
///    async-loaded XAML image.</summary>
public static class AgentIcon
{
    // AgentIconView.swift:142 — mono glyph fills 64% of the disc.
    private const double MonoGlyphScale = 0.64;

    public static FrameworkElement Create(string clientId, double size = 14)
    {
        var color = ClientRegistry.Style(clientId).Color;
        var info = AgentIcons.Resolve(clientId);
        return info switch
        {
            null => Ui.Disc(color, size),
            { Kind: AgentIconKind.Full } full => FullDisc(full, size),
            { Kind: AgentIconKind.Mono } mono => MonoDisc(mono, color, size),
            _ => Ui.Disc(color, size),
        };
    }

    private static Grid FullDisc(AgentIconInfo info, double size)
    {
        var host = new Grid { Width = size, Height = size };
        if (info.BackgroundHex is { } bg)
        {
            host.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                Fill = Ui.BrushFromHex(bg),
            });
        }

        var markSize = size * info.InsetScale;
        host.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = markSize,
            Height = markSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new ImageBrush
            {
                ImageSource = LoadSource(info.IconId),
                Stretch = Stretch.UniformToFill,
            },
        });
        return host;
    }

    private static Grid MonoDisc(AgentIconInfo info, string colorHex, double size)
    {
        var host = new Grid { Width = size, Height = size };
        host.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Fill = Ui.BrushFromHex(colorHex),
        });
        var glyph = new Image
        {
            Width = size * MonoGlyphScale,
            Height = size * MonoGlyphScale,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(glyph);
        _ = LoadTintedGlyphAsync(glyph, info.IconId);
        return host;
    }

    /// <summary>All shipped agent icons live under this app-relative path
    /// (ms-appx:///Assets/agent-icons/&lt;id&gt;.{svg,png}), the same
    /// unpackaged-asset pattern UpdateDialog uses for the title-bar icon.
    /// SVG is preferred; some brand marks ship only as PNG.</summary>
    private static ImageSource LoadSource(string iconId)
    {
        if (System.IO.File.Exists(AssetPath(iconId, "svg")))
        {
            return new SvgImageSource(new Uri($"ms-appx:///Assets/agent-icons/{iconId}.svg"));
        }

        return new BitmapImage(new Uri($"ms-appx:///Assets/agent-icons/{iconId}.png"));
    }

    private static async Task LoadTintedGlyphAsync(Image glyph, string iconId)
    {
        try
        {
            var svgPath = AssetPath(iconId, "svg");
            var svg = await System.IO.File.ReadAllTextAsync(svgPath);
            var tinted = svg.Replace("<svg ", "<svg fill=\"white\" ");
            var source = new SvgImageSource();
            using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(tinted));
            await source.SetSourceAsync(stream.AsRandomAccessStream());
            glyph.Source = source;
        }
        catch
        {
            // Missing/unreadable asset: the brand-colour disc stays bare
            // rather than crashing, same fallback policy as the tray
            // animation frames and strings-*.json.
        }
    }

    private static string AssetPath(string iconId, string extension) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "agent-icons", $"{iconId}.{extension}");
}
