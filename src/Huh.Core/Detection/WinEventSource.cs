using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Huh.Core.Interop;

namespace Huh.Core.Detection;

/// <summary>Kind of window-level WinEvent we forward.</summary>
internal enum WinEventKind { Shown, DialogStart, Alert, Hidden, Destroyed }

/// <summary>
/// Out-of-context WinEvent hooks on a dedicated thread with its own message loop.
/// The callback only filters and forwards; all real work happens on the monitor's worker thread
/// (hook callbacks must return quickly or the system starts dropping them).
/// </summary>
internal sealed unsafe class WinEventSource : IDisposable
{
    private const uint EVENT_SYSTEM_ALERT = 0x0002;
    private const uint EVENT_SYSTEM_DIALOGSTART = 0x0010;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint WM_QUIT = 0x0012;

    private static WinEventSource? s_current; // the static callback needs a way back to the instance

    private readonly Action<nint, WinEventKind, int> _sink;
    private Thread? _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _started = new();

    public WinEventSource(Action<nint, WinEventKind, int> sink) => _sink = sink;

    public void Start()
    {
        if (Interlocked.CompareExchange(ref s_current, this, null) is not null)
            throw new InvalidOperationException("Only one WinEventSource per process.");
        _thread = new Thread(Run) { IsBackground = true, Name = "huh-winevents" };
        _thread.Start();
        _started.Wait();
    }

    private void Run()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        delegate* unmanaged[Stdcall]<HWINEVENTHOOK, uint, HWND, int, int, uint, uint, void> proc = &Callback;
        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        UnhookWinEventSafeHandle[] hooks =
        [
            PInvoke.SetWinEventHook(EVENT_SYSTEM_ALERT, EVENT_SYSTEM_ALERT, null, proc, 0, 0, flags),
            PInvoke.SetWinEventHook(EVENT_SYSTEM_DIALOGSTART, EVENT_SYSTEM_DIALOGSTART, null, proc, 0, 0, flags),
            PInvoke.SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, null, proc, 0, 0, flags),
        ];
        _started.Set();

        // Standard message loop: out-of-context WinEvents are delivered as messages to this thread.
        Windows.Win32.UI.WindowsAndMessaging.MSG msg;
        while (PInvoke.GetMessage(&msg, HWND.Null, 0, 0) > 0)
        {
            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }

        foreach (var h in hooks) h.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Callback(HWINEVENTHOOK hook, uint ev, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            var self = s_current;
            if (self is null || hwnd.IsNull) return;

            switch (ev)
            {
                case EVENT_SYSTEM_DIALOGSTART:
                    self._sink(hwnd, WinEventKind.DialogStart, 0);
                    break;
                case EVENT_SYSTEM_ALERT:
                    self._sink(hwnd, WinEventKind.Alert, idChild);
                    break;
                case EVENT_OBJECT_SHOW or EVENT_OBJECT_HIDE or EVENT_OBJECT_DESTROY:
                    // Only whole top-level windows; ignore the flood of child-control/caret/cursor events.
                    if (idObject != 0 /* OBJID_WINDOW */ || idChild != 0 /* CHILDID_SELF */) return;
                    if (ev == EVENT_OBJECT_SHOW)
                    {
                        if (Native.IsTopLevel(hwnd)) self._sink(hwnd, WinEventKind.Shown, 0);
                    }
                    else
                    {
                        self._sink(hwnd, ev == EVENT_OBJECT_HIDE ? WinEventKind.Hidden : WinEventKind.Destroyed, 0);
                    }
                    break;
            }
        }
        catch
        {
            // Never let an exception cross the native boundary.
        }
    }

    public void Dispose()
    {
        if (_thread is null) return;
        PInvoke.PostThreadMessage(_threadId, WM_QUIT, default, default);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        Interlocked.CompareExchange(ref s_current, null, this);
    }
}
