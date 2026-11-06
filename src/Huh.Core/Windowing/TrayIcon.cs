using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Huh.Core.Windowing;

/// <summary>One entry of the tray menu. Id 0 with empty text is a separator.</summary>
public sealed record TrayMenuItem(int Id, string Text, bool Checked = false, bool Enabled = true, bool IsDefault = false)
{
    public static TrayMenuItem Separator { get; } = new(0, "");
    public bool IsSeparator => Id == 0 && Text.Length == 0;
}

/// <summary>
/// Notification-area icon via raw Shell_NotifyIcon (no package: the WinUI tray libraries are pre-release only).
/// Owns a hidden top-level window (not message-only: those don't receive the "TaskbarCreated" broadcast that says Explorer
/// restarted and icons must be re-added). Create and use it on the UI thread; WinUI's message loop pumps the window.
/// Left click / Enter raises <see cref="Activated"/>; right click / Shift+F10 shows the native menu built by the callback.
/// </summary>
public sealed unsafe partial class TrayIcon : IDisposable
{
    public const string WindowClass = "huh.TrayWindow";
    private const uint WM_NULL = 0x0000, WM_CONTEXTMENU = 0x007B, WM_LBUTTONUP = 0x0202, WM_APP = 0x8000;
    private const uint CallbackMessage = WM_APP + 1;
    /// <summary>Posted by a second huh instance: "open Settings" (then that instance exits).</summary>
    public const uint ShowRequestMessage = WM_APP + 2;
    private const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint MF_STRING = 0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    private const int SM_CXSMICON = 49, SM_CYSMICON = 50;
    private const uint IconId = 1;

    private static TrayIcon? s_current; // the static window procedure needs a way back to the instance

    private readonly Func<IReadOnlyList<TrayMenuItem>> _menu;
    private readonly uint _taskbarCreated;
    private nint _hwnd;
    private nint _icon;
    private string _tooltip;
    private bool _added;

    public event Action? Activated;
    public event Action<int>? Command;
    public event Action? ShowRequested;

    public TrayIcon(string iconPath, string tooltip, Func<IReadOnlyList<TrayMenuItem>> menu)
    {
        if (Interlocked.CompareExchange(ref s_current, this, null) is not null)
            throw new InvalidOperationException("Only one tray icon per process.");
        _menu = menu;
        _tooltip = tooltip;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        AllowDarkMenus();

        var hInstance = GetModuleHandleW(null);
        fixed (char* cls = WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = hInstance,
                lpszClassName = cls,
            };
            RegisterClassExW(&wc); // fails harmlessly if already registered
            _hwnd = CreateWindowExW(0, cls, cls, 0x80000000 /* WS_POPUP, never shown */, 0, 0, 0, 0, 0, 0, hInstance, null);
        }
        if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()})");
        _icon = LoadIcon(iconPath);
        Add();
    }

    /// <summary>Changes the icon and/or tooltip (e.g. paused state).</summary>
    public void Update(string iconPath, string tooltip)
    {
        var old = _icon;
        _icon = LoadIcon(iconPath);
        _tooltip = tooltip;
        var d = Data(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, &d);
        if (old != 0) DestroyIcon(old);
    }

    /// <summary>Asks an already running huh (same user session) to open its Settings. True if one was found.</summary>
    public static bool SignalExisting()
    {
        var h = FindWindowW(WindowClass, null);
        return h != 0 && PostMessageW(h, ShowRequestMessage, 0, 0);
    }

    private void Add()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIconW(NIM_ADD, &d);
        d.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &d);
    }

    private NOTIFYICONDATAW Data(uint flags)
    {
        var d = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        var tip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip;
        tip.AsSpan().CopyTo(new Span<char>(d.szTip, 128));
        d.szTip[tip.Length] = '\0';
        return d;
    }

    private static nint LoadIcon(string path)
    {
        uint dpi = GetDpiForSystem();
        return LoadImageW(0, path, IMAGE_ICON, GetSystemMetricsForDpi(SM_CXSMICON, dpi), GetSystemMetricsForDpi(SM_CYSMICON, dpi), LR_LOADFROMFILE);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var self = s_current;
        if (self is not null && self._hwnd == hwnd)
        {
            try
            {
                if (self.Handle(msg, wParam, lParam)) return 0;
            }
            catch { /* never let an exception cross the native boundary */ }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private bool Handle(uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0) { Add(); return true; } // Explorer restarted
        if (msg == ShowRequestMessage) { ShowRequested?.Invoke(); return true; }
        if (msg != CallbackMessage) return false;

        switch ((uint)(lParam & 0xFFFF))
        {
            case NIN_SELECT:
            case NIN_KEYSELECT:
                Activated?.Invoke();
                return true;
            case WM_CONTEXTMENU:
                // Version 4: wParam holds the anchor point (works for keyboard too).
                ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                return true;
            case WM_LBUTTONUP:
                return true; // NIN_SELECT already handled the click
        }
        return false;
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        try
        {
            foreach (var item in _menu())
            {
                if (item.IsSeparator) { AppendMenuW(menu, MF_SEPARATOR, 0, null); continue; }
                uint flags = MF_STRING | (item.Checked ? MF_CHECKED : 0) | (item.Enabled ? 0 : MF_GRAYED);
                AppendMenuW(menu, flags, (nuint)item.Id, item.Text);
                if (item.IsDefault) SetMenuDefaultItem(menu, (uint)item.Id, 0);
            }
            // Required so the menu closes when the user clicks elsewhere (documented TrackPopupMenu quirk).
            SetForegroundWindow(_hwnd);
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, x, y, _hwnd, 0);
            PostMessageW(_hwnd, WM_NULL, 0, 0);
            if (cmd != 0) Command?.Invoke(cmd);
        }
        finally { DestroyMenu(menu); }
    }

    /// <summary>
    /// Lets native popup menus follow the dark app theme. Undocumented uxtheme ordinals 135 (SetPreferredAppMode) and 136
    /// (FlushMenuThemes), used by many Win32 apps; if they are missing the menu is simply light.
    /// </summary>
    private static void AllowDarkMenus()
    {
        try
        {
            if (Environment.OSVersion.Version.Build < 18362) return;
            var ux = LoadLibraryExW("uxtheme.dll", 0, 0x00000800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */);
            if (ux == 0) return;
            var setMode = (delegate* unmanaged[Stdcall]<int, int>)GetProcAddress(ux, 135);
            var flush = (delegate* unmanaged[Stdcall]<void>)GetProcAddress(ux, 136);
            if (setMode != null) setMode(1 /* AllowDark: follow the system app theme */);
            if (flush != null) flush();
        }
        catch { }
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = Data(0);
            Shell_NotifyIconW(NIM_DELETE, &d);
            _added = false;
        }
        if (_icon != 0) { DestroyIcon(_icon); _icon = 0; }
        if (_hwnd != 0) { DestroyWindow(_hwnd); _hwnd = 0; }
        Interlocked.CompareExchange(ref s_current, null, this);
    }

    // ------------------------------------------------------------------ interop (hand-written: blittable, no runtime marshalling)

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterWindowMessageW(string name);
    [LibraryImport("user32.dll")] private static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, void* param);
    [LibraryImport("user32.dll")] private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint FindWindowW(string cls, string? name);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetMenuDefaultItem(nint menu, uint item, uint byPos);
    [LibraryImport("user32.dll")] private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyMenu(nint menu);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImageW(nint inst, string name, uint type, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyIcon(nint icon);
    [LibraryImport("user32.dll")] private static partial uint GetDpiForSystem();
    [LibraryImport("user32.dll")] private static partial int GetSystemMetricsForDpi(int index, uint dpi);
    [LibraryImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool Shell_NotifyIconW(uint msg, NOTIFYICONDATAW* data);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint LoadLibraryExW(string name, nint file, uint flags);
    [LibraryImport("kernel32.dll")] private static partial nint GetProcAddress(nint module, nint ordinal);
}
