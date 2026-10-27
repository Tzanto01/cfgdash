using System.Text.Json.Nodes;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Settings;

/// <summary>Maps merged settings to redacted kv rows; shared by every collector that shows settings.</summary>
internal static class SettingsRows
{
    public static SourceRef Source(SettingsLayer layer) => new(layer.Label, layer.FileId);

    /// <summary>Secret last key segment -> <c>"[redacted]"</c>, else <c>RedactJson</c>; source is the applied layer or "&lt;n&gt; layers" when merged.</summary>
    public static KvRow ToKvRow(EffectiveSetting setting, IRedactor redactor)
    {
        var secret = setting.Segments.Count > 0 && redactor.IsSecretName(setting.Segments[^1]);
        var changed = false;
        JsonNode? Redact(JsonNode? value)
        {
            var redacted = secret && value is not null ? JsonValue.Create(Redactor.Marker) : redactor.RedactJson(value);
            changed |= !JsonNode.DeepEquals(redacted, value);
            return redacted;
        }

        var value = Redact(setting.Value);
        var contributions = setting.Contributions.Select(c => new Contribution(Redact(c.Value), Source(c.Layer), c.Status)).ToList();
        var merged = setting.Contributions.Count(c => c.Status == ContributionStatus.Merged);
        var applied = setting.Contributions.FirstOrDefault(c => c.Status == ContributionStatus.Applied);
        var source = merged > 0 ? new SourceRef($"{merged} layers") : applied is null ? null : Source(applied.Layer);
        IReadOnlyList<string> flags = changed ? [.. setting.Flags, "redacted"] : setting.Flags;
        return new KvRow(setting.Key, value, source, changed, flags.Count > 0 ? flags : null, contributions);
    }
}
