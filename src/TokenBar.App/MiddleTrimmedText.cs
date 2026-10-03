using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        Children.Add(_text);
    }

    /// <summary>The untruncated text.</summary>
    public string Full { get; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;
        if (width != _fittedWidth)
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
        return finalSize;
    }
}
