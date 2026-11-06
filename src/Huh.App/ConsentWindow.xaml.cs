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

    internal ConsentWindow(HuhSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        AppWindow.Resize(Scale(new Windows.Graphics.SizeInt32(560, 720)));
        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsMaximizable = false; p.IsMinimizable = false; }
        CenterOnScreen();

        if (KeyStore.FromEnvironment)
        {
            KeyStatus.Text = "Using the GEMINI_API_KEY set in your environment.";
            KeyBox.Visibility = Visibility.Collapsed;
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
        _settings.AiConsentDecided = true;
        _settings.AiEnabled = enabled;
        _settings.AiConsentAt = enabled ? DateTimeOffset.Now : null;
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
