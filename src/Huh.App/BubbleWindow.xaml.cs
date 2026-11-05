using System.Diagnostics;
using Huh.Core.Dialogs;
using Huh.Core.Placement;
using Huh.Core.Windowing;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinRT.Interop;

namespace Huh;

/// <summary>
/// The bubble: a borderless, never-activated window anchored next to one dialog.
/// Positioning is done in physical pixels by <see cref="BubblePlacer"/>; the XAML content is laid out in DIPs.
/// </summary>
public sealed partial class BubbleWindow : Window
{
    public const double WidthDip = 340;

    public nint Hwnd { get; }
    internal DialogInfo Dialog { get; }
    public event Action<BubbleWindow>? Dismissed;

    private readonly WindowFollower _follower;
    private bool _hidden;
    private double _lastHeightDip;
    private uint _lastDpi;

    public BubbleWindow(DialogInfo dialog)
    {
        Dialog = dialog;
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false); // keep the thin border: native shadow + rounded corners
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        Screen.MakeBubbleWindow(Hwnd); // after SetPresenter: presenters reset window styles

        // Dev-only override so both themes can be checked without changing the user's Windows setting.
        if (Environment.GetEnvironmentVariable("HUH_THEME") is { } theme && Enum.TryParse<ElementTheme>(theme, true, out var t))
            Root.RequestedTheme = t;
        SystemBackdrop = new AlwaysActiveAcrylic();
        Bind(dialog);

        // Created before anything that can call Reposition() (which may start it).
        _restack = DispatcherQueue.CreateTimer();
        _restack.Interval = TimeSpan.FromMilliseconds(120);
        _restack.IsRepeating = false;
        _restack.Tick += (_, _) => { if (!_hidden) Reposition(); };

        Root.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Height - _lastHeightDip) > 0.5) Reposition();
        };

        _follower = new WindowFollower(dialog.Hwnd);
        _follower.Moved += () => { if (!_hidden) Reposition(); };
        // EVENT_SYSTEM_FOREGROUND can arrive before the new foreground window has actually been raised, so re-stack now
        // and again once the z-order has settled.
        _follower.ForegroundChanged += _ => { if (!_hidden) { Reposition(); _restack.Start(); } };
        _follower.Minimized += HideWithDialog;
        _follower.Hidden += HideWithDialog;
        _follower.Restored += ShowWithDialog;
        _follower.Shown += ShowWithDialog;
    }

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _restack;

    private void HideWithDialog() { DevLog.Write($"bubble 0x{Hwnd:X}: hide with dialog 0x{Dialog.Hwnd:X}"); _hidden = true; Screen.Hide(Hwnd); }

    private void ShowWithDialog()
    {
        DevLog.Write($"bubble 0x{Hwnd:X}: show-with-dialog request (visible={Screen.IsVisible(Dialog.Hwnd)} min={Screen.IsMinimized(Dialog.Hwnd)})");
        if (!Screen.IsVisible(Dialog.Hwnd) || Screen.IsMinimized(Dialog.Hwnd)) return;
        _hidden = false;
        Reposition();
        Screen.ShowNoActivate(Hwnd);
    }

    /// <summary>Place next to the dialog and show WITHOUT activating (no Window.Activate(): that would steal focus).</summary>
    /// <returns>False if the dialog is gone (nothing to anchor to); the caller should close the bubble.</returns>
    public bool ShowBeside()
    {
        DevLog.Write($"bubble 0x{Hwnd:X}: show beside dialog 0x{Dialog.Hwnd:X} ({Dialog.Process.Name})");
        if (!Reposition()) return false;
        Screen.ShowNoActivate(Hwnd);
        return true;
    }

    public bool Reposition()
    {
        var frame = Screen.IsVisible(Dialog.Hwnd) ? Screen.FrameBounds(Dialog.Hwnd) : null;
        if (frame is null) return false;
        _lastHeightDip = MeasureHeightDip();
        var placement = BubblePlacer.Place(frame.Value, WidthDip, _lastHeightDip, Screen.GetMonitors());
        Screen.PlaceAbove(Hwnd, Dialog.Hwnd, placement.Rect);
        // Crossing onto a monitor with another scale triggers WM_DPICHANGED, and WinUI then applies Windows' suggested size.
        // Place once more after that settles so our (placer-computed) size wins. Not testable on the single-monitor dev box.
        if (_lastDpi != 0 && placement.Monitor.Dpi != _lastDpi) _restack.Start();
        _lastDpi = placement.Monitor.Dpi;
        return true;
    }

    private double MeasureHeightDip()
    {
        Root.Measure(new Size(WidthDip, double.PositiveInfinity));
        double h = Root.DesiredSize.Height;
        return h > 1 ? Math.Ceiling(h) + 2 /* 1px border top+bottom */ : 160; // before first layout: a sane estimate
    }

    private void Bind(DialogInfo d)
    {
        string app = FriendlyName(d.Process);
        if (d.BlockedByUipi)
        {
            SeverityIcon.Glyph = ""; // Lock
            SeverityIcon.Foreground = Brush("SystemFillColorCautionBrush");
            Headline.Text = $"{app} is running as administrator";
            Detail.Text = "Windows doesn't let huh read dialogs from admin apps unless huh runs as admin too, and huh doesn't by default. "
                        + (d.Title.Length > 0 ? $"The dialog is titled “{d.Title}”." : "");
        }
        else
        {
            bool warning = d.Severity == DialogSeverity.Warning;
            SeverityIcon.Glyph = warning ? "" : ""; // StatusWarningFull : StatusErrorFull
            SeverityIcon.Foreground = Brush(warning ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush");
            Headline.Text = d.Title.Length > 0 ? d.Title : $"{app} reported an error";
            Detail.Text = d.Message;
            Detail.Visibility = d.Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        SourceName.Text = $"From {app}" + (d.Process.Name is { } n ? $" ({n}.exe)" : "");
        SourcePath.Text = d.Process.Path ?? "Path unavailable (protected or elevated process)";
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(SourcePath, SourcePath.Text);
    }

    private static string FriendlyName(ProcessDetails p)
    {
        try
        {
            if (p.Path is not null && FileVersionInfo.GetVersionInfo(p.Path).FileDescription is { Length: > 0 } desc) return desc;
        }
        catch { /* no version info */ }
        return p.Name ?? "An app";
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private void OnDismiss(object sender, RoutedEventArgs e) => Dismissed?.Invoke(this);

    public void CloseBubble()
    {
        _restack.Stop();
        _follower.Dispose();
        Close();
    }
}
