using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Huh.Core.Settings;

/// <summary>User settings in %LOCALAPPDATA%\huh\settings.json. AI is OFF until the user accepts on the consent screen.</summary>
public sealed class HuhSettings
{
    public bool AiConsentDecided { get; set; }
    public bool AiEnabled { get; set; }
    public DateTimeOffset? AiConsentAt { get; set; }
    public string Model { get; set; } = "gemini-3.8-flash";
    public string FallbackModel { get; set; } = "gemini-3.5-flash-lite";

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "huh", "settings.json");

    public static HuhSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new HuhSettings();
            var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            return new HuhSettings
            {
                AiConsentDecided = (bool?)o["aiConsentDecided"] ?? false,
                AiEnabled = (bool?)o["aiEnabled"] ?? false,
                AiConsentAt = DateTimeOffset.TryParse((string?)o["aiConsentAt"], out var at) ? at : null,
                Model = (string?)o["model"] is { Length: > 0 } m ? m : "gemini-3.8-flash",
                FallbackModel = (string?)o["fallbackModel"] is { Length: > 0 } fm ? fm : "gemini-3.5-flash-lite",
            };
        }
        catch { return new HuhSettings(); } // corrupt file => defaults (AI off)
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var o = new JsonObject
        {
            ["aiConsentDecided"] = AiConsentDecided,
            ["aiEnabled"] = AiEnabled,
            ["aiConsentAt"] = AiConsentAt?.ToString("o"),
            ["model"] = Model,
            ["fallbackModel"] = FallbackModel,
        };
        File.WriteAllText(path, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// The Gemini API key: GEMINI_API_KEY from the environment (development) wins; otherwise a key the user pasted on the consent
/// screen, stored encrypted with DPAPI (current user) in %LOCALAPPDATA%\huh\gemini.key. Never printed or logged.
/// </summary>
public static class KeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("huh/gemini-api-key/v1");

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "huh", "gemini.key");

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
}
