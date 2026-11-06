using System.Text.RegularExpressions;

namespace Huh.Core.AI;

/// <summary>
/// Deterministic check for dialog text that tries to talk to an AI ("ignore previous instructions", "note to AI assistant",
/// "action_id=..."). Real error dialogs never address AI tools, so a match means the dialog is attempting prompt injection:
/// huh then drops any suggested action and warns the user, whatever the model answered.
/// </summary>
public static partial class InjectionDetector
{
    [GeneratedRegex(@"(ignore|disregard|forget)\s+(all\s+|any\s+)?(the\s+)?(previous|prior|above|earlier|your)\s+(instructions|prompts?|rules)" +
                    @"|\b(note|message|instructions?)\s+(to|for)\s+(the\s+)?(ai|assistant|llm|chatbot|model|gemini|gpt|claude)\b" +
                    @"|\b(ai|llm)\s+assistant\b|\bsystem\s+prompt\b|\byou\s+are\s+(now\s+)?(an?\s+)?(ai|assistant|chatbot)\b" +
                    @"|\baction_id\b|\baction_param\b|\b(rerun|re-run)_as_admin\b|</?(dialog_data|verified_facts)>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Patterns();

    public static bool LooksLikeInjection(string? text) => !string.IsNullOrEmpty(text) && Patterns().IsMatch(text);
}
