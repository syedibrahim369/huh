using Huh.Core.AI;
using Huh.Core.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Huh;

/// <summary>
/// Settings (WinUI 3, Mica). Changes apply immediately and are saved at once, like Windows' own Settings. Everything here
/// goes through <see cref="App"/> so the tray menu and this window always agree.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private static readonly string[] KnownModels = [HuhSettings.DefaultModel, HuhSettings.DefaultFallbackModel];
    private readonly App _app = App.Current;
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "huh.ico"));
        AppWindow.Resize(Scale(new Windows.Graphics.SizeInt32(720, 820)));
        if (AppWindow.Presenter is OverlappedPresenter p) p.IsMinimizable = true;
        CenterOnScreen();

        foreach (var m in KnownModels) { ModelBox.Items.Add(m); FallbackBox.Items.Add(m); }
        AboutText.Text = $"huh {App.Version} · github.com/syedibrahim369/huh";

        _app.StateChanged += OnAppStateChanged;
        Closed += (_, _) => _app.StateChanged -= OnAppStateChanged;
        Refresh();
    }

    private void OnAppStateChanged() => DispatcherQueue.TryEnqueue(Refresh);

    /// <summary>Re-reads everything from the app (also after the tray menu or the consent screen changed something).</summary>
    private void Refresh()
    {
        _loading = true;
        try
        {
            var s = _app.Settings;
            WatchToggle.IsOn = !_app.IsPaused;
            WatchDescription.Text = _app.IsPaused
                ? "Paused: no bubbles until you turn this back on (or restart huh)."
                : "huh shows a bubble beside error messages from any app.";
            StartupToggle.IsOn = _app.SafeStartupEnabled();

            AiToggle.IsOn = s.AiEnabled || _app.IsFakeAi;
            AiToggle.IsEnabled = !_app.IsFakeAi;
            AiDescription.Text = _app.IsFakeAi ? "Development mode (HUH_AI_FAKE): canned answers, nothing is sent."
                : s.AiEnabled ? $"On since you agreed on {s.AiConsentAt?.LocalDateTime:d MMM yyyy}. Error text is sent to Google Gemini (redacted)."
                : s.AiConsentAt is null ? "Off. Turning it on shows what is sent and asks you first."
                : "Off. Only what huh works out on your PC is shown; nothing is sent.";
            ModelBox.Text = s.Model;
            ModelBox.SelectedItem = KnownModels.Contains(s.Model) ? s.Model : null;
            FallbackBox.Text = s.FallbackModel;
            FallbackBox.SelectedItem = KnownModels.Contains(s.FallbackModel) ? s.FallbackModel : null;

            KeyStatus.Text = KeyStore.FromEnvironment
                ? "Using the GEMINI_API_KEY environment variable (it takes priority over a saved key)."
                : KeyStore.HasStoredKey()
                    ? "A key is saved for your Windows account, encrypted with Windows DPAPI. It is never shown again."
                    : "No key saved. Get a free one at aistudio.google.com.";
            RemoveKeyButton.IsEnabled = KeyStore.HasStoredKey();

            RefreshExclusions();
            RefreshPrivacy();
        }
        finally { _loading = false; }
    }

    private Windows.Graphics.SizeInt32 Scale(Windows.Graphics.SizeInt32 dip)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var mon = Huh.Core.Windowing.Screen.GetMonitors().FirstOrDefault(m => m.Bounds.Left == area.OuterBounds.X && m.Bounds.Top == area.OuterBounds.Y);
        double scale = mon?.Scale ?? 1.0;
        int w = Math.Min((int)(dip.Width * scale), area.WorkArea.Width);
        int h = Math.Min((int)(dip.Height * scale), area.WorkArea.Height - 16);
        return new(w, h);
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + Math.Max(0, (area.Height - size.Height) / 2)));
    }

    // ------------------------------------------------------------------ general

    private void OnWatchToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetPaused(!WatchToggle.IsOn);
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!_app.SetStartWithWindows(StartupToggle.IsOn)) StartupDescription.Text = "Couldn't change the startup entry.";
        Refresh();
    }

    // ------------------------------------------------------------------ AI

    private void OnAiToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetAiEnabled(AiToggle.IsOn); // may open the consent screen instead; Refresh shows the real state
        Refresh();
    }

    private void OnModelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox box || box.SelectedItem is not string m) return;
        ApplyModel(box, m);
    }

    private void OnModelSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs e)
    {
        if (_loading) return;
        ApplyModel(sender, e.Text.Trim());
        e.Handled = true;
    }

    private void ApplyModel(ComboBox box, string name)
    {
        ModelMessage.Visibility = Visibility.Visible;
        if (!HuhSettings.IsValidModelName(name))
        {
            ModelMessage.Text = $"“{name}” isn't a model name huh accepts (lower-case letters, digits, dots and dashes, like {HuhSettings.DefaultModel}).";
            ModelMessage.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            Refresh();
            return;
        }
        if (box == ModelBox) _app.Settings.Model = name; else _app.Settings.FallbackModel = name;
        _app.SaveSettings();
        ModelMessage.Text = $"Saved. The next request uses {string.Join(", then ", _app.Settings.Models)}. Saved answers from other models are still reused.";
        ModelMessage.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        DevLog.Write($"models set to {string.Join(" > ", _app.Settings.Models)}");
        Refresh();
    }

    private void OnKeyChanged(object sender, RoutedEventArgs e) => SaveKeyButton.IsEnabled = KeyBox.Password.Trim().Length >= 20;

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        var k = KeyBox.Password.Trim();
        KeyBox.Password = "";
        if (k.Length < 20) return;
        KeyStore.Save(k);
        DevLog.Write("API key replaced (DPAPI store)");
        Refresh();
    }

    private void OnRemoveKey(object sender, RoutedEventArgs e)
    {
        KeyStore.Remove();
        DevLog.Write("saved API key removed");
        Refresh();
    }

    // ------------------------------------------------------------------ exclusions

    private void RefreshExclusions()
    {
        var s = _app.Settings;
        ExcludedList.Children.Clear();
        foreach (var exe in s.ExcludedApps)
        {
            var name = s.RecentApps.FirstOrDefault(a => a.Exe == exe)?.Name;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = name is null || name.Equals(Path.GetFileNameWithoutExtension(exe), StringComparison.OrdinalIgnoreCase) ? exe : $"{name} ({exe})", VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Content = "Remove" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Stop excluding {exe}");
            Grid.SetColumn(remove, 1);
            var captured = exe;
            remove.Click += (_, _) =>
            {
                _app.Settings.Include(captured);
                _app.SaveSettings();
                _app.ApplyExclusions();
                DevLog.Write($"exclusion removed: {captured}");
            };
            row.Children.Add(remove);
            ExcludedList.Children.Add(row);
        }
        ExcludedEmpty.Visibility = s.ExcludedApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RecentBox.Items.Clear();
        foreach (var a in s.RecentApps.Where(a => !s.IsExcluded(a.Exe)))
            RecentBox.Items.Add(new ComboBoxItem { Content = $"{a.Name} ({a.Exe}) · last seen {a.LastSeen.LocalDateTime:d MMM, HH:mm}", Tag = a.Exe });
        RecentBox.IsEnabled = RecentBox.Items.Count > 0;
        RecentEmpty.Visibility = s.RecentApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExcludeButton.IsEnabled = false;
    }

    private void OnRecentSelected(object sender, SelectionChangedEventArgs e) => ExcludeButton.IsEnabled = RecentBox.SelectedItem is ComboBoxItem;

    private void OnExclude(object sender, RoutedEventArgs e)
    {
        if (RecentBox.SelectedItem is not ComboBoxItem { Tag: string exe }) return;
        if (_app.Settings.Exclude(exe))
        {
            _app.SaveSettings();
            _app.ApplyExclusions();
            DevLog.Write($"exclusion added: {exe}");
        }
    }

    // ------------------------------------------------------------------ privacy

    private void RefreshPrivacy()
    {
        var s = _app.Settings;
        PrivacyWhen.Text = s.AiEnabled || _app.IsFakeAi
            ? "AI explanations are on, so huh sends the following for each new error message it shows a bubble for. Repeats are answered from the copy saved on your PC."
            : "AI explanations are off: nothing is sent anywhere. If you turn them on, this is what huh sends for each new error message:";
        PreviewLastItem.IsEnabled = _app.LastFacts is not null;
        if (PreviewSource.SelectedIndex < 0) PreviewSource.SelectedIndex = 0;

        var stored = new List<string>
        {
            $"Settings and the list of recently seen apps (exe names only): {HuhPaths.Settings}",
            KeyStore.HasStoredKey() ? $"Your API key, encrypted with Windows DPAPI for your account: {HuhPaths.Key}" : "No API key is saved by huh.",
            $"Saved AI answers ({SafeCount()}), kept 30 days, no dialog text: {HuhPaths.AiCache}",
            $"A diagnostics log of window events, never message text: {HuhPaths.Logs}",
        };
        StoredText.Text = string.Join("\n", stored.Select(x => "• " + x));
        if (PreviewExpander.IsExpanded) UpdatePreview();
    }

    private int SafeCount()
    {
        try { return _app.Cache.Count; } catch { return 0; }
    }

    private void OnPreviewExpanding(Expander sender, ExpanderExpandingEventArgs args) => UpdatePreview();

    private void OnPreviewSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewExpander.IsExpanded) UpdatePreview();
    }

    private void UpdatePreview()
    {
        var facts = PreviewSource.SelectedIndex == 1 && _app.LastFacts is { } last ? last : SampleFacts.Create();
        if (facts.Dialog.BlockedByUipi) { PreviewText.Text = "Nothing is sent for this message: huh couldn't read it (the app runs as administrator)."; return; }
        var p = RequestPreview.For(facts, _app.Ai.Redactor, _app.Settings.Model);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{p.Method} {p.Url}");
        foreach (var (name, value) in p.Headers) sb.AppendLine($"{name}: {value}");
        sb.AppendLine();
        sb.Append(p.Body);
        PreviewText.Text = sb.ToString();
    }

    private void OnClearCache(object sender, RoutedEventArgs e)
    {
        int n = _app.Cache.Clear();
        PrivacyResult.Text = n == 1 ? "Deleted 1 saved answer." : $"Deleted {n} saved answers.";
        PrivacyResult.Visibility = Visibility.Visible;
        DevLog.Write($"cache cleared ({n})");
        RefreshPrivacy();
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(HuhPaths.DataDir);
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var psi = new System.Diagnostics.ProcessStartInfo(explorer) { UseShellExecute = false };
        psi.ArgumentList.Add(HuhPaths.DataDir);
        System.Diagnostics.Process.Start(psi)?.Dispose();
    }
}
