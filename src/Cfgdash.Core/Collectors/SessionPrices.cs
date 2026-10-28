using System.Text.RegularExpressions;

namespace Cfgdash.Core.Collectors;

/// <summary>
/// Estimated Anthropic list prices in USD per million tokens, as of <see cref="AsOf"/>. An estimate only: no discounts,
/// batch or regional pricing. 5-minute cache writes cost 1.25x and 1-hour cache writes 2x the input price; fast mode costs 2x.
/// An unknown model has no price.
/// </summary>
internal static partial class SessionPrices
{
    public const string AsOf = "2026-10-06";

    /// <summary>Haiku 5.5 bills prompts above this many tokens at its long-prompt rates.</summary>
    private const long LongPromptTokens = 100_000;

    private static readonly (string Prefix, ModelPrice Price)[] Table =
    [
        ("claude-fable-5-1", new(10, 50, 0.25)),
        ("claude-mythos-5-1", new(10, 50, 0.25)),
        ("claude-fable-5", new(10, 50, 1)),
        ("claude-mythos-5", new(10, 50, 1)),
        ("claude-opus-5-5", new(4, 20, 0.2)),
        ("claude-opus-5", new(5, 25, 0.5)),
        ("claude-opus-4-8", new(5, 25, 0.5)),
        ("claude-opus-4-7", new(5, 25, 0.5)),
        ("claude-opus-4-6", new(5, 25, 0.5)),
        ("claude-sonnet-5-5", new(2, 10, 0.2)),
        ("claude-sonnet-5", new(2, 10, 0.2)),
        ("claude-sonnet-4-6", new(3, 15, 0.3)),
        ("claude-haiku-5-5", new(0.1, 0.5, 0.01, new ModelPrice(0.5, 2.5, 0.05))),
        ("claude-haiku-4-5", new(1, 5, 0.1)),
    ];

    /// <summary>USD for one assistant message, or null when the model has no price.</summary>
    public static double? Cost(MessageUsage usage)
    {
        if (usage.Model is not { } model || Find(model) is not { } price)
        {
            return null;
        }

        if (price.LongPrompt is { } longPrompt && usage.Input + usage.CacheRead + usage.CacheWrite > LongPromptTokens)
        {
            price = longPrompt;
        }

        var write5m = Math.Max(usage.CacheWrite - usage.CacheWrite1h, 0);
        var dollars = (usage.Input * price.Input
            + usage.Output * price.Output
            + usage.CacheRead * price.CacheRead
            + write5m * price.Input * 1.25
            + usage.CacheWrite1h * price.Input * 2) / 1_000_000;
        return usage.Fast ? dollars * 2 : dollars;
    }

    private static ModelPrice? Find(string model)
    {
        foreach (var (prefix, price) in Table)
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Suffix().IsMatch(model[prefix.Length..]))
            {
                return price;
            }
        }

        return null;
    }

    /// <summary>Nothing, a context marker (<c>[1m]</c>), a platform version (<c>@...</c>) or a date snapshot (<c>-20260101</c>).</summary>
    [GeneratedRegex(@"^(?:$|\[|@|-\d{8}(?!\d))", RegexOptions.CultureInvariant)]
    private static partial Regex Suffix();

    private sealed record ModelPrice(double Input, double Output, double CacheRead, ModelPrice? LongPrompt = null);
}

/// <summary>Usage of one assistant message. <c>CacheWrite1h</c> is the 1-hour part of <c>CacheWrite</c>.</summary>
internal readonly record struct MessageUsage(string? Model, long Input, long Output, long CacheRead, long CacheWrite, long CacheWrite1h, bool Fast)
{
    public long Total => Input + Output + CacheRead + CacheWrite;

    public MessageUsage Merge(MessageUsage other) => new(
        Model ?? other.Model,
        Math.Max(Input, other.Input),
        Math.Max(Output, other.Output),
        Math.Max(CacheRead, other.CacheRead),
        Math.Max(CacheWrite, other.CacheWrite),
        Math.Max(CacheWrite1h, other.CacheWrite1h),
        Fast || other.Fast);
}
