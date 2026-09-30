using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Pane children receive the CLI path and the mux socket.
/// Popup children keep the identity strip.
/// A custom agent report follows source and sequence rules.
/// </summary>
[Collection("ProcessSpawnTests")]
public sealed class PaneChildEnvironmentTests
{
    [SkippableFact]
    public async Task Pane_child_sees_executable_cli_path()
    {
        Skip.If(!Unix(), "Unix pane children only.");
        using var fx = Fixture.Create();
        var cli = fx.WriteCli();
        await using var plane = await fx.StartAsync(cliProcessPath: cli);
        var seen = await plane.SpawnDumpAsync();
        Assert.Equal(Path.GetFullPath(cli), seen.Bin);
        AssertExecutableHypa(seen.Bin);
    }

    [SkippableFact]
    public async Task Other_mux_name_still_sets_cli_path_on_pane_and_plugin()
    {
        Skip.If(!Unix(), "Unix pane children only.");
        using var fx = Fixture.Create();
        var cli = fx.WriteCli();
        var mux = Path.Combine(fx.Dir, "hypa-runtime");
        File.WriteAllText(mux, "");
        await using var plane = await fx.StartAsync(cliProcessPath: mux);
        var seen = await plane.SpawnDumpAsync();
        var expected = Path.GetFullPath(cli);
        Assert.Equal(expected, seen.Bin);
        Assert.Equal(expected, await plane.SpawnPluginDumpAsync());
        AssertExecutableHypa(seen.Bin);
    }

    [SkippableFact]
    public async Task Pane_child_sees_runtime_socket_and_connects()
    {
        Skip.If(!Unix(), "Unix domain sockets only.");
        using var fx = Fixture.Create();
        var cli = fx.WriteCli();
        await using var plane = await fx.StartAsync(cliProcessPath: cli, listen: true);
        var seen = await plane.SpawnDumpAsync();
        Assert.Equal(fx.SocketPath, seen.Socket);
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        client.Connect(new UnixDomainSocketEndPoint(seen.Socket));
        Assert.True(client.Connected);
    }

    [SkippableFact]
    public async Task Popup_child_omits_pane_id_and_token()
    {
        Skip.If(!Unix(), "Unix pane children only.");
        using var fx = Fixture.Create();
        var cli = fx.WriteCli();
        await using var plane = await fx.StartAsync(cliProcessPath: cli);
        var seen = await plane.SpawnPopupDumpAsync();
        Assert.Equal("absent", seen.Pane);
        Assert.Equal("absent", seen.Token);
    }

    [SkippableFact]
    public async Task Custom_agent_report_ignores_stale_sequence_then_releases()
    {
        Skip.If(!Unix(), "Unix domain sockets only.");
        using var fx = Fixture.Create();
        await using var plane = await fx.StartAsync(listen: true);
        var created = await CliAsync(
            fx.SocketPath,
            "workspace", "create",
            "--cwd", fx.Dir,
            "--command", "/bin/sleep",
            "--args", "180");
        var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString();
        Assert.False(string.IsNullOrEmpty(paneId));

        var report = await CliAsync(
            fx.SocketPath,
            "pane", "report-agent", paneId!,
            "--source", "custom:my-agent",
            "--agent", "my-agent",
            "--state", "working",
            "--seq", "2");
        Assert.True(report.GetProperty("ok").GetBoolean());

        var listed = await CliAsync(fx.SocketPath, "pane", "get", paneId!);
        Assert.Equal("working", listed.GetProperty("state").GetString());
        Assert.Equal("my-agent", listed.GetProperty("agent").GetString());

        var stale = await CliAsync(
            fx.SocketPath,
            "pane", "report-agent", paneId!,
            "--source", "custom:my-agent",
            "--agent", "my-agent",
            "--state", "blocked",
            "--message", "need a decision",
            "--seq", "1");
        Assert.True(stale.GetProperty("ok").GetBoolean());

        var after = await CliAsync(fx.SocketPath, "pane", "get", paneId!);
        Assert.Equal("working", after.GetProperty("state").GetString());
        Assert.DoesNotContain("need a decision", after.GetRawText(), StringComparison.Ordinal);

        var released = await CliAsync(
            fx.SocketPath,
            "pane", "release-agent", paneId!,
            "--source", "custom:my-agent",
            "--agent", "my-agent",
            "--seq", "3");
        Assert.True(released.GetProperty("ok").GetBoolean());

        var pane = plane.State.GetPane(new PaneId(paneId!));
        Assert.NotNull(pane);
        Assert.Null(pane.AgentAuthority);
    }

    private static bool Unix() => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static string TomlBasic(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void AssertExecutableHypa(string path)
    {
        Assert.Equal("hypa", Path.GetFileName(path));
        Assert.True(File.Exists(path), path);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var mode = File.GetUnixFileMode(path);
            var exec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            Assert.NotEqual(UnixFileMode.None, mode & exec);
        }

        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi);
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task<JsonElement> CliAsync(string sock, params string[] args)
    {
        var argv = new List<string> { "--session", "default", "--socket", sock };
        argv.AddRange(args);
        var (code, stdout, stderr) = await HypaCliProcess.RunAsync(argv.ToArray());
        Assert.True(code == 0, stderr + stdout);
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.Clone();
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string dir) => Dir = dir;

        public string Dir { get; }

        public string SocketPath => Path.Combine(Dir, "s.sock");

        public static Fixture Create()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hypa-penv-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(
                    dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return new Fixture(dir);
        }

        public string WriteCli()
        {
            var path = Path.Combine(Dir, "hypa");
            File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
            MarkExecutable(path);
            return path;
        }

        public string WriteDump()
        {
            var path = Path.Combine(Dir, "dump.sh");
            File.WriteAllText(path, """
                #!/bin/sh
                prefix=$1
                printf '%s' "${HYPA_BIN_PATH-}" > "${prefix}.bin"
                printf '%s' "${HYPA_RUNTIME_SOCKET-}" > "${prefix}.sock"
                if [ -n "${HYPA_PANE_ID+x}" ]; then
                  printf '%s' "$HYPA_PANE_ID" > "${prefix}.pane"
                else
                  printf 'absent' > "${prefix}.pane"
                fi
                if [ -n "${HYPA_PANE_TOKEN+x}" ]; then
                  printf '%s' "$HYPA_PANE_TOKEN" > "${prefix}.token"
                else
                  printf 'absent' > "${prefix}.token"
                fi
                """);
            MarkExecutable(path);
            return path;
        }

        private static void MarkExecutable(string path)
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public async Task<Plane> StartAsync(string? cliProcessPath = null, bool listen = false)
        {
            var state = new AppState(SessionId.New("penv-" + Guid.NewGuid().ToString("N")[..8]));
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
                runtimeSocketPath: listen ? SocketPath : null,
                cliProcessPath: cliProcessPath,
                pluginConfigRoot: Path.Combine(Dir, "plugin-config"));
            UnixSocketServer? server = null;
            if (listen)
            {
                server = new UnixSocketServer(cp, SocketPath);
                await server.StartAsync(CancellationToken.None);
            }

            return new Plane(this, state, cp, server);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Dir))
                    Directory.Delete(Dir, recursive: true);
            }
            catch
            {
                // The plane disposes the listener before this directory delete.
            }
        }
    }

    private sealed class Plane : IAsyncDisposable
    {
        private readonly Fixture _fx;
        private readonly UnixSocketServer? _server;

        public Plane(Fixture fx, AppState state, ControlPlaneService service, UnixSocketServer? server)
        {
            _fx = fx;
            State = state;
            Service = service;
            _server = server;
        }

        public AppState State { get; }

        public ControlPlaneService Service { get; }

        public async Task<Dump> SpawnDumpAsync()
        {
            var script = _fx.WriteDump();
            var prefix = Path.Combine(_fx.Dir, "child");
            var created = await DispatchAsync(ProtocolMethods.WorkspaceCreate, new JsonObject
            {
                ["cwd"] = _fx.Dir,
                ["command"] = script,
                ["args"] = new JsonArray(prefix),
                ["create_pane"] = true,
            });
            Assert.False(string.IsNullOrEmpty(created.GetProperty("pane").GetProperty("pane_id").GetString()));
            return await ReadDumpAsync(prefix);
        }

        public async Task<string> SpawnPluginDumpAsync()
        {
            var script = _fx.WriteDump();
            var prefix = Path.Combine(_fx.Dir, "plugin-child");
            var pluginDir = Path.Combine(_fx.Dir, "env-dump-plugin");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(
                Path.Combine(pluginDir, PluginHostService.ManifestFileName),
                "id = \"env.dump\"\n"
                + "name = \"Env dump\"\n"
                + "version = \"0.1.0\"\n"
                + "min_hypa_version = \"0.1.0\"\n"
                + "platforms = [\"linux\", \"macos\"]\n"
                + "\n"
                + "[[actions]]\n"
                + "id = \"dump\"\n"
                + "title = \"Dump\"\n"
                + "command = [" + TomlBasic(script) + ", " + TomlBasic(prefix) + "]\n");
            var linked = Service.Plugins.Link(pluginDir, true);
            Assert.True(linked.IsOk, linked.IsOk ? "" : linked.Error.Message);
            var invoked = Service.Plugins.InvokeAction("dump", "env.dump", null);
            Assert.True(invoked.IsOk, invoked.IsOk ? "" : invoked.Error.Message);
            var binPath = prefix + ".bin";
            for (var i = 0; i < 50; i++)
            {
                if (File.Exists(binPath))
                {
                    var logs = Service.Plugins.ListLogs("env.dump", 5);
                    if (logs.IsOk && logs.Value.Any(log => log.Status != "running"))
                        return await File.ReadAllTextAsync(binPath);
                }

                await Task.Delay(50);
            }

            var detail = Service.Plugins.ListLogs("env.dump", 5);
            var message = detail.IsOk
                ? string.Join(
                    "; ",
                    detail.Value.Select(log => log.Status + " " + log.Error + " " + log.Stderr))
                : detail.Error.Message;
            Assert.Fail("plugin child did not write HYPA_BIN_PATH. " + message);
            return "";
        }

        public async Task<Dump> SpawnPopupDumpAsync()
        {
            var script = _fx.WriteDump();
            var prefix = Path.Combine(_fx.Dir, "popup");
            _ = await DispatchAsync(ProtocolMethods.PopupOpen, new JsonObject
            {
                ["command"] = script,
                ["args"] = new JsonArray(prefix),
                ["cwd"] = _fx.Dir,
                ["area_cols"] = 80,
                ["area_rows"] = 24,
                ["env"] = new JsonObject
                {
                    ["HYPA_PANE_ID"] = "pane-from-caller",
                    ["HYPA_PANE_TOKEN"] = "token-from-caller",
                    ["HYPA_TAB_ID"] = "tab-from-caller",
                    ["HYPA_WORKSPACE_ID"] = "ws-from-caller",
                },
            });
            return await ReadDumpAsync(prefix);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.ShutdownAsync(CancellationToken.None);
            if (_server is not null)
                await _server.DisposeAsync();
        }

        private async Task<JsonElement> DispatchAsync(string method, JsonObject body)
        {
            using var doc = JsonDocument.Parse(body.ToJsonString());
            return await Service.DispatchAsync(method, doc.RootElement, CancellationToken.None);
        }

        private static async Task<Dump> ReadDumpAsync(string prefix)
        {
            var bin = await WaitTextAsync(prefix + ".bin");
            var socket = await WaitTextAsync(prefix + ".sock");
            var pane = await WaitTextAsync(prefix + ".pane");
            var token = await WaitTextAsync(prefix + ".token");
            return new Dump(bin, socket, pane, token);
        }

        private static async Task<string> WaitTextAsync(string path)
        {
            for (var i = 0; i < 50; i++)
            {
                if (File.Exists(path))
                    return await File.ReadAllTextAsync(path);
                await Task.Delay(50);
            }

            Assert.Fail("child did not write " + path);
            return "";
        }
    }

    private sealed record Dump(string Bin, string Socket, string Pane, string Token);
}
