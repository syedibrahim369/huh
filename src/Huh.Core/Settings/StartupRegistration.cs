using Microsoft.Win32;

namespace Huh.Core.Settings;

/// <summary>
/// "Start with Windows" for the current user only: a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no admin
/// rights needed). Off by default. Windows also records the Task Manager "Startup apps" toggle under
/// ...\Explorer\StartupApproved\Run; a disabled entry there counts as off, and turning it on here clears that override so the
/// two places agree.
/// </summary>
public sealed class StartupRegistration(string runKeyPath = StartupRegistration.RunKey, string approvedKeyPath = StartupRegistration.ApprovedKey)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "huh";

    public static string CommandFor(string exePath) => $"\"{exePath}\"";

    public bool IsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(runKeyPath);
        if (run?.GetValue(ValueName) is not string { Length: > 0 }) return false;
        using var approved = Registry.CurrentUser.OpenSubKey(approvedKeyPath);
        // First byte 2 (or missing) = enabled; odd values (3) = disabled in Task Manager.
        return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
    }

    /// <summary>The registered command, if any (to notice when huh was moved to another folder).</summary>
    public string? RegisteredCommand()
    {
        using var run = Registry.CurrentUser.OpenSubKey(runKeyPath);
        return run?.GetValue(ValueName) as string;
    }

    public void Enable(string exePath)
    {
        if (!Path.IsPathFullyQualified(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("needs the full path of huh's exe", nameof(exePath));
        using (var run = Registry.CurrentUser.CreateSubKey(runKeyPath))
            run.SetValue(ValueName, CommandFor(exePath), RegistryValueKind.String);
        using var approved = Registry.CurrentUser.OpenSubKey(approvedKeyPath, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public void Disable()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true))
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(approvedKeyPath, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// If huh is registered but the registered exe no longer exists (huh was moved, e.g. the zip was extracted elsewhere), point
    /// the entry at the current exe. A registered exe that still exists is left alone (another copy may be the one wanted).
    /// </summary>
    public bool RepairPath(string exePath)
    {
        if (RegisteredCommand() is not { } cmd || string.Equals(cmd, CommandFor(exePath), StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsEnabled() || File.Exists(cmd.Trim('"'))) return false;
        Enable(exePath);
        return true;
    }
}
