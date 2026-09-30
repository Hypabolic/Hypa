using System.Diagnostics;
using System.Runtime.InteropServices;
using Hypa.Annotate.Application;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class AnnotateHostCopyContextTests
{
    [Fact]
    public void Notify_arguments_include_plugin_source()
    {
        var arguments = HypaBinNotifier.BuildShowArguments(
            AnnotateCopyContext.EmptyTitle,
            AnnotateCopyContext.EmptyBody,
            AnnotateEnv.AnnotatePluginId);
        Assert.Equal(
            [
                "notification",
                "show",
                "--title",
                AnnotateCopyContext.EmptyTitle,
                "--source",
                "plugin:" + AnnotateEnv.AnnotatePluginId,
                "--body",
                AnnotateCopyContext.EmptyBody,
            ],
            arguments);
    }

    [SkippableFact]
    public void Empty_store_notifies_no_annotations()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "notify recorder is a POSIX script.");
        var hostProgram = HostProgramPath();
        Skip.If(hostProgram is null, "hypa-annotate is not in the test output.");

        var root = Path.Combine(Path.GetTempPath(), "hypa-host-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = Path.Combine(root, "state");
            Directory.CreateDirectory(state);
            var fakeBin = WriteNotifyRecorder(Path.Combine(root, "fake-bin"));

            var info = new ProcessStartInfo
            {
                FileName = hostProgram,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("copy-context");
            info.Environment[AnnotateEnv.StateDir] = state;
            info.Environment[AnnotateEnv.BinPath] = fakeBin;

            using var process = Process.Start(info);
            Assert.NotNull(process);
            Assert.True(process.WaitForExit(8000), process.StandardError.ReadToEnd());
            Assert.Equal(0, process.ExitCode);

            var argsPath = Path.Combine(Path.GetDirectoryName(fakeBin)!, "notify.args");
            Assert.True(File.Exists(argsPath), process.StandardError.ReadToEnd());
            var args = File.ReadAllText(argsPath);
            Assert.Contains("notification", args, StringComparison.Ordinal);
            Assert.Contains("show", args, StringComparison.Ordinal);
            Assert.Contains("--title", args, StringComparison.Ordinal);
            Assert.Contains(AnnotateCopyContext.EmptyTitle, args, StringComparison.Ordinal);
            Assert.Contains("--body", args, StringComparison.Ordinal);
            Assert.Contains(AnnotateCopyContext.EmptyBody, args, StringComparison.Ordinal);
            Assert.Contains("--source", args, StringComparison.Ordinal);
            Assert.Contains("plugin:" + AnnotateEnv.AnnotatePluginId, args, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string? HostProgramPath()
    {
        var name = OperatingSystem.IsWindows() ? "hypa-annotate.exe" : "hypa-annotate";
        var path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }

    private static string WriteNotifyRecorder(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "hypa");
        File.WriteAllText(path, """
            #!/bin/sh
            log="$(dirname "$0")/notify.args"
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
}
