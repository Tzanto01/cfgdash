using System.Text;
using System.Text.Json;
using Cfgdash.Core;

namespace Cfgdash.Host.Tests;

internal sealed class FakeAutostart(string actual = "off", string? detail = null, string? failure = null) : IAutostart
{
    public List<bool> Sets { get; } = [];

    public Action<bool>? OnSet { get; set; }

    public Func<CoreServices, string?, IAutostart> Factory => (_, _) => this;

    public Task<AutostartState> GetAsync(CancellationToken ct) => Task.FromResult(new AutostartState(actual, detail));

    public Task<string?> SetAsync(bool on, CancellationToken ct)
    {
        Sets.Add(on);
        OnSet?.Invoke(on);
        if (actual == "unsupported")
        {
            return Task.FromResult(detail);
        }

        if (failure is null)
        {
            actual = on ? "on" : "off";
        }

        return Task.FromResult(failure);
    }
}

internal sealed class FakeRunKey : IRunKey
{
    public string? Value { get; set; }

    public bool Fails { get; set; }

    public string? Get() => Value;

    public void Set(string value)
    {
        if (Fails)
        {
            throw new IOException("denied");
        }

        Value = value;
    }

    public void Delete() => Value = null;
}

internal static class UiRequests
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false });

    public static async Task<Resp> SendAsync(ServerHost host, HttpMethod method, string path, string? json = null, bool marker = true, string? origin = null, bool tailnet = false)
    {
        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{(tailnet ? host.BackendPort : host.Port)}{path}");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + host.Token);
        if (tailnet)
        {
            request.Headers.Host = host.TailnetHost;
            request.Headers.TryAddWithoutValidation("Tailscale-User-Login", ServerHost.Login);
        }

        request.Headers.TryAddWithoutValidation("Origin", origin ?? (tailnet ? $"https://{host.TailnetHost}" : $"http://127.0.0.1:{host.Port}"));
        if (marker)
        {
            request.Headers.TryAddWithoutValidation("X-Cfgdash-Request", "1");
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await Http.SendAsync(request);
        return new Resp(response, await response.Content.ReadAsStringAsync());
    }

    public static string Quote(string value) => JsonSerializer.Serialize(value);
}
