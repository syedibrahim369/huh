using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using Huh.Core.Dialogs;

namespace Huh.Core.Provenance;

public enum SourceKind
{
    /// <summary>A normal desktop program.</summary>
    App,
    /// <summary>A Microsoft Store / MSIX app (signature verified by Windows at install time).</summary>
    StoreApp,
    /// <summary>Raised by a core Windows process (e.g. csrss for "missing DLL" boxes), usually on behalf of another program.</summary>
    Windows,
    /// <summary>A JavaScript alert/confirm/prompt from a web page, shown by a browser.</summary>
    WebPage,
}

/// <param name="DisplayName">Friendly program name ("Windows Installer", "Notepad").</param>
/// <param name="Publisher">Who made it: the verified signer, or the (unverified) CompanyName from version info.</param>
/// <param name="PublisherVerified">True when <paramref name="Publisher"/> comes from a valid signature or package identity.</param>
/// <param name="OnBehalfOf">For <see cref="SourceKind.Windows"/>: the program the error is really about ("game.exe").</param>
/// <param name="Browser">For <see cref="SourceKind.WebPage"/>: "Microsoft Edge", "Google Chrome"...</param>
/// <param name="WebOrigin">For <see cref="SourceKind.WebPage"/>: the site named in the dialog ("example.com"), if shown.</param>
public sealed record SourceInfo(
    SourceKind Kind,
    string DisplayName,
    string? Publisher,
    bool PublisherVerified,
    SignatureInfo Signature,
    string? ExeName,
    string? Path,
    string? OnBehalfOf = null,
    string? Browser = null,
    string? WebOrigin = null);

/// <summary>Works out who raised a dialog and whether to trust that claim. Deterministic and offline.</summary>
public static partial class SourceAnalyzer
{
    private static readonly Dictionary<string, string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = "Microsoft Edge", ["chrome"] = "Google Chrome", ["firefox"] = "Firefox", ["brave"] = "Brave",
        ["opera"] = "Opera", ["vivaldi"] = "Vivaldi", ["iexplore"] = "Internet Explorer", ["arc"] = "Arc",
    };

    // Core Windows processes that raise dialogs for others. Their paths are protected, so we resolve them ourselves.
    private static readonly HashSet<string> WindowsHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "winlogon", "smss", "services", "lsass", "werfault", "consent",
    };

    // Chromium: "example.com says", "This page says", "An embedded page at x.com says"; Firefox shows the origin as text.
    [GeneratedRegex(@"(?:^|\n)\s*(?:An embedded page at\s+)?(?<origin>[^\s\n]+?|This page)\s+says\b", RegexOptions.IgnoreCase)]
    private static partial Regex SaysPattern();

    // csrss hard-error titles: "game.exe - System Error", "app.exe - Entry Point Not Found", "x.exe - Application Error"
    [GeneratedRegex(@"^(?<exe>[^\\/:*?""<>|]+?\.exe)\s+-\s+", RegexOptions.IgnoreCase)]
    private static partial Regex HardErrorTitle();

    public static SourceInfo Analyze(DialogInfo d)
    {
        var p = d.Process;
        string? exeName = p.Name is null ? null : p.Name + ".exe";

        // ---- Raised by Windows itself (csrss etc.) ----
        if (p.Name is not null && WindowsHosts.Contains(p.Name))
        {
            string sysPath = p.Path ?? System.IO.Path.Combine(Environment.SystemDirectory, exeName!);
            var onBehalf = HardErrorTitle().Match(d.Title) is { Success: true } m ? m.Groups["exe"].Value : null;
            return new SourceInfo(SourceKind.Windows, "Windows", "Microsoft", true, Authenticode.Verify(sysPath), exeName, sysPath,
                OnBehalfOf: onBehalf);
        }

        // ---- A web page, shown by a browser ----
        if (p.Name is not null && Browsers.TryGetValue(p.Name, out var browser))
        {
            var text = d.Title + "\n" + d.Message;
            var says = SaysPattern().Match(text);
            if (says.Success || d.Kind == DialogKind.OtherWindow)
            {
                string? origin = says.Success && !says.Groups["origin"].Value.Equals("This page", StringComparison.OrdinalIgnoreCase)
                    ? says.Groups["origin"].Value : null;
                return new SourceInfo(SourceKind.WebPage, browser, null, false, Authenticode.Verify(p.Path), exeName, p.Path,
                    Browser: browser, WebOrigin: origin);
            }
        }

        // ---- Store / MSIX app ----
        if (p.Path is not null && p.Path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) && ReadPackage(p.Path) is { } pkg)
        {
            return new SourceInfo(SourceKind.StoreApp, pkg.DisplayName ?? DisplayNameFor(p), pkg.Publisher, true,
                new SignatureInfo(SignatureState.Signed, pkg.Publisher, Detail: "MSIX package signature (verified by Windows at install)"),
                exeName, p.Path);
        }

        // ---- Normal desktop app ----
        var sig = Authenticode.Verify(p.Path);
        var (name, company) = VersionInfo(p.Path);
        bool verified = sig.State == SignatureState.Signed && sig.Signer is not null;
        return new SourceInfo(SourceKind.App, name ?? p.Name ?? "Unknown program", verified ? sig.Signer : company, verified, sig, exeName, p.Path);
    }

    private static string DisplayNameFor(ProcessDetails p) => VersionInfo(p.Path).Name ?? p.Name ?? "Unknown program";

    private static (string? Name, string? Company) VersionInfo(string? path)
    {
        if (path is null) return (null, null);
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            string? name = !string.IsNullOrWhiteSpace(v.FileDescription) ? v.FileDescription.Trim()
                         : !string.IsNullOrWhiteSpace(v.ProductName) ? v.ProductName.Trim() : null;
            // Some apps (Store Notepad) describe themselves as "Notepad.exe": show "Notepad".
            if (name is not null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            return (name, string.IsNullOrWhiteSpace(v.CompanyName) ? null : v.CompanyName.Trim());
        }
        catch { return (null, null); }
    }

    /// <summary>Reads Identity/@Publisher and Properties/PublisherDisplayName + DisplayName from the package's AppxManifest.xml.</summary>
    private static (string? Publisher, string? DisplayName)? ReadPackage(string exePath)
    {
        try
        {
            // ...\WindowsApps\<PackageFullName>\...\app.exe  ->  ...\WindowsApps\<PackageFullName>\AppxManifest.xml
            int i = exePath.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) + @"\WindowsApps\".Length;
            int j = exePath.IndexOf('\\', i);
            if (j < 0) return null;
            string manifest = System.IO.Path.Combine(exePath[..j], "AppxManifest.xml");
            if (!File.Exists(manifest)) return null;

            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                doc.Load(reader);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
            string? publisherCn = doc.SelectSingleNode("/f:Package/f:Identity/@Publisher", ns)?.Value;
            string? publisherName = doc.SelectSingleNode("/f:Package/f:Properties/f:PublisherDisplayName", ns)?.InnerText;
            string? display = doc.SelectSingleNode("/f:Package/f:Properties/f:DisplayName", ns)?.InnerText;
            if (display?.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) == true) display = null; // localized resource
            if (publisherName?.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) == true) publisherName = null;
            return (publisherName ?? CommonName(publisherCn), display);
        }
        catch { return null; }
    }

    private static string? CommonName(string? dn) =>
        dn?.Split(',').Select(s => s.Trim()).FirstOrDefault(s => s.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))?[3..];
}
