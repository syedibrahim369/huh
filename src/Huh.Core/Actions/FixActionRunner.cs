using System.ComponentModel;
using System.Text;
using Huh.Core.Facts;
using Huh.Core.Privacy;
using Huh.Core.Provenance;

namespace Huh.Core.Actions;

/// <summary>
/// Exactly what a confirmation shows BEFORE anything happens: what will run/open, the full path, the publisher/signature,
/// the settings page name. Built here (not in XAML) so it is unit-tested.
/// </summary>
/// <param name="AcknowledgeText">When set, a checkbox with this text must be ticked before the confirm button enables.</param>
public sealed record Confirmation(string Title, IReadOnlyList<(string Label, string Value)> Details, string ConfirmText,
    bool Danger, string? AcknowledgeText, string? Note);

public static class Confirmations
{
    public static Confirmation For(ValidatedAction a, DialogFacts f)
    {
        var src = f.Source;
        switch (a.Id)
        {
            case FixActionId.OpenSettings:
            {
                var key = a.Target!["ms-settings:".Length..];
                return new Confirmation("Open a Settings page?",
                    [("Page", $"Settings › {FixActions.SettingsPages[key]}"), ("Opens", a.Target!)],
                    "Open Settings", Danger: false, null, "Only opens the page. huh doesn't change any setting.");
            }
            case FixActionId.OpenFolder:
                return new Confirmation("Open this folder in File Explorer?",
                    [("Folder", a.Target!)],
                    "Open folder", false, null, "File Explorer opens the folder. Nothing is run, moved or deleted.");
            case FixActionId.SearchWeb:
                return new Confirmation("Search the web for this error?",
                    [("Search for", a.Target!), ("Opens", "Bing, in your default browser")],
                    "Search", false, null, "Your user name, user folders, computer name and e-mail addresses are removed from the search.");
            case FixActionId.CopyErrorText:
                return new Confirmation("Copy the error text?",
                    [("Copies", "the dialog's title and message, and the decoded error codes")],
                    "Copy", false, null, "Copied to your clipboard only.");
            case FixActionId.RerunAsAdmin when a.RequiresOverride:
                return new Confirmation("Run an UNSIGNED program as administrator?",
                    [("Program", src.DisplayName), ("Path", a.Target!), ("Publisher", "UNSIGNED: nobody has verified who made this program" +
                        (src.Publisher is { } claim ? $" (it claims to be “{claim}”)" : "")), ("Arguments", "none")],
                    "Run unsigned program as administrator", Danger: true,
                    AcknowledgeText: "I understand this program is unsigned",
                    Note: "Running it as administrator gives it full control of your PC. Windows will also ask for permission.");
            case FixActionId.RerunAsAdmin:
                return new Confirmation("Re-run this program as administrator?",
                    [("Program", src.DisplayName), ("Path", a.Target!), ("Publisher", $"Signed by {src.Signature.Signer} (verified)"), ("Arguments", "none")],
                    "Re-run as administrator", false, null,
                    "Starts a new copy of the program with full access to your PC. Windows will ask for permission first.");
        }
        throw new ArgumentOutOfRangeException(nameof(a));
    }
}

/// <summary>The only side effects huh can have. Implemented by the app; faked in tests.</summary>
public interface IActionLauncher
{
    /// <summary>Opens an ms-settings: or https: URI with the shell.</summary>
    void OpenUri(string uri);
    /// <summary>Opens a folder in File Explorer (explorer.exe "folder").</summary>
    void OpenFolder(string folder);
    /// <summary>Starts exePath elevated (verb runas), NO arguments. Throws Win32Exception(1223) if the user cancels UAC.</summary>
    void RunAsAdmin(string exePath, string workingDirectory);
    void CopyText(string text);
}

public enum RunResult { Done, Cancelled, Refused, Failed }

public sealed record RunOutcome(RunResult Result, string Message);

/// <summary>
/// Executes a validated action after the user's click. Re-checks everything at click time (time-of-check vs time-of-use:
/// the folder could have been replaced by a file, the exe swapped for an unsigned one), never passes arguments, and refuses
/// the unsigned override unless the user ticked the acknowledgement.
/// </summary>
public sealed class FixActionRunner(IActionLauncher launcher, Redactor redactor)
{
    public const string SearchEngine = "https://www.bing.com/search?q=";

    public RunOutcome Run(ValidatedAction a, DialogFacts facts, bool acknowledgedUnsigned)
    {
        try
        {
            switch (a.Id)
            {
                case FixActionId.OpenSettings:
                {
                    var key = a.Target is { } t && t.StartsWith("ms-settings:", StringComparison.Ordinal) ? t["ms-settings:".Length..] : null;
                    if (key is null || !FixActions.SettingsPages.TryGetValue(key, out var label)) return Refused("that Settings page isn't on huh's list");
                    launcher.OpenUri("ms-settings:" + key);
                    return new(RunResult.Done, $"Opened Settings › {label}.");
                }
                case FixActionId.OpenFolder:
                {
                    var folder = FixActions.CheckFolder(a.Target, redactor, out var why);
                    if (folder is null) return Refused(why!);
                    launcher.OpenFolder(folder);
                    return new(RunResult.Done, $"Opened {folder}.");
                }
                case FixActionId.SearchWeb:
                {
                    // Re-redact defensively; the query was built by huh from verified facts, never by the AI.
                    var q = redactor.Redact(a.Target ?? FixActions.BuildSearchQuery(facts, redactor));
                    if (q.Length == 0) return Refused("nothing to search for");
                    launcher.OpenUri(SearchEngine + Uri.EscapeDataString(q.Length > 200 ? q[..200] : q));
                    return new(RunResult.Done, "Opened a web search in your browser.");
                }
                case FixActionId.CopyErrorText:
                    launcher.CopyText(CopyText(facts));
                    return new(RunResult.Done, "Copied the error text.");
                case FixActionId.RerunAsAdmin:
                    return RunElevated(a, facts, acknowledgedUnsigned);
            }
            return Refused("unsupported action");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: user said No at the UAC prompt
        {
            return new(RunResult.Cancelled, "Cancelled at the Windows permission prompt. Nothing was started.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new(RunResult.Failed, $"Couldn't do that: {ex.Message}");
        }
    }

    private RunOutcome RunElevated(ValidatedAction a, DialogFacts facts, bool acknowledgedUnsigned)
    {
        // Same eligibility rules as when the action was offered, evaluated again now.
        var r = FixActions.RerunEligibility(facts);
        if (!r.Allowed) return Refused(r.Reason!);
        var path = facts.Source.Path!;
        if (!string.Equals(a.Target, path, StringComparison.OrdinalIgnoreCase)) return Refused("the program path changed");

        // Re-verify the file on disk at click time: it may have been replaced since the bubble appeared.
        var sigNow = Authenticode.Verify(path);
        bool signedNow = sigNow.State == SignatureState.Signed;
        if (!a.RequiresOverride && !signedNow) return Refused($"the program's signature changed ({sigNow.State}); not starting it");
        if (!a.RequiresOverride && sigNow.Signer != facts.Source.Signature.Signer) return Refused("the program's publisher changed; not starting it");
        if (!signedNow && !(a.RequiresOverride && acknowledgedUnsigned)) return Refused("the program is unsigned and the warning wasn't acknowledged");
        if (sigNow.State == SignatureState.Invalid) return Refused($"the program's signature is invalid ({sigNow.Detail})");

        launcher.RunAsAdmin(path, Path.GetDirectoryName(path)!);
        return new(RunResult.Done, $"Started {Path.GetFileName(path)} as administrator.");
    }

    /// <summary>What "copy" puts on the clipboard (local only, so not redacted).</summary>
    public static string CopyText(DialogFacts f)
    {
        var sb = new StringBuilder();
        if (f.Dialog.Title.Length > 0) sb.AppendLine(f.Dialog.Title);
        if (f.Dialog.Message.Length > 0) sb.AppendLine(f.Dialog.Message);
        foreach (var c in f.Codes) sb.AppendLine($"{c.Display} {c.Name ?? c.KindLabel}: {c.Message ?? "no Windows description"}");
        if (f.Source.ExeName is { } exe) sb.AppendLine($"Program: {exe}");
        return sb.ToString().TrimEnd();
    }

    private static RunOutcome Refused(string why) => new(RunResult.Refused, $"huh didn't do that: {why}.");
}
