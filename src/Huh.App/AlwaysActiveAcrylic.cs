using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Huh;

/// <summary>
/// Desktop Acrylic that always renders in its "active" state. The stock <see cref="DesktopAcrylicBackdrop"/> follows window
/// activation and falls back to a flat color when inactive, and the bubble is NEVER active (WS_EX_NOACTIVATE).
/// </summary>
internal sealed class AlwaysActiveAcrylic : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _config;
    private FrameworkElement? _themeSource;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _config = new SystemBackdropConfiguration { IsInputActive = true };
        _themeSource = root.Content as FrameworkElement;
        if (_themeSource is not null)
        {
            _themeSource.ActualThemeChanged += OnThemeChanged;
            OnThemeChanged(_themeSource, null);
        }
        _controller = new DesktopAcrylicController();
        ApplyTint();
        _controller.AddSystemBackdropTarget(target);
        _controller.SetSystemBackdropConfiguration(_config);
    }

    private void OnThemeChanged(FrameworkElement sender, object? args)
    {
        if (_config is null) return;
        _config.Theme = sender.ActualTheme switch
        {
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            ElementTheme.Light => SystemBackdropTheme.Light,
            _ => SystemBackdropTheme.Default,
        };
        ApplyTint();
    }

    /// <summary>
    /// The default desktop acrylic is very see-through; over busy content (code, video) the bubble text gets hard to read.
    /// These values sit close to Windows' own flyout acrylic: clearly translucent, but text stays legible.
    /// </summary>
    private void ApplyTint()
    {
        if (_controller is null || _config is null) return;
        bool dark = _config.Theme == SystemBackdropTheme.Dark
                    || (_config.Theme == SystemBackdropTheme.Default && _themeSource?.ActualTheme == ElementTheme.Dark);
        var tint = dark ? Windows.UI.Color.FromArgb(255, 0x2C, 0x2C, 0x2C) : Windows.UI.Color.FromArgb(255, 0xFC, 0xFC, 0xFC);
        _controller.TintColor = tint;
        _controller.FallbackColor = tint;
        _controller.TintOpacity = dark ? 0.80f : 0.70f;
        _controller.LuminosityOpacity = dark ? 0.94f : 0.90f;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        if (_themeSource is not null) _themeSource.ActualThemeChanged -= OnThemeChanged;
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
    }
}
