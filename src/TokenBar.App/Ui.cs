using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TokenBar.Core;
using TokenBar.Interop;
using Windows.UI;

namespace TokenBar.App;

/// <summary>Small element factories shared by every lens.</summary>
public static class Ui
{
    /// <summary>Legend keys. The labels stay English abbreviations in every
    /// language on purpose: they have to fit a legend chip, and Chinese has no
    /// two-character shortening of 快取讀取 that still reads. The same five
    /// concepts appear as full words — and are translated — in the Models card
    /// and the tooltips, which is the split English already makes.</summary>
    public static readonly (string Key, string Label, string Color)[] TokenKinds =
    [
        ("input", "In", "#3b82f6"),
        ("output", "Out", "#22c55e"),
        ("cacheRead", "CR", "#f59e0b"),
        ("cacheWrite", "CW", "#a855f7"),
        ("reasoning", "R", "#ec4899"),
    ];

    // Card header metrics, from macOS DashCard (Cards.swift:220-233).
    // Title 13 semibold is the same number on both platforms.
    private const double CardTitleSize = 13;

    // macOS sets the subtitle and the title accessory in .caption (10 pt).
    // Windows keeps 11: every secondary line here (Ui.Text's default, Ui.Dim,
    // the old right-aligned subtitle) is 11, and a 10 would read a size
    // smaller than its neighbours rather than "the same role".
    private const double CardSecondarySize = 11;

    // macOS .secondary foreground; 0.6 is the opacity the old subtitle and the
    // rest of the dashboard's secondary text already use for it.
    private const double CardSecondaryOpacity = 0.6;

    // macOS: VStack(spacing: 2) between the title line and the subtitle.
    private const double CardTitleSubtitleGap = 2;

    // macOS: HStack(spacing: 6) between the title and its accessory.
    private const double CardTitleAccessoryGap = 6;

    // Room kept for the accessory when the title is long: macOS shrinks both
    // texts, so some of the account label always shows; without this the
    // Auto title column could take the whole line and the label (and its
    // tooltip) would vanish instead of trimming.
    private const double CardAccessoryMinWidth = 48;

    /// <summary>A dashboard card, laid out as macOS <c>DashCard</c>: the title
    /// (and <paramref name="titleAccessory"/>) on the first line, the
    /// subtitle under it on its own line, wrapping, and
    /// <paramref name="trailing"/> at the right end of the header.</summary>
    /// <param name="titleAccessory">Secondary text right after the title on
    /// the same line (the non-primary account a window card shows). The title
    /// takes its full width and the accessory trims (macOS
    /// <c>DashCard.titleAccessory</c>).</param>
    public static Border Card(
        string title, UIElement content, string? subtitle = null, UIElement? trailing = null,
        string? titleAccessory = null)
    {
        var stack = new StackPanel();
        var head = new Microsoft.UI.Xaml.Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Without an accessory the title owns the line and trims with an
        // ellipsis (long window labels such as Antigravity's "Gemini Models ·
        // Weekly Limit Remaining"); the full text shows on hover only when
        // trimmed.
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = CardTitleSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        // The hover card is one shared popup per XamlRoot, so this title only
        // moves or closes it while it is the one that opened it; otherwise an
        // untrimmed title (or a rebuilt card's Unloaded) would move or close
        // another element's tip.
        var showing = false;
        titleText.PointerEntered += (_, e) =>
        {
            if (titleText.IsTextTrimmed && titleText.XamlRoot is { } root)
            {
                showing = true;
                HoverTip.ShowAt(titleText, new TextBlock
                {
                    Text = title,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 240, 245)),
                }, e.GetCurrentPoint(root.Content).Position);
            }
        };
        titleText.PointerMoved += (_, e) =>
        {
            if (showing && titleText.XamlRoot is { } root)
            {
                HoverTip.MoveAt(titleText, e.GetCurrentPoint(root.Content).Position);
            }
        };
        void Hide()
        {
            if (showing)
            {
                showing = false;
                HoverTip.HideFor(titleText);
            }
        }
        titleText.PointerExited += (_, _) => Hide();
        titleText.Unloaded += (_, _) => Hide();

        var left = new StackPanel { Spacing = CardTitleSubtitleGap };
        if (titleAccessory is null)
        {
            left.Children.Add(titleText);
        }
        else
        {
            // The window name is what must stay readable, so the title takes
            // its width (Auto) and the account label trims in the rest. The
            // title's MaxWidth follows the line, so a title wider than the
            // whole card still trims with its ellipsis and hover tip instead
            // of being clipped.
            var line = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = CardTitleAccessoryGap };
            line.SizeChanged += (_, e) => titleText.MaxWidth =
                Math.Max(0, e.NewSize.Width - CardTitleAccessoryGap - CardAccessoryMinWidth);
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            line.Children.Add(titleText);
            var accessory = new TextBlock
            {
                Text = titleAccessory,
                FontSize = CardSecondarySize,
                Opacity = CardSecondaryOpacity,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            ToolTipService.SetToolTip(accessory, titleAccessory);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(accessory, 1);
            line.Children.Add(accessory);
            left.Children.Add(line);
        }

        // An empty subtitle (a caller joining zero parts) is no subtitle: on
        // its own line it would leave a blank line under the title.
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            left.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = CardSecondarySize,
                Opacity = CardSecondaryOpacity,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        head.Children.Add(left);
        if (trailing is not null)
        {
            // macOS aligns the trailing control on the title's first baseline,
            // so with a subtitle line it sits by the title, not between lines.
            var end = (FrameworkElement)trailing;
            end.HorizontalAlignment = HorizontalAlignment.Right;
            end.VerticalAlignment = string.IsNullOrWhiteSpace(subtitle)
                ? VerticalAlignment.Center
                : VerticalAlignment.Top;
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(end, 1);
            head.Children.Add(end);
        }

        stack.Children.Add(head);
        stack.Children.Add(content);
        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = stack,
        };
    }

    public static Ellipse Disc(string hex, double size = 8) => new()
    {
        Width = size,
        Height = size,
        Fill = BrushFromHex(hex),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Text(string text, double size = 11, double opacity = 1.0,
        bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = opacity,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary><see cref="Text"/> truncated in the middle rather than at the
    /// end (macOS <c>.truncationMode(.middle)</c>), for model ids and account
    /// labels whose distinguishing part is at the end. Needs a bounded width;
    /// see <see cref="MiddleTrimmedText"/>.</summary>
    public static MiddleTrimmedText MiddleText(string text, double size = 11, double opacity = 1.0,
        bool bold = false) => new(Text(text, size, opacity, bold));

    public static TextBlock Dim(string text, double size = 11) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = 0.6,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Two-column row: content left, trailing text right.</summary>
    public static Microsoft.UI.Xaml.Controls.Grid Row(UIElement left, UIElement right)
    {
        var grid = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn((FrameworkElement)right, 1);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>Horizontal proportion bar (share of a total), brand colored.</summary>
    public static Microsoft.UI.Xaml.Controls.Grid ShareBar(double fraction, string hex)
    {
        var clamped = Math.Clamp(fraction, 0, 1);
        var track = new Microsoft.UI.Xaml.Controls.Grid
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128)),
        };
        track.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(clamped, 0.0001), GridUnitType.Star),
        });
        track.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(1 - clamped, 0.0001), GridUnitType.Star),
        });
        track.Children.Add(new Border
        {
            Background = BrushFromHex(hex),
            CornerRadius = new CornerRadius(2),
        });
        return track;
    }

    /// <summary>Live-session rows (macOS UsageTraceCard): one collapsed row per
    /// app, or one row per agent-and-model bucket when detailed, each with a
    /// rate bar. With nothing running it says so instead of disappearing, as
    /// macOS keeps the card and reads "No activity in this window".</summary>
    public static FrameworkElement TraceRows(
        IReadOnlyList<TraceBucket> trace, IReadOnlySet<string> selected, bool detailed)
    {
        var rows = TraceCollapse.CardRows(trace, selected, detailed);
        if (rows.Count == 0)
        {
            var empty = Dim("No activity in this window".Localized());
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 8, 0, 8);
            return empty;
        }

        var maxRate = rows.Max(r => r.TokensPerMin);
        var panel = new StackPanel { Spacing = 6 };
        foreach (var row in rows)
        {
            // Client, agent, then the model in the star column so a long
            // model id trims instead of pushing the rate off the row.
            var head = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 6 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cells = new UIElement[]
            {
                Text(ClientRegistry.ShortName(row.Client), 10, bold: true),
                Text(row.Agent, 10, 0.75),
                Text(row.Model, 10, 0.55),
                Text($"{Format.CompactTokens((long)Math.Round(row.TokensPerMin))}/m", 10, 0.75),
            };
            ((TextBlock)cells[2]).TextTrimming = TextTrimming.CharacterEllipsis;
            for (var c = 0; c < cells.Length; c++)
            {
                Microsoft.UI.Xaml.Controls.Grid.SetColumn((FrameworkElement)cells[c], c);
                head.Children.Add(cells[c]);
            }

            var line = new StackPanel { Spacing = 2 };
            line.Children.Add(head);
            line.Children.Add(ShareBar(
                TraceCollapse.BarPercent(row.TokensPerMin, maxRate) / 100,
                TraceBarColor));
            panel.Children.Add(line);
        }

        return panel;
    }

    /// <summary>The live-session bar: one accent for every row, as macOS
    /// fills it with the accent color rather than the client's.</summary>
    private const string TraceBarColor = "#3b82f6";

    public static SolidColorBrush BrushFromHex(string hex)
    {
        var h = hex.TrimStart('#');
        return new SolidColorBrush(Color.FromArgb(
            255,
            Convert.ToByte(h[..2], 16),
            Convert.ToByte(h[2..4], 16),
            Convert.ToByte(h[4..6], 16)));
    }
}
