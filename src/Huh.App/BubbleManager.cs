using Huh.Core.Facts;
using Huh.Core.Windowing;

namespace Huh;

/// <summary>One bubble per dialog window. UI-thread only.</summary>
internal sealed class BubbleManager
{
    private readonly Dictionary<nint, BubbleWindow> _bubbles = [];

    public void Show(DialogFacts facts)
    {
        var dialog = facts.Dialog;
        if (dialog.Hwnd == 0 || _bubbles.ContainsKey(dialog.Hwnd)) return; // window-less UIA dialogs: no anchor yet
        // The dialog may already be gone by the time the UI thread gets here (it was busy, the user was quick).
        if (!Screen.IsVisible(dialog.Hwnd)) { DevLog.Write($"dialog 0x{dialog.Hwnd:X} gone before its bubble was shown"); return; }
        var bubble = new BubbleWindow(facts);
        bubble.Dismissed += b => Close(b.Dialog.Hwnd);
        _bubbles[dialog.Hwnd] = bubble;
        if (!bubble.ShowBeside()) Close(dialog.Hwnd);
    }

    public void Close(nint dialogHwnd)
    {
        if (_bubbles.Remove(dialogHwnd, out var bubble)) bubble.CloseBubble();
    }

    public void CloseAll()
    {
        foreach (var h in _bubbles.Keys.ToList()) Close(h);
    }
}
