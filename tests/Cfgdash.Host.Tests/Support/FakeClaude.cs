using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cfgdash.Host.Tests;

/// <summary>What the fake CLI emits after the first stdin line.</summary>
internal enum FakeInit
{
    Good,
    BadTool,
    BadPlugin,
    None,
}

/// <summary>What the fake CLI does for each user line once init is out.</summary>
internal enum FakeTurn
{
    Normal,
    SplitSecret,
    Stall,
    ControlRequest,
    BigLine,
    ErrorResult,
}

/// <summary>The recorded facts of one fake <c>claude</c> run.</summary>
internal sealed class FakeRun(string dir)
{
    public string Dir { get; } = dir;

    public bool Ready => File.Exists(Path.Combine(Dir, "ready"));

    public IReadOnlyList<string> Argv => File.ReadAllText(Path.Combine(Dir, "argv")).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    public string Cwd => File.ReadAllText(Path.Combine(Dir, "cwd")).Trim();

    public int Pid => int.Parse(File.ReadAllText(Path.Combine(Dir, "pid")).Trim());

    public int? GrandchildPid => File.Exists(Path.Combine(Dir, "grandchild.pid")) ? int.Parse(File.ReadAllText(Path.Combine(Dir, "grandchild.pid")).Trim()) : null;

    public string Settings => File.ReadAllText(Path.Combine(Dir, "settings.json"));

    public string McpConfig => File.ReadAllText(Path.Combine(Dir, "mcp.json"));

    public string SettingsMode => File.ReadAllText(Path.Combine(Dir, "settings.mode")).Trim();

    public string McpMode => File.ReadAllText(Path.Combine(Dir, "mcp.mode")).Trim();

    public string SettingsPath => File.ReadAllText(Path.Combine(Dir, "settings.path")).Trim();

    public string McpPath => File.ReadAllText(Path.Combine(Dir, "mcp.path")).Trim();

    public IReadOnlyDictionary<string, bool> EnvSet =>
        File.ReadAllLines(Path.Combine(Dir, "env")).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1] == "set");

    public string Capability => JsonNode.Parse(McpConfig)!["mcpServers"]!["cfgdash"]!["env"]!["CFGDASH_MCP_CAPABILITY"]!.GetValue<string>();

    public IReadOnlyList<string> Stdin => File.Exists(Path.Combine(Dir, "stdin")) ? File.ReadAllLines(Path.Combine(Dir, "stdin")) : [];

    public IReadOnlyList<string> StdinKinds => [.. Stdin.Select(l => JsonNode.Parse(l)?["type"]?.GetValue<string>() ?? string.Empty)];
}

/// <summary>A bash script named <c>claude</c> in a temp bin dir that records how it was started and plays a scripted stream-json session. Linux only.</summary>
internal sealed class FakeClaude : IDisposable
{
    private static readonly string[] HelpFlags =
    [
        "--print", "--input-format", "--output-format", "--include-partial-messages", "--verbose", "--no-session-persistence",
        "--setting-sources", "--settings", "--mcp-config", "--strict-mcp-config", "--tools", "--allowedTools", "--permission-mode",
        "--disable-slash-commands", "--max-budget-usd", "--model", "--effort", "--append-system-prompt",
    ];

    private readonly TempDir temp = new();

    public FakeClaude()
    {
        BinDir = temp.Sub("bin");
        RecordDir = temp.Sub("records");
        File.WriteAllText(Path.Combine(BinDir, "claude"), Script.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(BinDir, "claude"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Configure();
    }

    public string BinDir { get; }

    public string RecordDir { get; }

    public IReadOnlyList<FakeRun> Runs =>
        [.. Directory.GetDirectories(RecordDir, "run-*").OrderBy(d => int.Parse(Path.GetFileName(d)[4..])).Select(d => new FakeRun(d))];

    /// <summary>Rewrites the fake's own config; every run re-reads it.</summary>
    public void Configure(
        bool loggedIn = true,
        string? helpOmit = null,
        FakeInit init = FakeInit.Good,
        int pluginFails = 1,
        FakeTurn turn = FakeTurn.Normal,
        bool grandchild = false,
        bool deltaAfterInit = false,
        string splitA = "",
        string splitB = "")
    {
        var lines = new[]
        {
            $"REC='{RecordDir}'",
            $"AUTH={(loggedIn ? "true" : "false")}",
            $"HELP_OMIT='{helpOmit}'",
            $"HELP_FLAGS='{string.Join(' ', HelpFlags)}'",
            $"INIT={init.ToString().ToLowerInvariant()}",
            $"PLUGIN_FAILS={pluginFails}",
            $"TURN={turn.ToString().ToLowerInvariant()}",
            $"GRANDCHILD={(grandchild ? 1 : 0)}",
            $"DELTA_AFTER_INIT={(deltaAfterInit ? 1 : 0)}",
            $"SPLIT_A='{splitA}'",
            $"SPLIT_B='{splitB}'",
        };
        File.WriteAllText(Path.Combine(BinDir, "fake.conf"), string.Join('\n', lines) + "\n");
    }

    public async Task<FakeRun> WaitRunAsync(int number)
    {
        Assert.True(await Poll.UntilAsync(() => Runs.Count >= number && Runs[number - 1].Ready), $"fake claude run {number} did not start");
        return Runs[number - 1];
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..][0] != 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static Task<bool> DiedAsync(int pid) => Poll.UntilAsync(() => !IsAlive(pid));

    /// <summary>Kills any process the fake recorded that is still alive.</summary>
    public void KillLeftovers()
    {
        foreach (var run in Runs)
        {
            foreach (var pid in new[] { TryPid(run.Dir, "pid"), TryPid(run.Dir, "grandchild.pid") }.OfType<int>())
            {
                if (IsAlive(pid))
                {
                    try
                    {
                        Process.GetProcessById(pid).Kill();
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                    {
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        KillLeftovers();
        temp.Dispose();
    }

    private static int? TryPid(string dir, string name)
    {
        try
        {
            return int.Parse(File.ReadAllText(Path.Combine(dir, name)).Trim());
        }
        catch (Exception e) when (e is IOException or FormatException)
        {
            return null;
        }
    }

    private const string Script = """
        #!/bin/bash
        here="$(cd "$(dirname "$0")" && pwd)"
        . "$here/fake.conf"
        case "$1" in
          --version) echo "2.1.316 (Claude Code)"; exit 0;;
          --help)
            for f in $HELP_FLAGS; do
              [ "$f" = "$HELP_OMIT" ] || echo "  $f <value>   option"
            done
            exit 0;;
          auth)
            printf '{"loggedIn": %s, "authMethod": "oauth", "email": "user%sexample.com"}\n' "$AUTH" "@"
            exit 0;;
        esac
        n=1
        while ! mkdir "$REC/run-$n" 2>/dev/null; do n=$((n+1)); done
        d="$REC/run-$n"
        printf '%s\0' "$@" > "$d/argv"
        pwd -P > "$d/cwd"
        echo $$ > "$d/pid"
        for v in CFGDASH_MCP_CAPABILITY CLAUDE_AGENT_SDK_DISABLE_BUILTIN_AGENTS CLAUDE_CODE_DISABLE_CLAUDE_MDS CLAUDE_CODE_DISABLE_AUTO_MEMORY CLAUDE_CODE_DISABLE_GIT_INSTRUCTIONS; do
          if [ -n "${!v+x}" ]; then echo "$v=set"; else echo "$v=unset"; fi
        done > "$d/env"
        for a in "$@"; do
          case "$a" in
            --settings=*) p="${a#--settings=}"; echo "$p" > "$d/settings.path"; cp "$p" "$d/settings.json"; stat -c %a "$p" > "$d/settings.mode";;
            --mcp-config=*) p="${a#--mcp-config=}"; echo "$p" > "$d/mcp.path"; cp "$p" "$d/mcp.json"; stat -c %a "$p" > "$d/mcp.mode";;
          esac
        done
        if [ "$GRANDCHILD" = 1 ]; then
          sleep 300 </dev/null >/dev/null 2>&1 &
          echo $! > "$d/grandchild.pid"
        fi
        touch "$d/ready"
        T='"mcp__cfgdash__'
        TOOLS="[${T}get_snapshot\",${T}get_findings\",${T}get_file\",${T}search\",${T}propose_change\",${T}propose_file_edit\"]"
        SERVERS='[{"name":"cfgdash","status":"connected"}]'
        init() {
          local tools="$TOOLS" plugins='[]'
          case "$INIT" in
            badtool) tools="[${T}get_snapshot\",${T}get_findings\",${T}get_file\",${T}search\",${T}propose_change\",${T}propose_file_edit\",\"Bash\"]";;
            badplugin) if [ "$n" -le "$PLUGIN_FAILS" ]; then plugins='[{"name":"x","path":"/x","source":"p@m"}]'; fi;;
          esac
          printf '{"type":"system","subtype":"init","tools":%s,"mcp_servers":%s,"agents":[],"skills":[],"slash_commands":[],"plugins":%s}\n' "$tools" "$SERVERS" "$plugins"
        }
        delta() { printf '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"%s"}}}\n' "$1"; }
        spent=0
        result() { spent=$((spent+25)); printf '{"type":"result","subtype":"success","is_error":false,"total_cost_usd":%d.%02d,"result":""}\n' $((spent/100)) $((spent%100)); }
        first=1
        while IFS= read -r line; do
          echo "$line" >> "$d/stdin"
          if [ "$first" = 1 ]; then
            first=0
            if [ "$INIT" = none ]; then sleep 300; exit 0; fi
            init
            if [ "$DELTA_AFTER_INIT" = 1 ]; then delta 'leaked text\n'; fi
          fi
          case "$TURN" in
            normal) delta 'hello\n'; result;;
            splitsecret) delta "$SPLIT_A"; delta "$SPLIT_B"; result;;
            stall)
              delta 'partial\n'
              end=$((SECONDS+300))
              while [ $SECONDS -lt $end ]; do
                IFS= read -r -t 1 extra; rc=$?
                if [ $rc = 0 ]; then echo "$extra" >> "$d/stdin"; elif [ $rc -le 128 ]; then break; fi
              done;;
            controlrequest)
              printf '{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash"}}\n'
              delta 'hello\n'; result;;
            bigline) head -c 1200000 /dev/zero | tr '\0' a; echo;;
            errorresult) printf '{"type":"result","subtype":"error_during_execution","is_error":true,"total_cost_usd":0.01,"result":"API Error: 529 Overloaded key %s%s"}\n' AKIAABCDEF GHIJKLMNOP;;
          esac
        done
        """;
}

internal sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only";
        }
    }
}
