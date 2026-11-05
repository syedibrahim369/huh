using System.Text.RegularExpressions;
using Huh.Core.Decoding;
using Huh.Core.Dialogs;
using Huh.Core.Provenance;

namespace Huh.Core.Facts;

/// <summary>Everything huh can say about a dialog without AI: decoded codes and where the dialog came from.</summary>
/// <param name="Headline">Short, plain statement of what went wrong (the dialog's first sentence, or its title).</param>
public sealed record DialogFacts(DialogInfo Dialog, IReadOnlyList<DecodedCode> Codes, SourceInfo Source, string Headline)
{
    /// <summary>The decoded message for the main code, unless the dialog already says exactly that.</summary>
    public string? PrimaryCodeExplanation =>
        Codes.FirstOrDefault(c => c.Message is not null) is { } c && !FactsBuilder.AlreadySays(Dialog.Message, c.Message!) ? c.Message : null;
}

public static partial class FactsBuilder
{
    private const int HeadlineMax = 110;

    public static DialogFacts Build(DialogInfo d)
    {
        var codes = ErrorCodeDecoder.Decode(d.Title + "\n" + d.Message);
        var source = SourceAnalyzer.Analyze(d);
        return new DialogFacts(d, codes, source, Headline(d));
    }

    /// <summary>
    /// Does the dialog already contain Windows' message for a code? Message-table text has placeholders where the dialog has
    /// real values ("The instruction at 0x… referenced memory…" vs "...at 0x00007FF6A1B2C3D4..."), so "…" is a wildcard.
    /// </summary>
    public static bool AlreadySays(string dialogText, string codeMessage)
    {
        if (dialogText.Contains(codeMessage, StringComparison.OrdinalIgnoreCase)) return true;
        var parts = codeMessage.Split('…');
        if (parts.Length < 2) return false;
        // Keep the literal whitespace around each placeholder ("0x… referenced" must still need the space after the value).
        var pattern = string.Join(@"\S*?", parts.Select(p => Regex.Escape(p).Replace(@"\ ", @"\s+")));
        try { return Regex.IsMatch(Regex.Replace(dialogText, @"\s+", " "), pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    [GeneratedRegex(@"^(.+?[.!?])(\s|$)", RegexOptions.Singleline)]
    private static partial Regex FirstSentence();

    internal static string Headline(DialogInfo d)
    {
        var msg = d.Message.Trim();
        if (msg.Length > 0)
        {
            var firstLine = msg.Split('\n', 2)[0].Trim();
            var s = FirstSentence().Match(firstLine) is { Success: true } m ? m.Groups[1].Value : firstLine;
            s = s.TrimEnd('.').Trim();
            if (s.Length > 0) return s.Length <= HeadlineMax ? s : s[..(HeadlineMax - 1)].TrimEnd() + "…";
        }
        return d.Title.Length > 0 ? d.Title : "Something went wrong";
    }
}
