using Huh.Core.AI;
using Huh.Core.Facts;
using Huh.Core.Windowing;
using Microsoft.UI.Dispatching;

namespace Huh;

/// <summary>
/// One bubble per dialog window. UI-thread only. Shows the deterministic (M3) content immediately, then fills in the AI
/// explanation when (and only if) the user has opted in. AI work runs off the UI thread.
/// </summary>
internal sealed class BubbleManager(DispatcherQueue ui, AiExplainService ai, Func<bool> aiEnabled, Action requestConsent)
{
    private readonly Dictionary<nint, BubbleWindow> _bubbles = [];

    public void Show(DialogFacts facts)
    {
        var dialog = facts.Dialog;
        if (dialog.Hwnd == 0 || _bubbles.ContainsKey(dialog.Hwnd)) return; // window-less UIA dialogs: no anchor yet
        // The dialog may already be gone by the time the UI thread gets here (it was busy, the user was quick).
        if (!Screen.IsVisible(dialog.Hwnd)) { DevLog.Write($"dialog 0x{dialog.Hwnd:X} gone before its bubble was shown"); return; }
        var bubble = new BubbleWindow(facts, ai.Redactor);
        bubble.Dismissed += b => Close(b.Dialog.Hwnd);
        bubble.AiOptInRequested += _ => requestConsent();
        _bubbles[dialog.Hwnd] = bubble;
        if (!bubble.ShowBeside()) { Close(dialog.Hwnd); return; }
        StartAi(bubble);
    }

    /// <summary>After the user opts in, explain the bubbles that are already open.</summary>
    public void RefreshAi()
    {
        foreach (var b in _bubbles.Values) StartAi(b);
    }

    private void StartAi(BubbleWindow bubble)
    {
        if (!aiEnabled()) { bubble.SetAiDisabled(); return; }
        if (bubble.Dialog.BlockedByUipi) return;
        bubble.SetAiLoading(ai.ModelName);
        var hwnd = bubble.Dialog.Hwnd;
        _ = Task.Run(async () =>
        {
            var outcome = await ai.ExplainAsync(bubble.Facts).ConfigureAwait(false);
            // Log metadata only (never dialog text or the key).
            DevLog.Write($"ai for 0x{hwnd:X}: {outcome.Status} {outcome.Reason} [{outcome.Detail}] model={outcome.Model} cached={outcome.FromCache} {outcome.LatencyMs:F0} ms " +
                         $"action={outcome.Action?.Action?.Id.ToString() ?? (outcome.Action?.RejectedReason is null ? "none" : "rejected")}");
            ui.TryEnqueue(() =>
            {
                if (_bubbles.TryGetValue(hwnd, out var still) && ReferenceEquals(still, bubble)) bubble.SetAi(outcome);
            });
        });
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
