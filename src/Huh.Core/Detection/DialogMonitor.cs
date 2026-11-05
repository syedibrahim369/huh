using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Huh.Core.Dialogs;
using Huh.Core.Interop;

namespace Huh.Core.Detection;

public sealed class DialogMonitorOptions
{
    /// <summary>Also subscribe to UIA WindowOpened (catches dialogs that don't raise WinEvents the usual way).</summary>
    public bool UseUiAutomationEvents { get; init; } = true;
    /// <summary>Wait this long after a window appears before reading it, so its text is populated.</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromMilliseconds(120);
    /// <summary>The same window, or the same (process, title, message), is reported at most once in this window.</summary>
    public TimeSpan DuplicateWindow { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// System-wide dialog detector. Two sources feed one worker queue:
///  - out-of-context WinEvent hooks (DIALOGSTART / ALERT / top-level SHOW / HIDE / DESTROY), and
///  - UI Automation WindowOpened on the desktop root (subtree).
/// A single MTA worker thread de-duplicates, lets the window settle, reads it via UIA, classifies it and raises
/// <see cref="DialogDetected"/> for every dialog (consumers filter on <see cref="DialogInfo.IsError"/>).
/// Events are raised on the worker thread.
/// </summary>
public sealed class DialogMonitor : IDisposable
{
    public event Action<DialogInfo>? DialogDetected;
    public event Action<nint>? DialogClosed;
    /// <summary>Diagnostics (failures, skipped windows). Never contains dialog text.</summary>
    public event Action<string>? Log;

    private abstract record Work(long Timestamp);
    private sealed record WindowWork(nint Hwnd, WinEventKind Kind, DetectionSource Source, bool? UiaIsDialog, long Timestamp) : Work(Timestamp);
    private sealed record ElementWork(IUIAutomationElement Element, int Pid, long Timestamp) : Work(Timestamp);
    private sealed record ClosedWork(nint Hwnd, long Timestamp) : Work(Timestamp);

    /// <summary>Non-#32770 window classes that host system dialogs (shell file-operation conflicts: "File In Use", "Access Denied").</summary>
    private static readonly HashSet<string> KnownDialogClasses = new(StringComparer.Ordinal) { "OperationStatusWindow" };

    private readonly DialogMonitorOptions _options;
    private readonly BlockingCollection<Work> _queue = new();
    private readonly int _ownPid = Environment.ProcessId;
    private readonly HashSet<int> _excludedPids = [];
    private WinEventSource? _winEvents;
    private Thread? _worker;
    private readonly ManualResetEventSlim _ready = new();
    private Exception? _startError;

    // Worker-thread-only state.
    private readonly Dictionary<nint, long> _seenWindows = [];
    private readonly Dictionary<string, long> _seenSignatures = [];
    private readonly HashSet<nint> _tracked = [];

    public DialogMonitor(DialogMonitorOptions? options = null) => _options = options ?? new();

    /// <summary>Ignore dialogs from these processes (huh's own UI is always ignored).</summary>
    public void ExcludeProcess(int pid) { lock (_excludedPids) _excludedPids.Add(pid); }

    public void Start()
    {
        _worker = new Thread(WorkerMain) { IsBackground = true, Name = "huh-dialog-worker" };
        _worker.SetApartmentState(ApartmentState.MTA); // UIA client calls must not run on an STA/UI thread
        _worker.Start();
        _ready.Wait();
        if (_startError is not null) throw new InvalidOperationException("UI Automation failed to start.", _startError);

        _winEvents = new WinEventSource((hwnd, kind, detail) =>
        {
            long now = Stopwatch.GetTimestamp();
            _queue.Add(kind is WinEventKind.Hidden or WinEventKind.Destroyed
                ? new ClosedWork(hwnd, now)
                : new WindowWork(hwnd, kind, DetectionSource.WinEvent, null, now));
        });
        _winEvents.Start();
    }

    private void WorkerMain()
    {
        UiaReader reader;
        UiaWindowOpenedHandler? handler = null;
        try
        {
            reader = new UiaReader();
            if (_options.UseUiAutomationEvents)
            {
                handler = new UiaWindowOpenedHandler(OnUiaWindowOpened);
                reader.Automation.AddAutomationEventHandler(UIA_EVENT_ID.UIA_Window_WindowOpenedEventId,
                    reader.Automation.GetRootElement(), TreeScope.TreeScope_Subtree, null, handler);
            }
        }
        catch (Exception ex)
        {
            _startError = ex;
            _ready.Set();
            return;
        }
        _ready.Set();

        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                switch (work)
                {
                    case WindowWork w: HandleWindow(reader, w); break;
                    case ElementWork e: HandleElement(reader, e); break;
                    case ClosedWork c: HandleClosed(c); break;
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"worker: {ex.GetType().Name} 0x{ex.HResult:X8}");
            }
        }

        if (handler is not null)
        {
            try { reader.Automation.RemoveAllEventHandlers(); } catch (COMException) { }
        }
    }

    // Runs on a UIA thread: keep it tiny.
    private void OnUiaWindowOpened(IUIAutomationElement sender)
    {
        long now = Stopwatch.GetTimestamp();
        nint hwnd = (nint)sender.get_CurrentNativeWindowHandle();
        if (hwnd != 0)
        {
            bool? isDialog = null;
            try
            {
                var v = sender.GetCurrentPropertyValue(UIA_PROPERTY_ID.UIA_IsDialogPropertyId);
                try { if (v.VarType == VarEnum.VT_BOOL) isDialog = v.As<bool>(); } finally { v.Dispose(); }
            }
            catch (COMException) { }
            _queue.Add(new WindowWork(hwnd, WinEventKind.Shown, DetectionSource.UiAutomation, isDialog, now));
        }
        else
        {
            int pid = sender.get_CurrentProcessId();
            if (pid != _ownPid) _queue.Add(new ElementWork(sender, pid, now));
        }
    }

    private bool IsExcluded(int pid)
    {
        if (pid == _ownPid || pid == 0) return true;
        lock (_excludedPids) return _excludedPids.Contains(pid);
    }

    private void HandleWindow(UiaReader reader, WindowWork w)
    {
        var hwnd = (HWND)w.Hwnd;
        if (!PInvoke.IsWindow(hwnd)) return;
        int pid = Native.ProcessId(hwnd);
        if (IsExcluded(pid)) return;
        if (Recently(_seenWindows, w.Hwnd, w.Timestamp)) return;

        // Cheap pre-filter so we only spend a UIA round trip on plausible dialogs.
        string cls = Native.ClassName(hwnd);
        bool candidate = cls == "#32770" || KnownDialogClasses.Contains(cls)
                         || w.Kind is WinEventKind.DialogStart or WinEventKind.Alert
                         || w.UiaIsDialog == true
                         || DialogClassifier.TitleLooksLikeError(Native.WindowText(hwnd));
        if (!candidate) return;

        // Let it settle (text is often set right after the window is shown), and wait for it to become visible.
        SleepUntil(w.Timestamp + ToTicks(_options.SettleDelay));
        for (int i = 0; i < 10 && !PInvoke.IsWindowVisible(hwnd); i++) Thread.Sleep(50);
        if (!PInvoke.IsWindow(hwnd) || !PInvoke.IsWindowVisible(hwnd)) return;

        _seenWindows[w.Hwnd] = w.Timestamp;
        string title = Native.WindowText(hwnd);
        // Apps that are still starting up (msiexec) time out or have no text yet: back off and retry before giving up.
        var snap = reader.Read(w.Hwnd);
        for (int attempt = 1; attempt <= 3 && (snap.Error is not null || !snap.Nodes.Any(n => n.ControlType is "Text" && n.Name.Length > 0)); attempt++)
        {
            Thread.Sleep(250 * attempt);
            if (!PInvoke.IsWindow(hwnd)) return;
            snap = reader.Read(w.Hwnd);
        }

        var icon = cls == "#32770" ? Native.MessageBoxIcon(hwnd).Severity : DialogSeverity.None;
        var proc = ProcessInfo.Get(pid);
        Emit(w.Hwnd, cls, title.Length > 0 ? title : snap.RootName, snap, icon, proc, w.Source, w.Timestamp);
    }

    private void HandleElement(UiaReader reader, ElementWork e)
    {
        if (IsExcluded(e.Pid)) return;
        SleepUntil(e.Timestamp + ToTicks(_options.SettleDelay));
        var snap = reader.Read(e.Element);
        if (snap.IsDialog != true) return; // window-less UIA elements only count if they declare themselves dialogs
        Emit(0, "", snap.RootName, snap, DialogSeverity.None, ProcessInfo.Get(e.Pid), DetectionSource.UiAutomation, e.Timestamp);
    }

    private void Emit(nint hwnd, string cls, string title, UiaSnapshot snap, DialogSeverity icon, ProcessDetails proc, DetectionSource source, long t0)
    {
        bool blocked = snap.Nodes.Count == 0 && proc.Elevation is ElevationState.Elevated or ElevationState.AccessDenied;
        var c = DialogClassifier.Classify(new DialogEvidence(cls, title, snap.Nodes, snap.IsDialog, icon));

        var signals = c.Signals.ToList();
        if (snap.Error is not null) signals.Add($"uia-error:{snap.Error}");
        if (blocked) signals.Add($"uipi:{proc.Elevation}");

        string signature = $"{proc.Pid}|{title}|{c.Message}";
        if (Recently(_seenSignatures, signature, t0)) return;
        _seenSignatures[signature] = t0;

        var info = new DialogInfo
        {
            Hwnd = hwnd,
            Process = proc,
            ClassName = cls,
            Kind = c.Kind,
            Severity = c.Severity,
            Title = title,
            Message = c.Message,
            Buttons = c.Buttons,
            Signals = signals,
            Nodes = snap.Nodes,
            BlockedByUipi = blocked,
            Source = source,
            LatencyMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds,
        };
        if (hwnd != 0) _tracked.Add(hwnd);
        DialogDetected?.Invoke(info);
    }

    private void HandleClosed(ClosedWork c)
    {
        _seenWindows.Remove(c.Hwnd); // window handles get recycled
        if (_tracked.Remove(c.Hwnd)) DialogClosed?.Invoke(c.Hwnd);
    }

    private bool Recently<TKey>(Dictionary<TKey, long> seen, TKey key, long now) where TKey : notnull
    {
        if (seen.Count > 512) // keep the maps small
            foreach (var k in seen.Where(kv => Stopwatch.GetElapsedTime(kv.Value) > _options.DuplicateWindow).Select(kv => kv.Key).ToList())
                seen.Remove(k);
        return seen.TryGetValue(key, out var t) && TimeSpan.FromTicks((long)((now - t) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency))) < _options.DuplicateWindow;
    }

    private static long ToTicks(TimeSpan ts) => (long)(ts.TotalSeconds * Stopwatch.Frequency);

    private static void SleepUntil(long timestamp)
    {
        long remaining = timestamp - Stopwatch.GetTimestamp();
        if (remaining > 0) Thread.Sleep(TimeSpan.FromSeconds(remaining / (double)Stopwatch.Frequency));
    }

    public void Dispose()
    {
        _winEvents?.Dispose();
        _queue.CompleteAdding();
        _worker?.Join(TimeSpan.FromSeconds(3));
    }
}
