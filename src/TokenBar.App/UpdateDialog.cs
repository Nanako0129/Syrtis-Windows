using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

/// <summary>
/// The Sparkle update alert (macOS <c>SUUpdateAlert</c>): rounded app icon and
/// two header lines, an inset scrolling changelog box, and three buttons.
///
/// <para><b>Skip sits alone on the left; Remind Me Later and Install Update
/// are grouped right, Install accented at the far right.</b> That arrangement
/// carries the meaning — Skip is the only one of the three with a persistent
/// consequence, so it is spatially separated from the two reversible ones.
/// </para>
///
/// <para>Sparkle's "Automatically download and install updates in the future"
/// checkbox is out of scope: unattended installation is a behaviour change,
/// not a UI port. The button row moves up into the space it would have taken.
/// Rendering it disabled for visual parity was considered and rejected — a
/// disabled, unexplained control is worse than its absence.</para>
///
/// <para><b>One instance.</b> Both triggers (the tray menu item and the balloon
/// click) stay live while this is open, by design: nothing has been taken yet.
/// A <c>ContentDialog</c> would throw <c>InvalidOperationException</c> on the
/// second open, synchronously inside <c>RelayCommand.Execute</c>, which has no
/// exception handling — a crash on the UI thread. A second <c>Window</c> would
/// give two dialogs racing for one pending action. So this follows
/// <c>SettingsWindow</c>'s shared-instance shape instead, re-binding and
/// re-activating the one window.</para>
/// </summary>
internal sealed class UpdateDialog : Window
{
    private const int DialogWidth = 660;
    private const int DialogHeight = 514;

    /// <summary>Usable width inside the notes box, for the one control that
    /// cannot infer it: DialogWidth less the root padding (24+24), the box
    /// border (1+1), the scroller padding (14+14) and room for the vertical
    /// scrollbar. Only <see cref="BuildTable"/> reads it — everything else in
    /// the box is text, which wraps to whatever width it is given.</summary>
    private const int NotesContentWidth = DialogWidth - 48 - 2 - 28 - 12;
    private const int IconSize = 64;

    private static readonly FontFamily CodeFont = new("Consolas, Cascadia Mono, monospace");

    private static UpdateDialog? _shared;

    private readonly Image _icon = new()
    {
        Width = IconSize,
        Height = IconSize,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private readonly TextBlock _headline = new()
    {
        FontSize = 15,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly TextBlock _versionLine = new()
    {
        FontSize = 12,
        Opacity = 0.75,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
    };

    private readonly RichTextBlock _notes = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    private readonly Button _skip = new();
    private readonly Button _later = new();
    private readonly Button _install = new();

    // Progress replaces the notes box in place rather than opening a second
    // window as Sparkle's SUStatus does. Sparkle needs one because its alert
    // closes on Install; this window does not have to close, and inventing a
    // second window's layout from a xib — with no rendered reference to check
    // it against — is how the rest of this feature's mistakes were made.
    private readonly ProgressBar _progress = new()
    {
        IsIndeterminate = true,
        Margin = new Thickness(0, 14, 0, 0),
    };

    private readonly StackPanel _progressHost = new()
    {
        Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Border? _notesBox;
    private Grid? _footer;
    private static UpdateDialog? _active;

    // Rebound on every Present, so the buttons always act on the offer that is
    // on screen right now rather than on whatever the first Present captured.
    private Func<bool> _onInstall = () => true;
    private Func<bool> _onLater = () => true;
    private Func<bool> _onSkip = () => true;

    /// <summary>Show the dialog for one offer. Each handler returns whether the
    /// dialog should close: a handler that finds the pending update has moved
    /// under it re-presents instead and returns false.</summary>
    internal static void Present(
        UpdateOffer offer,
        Func<bool> install,
        Func<bool> later,
        Func<bool> skip)
    {
        _shared ??= new UpdateDialog();
        _shared.Bind(offer, install, later, skip);
        _shared.AppWindow.Show();
        // Activate() alone cannot bring the window forward when the opener has
        // no foreground rights — a tray-menu click is exactly that case, the
        // lesson SettingsWindow.Present already carries.
        _shared.AppWindow.MoveInZOrderAtTop();
        _shared.Activate();
        DevLog.Write($"update-dialog: shown v{offer.Version}");
    }

    private UpdateDialog()
    {
        Title = UpdateDialogText.Title();
        SystemBackdrop = new MicaBackdrop();

        var root = new Grid { Padding = new Thickness(24, 20, 24, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star),
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(BuildHeader());
        _notesBox = BuildNotesBox();
        Grid.SetRow(_notesBox, 1);
        root.Children.Add(_notesBox);
        _progressHost.Children.Add(_progress);
        Grid.SetRow(_progressHost, 1);
        root.Children.Add(_progressHost);
        _footer = BuildFooter();
        Grid.SetRow(_footer, 2);
        root.Children.Add(_footer);
        Content = root;

        // ApplicationIcon only reaches the executable; the title bar reads its
        // icon from the window. Fail soft, same as SettingsWindow.
        try
        {
            AppWindow.SetIcon(Path.Combine(
                AppContext.BaseDirectory, "Assets", "syrtis.ico"));
        }
        catch (Exception ex)
        {
            DevLog.Write($"update-dialog icon: {ex.Message}");
        }

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        ApplySize();
        // Something in the show path renormalizes the size on a non-96-DPI
        // monitor; re-apply once the window has its final DPI (SettingsWindow's
        // measured lesson).
        Activated += (_, _) => ApplySize();

        // The close box means Remind Me Later: hide, keep the pending action,
        // and keep the instance so the next Present re-binds this window.
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };
    }

    private Grid BuildHeader()
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });

        // The reference screen's icon is a rounded square; syrtis.ico is
        // already a disc, so clipping is what keeps the two consistent.
        var iconHost = new Border
        {
            Width = IconSize,
            Height = IconSize,
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = _icon,
        };
        _icon.ImageFailed += (_, e) =>
        {
            DevLog.Write($"update-dialog icon image: {e.ErrorMessage}");
            iconHost.Visibility = Visibility.Collapsed;
        };
        try
        {
            // The 256 px PNG (the .ico's largest frame), decoded at the drawn
            // size times the display scale. Given syrtis.ico instead, the
            // icon drew blurry at 100%, 150% and 200% (188, 2026-10-10).
            _icon.Source = new BitmapImage(new Uri("ms-appx:///Assets/syrtis-256.png"))
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = IconSize,
            };
        }
        catch (Exception ex)
        {
            DevLog.Write($"update-dialog icon source: {ex.Message}");
            iconHost.Visibility = Visibility.Collapsed;
        }

        header.Children.Add(iconHost);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        text.Children.Add(_headline);
        text.Children.Add(_versionLine);
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        return header;
    }

    private Border BuildNotesBox() => new()
    {
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14, 12, 14, 12),
            Content = _notes,
        },
    };

    private Grid BuildFooter()
    {
        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _skip.Content = UpdateDialogText.Skip();
        _skip.HorizontalAlignment = HorizontalAlignment.Left;
        _skip.Click += (_, _) => Run(_onSkip);
        footer.Children.Add(_skip);

        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _later.Content = UpdateDialogText.Later();
        _later.Click += (_, _) => Run(_onLater);
        group.Children.Add(_later);

        _install.Content = UpdateDialogText.Install();
        _install.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _install.Click += (_, _) => Run(_onInstall);
        group.Children.Add(_install);

        Grid.SetColumn(group, 1);
        footer.Children.Add(group);
        return footer;
    }

    private void Run(Func<bool> handler)
    {
        bool close;
        try
        {
            close = handler();
        }
        catch (Exception ex)
        {
            // A Click handler throws straight into the XAML dispatcher. The
            // pending action's own failures are already logged and restored by
            // TrayService; this only exists so the dialog cannot become the
            // thing that takes the app down.
            var type = ex.GetType().Name;
            DevLog.Write($"update-dialog: handler failed {type[..Math.Min(type.Length, 64)]}");
            close = true;
        }

        if (close)
        {
            AppWindow.Hide();
        }
    }

    private void Bind(
        UpdateOffer offer,
        Func<bool> install,
        Func<bool> later,
        Func<bool> skip)
    {
        _onInstall = install;
        _onLater = later;
        _onSkip = skip;
        _active = this;
        ShowOffer();
        _headline.Text = UpdateDialogText.Headline(ProductIdentity.Name);
        _versionLine.Text = UpdateDialogText.VersionLine(
            ProductIdentity.Name, offer.Version, offer.InstalledVersion);
        RenderNotes(ReleaseNotesMarkdown.Parse(offer.NotesMarkdown));
    }

    private void ShowOffer()
    {
        if (_notesBox is not null) { _notesBox.Visibility = Visibility.Visible; }
        if (_footer is not null) { _footer.Visibility = Visibility.Visible; }
        _progressHost.Visibility = Visibility.Collapsed;
        _progress.IsIndeterminate = true;
    }

    /// <summary>Switch this window from offering the update to reporting on it.
    ///
    /// <para>Pressing Install used to close the dialog and leave nothing: a
    /// download of unknown length, then the process exiting for around a minute
    /// while Velopack applies the update. The first person to try it reasonably
    /// concluded the app had died.</para>
    ///
    /// <para>The three buttons go rather than being disabled — none of them
    /// means anything once the download has started. The close box still works
    /// and only hides the window; the download is owned by App and continues.
    /// There is no Cancel: Sparkle has one, but adding a second exit path to a
    /// flow that has already produced four concurrency defects buys less than
    /// it costs, and the window can simply be closed.</para></summary>
    private void ShowProgress(string phase, int? percent)
    {
        DevLog.Write(
            $"update-dialog: progress phase=\"{phase}\" len={phase.Length} "
                + $"percent={percent?.ToString() ?? "-"} visible={AppWindow.IsVisible}");
        _headline.Text = phase;
        _versionLine.Text = percent is { } p
            ? UpdateDialogText.Percent(p)
            : UpdateDialogText.RestartNotice();
        if (_notesBox is not null) { _notesBox.Visibility = Visibility.Collapsed; }
        if (_footer is not null) { _footer.Visibility = Visibility.Collapsed; }
        _progressHost.Visibility = Visibility.Visible;
        // The window must be on screen for any of this to mean anything. Install
        // used to return "close" from its handler, which hid the window before
        // the first report arrived, so every phase rendered into a hidden
        // dialog — the exact no-feedback behaviour progress exists to remove.
        // The handler no longer does that; this is the second line of defence.
        if (!AppWindow.IsVisible)
        {
            AppWindow.Show();
        }

        if (percent is { } value)
        {
            _progress.IsIndeterminate = false;
            _progress.Value = Math.Clamp(value, 0, 100);
        }
        else
        {
            _progress.IsIndeterminate = true;
        }
    }

    /// <summary>Called by App as the download runs. A no-op when the window was
    /// never opened or has been closed — the download does not depend on it.</summary>
    internal static void Report(string phase, int? percent)
    {
        var dialog = _active;
        if (dialog is null)
        {
            return;
        }

        _ = dialog.DispatcherQueue.TryEnqueue(() => dialog.ShowProgress(phase, percent));
    }

    /// <summary>Only --update-dialog-demo uses this. The real flow never closes
    /// the dialog from the progress side: the process exiting is what takes it
    /// off screen.</summary>
    internal static void CloseIfOpen()
    {
        var dialog = _active;
        _ = dialog?.DispatcherQueue.TryEnqueue(() => dialog.AppWindow.Hide());
    }

    /// <summary>Turn the parsed block list into paragraphs. Everything that
    /// could be hostile has already been bounded and stripped by
    /// <see cref="ReleaseNotesMarkdown"/>; this only chooses type.</summary>
    private void RenderNotes(IReadOnlyList<NotesBlock> blocks)
    {
        _notes.Blocks.Clear();
        if (blocks.Count == 0)
        {
            // A first-class state, not an error path: notes are baked into the
            // nuspec at pack time and cannot be backfilled, so every version
            // published before this slice is in it.
            var empty = new Paragraph();
            empty.Inlines.Add(new Run { Text = UpdateDialogText.NoNotes() });
            _notes.Blocks.Add(empty);
            return;
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Kind == NotesBlockKind.TableRow)
            {
                // A run of adjacent rows is one table: the parser drops the
                // |---| row, so a blank line (or any other block) is the only
                // thing that ends one.
                var end = i;
                while (end + 1 < blocks.Count
                    && blocks[end + 1].Kind == NotesBlockKind.TableRow)
                {
                    end++;
                }

                _notes.Blocks.Add(WrapInline(BuildTable(blocks, i, end)));
                i = end;
                continue;
            }

            var paragraph = new Paragraph
            {
                Margin = block.Kind switch
                {
                    NotesBlockKind.Heading => new Thickness(0, 12, 0, 4),
                    NotesBlockKind.Bullet => new Thickness(14, 0, 0, 4),
                    _ => new Thickness(0, 0, 0, 8),
                },
            };
            if (block.Kind == NotesBlockKind.Heading)
            {
                paragraph.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                paragraph.FontSize = 13;
            }

            if (block.Kind == NotesBlockKind.Bullet)
            {
                paragraph.Inlines.Add(new Run { Text = "• " });
            }

            AppendRuns(paragraph.Inlines, block.Runs);
            _notes.Blocks.Add(paragraph);
        }
    }

    private static void AppendRuns(InlineCollection target, IReadOnlyList<NotesRun> runs)
    {
        foreach (var run in runs)
        {
            // Only the true cases are set: a Run inherits the paragraph's
            // weight otherwise, and writing Normal explicitly would strip
            // the bold off every heading.
            var inline = new Run { Text = run.Text };
            if (run.Bold)
            {
                inline.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            }

            if (run.Italic)
            {
                inline.FontStyle = Windows.UI.Text.FontStyle.Italic;
            }

            if (run.Code)
            {
                inline.FontFamily = CodeFont;
            }

            target.Add(inline);
        }
    }

    /// <summary>A RichTextBlock has no table primitive, so the table is a real
    /// Grid hosted in a one-inline paragraph.
    ///
    /// <para>Every column is Auto and the whole grid is capped at
    /// <see cref="NotesContentWidth"/> rather than starring the last column: an
    /// InlineUIContainer does not promise its child a finite available width,
    /// and a Star column measured against infinity collapses. Auto plus an
    /// explicit cap behaves the same either way — the table hugs its content,
    /// and only a table wider than the box makes its cells wrap.</para>
    /// </summary>
    private static Grid BuildTable(IReadOnlyList<NotesBlock> blocks, int first, int last)
    {
        var columns = 0;
        for (var r = first; r <= last; r++)
        {
            columns = Math.Max(columns, blocks[r].Cells?.Count ?? 0);
        }

        var grid = new Grid
        {
            Margin = new Thickness(0, 2, 0, 10),
            MaxWidth = NotesContentWidth,
        };
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var r = first; r <= last; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cells = blocks[r].Cells;
            if (cells is null)
            {
                continue;
            }

            for (var c = 0; c < cells.Count && c < columns; c++)
            {
                // An empty cell gets no control, which is what bounds this
                // loop. MaxCells caps a row at 12 and MaxBlocks caps the
                // document at 500, but their product is 6 000 controls on the
                // UI thread and no parser bound stands in the way: an empty
                // cell costs no runs, so `|x|||||||||||` × 500 passes every
                // one of them. Skipping empties makes every control cost at
                // least one run, so the document-wide total is bounded by
                // MaxRuns. A Grid cell with no child is empty space and an
                // Auto column with no content collapses, so nothing shifts —
                // and a row whose cells are all empty never reaches here, the
                // parser drops it.
                if (cells[c].Count == 0)
                {
                    continue;
                }

                var text = new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, c == columns - 1 ? 0 : 18, 3),
                };
                if (r == first)
                {
                    text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }

                AppendRuns(text.Inlines, cells[c]);
                Grid.SetRow(text, r - first);
                Grid.SetColumn(text, c);
                grid.Children.Add(text);
            }
        }

        return grid;
    }

    private static Paragraph WrapInline(UIElement element)
    {
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new InlineUIContainer { Child = element });
        return paragraph;
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
