using System.Diagnostics;
using Huh.Core.Actions;
using Huh.Core.Facts;
using Huh.Core.Privacy;
using Huh.Core.Settings;

namespace Huh.Core.AI;

/// <summary>
/// The AI step: consent gate -> redact + build prompt -> cache -> provider -> sanitize -> validate the suggested action.
/// Never throws; every failure becomes an <see cref="AiOutcome"/> the bubble can show next to the deterministic (M3) info.
/// </summary>
public sealed class AiExplainService(IExplainer explainer, ExplanationCache cache, Func<HuhSettings> settings, Redactor? redactor = null)
{
    private readonly Redactor _redactor = redactor ?? new Redactor();

    public Redactor Redactor => _redactor;
    public string ModelName => explainer.Model;

    public async Task<AiOutcome> ExplainAsync(DialogFacts facts, CancellationToken ct = default)
    {
        if (!settings().AiEnabled) return AiOutcome.Disabled; // nothing leaves the machine without consent
        if (facts.Dialog.BlockedByUipi) return AiOutcome.Disabled; // nothing to explain: the text couldn't be read

        var sw = Stopwatch.StartNew();
        try
        {
            var req = Prompt.Build(facts, _redactor);
            if (cache.TryGet(req.CacheKey, out var cachedModel) is { } cached)
                return Finish(new AiOutcome(AiStatus.Ok, cached, FromCache: true, Model: cachedModel), facts, sw);

            var outcome = await explainer.ExplainAsync(req, ct).ConfigureAwait(false);
            if (outcome.Status == AiStatus.Ok && outcome.Explanation is { } e) cache.Put(req.CacheKey, e, outcome.Model);
            return Finish(outcome, facts, sw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AiOutcome(AiStatus.Unavailable, Reason: UnavailableReason.Service, Detail: ex.GetType().Name);
        }
    }

    private AiOutcome Finish(AiOutcome o, DialogFacts facts, Stopwatch sw)
    {
        if (o.Status != AiStatus.Ok || o.Explanation is null) return o with { LatencyMs = o.LatencyMs > 0 ? o.LatencyMs : sw.Elapsed.TotalMilliseconds };
        // A dialog that addresses AI tools is attempting prompt injection: whatever the model said, offer no action.
        bool injection = InjectionDetector.LooksLikeInjection(facts.Dialog.Title + "\n" + facts.Dialog.Message);
        var decision = injection
            ? new ActionDecision(null, "the dialog text contains instructions aimed at AI tools, so huh offers no action")
            : FixActions.Validate(o.Explanation.ActionId, o.Explanation.ActionParam, facts, _redactor);
        return o with { Action = decision, SuspectedInjection = injection, LatencyMs = o.FromCache ? sw.Elapsed.TotalMilliseconds : o.LatencyMs };
    }
}
