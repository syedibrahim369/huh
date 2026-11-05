using System.Globalization;
using System.Text.RegularExpressions;

namespace Huh.Core.Decoding;

public enum CodeKind
{
    /// <summary>COM/Windows HRESULT, e.g. 0x80070005.</summary>
    HResult,
    /// <summary>Win32 error code, e.g. 5 or 1619.</summary>
    Win32,
    /// <summary>Kernel/ntdll NTSTATUS, e.g. 0xC0000135.</summary>
    NtStatus,
}

/// <param name="Value">The raw 32-bit code.</param>
/// <param name="Text">How it appeared in the dialog (e.g. "0x80070005", "1619").</param>
/// <param name="Name">Symbolic name when known (E_ACCESSDENIED, ERROR_INSTALL_PACKAGE_OPEN_FAILED...).</param>
/// <param name="Message">Windows' own description (FormatMessage), cleaned up; null if Windows has none.</param>
/// <param name="Win32Code">For HRESULTs wrapping a Win32 error (facility 7): the inner Win32 code.</param>
public sealed record DecodedCode(uint Value, CodeKind Kind, string Text, string? Name, string? Message, int? Win32Code)
{
    /// <summary>"0x80070005", or "1619" for Win32 codes written in decimal.</summary>
    public string Display => Kind == CodeKind.Win32 && Value < 0x10000 ? Value.ToString(CultureInfo.InvariantCulture) : $"0x{Value:X8}";

    public string KindLabel => Kind switch
    {
        CodeKind.HResult => Win32Code is { } w ? $"HRESULT (Windows error {w})" : "HRESULT",
        CodeKind.NtStatus => "NTSTATUS",
        _ => "Windows error",
    };
}

/// <summary>
/// Finds error codes in dialog text and decodes them with Windows' own message tables. Deterministic, offline, no AI.
/// </summary>
public static partial class ErrorCodeDecoder
{
    private const int MaxCodes = 4;

    // 0x80070005, 0xc000007b (8 hex digits after 0x)
    [GeneratedRegex(@"(?<![0-9A-Za-z])0x(?<hex>[0-9A-Fa-f]{8})(?![0-9A-Za-z])")]
    private static partial Regex Hex8();

    // Bare 8-digit hex that looks like an HRESULT/NTSTATUS, e.g. "Code: 800A03EA", "error 80070057"
    [GeneratedRegex(@"\b(?<hex>[89ACDacd][0-9A-Fa-f]{7})\b")]
    private static partial Regex BareHex8();

    // "error 1619", "Error code: 1603", "Fehler 5" is NOT handled (English cue words only), "(error 2)", "err=32"
    [GeneratedRegex(@"\b(?:error|err|errno|status|win32 error)\s*(?:code)?\s*[:#=]?\s*(?<dec>\d{1,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex DecimalWithCue();

    // .NET prints HRESULTs as negative decimals: "HRESULT: -2147024891"
    [GeneratedRegex(@"(?<![\d.])-(?<neg>21474\d{5}|2[01]\d{8}|1\d{9})\b")]
    private static partial Regex NegativeDecimal();

    public static IReadOnlyList<DecodedCode> Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<(int Pos, uint Value, string Text, bool Decimal)>();

        foreach (Match m in Hex8().Matches(text))
            found.Add((m.Index, uint.Parse(m.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), m.Value, false));
        foreach (Match m in BareHex8().Matches(text))
        {
            if (found.Any(f => m.Index >= f.Pos && m.Index < f.Pos + f.Text.Length)) continue; // already part of 0x...
            if (m.Value.All(char.IsDigit)) continue; // all-digit "80070005" is ambiguous with plain numbers; require a letter
            found.Add((m.Index, uint.Parse(m.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), m.Value, false));
        }
        foreach (Match m in NegativeDecimal().Matches(text))
            if (long.TryParse(m.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var neg) && neg is >= int.MinValue and < 0)
                found.Add((m.Index, unchecked((uint)(int)neg), m.Value, false));
        foreach (Match m in DecimalWithCue().Matches(text))
            if (uint.TryParse(m.Groups["dec"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var dec) && dec is > 0 and < 16000)
                found.Add((m.Groups["dec"].Index, dec, m.Groups["dec"].Value, true));

        var result = new List<DecodedCode>();
        foreach (var f in found.OrderBy(f => f.Pos))
        {
            if (result.Any(r => r.Value == f.Value)) continue;
            var decoded = DecodeValue(f.Value, f.Text, f.Decimal);
            // A decimal number after "error" that Windows doesn't know is probably an app-specific number: drop it.
            if (f.Decimal && decoded.Message is null && decoded.Name is null) continue;
            result.Add(decoded);
            if (result.Count == MaxCodes) break;
        }
        return result;
    }

    /// <summary>Classify and decode a single 32-bit value.</summary>
    public static DecodedCode DecodeValue(uint value, string? text = null, bool writtenInDecimal = false)
    {
        text ??= $"0x{value:X8}";

        // Small values are Win32 codes (whether written in decimal or as 0x000004C7).
        if (value < 0x10000)
            return new DecodedCode(value, CodeKind.Win32, text, KnownCodes.Win32Name((int)value), MessageTable.System(value), null);

        // NTSTATUS: severity bits 11 (error) or 01/10 with the customer bit clear and N-bit clear; 0xC... is the common case.
        // HRESULT_FROM_NT sets the N bit (0x10000000), so 0xD... is an HRESULT wrapping an NTSTATUS.
        uint sev = value >> 30;
        bool nBit = (value & 0x10000000) != 0;
        if (sev == 3 && !nBit)
        {
            var msg = MessageTable.NtStatus(value) ?? MessageTable.System(value);
            return new DecodedCode(value, CodeKind.NtStatus, text, KnownCodes.NtStatusName(value), msg, null);
        }

        // HRESULT
        int? win32 = null;
        uint facility = (value >> 16) & 0x1FFF;
        if (facility == 7 /* FACILITY_WIN32 */) win32 = (int)(value & 0xFFFF);
        string? message = MessageTable.System(value)
                          ?? (win32 is { } w ? MessageTable.System((uint)w) : null)
                          ?? (nBit ? MessageTable.NtStatus(value & ~0x10000000u) : null)
                          ?? MessageTable.FromKnownModules(value);
        string? name = KnownCodes.HResultName(value) ?? (win32 is { } w2 ? KnownCodes.Win32Name(w2) : null);
        return new DecodedCode(value, CodeKind.HResult, text, name, message, win32);
    }
}
