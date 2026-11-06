using Huh.Core.Facts;
using Huh.Core.Privacy;
using Huh.Core.Provenance;

namespace Huh.Core.Actions;

/// <summary>The ONLY things huh can ever do. The AI may suggest one; huh validates it; the user must click.</summary>
public enum FixActionId { OpenSettings, OpenFolder, RerunAsAdmin, CopyErrorText, SearchWeb }

/// <summary>A suggestion that passed validation. Nothing here comes from the AI verbatim except the choice of action.</summary>
/// <param name="Target">ms-settings URI, an existing local folder, the exe path, or the search query (built by huh).</param>
/// <param name="RequiresOverride">Re-run as admin for an UNSIGNED program: only allowed behind a red "unsigned" warning.</param>
public sealed record ValidatedAction(FixActionId Id, string Label, string? Target, bool RequiresConfirmation, bool RequiresOverride = false, string? OverrideReason = null);

/// <summary>Outcome of validating an AI suggestion: an action, nothing suggested, or a rejection with the reason (shown in details).</summary>
public sealed record ActionDecision(ValidatedAction? Action, string? RejectedReason)
{
    public static readonly ActionDecision None = new(null, null);
}

public static class FixActions
{
    /// <summary>Wire names used in the AI schema. Anything else is rejected.</summary>
    public static readonly IReadOnlyDictionary<string, FixActionId> WireNames = new Dictionary<string, FixActionId>(StringComparer.Ordinal)
    {
        ["open_settings"] = FixActionId.OpenSettings,
        ["open_folder"] = FixActionId.OpenFolder,
        ["rerun_as_admin"] = FixActionId.RerunAsAdmin,
        ["copy_error_text"] = FixActionId.CopyErrorText,
        ["search_web"] = FixActionId.SearchWeb,
    };

    /// <summary>Fixed list of Settings pages the AI may point to (key -> label). The URI is always "ms-settings:" + key.</summary>
    public static readonly IReadOnlyDictionary<string, string> SettingsPages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["windowsupdate"] = "Windows Update",
        ["storagesense"] = "Storage",
        ["appsfeatures"] = "Installed apps",
        ["defaultapps"] = "Default apps",
        ["troubleshoot"] = "Troubleshoot",
        ["network-status"] = "Network status",
        ["network-proxy"] = "Proxy",
        ["display"] = "Display",
        ["sound"] = "Sound",
        ["printers"] = "Printers & scanners",
        ["bluetooth"] = "Bluetooth & devices",
        ["windowsdefender"] = "Windows Security",
        ["about"] = "About this PC",
        ["recovery"] = "Recovery",
        ["dateandtime"] = "Date & time",
        ["privacy-microphone"] = "Microphone privacy",
        ["privacy-webcam"] = "Camera privacy",
        ["privacy-broadfilesystemaccess"] = "File system access",
        ["developers"] = "For developers",
        ["powersleep"] = "Power & battery",
    };

    /// <summary>
    /// Turns an AI suggestion into something huh is willing to offer, or rejects it. The AI's free-text parameter is only
    /// ever used as a KEY into a fixed list (settings) or a path that must exist locally (folder); it is never executed.
    /// </summary>
    public static ActionDecision Validate(string? wireId, string? param, DialogFacts facts, Redactor redactor)
    {
        if (string.IsNullOrWhiteSpace(wireId) || wireId == "none") return ActionDecision.None;
        if (!WireNames.TryGetValue(wireId.Trim(), out var id)) return Reject($"unknown action \"{Trunc(wireId)}\"");

        var src = facts.Source;
        // A web page must never steer huh into changing the system: the only help is copying the text.
        if (src.Kind == SourceKind.WebPage && id != FixActionId.CopyErrorText)
            return Reject("the dialog came from a web page; only \"copy text\" is allowed");

        switch (id)
        {
            case FixActionId.OpenSettings:
            {
                var key = (param ?? "").Trim();
                if (key.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) key = key["ms-settings:".Length..];
                if (!SettingsPages.TryGetValue(key, out var label)) return Reject($"settings page \"{Trunc(param)}\" is not on the allowlist");
                return Ok(new ValidatedAction(id, $"Open {label} settings", "ms-settings:" + key, RequiresConfirmation: false));
            }
            case FixActionId.OpenFolder:
            {
                var folder = CheckFolder(param, redactor, out var why);
                if (folder is null) return Reject(why!);
                var shortName = Path.GetFileName(folder.TrimEnd('\\'));
                return Ok(new ValidatedAction(id, $"Open folder {(shortName.Length > 0 ? shortName : folder)}", folder, RequiresConfirmation: false));
            }
            case FixActionId.RerunAsAdmin:
            {
                if (src.Kind != SourceKind.App) return Reject($"re-run as admin is only offered for normal programs (this is {src.Kind})");
                if (facts.Dialog.BlockedByUipi || facts.Dialog.Process.Elevation == Dialogs.ElevationState.Elevated)
                    return Reject("the program is already running as administrator");
                if (src.Path is null || !File.Exists(src.Path) || !src.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return Reject("the program's file couldn't be found");
                // Never arguments, always a confirmation. Unsigned: only behind the red "unsigned" override (user decision).
                // Never suggested for unsigned programs: the red "unsigned" override (M5) is something the user starts, not the AI.
                if (src.Signature.State != SignatureState.Signed)
                    return Reject("the program is unsigned, so huh never suggests running it as administrator");
                return Ok(new ValidatedAction(id, $"Re-run {src.DisplayName} as administrator", src.Path, RequiresConfirmation: true));
            }
            case FixActionId.CopyErrorText:
                return Ok(new ValidatedAction(id, "Copy the error text", null, RequiresConfirmation: false));
            case FixActionId.SearchWeb:
                // The query is built by huh from verified facts (redacted); the AI's parameter is ignored entirely.
                return Ok(new ValidatedAction(id, "Search the web for this error", BuildSearchQuery(facts, redactor), RequiresConfirmation: false));
        }
        return Reject("unsupported action");
    }

    internal static string? CheckFolder(string? param, Redactor redactor, out string? why)
    {
        why = null;
        if (string.IsNullOrWhiteSpace(param)) { why = "no folder given"; return null; }
        var raw = redactor.ExpandProfile(param.Trim().Trim('"'));
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal)) { why = "network and device paths are not allowed"; return null; }
        if (raw.Contains('%') || raw.Contains('$')) { why = "unexpanded variables are not allowed"; return null; }
        if (raw.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || raw.Contains('*') || raw.Contains('?')) { why = "invalid path"; return null; }
        if (!Path.IsPathFullyQualified(raw)) { why = "folder must be a full local path"; return null; }
        string full;
        try { full = Path.GetFullPath(raw); } catch { why = "invalid path"; return null; }
        try
        {
            if (new DriveInfo(Path.GetPathRoot(full)!).DriveType is not (DriveType.Fixed or DriveType.Removable)) { why = "only local drives are allowed"; return null; }
        }
        catch { why = "invalid drive"; return null; }
        if (File.Exists(full)) { why = "that is a file, not a folder (huh never opens or runs files)"; return null; }
        if (!Directory.Exists(full)) { why = "folder does not exist"; return null; }
        return full;
    }

    public static string BuildSearchQuery(DialogFacts facts, Redactor redactor)
    {
        var parts = new List<string>();
        if (facts.Source.ExeName is { } exe && facts.Source.Kind != SourceKind.WebPage) parts.Add(exe);
        if (facts.Codes.FirstOrDefault() is { } c) parts.Add(c.Display + (c.Name is { } n ? " " + n : ""));
        else parts.Add(facts.Headline);
        return redactor.Redact(string.Join(" ", parts));
    }

    private static ActionDecision Ok(ValidatedAction a) => new(a, null);
    private static ActionDecision Reject(string why) => new(null, why);
    private static string Trunc(string? s) => s is null ? "" : s.Length <= 60 ? s : s[..60] + "…";
}
