using System.Text.RegularExpressions;

namespace Huh.Core.Dialogs;

/// <summary>What the classifier gets: everything is text or metadata, never pixels.</summary>
/// <param name="Icon">Icon type from Win32 metadata (MessageBox icon resource id), or None if unknown.</param>
internal sealed record DialogEvidence(string ClassName, string Title, IReadOnlyList<UiaNode> Nodes, bool? IsDialog, DialogSeverity Icon = DialogSeverity.None);

internal sealed record Classification(DialogKind Kind, DialogSeverity Severity, string Message, IReadOnlyList<string> Buttons, IReadOnlyList<string> Signals);

/// <summary>
/// Pure, deterministic classifier. Rules (in order):
///  1. Error cues (words, error codes, error icon) => Error.
///  2. A question/confirmation phrasing without error cues => Question, even with a warning icon
///     ("Are you sure you want to delete?" is not an error).
///  3. Warning cues => Warning.  4. Success/info cues or info icon => Information.  5. Otherwise None.
/// </summary>
internal static partial class DialogClassifier
{
    // English-only for now; localized dialogs rely on the icon signal (see NOTES.md).
    [GeneratedRegex(@"\b(error|errors|fail(ed|ure|s)?|fatal|denied|cannot|can't|can ?not|could ?n[o']t|unable|not found|can't find|cannot find|missing|invalid|corrupt(ed)?|crash(ed)?|stopped working|not responding|exception|problem|refused|expired|not ready|not enough|insufficient|violation|unexpected|unhandled|blocked|couldn't|wasn't able|went wrong|not (be )?(read|written|opened|loaded|found|available|connected)|(do ?n[o']t|doesn't|does not|don't) have (the )?(required |necessary )?(permissions?|access|rights)|permission (is )?(denied|required)|not (allowed|permitted|authori[sz]ed)|isn't allowed|unauthori[sz]ed)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorWords();

    // 0x80070005, 0xC0000005, 0xc000007b; "error 1603", "code 43", "Error code: 0x80004005"
    [GeneratedRegex(@"\b0x[0-9a-f]{8}\b|\b(error|code|err)\s*(code)?\s*[:#]?\s*(0x)?[0-9a-f]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCode();

    [GeneratedRegex(@"\b(warning|caution|attention|administrator permission|requires elevation)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarningWords();

    [GeneratedRegex(@"^\s*(do you want|are you sure|would you like|save changes|do you really|confirm)\b|\?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex QuestionPhrasing();

    [GeneratedRegex(@"\b(success(ful(ly)?)?|completed|finished|is available|up to date|saved|version \d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InfoWords();

    private static readonly string[] TitleBarIdPrefixes = ["Close", "Minimize", "Maximize", "Restore", "SystemMenuBar"];

    // Caption buttons: AutomationId "Close", "Minimize-Restore", "Maximize-Restore"... and no window class of their own.
    private static bool IsTitleBarButton(UiaNode n) =>
        n.ClassName.Length == 0 && TitleBarIdPrefixes.Any(p => n.AutomationId.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static Classification Classify(DialogEvidence ev)
    {
        var signals = new List<string>();

        var kind = ev.ClassName == "#32770"
            ? ev.Nodes.Any(IsTaskDialogPart) ? DialogKind.TaskDialog : DialogKind.Win32Dialog
            : DialogKind.OtherWindow;
        signals.Add($"class:{ev.ClassName}");
        if (ev.IsDialog == true) signals.Add("uia:isdialog");

        var buttons = new List<string>();
        var texts = new List<string>();
        var icon = ev.Icon;
        foreach (var n in ev.Nodes)
        {
            if (n.ControlType is "TitleBar" or "MenuBar" or "MenuItem" || IsTitleBarButton(n)) continue;
            if (string.IsNullOrWhiteSpace(n.Name)) continue;
            switch (n.ControlType)
            {
                case "Button" or "SplitButton":
                    if (!buttons.Contains(n.Name)) buttons.Add(n.Name);
                    break;
                case "Text" or "Hyperlink" or "Document" or "Edit":
                    if (n.Name != ev.Title && !texts.Contains(n.Name)) texts.Add(n.Name);
                    break;
                case "Image":
                    icon = Max(icon, IconFromName(n.Name));
                    break;
            }
        }
        var message = string.Join("\n", texts);
        // Match on typographic-quote-normalised text (Windows 11 dialogs use U+2019 in "don’t", "can’t"...).
        var haystack = NormalizeQuotes(ev.Title + "\n" + message);

        if (icon != DialogSeverity.None) signals.Add($"icon:{icon.ToString().ToLowerInvariant()}");
        var errWord = ErrorWords().Match(haystack);
        if (errWord.Success) signals.Add($"text:{errWord.Value.ToLowerInvariant()}");
        var code = ErrorCode().Match(haystack);
        if (code.Success) signals.Add($"code:{code.Value}");
        bool question = QuestionPhrasing().IsMatch(message);
        if (question) signals.Add("phrasing:question");
        var warnWord = WarningWords().Match(haystack);
        if (warnWord.Success) signals.Add($"text:{warnWord.Value.ToLowerInvariant()}");
        bool info = InfoWords().IsMatch(haystack);

        DialogSeverity severity;
        if (errWord.Success || code.Success || icon == DialogSeverity.Error) severity = DialogSeverity.Error;
        else if (question) severity = DialogSeverity.Question;
        else if (warnWord.Success || icon == DialogSeverity.Warning) severity = DialogSeverity.Warning;
        else if (info || icon == DialogSeverity.Information) severity = DialogSeverity.Information;
        else severity = icon; // Question or None

        return new Classification(kind, severity, message, buttons, signals);
    }

    private static DialogSeverity IconFromName(string name) => name.ToLowerInvariant() switch
    {
        var s when s.Contains("error") || s.Contains("critical") || s.Contains("stop") => DialogSeverity.Error,
        var s when s.Contains("warning") || s.Contains("exclamation") => DialogSeverity.Warning,
        var s when s.Contains("information") || s.Contains("asterisk") => DialogSeverity.Information,
        var s when s.Contains("question") => DialogSeverity.Question,
        _ => DialogSeverity.None,
    };

    internal static string NormalizeQuotes(string s) =>
        s.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"');

    private static DialogSeverity Max(DialogSeverity a, DialogSeverity b) => a >= b ? a : b;

    // UIA hides the DirectUIHWND class inside TaskDialogs, but its parts have stable AutomationIds.
    private static bool IsTaskDialogPart(UiaNode n) =>
        n.ClassName is "DirectUIHWND" or "CCPushButton"
        || n.AutomationId is "MainInstruction" or "ContentText" or "MainIcon" or "ExpandoButton" or "FootnoteText"
        || n.AutomationId.StartsWith("CommandButton_", StringComparison.Ordinal)
        || n.AutomationId.StartsWith("CommandLink_", StringComparison.Ordinal);

    /// <summary>Cheap title-only test used to decide whether a non-#32770 window is worth reading at all.</summary>
    public static bool TitleLooksLikeError(string title) =>
        title.Length > 0 && (ErrorWords().IsMatch(NormalizeQuotes(title)) || WarningWords().IsMatch(title));
}
