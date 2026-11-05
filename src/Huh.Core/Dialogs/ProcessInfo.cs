using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Huh.Core.Dialogs;

/// <summary>Elevation of a process as seen from huh (which runs at medium integrity).</summary>
public enum ElevationState
{
    Unknown,
    NotElevated,
    Elevated,
    /// <summary>The token could not be opened, which from a medium-IL process usually means elevated, SYSTEM or protected.</summary>
    AccessDenied,
}

public sealed record ProcessDetails(int Pid, string? Name, string? Path, ElevationState Elevation);

internal static unsafe class ProcessInfo
{
    private const int ERROR_ACCESS_DENIED = 5;

    public static ProcessDetails Get(int pid)
    {
        string? name = null, path = null;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { /* exited */ }

        var elevation = ElevationState.Unknown;
        using var h = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (!h.IsInvalid)
        {
            path = ImagePath(h);
            elevation = Elevation(h);
        }
        else if (Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED)
        {
            elevation = ElevationState.AccessDenied;
        }
        return new ProcessDetails(pid, name, path, elevation);
    }

    private static string? ImagePath(SafeFileHandle h)
    {
        Span<char> buf = stackalloc char[1024];
        uint size = (uint)buf.Length;
        return PInvoke.QueryFullProcessImageName(h, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buf, ref size)
            ? new string(buf[..(int)size]) : null;
    }

    private static ElevationState Elevation(SafeFileHandle process)
    {
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
            return Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED ? ElevationState.AccessDenied : ElevationState.Unknown;
        using (token)
        {
            TOKEN_ELEVATION e;
            uint ret;
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevation, &e, (uint)sizeof(TOKEN_ELEVATION), out ret))
                return ElevationState.Unknown;
            return e.TokenIsElevated != 0 ? ElevationState.Elevated : ElevationState.NotElevated;
        }
    }
}
