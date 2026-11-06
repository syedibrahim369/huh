using System.Diagnostics;

namespace Huh;

/// <summary>
/// Tiny diagnostics log: %LOCALAPPDATA%\huh\logs\huh.log. Records window/lifecycle events only, never dialog text
/// (privacy rule: dialog content can contain personal data).
/// </summary>
internal static class DevLog
{
    private static readonly object Gate = new();
    private static readonly string? FilePath = Init();

    private static string? Init()
    {
        try
        {
            var dir = Huh.Core.Settings.HuhPaths.Logs;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "huh.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
            return path;
        }
        catch { return null; }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}";
        Debug.WriteLine(line);
        if (FilePath is null) return;
        lock (Gate) { try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { } }
    }
}
