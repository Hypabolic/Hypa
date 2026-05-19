using System.Diagnostics;
using System.Text;
using Xunit;

namespace Hypa.IntegrationTests;

/// <summary>
/// Regression guard for Blocker A (phase-6d Step 0).
/// Verifies that CLI startup does not invoke the storage initialization path
/// that would create the Hypa data directory before the hook command runs.
///
/// Isolation strategy: the child process runs with HOME pointing to a temp
/// directory where $HOME/.hypa exists as a plain file. HypaDataOptions derives
/// DataDirectory from SpecialFolder.UserProfile (HOME on Linux), so if startup
/// init is ever reintroduced and calls Directory.CreateDirectory($HOME/.hypa),
/// it will collide with the file and throw before the hook command can run.
/// The test then fails on a non-zero exit code, catching that regression.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HookStartupTests : IAsyncLifetime
{
    private string _cliBinary = "";
    private string _fakeHome = "";

    public Task InitializeAsync()
    {
        var repoRoot = IntegrationTestHelpers.FindRepoRoot();
        _cliBinary = Path.Combine(repoRoot, "src", "Hypa.Cli", "bin", "Debug", "net10.0", "hypa.dll");
        Assert.True(File.Exists(_cliBinary), $"CLI binary not found at {_cliBinary}. Run 'dotnet build' first.");

        // Create a fake HOME directory with $HOME/.hypa as a plain file, not a
        // directory. Any attempt to Directory.CreateDirectory($HOME/.hypa) will
        // throw IOException on all platforms because a file already occupies that path.
        _fakeHome = Path.Combine(Path.GetTempPath(), $"hypa-fakehome-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fakeHome);
        File.WriteAllText(Path.Combine(_fakeHome, ".hypa"), "sentinel");

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_fakeHome, recursive: true); }
        catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HookCommand_StoragePathUnwritable_ExitsZeroWithDenyOutput()
    {
        const string payload = """{"tool_name":"Bash","tool_input":{"command":"git status"}}""";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        var psi = new ProcessStartInfo("dotnet", $"\"{_cliBinary}\" hook --agent codex")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Override user-profile environment variables so HypaDataOptions resolves
        // DataDirectory to our trap path on both Unix and Windows.
        psi.Environment["HOME"] = _fakeHome;
        psi.Environment["USERPROFILE"] = _fakeHome;
        var homeRoot = Path.GetPathRoot(_fakeHome) ?? "";
        psi.Environment["HOMEDRIVE"] = homeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        psi.Environment["HOMEPATH"] = _fakeHome.StartsWith(homeRoot, StringComparison.OrdinalIgnoreCase)
            ? _fakeHome[homeRoot.Length..]
            : _fakeHome;

        using var process = Process.Start(psi)!;
        await process.StandardInput.BaseStream.WriteAsync(payloadBytes);
        process.StandardInput.Close();

        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("deny", stdout, StringComparison.OrdinalIgnoreCase);
    }
}
