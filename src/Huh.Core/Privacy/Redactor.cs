using System.Text.RegularExpressions;

namespace Huh.Core.Privacy;

/// <summary>
/// Removes personal data before anything leaves the machine: the current user's profile path (-> %USERPROFILE%, so huh can
/// expand it back locally for "open folder"), other users' profile folders, the user name, the computer name and e-mail
/// addresses. Applied to every string sent to an AI provider.
/// </summary>
public sealed partial class Redactor
{
    public const string ProfileToken = "%USERPROFILE%";
    public const string UserToken = "<user>";
    public const string ComputerToken = "<computer>";
    public const string EmailToken = "<email>";

    private readonly string _profile;
    private readonly Regex? _userWord;
    private readonly Regex? _computerWord;

    public Redactor(string? userProfile = null, string? userName = null, string? machineName = null)
    {
        _profile = (userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd('\\');
        userName ??= Environment.UserName;
        machineName ??= Environment.MachineName;
        // Whole-word only, and only for names long enough not to clobber ordinary words.
        if (userName.Length >= 3) _userWord = new Regex($@"(?<![\w.-]){Regex.Escape(userName)}(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (machineName.Length >= 3) _computerWord = new Regex($@"(?<![\w.-]){Regex.Escape(machineName)}(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    // Any other profile folder: C:\Users\<name>\...  (also with forward slashes)
    [GeneratedRegex(@"(?i)\b([A-Z]:[\\/]Users[\\/])(?!Public\b|Default\b|All Users\b)[^\\/:*?""<>|\r\n]+")]
    private static partial Regex OtherProfile();

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;
        if (_profile.Length > 3)
        {
            s = s.Replace(_profile, ProfileToken, StringComparison.OrdinalIgnoreCase);
            s = s.Replace(_profile.Replace('\\', '/'), ProfileToken, StringComparison.OrdinalIgnoreCase);
        }
        s = OtherProfile().Replace(s, m => m.Groups[1].Value + UserToken);
        s = Email().Replace(s, EmailToken);
        if (_userWord is not null) s = _userWord.Replace(s, UserToken);
        if (_computerWord is not null) s = _computerWord.Replace(s, ComputerToken);
        return s;
    }

    /// <summary>Expands %USERPROFILE% (only that) in a path that came back from the AI.</summary>
    public string ExpandProfile(string path) => path.Replace(ProfileToken, _profile, StringComparison.OrdinalIgnoreCase);
}
