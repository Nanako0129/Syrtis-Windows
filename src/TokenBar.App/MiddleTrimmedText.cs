using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TokenBar.Core;
using Windows.Foundation;

namespace TokenBar.App;

/// <summary>One line of text truncated in the middle to the width it is
/// given (macOS <c>.lineLimit(1).truncationMode(.middle)</c>). WinUI's
/// <c>TextTrimming</c> only trims the end; this measures candidate strings
/// through <see cref="MiddleEllipsis.Fit"/> instead. It must sit where its
/// width is bounded (a star column, a vertical stack): measured unbounded, as
/// in a horizontal StackPanel, nothing is ever cut.</summary>
public sealed partial class MiddleTrimmedText : Panel
{
    private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);
    private readonly TextBlock _text;
    private double _fittedWidth = double.NaN;

    public MiddleTrimmedText(TextBlock text)
    {
        _text = text;
        _text.TextTrimming = TextTrimming.None;
        _text.TextWrapping = TextWrapping.NoWrap;
        Full = text.Text;
        // Screen readers get the whole id, not the truncated one: two dated
        // model ids must stay distinguishable there too.
        AutomationProperties.SetName(_text, Full);
        Children.Add(_text);
    }

    /// <summary>The untruncated text.</summary>
    public string Full { get; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;
        // Refit on a new width, or when the cached cut no longer fits at the
        // same width (text scale or a font fallback changed the metrics) —
        // unless it is already the bare "…": nothing shorter exists, and
        // refitting it on every measure would never settle.
        _text.Measure(Unbounded);
        if (width != _fittedWidth
            || (_text.DesiredSize.Width > width && _text.Text != MiddleEllipsis.Ellipsis))
        {
            _fittedWidth = width;
            _text.Text = double.IsInfinity(width)
                ? Full
                : MiddleEllipsis.Fit(Full, candidate =>
                {
                    _text.Text = candidate;
                    _text.Measure(Unbounded);
                    return _text.DesiredSize.Width <= width;
                });
        }

        _text.Measure(Unbounded);
        var desired = _text.DesiredSize;
        return new Size(Math.Min(desired.Width, width), desired.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _text.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        // Even "…" alone can be wider than a very narrow slot; never paint
        // into the neighbouring column.
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, finalSize.Width, finalSize.Height) };
        return finalSize;
    }
}
