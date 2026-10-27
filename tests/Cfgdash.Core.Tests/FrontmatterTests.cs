using System.Text.Json.Nodes;
using Cfgdash.Core.Catalog;

namespace Cfgdash.Core.Tests;

public sealed class FrontmatterTests
{
    [Fact]
    public void Deeply_nested_flow_frontmatter_is_refused_without_parsing()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = Frontmatter.Parse("---\nx: " + new string('[', 20_000) + new string(']', 20_000) + "\n---\nbody");

        Assert.Null(result.Fields);
        Assert.Equal("frontmatter is too large or too deeply nested", result.Error);
        Assert.Equal("body", result.Body);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), watch.Elapsed.ToString());
    }

    [Fact]
    public void Multi_line_scalars_lists_and_nested_hooks_are_parsed()
    {
        var result = Frontmatter.Parse("""
            ---
            name: reviewer
            description: |
              First line.
              Second line.
            summary: >
              folded one
              folded two
            plain: a plain scalar
              continued here
            tools:
              - Read
              - Grep
            hooks:
              PreToolUse:
                - matcher: Bash
                  hooks:
                    - type: command
                      command: ./guard.sh
            ---
            Body text.
            """);

        Assert.Null(result.Error);
        var fields = result.Fields!;
        Assert.Equal("reviewer", fields["name"]!.GetValue<string>());
        Assert.Equal("First line.\nSecond line.\n", fields["description"]!.GetValue<string>());
        Assert.Equal("folded one folded two\n", fields["summary"]!.GetValue<string>());
        Assert.Equal("a plain scalar continued here", fields["plain"]!.GetValue<string>());
        Assert.Equal("""["Read","Grep"]""", fields["tools"]!.ToJsonString());
        Assert.Equal("./guard.sh", fields["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("Body text.", result.Body);
    }

    [Theory]
    [InlineData("name: [unclosed")]
    [InlineData("a: [b\n- c")]
    [InlineData("a: 1\na: 2")]
    public void Invalid_yaml_reports_an_error_without_fields(string yaml)
    {
        var result = Frontmatter.Parse($"---\n{yaml}\n---\nbody");

        Assert.Null(result.Fields);
        Assert.StartsWith("invalid YAML", result.Error);
    }

    [Theory]
    [InlineData("# Title\n---\nname: x\n---\n")]
    [InlineData("---\nname: x\nno closing line\n")]
    public void Text_without_a_complete_leading_block_has_no_frontmatter(string text)
    {
        var result = Frontmatter.Parse(text);

        Assert.Null(result.Fields);
        Assert.Null(result.Error);
        Assert.Equal(text, result.Body);
    }

    [Fact]
    public void Byte_order_mark_and_crlf_are_accepted()
    {
        var result = Frontmatter.Parse("﻿---\r\nname: x\r\n---\r\nbody");

        Assert.Equal("x", result.Fields!["name"]!.GetValue<string>());
    }
}
