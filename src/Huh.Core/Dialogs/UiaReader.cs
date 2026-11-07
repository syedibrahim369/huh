using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Huh.Core.Interop;

namespace Huh.Core.Dialogs;

/// <summary>One element in a dialog's UIA subtree (text only; huh never captures pixels).</summary>
public sealed record UiaNode(string ControlType, string Name, string ClassName, string AutomationId);

/// <summary>Result of reading a window through UI Automation.</summary>
internal sealed record UiaSnapshot(string RootName, bool? IsDialog, IReadOnlyList<UiaNode> Nodes, string? Error);

/// <summary>
/// Raw UIA COM client (CsWin32 [GeneratedComInterface] projections, trim-safe).
/// Must be used from an MTA thread that is NOT a UI thread: UIA calls into other processes and can block.
/// </summary>
internal sealed class UiaReader
{
    private const uint TimeoutMs = 1500;

    public IUIAutomation Automation { get; }
    private readonly IUIAutomationCacheRequest _cache;
    private readonly IUIAutomationCondition _all;

    public UiaReader()
    {
        Automation = CUIAutomation8.CreateInstance<IUIAutomation>();
        if (Automation is IUIAutomation2 a2)
        {
            // Bound how long a hung target app can stall us (defaults are 2s connect / 20s transaction).
            a2.put_ConnectionTimeout(TimeoutMs);
            a2.put_TransactionTimeout(TimeoutMs);
        }

        _all = Automation.CreateTrueCondition();
        _cache = Automation.CreateCacheRequest();
        _cache.AddProperty(UIA_PROPERTY_ID.UIA_NamePropertyId);
        _cache.AddProperty(UIA_PROPERTY_ID.UIA_ControlTypePropertyId);
        _cache.AddProperty(UIA_PROPERTY_ID.UIA_ClassNamePropertyId);
        _cache.AddProperty(UIA_PROPERTY_ID.UIA_AutomationIdPropertyId);
    }

    public UiaSnapshot Read(nint hwnd)
    {
        IUIAutomationElement root;
        try { root = Automation.ElementFromHandle((HWND)hwnd); }
        catch (COMException ex) { return new UiaSnapshot("", null, [], $"ElementFromHandle 0x{ex.HResult:X8}"); }
        return Read(root);
    }

    public UiaSnapshot Read(IUIAutomationElement root)
    {
        string rootName = "";
        bool? isDialog = null;
        var nodes = new List<UiaNode>();
        try
        {
            rootName = Native.Take(root.get_CurrentName());
            isDialog = ReadBool(root, UIA_PROPERTY_ID.UIA_IsDialogPropertyId);

            // One cross-process round trip for the whole subtree.
            var found = root.FindAllBuildCache(TreeScope.TreeScope_Descendants, _all, _cache);
            int n = found.get_Length();
            for (int i = 0; i < n; i++)
            {
                var e = found.GetElement(i);
                nodes.Add(new UiaNode(
                    ControlTypeName(e.get_CachedControlType()),
                    CleanText(Native.Take(e.get_CachedName())),
                    Native.Take(e.get_CachedClassName()),
                    Native.Take(e.get_CachedAutomationId())));
            }
            return new UiaSnapshot(rootName, isDialog, nodes, null);
        }
        catch (COMException ex)
        {
            return new UiaSnapshot(rootName, isDialog, nodes, $"UIA 0x{ex.HResult:X8}");
        }
    }

    /// <summary>
    /// Finds a dialog drawn INSIDE a top-level window (no HWND of its own): Chromium tab-modal JS dialogs
    /// (JavaScriptTabModalDialogViewViews) or any element that declares IsDialog (Firefox prompts, XAML ContentDialogs).
    /// Uses a property condition so UIA can stop at the first match instead of us reading the whole web page.
    /// Returns the dialog's root (the element whose subtree has title, text and buttons).
    /// </summary>
    public IUIAutomationElement? FindEmbeddedDialog(nint topLevel) => FindEmbeddedDialog(topLevel, out _);

    /// <summary>
    /// Breadth-first search of the host window's OWN UI (browser frame, toolbars, dialogs), never descending into web content
    /// (Document elements / Chrome_RenderWidgetHostHWND): a full-page UIA search can take seconds on big pages.
    /// Matches Chromium's tab-modal JS dialog view, or any Window/Pane that declares IsDialog.
    /// </summary>
    public IUIAutomationElement? FindEmbeddedDialog(nint topLevel, out int depth)
    {
        depth = -1;
        const int MaxDepth = 6, MaxNodes = 600;
        try
        {
            var root = Automation.ElementFromHandle((HWND)topLevel);
            var level = new List<IUIAutomationElement> { root };
            int visited = 0;
            for (int d = 1; d <= MaxDepth && level.Count > 0 && visited < MaxNodes; d++)
            {
                var next = new List<IUIAutomationElement>();
                foreach (var parent in level)
                {
                    var kids = parent.FindAllBuildCache(TreeScope.TreeScope_Children, _all, _cache);
                    int n = kids.get_Length();
                    for (int i = 0; i < n && visited < MaxNodes; i++, visited++)
                    {
                        var k = kids.GetElement(i);
                        string cls = Native.Take(k.get_CachedClassName());
                        var type = k.get_CachedControlType();
                        if (cls == "JavaScriptTabModalDialogViewViews" || (type is UIA_CONTROLTYPE_ID.UIA_WindowControlTypeId or UIA_CONTROLTYPE_ID.UIA_PaneControlTypeId && ReadBool(k, UIA_PROPERTY_ID.UIA_IsDialogPropertyId) == true))
                        {
                            depth = d;
                            return DialogRoot(k, topLevel);
                        }
                        // Never walk into web content: that's the page's DOM, potentially thousands of nodes.
                        if (type == UIA_CONTROLTYPE_ID.UIA_DocumentControlTypeId || cls is "Chrome_RenderWidgetHostHWND" or "MozillaWindowClass") continue;
                        next.Add(k);
                    }
                }
                level = next;
            }
            return null;
        }
        catch (COMException) { return null; }
    }

    /// <summary>Chromium: JavaScriptTabModalDialogViewViews -> DialogClientView -> RootView (Window with the "x says" title).</summary>
    private IUIAutomationElement DialogRoot(IUIAutomationElement hit, nint topLevel)
    {
        var walker = Automation.get_ControlViewWalker();
        var e = hit;
        for (int i = 0; i < 3; i++)
        {
            if (e.get_CurrentControlType() == UIA_CONTROLTYPE_ID.UIA_WindowControlTypeId) return e;
            var parent = walker.GetParentElement(e);
            if (parent is null || (nint)parent.get_CurrentNativeWindowHandle() == topLevel) break;
            e = parent;
        }
        return hit;
    }

    /// <summary>Test tooling: find a Button / Hyperlink / CheckBox in a window by (part of) its name and invoke or toggle it.
    /// Uses UIA patterns, so it neither moves the mouse nor activates the window.</summary>
    public string Activate(nint hwnd, string nameContains, bool exact = false)
    {
        var root = Automation.ElementFromHandle((HWND)hwnd);
        var found = root.FindAllBuildCache(TreeScope.TreeScope_Descendants, _all, _cache);
        int n = found.get_Length();
        for (int i = 0; i < n; i++)
        {
            var e = found.GetElement(i);
            var type = e.get_CachedControlType();
            if (type is not (UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId or UIA_CONTROLTYPE_ID.UIA_HyperlinkControlTypeId or UIA_CONTROLTYPE_ID.UIA_CheckBoxControlTypeId or UIA_CONTROLTYPE_ID.UIA_MenuItemControlTypeId)) continue;
            var name = Native.Take(e.get_CachedName());
            if (exact ? !name.Equals(nameContains, StringComparison.OrdinalIgnoreCase) : !name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (type == UIA_CONTROLTYPE_ID.UIA_CheckBoxControlTypeId)
            {
                e.GetCurrentPatternAs<IUIAutomationTogglePattern>(UIA_PATTERN_ID.UIA_TogglePatternId, out var toggle);
                toggle.Toggle();
                return $"toggled checkbox '{name}'";
            }
            e.GetCurrentPatternAs<IUIAutomationInvokePattern>(UIA_PATTERN_ID.UIA_InvokePatternId, out var invoke);
            invoke.Invoke();
            return $"invoked '{name}'";
        }
        return $"nothing named '*{nameContains}*' found";
    }

    /// <summary>Test tooling: expands the first expander/combo whose name contains the text (ExpandCollapse pattern).</summary>
    public string Expand(nint hwnd, string nameContains)
    {
        var root = Automation.ElementFromHandle((HWND)hwnd);
        var found = root.FindAllBuildCache(TreeScope.TreeScope_Descendants, _all, _cache);
        for (int i = 0, n = found.get_Length(); i < n; i++)
        {
            var e = found.GetElement(i);
            var name = Native.Take(e.get_CachedName());
            if (!name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                e.GetCurrentPatternAs<IUIAutomationExpandCollapsePattern>(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId, out var ec);
                if (ec is null) continue;
                ec.Expand();
                return $"expanded '{name}'";
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException) { }
        }
        return $"nothing expandable named '*{nameContains}*' found";
    }

    /// <summary>Test tooling: sets the vertical scroll position (0-100 %) of the first scrollable element in the window.</summary>
    public string ScrollTo(nint hwnd, double verticalPercent)
    {
        var root = Automation.ElementFromHandle((HWND)hwnd);
        var found = root.FindAllBuildCache(TreeScope.TreeScope_Subtree, _all, _cache);
        for (int i = 0, n = found.get_Length(); i < n; i++)
        {
            var e = found.GetElement(i);
            try
            {
                e.GetCurrentPatternAs<IUIAutomationScrollPattern>(UIA_PATTERN_ID.UIA_ScrollPatternId, out var sp);
                if (sp is null || !sp.get_CurrentVerticallyScrollable()) continue;
                sp.SetScrollPercent(-1 /* UIA_ScrollPatternNoScroll */, verticalPercent);
                return $"scrolled to {verticalPercent}%";
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException) { }
        }
        return "nothing scrollable found";
    }

    /// <summary>Screen rectangle of an element in physical pixels, or null if it's gone / has no area.</summary>
    public static Placement.PxRect? Bounds(IUIAutomationElement e)
    {
        try
        {
            var r = e.get_CurrentBoundingRectangle();
            return r.right > r.left && r.bottom > r.top ? new Placement.PxRect(r.left, r.top, r.right, r.bottom) : null;
        }
        catch (COMException) { return null; }
    }

    /// <summary>Strips invisible bidi/format marks (Explorer sprinkles U+200E into dates and sizes) and trims.</summary>
    internal static string CleanText(string s)
    {
        if (s.Length == 0) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Format) sb.Append(ch);
        return sb.ToString().Trim();
    }

    private static bool? ReadBool(IUIAutomationElement e, UIA_PROPERTY_ID id)
    {
        var v = e.GetCurrentPropertyValue(id);
        try { return v.VarType == VarEnum.VT_BOOL ? v.As<bool>() : null; }
        finally { v.Dispose(); }
    }

    internal static string ControlTypeName(UIA_CONTROLTYPE_ID id)
    {
        var s = id.ToString(); // e.g. "UIA_ButtonControlTypeId"
        return s.StartsWith("UIA_", StringComparison.Ordinal) && s.EndsWith("ControlTypeId", StringComparison.Ordinal)
            ? s[4..^"ControlTypeId".Length]
            : s;
    }
}

/// <summary>COM sink for UIA WindowOpened events. Invoked on UIA's own MTA threads.</summary>
[GeneratedComClass]
internal sealed partial class UiaWindowOpenedHandler(Action<IUIAutomationElement> onOpened) : IUIAutomationEventHandler
{
    public void HandleAutomationEvent(IUIAutomationElement sender, UIA_EVENT_ID eventId)
    {
        try { onOpened(sender); } catch { /* never throw back into UIA */ }
    }
}
