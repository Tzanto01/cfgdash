using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cfgdash.Core.Redaction;

/// <summary>Key rule and value patterns from docs/contracts/security-v1.md. Over-redaction is acceptable; under-redaction is a defect.</summary>
internal sealed partial class Redactor : IRedactor
{
    public const string Marker = "[redacted]";

    private static readonly HashSet<string> SecretParts = new(StringComparer.Ordinal)
    {
        "token", "tokens", "secret", "secrets", "password", "passwords", "passwd", "pwd", "pass", "auth", "authorization",
        "credential", "credentials", "cookie", "cookies", "key", "keys", "apikey", "headers", "header", "session", "sessions",
    };

    private static readonly string[] SecretSubstrings = ["apikey", "accesskey", "privatekey"];

    private static readonly Regex[] HighConfidence =
    [
        new("AKIA[0-9A-Z]{16}", RegexOptions.CultureInvariant),
        new("xox[baprs]-[0-9A-Za-z-]{10,}", RegexOptions.CultureInvariant),
        new("gh[pousr]_[A-Za-z0-9]{36,}", RegexOptions.CultureInvariant),
        new("github_pat_[A-Za-z0-9_]{60,}", RegexOptions.CultureInvariant),
        new("sk-ant-[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant),
        new("sk-proj-[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant),
        new("sk-[A-Za-z0-9]{32,}", RegexOptions.CultureInvariant),
        new("sk_live_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant),
        new("rk_live_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant),
        new("AIza[0-9A-Za-z_-]{35}", RegexOptions.CultureInvariant),
    ];

    public bool IsSecretName(string name)
    {
        var compact = SeparatorRegex().Replace(name, string.Empty).ToLowerInvariant();
        if (SecretSubstrings.Any(compact.Contains))
        {
            return true;
        }

        return WordBoundaryRegex().Split(name)
            .SelectMany(part => SeparatorRegex().Split(part))
            .Any(part => part.Length > 0 && SecretParts.Contains(part.ToLowerInvariant()));
    }

    public string RedactText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var text = PemRegex().Replace(value, Marker);
        foreach (var pattern in HighConfidence)
        {
            text = pattern.Replace(text, Marker);
        }

        text = AccountKeyRegex().Replace(text, "${1}" + Marker);
        text = UrlCredentialRegex().Replace(text, "${1}" + Marker + "${3}");
        text = PasswordLiteralRegex().Replace(text, m => IsExempt(m.Groups[2].Value) ? m.Value : ReplaceGroup(m, m.Groups[2]));
        text = BearerRegex().Replace(text, m => ReplaceGroup(m, m.Groups[1]));
        text = CliFlagRegex().Replace(text, m => ReplaceValue(m, m.Groups[2]));
        text = AssignmentRegex().Replace(text, m =>
            IsPasswordName(m.Groups[1].Value) && IsExempt(m.Groups[3].Value) ? m.Value : ReplaceValue(m, m.Groups[3]));
        return text;
    }

    public JsonNode? RedactJson(JsonNode? node, bool redactAllValues = false)
    {
        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, child) in obj)
                {
                    copy[key] = IsSecretName(key) ? JsonValue.Create(Marker) : RedactJson(child, redactAllValues);
                }

                return copy;
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(RedactJson(item, redactAllValues));
                }

                return items;
            case null:
                return redactAllValues ? JsonValue.Create(Marker) : null;
            default:
                if (redactAllValues)
                {
                    return JsonValue.Create(Marker);
                }

                return node is JsonValue value && value.TryGetValue<string>(out var text)
                    ? JsonValue.Create(RedactText(text))
                    : node.DeepClone();
        }
    }

    private static bool IsExempt(string value) =>
        value.StartsWith(Marker, StringComparison.Ordinal) || PasswordPlaceholderRegex().IsMatch(value);

    private static bool IsPasswordName(string name) =>
        name.Equals("password", StringComparison.OrdinalIgnoreCase) || name.Equals("pwd", StringComparison.OrdinalIgnoreCase);

    private static string ReplaceGroup(Match match, Group group) =>
        match.Value[..(group.Index - match.Index)] + Marker + match.Value[(group.Index - match.Index + group.Length)..];

    private static string ReplaceValue(Match match, Group group)
    {
        var value = group.Value;
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
        {
            var prefix = match.Value[..(group.Index - match.Index + 1)];
            return prefix + Marker + match.Value[(group.Index - match.Index + group.Length - 1)..];
        }

        return ReplaceGroup(match, group);
    }

    [GeneratedRegex("[_\\-.\\s]+")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordBoundaryRegex();

    [GeneratedRegex("-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\\s\\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|\\z)")]
    private static partial Regex PemRegex();

    [GeneratedRegex("(AccountKey=)[A-Za-z0-9+/]{50,}={0,2}")]
    private static partial Regex AccountKeyRegex();

    [GeneratedRegex("\\b([a-z][a-z0-9+.-]*://[^\\s/@]*:)([^\\s/@]+)(@)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentialRegex();

    [GeneratedRegex("\\b(password|pwd)\\s*=\\s*([^;,'\"\\s]{6,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordLiteralRegex();

    [GeneratedRegex("^(your|changeme|placeholder|example|test|dummy|sample|xxx+|<|\\$\\{|%|\\{)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordPlaceholderRegex();

    [GeneratedRegex("\\bBearer\\s+([^\\s\"',;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(--(?:token|api-key|key|secret|password)(?:=|\\s+))(\"[^\"]*\"|'[^']*'|[^\\s\"']+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CliFlagRegex();

    [GeneratedRegex("\\b([A-Za-z0-9_.-]*(?:token|secret|passw(?:or)?d|pwd|api[-_]?key|access[-_]?key|private[-_]?key|credential|cookie)[A-Za-z0-9_.-]*)([\"']?\\s*[=:]\\s*)(\"[^\"]*\"|'[^']*'|[^\\s,;\"']+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentRegex();
}
