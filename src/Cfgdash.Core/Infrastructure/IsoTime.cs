using System.Globalization;

namespace Cfgdash.Core.Infrastructure;

internal static class IsoTime
{
    /// <summary>ISO 8601 with seconds and offset, e.g. <c>2026-10-27T09:30:00+01:00</c>.</summary>
    public static string Format(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
