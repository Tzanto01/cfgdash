using Cfgdash.Core.Assistant;
using Cfgdash.Core.Redaction;

namespace Cfgdash.Core.Tests.Assistant;

public sealed class LineRedactorTests
{
    private static readonly string Token = "gh" + "p_" + new string('z', 36);

    [Fact]
    public void A_secret_split_across_two_pushes_is_redacted_in_the_emitted_line()
    {
        var redactor = new LineRedactor();

        var first = redactor.Push("my token " + Token[..20]);
        var second = redactor.Push(Token[20..] + " ok\nnext");

        Assert.Equal(string.Empty, first);
        Assert.Equal("my token [redacted] ok\n", second);
        Assert.DoesNotContain("zzzzzzzz", second);
    }

    [Fact]
    public void Nothing_is_emitted_before_a_newline_and_flush_releases_the_rest_redacted()
    {
        var redactor = new LineRedactor();

        Assert.Equal(string.Empty, redactor.Push("hello "));
        Assert.Equal(string.Empty, redactor.Push("key " + Token));
        var rest = redactor.Flush();

        Assert.Equal("hello key [redacted]", rest);
        Assert.Equal(string.Empty, redactor.Flush());
    }

    [Fact]
    public void Complete_lines_are_released_and_the_partial_tail_waits()
    {
        var redactor = new LineRedactor();

        Assert.Equal("one\ntwo\n", redactor.Push("one\ntwo\nthr"));
        Assert.Equal("three\n", redactor.Push("ee\n"));
    }

    [Fact]
    public void A_private_key_block_is_redacted_line_by_line_across_pushes()
    {
        var redactor = new LineRedactor();

        var output = redactor.Push("before\n-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkq\nQWxwaGFCZXRh");
        output += redactor.Push("Cg==\n-----END PRIVATE KEY-----\nafter\n");

        var lines = output.TrimEnd('\n').Split('\n');
        Assert.Equal("before", lines[0]);
        Assert.Equal("after", lines[^1]);
        Assert.All(lines[1..^1], l => Assert.Equal(Redactor.Marker, l));
        Assert.DoesNotContain("MIIEvQ", output);
        Assert.DoesNotContain("QWxwaGFCZXRh", output);
        Assert.DoesNotContain("Cg==", output);
    }
}
