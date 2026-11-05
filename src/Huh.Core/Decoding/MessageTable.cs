using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Win32.System.LibraryLoader;

namespace Huh.Core.Decoding;

/// <summary>FormatMessage wrappers: the system message table, ntdll (NTSTATUS) and a few module tables (WinINet).</summary>
internal static unsafe partial class MessageTable
{
    private static readonly ConcurrentDictionary<string, nint> Modules = new(StringComparer.OrdinalIgnoreCase);

    public static string? System(uint code)
    {
        var msg = Format(FORMAT_MESSAGE_OPTIONS.FORMAT_MESSAGE_FROM_SYSTEM, null, code);
        // WinINet/WinHTTP errors (12001-12175, e.g. 12007 "name not resolved") live in their own message tables.
        if (msg is null && (code & 0xFFFF) is >= 12000 and <= 12200)
            msg = FromModule("wininet.dll", code & 0xFFFF) ?? FromModule("winhttp.dll", code & 0xFFFF);
        return msg;
    }

    public static string? NtStatus(uint status) => FromModule("ntdll.dll", status);

    /// <summary>Last resort for HRESULTs whose facility has its own module table.</summary>
    public static string? FromKnownModules(uint hr) =>
        ((hr >> 16) & 0x1FFF) == 7 ? System(hr & 0xFFFF) : null;

    private static string? FromModule(string dll, uint code)
    {
        nint h = Modules.GetOrAdd(dll, static name =>
        {
            // Data-file load: we only want the message table, no code runs. ntdll is always loaded already.
            var loaded = PInvoke.GetModuleHandle(name);
            if (!loaded.IsInvalid && loaded.DangerousGetHandle() != 0) return loaded.DangerousGetHandle();
            var lib = PInvoke.LoadLibraryEx(name, LOAD_LIBRARY_FLAGS.LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_FLAGS.LOAD_LIBRARY_SEARCH_SYSTEM32);
            var handle = lib.DangerousGetHandle();
            lib.SetHandleAsInvalid(); // keep it loaded for the process lifetime (cached)
            return handle;
        });
        return h == 0 ? null : Format(FORMAT_MESSAGE_OPTIONS.FORMAT_MESSAGE_FROM_HMODULE, (void*)h, code);
    }

    private static string? Format(FORMAT_MESSAGE_OPTIONS source, void* module, uint code)
    {
        const int Size = 2048;
        char* buf = stackalloc char[Size];
        uint n = PInvoke.FormatMessage(source | FORMAT_MESSAGE_OPTIONS.FORMAT_MESSAGE_IGNORE_INSERTS, module, code, 0,
            new PWSTR(buf), Size, null);
        return n == 0 ? null : Clean(new string(buf, 0, (int)n));
    }

    // "%1", "%hs", "%ws", "%s", "%lx", "%08lx", "%p", "%0" ...
    [GeneratedRegex(@"%(\d+(![^!]*!)?|0?\d*l?[hlw]?[sxdupX])")]
    private static partial Regex Inserts();

    [GeneratedRegex(@"^\s*\{[^}\r\n]{1,80}\}\s*")]
    private static partial Regex BraceHeader();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Strip NTSTATUS "{Title}" headers, replace printf-style inserts with an ellipsis, collapse whitespace.</summary>
    internal static string? Clean(string raw)
    {
        var s = BraceHeader().Replace(raw, "");
        s = Inserts().Replace(s, "…");
        s = Whitespace().Replace(s, " ").Trim();
        return s.Length == 0 ? null : s;
    }
}
