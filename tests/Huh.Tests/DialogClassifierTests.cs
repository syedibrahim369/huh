using Huh.Core.Dialogs;

namespace Huh.Tests;

public class DialogClassifierTests
{
    private static UiaNode Text(string s, string id = "65535") => new("Text", s, "Static", id);
    private static UiaNode Button(string s, string id = "1") => new("Button", s, "Button", id);
    private static readonly UiaNode CaptionClose = new("Button", "Close", "", "Close");
    private static readonly UiaNode TitleBar = new("TitleBar", "", "", "TitleBar");

    private static Classification Classify(string title, DialogSeverity icon = DialogSeverity.None, string cls = "#32770", params UiaNode[] nodes) =>
        DialogClassifier.Classify(new DialogEvidence(cls, title, nodes, true, icon));

    [Theory]
    [InlineData("Access is denied. (0x80070005)")]
    [InlineData("The system cannot find the file specified.")]
    [InlineData("Couldn't connect to the server")]
    [InlineData("0x8007045D")]
    [InlineData("Exception code: 0xC0000005")]
    [InlineData("The code execution cannot proceed because VCRUNTIME140.dll was not found.")]
    [InlineData("Error 1603")]
    [InlineData("Code 43.")]
    public void Error_text_is_an_error(string message)
    {
        var c = Classify("App", nodes: [Text(message), Button("OK")]);
        Assert.Equal(DialogSeverity.Error, c.Severity);
    }

    [Theory]
    [InlineData("Do you want to save changes to Untitled?")]
    [InlineData("Are you sure you want to permanently delete these 3 items?")]
    public void Confirmation_with_warning_icon_is_a_question_not_an_error(string message)
    {
        var c = Classify("Delete", DialogSeverity.Warning, nodes: [Text(message), Button("Yes"), Button("No")]);
        Assert.Equal(DialogSeverity.Question, c.Severity);
    }

    [Fact]
    public void Error_words_beat_question_phrasing()
    {
        var c = Classify("Sync", nodes: [Text("The upload failed. Do you want to try again?")]);
        Assert.Equal(DialogSeverity.Error, c.Severity);
    }

    [Fact]
    public void Error_icon_alone_is_enough_for_untranslated_text()
    {
        var c = Classify("Fehler", DialogSeverity.Error, nodes: [Text("Zugriff verweigert.")]);
        Assert.Equal(DialogSeverity.Error, c.Severity);
        Assert.Contains("icon:error", c.Signals);
    }

    [Fact]
    public void Success_message_is_information()
    {
        var c = Classify("Export", DialogSeverity.Information, nodes: [Text("Export completed successfully.")]);
        Assert.Equal(DialogSeverity.Information, c.Severity);
    }

    [Fact]
    public void Plain_text_without_cues_is_not_an_error()
    {
        var c = Classify("About", nodes: [Text("huh test harness\nVersion 1.0")]);
        Assert.False(c.Severity is DialogSeverity.Error or DialogSeverity.Warning);
    }

    [Fact]
    public void Extracts_message_and_buttons_and_skips_caption_buttons()
    {
        var c = Classify("Contoso", DialogSeverity.Error, nodes:
        [
            Button("OK", "2"), new UiaNode("Image", "", "Static", "20"), Text("Line one"), TitleBar, CaptionClose,
            new UiaNode("Button", "Minimize", "", "Minimize-Restore"),
        ]);
        Assert.Equal("Line one", c.Message);
        Assert.Equal(["OK"], c.Buttons);
    }

    [Fact]
    public void TaskDialog_is_recognised_by_its_automation_ids()
    {
        var c = Classify("Network", nodes:
        [
            new UiaNode("Text", "Couldn't connect", "Element", "MainInstruction"),
            new UiaNode("Button", "OK", "CCPushButton", "CommandButton_1"),
        ]);
        Assert.Equal(DialogKind.TaskDialog, c.Kind);
    }

    [Fact]
    public void Title_is_not_duplicated_into_message()
    {
        var c = Classify("Error", nodes: [Text("Error"), Text("Disk full")]);
        Assert.Equal("Disk full", c.Message);
    }

    [Theory]
    [InlineData("Error", true)]
    [InlineData("Export failed", true)]
    [InlineData("Notes", false)]
    [InlineData("", false)]
    public void Title_prefilter(string title, bool expected) => Assert.Equal(expected, DialogClassifier.TitleLooksLikeError(title));

    [Fact]
    public void CleanText_strips_bidi_marks() => Assert.Equal("11/6/2026", UiaReader.CleanText("‎11/‎6/‎2026 "));
}
