using System.Diagnostics;
using Hypa.Annotate.Application;
using Xunit;

namespace Hypa.Annotate.Tests;

/// <summary>
/// Native last command boundary. This is a Hypa extension. It is not Plannotator.
/// </summary>
public sealed class AnnotateLastHostTests
{
    [SkippableFact]
    public void Last_without_session_exits_nonzero_and_writes_no_pane_bytes()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "argv recorder is a POSIX script.");
        var hostProgram = HostProgramPath();
        Skip.If(hostProgram is null, "hypa-annotate is not in the test output.");

        var root = Path.Combine(Path.GetTempPath(), "hypa-host-last-refuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = Path.Combine(root, "state");
            var pluginRoot = Path.Combine(root, "plugin");
            Directory.CreateDirectory(state);
            Directory.CreateDirectory(pluginRoot);
            var fakeBin = WriteArgvRecorder(Path.Combine(root, "fake-bin"));

            var info = new ProcessStartInfo
            {
                FileName = hostProgram,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("last");
            info.Environment[AnnotateEnv.StateDir] = state;
            info.Environment[AnnotateEnv.PluginRoot] = pluginRoot;
            info.Environment[AnnotateEnv.BinPath] = fakeBin;
            info.Environment[AnnotateEnv.PaneId] = "pane-42";
            info.Environment[AnnotateEnv.ContextJson] = """{"workspace_id":"w1"}""";

            using var process = Process.Start(info);
            Assert.NotNull(process);
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(8000), stderr);
            Assert.Equal(1, process.ExitCode);
            Assert.Contains(AnnotateLastMessages.NoAgentSession, stderr, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(state, "last-pending-*.json"));

            var argsPath = Path.Combine(Path.GetDirectoryName(fakeBin)!, "hypa.args");
            Assert.False(File.Exists(argsPath));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Send_text_arguments_carry_comment_without_a_second_newline()
    {
        var arguments = HypaBinPluginPaneSender.BuildSendTextArguments("pane-42", "review note");
        Assert.Equal(["plugin", "pane", "send-text", "pane-42", "--text", "review note"], arguments);
        Assert.DoesNotContain('\n', arguments[^1]);
        Assert.DoesNotContain('\r', arguments[^1]);
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
