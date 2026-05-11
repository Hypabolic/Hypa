using Hypa.Infrastructure.Runner;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure;

public sealed class ProcessCommandRunnerTests
{
    private readonly ProcessCommandRunner _runner = new();
    private static readonly bool IsWindows = OperatingSystem.IsWindows();

    private static CommandInvocation BufferedShell(string command) =>
        IsWindows
            ? CommandInvocation.Buffered("cmd", ["/c", command], command)
            : CommandInvocation.Buffered("sh", ["-c", command], command);

    private static CommandInvocation PassthroughShell(string command) =>
        IsWindows
            ? CommandInvocation.Passthrough("cmd", ["/c", command], command)
            : CommandInvocation.Passthrough("sh", ["-c", command], command);

    private static string CreateStderrEchoCommand() => IsWindows ? "echo err 1>&2" : "echo err >&2";
    private static string CreateExitCommand(int code) => IsWindows ? $"exit /b {code}" : $"exit {code}";
    private static string CreateSleepCommand() => IsWindows ? "ping -n 11 127.0.0.1 >NUL" : "sleep 10";

    [Fact]
    public async Task RunAsync_Buffered_CapturesStdout()
    {
        var inv = BufferedShell("echo hello");
        var result = await _runner.RunAsync(inv, CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.Contains("hello", result.Value.Stdout);
    }

    [Fact]
    public async Task RunAsync_Buffered_CapturesStderrSeparately()
    {
        var inv = BufferedShell(CreateStderrEchoCommand());
        var result = await _runner.RunAsync(inv, CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.Contains("err", result.Value.Stderr);
        Assert.Equal(string.Empty, result.Value.Stdout.Trim());
    }

    [Fact]
    public async Task RunAsync_Buffered_PreservesNonZeroExitCode()
    {
        var inv = BufferedShell(CreateExitCommand(42));
        var result = await _runner.RunAsync(inv, CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value.ExitCode);
    }

    [Fact]
    public async Task RunAsync_Buffered_ZeroExitCode()
    {
        var inv = BufferedShell(CreateExitCommand(0));
        var result = await _runner.RunAsync(inv, CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.Equal(0, result.Value.ExitCode);
    }

    [Fact]
    public async Task RunAsync_Timeout_ReturnsTimedOut()
    {
        var inv = new CommandInvocation
        {
            Executable = IsWindows ? "cmd" : "sh",
            Arguments = IsWindows ? ["/c", CreateSleepCommand()] : ["-c", CreateSleepCommand()],
            OriginalCommand = CreateSleepCommand(),
            Mode = ToolRunMode.Buffered,
            Timeout = TimeSpan.FromMilliseconds(200),
        };
        var result = await _runner.RunAsync(inv, CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.True(result.Value.WasTimedOut);
    }

    [Fact]
    public async Task RunAsync_Cancelled_ReturnsFail()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var inv = BufferedShell(CreateSleepCommand());
        var result = await _runner.RunAsync(inv, cts.Token);
        Assert.False(result.IsOk);
    }

    [Fact]
    public async Task RunAsync_Passthrough_StdoutAndStderrEmpty()
    {
        var inv = PassthroughShell("echo hello");
        var result = await _runner.RunAsync(inv, CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.Equal(string.Empty, result.Value.Stdout);
        Assert.Equal(string.Empty, result.Value.Stderr);
    }
}
