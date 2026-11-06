using System.Text.Json;
using System.Text.Json.Nodes;
using Huh.Core.Decoding;
using Huh.Core.Dialogs;
using Huh.Core.Facts;
using Huh.Core.Privacy;
using Huh.Core.Provenance;

namespace Huh.Core.AI;

/// <summary>
/// Exactly what one AI request would contain, for the Settings privacy section. Built with the same
/// <see cref="Prompt.Build"/> and <see cref="GeminiExplainer.BuildBody"/> calls as a real request, so the preview cannot drift
/// from what is sent. The API key is never included: its header is shown as a placeholder.
/// </summary>
public sealed record RequestPreview(string Method, string Url, IReadOnlyList<(string Name, string Value)> Headers, string Body)
{
    public const string KeyPlaceholder = "(your API key: sent only in this header, never shown or logged)";

    public static RequestPreview For(DialogFacts facts, Redactor redactor, string model)
    {
        var req = Prompt.Build(facts, redactor);
        var body = GeminiExplainer.BuildBody(req, model);
        var pretty = JsonNode.Parse(body)!.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // show "<" and "'" as-is (display only)
        });
        return new RequestPreview("POST", GeminiExplainer.UrlFor(model),
            [("Content-Type", "application/json; charset=utf-8"), ("Accept", "application/json"), ("x-goog-api-key", KeyPlaceholder)],
            pretty);
    }

    /// <summary>The user content part only (the dialog text and huh's facts), which is the part that changes per error.</summary>
    public static string UserContent(DialogFacts facts, Redactor redactor) => Prompt.Build(facts, redactor).UserContent;
}

/// <summary>A realistic made-up dialog, used for the privacy preview and to pre-warm the bubble UI at startup.</summary>
public static class SampleFacts
{
    public static DialogFacts Create(string? userProfile = null)
    {
        var profile = (userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd('\\');
        var message = $"Couldn't save {profile}\\Documents\\Budget 2026.xlsx.\nAccess is denied. (0x80070005)";
        var d = new DialogInfo
        {
            Hwnd = 0,
            Title = "Contoso Sheets",
            Message = message,
            Buttons = ["OK"],
            Severity = DialogSeverity.Error,
            Kind = DialogKind.Win32Dialog,
            ClassName = "#32770",
            Process = new ProcessDetails(0, "contososheets", @"C:\Program Files\Contoso\Sheets\contososheets.exe", ElevationState.NotElevated),
        };
        var src = new SourceInfo(SourceKind.App, "Contoso Sheets", "Contoso Ltd", true,
            new SignatureInfo(SignatureState.Signed, "Contoso Ltd"), "contososheets.exe", d.Process.Path);
        return new DialogFacts(d, ErrorCodeDecoder.Decode(message), src, FactsBuilder.Headline(d));
    }
}
