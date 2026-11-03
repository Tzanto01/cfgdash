using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Cfgdash.Core;
using Cfgdash.Core.Assistant;
using Cfgdash.Core.Config;

namespace Cfgdash.Host;

/// <summary><c>Starting</c>: a process is being spawned, no turn. <c>Running</c>: a turn runs. <c>Idle</c>: a live process, no turn. <c>Dead</c>: no process. Every state but <c>Dead</c> holds one of the live-process slots.</summary>
internal enum AssistantState
{
    Starting,
    Running,
    Idle,
    Dead,
}

/// <summary>The outcome of the CLI checks behind <c>GET /api/v2/assistant/status</c>. <paramref name="Reason"/> is a cfgdash message; <paramref name="Detail"/> is the redacted CLI text behind it, if any.</summary>
internal sealed record AssistantProbe(bool Available, string? Reason, string? ClaudePath = null, string? Version = null, string? Setsid = null, string? Detail = null);

/// <summary>One cfgdash assistant session: its bound dir, its current <c>claude</c> process and its SSE events. <see cref="State"/>, <see cref="Process"/>, <see cref="Probe"/>, <see cref="ToolCalls"/> and <see cref="Sequence"/> change only under the registry lock.</summary>
internal sealed class AssistantSession(string id, string dir, string model, string effort, decimal? maxBudgetUsd, long created)
{
    public const int MaxQueuedEvents = 512;
    private readonly Lock events = new();
    private readonly Queue<string> backlog = new();
    private readonly List<Channel<string>> listeners = [];
    private double costUsd;
    private long lastActivity = created;
    private bool closed;

    public string Id { get; } = id;

    public string Dir { get; } = dir;

    public string Model { get; } = model;

    public string Effort { get; } = effort;

    /// <summary>The session-wide <c>--max-budget-usd</c> cap, read from cfgdash settings when the session was created; null when <c>assistant.budgetEnabled</c> was false.</summary>
    public decimal? MaxBudgetUsd { get; } = maxBudgetUsd;

    /// <summary>The per-session fence nonce of the system prompt, context block and <c>get_file</c> content.</summary>
    public string Nonce { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public long Created { get; } = created;

    public AssistantState State { get; set; } = AssistantState.Starting;

    public AssistantProcess? Process { get; set; }

    public AssistantProbe? Probe { get; set; }

    public int ToolCalls { get; set; }

    public int Sequence { get; set; }

    public double CostUsd => Volatile.Read(ref costUsd);

    public long LastActivity => Interlocked.Read(ref lastActivity);

    public void Touch(long ticks) => Interlocked.Exchange(ref lastActivity, ticks);

    public double AddCost(double value)
    {
        lock (events)
        {
            costUsd += value;
            return costUsd;
        }
    }

    /// <summary>Queues one SSE frame for every listener; without a listener it is kept in a backlog of 512, oldest dropped.</summary>
    public void Emit(string name, JsonObject data)
    {
        var frame = $"event: {name}\ndata: {data.ToJsonString()}\n\n";
        lock (events)
        {
            if (listeners.Count == 0)
            {
                if (backlog.Count == MaxQueuedEvents)
                {
                    backlog.Dequeue();
                }

                backlog.Enqueue(frame);
                return;
            }

            foreach (var listener in listeners)
            {
                listener.Writer.TryWrite(frame);
            }
        }
    }

    public void Error(string error, string message) => Emit("error", new JsonObject { ["error"] = error, ["message"] = message });

    /// <summary>A listener that first gets the backlog. Completes when the session is removed.</summary>
    public Channel<string> Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueuedEvents) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (events)
        {
            if (closed)
            {
                channel.Writer.TryComplete();
                return channel;
            }

            while (backlog.TryDequeue(out var frame))
            {
                channel.Writer.TryWrite(frame);
            }

            listeners.Add(channel);
        }

        return channel;
    }

    public void Unsubscribe(Channel<string> channel)
    {
        lock (events)
        {
            listeners.Remove(channel);
        }
    }

    public void Close()
    {
        lock (events)
        {
            closed = true;
            foreach (var listener in listeners)
            {
                listener.Writer.TryComplete();
            }

            listeners.Clear();
        }
    }
}

/// <summary>
/// The assistant sessions of one cfgdash instance (assistant-v1 Runtime): at most 2 live <c>claude</c> processes, 16 kept sessions,
/// the CLI status probe, the init containment check with one plugin retry, turns with their limits, idle ends and kill on every shutdown path.
/// </summary>
internal sealed class AssistantRegistry : IAsyncDisposable
{
    public const int MaxLive = 2;
    public const int MaxSessions = 16;
    public const int MaxToolCalls = 40;
    public const string DefaultModel = "sonnet";
    public const string DefaultEffort = "medium";
    public const decimal DefaultMaxBudgetUsd = 2.0m;
    /// <summary>The model aliases Claude Code 2.1.316 and 2.1.317 accept with <c>--model</c> (checked against the CLI), with their labels.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> ClaudeModels =
        [("sonnet", "Sonnet"), ("opus", "Opus"), ("haiku", "Haiku"), ("fable", "Fable"), ("sonnet[1m]", "Sonnet (1M context)"), ("opus[1m]", "Opus (1M context)")];

    public static readonly IReadOnlyList<string> Models = [.. ClaudeModels.Select(m => m.Id)];
    internal static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan TurnLimit = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan InterruptGrace = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan InitLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private const int MaxReasonChars = 300;
    private const int MaxLoggedArgsChars = 2048;
    private const string StartFailed = "the claude process could not start";
    private const string CouldNotStart = "Claude Code could not start; run `claude` once in a terminal to check it";

    private static readonly IReadOnlyList<string> RequiredFlags =
        [.. AssistantLaunch.Arguments("s", "m", DefaultModel, DefaultEffort, DefaultMaxBudgetUsd, "p").Select(a => a.Split('=')[0])];

    private readonly Lock gate = new();
    private readonly Dictionary<string, AssistantSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Path, string Version), string> sticky = [];
    private readonly Dictionary<string, HashSet<string>> disabledPlugins = new(StringComparer.Ordinal);
    private readonly List<Task> ending = [];
    private readonly CoreServices core;
    private readonly TimeProvider time;
    private readonly string cacheDir;
    private readonly string? exePath;
    private readonly CfgdashConfig config;
    private readonly Func<int> port;
    private readonly CfgdashLog log;
    private readonly CancellationTokenSource stop = new();
    private readonly Task sweep;
    private bool stopping;
    private string? lastFailure;
    private string? lastFailureDetail;

    public AssistantRegistry(CoreServices core, HostDependencies dependencies, Func<int> port, CfgdashLog log)
    {
        this.core = core;
        this.port = port;
        this.log = log;
        time = dependencies.TimeProvider;
        cacheDir = dependencies.CacheDir(core.EnvironmentReader);
        exePath = dependencies.ExePath;
        config = new CfgdashConfig(dependencies.ConfigDir(core.EnvironmentReader), cacheDir, time);
        sweep = SweepAsync();
    }

    private string AssistantDir => Path.Combine(cacheDir, "assistant");

    private long Now => time.GetUtcNow().UtcTicks;

    private string WorkDir() => PrivateFiles.EnsureDirectory(Path.Combine(PrivateFiles.EnsureDirectory(AssistantDir), "work"));

    /// <summary>Resolves <c>claude</c> (absolute PATH entries only; never a <c>.cmd</c>/<c>.bat</c>), needs <c>setsid</c> on Linux/macOS, then checks <c>--version</c>, every rev 2.1 flag in <c>--help</c> and <c>auth status</c> <c>loggedIn: true</c>. A containment failure is sticky per CLI path and version.</summary>
    public async Task<AssistantProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var environment = core.EnvironmentReader;
        var claude = HostDependencies.FindOnPath("claude", environment);
        if (claude is null)
        {
            return new AssistantProbe(false, "claude not found on PATH");
        }

        if (!Path.IsPathFullyQualified(claude) || Path.GetExtension(claude).ToLowerInvariant() is ".cmd" or ".bat")
        {
            return new AssistantProbe(false, "claude must be a native executable", claude);
        }

        string? setsid = null;
        if (!OperatingSystem.IsWindows() && (setsid = HostDependencies.FindOnPath("setsid", environment)) is null)
        {
            return new AssistantProbe(false, "setsid not found on PATH", claude);
        }

        if (string.IsNullOrEmpty(exePath))
        {
            return new AssistantProbe(false, "the cfgdash executable path is unknown", claude);
        }

        string workDir;
        try
        {
            workDir = WorkDir();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new AssistantProbe(false, "the cfgdash cache dir is not writable", claude);
        }

        var version = await core.Processes.RunAsync(claude, ["--version"], ProbeTimeout, cancellationToken, workDir);
        if (version.TimedOut || version.ExitCode != 0)
        {
            return new AssistantProbe(false, "claude --version failed", claude);
        }

        var versionText = Cap(AssistantRedaction.Text(version.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty));
        lock (gate)
        {
            if (sticky.TryGetValue((claude, versionText), out var reason))
            {
                return new AssistantProbe(false, reason, claude, versionText);
            }
        }

        var help = await core.Processes.RunAsync(claude, ["--help"], ProbeTimeout, cancellationToken, workDir);
        if (help.TimedOut || help.ExitCode != 0)
        {
            return new AssistantProbe(false, "claude --help failed", claude, versionText);
        }

        if (RequiredFlags.FirstOrDefault(flag => !Regex.IsMatch(help.StandardOutput, Regex.Escape(flag) + "(?![A-Za-z0-9-])", RegexOptions.CultureInvariant)) is { } missing)
        {
            return new AssistantProbe(false, $"the installed claude lacks {missing}", claude, versionText);
        }

        var auth = await core.Processes.RunAsync(claude, ["auth", "status"], ProbeTimeout, cancellationToken, workDir);
        if (auth.TimedOut || auth.ExitCode != 0 || !LoggedIn(auth.StandardOutput))
        {
            return new AssistantProbe(false, "claude is not logged in", claude, versionText);
        }

        lock (gate)
        {
            return new AssistantProbe(true, lastFailure, claude, versionText, setsid, lastFailureDetail);
        }
    }

    /// <summary><c>assistant.applyEnabled</c>, read from cfgdash settings at every call (assistant-v1 rev 3).</summary>
    public bool ApplyEnabled => config.Read().AssistantApplyEnabled;

    public List<AssistantSession> List()
    {
        lock (gate)
        {
            return [.. sessions.Values.OrderBy(s => s.Created)];
        }
    }

    public AssistantSession? Find(string id)
    {
        lock (gate)
        {
            if (sessions.TryGetValue(id, out var session))
            {
                session.Touch(Now);
            }

            return session;
        }
    }

    /// <summary>
    /// Reserves a slot, then starts the process. A null <paramref name="model"/> or <paramref name="effort"/> takes the cfgdash setting
    /// (<c>assistant.model</c>, <c>assistant.effort</c>); the budget is always <c>assistant.maxBudgetUsd</c>, or none when <c>assistant.budgetEnabled</c> is false. An invalid setting falls back to
    /// the built-in default. 200 <c>{ id }</c>, 429 at 2 live processes, 503 when unavailable or stopping.
    /// </summary>
    public async Task<(int Status, JsonObject Body)> CreateAsync(string dir, string? model, string? effort, CancellationToken cancellationToken)
    {
        var settings = config.Read();
        model ??= AssistantLaunch.IsValidModel(settings.AssistantModel) ? settings.AssistantModel : DefaultModel;
        effort ??= AssistantLaunch.IsValidEffort(settings.AssistantEffort) ? settings.AssistantEffort : DefaultEffort;
        decimal? budget = !settings.AssistantBudgetEnabled ? null
            : double.IsFinite(settings.AssistantMaxBudgetUsd) && settings.AssistantMaxBudgetUsd > 0 ? (decimal)settings.AssistantMaxBudgetUsd : DefaultMaxBudgetUsd;

        lock (gate)
        {
            if (LiveCount() >= MaxLive)
            {
                return TooMany();
            }
        }

        var probe = await ProbeAsync(cancellationToken);
        if (!probe.Available)
        {
            return Unavailable(probe.Reason);
        }

        AssistantSession session;
        lock (gate)
        {
            if (stopping)
            {
                return Unavailable("stopping");
            }

            if (LiveCount() >= MaxLive)
            {
                return TooMany();
            }

            Evict();
            session = new AssistantSession(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), dir, model, effort, budget, Now);
            sessions[session.Id] = session;
        }

        if (!Spawn(session, probe))
        {
            lock (gate)
            {
                sessions.Remove(session.Id);
            }

            session.Close();
            return Unavailable(StartFailed);
        }

        lock (gate)
        {
            if (session.State == AssistantState.Starting)
            {
                session.State = AssistantState.Idle;
            }
        }

        return (StatusCodes.Status200OK, new JsonObject { ["id"] = session.Id });
    }

    /// <summary>Starts a turn with <paramref name="text"/> as the user line; a dead session gets a new process (and capability) first. 202, 404, 409 while a turn runs or a process starts, 429, 503.</summary>
    public async Task<(int Status, JsonObject? Body)> SendAsync(AssistantSession session, string text, CancellationToken cancellationToken)
    {
        var result = await TrySendAsync(session, text, cancellationToken);
        if (result.Status == 0)
        {
            result = await TrySendAsync(session, text, cancellationToken);
        }

        return result.Status == 0 ? Busy() : result;
    }

    private async Task<(int Status, JsonObject? Body)> TrySendAsync(AssistantSession session, string text, CancellationToken cancellationToken)
    {
        bool dead;
        lock (gate)
        {
            if (!sessions.ContainsKey(session.Id))
            {
                return NotFound();
            }

            if (session.State is AssistantState.Running or AssistantState.Starting)
            {
                return Busy();
            }

            dead = session.State == AssistantState.Dead;
        }

        var probe = dead ? await ProbeAsync(cancellationToken) : null;
        if (probe is { Available: false })
        {
            return Unavailable(probe.Reason);
        }

        lock (gate)
        {
            if (!sessions.ContainsKey(session.Id))
            {
                return NotFound();
            }

            if (session.State is AssistantState.Running or AssistantState.Starting)
            {
                return Busy();
            }

            if (session.State == AssistantState.Dead)
            {
                if (probe is null)
                {
                    return (0, null);
                }

                if (RemainingBudget(session) <= 0)
                {
                    return (StatusCodes.Status409Conflict, new JsonObject { ["error"] = "budget-spent" });
                }

                if (stopping)
                {
                    return Unavailable("stopping");
                }

                if (LiveCount() >= MaxLive)
                {
                    return TooMany();
                }
            }

            session.State = AssistantState.Running;
            session.ToolCalls = 0;
            session.Touch(Now);
        }

        if (probe is not null && !Spawn(session, probe))
        {
            return Unavailable(StartFailed);
        }

        var line = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            },
        }.ToJsonString();
        _ = Task.Run(() => RunTurnAsync(session, line));
        return (StatusCodes.Status202Accepted, null);
    }

    /// <summary>Removes the session and kills its process tree. False for an unknown id.</summary>
    public bool Delete(string id)
    {
        AssistantSession? session;
        AssistantProcess? process;
        lock (gate)
        {
            if (!sessions.Remove(id, out session))
            {
                return false;
            }

            process = session.Process;
            session.Process = null;
            session.State = AssistantState.Dead;
        }

        process?.Kill();
        session.Close();
        return true;
    }

    /// <summary>The session whose live, init-checked process holds <paramref name="capability"/>, compared in fixed time against every session; counts the call against the turn's limit.</summary>
    public AssistantSession? Authorize(string? capability, out bool limited)
    {
        limited = false;
        if (string.IsNullOrEmpty(capability))
        {
            return null;
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(capability));
        lock (gate)
        {
            AssistantSession? match = null;
            foreach (var session in sessions.Values)
            {
                if (session.Process is { InitPassed: true, Ended: false } process
                    && CryptographicOperations.FixedTimeEquals(presented, SHA256.HashData(Encoding.UTF8.GetBytes(process.Capability))))
                {
                    match = session;
                }
            }

            if (match is not null)
            {
                match.Touch(Now);
                limited = ++match.ToolCalls > MaxToolCalls;
            }

            return match;
        }
    }

    /// <summary>Appends <c>{ at, tool, args, ms, ok }</c> to <c>&lt;cache&gt;/assistant/&lt;sessionId&gt;.jsonl</c> (0600). <paramref name="args"/> is redacted and capped at 2 KiB; results are never logged.</summary>
    public void LogTool(AssistantSession session, string tool, JsonNode? args, long ms, bool ok)
    {
        var text = AssistantRedaction.Json(args)?.ToJsonString() ?? "null";
        if (text.Length > MaxLoggedArgsChars)
        {
            text = text[..(char.IsHighSurrogate(text[MaxLoggedArgsChars - 1]) ? MaxLoggedArgsChars - 1 : MaxLoggedArgsChars)];
        }

        var line = new JsonObject
        {
            ["at"] = time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["tool"] = tool,
            ["args"] = text,
            ["ms"] = ms,
            ["ok"] = ok,
        }.ToJsonString() + "\n";
        try
        {
            using var stream = PrivateFiles.Open(Path.Combine(PrivateFiles.EnsureDirectory(AssistantDir), session.Id + ".jsonl"), FileMode.Append);
            stream.Write(Encoding.UTF8.GetBytes(line));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Error(e);
        }
    }

    /// <summary>Kills every process tree and refuses new starts. Runs on <c>ApplicationStopping</c> and again on dispose.</summary>
    public void StopAll()
    {
        List<(AssistantSession Session, AssistantProcess? Process)> all;
        lock (gate)
        {
            stopping = true;
            all = [.. sessions.Values.Select(s => (s, s.Process))];
            foreach (var (session, _) in all)
            {
                session.Process = null;
                session.State = AssistantState.Dead;
            }
        }

        foreach (var (session, process) in all)
        {
            if (process is not null)
            {
                process.Kill();
                lock (ending)
                {
                    ending.Add(process.Exited);
                }
            }

            session.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopAll();
        await stop.CancelAsync();
        await sweep;
        Task[] waits;
        lock (ending)
        {
            waits = [.. ending];
        }

        try
        {
            await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }

        stop.Dispose();
    }

    private bool Spawn(AssistantSession session, AssistantProbe probe)
    {
        AssistantProcess process;
        try
        {
            var disabled = new HashSet<string>(AssistantLaunch.UserPluginIds(core.Environment), StringComparer.Ordinal);
            var workDir = WorkDir();
            AssistantStart start;
            lock (gate)
            {
                session.Probe = probe;
                if (disabledPlugins.TryGetValue(probe.Version!, out var known))
                {
                    disabled.UnionWith(known);
                }

                start = new AssistantStart(
                    probe.ClaudePath!,
                    probe.Setsid,
                    workDir,
                    AssistantDir,
                    $"{session.Id}-{++session.Sequence}",
                    session.Model,
                    session.Effort,
                    RemainingBudget(session),
                    AssistantLaunch.SystemPrompt(session.Nonce),
                    disabled,
                    exePath!,
                    port());
            }

            process = AssistantProcess.Start(start);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.Error(e);
            Fail(session, StartFailed);
            return false;
        }

        lock (gate)
        {
            if (!stopping && session.State != AssistantState.Dead && sessions.ContainsKey(session.Id))
            {
                session.Process = process;
                _ = process.Exited.ContinueWith(_ => OnExited(session, process), TaskScheduler.Default);
                return true;
            }
        }

        process.Kill();
        return false;
    }

    private void Fail(AssistantSession session, string reason)
    {
        lock (gate)
        {
            lastFailure = reason;
            lastFailureDetail = null;
            session.State = AssistantState.Dead;
        }
    }

    private void OnExited(AssistantSession session, AssistantProcess process)
    {
        lock (gate)
        {
            if (session.Process != process)
            {
                return;
            }

            session.Process = null;
            if (session.State is AssistantState.Idle or AssistantState.Starting)
            {
                session.State = AssistantState.Dead;
            }
        }
    }

    private async Task RunTurnAsync(AssistantSession session, string line)
    {
        try
        {
            var retried = false;
            while (true)
            {
                AssistantProcess? process;
                lock (gate)
                {
                    process = session.State == AssistantState.Running ? session.Process : null;
                }

                if (process is null)
                {
                    session.Error("process-exited", "the assistant process ended");
                    return;
                }

                if (!await process.SendAsync(line))
                {
                    process.Kill();
                    session.Error("process-exited", "the assistant process ended");
                    return;
                }

                var retry = await ReadTurnAsync(session, process, !retried);
                if (retry is null)
                {
                    return;
                }

                retried = true;
                process.Kill();
                lock (gate)
                {
                    var version = session.Probe!.Version!;
                    if (!disabledPlugins.TryGetValue(version, out var known))
                    {
                        disabledPlugins[version] = known = new HashSet<string>(StringComparer.Ordinal);
                    }

                    known.UnionWith(retry);
                }

                if (!Spawn(session, session.Probe!))
                {
                    session.Error("process-exited", "the assistant process could not restart");
                    return;
                }
            }
        }
        catch (Exception e)
        {
            log.Error(e);
            AssistantProcess? process;
            lock (gate)
            {
                process = session.Process;
            }

            process?.Kill();
            session.Error("internal", "the assistant turn failed");
        }
        finally
        {
            AssistantProcess? dead = null;
            lock (gate)
            {
                if (session.State == AssistantState.Running)
                {
                    if (session.Process is { Ended: false })
                    {
                        session.State = AssistantState.Idle;
                    }
                    else
                    {
                        dead = session.Process;
                        session.Process = null;
                        session.State = AssistantState.Dead;
                    }
                }

                session.Touch(Now);
            }

            dead?.Kill();
        }
    }

    /// <summary>Reads one turn. Returns the plugin ids to disable when the init check failed only on <c>plugins</c> and a retry is allowed; null otherwise (the turn ended).</summary>
    private async Task<IReadOnlyList<string>?> ReadTurnAsync(AssistantSession session, AssistantProcess process, bool allowRetry)
    {
        using var turn = new CancellationTokenSource(TurnLimit, time);
        using var init = new CancellationTokenSource(InitLimit, time);
        using var beforeInit = CancellationTokenSource.CreateLinkedTokenSource(turn.Token, init.Token);
        var redactor = new LineRedactor();
        var sawText = false;
        while (true)
        {
            var checkedInit = process.InitPassed;
            string? line = null;
            try
            {
                if (await process.Lines.WaitToReadAsync(checkedInit ? turn.Token : beforeInit.Token))
                {
                    process.Lines.TryRead(out line);
                }
            }
            catch (OperationCanceledException)
            {
                if (!checkedInit && !turn.IsCancellationRequested)
                {
                    Contain(session, process, "the init event did not arrive within 60 s");
                    return null;
                }

                Delta(session, redactor.Flush(), ref sawText);
                await process.SendAsync(new JsonObject
                {
                    ["type"] = "control_request",
                    ["request_id"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                    ["request"] = new JsonObject { ["subtype"] = "interrupt" },
                }.ToJsonString());
                try
                {
                    await process.Exited.WaitAsync(InterruptGrace, time);
                }
                catch (TimeoutException)
                {
                }

                process.Kill();
                session.Error("turn-timeout", "the turn took longer than 10 minutes");
                return null;
            }

            if (line is null)
            {
                if (process.Lines.Completion.IsCompleted)
                {
                    process.Kill();
                    if (checkedInit)
                    {
                        Delta(session, redactor.Flush(), ref sawText);
                    }
                    else
                    {
                        await Task.WhenAny(process.StderrDrained, Task.Delay(TimeSpan.FromSeconds(2)));
                        SetFailure(CouldNotStart, process.StderrSummary is { Length: > 0 } detail ? detail : "claude exited before init");
                    }

                    if (process.Failure == "line-too-long")
                    {
                        session.Error("line-too-long", "the assistant output a line over 1 MiB");
                    }
                    else
                    {
                        session.Error("process-exited", "the assistant process ended");
                    }

                    return null;
                }

                continue;
            }

            var parsed = StreamJson.Parse(line);
            if (!checkedInit)
            {
                switch (parsed)
                {
                    case StreamEvent.Init(var initEvent):
                        var verdict = InitCheck.Evaluate(initEvent);
                        if (verdict.Ok)
                        {
                            process.InitPassed = true;
                        }
                        else if (verdict.PluginsOnly && allowRetry)
                        {
                            return verdict.PluginIds;
                        }
                        else
                        {
                            Contain(session, process, "containment check failed" + (verdict.Reason is { Length: > 0 } reason ? ": " + reason : string.Empty));
                            return null;
                        }

                        break;
                    case StreamEvent.Result(_, _, var subtype, _):
                        process.Kill();
                        SetFailure(CouldNotStart, "claude ended the turn before init" + (subtype is { Length: > 0 } ? ": " + subtype : string.Empty));
                        session.Error("turn-failed", "the assistant could not start the turn");
                        return null;
                }

                continue;
            }

            switch (parsed)
            {
                case StreamEvent.Init(var reinit) when !InitCheck.Evaluate(reinit).Ok:
                    Contain(session, process, "containment check failed on a later init");
                    return null;
                case StreamEvent.TextDelta(var text):
                    Delta(session, redactor.Push(text), ref sawText);
                    break;
                case StreamEvent.Result(var cost, var isError, var subtype, var resultText):
                    Delta(session, redactor.Flush(), ref sawText);
                    if (!sawText && !isError && !string.IsNullOrEmpty(resultText))
                    {
                        Delta(session, AssistantRedaction.Text(resultText), ref sawText);
                    }

                    if (cost is { } total)
                    {
                        var spent = Math.Max(0, total - process.ReportedCostUsd);
                        process.ReportedCostUsd = Math.Max(total, process.ReportedCostUsd);
                        session.Emit("cost", new JsonObject { ["costUsd"] = session.AddCost(spent) });
                    }

                    if (isError)
                    {
                        var (message, detail) = CliMessages.ForResult(subtype, resultText);
                        SetFailure("the last turn failed: " + message);
                        session.Emit("error", new JsonObject { ["error"] = "turn-failed", ["message"] = message, ["detail"] = detail });
                    }
                    else
                    {
                        SetFailure(null);
                        session.Emit("done", new JsonObject());
                    }

                    return null;
            }
        }
    }

    private static void Delta(AssistantSession session, string text, ref bool sawText)
    {
        if (text.Length > 0)
        {
            sawText = true;
            session.Emit("delta", new JsonObject { ["text"] = text });
        }
    }

    private void Contain(AssistantSession session, AssistantProcess process, string reason)
    {
        process.Kill();
        var redacted = Cap(AssistantRedaction.Text(reason));
        lock (gate)
        {
            if (session.Probe is { ClaudePath: { } path, Version: { } version })
            {
                sticky[(path, version)] = redacted;
            }

            lastFailure = redacted;
            lastFailureDetail = null;
        }

        session.Error("containment", "containment check failed");
    }

    private void SetFailure(string? reason, string? detail = null)
    {
        var redacted = reason is null ? null : Cap(AssistantRedaction.Text(reason));
        lock (gate)
        {
            lastFailure = redacted;
            lastFailureDetail = detail is null ? null : Cap(AssistantRedaction.Text(detail));
        }
    }

    private async Task SweepAsync()
    {
        using var timer = new PeriodicTimer(SweepInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                List<AssistantProcess> idle = [];
                lock (gate)
                {
                    foreach (var session in sessions.Values)
                    {
                        if (session.State == AssistantState.Idle && Now - session.LastActivity >= IdleLimit.Ticks)
                        {
                            if (session.Process is { } process)
                            {
                                idle.Add(process);
                            }

                            session.Process = null;
                            session.State = AssistantState.Dead;
                        }
                    }
                }

                foreach (var process in idle)
                {
                    process.Kill();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private int LiveCount() => sessions.Values.Count(s => s.State != AssistantState.Dead);

    /// <summary>The per-session cap minus what the session's earlier processes spent, floored to cents; each new process gets only this. Null without a budget.</summary>
    private static decimal? RemainingBudget(AssistantSession session) =>
        session.MaxBudgetUsd is { } cap ? Math.Floor((cap - (decimal)session.CostUsd) * 100) / 100 : null;

    private void Evict()
    {
        while (sessions.Count >= MaxSessions && sessions.Values.Where(s => s.State == AssistantState.Dead).MinBy(s => s.LastActivity) is { } oldest)
        {
            sessions.Remove(oldest.Id);
            oldest.Close();
        }
    }

    private static bool LoggedIn(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("loggedIn", out var loggedIn)
                && loggedIn.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Cap(string text) => text.Length > MaxReasonChars ? text[..MaxReasonChars] : text;

    private static (int, JsonObject) Unavailable(string? reason) =>
        (StatusCodes.Status503ServiceUnavailable, new JsonObject { ["error"] = "unavailable", ["reason"] = reason ?? "unavailable" });

    private static (int, JsonObject) TooMany() => (StatusCodes.Status429TooManyRequests, new JsonObject { ["error"] = "too-many-sessions" });

    private static (int, JsonObject) Busy() => (StatusCodes.Status409Conflict, new JsonObject { ["error"] = "turn-running" });

    private static (int, JsonObject) NotFound() => (StatusCodes.Status404NotFound, new JsonObject { ["error"] = "not-found" });
}
