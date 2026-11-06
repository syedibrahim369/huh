using Huh.Core.AI;
using Huh.Core.Detection;
using Huh.Core.Facts;
using Huh.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Huh;

/// <summary>
/// huh has no main window: it runs in the background (tray icon arrives in M6), watches for error dialogs and shows a bubble
/// beside each one. AI explanations are opt-in: the consent screen appears on first run and nothing is sent before "Turn on".
/// </summary>
public partial class App : Application
{
    private DialogMonitor? _monitor;
    private BubbleManager? _bubbles;
    private HuhSettings _settings = new();
    private ConsentWindow? _consent;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(30), // per-call timeout is enforced by the explainer (10 s)
    };

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Without windows, the default (exit when the last window closes) would end the app after the first bubble.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        var ui = DispatcherQueue.GetForCurrentThread();

        _settings = HuhSettings.Load();
        var fakeMode = Environment.GetEnvironmentVariable("HUH_AI_FAKE"); // dev-only: "1" = canned answer, "quota" = simulated 429
        bool fake = fakeMode is "1" or "quota";
        IExplainer explainer = fake
            ? new DevFakeExplainer(fakeMode == "quota") // no network at all
            : new GeminiExplainer(Http, () => KeyStore.Get(), [_settings.Model, _settings.FallbackModel]);
        // Fake mode makes no network calls, so it runs with AI "on" in memory only (the real consent setting is untouched).
        var aiSettings = fake ? new HuhSettings { AiEnabled = true, AiConsentDecided = true } : null;
        var ai = new AiExplainService(explainer, new ExplanationCache(fake ? Path.Combine(Path.GetTempPath(), "huh-fake-cache") : ExplanationCache.DefaultDirectory), () => aiSettings ?? _settings);
        DevLog.Write($"AI: {(_settings.AiEnabled ? "on" : "off")}, consent decided={_settings.AiConsentDecided}, model={explainer.Model}, key from {(KeyStore.FromEnvironment ? "environment" : KeyStore.HasStoredKey() ? "DPAPI store" : "nowhere")}");

        _bubbles = new BubbleManager(ui, ai, () => fake || _settings.AiEnabled, ShowConsent);
        _monitor = new DialogMonitor();
        _monitor.DialogDetected += d =>
        {
            DevLog.Write($"detected 0x{d.Hwnd:X} {d.Process.Name} {d.Severity} blocked={d.BlockedByUipi} {d.LatencyMs:F0}ms");
            if (!(d.IsError || d.BlockedByUipi)) return;
            // Still on the monitor worker thread: decoding + WinVerifyTrust stay off the UI thread.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var facts = FactsBuilder.Build(d);
            DevLog.Write($"facts for 0x{d.Hwnd:X}: {facts.Codes.Count} code(s), source {facts.Source.Kind}/{facts.Source.Signature.State} in {sw.ElapsedMilliseconds} ms");
            ui.TryEnqueue(() => _bubbles.Show(facts));
        };
        _monitor.DialogClosed += h => { DevLog.Write($"dialog 0x{h:X} destroyed"); ui.TryEnqueue(() => _bubbles.Close(h)); };
        _monitor.Log += m => DevLog.Write($"monitor: {m}");
        var swStart = System.Diagnostics.Stopwatch.StartNew();
        _monitor.Start();
        DevLog.Write($"monitor started in {swStart.ElapsedMilliseconds} ms (process up {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms)");

        if (!_settings.AiConsentDecided && !fake) ShowConsent(); // first run
    }

    private void ShowConsent()
    {
        if (_consent is not null) { _consent.Activate(); return; }
        _consent = new ConsentWindow(_settings);
        _consent.Decided += enabled => { if (enabled) _bubbles?.RefreshAi(); };
        _consent.Closed += (_, _) => _consent = null;
        _consent.Activate(); // a normal window the user asked for (first run / clicked the link): activation is fine here
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
