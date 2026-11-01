using System.Text;

namespace Cfgdash.Core.Edits;

/// <summary>Unified diff with three lines of context: common prefix and suffix trimmed, LCS over the rest, whole-block replace above a cell cap.</summary>
internal static class UnifiedDiff
{
    public const string ReformatMarker = "# cfgdash: file will be reformatted";

    private const int Context = 3;
    private const long MaxCells = 4_000_000;

    /// <summary><paramref name="before"/> is null for a new file (<c>--- /dev/null</c>).</summary>
    public static string Create(string? before, string after, string path, bool reformatted)
    {
        var text = new StringBuilder();
        if (reformatted)
        {
            text.Append(ReformatMarker).Append('\n');
        }

        var name = path.TrimStart('/');
        text.Append(before is null ? "--- /dev/null" : "--- a/" + name).Append('\n');
        text.Append("+++ b/").Append(name).Append('\n');

        var ops = Ops(Lines(before), Lines(after));
        var oldPos = new int[ops.Count + 1];
        var newPos = new int[ops.Count + 1];
        for (var k = 0; k < ops.Count; k++)
        {
            oldPos[k + 1] = oldPos[k] + (ops[k].Kind == '+' ? 0 : 1);
            newPos[k + 1] = newPos[k] + (ops[k].Kind == '-' ? 0 : 1);
        }

        var changes = Enumerable.Range(0, ops.Count).Where(k => ops[k].Kind != ' ').ToList();
        for (var i = 0; i < changes.Count;)
        {
            var j = i;
            while (j + 1 < changes.Count && changes[j + 1] - changes[j] - 1 <= 2 * Context)
            {
                j++;
            }

            var start = Math.Max(0, changes[i] - Context);
            var end = Math.Min(ops.Count, changes[j] + Context + 1);
            text.Append("@@ -").Append(Range(oldPos[start], oldPos[end] - oldPos[start]))
                .Append(" +").Append(Range(newPos[start], newPos[end] - newPos[start])).Append(" @@\n");
            for (var k = start; k < end; k++)
            {
                text.Append(ops[k].Kind).Append(ops[k].Line).Append('\n');
            }

            i = j + 1;
        }

        return text.ToString();
    }

    private static string Range(int position, int count) => count switch
    {
        0 => $"{position},0",
        1 => $"{position + 1}",
        _ => $"{position + 1},{count}",
    };

    private static string[] Lines(string? text) =>
        string.IsNullOrEmpty(text) ? [] : (text.EndsWith('\n') ? text[..^1] : text).Split('\n');

    private static List<(char Kind, string Line)> Ops(string[] a, string[] b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
        {
            suffix++;
        }

        var ops = new List<(char Kind, string Line)>(a.Take(prefix).Select(l => (' ', l)));
        var n = a.Length - prefix - suffix;
        var m = b.Length - prefix - suffix;
        if ((long)(n + 1) * (m + 1) > MaxCells)
        {
            ops.AddRange(a.Skip(prefix).Take(n).Select(l => ('-', l)));
            ops.AddRange(b.Skip(prefix).Take(m).Select(l => ('+', l)));
        }
        else
        {
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
            {
                for (var j = m - 1; j >= 0; j--)
                {
                    lcs[i, j] = a[prefix + i] == b[prefix + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[prefix + x] == b[prefix + y])
                {
                    ops.Add((' ', a[prefix + x++]));
                    y++;
                }
                else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1]))
                {
                    ops.Add(('-', a[prefix + x++]));
                }
                else
                {
                    ops.Add(('+', b[prefix + y++]));
                }
            }
        }

        ops.AddRange(a.Skip(a.Length - suffix).Select(l => (' ', l)));
        return ops;
    }
}
