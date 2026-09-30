using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.Annotate.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class BundledAnnotateCaptureProcessTests
{
    [SkippableFact]
    public void Capture_child_opens_editor_through_bin_path()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "pane-open recorder is a POSIX script.");

        var root = Path.Combine(Path.GetTempPath(), "hypa-capture-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var hypa = BundledAnnotateFixtures.WriteHypaPair(
                Path.Combine(root, "product"),
                annotateContents: """
                    #!/bin/sh
                    pending="$HYPA_PLUGIN_STATE_DIR/pending.json"
                    printf 'selection\n' > "$pending"
                    "$HYPA_BIN_PATH" plugin pane open annotate editor \
                      --placement popup \
                      --width 88 \
                      --height 24 \
                      --cwd "$HYPA_PLUGIN_ROOT" \
                      --focus \
                      --target-pane "$HYPA_PANE_ID" \
                      --env "HYPA_ANNOTATE_PENDING=$pending"
                    exit 0
                    """);
            var fakeBin = BundledAnnotateFixtures.WriteArgvRecorder(Path.Combine(root, "fake-bin"), "pane.args");
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
            Assert.DoesNotContain("testhost", staged.Value.Command0, StringComparison.OrdinalIgnoreCase);
            Assert.True(host.Link(staged.Value.PluginRoot, true).IsOk);

            var invoked = host.InvokeAction(
                "capture",
                "annotate",
                new PluginInvocationContext { FocusedPaneId = "pane-42" });
            Assert.True(invoked.IsOk, invoked.IsOk ? "" : invoked.Error.Message);

            var log = BundledAnnotateFixtures.WaitFinished(
                host,
                "annotate",
                "capture",
                TimeSpan.FromSeconds(8));
            Assert.Equal(0, log.ExitCode);
            Assert.True(log.Pid is > 0);
            Assert.EndsWith(
                BundledPluginLayout.AnnotateProgramFileName,
                log.Command[0],
                StringComparison.Ordinal);
            Assert.Equal("capture", log.Command[1]);

            var argsPath = Path.Combine(Path.GetDirectoryName(fakeBin)!, "pane.args");
            Assert.True(File.Exists(argsPath), log.Stderr + log.Error);
            var args = File.ReadAllText(argsPath);
            Assert.Contains("plugin\n", args, StringComparison.Ordinal);
            Assert.Contains("pane\n", args, StringComparison.Ordinal);
            Assert.Contains("open\n", args, StringComparison.Ordinal);
            Assert.Contains("annotate\n", args, StringComparison.Ordinal);
            Assert.Contains("editor\n", args, StringComparison.Ordinal);
            Assert.Contains("--placement\npopup\n", args, StringComparison.Ordinal);
            Assert.Contains("--width\n88\n", args, StringComparison.Ordinal);
            Assert.Contains("--height\n24\n", args, StringComparison.Ordinal);
            Assert.Contains("--cwd\n" + staged.Value.PluginRoot + "\n", args, StringComparison.Ordinal);
            Assert.Contains("--focus\n", args, StringComparison.Ordinal);
            Assert.Contains("--target-pane\npane-42\n", args, StringComparison.Ordinal);
            Assert.Contains(
                "--env\n" + AnnotateEnv.PendingPath + "=",
                args,
                StringComparison.Ordinal);
            Assert.DoesNotContain("HERDR_", args, StringComparison.Ordinal);
            Assert.DoesNotContain("plugin bundled action", args, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [SkippableFact]
    public void Blank_capture_exits_zero_without_pending_file()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "notify recorder is a POSIX script.");

        var root = Path.Combine(Path.GetTempPath(), "hypa-capture-blank-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var hypa = BundledAnnotateFixtures.WriteHypaPair(
                Path.Combine(root, "product"),
                annotateContents: """
                    #!/bin/sh
                    printf '%s\n' "Nothing to annotate."
                    "$HYPA_BIN_PATH" notification show --title "Nothing to annotate" --body "Select text in the pane or copy text to the clipboard."
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
            Assert.True(host.Link(staged.Value.PluginRoot, true).IsOk);

            var invoked = host.InvokeAction("capture", "annotate", null);
            Assert.True(invoked.IsOk, invoked.IsOk ? "" : invoked.Error.Message);
            var log = BundledAnnotateFixtures.WaitFinished(
                host,
                "annotate",
                "capture",
                TimeSpan.FromSeconds(8));
            Assert.Equal(0, log.ExitCode);
            Assert.Contains(AnnotateCaptureService.BlankSelectionMessage, log.Stdout, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(paths.PluginStateDir("annotate"), "pending-*.json"));

            var argsPath = Path.Combine(Path.GetDirectoryName(fakeBin)!, "notify.args");
            Assert.True(File.Exists(argsPath), log.Stderr + log.Error);
            var args = File.ReadAllText(argsPath);
            Assert.Contains("notification", args, StringComparison.Ordinal);
            Assert.Contains("--title", args, StringComparison.Ordinal);
            Assert.Contains(AnnotateCaptureService.BlankSelectionTitle, args, StringComparison.Ordinal);
            Assert.Contains("--body", args, StringComparison.Ordinal);
            Assert.Contains(AnnotateCaptureService.BlankSelectionBody, args, StringComparison.Ordinal);
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
}
