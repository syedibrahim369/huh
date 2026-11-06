using Huh.Core.Dialogs;
using Huh.Core.Facts;
using Huh.Core.Provenance;

namespace Huh.Tests;

// These use real files on the machine (Windows binaries, the .NET host, the test assembly itself).
public class ProvenanceTests
{
    private static readonly string Sys32 = Environment.SystemDirectory;

    [Fact]
    public void Inbox_windows_dll_is_signed_by_microsoft_windows_via_catalog_or_embedded()
    {
        var s = Authenticode.Verify(Path.Combine(Sys32, "kernel32.dll"));
        Assert.Equal(SignatureState.Signed, s.State);
        Assert.Equal("Microsoft Windows", s.Signer);
    }

    [Fact]
    public void Catalog_signed_system_file_is_recognised()
    {
        // System32 notepad.exe has no certificate table (checked: PE security directory size 0); it is catalog-signed only.
        // (csrss.exe and kernel32.dll carry embedded signatures on this build, even though Get-AuthenticodeSignature reports "Catalog".)
        var s = Authenticode.Verify(Path.Combine(Sys32, "notepad.exe"));
        Assert.Equal(SignatureState.Signed, s.State);
        Assert.True(s.ViaCatalog);
        Assert.Equal("Microsoft Windows", s.Signer);
    }

    [Fact]
    public void Dotnet_host_has_an_embedded_signature()
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var s = Authenticode.Verify(dotnet);
        Assert.Equal(SignatureState.Signed, s.State);
        Assert.False(s.ViaCatalog);
        Assert.Equal(".NET", s.Signer); // the .NET host is signed with a ".NET" certificate (matches Get-AuthenticodeSignature)
    }

    [Fact]
    public void Our_own_unsigned_build_output_is_unsigned()
    {
        var s = Authenticode.Verify(typeof(ProvenanceTests).Assembly.Location);
        Assert.Equal(SignatureState.Unsigned, s.State);
        Assert.Null(s.Signer);
    }

    [Fact]
    public void Tampered_signed_file_is_invalid()
    {
        var copy = Path.Combine(Path.GetTempPath(), $"huh-tamper-{Guid.NewGuid():N}.exe");
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        File.Copy(dotnet, copy);
        try
        {
            var bytes = File.ReadAllBytes(copy);
            bytes[0x400] ^= 0xFF; // flip a byte inside the signed image (past the headers)
            File.WriteAllBytes(copy, bytes);
            Assert.Equal(SignatureState.Invalid, Authenticode.Verify(copy).State);
        }
        finally { File.Delete(copy); }
    }

    [Fact]
    public void Missing_path_is_unknown() => Assert.Equal(SignatureState.Unknown, Authenticode.Verify(null).State);

    // ---------------- source analysis ----------------

    private static DialogInfo Dialog(string? name, string? path, string title = "", string message = "", DialogKind kind = DialogKind.Win32Dialog) =>
        new() { Hwnd = 1, Process = new ProcessDetails(42, name, path, ElevationState.NotElevated), Title = title, Message = message, Kind = kind };

    [Fact]
    public void Csrss_hard_error_is_raised_by_windows_on_behalf_of_the_program()
    {
        var src = SourceAnalyzer.Analyze(Dialog("csrss", null, "game.exe - System Error",
            "The code execution cannot proceed because VCRUNTIME140.dll was not found."));
        Assert.Equal(SourceKind.Windows, src.Kind);
        Assert.Equal("game.exe", src.OnBehalfOf);
        Assert.Equal(SignatureState.Signed, src.Signature.State); // resolved %SystemRoot%\System32\csrss.exe ourselves
    }

    [Fact]
    public void Browser_alert_is_a_web_page_with_its_origin()
    {
        var src = SourceAnalyzer.Analyze(Dialog("msedge", null, "", "support-alert.example says\nYour PC is infected. Error 0x80070005.", DialogKind.OtherWindow));
        Assert.Equal(SourceKind.WebPage, src.Kind);
        Assert.Equal("Microsoft Edge", src.Browser);
        Assert.Equal("support-alert.example", src.WebOrigin);
    }

    [Fact]
    public void Browser_alert_from_data_url_has_no_origin()
    {
        var src = SourceAnalyzer.Analyze(Dialog("chrome", null, "", "This page says\nHello", DialogKind.OtherWindow));
        Assert.Equal(SourceKind.WebPage, src.Kind);
        Assert.Null(src.WebOrigin);
    }

    [Fact]
    public void Signed_app_uses_the_verified_signer_as_publisher()
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var src = SourceAnalyzer.Analyze(Dialog("dotnet", dotnet));
        Assert.Equal(SourceKind.App, src.Kind);
        Assert.True(src.PublisherVerified);
        Assert.Equal(".NET", src.Publisher);
    }

    [Fact]
    public void Unsigned_app_publisher_is_marked_unverified()
    {
        var src = SourceAnalyzer.Analyze(Dialog("Huh.Tests", typeof(ProvenanceTests).Assembly.Location));
        Assert.False(src.PublisherVerified);
        Assert.Equal(SignatureState.Unsigned, src.Signature.State);
    }

    // ---------------- headline ----------------

    [Theory]
    [InlineData("Could not save the file.\n\nAccess is denied. (0x80070005)", "Could not save the file")]
    [InlineData("This installation package could not be opened. Verify that the package exists.", "This installation package could not be opened")]
    [InlineData("Windows cannot find 'nope'. Make sure you typed the name correctly, and then try again.", "Windows cannot find 'nope'")]
    [InlineData("0x8007045D", "0x8007045D")]
    [InlineData("C:\\huh-demo.txt\nYou don\u2019t have permission to save in this location.\nContact the administrator.", "You don\u2019t have permission to save in this location")]
    [InlineData("report.docx\nThe file is open in another program.", "The file is open in another program")]
    public void Headline_is_the_first_sentence(string message, string expected) =>
        Assert.Equal(expected, FactsBuilder.Headline(Dialog("x", null, "Title", message)));

    [Fact]
    public void Headline_falls_back_to_title() => Assert.Equal("Backup", FactsBuilder.Headline(Dialog("x", null, "Backup", "")));
}

public class AlreadySaysTests
{
    [Theory]
    [InlineData("Could not save. Access is denied. (0x80070005)", "Access is denied.", true)]
    [InlineData("The instruction at 0x00007FF6A1B2C3D4 referenced memory at 0x0000000000000000. The memory could not be read.",
                "The instruction at 0x… referenced memory at 0x…. The memory could not be ….", true)]
    [InlineData("Backup failed.", "Access is denied.", false)]
    [InlineData("The code execution cannot proceed because VCRUNTIME140.dll was not found.",
                "The code execution cannot proceed because … was not found. Reinstalling the program may fix this problem.", false)]
    public void Placeholder_aware_containment(string dialog, string message, bool expected) =>
        Assert.Equal(expected, Huh.Core.Facts.FactsBuilder.AlreadySays(dialog, message));
}
