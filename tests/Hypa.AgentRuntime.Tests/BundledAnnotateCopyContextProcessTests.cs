using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Protocol;
using Hypa.Annotate.Application;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class BundledAnnotateCopyContextProcessTests
{
    [SkippableFact]
    public void Copy_context_child_notifies_through_bin_path()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "notify recorder is a POSIX script.");

        var root = Path.Combine(Path.GetTempPath(), "hypa-copy-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var hypa = BundledAnnotateFixtures.WriteHypaPair(
                Path.Combine(root, "product"),
                annotateContents: """
                    #!/bin/sh
                    "$HYPA_BIN_PATH" notification show --title "No annotations" --body "There is nothing to copy yet."
                    exit 0
                    """);
            var fakeBin = BundledAnnotateFixtures.WriteNotifyRecorder(Path.Combine(root, "fake-bin"));
            var files = new SystemPluginFiles();
            var paths = new SystemPluginPathRoots(root);
            var launcher = new ProcessPluginLauncher();
            var host = new PluginHostService(
                files,
                new SystemPluginClock(),
                paths,
                new PluginManifestParser(),
                new FilePluginRegistry(files, paths),
                launcher,
                binPath: fakeBin);
            var bundled = new BundledPluginService(files, paths);
            var staged = bundled.StageAnnotate(hypa);
            Assert.True(staged.IsOk, staged.IsOk ? "" : staged.Error.Message);
            Assert.True(File.Exists(BundledPluginLayout.AnnotateProgramStagePath(staged.Value.PluginRoot)));
            Assert.True(host.Link(staged.Value.PluginRoot, true).IsOk);

            var invoked = host.InvokeAction("copy-context", "annotate", null);
            Assert.True(invoked.IsOk, invoked.IsOk ? "" : invoked.Error.Message);

            var log = BundledAnnotateFixtures.WaitFinished(
                host,
                "annotate",
                "copy-context",
                TimeSpan.FromSeconds(8));
            Assert.Equal(0, log.ExitCode);
            Assert.True(log.Pid is > 0);
            Assert.EndsWith(
                BundledPluginLayout.AnnotateProgramFileName,
                log.Command[0],
                StringComparison.Ordinal);

            var argsPath = Path.Combine(Path.GetDirectoryName(fakeBin)!, "notify.args");
            Assert.True(File.Exists(argsPath), log.Stderr + log.Error);
            var args = File.ReadAllText(argsPath);
            Assert.Contains("notification", args, StringComparison.Ordinal);
            Assert.Contains("show", args, StringComparison.Ordinal);
            Assert.Contains("--title", args, StringComparison.Ordinal);
            Assert.Contains(AnnotateCopyContext.EmptyTitle, args, StringComparison.Ordinal);
            Assert.Contains("--body", args, StringComparison.Ordinal);
            Assert.Contains(AnnotateCopyContext.EmptyBody, args, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [SkippableFact]
    public void Child_receives_hypa_environment()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "environment dump is a POSIX script.");

        var root = Path.Combine(Path.GetTempPath(), "hypa-copy-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var hypa = BundledAnnotateFixtures.WriteHypaPair(
                Path.Combine(root, "product"),
                annotateContents: """
                    #!/bin/sh
                    printf '%s\n' "$HYPA_PLUGIN_ROOT" > "$HYPA_PLUGIN_STATE_DIR/env-root"
                    printf '%s\n' "$HYPA_PLUGIN_STATE_DIR" > "$HYPA_PLUGIN_STATE_DIR/env-state"
                    printf '%s\n' "$HYPA_BIN_PATH" > "$HYPA_PLUGIN_STATE_DIR/env-bin"
                    printf '%s\n' "$HYPA_PLUGIN_ID" > "$HYPA_PLUGIN_STATE_DIR/env-id"
                    printf '%s\n' "$HYPA_PLUGIN_GRANT_TOKEN" > "$HYPA_PLUGIN_STATE_DIR/env-token"
                    env | grep '^HERDR_' > "$HYPA_PLUGIN_STATE_DIR/env-herdr" || true
                    exit 0
                    """);
            var files = new SystemPluginFiles();
            var paths = new SystemPluginPathRoots(root);
            var fakeBin = Path.Combine(root, "product", "hypa");
            var launcher = new ProcessPluginLauncher();
            var host = new PluginHostService(
                files,
                new SystemPluginClock(),
                paths,
                new PluginManifestParser(),
                new FilePluginRegistry(files, paths),
                launcher,
                binPath: fakeBin);
            var bundled = new BundledPluginService(files, paths);
            var staged = bundled.StageAnnotate(hypa);
            Assert.True(staged.IsOk, staged.IsOk ? "" : staged.Error.Message);
            Assert.True(host.Link(staged.Value.PluginRoot, true).IsOk);

            var invoked = host.InvokeAction("copy-context", "annotate", null);
            Assert.True(invoked.IsOk, invoked.IsOk ? "" : invoked.Error.Message);
            var log = BundledAnnotateFixtures.WaitFinished(
                host,
                "annotate",
                "copy-context",
                TimeSpan.FromSeconds(8));
            Assert.Equal(0, log.ExitCode);

            var state = paths.PluginStateDir("annotate");
            Assert.Equal(staged.Value.PluginRoot, File.ReadAllText(Path.Combine(state, "env-root")).Trim());
            Assert.Equal(state, File.ReadAllText(Path.Combine(state, "env-state")).Trim());
            Assert.Equal(Path.GetFullPath(fakeBin), File.ReadAllText(Path.Combine(state, "env-bin")).Trim());
            Assert.Equal("annotate", File.ReadAllText(Path.Combine(state, "env-id")).Trim());
            Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(Path.Combine(state, "env-token")).Trim()));
            Assert.Equal("", File.ReadAllText(Path.Combine(state, "env-herdr")).Trim());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [SkippableFact]
    public async Task Nonzero_child_exit_leaves_mux_running()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets and POSIX scripts only.");

        var root = Path.Combine(Path.GetTempPath(), "hypa-copy-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sockDir = Path.Combine(Path.GetTempPath(), "h205" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sockDir);
        var sock = Path.Combine(sockDir, "s.sock");
        try
        {
            var hypa = BundledAnnotateFixtures.WriteHypaPair(
                Path.Combine(root, "product"),
                annotateContents: "#!/bin/sh\nexit 2\n");
            var files = new SystemPluginFiles();
            var paths = new SystemPluginPathRoots(root);
            var processRegistry = new PluginProcessRegistry();
            var launcher = new ProcessPluginLauncher(processRegistry);
            var host = new PluginHostService(
                files,
                new SystemPluginClock(),
                paths,
                new PluginManifestParser(),
                new FilePluginRegistry(files, paths),
                launcher,
                processRegistry: processRegistry);
            var bundled = new BundledPluginService(files, paths);
            var staged = bundled.StageAnnotate(hypa);
            Assert.True(staged.IsOk, staged.IsOk ? "" : staged.Error.Message);
            Assert.True(host.Link(staged.Value.PluginRoot, true).IsOk);

            var app = new AppState(SessionId.New("annotate-copy-crash"));
            app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
            var cp = new ControlPlaneService(
                app,
                TestPaneFactories.Stub(),
                new PaneIntelligencePipeline(),
                new HeuristicAgentDetector(),
                stateDirectory: root,
                plugins: host);
            await using var server = new UnixSocketServer(cp, sock);
            await server.StartAsync(CancellationToken.None);
            await using var client = new ControlPlaneClient(sock, connectTimeout: TimeSpan.FromSeconds(2));
            await client.ConnectAsync();
            try
            {
                var invoked = host.InvokeAction("copy-context", "annotate", null);
                Assert.True(invoked.IsOk, invoked.IsOk ? "" : invoked.Error.Message);
                var log = BundledAnnotateFixtures.WaitFinished(
                    host,
                    "annotate",
                    "copy-context",
                    TimeSpan.FromSeconds(8));
                Assert.Equal(2, log.ExitCode);
                Assert.True(log.Pid is > 0);
                Assert.Equal("failed", log.Status);

                var listed = await client.CallAsync(
                    ProtocolMethods.PluginList,
                    new System.Text.Json.Nodes.JsonObject { ["plugin_id"] = "annotate" });
                var plugin = Assert.Single(
                    listed.GetProperty("plugins").EnumerateArray(),
                    p => p.GetProperty("plugin_id").GetString() == "annotate");
                Assert.True(plugin.GetProperty("enabled").GetBoolean());
            }
            finally
            {
                await cp.ShutdownAsync(CancellationToken.None);
            }
        }
        finally
        {
            TryDelete(root);
            TryDelete(sockDir);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
