using Cfgdash.Core.Collectors;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class ToolConfigTests
{
    [Fact]
    public void Jsonc_with_a_duplicate_key_is_an_error_instead_of_an_exception()
    {
        using var tree = new TempTree();
        var path = tree.Write("opencode.json", """{ "model": "a", "model": "b" }""");

        Assert.False(ToolConfig.TryReadJsonc(path, out var root, out var error));
        Assert.Null(root);
        Assert.StartsWith("invalid JSON", error);
    }

    [Theory]
    [InlineData("password = q7Zx9vLm\n")]
    [InlineData("model = \"a\"\nq7Zx9vLm = = 1\n")]
    public void Toml_errors_report_the_position_and_never_the_source_text(string toml)
    {
        using var tree = new TempTree();
        var path = tree.Write("config.toml", toml);

        Assert.False(ToolConfig.TryReadToml(path, out _, out var error));
        Assert.StartsWith("invalid TOML at line ", error);
        Assert.DoesNotContain("q7Zx9vLm", error, StringComparison.Ordinal);
    }
}
