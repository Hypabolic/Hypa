using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Caller pane target, pane run, and pane wait-output against a test mux.
/// </summary>
public class CallerPaneCommandTests
{
    [Fact]
    public void Pane_run_preserves_dashed_command_tail()
    {
        var direct = ControlPlaneCliCommands.Parse(
            ["pane", "run", "p1", "dotnet", "test", "--no-restore"]);
        Assert.Equal("dotnet test --no-restore", direct.PaneRunCommand);

        var delimited = ControlPlaneCliCommands.Parse(
            ["pane", "run", "p1", "--", "dotnet", "test", "--no-restore"]);
        Assert.Equal("dotnet test --no-restore", delimited.PaneRunCommand);

        var current = ControlPlaneCliCommands.Parse(
            ["pane", "run", "--current", "dotnet", "test", "--no-restore"]);
        Assert.Equal("dotnet test --no-restore", current.PaneRunCommand);
        Assert.Contains("--current", current.Switches);

        var unknown = Assert.Throws<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.Parse(
                ["pane", "split", "p1", "--direction", "right", "--no-restore"]));
        Assert.Contains("Unknown flag", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wait_output_omitted_timeout_has_no_client_deadline()
    {
        var omitted = ControlPlaneCliCommands.Parse(
            ["pane", "wait-output", "p1", "--match", "ready"]);
        var omittedBody = ControlPlaneCliCommands.BuildPaneWaitOutputParams(
            omitted, omitted.Tokens.Skip(1).ToArray());
        Assert.Null(omittedBody["timeout_ms"]);
        Assert.Equal(
            Timeout.InfiniteTimeSpan,
            ControlPlaneCliCommands.PaneWaitOutputClientDeadline(omitted, omittedBody));

        var explicitTimeout = ControlPlaneCliCommands.Parse(
        [
            "--timeout-ms", "500",
            "pane", "wait-output", "p1",
            "--match", "ready",
            "--timeout", "120000",
        ]);
        var explicitBody = ControlPlaneCliCommands.BuildPaneWaitOutputParams(
            explicitTimeout, explicitTimeout.Tokens.Skip(1).ToArray());
        var deadline = ControlPlaneCliCommands.PaneWaitOutputClientDeadline(
            explicitTimeout, explicitBody);
        Assert.True(deadline > TimeSpan.FromMilliseconds(120_000), deadline.ToString());
    }

    [Fact]
    public async Task Fixture_requests_serialize_and_dispatch()
    {
        var currentRequest = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(ProtocolMethods.PaneCurrent)),
            ProtocolJsonContext.Default.RpcRequest);
        var currentParams = JsonSerializer.Deserialize(
            currentRequest!.Params!.Value,
            ProtocolJsonContext.Default.PaneCurrentParams);
        Assert.Equal("p_1", currentParams!.CallerPaneId);

        var waitRequest = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(ProtocolMethods.PaneWaitForOutput)),
            ProtocolJsonContext.Default.RpcRequest);
        var waitParams = JsonSerializer.Deserialize(
            waitRequest!.Params!.Value,
            ProtocolJsonContext.Default.PaneWaitForOutputParams);
        Assert.Equal("ready", waitParams!.Match);
        Assert.Equal(5000, waitParams.TimeoutMs);

        var runRequest = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.PaneSendInputCombinedRequest),
            ProtocolJsonContext.Default.RpcRequest);
        var runParams = JsonSerializer.Deserialize(
            runRequest!.Params!.Value,
            ProtocolJsonContext.Default.PaneSendInputParams);
        Assert.Equal(ProtocolMethods.PaneSendInput, runRequest.Method);
        Assert.Equal("ls", runParams!.Text);
        Assert.Equal("enter", Assert.Single(runParams.Keys!));

        var factory = TestPaneFactories.Scripted();
        var state = new AppState(SessionId.New("caller-pane-fixtures"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector());
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "hypa-fixture-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var created = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonSerializer.SerializeToElement(
                    new WorkspaceCreateParams
                    {
                        Cwd = dir,
                        Label = "fixture",
                        Command = "fixture",
                        CreatePane = true,
                    },
                    ProtocolJsonContext.Default.WorkspaceCreateParams),
                CancellationToken.None);
            var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString();
            Assert.False(string.IsNullOrEmpty(paneId));
            var runtime = Assert.Single(factory.Created);
            runtime.FireOutput("noise\nready\nmore\n");

            var current = await cp.DispatchAsync(
                ProtocolMethods.PaneCurrent,
                JsonSerializer.SerializeToElement(
                    currentParams with { CallerPaneId = paneId },
                    ProtocolJsonContext.Default.PaneCurrentParams),
                CancellationToken.None);
            Assert.Equal(paneId, current.GetProperty("pane_id").GetString());
            AssertFixtureResultKeys(FixtureCatalog.MethodResponsePath(ProtocolMethods.PaneCurrent), current);

            var waited = await cp.DispatchAsync(
                ProtocolMethods.PaneWaitForOutput,
                JsonSerializer.SerializeToElement(
                    waitParams with { PaneId = paneId },
                    ProtocolJsonContext.Default.PaneWaitForOutputParams),
                CancellationToken.None);
            Assert.True(waited.GetProperty("matched").GetBoolean(), waited.GetRawText());
            Assert.Contains("ready", waited.GetProperty("text").GetString(), StringComparison.Ordinal);
            AssertFixtureResultKeys(
                FixtureCatalog.MethodResponsePath(ProtocolMethods.PaneWaitForOutput), waited);

            var conn = new DispatchConnection("fixture");
            var claim = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                JsonSerializer.SerializeToElement(
                    new LeaseClaimParams
                    {
                        PaneId = paneId,
                        Scope = LeaseScopes.Input,
                        TtlMs = 30_000,
                    },
                    ProtocolJsonContext.Default.LeaseClaimParams),
                conn,
                CancellationToken.None);
            var leaseId = claim.GetProperty("lease_id").GetString();
            Assert.False(string.IsNullOrEmpty(leaseId));

            var sent = await cp.DispatchAsync(
                ProtocolMethods.PaneSendInput,
                JsonSerializer.SerializeToElement(
                    runParams with { PaneId = paneId, LeaseId = leaseId },
                    ProtocolJsonContext.Default.PaneSendInputParams),
                conn,
                CancellationToken.None);
            Assert.True(sent.GetProperty("ok").GetBoolean(), sent.GetRawText());
            Assert.Equal(paneId, sent.GetProperty("pane_id").GetString());
            AssertFixtureResultKeys(FixtureCatalog.MethodResponsePath(ProtocolMethods.PaneSendInput), sent);
            var written = Assert.Single(runtime.Writes);
            Assert.StartsWith("ls", Encoding.UTF8.GetString(written), StringComparison.Ordinal);
            Assert.True(written.Length > runParams.Text!.Length);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Current_flag_without_pane_id_exits_usage_and_does_not_connect()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var missing = Path.Combine(Path.GetTempPath(), "hypa-absent-" + Guid.NewGuid().ToString("N") + ".sock");
        var env = new Dictionary<string, string?>
        {
            [PaneIdEnvironment.HypaPaneId] = null,
            ["HYPA_RUNTIME_SOCKET"] = null,
        };
        var (code, stdout, stderr) = await HypaCliProcess.RunAsync(
            env,
            "--socket", missing,
            "pane", "split", "--current", "--direction", "right");
        Assert.Equal(4, code);
        Assert.Contains("HYPA_PANE_ID is not set", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to connect", stderr, StringComparison.Ordinal);
        Assert.Equal("", stdout);
    }

    [SkippableFact]
    public async Task Child_process_pane_current_returns_its_pane()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        await UsingMuxAsync(async (sock, dir) =>
        {
            var scriptPath = Path.Combine(dir, "current.sh");
            await File.WriteAllTextAsync(scriptPath, CurrentPaneScript(HypaCliProcess.CliPath(), sock) + "\n");
            var created = await CliAsync(
                sock,
                "workspace", "create", "--cwd", dir,
                "--command", "/bin/sh", "--args", scriptPath);
            var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString();
            Assert.False(string.IsNullOrEmpty(paneId));

            var text = await ReadUntilAsync(sock, paneId!, paneId!);
            using var doc = JsonDocument.Parse(ExtractJson(text));
            Assert.Equal(paneId, doc.RootElement.GetProperty("pane_id").GetString());
        });
    }

    [SkippableFact]
    public async Task Pane_run_submits_text_and_enter_once()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        await UsingMuxAsync(async (sock, dir) =>
        {
            var marker = Path.Combine(dir, "once.txt");
            var shell = await WriteLineShellAsync(dir);
            var created = await CliAsync(
                sock,
                "workspace", "create", "--cwd", dir, "--command", "/bin/bash", "--args", shell);
            var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString()!;
            var command = "printf RAN_ONCE > " + marker;
            var ran = await CliAsync(sock, "pane", "run", paneId, command);
            Assert.True(ran.GetProperty("ok").GetBoolean(), ran.GetRawText());
            Assert.Equal(command.Length + 1, ran.GetProperty("accepted_bytes").GetInt32());

            var body = await WaitForFileAsync(marker);
            Assert.Equal("RAN_ONCE", body);
        });
    }

    [SkippableFact]
    public async Task Pane_run_keeps_dashed_arguments()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        await UsingMuxAsync(async (sock, dir) =>
        {
            var shell = await WriteLineShellAsync(dir);
            var created = await CliAsync(
                sock,
                "workspace", "create", "--cwd", dir, "--command", "/bin/bash", "--args", shell);
            var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString()!;
            var ran = await CliAsync(
                sock,
                "pane", "run", paneId,
                "printf", "%s", "--no-restore", ">", "dash.txt");
            var command = "printf %s --no-restore > dash.txt";
            Assert.Equal(command.Length + 1, ran.GetProperty("accepted_bytes").GetInt32());
            Assert.Equal("--no-restore", await WaitForFileAsync(Path.Combine(dir, "dash.txt")));

            await CliAsync(
                sock,
                "pane", "run", paneId,
                "--", "printf", "%s", "--delimited", ">", "delim.txt");
            Assert.Equal("--delimited", await WaitForFileAsync(Path.Combine(dir, "delim.txt")));
        });
    }

    [SkippableFact]
    public async Task Wait_output_match_sees_existing_and_later_output()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        await UsingMuxAsync(async (sock, dir) =>
        {
            var shell = await WriteLineShellAsync(dir);
            var created = await CliAsync(
                sock,
                "workspace", "create", "--cwd", dir, "--command", "/bin/bash", "--args", shell);
            var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString()!;
            await CliAsync(sock, "pane", "run", paneId, "printf BEFORE_MARK");
            var seen = await ReadUntilAsync(sock, paneId, "BEFORE_MARK");
            Assert.Contains("BEFORE_MARK", seen, StringComparison.Ordinal);

            var before = Stopwatch.StartNew();
            var matched = await CliAsync(
                sock,
                "--timeout-ms", "5000",
                "pane", "wait-output", paneId,
                "--match", "BEFORE_MARK",
                "--timeout", "3000");
            before.Stop();
            Assert.True(matched.GetProperty("matched").GetBoolean(), matched.GetRawText());
            Assert.Contains("BEFORE_MARK", matched.GetProperty("text").GetString(), StringComparison.Ordinal);
            Assert.True(before.Elapsed < TimeSpan.FromSeconds(2), before.Elapsed.ToString());

            var later = CliAsync(
                sock,
                "--timeout-ms", "8000",
                "pane", "wait-output", paneId,
                "--match", "AFTER_MARK",
                "--timeout", "6000");
            await Task.Delay(200);
            await CliAsync(sock, "pane", "run", paneId, "printf AFTER_MARK");
            var after = await later;
            Assert.True(after.GetProperty("matched").GetBoolean(), after.GetRawText());
            Assert.Contains("AFTER_MARK", after.GetProperty("text").GetString(), StringComparison.Ordinal);
        });
    }

    [SkippableFact]
    public async Task Wait_output_without_timeout_survives_past_client_budget()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        await UsingMuxAsync(async (sock, dir) =>
        {
            var shell = await WriteLineShellAsync(dir);
            var created = await CliAsync(
                sock,
                "workspace", "create", "--cwd", dir, "--command", "/bin/bash", "--args", shell);
            var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString()!;
            var later = CliAsync(
                sock,
                "--timeout-ms", "500",
                "pane", "wait-output", paneId,
                "--match", "LATE_MARK");
            await Task.Delay(1500);
            await CliAsync(sock, "pane", "run", paneId, "printf LATE_MARK");
            var after = await later;
            Assert.True(after.GetProperty("matched").GetBoolean(), after.GetRawText());
            Assert.Contains("LATE_MARK", after.GetProperty("text").GetString(), StringComparison.Ordinal);
        });
    }

    [SkippableFact]
    public async Task Wait_output_regex_matches_and_timeout_returns_timeout()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        await UsingMuxAsync(async (sock, dir) =>
        {
            var shell = await WriteLineShellAsync(dir);
            var created = await CliAsync(
                sock,
                "workspace", "create", "--cwd", dir, "--command", "/bin/bash", "--args", shell);
            var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString()!;
            await CliAsync(sock, "pane", "run", paneId, "printf REGEX_MARK");
            await ReadUntilAsync(sock, paneId, "REGEX_MARK");

            var matched = await CliAsync(
                sock,
                "--timeout-ms", "5000",
                "pane", "wait-output", paneId,
                "--regex", "REGEX_M.RK",
                "--timeout", "3000");
            Assert.True(matched.GetProperty("matched").GetBoolean(), matched.GetRawText());
            Assert.Contains("REGEX_MARK", matched.GetProperty("text").GetString(), StringComparison.Ordinal);

            await CliAsync(sock, "pane", "run", paneId, "printf 'noise\\nTOKEN\\nmore\\n'");
            var seen = await ReadUntilAsync(sock, paneId, "TOKEN");
            Assert.Contains("noise", seen, StringComparison.Ordinal);
            var anchored = await CliAsync(
                sock,
                "--timeout-ms", "5000",
                "pane", "wait-output", paneId,
                "--regex", "^TOKEN$",
                "--timeout", "3000");
            Assert.True(anchored.GetProperty("matched").GetBoolean(), anchored.GetRawText());
            var anchoredText = anchored.GetProperty("text").GetString() ?? "";
            Assert.Contains("TOKEN", anchoredText, StringComparison.Ordinal);
            Assert.Contains("noise", anchoredText, StringComparison.Ordinal);

            var (code, stdout, stderr) = await HypaCliProcess.RunAsync(
                "--socket", sock,
                "--timeout-ms", "5000",
                "pane", "wait-output", paneId,
                "--match", "NO_SUCH_OUTPUT_TOKEN",
                "--timeout", "300");
            Assert.Equal(1, code);
            Assert.Contains(PaneWaitForOutputErrors.TimeoutMessage, stderr, StringComparison.Ordinal);
            Assert.Contains("(" + PaneWaitForOutputErrors.Timeout + ")", stderr, StringComparison.Ordinal);
            Assert.Equal("", stdout);
        });
    }

    [SkippableFact]
    public async Task Command_recipe_splits_runs_waits_and_reads()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        await UsingMuxAsync(async (sock, dir) =>
        {
            var created = await CliAsync(
                sock,
                "workspace", "create", "--cwd", dir, "--command", "/bin/sleep", "--args", "180");
            var parentId = created.GetProperty("pane").GetProperty("pane_id").GetString()!;
            var tabId = created.GetProperty("pane").GetProperty("tab_id").GetString()!;

            var split = await CliAsPaneAsync(
                sock,
                parentId,
                "pane", "split", "--current",
                "--direction", "right",
                "--no-focus",
                "--command", "/bin/bash",
                "--args", await WriteLineShellAsync(dir));
            var childId = split.GetProperty("pane_id").GetString();
            Assert.False(string.IsNullOrEmpty(childId));
            Assert.NotEqual(parentId, childId);

            var tab = await CliAsync(sock, "tab", "get", tabId);
            Assert.Equal(parentId, tab.GetProperty("focused_pane_id").GetString());

            await CliAsync(sock, "pane", "run", childId!, "printf RECIPE_MARK");
            var waited = await CliAsync(
                sock,
                "--timeout-ms", "8000",
                "pane", "wait-output", childId!,
                "--match", "RECIPE_MARK",
                "--timeout", "6000");
            Assert.True(waited.GetProperty("matched").GetBoolean(), waited.GetRawText());

            var read = await CliAsync(
                sock,
                "pane", "read", childId!,
                "--source", "recent-unwrapped",
                "--lines", "120");
            Assert.Contains("RECIPE_MARK", read.GetProperty("text").GetString(), StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Bash that runs each submitted line when Enter arrives.
    /// Process-io has no terminal line discipline, so this shell treats CR as Enter.
    /// </summary>
    private static async Task<string> WriteLineShellAsync(string dir)
    {
        var path = Path.Combine(dir, "line-shell.sh");
        await File.WriteAllTextAsync(path, """
            line=""
            while IFS= read -r -n 1 ch; do
              if [ "$ch" = $'\r' ] || [ "$ch" = $'\n' ]; then
                if [ -n "$line" ]; then
                  eval "$line"
                fi
                line=""
              else
                line="${line}${ch}"
              fi
            done
            """);
        return path;
    }

    private static string CurrentPaneScript(string cli, string sock)
    {
        var command = cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? "dotnet exec " + Quote(cli)
            : Quote(cli);
        return command + " --socket " + Quote(sock) + " pane current --current";
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        Assert.True(start >= 0 && end > start, text);
        return text[start..(end + 1)];
    }

    private static async Task<string> WaitForFileAsync(string path)
    {
        for (var i = 0; i < 40; i++)
        {
            if (File.Exists(path))
            {
                var body = await File.ReadAllTextAsync(path);
                if (body.Length > 0)
                    return body;
            }

            await Task.Delay(50);
        }

        Assert.Fail("command output file was not written: " + path);
        return "";
    }

    private static async Task<string> ReadUntilAsync(string sock, string paneId, string needle)
    {
        JsonElement last = default;
        for (var i = 0; i < 40; i++)
        {
            last = await CliAsync(
                sock,
                "pane", "read", paneId,
                "--source", "recent-unwrapped",
                "--lines", "120");
            var text = last.GetProperty("text").GetString() ?? "";
            if (text.Contains(needle, StringComparison.Ordinal))
                return text;
            await Task.Delay(50);
        }

        return last.ValueKind == JsonValueKind.Undefined ? "" : last.GetProperty("text").GetString() ?? "";
    }

    private static async Task UsingMuxAsync(Func<string, string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-pane-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var sock = Path.Combine(dir, "s.sock");
        var state = new AppState(SessionId.New("caller-pane"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Create(intel),
            intel,
            new HeuristicAgentDetector(),
            runtimeSocketPath: sock);
        await using var server = new UnixSocketServer(cp, sock);
        try
        {
            await server.StartAsync(CancellationToken.None);
            await body(sock, dir);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            try { Directory.Delete(dir, recursive: true); } catch { /* teardown */ }
        }
    }

    private static Task<JsonElement> CliAsPaneAsync(string sock, string paneId, params string[] args) =>
        CliAsync(
            sock,
            new Dictionary<string, string?> { [PaneIdEnvironment.HypaPaneId] = paneId },
            args);

    private static Task<JsonElement> CliAsync(string sock, params string[] args) =>
        CliAsync(sock, environment: null, args);

    private static async Task<JsonElement> CliAsync(
        string sock,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] args)
    {
        var argv = new List<string> { "--session", "default", "--socket", sock };
        argv.AddRange(args);
        var (code, stdout, stderr) = environment is null
            ? await HypaCliProcess.RunAsync(argv.ToArray())
            : await HypaCliProcess.RunAsync(environment, argv.ToArray());
        Assert.True(code == 0, stderr + stdout);
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.Clone();
    }

    private static void AssertFixtureResultKeys(string fixturePath, JsonElement live)
    {
        var response = JsonSerializer.Deserialize(
            FixtureCatalog.Load(fixturePath),
            ProtocolJsonContext.Default.RpcResponse);
        Assert.NotNull(response?.Result);
        foreach (var property in response.Result.Value.EnumerateObject())
            Assert.True(live.TryGetProperty(property.Name, out _), property.Name);
    }

    private sealed class DispatchConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;
        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }
}
