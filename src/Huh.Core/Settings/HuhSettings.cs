using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Huh.Core.Settings;

/// <summary>Where huh keeps its files. <c>HUH_DATA_DIR</c> (development/testing) points everything at another folder, so a
/// first-run screen can be checked without touching the real settings.</summary>
public static class HuhPaths
{
    public static string DataDir =>
        Environment.GetEnvironmentVariable("HUH_DATA_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "huh");

    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string Key => Path.Combine(DataDir, "gemini.key");
    public static string AiCache => Path.Combine(DataDir, "cache", "ai");
    public static string Logs => Path.Combine(DataDir, "logs");
}

/// <summary>An app the user has seen error dialogs from (stored locally; offered in the exclusions picker).</summary>
public sealed record SeenApp(string Exe, string Name, DateTimeOffset LastSeen);

/// <summary>User settings in %LOCALAPPDATA%\huh\settings.json. AI is OFF until the user accepts on the consent screen.</summary>
public sealed partial class HuhSettings
{
    public const string DefaultModel = "gemini-3.8-flash";
    public const string DefaultFallbackModel = "gemini-3.5-flash-lite";
    public const string ProviderGemini = "gemini";
    public const int MaxRecentApps = 15;

    public bool WelcomeSeen { get; set; }
    public bool AiConsentDecided { get; set; }
    public bool AiEnabled { get; set; }
    public DateTimeOffset? AiConsentAt { get; set; }
    /// <summary>Only "gemini" today (a local Ollama provider is on the roadmap).</summary>
    public string Provider { get; set; } = ProviderGemini;
    public string Model { get; set; } = DefaultModel;
    public string FallbackModel { get; set; } = DefaultFallbackModel;
    /// <summary>Exe names (lower case, e.g. "notepad.exe") whose dialogs huh ignores.</summary>
    public List<string> ExcludedApps { get; set; } = [];
    /// <summary>Most recent first, at most <see cref="MaxRecentApps"/>: exe name, display name and date only.</summary>
    public List<SeenApp> RecentApps { get; set; } = [];

    public static string DefaultPath => HuhPaths.Settings;

    /// <summary>Primary first, then the fallback (if different).</summary>
    public IReadOnlyList<string> Models =>
        string.Equals(Model, FallbackModel, StringComparison.OrdinalIgnoreCase) ? [Model] : [Model, FallbackModel];

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.\-]*$")]
    private static partial Regex ModelNameRegex();

    /// <summary>Model names go into the request URL path, so only plain model ids are accepted.</summary>
    public static bool IsValidModelName(string? name) => name is { Length: > 0 and <= 64 } && ModelNameRegex().IsMatch(name);

    public static string NormalizeExe(string exe) => Path.GetFileName(exe.Trim()).ToLowerInvariant();

    /// <summary>
    /// The app a dialog is attributed to, for exclusions and the recently-seen list: for a Windows hard error the program it is
    /// about ("game.exe", not csrss.exe), otherwise the program that raised it (for a web page: the browser).
    /// </summary>
    public static string? AppKeyFor(Facts.DialogFacts f)
    {
        if (f.Source.OnBehalfOf is { Length: > 0 } about && about.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return NormalizeExe(about);
        if (f.Source.ExeName is { Length: > 0 } exe) return NormalizeExe(exe);
        if (f.Dialog.Process.Path is { Length: > 0 } p) return NormalizeExe(p);
        return f.Dialog.Process.Name is { Length: > 0 } n ? NormalizeExe(n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe") : null;
    }

    /// <summary>Display name for <see cref="AppKeyFor"/> (shown in the exclusions picker).</summary>
    public static string AppNameFor(Facts.DialogFacts f) =>
        f.Source.OnBehalfOf is { Length: > 0 } about && about.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(about)
            : f.Source.Kind == Provenance.SourceKind.WebPage ? f.Source.Browser ?? f.Source.DisplayName : f.Source.DisplayName;

    public bool IsExcluded(string? exe) =>
        exe is { Length: > 0 } && ExcludedApps.Contains(NormalizeExe(exe), StringComparer.OrdinalIgnoreCase);

    public bool Exclude(string exe)
    {
        var e = NormalizeExe(exe);
        if (e.Length == 0 || IsExcluded(e)) return false;
        ExcludedApps.Add(e);
        ExcludedApps.Sort(StringComparer.OrdinalIgnoreCase);
        return true;
    }

    public bool Include(string exe) =>
        ExcludedApps.RemoveAll(x => string.Equals(x, NormalizeExe(exe), StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>Remember an app the user saw a dialog from (most recent first, one entry per exe).</summary>
    public void NoteSeen(string exe, string name, DateTimeOffset when)
    {
        var e = NormalizeExe(exe);
        if (e.Length == 0) return;
        RecentApps.RemoveAll(a => string.Equals(a.Exe, e, StringComparison.OrdinalIgnoreCase));
        RecentApps.Insert(0, new SeenApp(e, name.Trim().Length > 0 ? name.Trim() : e, when));
        if (RecentApps.Count > MaxRecentApps) RecentApps.RemoveRange(MaxRecentApps, RecentApps.Count - MaxRecentApps);
    }

    public static HuhSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new HuhSettings();
            var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var s = new HuhSettings
            {
                WelcomeSeen = (bool?)o["welcomeSeen"] ?? false,
                AiConsentDecided = (bool?)o["aiConsentDecided"] ?? false,
                AiEnabled = (bool?)o["aiEnabled"] ?? false,
                AiConsentAt = DateTimeOffset.TryParse((string?)o["aiConsentAt"], out var at) ? at : null,
                Provider = ProviderGemini, // the only provider today
                Model = (string?)o["model"] is { } m && IsValidModelName(m) ? m : DefaultModel,
                FallbackModel = (string?)o["fallbackModel"] is { } fm && IsValidModelName(fm) ? fm : DefaultFallbackModel,
            };
            // Someone who answered the AI question before the welcome screen existed has effectively seen it.
            if (s.AiConsentDecided) s.WelcomeSeen = true;
            if (o["excludedApps"] is JsonArray ex)
                foreach (var x in ex)
                    if (x is JsonValue v && v.TryGetValue<string>(out var e) && e.Length > 0) s.Exclude(e);
            if (o["recentApps"] is JsonArray ra)
                foreach (var x in ra.OfType<JsonObject>())
                {
                    if (x["exe"] is not JsonValue ev || !ev.TryGetValue<string>(out var exe) || exe.Length == 0) continue;
                    var n = NormalizeExe(exe);
                    if (s.RecentApps.Count >= MaxRecentApps || s.RecentApps.Any(a => a.Exe == n)) continue;
                    string name = x["name"] is JsonValue nv && nv.TryGetValue<string>(out var nm) ? nm : n;
                    var seen = x["lastSeen"] is JsonValue lv && lv.TryGetValue<string>(out var ls) && DateTimeOffset.TryParse(ls, out var t) ? t : DateTimeOffset.MinValue;
                    s.RecentApps.Add(new SeenApp(n, name, seen));
                }
            return s;
        }
        catch { return new HuhSettings(); } // corrupt file => defaults (AI off)
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var o = new JsonObject
        {
            ["welcomeSeen"] = WelcomeSeen,
            ["aiConsentDecided"] = AiConsentDecided,
            ["aiEnabled"] = AiEnabled,
            ["aiConsentAt"] = AiConsentAt?.ToString("o"),
            ["provider"] = Provider,
            ["model"] = Model,
            ["fallbackModel"] = FallbackModel,
            ["excludedApps"] = new JsonArray(ExcludedApps.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
            ["recentApps"] = new JsonArray(RecentApps.Select(a => (JsonNode?)new JsonObject
            {
                ["exe"] = a.Exe,
                ["name"] = a.Name,
                ["lastSeen"] = a.LastSeen.ToString("o"),
            }).ToArray()),
        };
        // Write, then rename: a crash mid-write can't leave a half-written file (which would reset everything to defaults).
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// The Gemini API key: GEMINI_API_KEY from the environment (development) wins; otherwise a key the user pasted on the consent
/// screen, stored encrypted with DPAPI (current user) in %LOCALAPPDATA%\huh\gemini.key. Never printed or logged.
/// </summary>
public static class KeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("huh/gemini-api-key/v1");

    public static string DefaultPath => HuhPaths.Key;

    public static bool FromEnvironment => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"));

    public static string? Get(string? path = null)
    {
        var env = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
    }

    public static void Save(string key, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = Encoding.UTF8.GetBytes(key.Trim());
        try { File.WriteAllBytes(path, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static bool HasStoredKey(string? path = null) => File.Exists(path ?? DefaultPath);

    /// <summary>Deletes the saved (DPAPI) key. A GEMINI_API_KEY environment variable is not affected.</summary>
    public static void Remove(string? path = null)
    {
        try { File.Delete(path ?? DefaultPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
