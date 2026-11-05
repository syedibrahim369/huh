using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Huh.Core.Windowing;

/// <summary>
/// Follows one window (a dialog) with out-of-context WinEvent hooks scoped to its process/thread:
/// moves/resizes (EVENT_OBJECT_LOCATIONCHANGE), minimize/restore, and global foreground changes (for z-order).
/// Must be created on a thread that pumps messages (the UI thread); events are raised on that same thread.
/// </summary>
public sealed unsafe class WindowFollower : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0;

    // Hook handle -> follower. Only touched on the creating (UI) thread, but guard anyway.
    private static readonly Dictionary<nint, WindowFollower> s_byHook = [];

    public nint Target { get; }
    public event Action? Moved;
    public event Action? Minimized;
    public event Action? Restored;
    public event Action<nint>? ForegroundChanged;
    /// <summary>The window was hidden (e.g. its owner was minimized) or shown again. Destruction is reported by DialogMonitor.</summary>
    public event Action? Hidden;
    public event Action? Shown;

    private readonly List<UnhookWinEventSafeHandle> _hooks = [];

    public WindowFollower(nint target)
    {
        Target = target;
        uint pid;
        uint tid = PInvoke.GetWindowThreadProcessId((HWND)target, &pid);
        delegate* unmanaged[Stdcall]<HWINEVENTHOOK, uint, HWND, int, int, uint, uint, void> cb = &Callback;
        Add(PInvoke.SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, null, cb, pid, tid, WINEVENT_OUTOFCONTEXT));
        Add(PInvoke.SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE, null, cb, pid, tid, WINEVENT_OUTOFCONTEXT));
        Add(PInvoke.SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, null, cb, pid, 0, WINEVENT_OUTOFCONTEXT));
        Add(PInvoke.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, null, cb, 0, 0, WINEVENT_OUTOFCONTEXT));
    }

    private void Add(UnhookWinEventSafeHandle h)
    {
        if (h.IsInvalid) return;
        _hooks.Add(h);
        lock (s_byHook) s_byHook[h.DangerousGetHandle()] = this;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Callback(HWINEVENTHOOK hook, uint ev, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            WindowFollower? f;
            lock (s_byHook) s_byHook.TryGetValue((nint)hook.Value, out f);
            if (f is null) return;

            switch (ev)
            {
                case EVENT_SYSTEM_FOREGROUND:
                    f.ForegroundChanged?.Invoke((nint)hwnd);
                    break;
                case EVENT_OBJECT_LOCATIONCHANGE when (nint)hwnd == f.Target && idObject == 0 && idChild == 0:
                    f.Moved?.Invoke();
                    break;
                case EVENT_OBJECT_HIDE when (nint)hwnd == f.Target && idObject == 0 && idChild == 0:
                    f.Hidden?.Invoke();
                    break;
                case EVENT_OBJECT_SHOW when (nint)hwnd == f.Target && idObject == 0 && idChild == 0:
                    f.Shown?.Invoke();
                    break;
                case EVENT_SYSTEM_MINIMIZESTART when (nint)hwnd == f.Target || IsOwnedBy(f.Target, hwnd):
                    f.Minimized?.Invoke();
                    break;
                case EVENT_SYSTEM_MINIMIZEEND when (nint)hwnd == f.Target || IsOwnedBy(f.Target, hwnd):
                    f.Restored?.Invoke();
                    break;
            }
        }
        catch { /* never throw across the native boundary */ }
    }

    // A dialog disappears with its owner when the owner window is minimized.
    private static bool IsOwnedBy(nint dialog, HWND candidateOwner) =>
        PInvoke.GetWindow((HWND)dialog, Windows.Win32.UI.WindowsAndMessaging.GET_WINDOW_CMD.GW_OWNER) == candidateOwner;

    public void Dispose()
    {
        foreach (var h in _hooks)
        {
            lock (s_byHook) s_byHook.Remove(h.DangerousGetHandle());
            h.Dispose();
        }
        _hooks.Clear();
    }
}
