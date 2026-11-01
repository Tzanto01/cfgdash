using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cfgdash.Core.Collectors;

/// <summary>Why a metrics store request failed; <see cref="Exception.Message"/> is the one-line collector reason.</summary>
internal sealed class MetricsStoreException(string message) : Exception(message);

/// <summary>One series of a Prometheus API answer: its labels and its (unix seconds, value) samples.</summary>
internal sealed record PromSeries(IReadOnlyDictionary<string, string> Labels, IReadOnlyList<(double Time, double Value)> Samples);

/// <summary>
/// Bounded read-only client for one store origin (docs/contracts/usage-v1.md): plain GETs with no headers or credentials,
/// 5 s per request, at most 10 requests, same-origin redirects only, answers capped at 8 MiB.
/// </summary>
internal sealed class PrometheusClient(HttpMessageHandler handler, Uri origin)
{
    public const int MaxRequests = 10;
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient http = new(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private int requests;

    /// <summary><c>host:port</c> of the store.</summary>
    public string Source { get; } = $"{origin.Host}:{origin.Port}";

    public Task<IReadOnlyList<PromSeries>> QueryAsync(string query, double time, CancellationToken ct) =>
        SeriesAsync("/api/v1/query", [("query", query), ("time", Unix(time))], ct);

    public Task<IReadOnlyList<PromSeries>> QueryRangeAsync(string query, double start, double end, int step, CancellationToken ct) =>
        SeriesAsync("/api/v1/query_range", [("query", query), ("start", Unix(start)), ("end", Unix(end)), ("step", step.ToString(CultureInfo.InvariantCulture))], ct);

    public async Task<IReadOnlyList<string>> LabelsAsync(IEnumerable<string> matchers, double start, double end, CancellationToken ct)
    {
        var data = await GetDataAsync("/api/v1/labels", [.. matchers.Select(m => ("match[]", m)), ("start", Unix(start)), ("end", Unix(end))], ct);
        return data is JsonArray array ? [.. array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>()] : throw Unexpected();
    }

    private async Task<IReadOnlyList<PromSeries>> SeriesAsync(string path, IReadOnlyList<(string, string)> parameters, CancellationToken ct)
    {
        var data = await GetDataAsync(path, parameters, ct);
        if (data?["result"] is not JsonArray result)
        {
            throw Unexpected();
        }

        var series = new List<PromSeries>();
        foreach (var item in result.OfType<JsonObject>())
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item["metric"] is JsonObject metric)
            {
                foreach (var (key, value) in metric)
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        labels[key] = s;
                    }
                }
            }

            var samples = new List<(double, double)>();
            foreach (var sample in item["values"] is JsonArray values ? values : new JsonArray(item["value"]?.DeepClone()))
            {
                if (sample is JsonArray { Count: 2 } pair && pair[0] is JsonValue t && t.TryGetValue<double>(out var time)
                    && pair[1] is JsonValue raw && raw.TryGetValue<string>(out var text)
                    && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && Math.Abs(value) < 1e15)
                {
                    samples.Add((time, value));
                }
            }

            series.Add(new PromSeries(labels, samples));
        }

        return series;
    }

    private async Task<JsonNode?> GetDataAsync(string path, IReadOnlyList<(string Key, string Value)> parameters, CancellationToken ct)
    {
        var uri = new Uri(origin, path + "?" + string.Join('&', parameters.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value))));
        while (true)
        {
            if (++requests > MaxRequests)
            {
                throw new MetricsStoreException($"metrics store at {Source} needs more than {MaxRequests} requests");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = new Uri(uri, location);
                    if (!SameOrigin(next))
                    {
                        throw new MetricsStoreException($"metrics store at {Source} redirects to another origin (not followed)");
                    }

                    uri = next;
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new MetricsStoreException("metrics store needs auth (not supported)");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw Unreachable();
                }

                return ParseData(await ReadCappedAsync(response.Content, timeout.Token));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Unreachable();
            }
            catch (HttpRequestException)
            {
                throw Unreachable();
            }
        }
    }

    private JsonNode? ParseData(byte[] body)
    {
        try
        {
            var root = JsonNode.Parse(body) as JsonObject;
            return root?["status"]?.GetValueKind() == JsonValueKind.String && root["status"]!.GetValue<string>() == "success" ? root["data"] : throw Unexpected();
        }
        catch (JsonException)
        {
            throw Unexpected();
        }
    }

    private async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw new MetricsStoreException($"metrics store at {Source} answered more than 8 MiB");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private bool SameOrigin(Uri uri) =>
        uri.Scheme == origin.Scheme && string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == origin.Port;

    private MetricsStoreException Unreachable() => new($"metrics store at {Source} unreachable");

    private MetricsStoreException Unexpected() => new($"metrics store at {Source} gave an unexpected answer");

    private static string Unix(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}
