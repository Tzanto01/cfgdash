using System.Text;
using Cfgdash.Core.Infrastructure;

namespace Cfgdash.Core.Hooks;

/// <summary>Script resolution and header purpose for <c>command</c> hook handlers (mirrors config/ci/parse-gate.ps1).</summary>
internal static class HookScripts
{
    private const int MaxPurpose = 300;
    private const int MaxHeaderBytes = 64 * 1024;

    private static readonly string[] ScriptExtensions = [".ps1", ".sh", ".py", ".js", ".mjs", ".cjs"];

    /// <summary>The <c>-File</c> argument, else the first quote-aware token ending in a script extension, else null.</summary>
    public static string? ExtractScript(string command)
    {
        var tokens = Tokens(command);
        var file = tokens.FindIndex(t => t.Equals("-File", StringComparison.OrdinalIgnoreCase));
        if (file >= 0 && file + 1 < tokens.Count)
        {
            return tokens[file + 1];
        }

        return tokens.FirstOrDefault(t => ScriptExtensions.Any(e => t.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Expands the config, project and plugin dir variables and a leading <c>~</c>; a relative result is resolved under <paramref name="targetDir"/>.</summary>
    public static string Expand(string path, string? configDir, string targetDir, string? pluginDir, string homeDir)
    {
        var result = path;
        if (configDir is not null)
        {
            result = result.Replace("${CLAUDE_CONFIG_DIR}", configDir, StringComparison.Ordinal)
                .Replace("%CLAUDE_CONFIG_DIR%", configDir, StringComparison.OrdinalIgnoreCase)
                .Replace("$CLAUDE_CONFIG_DIR", configDir, StringComparison.Ordinal);
        }

        result = result.Replace("${CLAUDE_PROJECT_DIR}", targetDir, StringComparison.Ordinal)
            .Replace("%CLAUDE_PROJECT_DIR%", targetDir, StringComparison.OrdinalIgnoreCase)
            .Replace("$CLAUDE_PROJECT_DIR", targetDir, StringComparison.Ordinal);
        if (pluginDir is not null)
        {
            result = result.Replace("${CLAUDE_PLUGIN_ROOT}", pluginDir, StringComparison.Ordinal);
        }

        result = PathUtil.ExpandHome(result.Replace('\\', '/'), homeDir);
        return PathUtil.Full(Path.IsPathRooted(result) ? result : Path.Combine(targetDir, result));
    }

    /// <summary>The first header comment paragraph of the script (unredacted), max 300 chars; null when none.</summary>
    public static string? Purpose(string scriptPath)
    {
        var text = HarnessFiles.ReadText(scriptPath, MaxHeaderBytes);
        if (text is null)
        {
            return null;
        }

        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).ToList();
        var extension = Path.GetExtension(scriptPath).ToLowerInvariant();
        var index = 0;
        if (extension == ".ps1")
        {
            while (index < lines.Count && (lines[index].Length == 0 || lines[index].StartsWith("#Requires", StringComparison.OrdinalIgnoreCase)))
            {
                index++;
            }

            if (index < lines.Count && lines[index].StartsWith("<#", StringComparison.Ordinal))
            {
                var block = new List<string>();
                lines[index] = lines[index][2..];
                for (; index < lines.Count; index++)
                {
                    var end = lines[index].IndexOf("#>", StringComparison.Ordinal);
                    block.Add((end >= 0 ? lines[index][..end] : lines[index]).Trim());
                    if (end >= 0)
                    {
                        break;
                    }
                }

                return Paragraph(block);
            }

            return Paragraph(Leading(lines, index, "#"));
        }

        if (index < lines.Count && lines[index].StartsWith("#!", StringComparison.Ordinal))
        {
            index++;
        }

        while (index < lines.Count && lines[index].Length == 0)
        {
            index++;
        }

        return extension switch
        {
            ".sh" or ".py" => Paragraph(Leading(lines, index, "#")),
            ".js" or ".mjs" or ".cjs" => Paragraph(Leading(lines, index, "//")),
            _ => null,
        };
    }

    private static List<string> Leading(List<string> lines, int index, string marker)
    {
        var result = new List<string>();
        for (; index < lines.Count && lines[index].StartsWith(marker, StringComparison.Ordinal); index++)
        {
            result.Add(lines[index].TrimStart(marker[0]).Trim());
        }

        return result;
    }

    private static string? Paragraph(List<string> lines)
    {
        var paragraph = lines.SkipWhile(l => l.Length == 0).TakeWhile(l => l.Length > 0).ToList();
        if (paragraph.Count == 0)
        {
            return null;
        }

        var joined = string.Join(' ', paragraph);
        return joined.Length > MaxPurpose ? joined[..MaxPurpose] : joined;
    }

    private static List<string> Tokens(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        char? quote = null;
        foreach (var c in command)
        {
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
