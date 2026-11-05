using Huh.Core.Detection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Huh;

/// <summary>
/// huh has no main window: it runs in the background (tray icon arrives in M6), watches for error dialogs and shows a bubble
/// beside each one.
/// </summary>
public partial class App : Application
{
    private DialogMonitor? _monitor;
    private BubbleManager? _bubbles;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Without windows, the default (exit when the last window closes) would end the app after the first bubble.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        var ui = DispatcherQueue.GetForCurrentThread();
        _bubbles = new BubbleManager();
        _monitor = new DialogMonitor();
        _monitor.DialogDetected += d =>
        {
            DevLog.Write($"detected 0x{d.Hwnd:X} {d.Process.Name} {d.Severity} blocked={d.BlockedByUipi} {d.LatencyMs:F0}ms");
            if (d.IsError || d.BlockedByUipi) ui.TryEnqueue(() => _bubbles.Show(d));
        };
        _monitor.DialogClosed += h => { DevLog.Write($"dialog 0x{h:X} destroyed"); ui.TryEnqueue(() => _bubbles.Close(h)); };
        _monitor.Log += m => DevLog.Write($"monitor: {m}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _monitor.Start();
        DevLog.Write($"monitor started in {sw.ElapsedMilliseconds} ms (process up {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms)");
    }
}
