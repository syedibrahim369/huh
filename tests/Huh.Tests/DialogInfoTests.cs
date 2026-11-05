using Huh.Core.Dialogs;

namespace Huh.Tests;

public class DialogInfoTests
{
    private static DialogInfo Make(DialogSeverity s) =>
        new() { Hwnd = 1, Process = new ProcessDetails(2, "app", null, ElevationState.NotElevated), Severity = s };

    [Fact]
    public void Defaults_are_empty_not_null()
    {
        var d = Make(DialogSeverity.None);
        Assert.Equal("", d.Title);
        Assert.Equal("", d.Message);
        Assert.Empty(d.Buttons);
        Assert.False(d.BlockedByUipi);
    }

    [Theory]
    [InlineData(DialogSeverity.Error, true)]
    [InlineData(DialogSeverity.Warning, true)]
    [InlineData(DialogSeverity.Question, false)]
    [InlineData(DialogSeverity.Information, false)]
    [InlineData(DialogSeverity.None, false)]
    public void IsError_covers_errors_and_warnings(DialogSeverity s, bool expected) => Assert.Equal(expected, Make(s).IsError);
}
