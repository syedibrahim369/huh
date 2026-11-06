using Huh.Core.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Huh;

/// <summary>
/// First-run opt-in for AI explanations. Nothing is sent to Gemini until the user presses "Turn on AI explanations".
/// The key comes from GEMINI_API_KEY (development) or is pasted here and stored with DPAPI; it is never shown again.
/// </summary>
public sealed partial class ConsentWindow : Window
{
    private readonly HuhSettings _settings;

    /// <summary>Raised after the user decides (true = AI on).</summary>
    public event Action<bool>? Decided;

    internal ConsentWindow(HuhSettings settings, bool showWelcome)
    {
        _settings = settings;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "huh.ico"));
        AppWindow.Resize(Scale(new Windows.Graphics.SizeInt32(560, 700)));
        if (showWelcome)
        {
            Title = "Welcome to huh";
            WelcomePanel.Visibility = Visibility.Visible;
            ConsentPanel.Visibility = Visibility.Collapsed;
        }
        else Title = "huh: AI explanations";
        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsMaximizable = false; p.IsMinimizable = false; }
        CenterOnScreen();

        if (KeyStore.FromEnvironment)
        {
            KeyStatus.Text = "Using the GEMINI_API_KEY set in your environment.";
            KeyBox.Visibility = Visibility.Collapsed;
            GetKeyLink.Visibility = Visibility.Collapsed;
        }
        else if (KeyStore.HasStoredKey())
        {
            KeyStatus.Text = "A key is saved for your Windows account (encrypted). Paste a new one to replace it.";
        }
        else
        {
            KeyStatus.Text = "Stored encrypted for your Windows account only (Windows DPAPI). Never shown or sent anywhere except to Google.";
        }
        UpdateAccept();
    }

    /// <summary>Welcome seen: remember it, then ask the AI question (or close if that was already answered).</summary>
    private void OnWelcomeNext(object sender, RoutedEventArgs e)
    {
        _settings.WelcomeSeen = true;
        _settings.Save();
        if (_settings.AiConsentDecided) { Close(); return; }
        Title = "huh: AI explanations";
        WelcomePanel.Visibility = Visibility.Collapsed;
        ConsentPanel.Visibility = Visibility.Visible;
        if (Motion.Enabled) Motion.FadeIn(ConsentPanel, 160);
    }

    /// <summary>Opens Google AI Studio's key page in the browser (the user clicked the link).</summary>
    private void OnGetKey(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://aistudio.google.com/apikey") { UseShellExecute = true })?.Dispose();

    private void OnKeyChanged(object sender, RoutedEventArgs e) => UpdateAccept();

    private void UpdateAccept() =>
        AcceptButton.IsEnabled = KeyStore.FromEnvironment || KeyStore.HasStoredKey() || KeyBox.Password.Trim().Length >= 20;

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        var pasted = KeyBox.Password.Trim();
        if (pasted.Length > 0) KeyStore.Save(pasted);
        KeyBox.Password = "";
        Save(enabled: true);
    }

    private void OnDecline(object sender, RoutedEventArgs e) => Save(enabled: false);

    private void Save(bool enabled)
    {
        _settings.WelcomeSeen = true;
        _settings.AiConsentDecided = true;
        _settings.AiEnabled = enabled;
        if (enabled) _settings.AiConsentAt = DateTimeOffset.Now; // kept after a later "off": re-enabling from Settings then needs no new consent
        _settings.Save();
        DevLog.Write($"AI consent: {(enabled ? "accepted" : "declined")}");
        Decided?.Invoke(enabled);
        Close();
    }

    private Windows.Graphics.SizeInt32 Scale(Windows.Graphics.SizeInt32 dip)
    {
        double s = Content?.XamlRoot?.RasterizationScale ?? GetDpiScale();
        return new((int)(dip.Width * s), (int)(dip.Height * s));
    }

    private double GetDpiScale()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var mon = Huh.Core.Windowing.Screen.GetMonitors().FirstOrDefault(m => m.Bounds.Left == area.OuterBounds.X && m.Bounds.Top == area.OuterBounds.Y);
        return mon?.Scale ?? 1.0;
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }
}
