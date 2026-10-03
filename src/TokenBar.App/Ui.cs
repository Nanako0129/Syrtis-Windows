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

    public static Border Card(
        string title, UIElement content, string? subtitle = null, UIElement? trailing = null)
    {
        var stack = new StackPanel();
        var head = new Microsoft.UI.Xaml.Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // The title owns the star column and trims with an ellipsis rather than
        // running under the right-aligned subtitle (long window labels such as
        // Antigravity's "Gemini Models · Weekly Limit Remaining"). Nothing
        // changes when it fits. The full text shows on hover only when trimmed.
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
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
        head.Children.Add(titleText);
        if (subtitle is not null || trailing is not null)
        {
            var end = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (subtitle is not null)
            {
                end.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 11,
                    Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            if (trailing is not null)
            {
                end.Children.Add(trailing);
            }

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
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            name.Children.Add(Text(ClientRegistry.ShortName(row.Client), 10, bold: true));
            name.Children.Add(Text(row.Agent, 10, 0.75));
            var model = Text(row.Model, 10, 0.55);
            model.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Children.Add(model);
            var line = new StackPanel { Spacing = 2 };
            line.Children.Add(Row(
                name, Text($"{Format.CompactTokens((long)Math.Round(row.TokensPerMin))}/m", 10, 0.75)));
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
