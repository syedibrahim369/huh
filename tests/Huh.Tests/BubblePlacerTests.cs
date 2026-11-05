using Huh.Core.Placement;

namespace Huh.Tests;

public class BubblePlacerTests
{
    private const double W = 360, H = 200; // bubble size in DIPs

    // The real dev machine: 1920x1200 at 125%, taskbar 60 px.
    private static readonly MonitorGeometry Laptop125 = new(new(0, 0, 1920, 1200), new(0, 0, 1920, 1140), 120);

    private static void AssertInsideWork(BubblePlacement p) => Assert.True(p.Monitor.WorkArea.Contains(p.Rect), $"{p.Rect} not inside {p.Monitor.WorkArea}");

    [Fact]
    public void Centered_dialog_gets_bubble_on_the_right_scaled_to_125_percent()
    {
        var dialog = PxRect.FromSize(700, 400, 500, 220);
        var p = BubblePlacer.Place(dialog, W, H, [Laptop125]);
        Assert.Equal(BubbleSide.Right, p.Side);
        Assert.Equal(450, p.Rect.Width);   // 360 * 1.25
        Assert.Equal(250, p.Rect.Height);  // 200 * 1.25
        Assert.Equal(dialog.Right + 13, p.Rect.Left); // 10 DIP gap -> 12.5 -> 13 px (midpoint rounds away from zero)
        Assert.Equal(dialog.Top, p.Rect.Top);
        AssertInsideWork(p);
    }

    [Fact]
    public void Dialog_at_right_edge_puts_bubble_on_the_left()
    {
        var p = BubblePlacer.Place(PxRect.FromSize(1400, 300, 500, 220), W, H, [Laptop125]);
        Assert.Equal(BubbleSide.Left, p.Side);
        Assert.True(p.Rect.Right <= 1400);
        AssertInsideWork(p);
    }

    [Fact]
    public void Wide_dialog_puts_bubble_below()
    {
        var p = BubblePlacer.Place(PxRect.FromSize(100, 100, 1700, 400), W, H, [Laptop125]);
        Assert.Equal(BubbleSide.Below, p.Side);
        AssertInsideWork(p);
    }

    [Fact]
    public void Wide_dialog_near_bottom_puts_bubble_above()
    {
        var p = BubblePlacer.Place(PxRect.FromSize(100, 700, 1700, 400), W, H, [Laptop125]);
        Assert.Equal(BubbleSide.Above, p.Side);
        AssertInsideWork(p);
    }

    [Fact]
    public void Huge_dialog_overlaps_its_corner_inside_the_work_area()
    {
        var p = BubblePlacer.Place(new PxRect(0, 0, 1920, 1140), W, H, [Laptop125]);
        Assert.Equal(BubbleSide.Overlay, p.Side);
        AssertInsideWork(p);
    }

    [Fact]
    public void Dialog_near_bottom_slides_bubble_up_but_never_under_the_taskbar()
    {
        var p = BubblePlacer.Place(PxRect.FromSize(600, 1000, 400, 130), W, H, [Laptop125]);
        Assert.Equal(BubbleSide.Right, p.Side);
        Assert.True(p.Rect.Bottom <= 1140 - 10); // 8 DIP margin = 10 px
        AssertInsideWork(p);
    }

    // ---------------- mixed scale (fake geometry; dev machine has a single monitor) ----------------

    // 150% laptop on the left, 100% 1440p monitor on the right, top-aligned.
    private static readonly MonitorGeometry A150 = new(new(0, 0, 1920, 1200), new(0, 0, 1920, 1152), 144);
    private static readonly MonitorGeometry B100 = new(new(1920, 0, 4480, 1440), new(1920, 0, 4480, 1400), 96);

    [Fact]
    public void Bubble_spilling_onto_a_100_percent_monitor_uses_that_monitors_scale()
    {
        var dialog = PxRect.FromSize(1500, 300, 400, 200); // right edge at 1900, 20 px from the boundary
        var p = BubblePlacer.Place(dialog, W, H, [A150, B100]);
        Assert.Equal(BubbleSide.Right, p.Side);
        Assert.Same(B100, p.Monitor);
        Assert.Equal(360, p.Rect.Width); // 100% on B, not 540 (150%)
        Assert.Equal(200, p.Rect.Height);
        Assert.Equal(1920 + 8, p.Rect.Left); // pushed fully onto B (8 DIP margin at 100%)
        AssertInsideWork(p);
    }

    [Fact]
    public void Bubble_on_the_150_percent_monitor_is_scaled_up()
    {
        var dialog = PxRect.FromSize(2000, 300, 400, 200); // on B, right side has room
        var p = BubblePlacer.Place(dialog, W, H, [B100, A150]);
        Assert.Same(B100, p.Monitor);
        Assert.Equal(360, p.Rect.Width);

        var onA = BubblePlacer.Place(PxRect.FromSize(200, 300, 400, 200), W, H, [A150, B100]);
        Assert.Same(A150, onA.Monitor);
        Assert.Equal(540, onA.Rect.Width);
    }

    [Fact]
    public void Secondary_monitor_left_of_primary_with_negative_coordinates()
    {
        var left = new MonitorGeometry(new(-2560, 0, 0, 1440), new(-2560, 0, 0, 1400), 96);
        var dialog = PxRect.FromSize(-600, 500, 500, 200); // right edge at -100: bubble would straddle into primary
        var p = BubblePlacer.Place(dialog, W, H, [Laptop125, left]);
        AssertInsideWork(p);
        Assert.True(p.Rect.IntersectionArea(dialog) == 0);
    }

    [Fact]
    public void Vertically_offset_monitor_clamps_into_its_own_work_area()
    {
        // B sits lower than A; dialog near the top of A at its right edge -> bubble lands on B, must be pushed down into B.
        var bLow = new MonitorGeometry(new(1920, 600, 3840, 1680), new(1920, 600, 3840, 1640), 96);
        var dialog = PxRect.FromSize(1500, 500, 400, 300);
        var p = BubblePlacer.Place(dialog, W, H, [A150, bLow]);
        AssertInsideWork(p);
    }

    [Fact]
    public void Dialog_straddling_two_monitors_uses_the_one_showing_most_of_it()
    {
        var dialog = new PxRect(1700, 300, 2300, 500); // 220 px on A, 380 px on B
        Assert.Same(B100, BubblePlacer.MonitorFor(dialog, [A150, B100]));
    }

    [Fact]
    public void Off_screen_dialog_falls_back_to_nearest_monitor()
    {
        var p = BubblePlacer.Place(PxRect.FromSize(5000, 300, 300, 200), W, H, [A150, B100]);
        Assert.Same(B100, p.Monitor);
        AssertInsideWork(p);
    }
}
