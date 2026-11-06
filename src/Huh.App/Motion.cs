using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Huh;

/// <summary>
/// Small, subtle animations that respect Windows' "Animation effects" setting (Settings › Accessibility › Visual effects):
/// when it is off, nothing animates. <c>HUH_ANIMATIONS=0|1</c> overrides it for testing without changing the system setting.
/// </summary>
internal static class Motion
{
    private static readonly Windows.UI.ViewManagement.UISettings Ui = new();

    /// <summary>Read live, so flipping the Windows setting applies to the next bubble.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("HUH_ANIMATIONS") switch
    {
        "0" => false,
        "1" => true,
        _ => SafeSystemSetting(),
    };

    private static bool SafeSystemSetting()
    {
        try { return Ui.AnimationsEnabled; } catch { return true; }
    }

    public static void Add(Storyboard sb, DependencyObject target, string property, double to, int ms, EasingFunctionBase? ease)
    {
        var a = new DoubleAnimation { To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EasingFunction = ease };
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, property);
        sb.Children.Add(a);
    }

    public static Storyboard Fade(UIElement element, double to, int ms)
    {
        var sb = new Storyboard();
        Add(sb, element, "Opacity", to, ms, null);
        return sb;
    }

    public static void FadeIn(UIElement element, int ms)
    {
        element.Opacity = 0;
        Fade(element, 1, ms).Begin();
    }
}
