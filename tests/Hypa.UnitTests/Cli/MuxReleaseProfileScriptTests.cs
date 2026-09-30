using System.Diagnostics;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class MuxReleaseProfileScriptTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [SkippableFact]
    public void MuxReleaseProfile_UnknownProfileFailsClosed()
    {
        Skip.If(string.IsNullOrEmpty(FindBash()), "bash is required");
        using var fixture = new TempDir();
        File.WriteAllText(Path.Combine(fixture.Dir, "hypa"), "fixture\n");
        var script = Path.Combine(RepoRoot, "scripts", "verify-f1-pack-smoke.sh");
        var result = RunBash($"\"{script}\" --rid osx-x64 --channel f1 --profile bogus \"{fixture.Dir}\"");
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("unknown pack profile", result.Combined, StringComparison.Ordinal);
        Assert.Contains("bogus", result.Combined, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void MuxReleaseNotes_StateContinuityUnavailable()
    {
        Skip.If(string.IsNullOrEmpty(FindBash()), "bash is required");
        var script = Path.Combine(RepoRoot, "scripts", "emit-mux-release-notes.sh");
        var result = RunBash($"\"{script}\" --profile mux-release --tag v9.9.9");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Continuity is unavailable", result.Combined, StringComparison.Ordinal);
        Assert.Contains("hypa accepts no rendezvous command.", result.Combined, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void MuxReleaseNotes_StateUninstallPurge()
    {
        Skip.If(string.IsNullOrEmpty(FindBash()), "bash is required");
        var script = Path.Combine(RepoRoot, "scripts", "emit-mux-release-notes.sh");
        var result = RunBash($"\"{script}\" --profile mux-release --tag v9.9.9");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("~/.hypa", result.Combined, StringComparison.Ordinal);
        Assert.Contains("uninstall", result.Combined, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void MuxReleaseNotes_UnknownProfileFailsClosed()
    {
        Skip.If(string.IsNullOrEmpty(FindBash()), "bash is required");
        var script = Path.Combine(RepoRoot, "scripts", "emit-mux-release-notes.sh");
        var result = RunBash($"\"{script}\" --profile bogus --tag v9.9.9");
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("unknown pack profile", result.Combined, StringComparison.Ordinal);
        Assert.Contains("bogus", result.Combined, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void MuxReleaseNotes_FullProfileMatchesPackerContract()
    {
        Skip.If(string.IsNullOrEmpty(FindBash()), "bash is required");
        var script = Path.Combine(RepoRoot, "scripts", "emit-mux-release-notes.sh");
        var result = RunBash($"\"{script}\" --profile full --tag test-full");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hypa mux release test-full", result.Combined, StringComparison.Ordinal);
        Assert.Contains("Continuity is unavailable", result.Combined, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void MuxReleaseUnifiedSmoke_HelpOmitsContinuityVerbs()
    {
        Skip.If(string.IsNullOrEmpty(FindBash()), "bash is required");
        Skip.If(OperatingSystem.IsWindows(), "unified smoke stubs are Unix scripts");
        using var fixture = new TempDir();
        var hypa = Path.Combine(fixture.Dir, "hypa");
        File.WriteAllText(
            hypa,
            """
            #!/bin/sh
            echo "  rendezvous    Join a named destination"
            exit 0
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hypa,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        var script = Path.Combine(RepoRoot, "scripts", "verify-hypa-unified-smoke.sh");
        var result = RunBash($"\"{script}\" --profile mux-release \"{hypa}\"");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("rendezvous", result.Combined, StringComparison.Ordinal);
    }

    private static SmokeResult RunBash(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FindBash()!,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot,
        };
        using var proc = Process.Start(psi);
        Assert.NotNull(proc);
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(30_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("Timed out running bash " + arguments);
        }

        return new SmokeResult(proc.ExitCode, stdout + Environment.NewLine + stderr);
    }

    private static string? FindBash() => File.Exists("/bin/bash") ? "/bin/bash" : null;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypa.slnx"))
                && File.Exists(Path.Combine(dir.FullName, "scripts", "emit-mux-release-notes.sh")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Dir = Path.Combine(Path.GetTempPath(), "hypa-mux-rel-prof-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
        }

        public string Dir { get; }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private readonly record struct SmokeResult(int ExitCode, string Combined);
}
