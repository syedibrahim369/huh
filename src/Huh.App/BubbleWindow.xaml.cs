using Huh.Core.Decoding;
using Huh.Core.Dialogs;
using Huh.Core.Facts;
using Huh.Core.Placement;
using Huh.Core.Provenance;
using Huh.Core.Windowing;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using WinRT.Interop;

namespace Huh;

/// <summary>
/// The bubble: a borderless, never-activated window anchored next to one dialog.
/// Positioning is done in physical pixels by <see cref="BubblePlacer"/>; the XAML content is laid out in DIPs.
/// Content (M3) is fully deterministic: headline, decoded codes, source + trust badge, expandable details.
/// </summary>
public sealed partial class BubbleWindow : Window
{
    public const double WidthDip = 340;

    public nint Hwnd { get; }
    internal DialogInfo Dialog => Facts.Dialog;
    internal DialogFacts Facts { get; }
    public event Action<BubbleWindow>? Dismissed;

    private readonly WindowFollower? _follower; // null for the pre-warm bubble (no real dialog)
    private readonly bool _prewarm;
    private BubbleSide _side = BubbleSide.Right;
    private bool _closing;
    private readonly Huh.Core.Actions.FixActionRunner _runner;
    private readonly Huh.Core.Privacy.Redactor _redactor;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _restack;
    private bool _hidden;
    private double _lastHeightDip;
    private uint _lastDpi;
    internal PxRect LastAnchor { get; private set; }

    // Segoe Fluent Icons code points (built from ints so no escaping can get mangled).
    private const int GlyphErrorFull = 0xEB90, GlyphWarningFull = 0xE814, GlyphLock = 0xE72E, GlyphCheck = 0xE73E,
        GlyphWarning = 0xE7BA, GlyphError = 0xE783, GlyphUnknown = 0xE9CE, GlyphStore = 0xE719, GlyphWindows = 0xE770,
        GlyphGlobe = 0xE774, GlyphChevronDown = 0xE70D, GlyphChevronUp = 0xE70E;

    internal BubbleWindow(DialogFacts facts, Huh.Core.Privacy.Redactor redactor, bool prewarm = false)
    {
        Facts = facts;
        _prewarm = prewarm;
        _redactor = redactor;
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);
        // Side effects go through a launcher owned by this bubble (UAC prompts attach to it).
        _runner = new Huh.Core.Actions.FixActionRunner(new WinAppLauncher(Hwnd, DispatcherQueue), redactor);

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false); // keep the thin border: native shadow + rounded corners
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        Screen.MakeBubbleWindow(Hwnd); // after SetPresenter: presenters reset window styles

        // Dev-only overrides: check both themes / the expanded layout without changing anything on the user's machine.
        if (Environment.GetEnvironmentVariable("HUH_THEME") is { } theme && Enum.TryParse<ElementTheme>(theme, true, out var t))
            Root.RequestedTheme = t;
        SystemBackdrop = new AlwaysActiveAcrylic();
        Bind(facts);
        BuildManualActions();
        if (Environment.GetEnvironmentVariable("HUH_EXPAND_DETAILS") == "1") SetDetailsExpanded(true);

        // Created before anything that can call Reposition() (which may start it).
        _restack = DispatcherQueue.CreateTimer();
        _restack.Interval = TimeSpan.FromMilliseconds(120);
        _restack.IsRepeating = false;
        _restack.Tick += (_, _) => { if (!_hidden) Reposition(); };

        Root.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Height - _lastHeightDip) > 0.5) Reposition();
        };

        if (prewarm) return; // nothing to follow
        _follower = new WindowFollower(Dialog.Hwnd);
        _follower.Moved += () => { if (!_hidden) Reposition(); };
        // EVENT_SYSTEM_FOREGROUND can arrive before the new foreground window has actually been raised, so re-stack now
        // and again once the z-order has settled.
        _follower.ForegroundChanged += _ => { if (!_hidden) { Reposition(); _restack.Start(); } };
        _follower.Minimized += HideWithDialog;
        _follower.Hidden += HideWithDialog;
        _follower.Restored += ShowWithDialog;
        _follower.Shown += ShowWithDialog;
    }

    // ------------------------------------------------------------------ placement / lifecycle

    private void HideWithDialog() { DevLog.Write($"bubble 0x{Hwnd:X}: hide with dialog 0x{Dialog.Hwnd:X}"); _hidden = true; Screen.Hide(Hwnd); }

    private void ShowWithDialog()
    {
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
        DevLog.Write($"bubble 0x{Hwnd:X}: anchor={LastAnchor.Left},{LastAnchor.Top},{LastAnchor.Right},{LastAnchor.Bottom}");
        if (Motion.Enabled) PlayEntrance(_side);
        LogFirstFrame();
        Screen.ShowNoActivate(Hwnd);
        return true;
    }

    public bool Reposition()
    {
        var frame = Screen.IsVisible(Dialog.Hwnd) ? Screen.FrameBounds(Dialog.Hwnd) : null;
        if (frame is null) return false;
        _lastHeightDip = MeasureHeightDip();
        // Embedded dialogs (browser alerts) are anchored to their own rectangle inside the host window, not the whole window.
        var anchor = Dialog.AnchorFor(frame.Value);
        LastAnchor = anchor;
        var placement = BubblePlacer.Place(anchor, WidthDip, _lastHeightDip, Screen.GetMonitors());
        Screen.PlaceAbove(Hwnd, Dialog.Hwnd, placement.Rect);
        _side = placement.Side;
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

    // ------------------------------------------------------------------ content

    private void Bind(DialogFacts f)
    {
        var d = f.Dialog;
        var src = f.Source;

        if (d.BlockedByUipi)
        {
            SetIcon(SeverityIcon, GlyphLock, "SystemFillColorCautionBrush");
            Headline.Text = $"{src.DisplayName} is running as administrator";
            ShowExcerpt("Windows doesn't let huh read dialogs from admin apps unless huh also runs as admin (it doesn't, by default)."
                        + (d.Title.Length > 0 ? $" The dialog is titled “{d.Title}”." : ""));
        }
        else
        {
            bool warning = d.Severity == DialogSeverity.Warning;
            SetIcon(SeverityIcon, warning ? GlyphWarningFull : GlyphErrorFull, warning ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush");
            Headline.Text = f.Headline;

            foreach (var code in f.Codes.Take(2)) CodesPanel.Children.Add(CodeRow(code, d.Message));
            if (f.Codes.Count == 0) ShowExcerpt(RestOfMessage(d.Message, f.Headline));
        }

        BindSource(src);
        BuildDetails(f);
    }

    private void ShowExcerpt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Excerpt.Text = text;
        Excerpt.Visibility = Visibility.Visible;
    }

    /// <summary>Dialog text after the headline sentence (so the excerpt adds information instead of repeating it).</summary>
    private static string? RestOfMessage(string message, string headline)
    {
        var m = message.Trim();
        int i = m.IndexOf(headline, StringComparison.Ordinal);
        var rest = i >= 0 ? m[(i + headline.Length)..].TrimStart('.', ' ', '\r', '\n') : m;
        rest = string.Join(' ', rest.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return rest.Length > 0 ? rest : null;
    }

    /// <summary>One code: a monospace chip ("0x80070005") + name and Windows' own explanation.</summary>
    private static UIElement CodeRow(DecodedCode c, string dialogMessage)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var chip = new Border
        {
            Background = Brush("SubtleFillColorSecondaryBrush"),
            BorderBrush = Brush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 2),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = c.Display, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 },
        };
        grid.Children.Add(chip);

        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        Grid.SetColumn(text, 1);
        string label = c.Name ?? c.KindLabel;
        text.Inlines.Add(new Run { Text = label, FontWeight = FontWeights.SemiBold });
        // Don't repeat what the dialog already says word-for-word; then the kind label is the useful part.
        if (c.Message is { } msg && !FactsBuilder.AlreadySays(dialogMessage, msg))
            text.Inlines.Add(new Run { Text = "\n" + msg, Foreground = Brush("TextFillColorSecondaryBrush") });
        else if (c.Name is not null)
            text.Inlines.Add(new Run { Text = "\n" + c.KindLabel, Foreground = Brush("TextFillColorSecondaryBrush") });
        grid.Children.Add(text);
        return grid;
    }

    private void BindSource(SourceInfo s)
    {
        SourceName.Text = s.Kind switch
        {
            SourceKind.Windows => "Windows" + (s.OnBehalfOf is { } exe ? $" · about {exe}" : ""),
            SourceKind.WebPage => s.WebOrigin ?? "A web page",
            _ => s.DisplayName + (s.ExeName is { } e && !s.DisplayName.Equals(Path.GetFileNameWithoutExtension(e), StringComparison.OrdinalIgnoreCase) ? $" ({e})" : ""),
        };
        ToolTipService.SetToolTip(SourceName, s.Path ?? s.DisplayName);

        (int glyph, string brush, string text) = s.Kind switch
        {
            SourceKind.WebPage => (GlyphGlobe, "SystemFillColorCautionBrush",
                $"From a web page in {s.Browser}. This is not a Windows message."),
            SourceKind.Windows => (GlyphWindows, "SystemFillColorSuccessBrush",
                s.OnBehalfOf is { } exe ? $"Raised by Windows itself ({s.ExeName}) because {exe} couldn't start properly"
                                        : $"Raised by Windows itself ({s.ExeName})"),
            SourceKind.StoreApp => (GlyphStore, "SystemFillColorSuccessBrush", $"Microsoft Store app by {s.Publisher ?? "unknown publisher"}"),
            _ => s.Signature.State switch
            {
                SignatureState.Signed => (GlyphCheck, "SystemFillColorSuccessBrush", $"Signed by {s.Signature.Signer}"),
                SignatureState.Unsigned => (GlyphWarning, "SystemFillColorCautionBrush",
                    "Unsigned: the publisher can't be verified" + (s.Publisher is { } p ? $" (claims to be {p})" : "")),
                SignatureState.Invalid => (GlyphError, "SystemFillColorCriticalBrush", $"Invalid signature: {s.Signature.Detail}"),
                _ => (GlyphUnknown, "TextFillColorTertiaryBrush", "Signature couldn't be checked"),
            },
        };
        SetIcon(BadgeIcon, glyph, brush);
        BadgeText.Text = text;
        BadgeText.Foreground = Brush(s.Kind == SourceKind.WebPage || s.Signature.State is SignatureState.Unsigned or SignatureState.Invalid
            ? brush : "TextFillColorSecondaryBrush");
    }

    // ------------------------------------------------------------------ details

    private void BuildDetails(DialogFacts f)
    {
        var d = f.Dialog;
        var s = f.Source;
        Section("Dialog");
        Line("Title", d.Title.Length > 0 ? d.Title : "(none)");
        Line("Message", d.Message.Length > 0 ? d.Message : "(none)");
        if (d.Buttons.Count > 0) Line("Buttons", string.Join(", ", d.Buttons));

        if (f.Codes.Count > 0)
        {
            Section("Error codes");
            foreach (var c in f.Codes)
                Line(c.Display, $"{c.KindLabel}{(c.Name is { } n ? " · " + n : "")}\n{c.Message ?? "Windows has no description for this code."}");
        }

        Section("Source");
        Line("Program", s.DisplayName + (s.ExeName is { } e ? $" ({e})" : ""));
        if (s.OnBehalfOf is not null) Line("About", s.OnBehalfOf);
        if (s.Kind == SourceKind.WebPage) Line("Web page", $"{s.WebOrigin ?? "unknown origin"} in {s.Browser}");
        Line("Publisher", s.Publisher is null ? "unknown" : s.Publisher + (s.PublisherVerified ? " (verified)" : " (unverified: from the file's version info)"));
        Line("Signature", s.Signature.State switch
        {
            SignatureState.Signed => $"Valid, signed by {s.Signature.Signer}" + (s.Signature.ViaCatalog ? " (Windows catalog)" : s.Signature.Detail is { } det ? $" ({det})" : " (embedded)"),
            SignatureState.Unsigned => "None",
            SignatureState.Invalid => $"Invalid: {s.Signature.Detail}",
            _ => $"Not checked: {s.Signature.Detail}",
        });
        Line("Path", s.Path ?? "unavailable");
        Line("Process", $"PID {d.Process.Pid}, {d.Process.Elevation switch { ElevationState.Elevated => "running as administrator", ElevationState.NotElevated => "not elevated", _ => "elevation unknown" }}");
        Line("Window", $"{d.Kind}, class {d.ClassName}");
    }

    private void Section(string title)
    {
        var p = new Paragraph { Margin = new Thickness(0, DetailsText.Blocks.Count == 0 ? 0 : 8, 0, 2) };
        p.Inlines.Add(new Run { Text = title.ToUpperInvariant(), FontWeight = FontWeights.SemiBold, FontSize = 11, Foreground = Brush("TextFillColorTertiaryBrush") });
        DetailsText.Blocks.Add(p);
    }

    private void Line(string label, string value)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
        p.Inlines.Add(new Run { Text = label + ": ", FontWeight = FontWeights.SemiBold });
        p.Inlines.Add(new Run { Text = value, Foreground = Brush("TextFillColorSecondaryBrush") });
        DetailsText.Blocks.Add(p);
    }

    private void OnToggleDetails(object sender, RoutedEventArgs e) => SetDetailsExpanded(DetailsPanel.Visibility != Visibility.Visible);

    private void SetDetailsExpanded(bool expanded)
    {
        DetailsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        DetailsToggleText.Text = expanded ? "Hide details" : "Details";
        DetailsChevron.Glyph = G(expanded ? GlyphChevronUp : GlyphChevronDown);
        // Height changes -> Root.SizeChanged -> Reposition() (may flip sides if it no longer fits).
    }

    // ------------------------------------------------------------------ AI section (M4)

    /// <summary>User clicked "Explain this with AI": the app shows the consent screen.</summary>
    public event Action<BubbleWindow>? AiOptInRequested;

    private const int GlyphSparkle = 0xE945, GlyphInfo = 0xE946, GlyphShield = 0xEA18, GlyphFolder = 0xE8B7, GlyphSettings = 0xE713,
        GlyphCopy = 0xE8C8, GlyphSearch = 0xE721, GlyphAdmin = 0xE7EF;

    internal void SetAiDisabled()
    {
        if (Dialog.BlockedByUipi) { AiPanel.Visibility = Visibility.Collapsed; return; }
        ResetAi();
        AiPanel.Visibility = Visibility.Visible;
        AiHeader.Text = "AI explanations are off";
        AiOptIn.Visibility = Visibility.Visible;
    }

    internal void SetAiLoading(string model)
    {
        ResetAi();
        AiPanel.Visibility = Visibility.Visible;
        AiHeader.Text = $"Asking Gemini ({model})…";
        AiProgress.IsActive = true;
    }

    internal void SetAi(Huh.Core.AI.AiOutcome o)
    {
        ResetAi();
        AiPanel.Visibility = Visibility.Visible;
        if (o.Status == Huh.Core.AI.AiStatus.Disabled) { SetAiDisabled(); return; }

        if (o.Status == Huh.Core.AI.AiStatus.Unavailable || o.Explanation is null)
        {
            SetIcon(AiIcon, GlyphInfo, "TextFillColorTertiaryBrush");
            AiHeader.Text = o.UnavailableText; // M3 info above and below stays exactly as it was
            AiHeader.TextWrapping = TextWrapping.Wrap;
            AddAiDetails(o);
            return;
        }

        var e = o.Explanation;
        SetIcon(AiIcon, GlyphSparkle, "AccentTextFillColorPrimaryBrush");
        AiHeader.Text = "Gemini explains" + (o.FromCache ? " (saved answer)" : "");
        if (o.SuspectedInjection)
        {
            // The dialog tried to instruct AI tools: say so loudly, keep the explanation visible but offer nothing to click.
            SetIcon(AiIcon, GlyphShield, "SystemFillColorCautionBrush");
            AiHeader.Text = "Caution: this message tries to give instructions to AI tools";
            AiHeader.TextWrapping = TextWrapping.Wrap;
            AiHeader.Foreground = Brush("SystemFillColorCautionBrush");
        }
        AiCause.Text = e.Cause;
        AiCause.Visibility = Visibility.Visible;
        for (int i = 0; i < e.FixSteps.Count; i++)
            AiSteps.Children.Add(new TextBlock { Text = $"{i + 1}. {e.FixSteps[i]}", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        AiSteps.Visibility = e.FixSteps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (o.Action?.Action is { } a)
        {
            _aiAction = a;
            AiActionText.Text = a.Label + "…";
            AiActionIcon.Glyph = G(IconFor(a.Id));
            AutomationProperties.SetName(AiActionChip, a.Label);
            AiActionChip.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(AiActionChip, "Suggested fix, checked by huh. You'll see exactly what happens before anything happens.");
        }
        else if (o.SuspectedInjection)
        {
            AiNote.Text = "Treat the advice above with suspicion. Real error messages don't address AI tools, so huh offers no one-click action here.";
            AiNote.Foreground = Brush("SystemFillColorCautionBrush");
            AiNote.Visibility = Visibility.Visible;
        }
        else if (o.Action?.Hint is { } hint)
        {
            // e.g. re-run rejected for a Windows process: point at the dialog's own button instead.
            AiNote.Text = "Tip: " + hint;
            AiNote.Foreground = Brush("TextFillColorPrimaryBrush");
            AiNote.Visibility = Visibility.Visible;
        }
        else if (o.Action?.RejectedReason is { } why)
        {
            AiNote.Text = $"huh ignored the AI's suggested action: {why}.";
            AiNote.Foreground = Brush("SystemFillColorCautionBrush");
            AiNote.Visibility = Visibility.Visible;
        }
        AddAiDetails(o);
        if (Motion.Enabled) Motion.FadeIn(AiPanel, 180);
    }

    private void ResetAi()
    {
        AiProgress.IsActive = false;
        AiCause.Visibility = AiSteps.Visibility = AiActionChip.Visibility = AiNote.Visibility = AiOptIn.Visibility = Visibility.Collapsed;
        AiSteps.Children.Clear();
        AiHeader.TextWrapping = TextWrapping.NoWrap;
        AiHeader.Foreground = Brush("TextFillColorSecondaryBrush");
        SetIcon(AiIcon, GlyphSparkle, "AccentTextFillColorPrimaryBrush");
    }

    private void AddAiDetails(Huh.Core.AI.AiOutcome o)
    {
        if (_aiDetailsAdded) return;
        _aiDetailsAdded = true;
        Section("AI");
        Line("Status", o.Status == Huh.Core.AI.AiStatus.Ok
            ? $"answered by {o.Model}{(o.FromCache ? " (saved answer, no request sent)" : $" in {o.LatencyMs:F0} ms")}"
            : $"{o.Reason}{(o.Detail is { } d ? ": " + d : "")}");
        if (o.Explanation is { } e)
        {
            Line("Confidence", $"{e.Confidence:P0}");
            Line("Suggested action", o.Action?.Action is { } a ? $"{a.Label} (validated{(a.RequiresOverride ? ", needs unsigned override" : "")})"
                : o.Action?.RejectedReason is { } why ? $"rejected: {why}" : "none");
        }
        Line("Sent to Gemini", "dialog text and the facts above, with your user name, user-folder paths, computer name and e-mail addresses removed");
    }

    private bool _aiDetailsAdded;

    private void OnAiOptIn(object sender, RoutedEventArgs e) => AiOptInRequested?.Invoke(this);

    // ------------------------------------------------------------------ actions (M5): click -> confirmation -> run

    private Huh.Core.Actions.ValidatedAction? _aiAction;
    private Huh.Core.Actions.ValidatedAction? _pending;

    private static int IconFor(Huh.Core.Actions.FixActionId id) => id switch
    {
        Huh.Core.Actions.FixActionId.OpenSettings => GlyphSettings,
        Huh.Core.Actions.FixActionId.OpenFolder => GlyphFolder,
        Huh.Core.Actions.FixActionId.RerunAsAdmin => GlyphAdmin,
        Huh.Core.Actions.FixActionId.CopyErrorText => GlyphCopy,
        _ => GlyphSearch,
    };

    /// <summary>Always-available actions (no AI needed): Copy, Search, and Run as admin when eligible (unsigned => red override).</summary>
    private void BuildManualActions()
    {
        foreach (var a in Huh.Core.Actions.FixActions.ManualActions(Facts, _redactor))
        {
            var label = a.Id switch
            {
                Huh.Core.Actions.FixActionId.CopyErrorText => "Copy",
                Huh.Core.Actions.FixActionId.SearchWeb => "Search",
                Huh.Core.Actions.FixActionId.RerunAsAdmin => "Run as admin…",
                _ => a.Label,
            };
            var link = new HyperlinkButton
            {
                Padding = new Thickness(6, 2, 6, 2),
                Content = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] },
            };
            if (a.RequiresOverride) link.Foreground = Brush("SystemFillColorCriticalBrush");
            ToolTipService.SetToolTip(link, a.Label + (a.RequiresOverride ? " (unsigned: needs your explicit confirmation)" : ""));
            AutomationProperties.SetName(link, a.Label);
            var action = a;
            link.Click += (_, _) => Request(action);
            ManualActions.Children.Add(link);
        }
    }

    private void OnAiAction(object sender, RoutedEventArgs e) { if (_aiAction is not null) Request(_aiAction); }

    /// <summary>Copy runs at once; everything else shows the confirmation with exactly what will happen.</summary>
    private void Request(Huh.Core.Actions.ValidatedAction a)
    {
        if (!a.RequiresConfirmation) { Execute(a, acknowledgedUnsigned: false); return; }
        _pending = a;
        var c = Huh.Core.Actions.Confirmations.For(a, Facts);
        ConfirmTitle.Text = c.Title;
        ConfirmDetails.Children.Clear();
        foreach (var (label, value) in c.Details)
        {
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            t.Inlines.Add(new Run { Text = label + ": ", FontWeight = FontWeights.SemiBold });
            bool mono = label is "Path" or "Folder" || (label == "Opens" && value.Contains(':') && !value.Contains(' '));
            t.Inlines.Add(new Run { Text = value, FontFamily = mono ? new FontFamily("Cascadia Mono, Consolas") : FontFamily.XamlAutoFontFamily });
            if (c.Danger && label is "Path" or "Publisher") t.Foreground = Brush("SystemFillColorCriticalBrush");
            ConfirmDetails.Children.Add(t);
        }
        ConfirmNote.Text = c.Note ?? "";
        ConfirmGo.Content = c.ConfirmText;

        ConfirmAck.IsChecked = false;
        ConfirmAck.Visibility = c.AcknowledgeText is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmAckText.Text = c.AcknowledgeText ?? "";
        AutomationProperties.SetName(ConfirmAck, c.AcknowledgeText ?? "");
        ConfirmGo.IsEnabled = c.AcknowledgeText is null; // the deliberate second step for the unsigned override

        if (c.Danger)
        {
            ConfirmPanel.Background = Brush("SystemFillColorCriticalBackgroundBrush");
            ConfirmPanel.BorderBrush = Brush("SystemFillColorCriticalBrush");
            ConfirmPanel.BorderThickness = new Thickness(2);
            ConfirmTitle.Foreground = Brush("SystemFillColorCriticalBrush");
            SetIcon(ConfirmIcon, GlyphWarningFull, "SystemFillColorCriticalBrush");
            ConfirmGo.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
            ConfirmGo.Foreground = Brush("SystemFillColorCriticalBrush");
        }
        else
        {
            ConfirmPanel.Background = Brush("ControlFillColorDefaultBrush");
            ConfirmPanel.BorderBrush = Brush("ControlStrokeColorDefaultBrush");
            ConfirmPanel.BorderThickness = new Thickness(1);
            ConfirmTitle.Foreground = Brush("TextFillColorPrimaryBrush");
            SetIcon(ConfirmIcon, IconFor(a.Id), "AccentTextFillColorPrimaryBrush");
            ConfirmGo.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            ConfirmGo.ClearValue(Control.ForegroundProperty);
            ConfirmGo.ClearValue(Control.BackgroundProperty);
        }
        ActionResult.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Visible; // height changes -> Reposition()
        if (Motion.Enabled) Motion.FadeIn(ConfirmPanel, 150);
    }

    private void OnAckChanged(object sender, RoutedEventArgs e)
    {
        bool ok = ConfirmAck.IsChecked == true;
        ConfirmGo.IsEnabled = ok;
        // Make the armed state unmistakable: solid red once the user has acknowledged the warning.
        if (ok)
        {
            ConfirmGo.Background = Brush("SystemFillColorCriticalBrush");
            ConfirmGo.Foreground = Brush("TextOnAccentFillColorPrimaryBrush");
        }
        else
        {
            ConfirmGo.ClearValue(Control.BackgroundProperty);
            ConfirmGo.Foreground = Brush("SystemFillColorCriticalBrush");
        }
    }

    private void OnConfirmCancel(object sender, RoutedEventArgs e)
    {
        _pending = null;
        ConfirmPanel.Visibility = Visibility.Collapsed;
    }

    private void OnConfirmGo(object sender, RoutedEventArgs e)
    {
        if (_pending is not { } a) return;
        bool ack = ConfirmAck.Visibility == Visibility.Visible && ConfirmAck.IsChecked == true;
        _pending = null;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        Execute(a, ack);
    }

    private void Execute(Huh.Core.Actions.ValidatedAction a, bool acknowledgedUnsigned)
    {
        ShowResult("Working…", "TextFillColorSecondaryBrush");
        DevLog.Write($"bubble 0x{Hwnd:X}: action {a.Id} requested (override={a.RequiresOverride}, ack={acknowledgedUnsigned})");
        var facts = Facts;
        // Off the UI thread: the UAC prompt blocks ShellExecute until the user answers.
        _ = Task.Run(() => _runner.Run(a, facts, acknowledgedUnsigned)).ContinueWith(t =>
        {
            var o = t.IsCompletedSuccessfully ? t.Result
                : new Huh.Core.Actions.RunOutcome(Huh.Core.Actions.RunResult.Failed, "Something went wrong.");
            DevLog.Write($"bubble 0x{Hwnd:X}: action {a.Id} -> {o.Result}");
            DispatcherQueue.TryEnqueue(() => ShowResult(o.Message, o.Result switch
            {
                Huh.Core.Actions.RunResult.Done => "SystemFillColorSuccessBrush",
                Huh.Core.Actions.RunResult.Cancelled => "TextFillColorSecondaryBrush",
                _ => "SystemFillColorCautionBrush",
            }));
        }, TaskScheduler.Default);
    }

    private void ShowResult(string text, string brush)
    {
        ActionResult.Text = text;
        ActionResult.Foreground = Brush(brush);
        ActionResult.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ motion + pre-warm (M6)

    /// <summary>Slides ~14 DIP out from the dialog's side while fading in. Transform + opacity run on the compositor.</summary>
    private void PlayEntrance(BubbleSide side)
    {
        const double d = 14;
        var (x, y) = side switch
        {
            BubbleSide.Right => (-d, 0.0),
            BubbleSide.Left => (d, 0.0),
            BubbleSide.Below => (0.0, -d),
            BubbleSide.Above => (0.0, d),
            _ => (0.0, 6.0),
        };
        var t = new TranslateTransform { X = x, Y = y };
        Root.RenderTransform = t;
        Root.Opacity = 0;
        var sb = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Motion.Add(sb, t, "X", 0, 220, ease);
        Motion.Add(sb, t, "Y", 0, 220, ease);
        Motion.Add(sb, Root, "Opacity", 1, 160, null);
        sb.Begin();
    }

    /// <summary>Logs when the bubble's first frame is rendered, relative to the dialog's first window event (for the pre-warm numbers).</summary>
    private void LogFirstFrame()
    {
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            var sinceRead = (DateTimeOffset.Now - Dialog.DetectedAt).TotalMilliseconds;
            DevLog.Write($"bubble 0x{Hwnd:X}: first frame {Dialog.LatencyMs + sinceRead:F0} ms after the dialog appeared ({sinceRead:F0} ms after it was read)");
        };
        CompositionTarget.Rendering += handler;
    }

    /// <summary>
    /// Builds and renders one bubble for a sample dialog while cloaked and off-screen, then closes it, so that XAML parsing,
    /// control templates, fonts and the acrylic backdrop are warm before the first real error. Nothing appears on screen.
    /// </summary>
    internal static void Prewarm(Huh.Core.Privacy.Redactor redactor)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        BubbleWindow w;
        try
        {
            w = new BubbleWindow(Huh.Core.AI.SampleFacts.Create(), redactor, prewarm: true);
            Screen.Cloak(w.Hwnd, true);
            Screen.MoveTo(w.Hwnd, PxRect.FromSize(-32000, -32000, 425, 400));
            w.SetAiLoading("prewarm");
            w.Request(Huh.Core.Actions.FixActions.ManualActions(w.Facts, redactor).First(a => a.RequiresConfirmation));
            if (Motion.Enabled) w.PlayEntrance(BubbleSide.Right);
            Screen.ShowNoActivate(w.Hwnd);
        }
        catch (Exception ex) { DevLog.Write($"prewarm (bubble) failed: {ex}"); return; }

        int frames = 0;
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            if (++frames < 3) return;
            CompositionTarget.Rendering -= handler;
            DevLog.Write($"prewarm: bubble rendered (cloaked) in {sw.ElapsedMilliseconds} ms");
            w.CloseBubble();
        };
        CompositionTarget.Rendering += handler;
    }

    // ------------------------------------------------------------------ helpers

    private static string G(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static void SetIcon(FontIcon icon, int glyph, string brushKey)
    {
        icon.Glyph = G(glyph);
        icon.Foreground = Brush(brushKey);
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private void OnDismiss(object sender, RoutedEventArgs e) => Dismissed?.Invoke(this);

    public void CloseBubble()
    {
        if (_closing) return;
        _closing = true;
        _restack.Stop();
        _follower?.Dispose();
        if (!Motion.Enabled || _hidden || _prewarm) { Close(); return; }
        // Short fade-out; the window is gone ~120 ms after its dialog.
        var sb = Motion.Fade(Root, 0, 120);
        sb.Completed += (_, _) => Close();
        sb.Begin();
    }
}
