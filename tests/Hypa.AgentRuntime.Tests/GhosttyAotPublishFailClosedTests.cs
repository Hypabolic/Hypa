using System.Diagnostics;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Unix AOT publish of the mux host fails closed when libghostty-vt is missing.
/// Direct AgentServer publish must not skip the Ghostty native gate.
/// </summary>
public class GhosttyAotPublishFailClosedTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Theory]
    [InlineData("linux-x64", "libghostty-vt.so")]
    [InlineData("osx-arm64", "libghostty-vt.dylib")]
    public void AgentServer_aot_publish_target_fails_when_ghostty_native_missing(
        string rid,
        string libName)
    {
        using var publishDir = new TempDir();
        var result = InvokeFailClosedTarget(
            "src/Hypa.AgentServer/Hypa.AgentServer.csproj",
            "FailClosedNoGhosttyOnCliAotPublish",
            rid,
            publishDir.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(libName, result.Combined, StringComparison.Ordinal);
        Assert.Contains("Hypa.AgentServer", result.Combined, StringComparison.Ordinal);
        Assert.Contains("Do not fall back to Basic", result.Combined, StringComparison.Ordinal);
        Assert.DoesNotContain("error MSB4057", result.Combined, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("linux-x64", "libghostty-vt.so")]
    [InlineData("osx-arm64", "libghostty-vt.dylib")]
    public void AgentServer_ghostty_include_target_fails_when_ghostty_native_missing(
        string rid,
        string libName)
    {
        using var publishDir = new TempDir();
        var result = InvokeFailClosedTarget(
            "src/Hypa.AgentServer/Hypa.AgentServer.csproj",
            "FailClosedGhosttyOnCliAotPublish",
            rid,
            publishDir.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(libName, result.Combined, StringComparison.Ordinal);
        Assert.Contains("Hypa.AgentServer", result.Combined, StringComparison.Ordinal);
        Assert.Contains("Do not fall back to the host RID", result.Combined, StringComparison.Ordinal);
        Assert.DoesNotContain("error MSB4057", result.Combined, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("linux-x64", "libghostty-vt.so")]
    [InlineData("osx-arm64", "libghostty-vt.dylib")]
    public void AgentServer_aot_publish_target_passes_when_ghostty_native_present(
        string rid,
        string libName)
    {
        using var publishDir = new TempDir();
        File.WriteAllBytes(Path.Combine(publishDir.Path, libName), new byte[] { 0x00 });
        var result = InvokeFailClosedTarget(
            "src/Hypa.AgentServer/Hypa.AgentServer.csproj",
            "FailClosedNoGhosttyOnCliAotPublish",
            rid,
            publishDir.Path);

        Assert.True(result.ExitCode == 0, result.Combined);
        Assert.DoesNotContain("Do not fall back to Basic", result.Combined, StringComparison.Ordinal);
    }

    private static InvokeResult InvokeFailClosedTarget(
        string relativeProject,
        string target,
        string rid,
        string publishDir)
    {
        var project = Path.Combine(RepoRoot, relativeProject.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(project), "missing " + relativeProject);

        var trailing = publishDir.EndsWith(Path.DirectorySeparatorChar)
            ? publishDir
            : publishDir + Path.DirectorySeparatorChar;
        var psi = new ProcessStartInfo
        {
            FileName = FindDotnet(),
            Arguments =
                "msbuild \"" + project + "\""
                + " -nologo -restore:false"
                + " -t:" + target
                + " -p:PublishAot=true"
                + " -p:HypaIncludeGhostty=true"
                + " -p:RuntimeIdentifier=" + rid
                + " -p:PublishDir=\"" + trailing + "\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot,
        };

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("Timed out running " + psi.FileName + " " + psi.Arguments);
        }

        return new InvokeResult(proc.ExitCode, stdout + Environment.NewLine + stderr);
    }

    private static string FindDotnet()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(host) && File.Exists(host))
            return host;
        return "dotnet";
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypa.slnx"))
                && File.Exists(Path.Combine(dir.FullName, "Directory.Build.targets")))
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
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "hypa-ghostty-aot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

    private readonly record struct InvokeResult(int ExitCode, string Combined);
}
