using System.Diagnostics;
using Hypa.Annotate.Application;
using Xunit;

namespace Hypa.Annotate.Tests;

/// <summary>
// / Native capture command boundary.
/// </summary>
public sealed class AnnotateCaptureHostTests
{
    [SkippableFact]
    public void Blank_capture_exits_zero_and_writes_no_pending_file()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "notify recorder is a POSIX script.");
        var hostProgram = HostProgramPath();
        Skip.If(hostProgram is null, "hypa-annotate is not in the test output.");

        var root = Path.Combine(Path.GetTempPath(), "hypa-host-capture-blank-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = Path.Combine(root, "state");
            var pluginRoot = Path.Combine(root, "plugin");
            var runtime = Path.Combine(root, "runtime");
            var clip = Path.Combine(root, "clip");
            Directory.CreateDirectory(state);
            Directory.CreateDirectory(pluginRoot);
            Directory.CreateDirectory(runtime);
            WriteEmptyClipboard(clip);
            var fakeBin = WriteArgvRecorder(Path.Combine(root, "fake-bin"));

            var info = CaptureStartInfo(hostProgram, state, pluginRoot, runtime, clip, fakeBin, paneId: "pane-42");
            using var process = Process.Start(info);
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(8000), stderr);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(AnnotateCaptureService.BlankSelectionMessage + Environment.NewLine, stdout);
            Assert.Empty(Directory.GetFiles(state, "pending-*.json"));

            var argsPath = Path.Combine(Path.GetDirectoryName(fakeBin)!, "hypa.args");
            Assert.True(File.Exists(argsPath), stderr);
            var args = File.ReadAllText(argsPath);
            Assert.Contains("notification", args, StringComparison.Ordinal);
            Assert.Contains("show", args, StringComparison.Ordinal);
            Assert.Contains("--title", args, StringComparison.Ordinal);
            Assert.Contains(AnnotateCaptureService.BlankSelectionTitle, args, StringComparison.Ordinal);
            Assert.Contains("--body", args, StringComparison.Ordinal);
            Assert.Contains(AnnotateCaptureService.BlankSelectionBody, args, StringComparison.Ordinal);
            Assert.Contains("--source", args, StringComparison.Ordinal);
            Assert.Contains("plugin:" + AnnotateEnv.AnnotatePluginId, args, StringComparison.Ordinal);
            var lines = args.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.DoesNotContain("pane", lines, StringComparer.Ordinal);
            Assert.DoesNotContain("open", lines, StringComparer.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [SkippableFact]
    public void Capture_opens_editor_popup_through_bin_path()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "pane-open recorder is a POSIX script.");
        var hostProgram = HostProgramPath();
        Skip.If(hostProgram is null, "hypa-annotate is not in the test output.");

        var root = Path.Combine(Path.GetTempPath(), "hypa-host-capture-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = Path.Combine(root, "state");
            var pluginRoot = Path.Combine(root, "plugin");
            var runtime = Path.Combine(root, "runtime");
            var clip = Path.Combine(root, "clip");
            Directory.CreateDirectory(state);
            Directory.CreateDirectory(pluginRoot);
            Directory.CreateDirectory(runtime);
            WriteEmptyClipboard(clip);
            var fakeBin = WriteArgvRecorder(Path.Combine(root, "fake-bin"));
            var uid = UnixUserId();
            var handoff = Path.Combine(runtime, "hypa-annotate-" + uid, "selection");
            var written = SelectionHandoff.WriteHandoff("selected text", handoff);
            Assert.True(written.IsOk, written.IsOk ? "" : written.Error);

            var info = CaptureStartInfo(hostProgram, state, pluginRoot, runtime, clip, fakeBin, paneId: "pane-42");
            info.Environment[AnnotateEnv.PluginId] = AnnotateEnv.AnnotatePluginId;
            using var process = Process.Start(info);
            Assert.NotNull(process);
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(8000), stderr);
            Assert.Equal(0, process.ExitCode);

            var pending = Assert.Single(Directory.GetFiles(state, "pending-*.json"));
            var argsPath = Path.Combine(Path.GetDirectoryName(fakeBin)!, "hypa.args");
            Assert.True(File.Exists(argsPath), stderr);
            var args = File.ReadAllText(argsPath);
            Assert.Contains("plugin\n", args, StringComparison.Ordinal);
            Assert.Contains("pane\n", args, StringComparison.Ordinal);
            Assert.Contains("open\n", args, StringComparison.Ordinal);
            Assert.Contains("annotate\n", args, StringComparison.Ordinal);
            Assert.Contains("editor\n", args, StringComparison.Ordinal);
            Assert.Contains("--placement\npopup\n", args, StringComparison.Ordinal);
            Assert.Contains("--width\n88\n", args, StringComparison.Ordinal);
            Assert.Contains("--height\n24\n", args, StringComparison.Ordinal);
            Assert.Contains("--cwd\n" + pluginRoot + "\n", args, StringComparison.Ordinal);
            Assert.Contains("--focus\n", args, StringComparison.Ordinal);
            Assert.Contains("--target-pane\npane-42\n", args, StringComparison.Ordinal);
            Assert.Contains(
                "--env\n" + AnnotateEnv.PendingPath + "=" + pending + "\n",
                args,
                StringComparison.Ordinal);
            Assert.DoesNotContain("--plugin\n", args, StringComparison.Ordinal);
            Assert.DoesNotContain("--entrypoint\n", args, StringComparison.Ordinal);
            Assert.DoesNotContain("HERDR_", args, StringComparison.Ordinal);
            Assert.DoesNotContain("plugin bundled action", args, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static ProcessStartInfo CaptureStartInfo(
        string hostProgram,
        string state,
        string pluginRoot,
        string runtime,
        string clip,
        string fakeBin,
        string paneId)
    {
        var info = new ProcessStartInfo
        {
            FileName = hostProgram,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("capture");
        info.Environment[AnnotateEnv.StateDir] = state;
        info.Environment[AnnotateEnv.PluginRoot] = pluginRoot;
        info.Environment[AnnotateEnv.BinPath] = fakeBin;
        info.Environment[AnnotateEnv.PaneId] = paneId;
        info.Environment["XDG_RUNTIME_DIR"] = runtime;
        var path = info.Environment.TryGetValue("PATH", out var existing) ? existing : "";
        info.Environment["PATH"] = clip + Path.PathSeparator + path;
        return info;
    }

    private static string? HostProgramPath()
    {
        var name = OperatingSystem.IsWindows() ? "hypa-annotate.exe" : "hypa-annotate";
        var path = Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(path))
            return null;
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "testhost", StringComparison.OrdinalIgnoreCase))
            return null;
        return path;
    }

    private static string WriteArgvRecorder(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "hypa");
        File.WriteAllText(path, """
            #!/bin/sh
            log="$(dirname "$0")/hypa.args"
            : > "$log"
            for arg in "$@"; do
              printf '%s\n' "$arg" >> "$log"
            done
            exit 0
            """);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private static void WriteEmptyClipboard(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "pbpaste", "wl-paste", "xclip", "xsel" })
        {
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    private static string UnixUserId()
    {
        var info = new ProcessStartInfo("id")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-u");
        using var process = Process.Start(info);
        Assert.NotNull(process);
        var uid = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.False(string.IsNullOrWhiteSpace(uid));
        return uid;
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
