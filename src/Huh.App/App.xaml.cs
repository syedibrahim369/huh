using Huh.Core.AI;
using Huh.Core.Decoding;
using Huh.Core.Detection;
using Huh.Core.Facts;
using Huh.Core.Provenance;
using Huh.Core.Settings;
using Huh.Core.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Huh;

/// <summary>
/// huh has no main window: it lives in the notification area, watches for error dialogs and shows a bubble beside each one.
/// One instance per user session (a second launch opens Settings in the first). AI explanations are opt-in: the welcome +
/// consent screen appears on first run and nothing is sent before "Turn on".
/// </summary>
public partial class App : Application
{
    private DialogMonitor? _monitor;
    private BubbleManager? _bubbles;
    private TrayIcon? _tray;
    private ConsentWindow? _consent;
    private SettingsWindow? _settingsWindow;
    private Mutex? _singleInstance;
    private DispatcherQueue _ui = null!;
    private bool _fake;

    // Read on the monitor's worker thread: immutable snapshots, swapped on the UI thread.
    private volatile bool _paused;
    private volatile HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    internal HuhSettings Settings { get; private set; } = new();
    internal AiExplainService Ai { get; private set; } = null!;
    internal ExplanationCache Cache { get; private set; } = null!;
    internal StartupRegistration Startup { get; } = new();
    internal bool IsPaused => _paused;
    internal bool IsFakeAi => _fake;
    /// <summary>The last error dialog huh showed a bubble for (memory only; used by the privacy preview).</summary>
    internal DialogFacts? LastFacts { get; private set; }
    internal event Action? StateChanged;

    internal static new App Current => (App)Application.Current;
    internal static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Huh.exe");
    internal static string Version => typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(30), // per-call timeout is enforced by the explainer (8 s per model)
    };

    private const int CmdPause = 1, CmdAi = 2, CmdSettings = 3, CmdStartup = 4, CmdQuit = 5;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Without windows, the default (exit when the last window closes) would end the app after the first bubble.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        _ui = DispatcherQueue.GetForCurrentThread();

        // One huh per user session; per data folder, so a test instance (HUH_DATA_DIR) can run next to the real one.
        var dataKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(HuhPaths.DataDir.ToLowerInvariant())))[..16];
        _singleInstance = new Mutex(true, $@"Local\huh-{dataKey}", out bool first);
        if (!first)
        {
            DevLog.Write("another huh is already running: asking it to open Settings, then exiting");
            TrayIcon.SignalExisting();
            Exit();
            return;
        }

        Settings = HuhSettings.Load();
        var fakeMode = Environment.GetEnvironmentVariable("HUH_AI_FAKE"); // dev-only: "1" = canned answer, "quota" = simulated 429
        _fake = fakeMode is "1" or "quota";
        IExplainer explainer = _fake
            ? new DevFakeExplainer(fakeMode == "quota") // no network at all
            : new GeminiExplainer(Http, () => KeyStore.Get(), () => Settings.Models);
        // Fake mode makes no network calls, so it runs with AI "on" in memory only (the real consent setting is untouched).
        var aiSettings = _fake ? new HuhSettings { AiEnabled = true, AiConsentDecided = true } : null;
        Cache = new ExplanationCache(_fake ? Path.Combine(Path.GetTempPath(), "huh-fake-cache") : ExplanationCache.DefaultDirectory);
        Ai = new AiExplainService(explainer, Cache, () => aiSettings ?? Settings);
        DevLog.Write($"huh {Version} starting. AI: {(Settings.AiEnabled ? "on" : "off")}, consent decided={Settings.AiConsentDecided}, model={explainer.Model}, " +
                     $"key from {(KeyStore.FromEnvironment ? "environment" : KeyStore.HasStoredKey() ? "DPAPI store" : "nowhere")}");
        ApplyExclusions();

        _bubbles = new BubbleManager(_ui, Ai, () => _fake || Settings.AiEnabled, ShowConsent);
        _monitor = new DialogMonitor();
        _monitor.DialogDetected += OnDialogDetected;
        _monitor.DialogClosed += h => { DevLog.Write($"dialog 0x{h:X} destroyed"); _ui.TryEnqueue(() => _bubbles.Close(h)); };
        _monitor.Log += m => DevLog.Write($"monitor: {m}");
        var swStart = System.Diagnostics.Stopwatch.StartNew();
        _monitor.Start();
        DevLog.Write($"monitor started in {swStart.ElapsedMilliseconds} ms (process up {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms)");

        CreateTray();
        if (Startup.RepairPath(ExePath)) DevLog.Write("start-with-Windows entry pointed at a missing exe: updated");
        Prewarm();

        if (!_fake && (!Settings.WelcomeSeen || !Settings.AiConsentDecided)) ShowConsent(); // first run: welcome, then the AI question
    }

    private void OnDialogDetected(Huh.Core.Dialogs.DialogInfo d)
    {
        DevLog.Write($"detected 0x{d.Hwnd:X} {d.Process.Name} {d.Severity} blocked={d.BlockedByUipi} {d.LatencyMs:F0}ms");
        if (!(d.IsError || d.BlockedByUipi)) return;
        if (_paused) { DevLog.Write($"paused: no bubble for 0x{d.Hwnd:X}"); return; }
        // Still on the monitor worker thread: decoding + WinVerifyTrust stay off the UI thread.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var facts = FactsBuilder.Build(d);
        var key = HuhSettings.AppKeyFor(facts);
        if (key is not null && _excluded.Contains(key)) { DevLog.Write($"excluded app {key}: no bubble for 0x{d.Hwnd:X}"); return; }
        DevLog.Write($"facts for 0x{d.Hwnd:X}: {facts.Codes.Count} code(s), source {facts.Source.Kind}/{facts.Source.Signature.State} in {sw.ElapsedMilliseconds} ms");
        _ui.TryEnqueue(() =>
        {
            if (_paused) return;
            LastFacts = facts;
            if (key is not null && !d.BlockedByUipi)
            {
                Settings.NoteSeen(key, HuhSettings.AppNameFor(facts), DateTimeOffset.Now);
                SaveSettings();
            }
            _bubbles!.Show(facts);
        });
    }

    // ------------------------------------------------------------------ tray

    private static string IconPath(bool paused) => Path.Combine(AppContext.BaseDirectory, "Assets", paused ? "huh-paused.ico" : "huh.ico");

    private string Tooltip => _paused ? "huh: paused" : $"huh: watching for errors{(Settings.AiEnabled || _fake ? " (AI on)" : "")}";

    private void CreateTray()
    {
        try
        {
            _tray = new TrayIcon(IconPath(false), Tooltip, BuildMenu);
            _tray.Activated += ShowSettings;
            _tray.ShowRequested += ShowSettings;
            _tray.Command += OnTrayCommand;
        }
        catch (Exception ex) { DevLog.Write($"tray icon failed: {ex.Message}"); }
    }

    private IReadOnlyList<TrayMenuItem> BuildMenu() =>
    [
        new(100, _paused ? "huh is paused" : "huh is watching for errors", Enabled: false),
        TrayMenuItem.Separator,
        new(CmdPause, "Pause huh", Checked: _paused),
        new(CmdAi, "AI explanations", Checked: Settings.AiEnabled || _fake, Enabled: !_fake),
        new(CmdStartup, "Start with Windows", Checked: SafeStartupEnabled()),
        TrayMenuItem.Separator,
        new(CmdSettings, "Settings…", IsDefault: true),
        new(CmdQuit, "Quit huh"),
    ];

    private void OnTrayCommand(int id)
    {
        switch (id)
        {
            case CmdPause: SetPaused(!_paused); break;
            case CmdAi: SetAiEnabled(!Settings.AiEnabled); break;
            case CmdStartup: SetStartWithWindows(!SafeStartupEnabled()); break;
            case CmdSettings: ShowSettings(); break;
            case CmdQuit: Quit(); break;
        }
    }

    // ------------------------------------------------------------------ state changes (UI thread; also used by Settings)

    internal void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (paused) _bubbles?.CloseAll();
        DevLog.Write(paused ? "paused" : "resumed");
        _tray?.Update(IconPath(paused), Tooltip);
        StateChanged?.Invoke();
    }

    /// <summary>Turning AI on needs the consent screen unless the user already accepted once (and a key is available).</summary>
    internal void SetAiEnabled(bool enabled)
    {
        if (_fake) return;
        if (enabled && (Settings.AiConsentAt is null || KeyStore.Get() is null)) { ShowConsent(); return; }
        Settings.AiEnabled = enabled;
        if (enabled) Settings.AiConsentDecided = true;
        SaveSettings();
        DevLog.Write($"AI {(enabled ? "on" : "off")} (settings)");
        if (enabled) _bubbles?.RefreshAi();
        _tray?.Update(IconPath(_paused), Tooltip);
        StateChanged?.Invoke();
    }

    internal bool SafeStartupEnabled()
    {
        try { return Startup.IsEnabled(); } catch { return false; }
    }

    internal bool SetStartWithWindows(bool enabled)
    {
        try
        {
            if (enabled) Startup.Enable(ExePath); else Startup.Disable();
            DevLog.Write($"start with Windows: {(enabled ? "on" : "off")}");
        }
        catch (Exception ex) { DevLog.Write($"start with Windows failed: {ex.GetType().Name}"); return false; }
        StateChanged?.Invoke();
        return true;
    }

    internal void ApplyExclusions()
    {
        _excluded = new HashSet<string>(Settings.ExcludedApps, StringComparer.OrdinalIgnoreCase);
        StateChanged?.Invoke();
    }

    internal void SaveSettings()
    {
        try { Settings.Save(); } catch (Exception ex) { DevLog.Write($"saving settings failed: {ex.GetType().Name}"); }
    }

    // ------------------------------------------------------------------ windows

    internal void ShowSettings()
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate(); // the user asked for it (tray click): activation is fine here
    }

    internal void ShowConsent()
    {
        if (_fake) return;
        if (_consent is not null) { _consent.Activate(); return; }
        _consent = new ConsentWindow(Settings, showWelcome: !Settings.WelcomeSeen);
        _consent.Decided += enabled =>
        {
            if (enabled) _bubbles?.RefreshAi();
            _tray?.Update(IconPath(_paused), Tooltip);
            StateChanged?.Invoke();
        };
        _consent.Closed += (_, _) => _consent = null;
        _consent.Activate(); // a normal window the user expects (first run / clicked the link): activation is fine here
    }

    private void Quit()
    {
        DevLog.Write("quit from the tray menu");
        _bubbles?.CloseAll();
        _consent?.Close();
        _settingsWindow?.Close();
        _monitor?.Dispose();
        _tray?.Dispose();
        _singleInstance?.ReleaseMutex();
        Exit();
    }

    // ------------------------------------------------------------------ pre-warm

    /// <summary>
    /// The first bubble used to take 0.6-1.7 s (XAML cold start, first WinVerifyTrust, JIT) against ~100-270 ms for later ones.
    /// Pay that once at startup instead: run the facts pipeline on a sample, and render one cloaked, off-screen bubble.
    /// Nothing is shown and nothing is sent anywhere. HUH_NO_PREWARM=1 turns it off (for measuring).
    /// </summary>
    private void Prewarm()
    {
        if (Environment.GetEnvironmentVariable("HUH_NO_PREWARM") == "1") { DevLog.Write("prewarm: off"); return; }
        _ = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Authenticode.Verify(ExePath);
                Authenticode.Verify(Path.Combine(Environment.SystemDirectory, "notepad.exe")); // catalog path
                ErrorCodeDecoder.Decode("Access is denied. (0x80070005) 0xC0000135 error 1619");
                _ = Prompt.Build(SampleFacts.Create(), Ai.Redactor);
            }
            catch (Exception ex) { DevLog.Write($"prewarm (facts) failed: {ex.GetType().Name}"); }
            DevLog.Write($"prewarm: facts pipeline in {sw.ElapsedMilliseconds} ms");
        });
        _ui.TryEnqueue(DispatcherQueuePriority.Low, () => BubbleWindow.Prewarm(Ai.Redactor));
    }
}

/// <summary>Development-only explainer (HUH_AI_FAKE=1): canned answers after a short delay, so UI work costs no quota.</summary>
internal sealed class DevFakeExplainer(bool simulateQuota) : IExplainer
{
    public string Model => "dev-fake";

    public async Task<AiOutcome> ExplainAsync(ExplainRequest request, CancellationToken ct)
    {
        await Task.Delay(900, ct);
        if (simulateQuota)
            return new AiOutcome(AiStatus.Unavailable, Reason: UnavailableReason.Quota, Detail: "RESOURCE_EXHAUSTED (simulated)", Model: Model, LatencyMs: 900);
        var e = Explanation.Sanitized(
            "This is a placeholder explanation from the development fake. The real one comes from Gemini.",
            ["First step would go here.", "Second step would go here."], "open_settings", "troubleshoot", 0.5);
        return new AiOutcome(AiStatus.Ok, e, Model: Model, LatencyMs: 900);
    }
}
