using System.Text.Json;
using System.Text.Json.Nodes;

namespace Huh.Core.AI;

/// <summary>
/// On-disk cache of explanations keyed by a hash of the (redacted) prompt, so the same error never costs free-tier quota twice.
/// Stores only the model's sanitized answer; nothing personal (the prompt was redacted before hashing).
/// </summary>
public sealed class ExplanationCache(string directory, TimeSpan? ttl = null)
{
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromDays(30);

    public static string DefaultDirectory => Settings.HuhPaths.AiCache;

    /// <summary>Number of saved answers (shown in Settings).</summary>
    public int Count => Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Length : 0;

    /// <summary>Deletes every saved answer. Returns how many were removed.</summary>
    public int Clear()
    {
        int n = 0;
        if (!Directory.Exists(directory)) return 0;
        foreach (var f in Directory.GetFiles(directory, "*.json"))
            try { File.Delete(f); n++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return n;
    }

    public Explanation? TryGet(string key, out string? model)
    {
        model = null;
        var path = PathFor(key);
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            if (DateTime.UtcNow - fi.LastWriteTimeUtc > _ttl) { fi.Delete(); return null; }
            var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            model = (string?)o["model"];
            return Explanation.Sanitized((string?)o["cause"], o["fix_steps"]?.AsArray().Select(x => (string?)x), (string?)o["action_id"],
                (string?)o["action_param"], (double?)o["confidence"] ?? 0);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    public void Put(string key, Explanation e, string? model)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var o = new JsonObject
            {
                ["model"] = model,
                ["cause"] = e.Cause,
                ["fix_steps"] = new JsonArray(e.FixSteps.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["action_id"] = e.ActionId,
                ["action_param"] = e.ActionParam,
                ["confidence"] = e.Confidence,
            };
            var tmp = PathFor(key) + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, PathFor(key), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* cache is best-effort */ }
    }

    private string PathFor(string key) => Path.Combine(directory, key + ".json");
}
