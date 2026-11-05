namespace Huh.Core.Dialogs;

/// <summary>How a detected window was recognised as a dialog.</summary>
public enum DialogKind
{
    Unknown,
    /// <summary>Classic Win32 dialog (window class #32770), including MessageBox.</summary>
    Win32Dialog,
    /// <summary>Comctl32 TaskDialog (a #32770 host containing a DirectUIHWND).</summary>
    TaskDialog,
    /// <summary>Found only via UI Automation (XAML/UWP/Chromium dialogs).</summary>
    UiaDialog,
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

/// <summary>Immutable snapshot of a dialog read through UI Automation / Win32. Never contains pixels.</summary>
public sealed record DialogInfo
{
    public required nint Hwnd { get; init; }
    public required int ProcessId { get; init; }
    public string? ProcessName { get; init; }
    public string? ProcessPath { get; init; }
    public DialogKind Kind { get; init; }
    public DialogSeverity Severity { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public IReadOnlyList<string> Buttons { get; init; } = [];
    /// <summary>True when the dialog's process is elevated and UIPI blocked reading it.</summary>
    public bool BlockedByUipi { get; init; }
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.Now;
}
