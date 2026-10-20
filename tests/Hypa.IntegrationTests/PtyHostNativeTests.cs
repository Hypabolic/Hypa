using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Hypa.IntegrationTests;

/// <summary>
/// Checks of the native <c>hypa-pty-host</c> helper. The helper is a C program.
/// The integration job builds it before the tests run (see the CI workflow).
/// These tests need that binary and never skip. If it is missing, the test fails
/// and the message names the build script. The unit suites do not run these
/// checks, because a unit suite must not need a native toolchain.
/// </summary>
public sealed class PtyHostNativeTests
{
    [Fact]
    public async Task Fdpass_selftest_rejects_non_pty_and_accepts_pty()
    {
        Assert.False(
            OperatingSystem.IsWindows(),
            "hypa-pty-host is Unix-only. Windows is not a mux host.");

        var helper = ResolveHelper();
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = helper,
            ArgumentList = { "--selftest-fdpass" },
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(proc);

        // The selftest has no clock in it. It opens a pty and a pipe, passes both
        // over a socket pair, and exits. The limit below stops a hang only, for
        // example a helper that is too old to know the flag and waits on stdin.
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var hangGuard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await proc.WaitForExitAsync(hangGuard.Token);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            Assert.Fail("--selftest-fdpass did not exit. helper=" + helper);
        }

        var stderr = await stderrTask;
        Assert.True(
            proc.ExitCode == 0,
            $"--selftest-fdpass failed exit={proc.ExitCode} helper={helper} stderr={stderr}");
    }

    /// <summary>
    /// The pane child must get default dispositions and an empty mask, also
    /// when the helper itself ignores the signal. A mux started as <c>cmd &amp;</c>
    /// ignores SIGINT, and ctrl+c then did not stop a pane program.
    /// </summary>
    [Theory]
    [InlineData("--selftest-sigpipe")]
    [InlineData("--selftest-sigint")]
    public async Task Pane_child_dies_by_a_signal_the_helper_ignores(string flag)
    {
        Assert.False(
            OperatingSystem.IsWindows(),
            "hypa-pty-host is Unix-only. Windows is not a mux host.");

        var helper = ResolveHelper();
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = helper,
            ArgumentList = { flag },
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(proc);

        // The selftest stops its own child after 4 s. This limit stops a hang only.
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var hangGuard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await proc.WaitForExitAsync(hangGuard.Token);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            Assert.Fail(flag + " did not exit. helper=" + helper);
        }

        var stderr = await stderrTask;
        // 5: the child printed SURVIVED. 6: the child exited clean.
        Assert.True(
            proc.ExitCode == 0,
            $"{flag} failed exit={proc.ExitCode} helper={helper} stderr={stderr}");
    }

    internal static string ResolveHelper()
    {
        var fromEnv = Environment.GetEnvironmentVariable("HYPA_PTY_HOST");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            Assert.True(File.Exists(fromEnv), $"HYPA_PTY_HOST points to a missing file: {fromEnv}");
            return fromEnv;
        }

        var rid = OperatingSystem.IsMacOS()
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
            : (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "native", "runtimes", rid, "native", "hypa-pty-host");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        Assert.Fail(
            "hypa-pty-host is missing for " + rid + ". "
            + "Build it with scripts/build-hypa-pty-host.sh or set HYPA_PTY_HOST. "
            + "The integration job does this before the tests.");
        return "";
    }
}
