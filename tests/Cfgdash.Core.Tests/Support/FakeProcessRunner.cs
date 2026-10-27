using System.Collections.Concurrent;

namespace Cfgdash.Core.Tests.Support;

/// <summary>Canned process results keyed by executable and arguments; records every call. Unknown commands throw.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly ConcurrentDictionary<string, ProcessResult> results = new(StringComparer.Ordinal);

    public ConcurrentQueue<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = new();

    public ConcurrentQueue<string?> StandardInputs { get; } = new();

    public FakeProcessRunner On(string executable, IReadOnlyList<string> arguments, string stdout, int exitCode = 0, string stderr = "")
    {
        results[Key(executable, arguments)] = new ProcessResult(exitCode, stdout, stderr, false);
        return this;
    }

    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? workingDirectory = null, string? standardInput = null)
    {
        Calls.Enqueue((executable, arguments));
        StandardInputs.Enqueue(standardInput);
        return results.TryGetValue(Key(executable, arguments), out var result)
            ? Task.FromResult(result)
            : throw new InvalidOperationException($"No canned result for {executable} {string.Join(' ', arguments)}");
    }

    private static string Key(string executable, IReadOnlyList<string> arguments) => executable + "\u0000" + string.Join("\u0000", arguments);
}
