namespace Cfgdash.Core.Tests.Support;

/// <summary>In-memory environment: process values plus optional Windows User/Machine scope values.</summary>
public sealed class FakeEnvironmentReader : IEnvironmentReader
{
    public Dictionary<string, string> Process { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> User { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Machine { get; } = new(StringComparer.Ordinal);

    public string? Get(string name) => Process.GetValueOrDefault(name);

    public string? GetScoped(string name, EnvironmentVariableTarget target) => target switch
    {
        EnvironmentVariableTarget.User => User.GetValueOrDefault(name),
        EnvironmentVariableTarget.Machine => Machine.GetValueOrDefault(name),
        _ => Process.GetValueOrDefault(name),
    };
}
