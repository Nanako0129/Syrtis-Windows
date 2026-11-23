using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TokenBar.Core;

namespace TokenBar.App;

/// <summary>A short device status: a small dot, then the phrase (macOS
/// DeviceShortStatusView). One drawing for the device menu, the per-device card
/// and Settings → Devices. The dot is decoration: hidden from accessibility,
/// the text carries the state. The text is another device's data in part
/// (names inside a status), so it is only ever <c>TextBlock.Text</c>. Controls
/// are made once and updated in place by <see cref="Update"/>, never
/// re-parented.</summary>
public sealed class DeviceStatusView
{
    private const double DotSide = 7;

    // Green connected, orange trouble (the pace orange of the other state
    // notices), grey outline not shown or stopped, dotted outline updating.
    private const string Green = "#34C759";

    private readonly Ellipse _dot = new() { Width = DotSide, Height = DotSide, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text;

    public DeviceStatusView(double textSize = 10)
    {
        AutomationProperties.SetAccessibilityView(_dot, AccessibilityView.Raw);
        _text = Ui.Text("", textSize, 0.6);
        Root = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        Root.Children.Add(_dot);
        Root.Children.Add(_text);
    }

    public StackPanel Root { get; }

    public void Update(DeviceOverlayCopy.ShortStatusResult status)
    {
        _text.Text = status.Text;
        _dot.StrokeDashArray = null;
        _dot.StrokeThickness = 0;
        _dot.Fill = null;
        _dot.Stroke = null;
        switch (status.Dot)
        {
            case DeviceOverlayCopy.ShortDot.Green:
                _dot.Fill = Ui.BrushFromHex(Green);
                break;
            case DeviceOverlayCopy.ShortDot.Orange:
                _dot.Fill = Ui.BrushFromHex(DashboardView.PaceOrange);
                break;
            default:
                _dot.Stroke = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                _dot.StrokeThickness = 1;
                if (status.Dot == DeviceOverlayCopy.ShortDot.Progress)
                {
                    _dot.StrokeDashArray = new DoubleCollection { 1.5, 1.5 };
                }

                break;
        }
    }

    public static StackPanel Make(DeviceOverlayCopy.ShortStatusResult status, double textSize = 10)
    {
        var view = new DeviceStatusView(textSize);
        view.Update(status);
        return view.Root;
    }
}
