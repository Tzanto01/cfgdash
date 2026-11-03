using Cfgdash.Core.Assistant;

namespace Cfgdash.Core.Tests.Assistant;

public sealed class CliMessagesTests
{
    [Theory]
    [InlineData("error_max_budget_usd", null, "cost cap")]
    [InlineData("error_max_turns", null, "turn limit")]
    [InlineData("error_during_execution", "API Error: 529 Overloaded", "overloaded")]
    [InlineData("success", "Claude AI usage limit reached|1762160400", "usage limit")]
    [InlineData("success", "Prompt is too long", "too long")]
    [InlineData("success", "prompt is too long: 215290 tokens > 200000 maximum", "too long")]
    [InlineData("error_during_execution", "request req_4011 failed", "failed in the Claude CLI")]
    [InlineData("error_during_execution", "request req_011CT529xyz failed", "failed in the Claude CLI")]
    [InlineData("error_during_execution", "API Error: 401 Unauthorized", "not logged in")]
    [InlineData("success", "Invalid API key · Please run /login", "not logged in")]
    [InlineData("error_during_execution", "something new", "failed in the Claude CLI")]
    public void A_failed_result_maps_to_a_cfgdash_message(string subtype, string? text, string expected)
    {
        var (message, _) = CliMessages.ForResult(subtype, text);

        Assert.Contains(expected, message, StringComparison.OrdinalIgnoreCase);
        if (text is not null)
        {
            Assert.DoesNotContain(text, message);
        }
    }

    [Fact]
    public void The_detail_keeps_the_raw_text_redacted_and_capped()
    {
        var (_, detail) = CliMessages.ForResult("error_during_execution", "token ghp_" + new string('z', 36) + " " + new string('x', 400));

        Assert.StartsWith("error_during_execution: token [redacted]", detail);
        Assert.DoesNotContain("zzzz", detail);
        Assert.Equal(300, detail.Length);
    }
}
