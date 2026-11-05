using System.Diagnostics.CodeAnalysis;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Huh.Core.Interop;

/// <summary>Small managed helpers over CsWin32-generated APIs.</summary>
internal static unsafe class Native
{
    /// <summary>Copies a BSTR returned by a COM call into a managed string and frees it.</summary>
    public static string Take(BSTR b)
    {
        if (b.Value is null) return "";
        try
        {
            int len = (int)(*((uint*)b.Value - 1) / sizeof(char));
            return new string(b.Value, 0, len);
        }
        finally { PInvoke.SysFreeString(b); }
    }

    public static string ClassName(HWND hwnd)
    {
        Span<char> buf = stackalloc char[256];
        int n = PInvoke.GetClassName(hwnd, buf);
        return n > 0 ? new string(buf[..n]) : "";
    }

    /// <summary>Window caption. GetWindowText on another process's top-level window reads the cached title (no message is sent),
    /// so it cannot hang on a frozen app.</summary>
    public static string WindowText(HWND hwnd)
    {
        int len = PInvoke.GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var buf = new char[len + 1];
        fixed (char* p = buf)
        {
            int n = PInvoke.GetWindowText(hwnd, p, buf.Length);
            return new string(p, 0, n);
        }
    }

    public static int ProcessId(HWND hwnd)
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        return (int)pid;
    }

    public static bool IsTopLevel(HWND hwnd) => PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) == hwnd;

    public static HWND Owner(HWND hwnd) => PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER);

    [SuppressMessage("Interoperability", "CA1416")]
    public static WINDOW_EX_STYLE ExStyle(HWND hwnd) => (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

    /// <summary>
    /// For classic MessageBoxes: the icon lives in a Static control with id 20. MessageBox loads a DPI-scaled copy, so the HICON
    /// differs from LoadIcon(NULL, IDI_*), but GetIconInfoEx still reports the resource it came from (user32 #32513..32516).
    /// That identifies the icon type from metadata alone: no pixels are read.
    /// </summary>
    public static (Huh.Core.Dialogs.DialogSeverity Severity, string Diag) MessageBoxIcon(HWND dialog)
    {
        const int MessageBoxIconId = 20;
        const uint STM_GETICON = 0x0171;
        var iconCtl = PInvoke.GetDlgItem(dialog, MessageBoxIconId);
        if (iconCtl.IsNull) return (Huh.Core.Dialogs.DialogSeverity.None, "no-icon-control");
        nuint result;
        if (PInvoke.SendMessageTimeout(iconCtl, STM_GETICON, default, default,
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK, 200, &result) == 0 || result == 0)
            return (Huh.Core.Dialogs.DialogSeverity.None, "no-icon");

        var info = new ICONINFOEXW { cbSize = (uint)sizeof(ICONINFOEXW) };
        if (!PInvoke.GetIconInfoEx((HICON)(nint)result, &info)) return (Huh.Core.Dialogs.DialogSeverity.None, "iconinfo-failed");
        if (!info.hbmMask.IsNull) PInvoke.DeleteObject(info.hbmMask);
        if (!info.hbmColor.IsNull) PInvoke.DeleteObject(info.hbmColor);

        // Two observed sources (Windows 11 26300):
        //  - user32 shared icons #32513..32516 (e.g. csrss hard-error boxes), and
        //  - imageres.dll icons #98/#84/#81/#99 (MessageBox in apps). imageres is mapped as a datafile, so szModName is only a
        //    handle string; we accept those IDs when the module is not a real path or is imageres.dll.
        string mod = info.szModName.ToString();
        bool user32 = mod.EndsWith("user32.dll", StringComparison.OrdinalIgnoreCase) || mod.Equals("USER32", StringComparison.OrdinalIgnoreCase);
        bool imageres = mod.EndsWith("imageres.dll", StringComparison.OrdinalIgnoreCase) || !mod.Contains('\\');
        var sev = (info.wResID, user32, imageres) switch
        {
            (32513, true, _) or (98, _, true) => Huh.Core.Dialogs.DialogSeverity.Error,
            (32515, true, _) or (84, _, true) => Huh.Core.Dialogs.DialogSeverity.Warning,
            (32516, true, _) or (81, _, true) => Huh.Core.Dialogs.DialogSeverity.Information,
            (32514, true, _) or (99, _, true) => Huh.Core.Dialogs.DialogSeverity.Question,
            _ => Huh.Core.Dialogs.DialogSeverity.None,
        };
        return (sev, $"#{info.wResID}");
    }

    public static WINDOW_STYLE Style(HWND hwnd) => (WINDOW_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
}
