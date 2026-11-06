using System.Diagnostics;
using Huh.Core.Actions;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Huh;

/// <summary>
/// The real side effects behind <see cref="IActionLauncher"/>. Targets reaching this class were validated by huh at offer time
/// AND re-checked by <see cref="FixActionRunner"/> at click time; these methods add a last scheme/shape guard and never pass
/// arguments to a program they start.
/// </summary>
internal sealed class WinAppLauncher(nint ownerHwnd, DispatcherQueue ui) : IActionLauncher
{
    public void OpenUri(string uri)
    {
        if (!(uri.StartsWith("ms-settings:", StringComparison.Ordinal) || uri.StartsWith("https://", StringComparison.Ordinal)))
            throw new InvalidOperationException("only ms-settings: and https: links can be opened");
        using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    public void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new IOException("the folder no longer exists");
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        // explorer.exe with the folder as its single argument: opens a window on that folder, never "runs" anything.
        var psi = new ProcessStartInfo(explorer) { UseShellExecute = false };
        psi.ArgumentList.Add(folder);
        using var _ = Process.Start(psi);
    }

    public void RunAsAdmin(string exePath, string workingDirectory)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",                       // UAC elevation prompt
            WorkingDirectory = workingDirectory,  // Arguments deliberately never set
            ErrorDialog = false,
            ErrorDialogParentHandle = ownerHwnd,  // ties the UAC prompt to the bubble
        };
        using var _ = Process.Start(psi);
    }

    public void CopyText(string text)
    {
        // Clipboard is a UI-thread API.
        var done = new ManualResetEventSlim();
        Exception? error = null;
        if (!ui.TryEnqueue(() =>
            {
                try
                {
                    var p = new DataPackage();
                    p.SetText(text);
                    Clipboard.SetContent(p);
                    Clipboard.Flush();
                }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            }))
            throw new InvalidOperationException("UI thread unavailable");
        done.Wait(TimeSpan.FromSeconds(3));
        if (error is not null) throw new InvalidOperationException("couldn't use the clipboard: " + error.Message);
    }
}
