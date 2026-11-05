using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;
using Huh.Core.Interop;
using Huh.Core.Placement;

namespace Huh.Core.Windowing;

/// <summary>Monitor geometry and window placement helpers. All coordinates are physical pixels (caller must be per-monitor-v2 aware).</summary>
public static unsafe class Screen
{
    public static IReadOnlyList<MonitorGeometry> GetMonitors()
    {
        var list = new List<MonitorGeometry>();
        var handle = GCHandle.Alloc(list);
        try { PInvoke.EnumDisplayMonitors(HDC.Null, null, &OnMonitor, (LPARAM)GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
        return list;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL OnMonitor(HMONITOR mon, HDC hdc, RECT* rc, LPARAM data)
    {
        var list = (List<MonitorGeometry>)GCHandle.FromIntPtr(data).Target!;
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (PInvoke.GetMonitorInfo(mon, &mi))
        {
            uint dpiX = 96, dpiY = 96;
            PInvoke.GetDpiForMonitor(mon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dpiX, &dpiY);
            list.Add(new MonitorGeometry(ToPx(mi.rcMonitor), ToPx(mi.rcWork), dpiX));
        }
        return true;
    }

    /// <summary>Visible frame of a window (DWM extended frame bounds: excludes the invisible resize border).</summary>
    public static PxRect? FrameBounds(nint hwnd)
    {
        RECT r;
        if (PInvoke.DwmGetWindowAttribute((HWND)hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT)).Succeeded)
            return ToPx(r);
        return PInvoke.GetWindowRect((HWND)hwnd, &r) ? ToPx(r) : null;
    }

    public static bool IsMinimized(nint hwnd) => PInvoke.IsIconic((HWND)hwnd);
    public static bool IsVisible(nint hwnd) => PInvoke.IsWindow((HWND)hwnd) && PInvoke.IsWindowVisible((HWND)hwnd);
    public static nint Foreground => (nint)PInvoke.GetForegroundWindow();

    /// <summary>
    /// Moves/sizes <paramref name="window"/> and puts it directly above <paramref name="anchor"/> in z-order, without activating it.
    /// If the anchor is topmost (system-modal / MB_TOPMOST dialogs) the window becomes topmost too; otherwise it stays in the normal
    /// band so it never floats over unrelated apps when the user switches away from the dialog.
    /// </summary>
    public static void PlaceAbove(nint window, nint anchor, PxRect visibleRect)
    {
        var w = (HWND)window;
        var a = (HWND)anchor;
        var rect = OuterRectFor(window, visibleRect);
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;

        HWND insertAfter;
        if (IsTopmost(a))
        {
            insertAfter = HWND.HWND_TOPMOST;
        }
        else
        {
            if (IsTopmost(w)) PInvoke.SetWindowPos(w, HWND.HWND_NOTOPMOST, 0, 0, 0, 0, flags | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
            var prev = PInvoke.GetWindow(a, GET_WINDOW_CMD.GW_HWNDPREV);
            if (prev == w)
            {
                PInvoke.SetWindowPos(w, HWND.Null, rect.Left, rect.Top, rect.Width, rect.Height, flags | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
                return;
            }
            // Inserting after a topmost window would pull us into the topmost band; HWND_TOP is "just above the anchor" then.
            insertAfter = prev.IsNull || IsTopmost(prev) ? HWND.HWND_TOP : prev;
        }
        PInvoke.SetWindowPos(w, insertAfter, rect.Left, rect.Top, rect.Width, rect.Height, flags);
    }

    /// <summary>
    /// Turns a top-level window into a bubble: never activated by clicks (WS_EX_NOACTIVATE), not in the taskbar/Alt+Tab
    /// (WS_EX_TOOLWINDOW), with Windows 11 rounded corners. Call after any AppWindow presenter change (presenters reset styles).
    /// </summary>
    public static void MakeBubbleWindow(nint hwnd)
    {
        var h = (HWND)hwnd;
        var ex = Native.ExStyle(h) | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        ex &= ~WINDOW_EX_STYLE.WS_EX_APPWINDOW;
        PInvoke.SetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)ex);
        var corner = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        PInvoke.DwmSetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        PInvoke.SetWindowPos(h, HWND.Null, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
    }

    /// <summary>Shows a window without activating it (never steals focus from the dialog the user is reading).</summary>
    public static void ShowNoActivate(nint hwnd) => PInvoke.ShowWindow((HWND)hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

    public static void Hide(nint hwnd) => PInvoke.ShowWindow((HWND)hwnd, SHOW_WINDOW_CMD.SW_HIDE);

    /// <summary>
    /// SetWindowPos works on the outer window rect, which includes the invisible resize borders DWM draws around bordered windows
    /// (8 px at 125% on the left/right/bottom). Expand the desired visible rect by the window's current insets.
    /// </summary>
    private static PxRect OuterRectFor(nint window, PxRect visible)
    {
        RECT outer;
        var frame = FrameBounds(window);
        if (frame is null || !PInvoke.GetWindowRect((HWND)window, &outer)) return visible;
        int l = frame.Value.Left - outer.left, t = frame.Value.Top - outer.top;
        int r = outer.right - frame.Value.Right, b = outer.bottom - frame.Value.Bottom;
        if (l < 0 || t < 0 || r < 0 || b < 0 || l > 32 || t > 32 || r > 32 || b > 32) return visible; // not laid out yet
        return new PxRect(visible.Left - l, visible.Top - t, visible.Right + r, visible.Bottom + b);
    }

    private static bool IsTopmost(HWND h) => (Native.ExStyle(h) & WINDOW_EX_STYLE.WS_EX_TOPMOST) != 0;

    private static PxRect ToPx(RECT r) => new(r.left, r.top, r.right, r.bottom);
}
