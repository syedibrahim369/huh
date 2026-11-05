using Huh.Core.Decoding;

namespace Huh.Tests;

// Messages come from the OS message tables; assertions use key phrases from en-US Windows.
public class ErrorCodeDecoderTests
{
    [Fact]
    public void E_ACCESSDENIED_is_an_HRESULT_wrapping_win32_5()
    {
        var c = ErrorCodeDecoder.DecodeValue(0x80070005);
        Assert.Equal(CodeKind.HResult, c.Kind);
        Assert.Equal(5, c.Win32Code);
        Assert.Equal("E_ACCESSDENIED", c.Name);
        Assert.Equal("Access is denied.", c.Message);
        Assert.Equal("0x80070005", c.Display);
    }

    [Fact]
    public void STATUS_DLL_NOT_FOUND_is_decoded_from_ntdll()
    {
        var c = ErrorCodeDecoder.DecodeValue(0xC0000135);
        Assert.Equal(CodeKind.NtStatus, c.Kind);
        Assert.Equal("STATUS_DLL_NOT_FOUND", c.Name);
        Assert.NotNull(c.Message);
        Assert.Contains("was not found", c.Message);
        Assert.DoesNotContain("%hs", c.Message); // printf inserts are replaced
        Assert.DoesNotContain("{", c.Message);   // "{Unable To Locate Component}" header stripped
    }

    [Fact]
    public void Win32_1619_is_the_msi_package_open_failure()
    {
        var c = ErrorCodeDecoder.DecodeValue(1619);
        Assert.Equal(CodeKind.Win32, c.Kind);
        Assert.Equal("ERROR_INSTALL_PACKAGE_OPEN_FAILED", c.Name);
        Assert.StartsWith("This installation package could not be opened", c.Message);
        Assert.Equal("1619", c.Display);
    }

    [Fact]
    public void Access_violation_ntstatus()
    {
        var c = ErrorCodeDecoder.DecodeValue(0xC0000005);
        Assert.Equal(CodeKind.NtStatus, c.Kind);
        Assert.Equal("STATUS_ACCESS_VIOLATION", c.Name);
        Assert.Contains("memory", c.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bad_image_0xc000007b()
    {
        var c = ErrorCodeDecoder.DecodeValue(0xC000007B);
        Assert.Equal(CodeKind.NtStatus, c.Kind);
        Assert.Equal("STATUS_INVALID_IMAGE_FORMAT", c.Name);
        Assert.NotNull(c.Message);
    }

    [Fact]
    public void Module_not_found_hresult_from_regsvr32()
    {
        var c = ErrorCodeDecoder.DecodeValue(0x8007007E);
        Assert.Equal(126, c.Win32Code);
        Assert.Equal("The specified module could not be found.", c.Message);
    }

    [Fact]
    public void Wininet_name_not_resolved_uses_the_wininet_message_table()
    {
        var c = ErrorCodeDecoder.DecodeValue(0x80072EE7);
        Assert.Equal(12007, c.Win32Code);
        Assert.NotNull(c.Message);
        Assert.Contains("name", c.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_hresult_still_classified_without_message()
    {
        var c = ErrorCodeDecoder.DecodeValue(0x8BADF00D);
        Assert.Equal(CodeKind.HResult, c.Kind);
        Assert.Null(c.Name);
    }

    // ---------------- extraction from dialog text ----------------

    [Fact]
    public void Extracts_hresult_from_message() =>
        Assert.Equal([0x80070005u], ErrorCodeDecoder.Decode("Could not save the file.\n\nAccess is denied. (0x80070005)").Select(c => c.Value));

    [Fact]
    public void Extracts_lowercase_ntstatus() =>
        Assert.Equal([0xC000007Bu], ErrorCodeDecoder.Decode("The application was unable to start correctly (0xc000007b). Click OK to close.").Select(c => c.Value));

    [Fact]
    public void Extracts_decimal_win32_after_cue_word()
    {
        var codes = ErrorCodeDecoder.Decode("Installation failed with error 1619.");
        Assert.Single(codes);
        Assert.Equal(CodeKind.Win32, codes[0].Kind);
        Assert.Equal(1619u, codes[0].Value);
    }

    [Fact]
    public void Extracts_bare_wsh_style_code() =>
        Assert.Equal([0x800A03EAu], ErrorCodeDecoder.Decode("Line: 3\nChar: 1\nError: Syntax error\nCode: 800A03EA").Select(c => c.Value));

    [Fact]
    public void Extracts_dotnet_negative_hresult() =>
        Assert.Equal([0x80070005u], ErrorCodeDecoder.Decode("System.UnauthorizedAccessException (HRESULT: -2147024891)").Select(c => c.Value));

    [Fact]
    public void Several_codes_in_order_without_duplicates()
    {
        var codes = ErrorCodeDecoder.Decode("Exception code: 0xC0000005 at 0x00007FF6A1B2C3D4. Also 0xC0000005 again, then 0x80004005.");
        Assert.Equal([0xC0000005u, 0x80004005u], codes.Select(c => c.Value));
    }

    [Fact]
    public void Addresses_are_not_codes()
    {
        // 16-digit pointers must not be split into 8-digit "codes".
        var codes = ErrorCodeDecoder.Decode("The instruction at 0x00007FF6A1B2C3D4 referenced memory at 0x0000000000000000.");
        Assert.Empty(codes);
    }

    [Theory]
    [InlineData("Code 43.")]                 // Device Manager code, not a Win32 error
    [InlineData("Version 2.1 is ready")]     // plain numbers
    [InlineData("Saved 3 items")]
    [InlineData("error 99999")]              // out of range
    public void Ignores_non_codes(string text) => Assert.Empty(ErrorCodeDecoder.Decode(text));

    [Fact]
    public void Decimal_after_cue_that_windows_does_not_know_is_dropped() =>
        Assert.Empty(ErrorCodeDecoder.Decode("Error 15999 occurred in module Foo")); // no system message, no name

    [Theory]
    [InlineData("{Application Error}\r\nThe application was unable to start correctly (0x%lx).", "The application was unable to start correctly (0x…).")]
    [InlineData("The code execution cannot proceed because %hs was not found.", "The code execution cannot proceed because … was not found.")]
    [InlineData("Access is denied.\r\n", "Access is denied.")]
    public void Message_cleanup(string raw, string expected) => Assert.Equal(expected, MessageTable.Clean(raw));
}
