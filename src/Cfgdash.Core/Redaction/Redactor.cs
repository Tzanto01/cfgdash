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

    private const RegexOptions Linear = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
    private const RegexOptions LinearIgnoreCase = Linear | RegexOptions.IgnoreCase;

    private static readonly Regex[] HighConfidence =
    [
        new("AKIA[0-9A-Z]{16}", Linear),
        new("xox[baprs]-[0-9A-Za-z-]{10,}", Linear),
        new("gh[pousr]_[A-Za-z0-9]{36,}", Linear),
        new("github_pat_[A-Za-z0-9_]{60,}", Linear),
        new("sk-ant-[A-Za-z0-9_-]{20,}", Linear),
        new("sk-proj-[A-Za-z0-9_-]{20,}", Linear),
        new("sk-[A-Za-z0-9]{32,}", Linear),
        new("sk_live_[A-Za-z0-9]{20,}", Linear),
        new("rk_live_[A-Za-z0-9]{20,}", Linear),
        new("AIza[0-9A-Za-z_-]{35}", Linear),
    ];

    private static readonly Regex SeparatorRegex = new("[_\\-.\\s]+", Linear);

    private static readonly Regex PemRegex = new(
        "-----BEGIN [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----[\\s\\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----|\\z)", Linear);

    private static readonly Regex AccountKeyRegex = new("(AccountKey=)[A-Za-z0-9+/]{50,}={0,2}", Linear);

    private static readonly Regex UrlCredentialRegex = new("\\b([a-z][a-z0-9+.-]*://[^\\s/@]*:)([^\\s/@]+)(@)", LinearIgnoreCase);

    private static readonly Regex PasswordLiteralRegex = new("\\b(password|pwd)\\s*=\\s*([^;,'\"\\s]{6,})", LinearIgnoreCase);

    private static readonly Regex PasswordPlaceholderRegex = new("^(your|changeme|placeholder|example|test|dummy|sample|xxx+|<|\\$\\{|%|\\{)", LinearIgnoreCase);

    private static readonly Regex BearerRegex = new("\\bBearer\\s+([^\\s\"',;]+)", LinearIgnoreCase);

    private static readonly Regex BasicRegex = new("\\bBasic\\s+([A-Za-z0-9+/]{8,}={0,2})", Linear);

    private static readonly Regex CliFlagRegex = new(
        "(?:^|[\\s\"'(=])(-{1,2}[A-Za-z0-9_-]*(?:token|secret|pass|pwd|key|auth|credential)[A-Za-z0-9_-]*(?:=|\\s+|[\"']\\s*,\\s*))(\"[^\"]*\"|'[^']*'|[^\\s\"']+)",
        LinearIgnoreCase | RegexOptions.Multiline);

    private static readonly Regex BareSecretFlagRegex = new(
        "^-{1,2}[A-Za-z0-9_-]*(?:token|secret|pass|pwd|key|auth|credential)[A-Za-z0-9_-]*$", LinearIgnoreCase);

    private static readonly Regex AssignmentRegex = new(
        "\\b([A-Za-z0-9_.-]*(?:token|secret|passw(?:or)?d|pwd|api[-_]?key|access[-_]?key|private[-_]?key|credential|cookie)[A-Za-z0-9_.-]*"
        + "|(?:[A-Za-z0-9_.-]*[_.-])?(?:key|pass|auth|authorization|session)(?:[_.-][A-Za-z0-9_.-]*)?)"
        + "([\"']?\\s*[=:]\\s*(?:(?:Bearer|Basic)\\s+)?)(\"[^\"]*\"|'[^']*'|[^\\s,;\"']+)",
        LinearIgnoreCase);

    private static readonly Regex CamelAssignmentRegex = new(
        "\\b([A-Za-z0-9_.-]*[a-z0-9](?:Key|Keys|Pass|Auth|Session|Secret|Token|Password|Pwd|Credential|Cookie)[A-Za-z0-9_.-]*)"
        + "([\"']?\\s*[=:]\\s*(?:(?:Bearer|Basic)\\s+)?)(\"[^\"]*\"|'[^']*'|[^\\s,;\"']+)",
        Linear);

    private static readonly Regex NetrcPasswordRegex = new("\\b(?:password|passwd)[ \\t]+([^\\s=:]\\S*)", LinearIgnoreCase);

    public bool IsSecretName(string name)
    {
        var compact = SeparatorRegex.Replace(name, string.Empty).ToLowerInvariant();
        if (SecretSubstrings.Any(compact.Contains))
        {
            return true;
        }

        try
        {
            return WordBoundaryRegex().Split(name)
                .SelectMany(part => SeparatorRegex.Split(part))
                .Any(part => part.Length > 0 && SecretParts.Contains(part.ToLowerInvariant()));
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    public string RedactText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var text = PemRegex.Replace(value, Marker);
        foreach (var pattern in HighConfidence)
        {
            text = pattern.Replace(text, Marker);
        }

        text = AccountKeyRegex.Replace(text, "${1}" + Marker);
        text = UrlCredentialRegex.Replace(text, "${1}" + Marker + "${3}");
        text = PasswordLiteralRegex.Replace(text, m => IsExempt(m.Groups[2].Value) ? m.Value : ReplaceGroup(m, m.Groups[2]));
        text = BearerRegex.Replace(text, m => ReplaceGroup(m, m.Groups[1]));
        text = BasicRegex.Replace(text, m => ReplaceGroup(m, m.Groups[1]));
        text = CliFlagRegex.Replace(text, m => ReplaceValue(m, m.Groups[2]));
        text = AssignmentRegex.Replace(text, m =>
            IsPasswordName(m.Groups[1].Value) && IsExempt(m.Groups[3].Value) ? m.Value : ReplaceValue(m, m.Groups[3]));
        text = CamelAssignmentRegex.Replace(text, m => ReplaceValue(m, m.Groups[3]));
        text = NetrcPasswordRegex.Replace(text, m => ReplaceGroup(m, m.Groups[1]));
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
                var afterSecretFlag = false;
                foreach (var item in array)
                {
                    items.Add(afterSecretFlag && item is JsonValue ? JsonValue.Create(Marker) : RedactJson(item, redactAllValues));
                    afterSecretFlag = item is JsonValue flag && flag.TryGetValue<string>(out var flagText) && BareSecretFlagRegex.IsMatch(flagText);
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
        value.StartsWith(Marker, StringComparison.Ordinal) || PasswordPlaceholderRegex.IsMatch(value);

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

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.None, 1000)]
    private static partial Regex WordBoundaryRegex();
}
