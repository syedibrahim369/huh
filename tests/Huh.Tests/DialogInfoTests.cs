using Huh.Core.Dialogs;

namespace Huh.Tests;

public class DialogInfoTests
{
    [Fact]
    public void Defaults_are_empty_not_null()
    {
        var d = new DialogInfo { Hwnd = 1, ProcessId = 2 };
        Assert.Equal("", d.Title);
        Assert.Equal("", d.Message);
        Assert.Empty(d.Buttons);
        Assert.False(d.BlockedByUipi);
    }
}
