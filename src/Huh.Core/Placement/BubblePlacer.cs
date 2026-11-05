namespace Huh.Core.Placement;

/// <summary>Rectangle in physical pixels (virtual-screen coordinates, as seen by a per-monitor-v2 DPI-aware process).</summary>
public readonly record struct PxRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public static PxRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);
    public bool Contains(PxRect r) => r.Left >= Left && r.Top >= Top && r.Right <= Right && r.Bottom <= Bottom;
    public long IntersectionArea(PxRect r)
    {
        long w = Math.Min(Right, r.Right) - Math.Max(Left, r.Left);
        long h = Math.Min(Bottom, r.Bottom) - Math.Max(Top, r.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }
    public (int X, int Y) Center => ((Left + Right) / 2, (Top + Bottom) / 2);
    public bool ContainsPoint(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

/// <summary>A monitor: full bounds, work area (minus taskbar) and effective DPI (96 = 100%).</summary>
public sealed record MonitorGeometry(PxRect Bounds, PxRect WorkArea, uint Dpi)
{
    public double Scale => Dpi / 96.0;
}

public enum BubbleSide { Right, Left, Below, Above, Overlay }

public sealed record BubblePlacement(PxRect Rect, BubbleSide Side, MonitorGeometry Monitor);

/// <summary>
/// Pure placement logic. The bubble size is given in DIPs and converted with the DPI of the monitor the bubble lands on,
/// which matters when the dialog sits at the edge of one monitor and the bubble spills onto a monitor with a different scale.
/// Preference: right of the dialog, then left, below, above; otherwise overlap the dialog's corner inside the work area.
/// </summary>
public static class BubblePlacer
{
    public const double GapDip = 10;
    public const double MarginDip = 8;

    public static BubblePlacement Place(PxRect dialog, double widthDip, double heightDip, IReadOnlyList<MonitorGeometry> monitors)
    {
        if (monitors.Count == 0) throw new ArgumentException("No monitors.", nameof(monitors));
        var home = MonitorFor(dialog, monitors);

        foreach (var side in (ReadOnlySpan<BubbleSide>)[BubbleSide.Right, BubbleSide.Left, BubbleSide.Below, BubbleSide.Above])
        {
            var p = TrySide(side, dialog, widthDip, heightDip, home, monitors);
            if (p is not null) return p;
        }

        // Nothing fits outside the dialog (dialog nearly fills the screen): overlap its bottom-right corner.
        int w = Px(widthDip, home), h = Px(heightDip, home), m = Px(MarginDip, home);
        var work = home.WorkArea;
        int left = Clamp(dialog.Right - w - m, work.Left + m, work.Right - m - w);
        int top = Clamp(dialog.Bottom - h - m, work.Top + m, work.Bottom - m - h);
        return new BubblePlacement(PxRect.FromSize(left, top, Math.Min(w, work.Width), Math.Min(h, work.Height)), BubbleSide.Overlay, home);
    }

    private static BubblePlacement? TrySide(BubbleSide side, PxRect d, double widthDip, double heightDip,
        MonitorGeometry home, IReadOnlyList<MonitorGeometry> monitors)
    {
        var target = home;
        PxRect rect = default;
        // Size depends on the target monitor's DPI and the target depends on the rect: two passes settle it.
        for (int pass = 0; pass < 2; pass++)
        {
            int w = Px(widthDip, target), h = Px(heightDip, target), gap = Px(GapDip, target);
            rect = side switch
            {
                BubbleSide.Right => PxRect.FromSize(d.Right + gap, d.Top, w, h),
                BubbleSide.Left => PxRect.FromSize(d.Left - gap - w, d.Top, w, h),
                BubbleSide.Below => PxRect.FromSize(d.Left, d.Bottom + gap, w, h),
                _ => PxRect.FromSize(d.Left, d.Top - gap - h, w, h),
            };
            var (cx, cy) = rect.Center;
            var next = monitors.FirstOrDefault(mon => mon.Bounds.ContainsPoint(cx, cy));
            if (next is null) return null; // would land off-screen
            if (next == target) break;
            target = next;
        }

        // Slide along the dialog edge to stay inside the work area.
        int margin = Px(MarginDip, target);
        var work = target.WorkArea;
        // When the bubble spills onto a neighbouring monitor it may straddle the boundary: push it fully onto that monitor,
        // moving AWAY from the dialog only (never back over it).
        if (side is BubbleSide.Right or BubbleSide.Left)
        {
            int left = side == BubbleSide.Right
                ? Math.Max(rect.Left, work.Left + margin)
                : Math.Min(rect.Left, work.Right - margin - rect.Width);
            rect = PxRect.FromSize(left, Clamp(rect.Top, work.Top + margin, work.Bottom - margin - rect.Height), rect.Width, rect.Height);
        }
        else
        {
            int top = side == BubbleSide.Below
                ? Math.Max(rect.Top, work.Top + margin)
                : Math.Min(rect.Top, work.Bottom - margin - rect.Height);
            rect = PxRect.FromSize(Clamp(rect.Left, work.Left + margin, work.Right - margin - rect.Width), top, rect.Width, rect.Height);
        }

        return work.Contains(rect) ? new BubblePlacement(rect, side, target) : null;
    }

    /// <summary>The monitor showing most of the dialog (falls back to the nearest one by center distance).</summary>
    public static MonitorGeometry MonitorFor(PxRect r, IReadOnlyList<MonitorGeometry> monitors)
    {
        var best = monitors.MaxBy(m => m.Bounds.IntersectionArea(r))!;
        if (best.Bounds.IntersectionArea(r) > 0) return best;
        var (cx, cy) = r.Center;
        return monitors.MinBy(m => { var (mx, my) = m.Bounds.Center; return (long)(mx - cx) * (mx - cx) + (long)(my - cy) * (my - cy); })!;
    }

    public static int Px(double dip, MonitorGeometry m) => (int)Math.Round(dip * m.Scale, MidpointRounding.AwayFromZero);

    private static int Clamp(int v, int lo, int hi) => hi < lo ? lo : Math.Clamp(v, lo, hi);
}
