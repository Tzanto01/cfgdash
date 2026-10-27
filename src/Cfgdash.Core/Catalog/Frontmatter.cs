using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Cfgdash.Core.Catalog;

/// <summary><see cref="Fields"/> is null when there is no frontmatter or it is invalid (<see cref="Error"/> set). Values are unredacted.</summary>
internal sealed record FrontmatterResult(JsonObject? Fields, string Body, string? Error);

/// <summary>YAML frontmatter between a leading <c>---</c> line and the next <c>---</c> line. Mappings, sequences and scalars (as strings) only.</summary>
internal static class Frontmatter
{
    private const int MaxNodes = 10_000;
    private const int MaxDepth = 64;
    private const int MaxYamlChars = 64 * 1024;
    private const string TooLarge = "frontmatter is too large or too deeply nested";

    public static FrontmatterResult Parse(string text)
    {
        var start = text.StartsWith('﻿') ? 1 : 0;
        if (!TryLine(text, start, out var firstEnd, out var next) || text[start..firstEnd].TrimEnd() != "---")
        {
            return new FrontmatterResult(null, text, null);
        }

        var yamlStart = next;
        for (var position = next; TryLine(text, position, out var end, out var after); position = after)
        {
            if (text[position..end].TrimEnd() != "---")
            {
                continue;
            }

            var body = text[after..];
            var yaml = text[yamlStart..position];
            if (yaml.Length > MaxYamlChars)
            {
                return new FrontmatterResult(null, body, TooLarge);
            }

            try
            {
                if (NestingDepth(yaml) > MaxDepth)
                {
                    return new FrontmatterResult(null, body, TooLarge);
                }

                var stream = new YamlStream();
                stream.Load(new StringReader(yaml));
                if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
                {
                    return new FrontmatterResult(new JsonObject(), body, null);
                }

                var budget = MaxNodes;
                return Convert(stream.Documents[0].RootNode, 0, ref budget) is JsonObject fields
                    ? new FrontmatterResult(fields, body, null)
                    : new FrontmatterResult(null, body, "frontmatter is not a mapping");
            }
            catch (Exception e) when (e is YamlException or InvalidOperationException)
            {
                return new FrontmatterResult(null, body, "invalid YAML: " + e.Message.Split('\n')[0].Trim());
            }
            catch (InvalidDataException e)
            {
                return new FrontmatterResult(null, body, e.Message);
            }
        }

        return new FrontmatterResult(null, text, null);
    }

    private static bool TryLine(string text, int position, out int end, out int next)
    {
        if (position >= text.Length)
        {
            end = next = position;
            return false;
        }

        var newline = text.IndexOf('\n', position);
        end = newline < 0 ? text.Length : newline;
        next = newline < 0 ? text.Length : newline + 1;
        return true;
    }

    private static int NestingDepth(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        var depth = 0;
        while (depth <= MaxDepth && parser.MoveNext())
        {
            depth += parser.Current switch
            {
                MappingStart or SequenceStart => 1,
                MappingEnd or SequenceEnd => -1,
                _ => 0,
            };
        }

        return depth;
    }

    private static JsonNode? Convert(YamlNode node, int depth, ref int budget)
    {
        if (--budget < 0 || depth > MaxDepth)
        {
            throw new InvalidDataException(TooLarge);
        }

        switch (node)
        {
            case YamlMappingNode mapping:
                var obj = new JsonObject();
                foreach (var (key, value) in mapping.Children)
                {
                    obj[key is YamlScalarNode scalarKey ? scalarKey.Value ?? string.Empty : key.ToString()] = Convert(value, depth + 1, ref budget);
                }

                return obj;
            case YamlSequenceNode sequence:
                var array = new JsonArray();
                foreach (var item in sequence.Children)
                {
                    array.Add(Convert(item, depth + 1, ref budget));
                }

                return array;
            case YamlScalarNode scalar:
                return scalar.Value is null ? null : JsonValue.Create(scalar.Value);
            default:
                return null;
        }
    }
}
