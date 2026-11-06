using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Huh.Core.AI;

/// <summary>
/// Google Gemini via the native REST endpoint:
///   POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent, header x-goog-api-key.
/// Works with both key formats (AIza... and the newer AQ. auth keys). The key is read per call from a provider, only ever
/// placed in that header, and never logged, cached or included in exceptions.
/// </summary>
public sealed class GeminiExplainer : IExplainer
{
    public const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/";

    private readonly HttpClient _http;
    private readonly Func<string?> _key;
    private readonly Func<IReadOnlyList<string>> _models;
    private readonly TimeSpan _timeout;

    public string Model => _models() is { Count: > 0 } m ? m[0] : "?";

    /// <param name="models">Primary first; later ones are tried on 429 (separate quota) or 404 (model gone).</param>
    public GeminiExplainer(HttpClient http, Func<string?> keyProvider, IReadOnlyList<string> models, TimeSpan? timeout = null)
    {
        _http = http;
        _key = keyProvider;
        var fixedModels = models.Count > 0 ? models.ToArray() : throw new ArgumentException("no model", nameof(models));
        _models = () => fixedModels;
        _timeout = timeout ?? TimeSpan.FromSeconds(8); // per model; primary + fallback stays well under 20 s
    }

    /// <param name="models">Read on every call, so a model changed in Settings applies to the next request.</param>
    public GeminiExplainer(HttpClient http, Func<string?> keyProvider, Func<IReadOnlyList<string>> models, TimeSpan? timeout = null)
    {
        _http = http;
        _key = keyProvider;
        _models = models;
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    public async Task<AiOutcome> ExplainAsync(ExplainRequest request, CancellationToken ct)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key)) return new AiOutcome(AiStatus.Unavailable, Reason: UnavailableReason.NoKey);

        var sw = Stopwatch.StartNew();
        AiOutcome last = new(AiStatus.Unavailable, Reason: UnavailableReason.Service);
        var models = _models().Where(Settings.HuhSettings.IsValidModelName).ToArray();
        if (models.Length == 0) return new AiOutcome(AiStatus.Unavailable, Reason: UnavailableReason.ModelUnavailable, Detail: "no valid model name");
        foreach (var model in models)
        {
            last = await CallAsync(model, key, request, ct).ConfigureAwait(false);
            last = last with { LatencyMs = sw.Elapsed.TotalMilliseconds, Model = model };
            // Worth retrying with the next model: quota (separate bucket), missing model, overloaded model (503), timeout.
            if (last.Status == AiStatus.Ok || last.Reason is not (UnavailableReason.Quota or UnavailableReason.ModelUnavailable or UnavailableReason.Busy or UnavailableReason.Timeout)) break;
        }
        return last;
    }

    private async Task<AiOutcome> CallAsync(string model, string key, ExplainRequest request, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var msg = new HttpRequestMessage(HttpMethod.Post, UrlFor(model))
        {
            Content = new StringContent(BuildBody(request, model), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("x-goog-api-key", key);
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(msg, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Unavailable(UnavailableReason.Timeout, "timeout"); }
        catch (HttpRequestException ex) { return Unavailable(UnavailableReason.Network, ex.HttpRequestError.ToString()); }

        using (resp)
        {
            string body;
            try { body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Unavailable(UnavailableReason.Timeout, "timeout reading body"); }

            if (resp.StatusCode == HttpStatusCode.OK) return Parse(body);
            var (status, text) = ErrorInfo(body);
            return resp.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => Unavailable(UnavailableReason.Quota, status ?? "RESOURCE_EXHAUSTED"),
                HttpStatusCode.NotFound => Unavailable(UnavailableReason.ModelUnavailable, $"{model}: {status}"),
                // ACCESS_TOKEN_TYPE_UNSUPPORTED can mean a deprecated model as well as a key problem: report the status as-is.
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Unavailable(UnavailableReason.Auth, $"{(int)resp.StatusCode} {status} ({model})"),
                HttpStatusCode.BadRequest when text?.Contains("API key", StringComparison.OrdinalIgnoreCase) == true
                    => Unavailable(UnavailableReason.Auth, $"400 {status}"),
                HttpStatusCode.BadRequest => Unavailable(UnavailableReason.BadResponse, $"400 {status}: {Trunc(text, 160)}"),
                HttpStatusCode.ServiceUnavailable => Unavailable(UnavailableReason.Busy, $"503 {status} ({model})"),
                _ when (int)resp.StatusCode >= 500 => Unavailable(UnavailableReason.Service, $"{(int)resp.StatusCode} {status} ({model})"),
                _ => Unavailable(UnavailableReason.Service, $"{(int)resp.StatusCode} {status}"),
            };
        }
    }

    public static string UrlFor(string model) => Endpoint + Uri.EscapeDataString(model) + ":generateContent";

    /// <summary>The exact request body huh sends (also used by the Settings privacy preview, so the preview can't drift).</summary>
    public static string BuildBody(ExplainRequest request, string model)
    {
        var generation = new JsonObject
        {
            ["responseMimeType"] = "application/json",
            ["responseJsonSchema"] = JsonNode.Parse(Prompt.ResponseJsonSchema),
            ["temperature"] = 0.2,
            ["maxOutputTokens"] = 2048, // includes thinking tokens on Gemini 3.x
        };
        // Gemini 3+ supports thinking levels; LOW keeps latency down for this short task (earlier models reject the field).
        if (model.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase))
            generation["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "LOW" };

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = Prompt.SystemInstruction }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = request.UserContent }),
            }),
            ["generationConfig"] = generation,
        };
        return body.ToJsonString();
    }

    /// <summary>Parses a generateContent response (also used for saved fixtures).</summary>
    internal static AiOutcome Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("promptFeedback", out var pf) && pf.TryGetProperty("blockReason", out var br))
                return Unavailable(UnavailableReason.Blocked, br.GetString());
            if (!root.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0)
                return Unavailable(UnavailableReason.BadResponse, "no candidates");
            var cand = cands[0];
            string finish = cand.TryGetProperty("finishReason", out var fr) ? fr.GetString() ?? "" : "";
            if (finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST") return Unavailable(UnavailableReason.Blocked, finish);

            var text = new StringBuilder();
            if (cand.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
                foreach (var p in parts.EnumerateArray())
                {
                    if (p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True) continue; // skip thought summaries
                    if (p.TryGetProperty("text", out var t)) text.Append(t.GetString());
                }
            if (text.Length == 0) return Unavailable(UnavailableReason.BadResponse, finish == "MAX_TOKENS" ? "ran out of tokens" : "empty");

            using var ej = JsonDocument.Parse(text.ToString());
            var e = ej.RootElement;
            var expl = Explanation.Sanitized(
                Str(e, "cause"),
                e.TryGetProperty("fix_steps", out var steps) && steps.ValueKind == JsonValueKind.Array ? steps.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null) : null,
                Str(e, "action_id") ?? "none",
                Str(e, "action_param"),
                e.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0);
            if (expl.Cause.Length == 0) return Unavailable(UnavailableReason.BadResponse, "no cause");
            return new AiOutcome(AiStatus.Ok, expl);
        }
        catch (JsonException) { return Unavailable(UnavailableReason.BadResponse, "invalid JSON"); }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static (string? Status, string? Message) ErrorInfo(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                string? status = err.TryGetProperty("status", out var s) ? s.GetString() : null;
                string? message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                // A detail reason such as ACCESS_TOKEN_TYPE_UNSUPPORTED is more useful than the generic status.
                if (err.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                    foreach (var d in details.EnumerateArray())
                        if (d.TryGetProperty("reason", out var r) && r.GetString() is { Length: > 0 } reason) { status = $"{status}/{reason}"; break; }
                return (status, message);
            }
        }
        catch (JsonException) { }
        return (null, null);
    }

    private static AiOutcome Unavailable(UnavailableReason r, string? detail) => new(AiStatus.Unavailable, Reason: r, Detail: detail);
    private static string? Trunc(string? s, int n) => s is null ? null : s.Length <= n ? s : s[..n] + "…";
}
