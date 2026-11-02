using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core;

namespace Cfgdash.Host;

/// <summary>Reads the <c>cfgdash mcp</c> capability (assistant-v1). Process scope only, never a Windows User or Machine value; the value is never logged or echoed.</summary>
internal static class McpCapability
{
    public const string Variable = "CFGDASH_MCP_CAPABILITY";

    public static string? Read(IEnvironmentReader environment) => environment.Get(Variable);
}

/// <summary>
/// <c>cfgdash mcp</c>: an MCP server over stdio (newline-delimited JSON-RPC 2.0) that forwards the five assistant tools to
/// <c>POST http://127.0.0.1:&lt;port&gt;/api/v2/assistant/internal/&lt;tool&gt;</c> with the capability as bearer. It never listens and
/// never reads <c>run.json</c>. Exit 0 on stdin EOF, 1 when the link fails (connection error, 401 or 404); a slow call answers a <c>timeout</c> tool error.
/// </summary>
internal static class McpServer
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const string DefaultProtocolVersion = "2025-06-18";

    private static readonly JsonObject NoArgs = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false };

    public static async Task<int> RunAsync(TextReader input, TextWriter output, TextWriter error, int port, string capability, HttpMessageHandler? handler = null)
    {
        using var http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
        var baseUri = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/api/v2/assistant/internal/";
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonObject? message;
            try
            {
                message = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                message = null;
            }

            if (message is null)
            {
                await WriteAsync(output, Failure(null, -32700, "parse error"));
                continue;
            }

            var id = message["id"]?.DeepClone();
            var method = message["method"] is JsonValue m && m.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : null;
            if (id is null)
            {
                continue;
            }

            JsonObject response;
            switch (method)
            {
                case "initialize":
                    response = Success(id, new JsonObject
                    {
                        ["protocolVersion"] = message["params"]?["protocolVersion"] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Length <= 32
                            ? v.GetValue<string>()
                            : DefaultProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "cfgdash", ["version"] = HostDependencies.Version },
                    });
                    break;
                case "ping":
                    response = Success(id, new JsonObject());
                    break;
                case "tools/list":
                    response = Success(id, new JsonObject { ["tools"] = Tools() });
                    break;
                case "tools/call":
                    var name = message["params"]?["name"] is JsonValue n && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;
                    if (name is null || !Tools().OfType<JsonObject>().Any(t => (string?)t["name"] == name))
                    {
                        response = Failure(id, -32602, "unknown tool");
                        break;
                    }

                    var arguments = message["params"]?["arguments"] as JsonObject ?? [];
                    var (linkFailed, text) = await CallAsync(http, baseUri + name, capability, arguments);
                    if (linkFailed)
                    {
                        await error.WriteLineAsync($"cfgdash mcp: {text}");
                        return CfgdashCli.ExitError;
                    }

                    response = Success(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                        ["isError"] = IsToolError(text),
                    });
                    break;
                default:
                    response = Failure(id, -32601, "method not found");
                    break;
            }

            await WriteAsync(output, response);
        }

        return CfgdashCli.ExitOk;
    }

    /// <summary>The tool body text, or <c>linkFailed</c> with a reason that never contains the capability.</summary>
    private static async Task<(bool LinkFailed, string Text)> CallAsync(HttpClient http, string uri, string capability, JsonObject arguments)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(arguments.ToJsonString(), new UTF8Encoding(false), "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", capability);
        try
        {
            using var response = await http.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            {
                return (true, $"link refused ({(int)response.StatusCode})");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return (false, $"{{\"error\":\"cfgdash-{((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}\"}}");
            }

            return (false, await response.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            return (false, "{\"error\":\"response-too-large\"}");
        }
        catch (TaskCanceledException e) when (e.InnerException is TimeoutException)
        {
            return (false, "{\"error\":\"timeout\"}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (true, "link failed");
        }
    }

    private static bool IsToolError(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject body && body.ContainsKey("error");
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static JsonArray Tools() =>
    [
        Tool("get_snapshot", "The redacted config snapshot of the bound folder, filtered to the assistant allowlist, capped at 48 KiB. The collectors list names every collector id and its view; when truncated is true, call again with one view or collectorId from that list.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["view"] = StringSchema(), ["collectorId"] = StringSchema() },
                ["additionalProperties"] = false,
            }),
        Tool("get_findings", "The health findings of the bound folder.", NoArgs.DeepClone()),
        Tool("get_file", "The redacted content of one registered file by fileId, capped at 64 KiB.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["fileId"] = StringSchema() },
                ["required"] = new JsonArray("fileId"),
                ["additionalProperties"] = false,
            }),
        Tool("search", "Case-insensitive search over the snapshot and registered files of the bound folder.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["q"] = new JsonObject { ["type"] = "string", ["minLength"] = 2 } },
                ["required"] = new JsonArray("q"),
                ["additionalProperties"] = false,
            }),
        Tool("propose_change", "Proposes catalog settings changes for the bound folder as a dry run the user reviews and applies. Denied keys answer { denied, reason }.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["changes"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["key"] = StringSchema(),
                                ["scope"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("user", "project", "local") },
                                ["value"] = new JsonObject(),
                                ["unset"] = new JsonObject { ["type"] = "boolean" },
                            },
                            ["required"] = new JsonArray("key", "scope"),
                        },
                    },
                    ["rationale"] = StringSchema(),
                },
                ["required"] = new JsonArray("changes", "rationale"),
                ["additionalProperties"] = false,
            }),
    ];

    private static JsonObject Tool(string name, string description, JsonNode schema) =>
        new() { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };

    private static JsonObject StringSchema() => new() { ["type"] = "string" };

    private static JsonObject Success(JsonNode id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Failure(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static async Task WriteAsync(TextWriter output, JsonObject message)
    {
        await output.WriteAsync(message.ToJsonString() + "\n");
        await output.FlushAsync();
    }
}
