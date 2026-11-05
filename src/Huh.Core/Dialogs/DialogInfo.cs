namespace Huh.Core.Dialogs;

/// <summary>How a detected window was recognised as a dialog.</summary>
public enum DialogKind
{
    Unknown,
    /// <summary>Classic Win32 dialog (window class #32770), including MessageBox.</summary>
    Win32Dialog,
    /// <summary>Comctl32 TaskDialog (a #32770 host containing a DirectUIHWND).</summary>
    TaskDialog,
    /// <summary>Some other top-level window (custom error windows, UIA IsDialog windows).</summary>
    OtherWindow,
}

/// <summary>Severity inferred from icon and text cues.</summary>
public enum DialogSeverity
{
    None,
    Information,
    Question,
    Warning,
    Error,
}

/// <summary>Which event source surfaced the dialog first.</summary>
public enum DetectionSource { WinEvent, UiAutomation }

/// <summary>Immutable snapshot of a dialog read through UI Automation. Never contains pixels.</summary>
public sealed record DialogInfo
{
    public required nint Hwnd { get; init; }
    public required ProcessDetails Process { get; init; }
    public string ClassName { get; init; } = "";
    public DialogKind Kind { get; init; }
    public DialogSeverity Severity { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public IReadOnlyList<string> Buttons { get; init; } = [];
    /// <summary>Human-readable reasons for the classification, e.g. "icon:error", "text:denied", "code:0x80070005".</summary>
    public IReadOnlyList<string> Signals { get; init; } = [];
    /// <summary>Raw UIA subtree, kept for diagnostics (Probe --verbose). Text only.</summary>
    public IReadOnlyList<UiaNode> Nodes { get; init; } = [];
    /// <summary>True when the dialog's content could not be read because its process runs at a higher integrity level (UIPI).</summary>
    public bool BlockedByUipi { get; init; }
    public DetectionSource Source { get; init; }
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.Now;
    /// <summary>Milliseconds from the first window event to a fully read + classified dialog.</summary>
    public double LatencyMs { get; init; }

    /// <summary>The dialogs huh acts on: errors and warnings.</summary>
    public bool IsError => Severity is DialogSeverity.Error or DialogSeverity.Warning;
}
