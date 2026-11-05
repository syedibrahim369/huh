using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
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
