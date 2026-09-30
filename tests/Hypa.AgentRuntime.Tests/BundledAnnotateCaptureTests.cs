using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.Annotate.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class BundledAnnotateCaptureTests
{
    [Fact]
    public void Stage_manifest_capture_runs_annotate_program_with_pane_context()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-bundled-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var binary = BundledAnnotateFixtures.WriteHypaPair(root);
            var bundled = new BundledPluginService(new SystemPluginFiles(), new SystemPluginPathRoots(root));
            var staged = bundled.StageAnnotate(binary);
            Assert.True(staged.IsOk, staged.IsOk ? "" : staged.Error.Message);
            Assert.DoesNotContain("testhost", staged.Value.Command0, StringComparison.OrdinalIgnoreCase);

            var parsed = new PluginManifestParser().Parse(File.ReadAllText(staged.Value.ManifestPath));
            Assert.True(parsed.IsOk, parsed.IsOk ? "" : parsed.Error.Message);
            var capture = Assert.Single(parsed.Value.Actions, action => action.Id == "capture");
            Assert.Equal(BundledPluginLayout.AnnotateProgramRelativeCommand, capture.Command[0]);
            Assert.Equal(["capture"], capture.Command.Skip(1).ToArray());
            Assert.Equal(["pane"], capture.Contexts);
            Assert.DoesNotContain(
                capture.Command,
                part => string.Equals(part, "bundled", StringComparison.Ordinal));

            var editor = Assert.Single(parsed.Value.Panes, pane => pane.Id == "editor");
            Assert.Equal("popup", editor.Placement);
            Assert.Equal(BundledPluginLayout.AnnotateProgramRelativeCommand, editor.Command[0]);
            Assert.Equal(["editor"], editor.Command.Skip(1).ToArray());
            Assert.DoesNotContain(
                editor.Command,
                part => string.Equals(part, "bundled", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Editor_popup_open_payload_is_focused_with_pending_env_and_target_pane()
    {
        var arguments = HypaBinPluginPaneOpener.BuildOpenArguments(
            "annotate",
            "editor",
            "/plugin/root",
            "pane-42",
            HypaBinPluginPaneOpener.EditorWidth,
            HypaBinPluginPaneOpener.EditorHeight,
            AnnotateEnv.PendingPath,
            "/state/pending-1.json");

        Assert.Contains("--env", arguments, StringComparer.Ordinal);
        Assert.Contains(AnnotateEnv.PendingPath + "=/state/pending-1.json", arguments, StringComparer.Ordinal);
        Assert.Contains("--target-pane", arguments, StringComparer.Ordinal);
        Assert.Contains("pane-42", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(arguments, argument => argument.Contains("HERDR_", StringComparison.Ordinal));
    }

    [Fact]
    public void Hypa_bin_open_arguments_use_positionals_and_pending_env()
    {
        var arguments = HypaBinPluginPaneOpener.BuildOpenArguments(
            "annotate",
            "editor",
            "/plugin/root",
            "pane-42",
            HypaBinPluginPaneOpener.EditorWidth,
            HypaBinPluginPaneOpener.EditorHeight,
            AnnotateEnv.PendingPath,
            "/state/pending-1.json");

        Assert.Equal(
            [
                "plugin", "pane", "open", "annotate", "editor",
                "--placement", "popup",
                "--width", "88",
                "--height", "24",
                "--cwd", "/plugin/root",
                "--focus",
                "--target-pane", "pane-42",
                "--env", AnnotateEnv.PendingPath + "=/state/pending-1.json",
            ],
            arguments);
        Assert.DoesNotContain("--plugin", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("--entrypoint", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(arguments, argument => argument.Contains("HERDR_", StringComparison.Ordinal));
    }

    [Fact]
    public void Invoke_capture_starts_annotate_program_not_bundled_action()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-capture-invoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var binary = BundledAnnotateFixtures.WriteHypaPair(root);
            var files = new SystemPluginFiles();
            var paths = new SystemPluginPathRoots(root);
            var launcher = new RecordingLauncher();
            var host = new PluginHostService(
                files,
                new SystemPluginClock(),
                paths,
                new PluginManifestParser(),
                new FilePluginRegistry(files, paths),
                launcher,
                binPath: Path.Combine(root, "hypa"));
            var bundled = new BundledPluginService(files, paths);
            var staged = bundled.StageAnnotate(binary);
            Assert.True(staged.IsOk, staged.IsOk ? "" : staged.Error.Message);
            Assert.True(host.Link(staged.Value.PluginRoot, true).IsOk);

            var invoked = host.InvokeAction(
                "capture",
                "annotate",
                new PluginInvocationContext { FocusedPaneId = "pane-42" });
            Assert.True(invoked.IsOk, invoked.IsOk ? "" : invoked.Error.Message);
            var start = Assert.Single(launcher.Starts);
            Assert.Equal(Path.GetFullPath(staged.Value.Command0), start.Program);
            Assert.EndsWith(
                BundledPluginLayout.AnnotateProgramFileName,
                start.Program,
                StringComparison.Ordinal);
            Assert.Equal(["capture"], start.Arguments);
            Assert.DoesNotContain("testhost", start.Program, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("pane-42", start.Environment[PluginEnv.PaneId]);
            Assert.Equal(Path.GetFullPath(binary), start.Environment[PluginEnv.BinPath]);
        }
        finally
        {
            TryDelete(root);
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

    private sealed class RecordingLauncher : IPluginProcessLauncher
    {
        public List<Launch> Starts { get; } = [];

        public bool TryStart(
            string program,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            int outputCapBytes,
            Action<PluginProcessExit> onExit,
            out string? error)
        {
            error = null;
            Starts.Add(new Launch(
                program,
                arguments.ToArray(),
                workingDirectory,
                new Dictionary<string, string>(environment)));
            onExit(new PluginProcessExit(0, "", "", null));
            return true;
        }

        public sealed record Launch(
            string Program,
            IReadOnlyList<string> Arguments,
            string Cwd,
            IReadOnlyDictionary<string, string> Environment);
    }
}
