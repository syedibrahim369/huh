using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.UI.WindowsAndMessaging.MESSAGEBOX_STYLE;

namespace Huh.TestHarness;

/// <param name="ExpectError">Ground truth: should huh treat this as an error dialog?</param>
/// <param name="ExpectedProcess">Process that owns the dialog when it is NOT the harness itself (e.g. "rundll32", "csrss").</param>
internal sealed record Scenario(string Id, bool ExpectError, string Description, Action Run, string? ExpectedProcess = null);

internal static unsafe class Scenarios
{
    public static readonly IReadOnlyList<Scenario> All =
    [
        // ---------------- MessageBox variants (error / warning) ----------------
        new("mb-error-access-denied", true, "MessageBox, error icon, Access is denied + HRESULT",
            () => Mb("Could not save the file.\n\nAccess is denied. (0x80070005)", "Contoso Writer", MB_OK | MB_ICONHAND)),
        new("mb-error-file-not-found", true, "MessageBox, error icon, file not found",
            () => Mb("The system cannot find the file specified.\nC:\\Users\\Public\\Documents\\report.docx", "Open", MB_OK | MB_ICONHAND)),
        new("mb-warning-disk-full", true, "MessageBox, warning icon, disk full",
            () => Mb("There is not enough space on the disk.", "Export failed", MB_OK | MB_ICONWARNING)),
        new("mb-error-icon-only", true, "MessageBox, error icon, no text cues at all",
            () => Mb("Code 43.", "Device", MB_OK | MB_ICONHAND)),
        new("mb-error-hresult-only", true, "MessageBox, no icon, bare HRESULT",
            () => Mb("0x8007045D", "Backup", MB_OK)),
        new("mb-error-retry-cancel", true, "MessageBox, Retry/Cancel, device not ready",
            () => Mb("The device is not ready.", "Copy", MB_RETRYCANCEL | MB_ICONHAND)),
        new("mb-error-abort-retry-ignore", true, "MessageBox, Abort/Retry/Ignore, file in use",
            () => Mb("Cannot copy settings.dat: the process cannot access the file because it is being used by another process.", "Installer", MB_ABORTRETRYIGNORE | MB_ICONWARNING)),
        new("mb-error-system-modal", true, "MessageBox, system-modal",
            () => Mb("A fatal error occurred while loading the profile.", "Sync", MB_OK | MB_ICONHAND | MB_SYSTEMMODAL)),
        new("mb-error-topmost", true, "MessageBox, topmost + setforeground",
            () => Mb("Connection failed: the remote host refused the connection.", "Remote", MB_OK | MB_ICONHAND | MB_TOPMOST | MB_SETFOREGROUND)),
        new("mb-error-long-stack", true, "MessageBox, long unhandled-exception text",
            () => Mb("Unhandled exception has occurred in your application.\n\nSystem.UnauthorizedAccessException: Access to the path 'C:\\ProgramData\\Contoso\\cache.bin' is denied.\n   at System.IO.FileSystem.CreateFile(...)\n   at Contoso.Cache.Flush()\n   at Contoso.App.OnExit()", "Contoso", MB_OK | MB_ICONHAND)),
        new("mb-error-ntstatus", true, "MessageBox, access violation NTSTATUS",
            () => Mb("The instruction at 0x00007FF6A1B2C3D4 referenced memory at 0x0000000000000000. The memory could not be read.\n\nException code: 0xC0000005", "game.exe - Application Error", MB_OK | MB_ICONHAND)),
        new("mb-error-german", true, "MessageBox, German text + error icon",
            () => Mb("Zugriff verweigert. Die Datei konnte nicht gespeichert werden.", "Fehler", MB_OK | MB_ICONHAND)),
        new("mb-error-owned", true, "MessageBox owned by an app window",
            () => WithOwnerWindow(owner => PInvoke.MessageBox(owner, "Failed to connect to the license server.", "Licensing", MB_OK | MB_ICONHAND))),

        // ---------------- TaskDialog variants ----------------
        new("td-error-basic", true, "TaskDialog, error icon",
            () => Td("Network", "Couldn't connect to the server", "The operation failed with error 0x800704CF.", TDIcon.Error)),
        new("td-warning-basic", true, "TaskDialog, warning icon",
            () => Td("Updates", "Update could not be installed", "Some update files are missing or have problems. We'll try to download the update again later.", TDIcon.Warning)),
        new("td-error-command-links", true, "TaskDialogIndirect with command links + expando + footer",
            () => TdIndirect("Photo Importer", "The import failed", "2 files could not be imported because the device was disconnected.",
                             TDIcon.Error, expanded: "Error 0x8007048F: The device is not connected.", footer: "Check the USB cable.",
                             links: ["Try again", "Skip these files"])),
        new("td-error-hyperlink", true, "TaskDialogIndirect with a hyperlink",
            () => TdIndirect("Sign in", "We couldn't sign you in", "Your password may have expired. <a href=\"https://example.invalid\">Reset it</a>.",
                             TDIcon.Error, hyperlinks: true)),
        new("td-shield-elevation", true, "TaskDialog, shield icon, needs admin",
            () => Td("Settings", "You need administrator permission", "Access denied while writing to HKLM\\Software\\Contoso.", TDIcon.Shield)),

        // ---------------- Real Windows dialogs raised by system components ----------------
        new("shell-file-in-use", true, "Shell 'File In Use' dialog (IFileOperation delete on a locked file)", ShellFileInUse),
        new("shell-access-denied", true, "Shell 'Destination Folder Access Denied' (copy into C:\\Windows)", ShellCopyToWindows),
        new("shell-path-not-found", true, "Shell 'Windows cannot find' (ShellExecuteEx on a missing exe)", ShellExecuteMissing),
        new("rundll32-missing-dll", true, "rundll32 with a DLL that does not exist",
            () => RunChild("rundll32.exe", "huh_missing_component.dll,Run"), ExpectedProcess: "rundll32"),
        new("regsvr32-missing-dll", true, "regsvr32 with a DLL that does not exist (0x8007007e)",
            () => RunChild("regsvr32.exe", "huh_missing_component.dll"), ExpectedProcess: "regsvr32"),
        new("msiexec-bad-package", true, "msiexec with a package that does not exist",
            () => RunChild("msiexec.exe", "/i \"C:\\huh-does-not-exist\\setup.msi\""), ExpectedProcess: "msiexec"),
        new("hard-error-dll-not-found", true, "System hard error STATUS_DLL_NOT_FOUND (the 'code execution cannot proceed' box)",
            () => HardError(0xC0000135, "VCRUNTIME140_huh.dll"), ExpectedProcess: "csrss"),
        new("hard-error-bad-image", true, "System hard error STATUS_INVALID_IMAGE_FORMAT (0xc000007b)",
            () => HardError(0xC000007B, "C:\\Program Files\\Contoso\\plugin.dll"), ExpectedProcess: "csrss"),
        new("custom-window-error", true, "Custom (non-#32770) top-level window titled 'Error'",
            () => CustomWindow("Error", "Failed to save settings. The configuration file is read-only.")),
        new("mb-error-from-thread", true, "MessageBox raised from a background thread",
            () => { var t = new Thread(() => Mb("Background sync failed: the token has expired.", "Sync", MB_OK | MB_ICONHAND)); t.Start(); t.Join(); }),

        // ---------------- Negatives: dialogs that are NOT errors ----------------
        new("neg-info-done", false, "MessageBox, info icon, success",
            () => Mb("Export completed successfully.", "Export", MB_OK | MB_ICONASTERISK)),
        new("neg-question-save", false, "MessageBox, question, save changes",
            () => Mb("Do you want to save changes to Untitled?", "Notepad", MB_YESNOCANCEL | MB_ICONQUESTION)),
        new("neg-confirm-delete", false, "MessageBox, warning icon, delete confirmation",
            () => Mb("Are you sure you want to permanently delete these 3 items?", "Delete", MB_YESNO | MB_ICONWARNING)),
        new("neg-td-info", false, "TaskDialog, info icon, update available",
            () => Td("Contoso", "An update is available", "Version 2.1 is ready to install.", TDIcon.Information)),
        new("neg-about", false, "MessageBox, about box",
            () => Mb("huh test harness\nVersion 1.0", "About", MB_OK)),
        new("neg-plain-window", false, "Ordinary top-level window (not a dialog)",
            () => CustomWindow("Notes", "Shopping list: milk, eggs, bread.")),
    ];

    // ---------------------------------------------------------------- helpers

    private static void Mb(string text, string caption, MESSAGEBOX_STYLE style) =>
        PInvoke.MessageBox(HWND.Null, text, caption, style);

    private enum TDIcon : int { Warning = -1, Error = -2, Information = -3, Shield = -4 }

    private static PCWSTR IconRes(TDIcon icon) => new((char*)(nuint)(ushort)(short)icon); // MAKEINTRESOURCEW(-n)

    private static void Td(string title, string instruction, string content, TDIcon icon)
    {
        int pressed;
        fixed (char* t = title, i = instruction, c = content)
        {
            PInvoke.TaskDialog(HWND.Null, HINSTANCE.Null, t, i, c,
                TASKDIALOG_COMMON_BUTTON_FLAGS.TDCBF_OK_BUTTON, IconRes(icon), &pressed).ThrowOnFailure();
        }
    }

    private static void TdIndirect(string title, string instruction, string content, TDIcon icon,
        string? expanded = null, string? footer = null, string[]? links = null, bool hyperlinks = false)
    {
        var handles = new List<GCHandle>();
        char* Pin(string? s)
        {
            if (s is null) return null;
            var h = GCHandle.Alloc(s, GCHandleType.Pinned);
            handles.Add(h);
            return (char*)h.AddrOfPinnedObject();
        }

        try
        {
            var buttons = (links ?? []).Select((l, idx) => new TASKDIALOG_BUTTON { nButtonID = 100 + idx, pszButtonText = Pin(l) }).ToArray();
            fixed (TASKDIALOG_BUTTON* pButtons = buttons)
            {
                var cfg = new TASKDIALOGCONFIG
                {
                    cbSize = (uint)sizeof(TASKDIALOGCONFIG),
                    pszWindowTitle = Pin(title),
                    pszMainInstruction = Pin(instruction),
                    pszContent = Pin(content),
                    pszExpandedInformation = Pin(expanded),
                    pszFooter = Pin(footer),
                    dwCommonButtons = TASKDIALOG_COMMON_BUTTON_FLAGS.TDCBF_CLOSE_BUTTON,
                    dwFlags = (links is { Length: > 0 } ? TASKDIALOG_FLAGS.TDF_USE_COMMAND_LINKS : 0)
                            | (hyperlinks ? TASKDIALOG_FLAGS.TDF_ENABLE_HYPERLINKS : 0),
                    cButtons = (uint)buttons.Length,
                    pButtons = pButtons,
                };
                cfg.Anonymous1.pszMainIcon = IconRes(icon);
                PInvoke.TaskDialogIndirect(cfg, out _, out _, out _).ThrowOnFailure();
            }
        }
        finally
        {
            foreach (var h in handles) h.Free();
        }
    }

    private static void ShellFileInUse()
    {
        // Realistic setup: ANOTHER process holds the file open (the shell doesn't show "File In Use" for a lock held by the
        // process doing the delete), and the operation has an owner window (no owner => no UI).
        var path = Path.Combine(Path.GetTempPath(), "huh-locked-report.xlsx");
        File.WriteAllText(path, "locked");
        using var locker = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"$f=[IO.File]::Open('{path}','Open','ReadWrite','None'); Start-Sleep 120\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
        for (int i = 0; i < 50 && CanOpenExclusive(path); i++) Thread.Sleep(100); // wait until the lock is taken
        try
        {
            WithOwnerWindow(owner => RunFileOperation(op => op.DeleteItem(ShellItem(path), null), FILEOPERATION_FLAGS.FOF_NOCONFIRMATION, owner));
        }
        finally { try { locker.Kill(); } catch { } }
    }

    private static bool CanOpenExclusive(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return true; }
        catch (IOException) { return false; }
    }

    private static IShellItem ShellItem(string path)
    {
        var iid = typeof(IShellItem).GUID;
        fixed (char* p = path)
        {
            PInvoke.SHCreateItemFromParsingName(p, null, &iid, out var o).ThrowOnFailure();
            return (IShellItem)o;
        }
    }

    private static void ShellCopyToWindows()
    {
        var path = Path.Combine(Path.GetTempPath(), "huh-copy-test.txt");
        File.WriteAllText(path, "copy me");
        RunFileOperation(op => op.CopyItem(ShellItem(path), ShellItem(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), null, null),
            FILEOPERATION_FLAGS.FOF_NOCONFIRMMKDIR);
    }

    private static void RunFileOperation(Action<IFileOperation> queue, FILEOPERATION_FLAGS flags, HWND owner = default)
    {
        var op = (IFileOperation)new FileOperation();
        op.SetOperationFlags(flags);
        if (!owner.IsNull) op.SetOwnerWindow(owner);
        queue(op);
        try { op.PerformOperations(); } catch (COMException) { /* user cancelled / failed: expected */ }
    }

    private static void ShellExecuteMissing()
    {
        fixed (char* file = @"C:\huh-does-not-exist\contoso-setup.exe")
        fixed (char* verb = "open")
        {
            var sei = new SHELLEXECUTEINFOW
            {
                cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                lpVerb = verb,
                lpFile = file,
                nShow = (int)SHOW_WINDOW_CMD.SW_SHOWNORMAL,
            };
            PInvoke.ShellExecuteEx(ref sei); // shows its own error UI (no SEE_MASK_FLAG_NO_UI)
        }
    }

    private static void RunChild(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
        p?.WaitForExit();
    }

    // Undocumented but stable ntdll API; used here only to reproduce the loader's hard-error box on demand.
    [DllImport("ntdll.dll")]
    private static extern int NtRaiseHardError(uint status, uint numberOfParameters, uint unicodeStringParameterMask,
        nint* parameters, uint validResponseOptions, out uint response);

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING { public ushort Length, MaximumLength; public char* Buffer; }

    private static void HardError(uint status, string param)
    {
        PInvoke.SetErrorMode(0); // make sure hard errors are not suppressed for this process
        fixed (char* p = param)
        {
            var us = new UNICODE_STRING { Length = (ushort)(param.Length * 2), MaximumLength = (ushort)(param.Length * 2), Buffer = p };
            nint arg = (nint)(&us);
            const uint OptionOk = 1;
            NtRaiseHardError(status, 1, 1, &arg, OptionOk, out _);
        }
    }

    // ---------------- custom window ----------------
    private static string? s_customText;

    private static void CustomWindow(string title, string text)
    {
        s_customText = text;
        var hInst = PInvoke.GetModuleHandle((string?)null);
        fixed (char* cls = "HuhHarnessWindow")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = s_wndProc,
                hInstance = (HINSTANCE)hInst.DangerousGetHandle(),
                lpszClassName = cls,
                hbrBackground = (Windows.Win32.Graphics.Gdi.HBRUSH)(nint)(Windows.Win32.Graphics.Gdi.SYS_COLOR_INDEX.COLOR_WINDOW + 1),
            };
            PInvoke.RegisterClassEx(wc);
            var hwnd = PInvoke.CreateWindowEx(0, "HuhHarnessWindow", title,
                WINDOW_STYLE.WS_OVERLAPPED | WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_SYSMENU | WINDOW_STYLE.WS_VISIBLE,
                200, 200, 460, 180, HWND.Null, null, hInst, null);
            PInvoke.CreateWindowEx(0, "STATIC", text, WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE,
                16, 16, 420, 60, hwnd, null, hInst, null);
            PInvoke.CreateWindowEx(0, "BUTTON", "OK", WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE,
                350, 90, 80, 28, hwnd, null, hInst, null);
            PumpMessages();
        }
    }

    private static void WithOwnerWindow(Action<HWND> show)
    {
        var hInst = PInvoke.GetModuleHandle((string?)null);
        var owner = PInvoke.CreateWindowEx(0, "STATIC", "Contoso License Manager",
            WINDOW_STYLE.WS_OVERLAPPEDWINDOW | WINDOW_STYLE.WS_VISIBLE, 150, 150, 600, 400, HWND.Null, null, hInst, null);
        show(owner);
    }

    private static readonly WNDPROC s_wndProc = WndProc; // rooted for the process lifetime

    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM w, LPARAM l)
    {
        if (msg == PInvoke.WM_DESTROY) { PInvoke.PostQuitMessage(0); return default; }
        return PInvoke.DefWindowProc(hwnd, msg, w, l);
    }

    private static void PumpMessages()
    {
        while (PInvoke.GetMessage(out var m, HWND.Null, 0, 0))
        {
            PInvoke.TranslateMessage(m);
            PInvoke.DispatchMessage(m);
        }
    }
}
