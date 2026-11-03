namespace Cfgdash.Core.Assistant;

/// <summary>Maps a failed <c>claude</c> result to a cfgdash message for the panel (assistant-v1 rev 3); the raw text goes only to a redacted detail.</summary>
public static class CliMessages
{
    private const int MaxDetailChars = 300;

    /// <summary>The cfgdash message for a failed result with <paramref name="subtype"/> and <paramref name="text"/>, and their redacted, capped detail.</summary>
    public static (string Message, string Detail) ForResult(string? subtype, string? text)
    {
        var raw = string.Join(": ", new[] { subtype, text }.Where(t => !string.IsNullOrWhiteSpace(t)));
        var detail = AssistantRedaction.Text(raw.Length > 0 ? raw : "error");
        if (detail.Length > MaxDetailChars)
        {
            detail = detail[..(char.IsHighSurrogate(detail[MaxDetailChars - 1]) ? MaxDetailChars - 1 : MaxDetailChars)];
        }

        bool Has(string part) => raw.Contains(part, StringComparison.OrdinalIgnoreCase);
        var message = subtype switch
        {
            "error_max_budget_usd" => "The session reached its cost cap. Raise or turn off the cap in cfgdash settings, then start a new session.",
            "error_max_turns" => "The assistant reached the turn limit of the Claude CLI. Send a shorter request.",
            _ when Has("usage limit") || Has("rate limit") || Has("rate_limit") => "Claude usage limit reached. Try again later.",
            _ when Has("overloaded") || Has("529") => "Claude is overloaded right now. Try again in a moment.",
            _ when Has("prompt is too long") || Has("context window") => "The conversation is too long for the model. End the session and start a new one.",
            _ when Has("credit balance") || Has("billing") => "The Claude account has no credit left for this request.",
            _ when Has("/login") || Has("not logged in") || Has("authentication") || Has("invalid api key") || Has("401") => "The Claude CLI is not logged in. Run claude, log in, then try again.",
            _ => "The assistant turn failed in the Claude CLI.",
        };
        return (message, detail);
    }
}
