using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TokenBar.Core;
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

/// <summary>The one-time Discord introduction (macOS <c>DiscordIntro</c>'s
/// NSAlert). It enables nothing: "Open Settings" navigates to the Discord
/// section, where the full disclosure is; "Not now" and the close box only
/// dismiss. Neither button is accented, and neither is the default — a filled
/// button beside a plain one is a thumb on the scale (DiscordIntro.swift
/// :109-113). Initial keyboard focus goes to "Not now", the direction that
/// changes nothing, and Esc dismisses.
///
/// <para>The show-once rule lives in <see cref="DiscordIntro.Consume"/>
/// (linked into the tests); this file is only the presentation.</para></summary>
internal sealed class DiscordIntroWindow : Window
{
    private const int DialogWidth = 440;
    private const int DialogHeight = 360;

    // Preview mock parameters, from macOS DiscordIntro.swift's
    // DiscordPresencePreview so the two cards read the same.

    /// <summary>Art tile corner radius: <c>RoundedRectangle(cornerRadius: 10)</c>
    /// (DiscordIntro.swift :138).</summary>
    private const double ArtCornerRadius = 10;

    /// <summary>Card corner radius: <c>RoundedRectangle(cornerRadius: 8)</c>
    /// (DiscordIntro.swift :156).</summary>
    private const double CardCornerRadius = 8;

    /// <summary>Button chip corner radius: <c>RoundedRectangle(cornerRadius: 4)</c>
    /// (DiscordIntro.swift :149).</summary>
    private const double ButtonCornerRadius = 4;

    /// <summary>Card fill alpha: <c>Color.secondary.opacity(0.10)</c>
    /// (DiscordIntro.swift :157), 0.10 × 255 ≈ 26.</summary>
    private const byte CardFillAlpha = 26;

    /// <summary>Button chip fill alpha: <c>Color.secondary.opacity(0.18)</c>
    /// (DiscordIntro.swift :149), 0.18 × 255 ≈ 46.</summary>
    private const byte ButtonFillAlpha = 46;

    /// <summary>The state line is <c>.foregroundStyle(.secondary)</c>
    /// (DiscordIntro.swift :142); 0.6 is the secondary-text opacity this app
    /// already uses for dim text (Ui.Dim).</summary>
    private const double SecondaryTextOpacity = 0.6;

    /// <summary>Held so the window is not collected while on screen; released
    /// on close — the card is never shown twice, so nothing reuses it.</summary>
    private static DiscordIntroWindow? _open;

    private readonly Button _notNow = new();

    internal static void PresentIfNeeded(Action openSettings)
    {
        // Consumed BEFORE presenting: closing or quitting while the card is up
        // counts as shown (DiscordIntro.swift :98-101).
        if (!DiscordIntro.Consume(AppSettings.Store))
        {
            return;
        }

        try
        {
            var window = new DiscordIntroWindow(openSettings);
            _open = window;
            window.AppWindow.Show();
            // Launch has no foreground rights; hoist it like SettingsWindow.
            window.AppWindow.MoveInZOrderAtTop();
            window.Activate();
            DevLog.Write("discord-intro: shown");
        }
        catch (Exception ex)
        {
            var type = ex.GetType().Name;
            DevLog.Write($"discord-intro: failed {type[..Math.Min(type.Length, 64)]}");
        }
    }

    private DiscordIntroWindow(Action openSettings)
    {
        Title = DiscordCopy.Toggle.Localized();
        SystemBackdrop = new MicaBackdrop();

        // The text scrolls; the button row is pinned below it, outside the
        // scroller, so both buttons stay reachable at any text-scaling setting
        // even though the window size is fixed.
        var root = new Grid { Padding = new Thickness(24, 20, 24, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var body = new StackPanel { Spacing = 12 };
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = body,
        });
        body.Children.Add(new TextBlock
        {
            Text = DiscordCopy.Toggle.Localized(),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = DiscordCopy.IntroBody.Localized(),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(BuildPreview());

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var settings = new Button { Content = DiscordCopy.OpenSettings.Localized() };
        settings.Click += (_, _) =>
        {
            Close();
            openSettings(); // navigation, never consent
        };
        buttons.Children.Add(settings);
        _notNow.Content = DiscordCopy.NotNow.Localized();
        _notNow.Click += (_, _) => Close();
        buttons.Children.Add(_notNow);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);

        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        root.KeyboardAccelerators.Add(escape);
        Content = root;

        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "syrtis.ico"));
        }
        catch (Exception ex)
        {
            DevLog.Write($"discord-intro icon: {ex.Message}");
        }

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        ApplySize();
        var focused = false;
        Activated += (_, _) =>
        {
            ApplySize();
            if (!focused)
            {
                focused = true;
                _notNow.Focus(FocusState.Programmatic);
            }
        };
        Closed += (_, _) => _open = null;
    }

    /// <summary>A mock of the activity as Discord lays it out: the art, the
    /// portal app name, details, state and the button. Representative values
    /// labelled as a preview — never the user's figures, which may not have
    /// loaded yet (DiscordIntro.swift :76-95, :120-160).</summary>
    private static Border BuildPreview()
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var art = new Image { Width = 56, Height = 56 };
        // The same asset Discord resolves `syrtis` to, clipped to a rounded
        // square the way UpdateDialog clips its header icon.
        var artHost = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(ArtCornerRadius),
            VerticalAlignment = VerticalAlignment.Top,
            Child = art,
        };
        art.ImageFailed += (_, _) => artHost.Visibility = Visibility.Collapsed;
        try
        {
            art.Source = new BitmapImage(new Uri("ms-appx:///Assets/syrtis.ico"));
        }
        catch (Exception ex)
        {
            DevLog.Write($"discord-intro art: {ex.Message}");
            artHost.Visibility = Visibility.Collapsed;
        }

        row.Children.Add(artHost);

        var lines = new StackPanel { Spacing = 2 };
        lines.Children.Add(new TextBlock
        {
            Text = DiscordCopy.PreviewTitle,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        });
        lines.Children.Add(new TextBlock { Text = DiscordCopy.PreviewDetails.Localized(), FontSize = 11 });
        lines.Children.Add(new TextBlock
        {
            Text = DiscordCopy.PreviewState.Localized(),
            FontSize = 11,
            Opacity = SecondaryTextOpacity,
        });
        lines.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(ButtonCornerRadius),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 3, 0, 0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(ButtonFillAlpha, 128, 128, 128)),
            Child = new TextBlock
            {
                Text = DiscordIpc.ButtonLabel,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        });
        Grid.SetColumn(lines, 1);
        row.Children.Add(lines);

        return new Border
        {
            CornerRadius = new CornerRadius(CardCornerRadius),
            Padding = new Thickness(10),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(CardFillAlpha, 128, 128, 128)),
            Child = row,
        };
    }

    private void ApplySize()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = new Windows.Graphics.SizeInt32(
            Math.Min((int)(DialogWidth * scale), area.Width),
            Math.Min((int)(DialogHeight * scale), area.Height));
        if (AppWindow.Size == size)
        {
            return;
        }

        AppWindow.Resize(size);
        AppWindow.Move(new Windows.Graphics.PointInt32(
            area.X + (area.Width - AppWindow.Size.Width) / 2,
            area.Y + (area.Height - AppWindow.Size.Height) / 3));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
