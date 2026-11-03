using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core;

namespace Cfgdash.Host.Tests;

public sealed class AssistantMcpTests
{
    private const string Capability = "synthetic-capability-value";

    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return respond(request, body);
        }
    }

    private sealed class ScopedOnlyReader : IEnvironmentReader
    {
        public string? Get(string name) => null;

        public string? GetScoped(string name, EnvironmentVariableTarget target) => target == EnvironmentVariableTarget.User ? "user-scope-value" : null;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static async Task<(int Exit, List<JsonObject> Replies, string Error)> RunAsync(StubHandler handler, params string[] lines)
    {
        using var input = new StringReader(string.Join('\n', lines) + "\n");
        var output = new CaptureWriter();
        var error = new CaptureWriter();
        var exit = await McpServer.RunAsync(input, output, error, 4711, Capability, handler);
        var replies = output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        return (exit, replies, error.Text);
    }

    private static string Call(int id, string tool, string arguments) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}";

    [Fact]
    public async Task Initialize_and_tools_list_serve_exactly_the_five_assistant_tools()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("no link call expected"));

        var (exit, replies, _) = await RunAsync(
            handler,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        Assert.Equal(0, exit);
        Assert.Equal(2, replies.Count);
        Assert.Equal("cfgdash", replies[0]["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        var names = replies[1]["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Order().ToList();
        Assert.Equal(new[] { "get_file", "get_findings", "get_snapshot", "propose_change", "propose_file_edit", "search" }, names);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Propose_change_forwards_with_the_capability_and_returns_the_tool_body_without_confirm()
    {
        var handler = new StubHandler((_, _) => Json("""{"proposalId":"p1","expiresAt":"2026-01-02T03:14:05Z","files":[{"path":"/x/.claude/settings.json","scope":"project","diff":"+theme"}]}"""));
        var arguments = """{"changes":[{"key":"theme","scope":"project","value":"dark"}],"rationale":"why"}""";

        var (exit, replies, _) = await RunAsync(handler, Call(7, "propose_change", arguments));

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:4711/api/v2/assistant/internal/propose_change", request.Uri);
        Assert.Equal("Bearer " + Capability, request.Authorization);
        Assert.Equal(JsonNode.Parse(arguments)!.ToJsonString(), JsonNode.Parse(request.Body)!.ToJsonString());
        var text = replies[0]["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Equal("p1", JsonNode.Parse(text)!["proposalId"]!.GetValue<string>());
        Assert.DoesNotContain("confirm", replies[0].ToJsonString());
        Assert.False(replies[0]["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_tool_error_body_is_reported_as_a_tool_error_without_ending_the_server()
    {
        var handler = new StubHandler((_, _) => Json("""{"denied":"env.X","reason":"assistant-policy","error":"denied"}"""));

        var (exit, replies, _) = await RunAsync(handler, Call(1, "propose_change", "{}"), Call(2, "get_findings", "{}"));

        Assert.Equal(0, exit);
        Assert.Equal(2, replies.Count);
        Assert.True(replies[0]["result"]!["isError"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_refused_link_exits_1_without_a_reply_and_never_echoes_the_capability(HttpStatusCode status)
    {
        var handler = new StubHandler((_, _) => Json("{}", status));

        var (exit, replies, error) = await RunAsync(handler, Call(1, "get_findings", "{}"), Call(2, "get_findings", "{}"));

        Assert.Equal(1, exit);
        Assert.Empty(replies);
        Assert.Single(handler.Requests);
        Assert.NotEmpty(error);
        Assert.DoesNotContain(Capability, error);
    }

    [Fact]
    public async Task A_connection_failure_exits_1_and_never_echoes_the_capability()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connect failed for " + "Bearer " + Capability));

        var (exit, _, error) = await RunAsync(handler, Call(1, "get_findings", "{}"));

        Assert.Equal(1, exit);
        Assert.DoesNotContain(Capability, error);
    }

    [Fact]
    public void Capability_is_read_from_process_scope_only_never_from_a_user_scope_value()
    {
        Assert.Null(McpCapability.Read(new ScopedOnlyReader()));
        Assert.Equal("v", McpCapability.Read(new FakeEnv(new Dictionary<string, string> { ["CFGDASH_MCP_CAPABILITY"] = "v" })));
    }
}
