namespace TokenBar.Core.Tests;

// G5k: hover tooltip placement against macOS PopoverTooltipPlacement.offset
// (Views/Cards.swift:15-118). Expected values are worked by hand from that
// function.
public class TooltipPlacementTests
{
    private static readonly Box Window = new(0, 0, 400, 640);
    private static readonly Box Viewport = new(0, 60, 400, 500);
    private static readonly Box Card = new(50, 80, 300, 200);

    [Fact]
    public void CentresOnThePointerAndSitsBelowInTheUpperRegion() =>
        Assert.Equal((150.0, 112.0),
            TooltipPlacement.Origin(200, 100, 100, 50, Card, Viewport, Window));

    [Fact]
    public void SitsAboveThePointerInTheLowerRegion() =>
        Assert.Equal((150.0, 188.0),
            TooltipPlacement.Origin(200, 250, 100, 50, Card, Viewport, Window));

    // The viewport ends where the footer begins; with no room below or above
    // the tooltip is clamped so its bottom stays on the viewport's edge.
    [Fact]
    public void NeverHangsBelowTheViewportIntoTheFooter()
    {
        var short_ = new Box(0, 60, 400, 100);

        var at = TooltipPlacement.Origin(200, 100, 100, 50, Card, short_, Window);

        Assert.Equal((150.0, 106.0), at);
        Assert.True(at!.Value.Y + 50 <= short_.Bottom - TooltipPlacement.EdgeInset);
    }

    [Fact]
    public void ClampsInsideTheHoveredCardHorizontally() =>
        Assert.Equal(250.0,
            TooltipPlacement.Origin(390, 100, 100, 50, Card, Viewport, Window)!.Value.X);

    // A pointer over chrome outside the scroll area (a tab button) is placed
    // in the whole window; a button narrower than the tooltip does not pin it.
    [Fact]
    public void FallsBackToTheWindowOutsideTheViewport() =>
        Assert.Equal((150.0, 32.0),
            TooltipPlacement.Origin(200, 20, 100, 50, new Box(180, 10, 40, 20), Viewport, Window));

    [Fact]
    public void ATooltipTallerThanTheViewportStartsAtItsTop() =>
        Assert.Equal(64.0,
            TooltipPlacement.Origin(200, 100, 100, 600, Card, Viewport, Window)!.Value.Y);

    [Fact]
    public void AnUnmeasuredTooltipIsNotPlaced() =>
        Assert.Null(TooltipPlacement.Origin(200, 100, 0, 50, Card, Viewport, Window));
}
