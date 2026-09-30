using System.Diagnostics;
using System.Text.Json;
using Hypa.Annotate.Application;
using Hypa.Annotate.Domain;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class AnnotateDoctorTests
{
    [Fact]
    public void Report_missing_state_directory_is_fail()
    {
        var report = AnnotateDoctorService.Report(null);
        Assert.Equal("fail", report.Status);
        Assert.Equal("state directory missing", report.Value);
    }

    [Fact]
    public void Report_empty_store_is_ok_zero_annotations()
    {
        var directory = CreateStateDirectory();
        try
        {
            var report = AnnotateDoctorService.Report(directory);
            Assert.Equal("ok", report.Status);
            Assert.Equal("0 annotations", report.Value);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void Report_one_annotation_uses_singular_value()
    {
        var directory = CreateStateDirectory();
        try
        {
            Assert.True(AnnotationStore.AppendAnnotation(directory, SampleAnnotation("one")).IsOk);
            var report = AnnotateDoctorService.Report(directory);
            Assert.Equal("ok", report.Status);
            Assert.Equal("1 annotation", report.Value);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void Report_corrupt_store_is_fail()
    {
        var directory = CreateStateDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "annotations.jsonl"), "{broken");
            var report = AnnotateDoctorService.Report(directory);
            Assert.Equal("fail", report.Status);
            Assert.Equal("store unreadable", report.Value);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [SkippableFact]
    public void Host_doctor_writes_json_and_exits_zero_when_status_is_fail()
    {
        var hostProgram = HostProgramPath();
        Skip.If(hostProgram is null, "hypa-annotate is not in the test output.");

        var info = new ProcessStartInfo
        {
            FileName = hostProgram,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("doctor");
        info.Environment[AnnotateEnv.StateDir] = "";

        using var process = Process.Start(info);
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(8000), process.StandardError.ReadToEnd());
        Assert.Equal(0, process.ExitCode);
        var line = LastNonEmptyLine(process.StandardOutput.ReadToEnd());
        Assert.False(string.IsNullOrWhiteSpace(line));
        var dto = JsonSerializer.Deserialize(line!, AnnotateJsonContext.Default.PluginDoctorStdout);
        Assert.NotNull(dto);
        Assert.Equal("fail", dto.Status);
        Assert.Equal("state directory missing", dto.Value);
    }

    [SkippableFact]
    public void Host_doctor_writes_ok_json_for_empty_store()
    {
        var hostProgram = HostProgramPath();
        Skip.If(hostProgram is null, "hypa-annotate is not in the test output.");
        var directory = CreateStateDirectory();
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = hostProgram,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("doctor");
            info.Environment[AnnotateEnv.StateDir] = directory;

            using var process = Process.Start(info);
            Assert.NotNull(process);
            Assert.True(process.WaitForExit(8000), process.StandardError.ReadToEnd());
            Assert.Equal(0, process.ExitCode);
            var line = LastNonEmptyLine(process.StandardOutput.ReadToEnd());
            var dto = JsonSerializer.Deserialize(line!, AnnotateJsonContext.Default.PluginDoctorStdout);
            Assert.NotNull(dto);
            Assert.Equal("ok", dto.Status);
            Assert.Equal("0 annotations", dto.Value);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static string? HostProgramPath()
    {
        var name = OperatingSystem.IsWindows() ? "hypa-annotate.exe" : "hypa-annotate";
        var path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }

    private static string CreateStateDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "hypa-annotate-doctor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }

    private static string? LastNonEmptyLine(string stdout)
    {
        string? last = null;
        using var reader = new StringReader(stdout ?? "");
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                last = line.Trim();
        }

        return last;
    }

    private static Annotation SampleAnnotation(string id) =>
        new()
        {
            SelectedText = $"selection {id}",
            CapturedAt = "2026-08-08T00:00:00Z",
            Context = new CaptureContext(),
            Id = id,
            Comment = $"comment {id}",
            CreatedAt = "2026-08-08T00:00:01Z",
        };
}
