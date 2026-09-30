using System.Diagnostics;
using Hypa.Annotate.Application;
using Hypa.Annotate.Domain;
using Xunit;

namespace Hypa.Annotate.Tests;

/// <summary>
// / Global copy-archive is a native clipboard write.
/// <c>rust/src/cli.rs:183-212</c>. Redirected child stdout is not OSC 52.
/// </summary>
public sealed class AnnotateHostCopyArchiveTests
{
    [SkippableFact]
    public void Redirected_stdout_does_not_archive_when_native_clipboard_fails()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "native clipboard PATH lookup is Unix.");
        var hostProgram = HostProgramPath();
        Skip.If(hostProgram is null, "hypa-annotate is not in the test output.");

        var root = Path.Combine(Path.GetTempPath(), "hypa-host-copy-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = Path.Combine(root, "state");
            Directory.CreateDirectory(state);
            Assert.True(AnnotationStore.AppendAnnotation(state, SampleAnnotation()).IsOk);
            var emptyPath = Path.Combine(root, "empty-path");
            Directory.CreateDirectory(emptyPath);
            var fakeBin = WriteNotifyRecorder(Path.Combine(root, "fake-bin"));
            var info = new ProcessStartInfo
            {
                FileName = hostProgram,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("copy-archive");
            info.Environment[AnnotateEnv.StateDir] = state;
            info.Environment[AnnotateEnv.BinPath] = fakeBin;
            info.Environment["PATH"] = emptyPath;

            using var process = Process.Start(info);
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(8000), stderr);
            Assert.Equal(1, process.ExitCode);
            Assert.DoesNotContain("\u001b]52;", stdout, StringComparison.Ordinal);
            Assert.Single(AnnotationStore.LoadAnnotations(state).Value!);
            Assert.Empty(AnnotationStore.LoadArchivedSets(state).Value!);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [SkippableFact]
    public void Console_osc52_does_not_succeed_when_stdout_is_redirected()
    {
        Skip.If(!Console.IsOutputRedirected, "stdout is a TTY.");
        Assert.False(new ConsoleOsc52Emitter(Console.Out).TryEmit("\u001b]52;c;YQ==\u0007"));
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

    private static Annotation SampleAnnotation() =>
        new()
        {
            SelectedText = "selection one",
            CapturedAt = "2026-08-08T00:00:00Z",
            Context = new CaptureContext(),
            Id = "one",
            Comment = "comment one",
            CreatedAt = "2026-08-08T00:00:01Z",
        };
}
