using System.CommandLine;
using Hypa.Cli.Commands;
using Hypa.Infrastructure.Rewrite;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;
using Hypa.Runtime.Domain.Filters;
using Hypa.Runtime.Domain.Metrics;
using Hypa.Runtime.Domain.Runner;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class RunCommandTests
{
    // The buffered shell invocation differs by OS: cmd.exe on Windows, sh elsewhere.
    private static readonly string ExpectedShell = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";

    private static string[] ExpectedShellArgs(string command) =>
        OperatingSystem.IsWindows() ? ["/d", "/s", "/c", command] : ["-c", command];

    [Fact]
    public async Task BufferedPackageManagerCommand_UsesLongDefaultTimeout()
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.InvokeAsync(["-c", "pnpm build"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(TimeSpan.FromMinutes(10), invocation.Timeout);
    }

    [Fact]
    public async Task BufferedCommand_TimeoutOverrideWins()
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.InvokeAsync(["--timeout-ms", "1234", "-c", "pnpm build"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), invocation.Timeout);
    }

    [Fact]
    public async Task BufferedNonPackageManagerCommand_UsesShortDefaultTimeout()
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.InvokeAsync(["-c", "echo hello"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(TimeSpan.FromSeconds(30), invocation.Timeout);
    }

    [Fact]
    public async Task BufferedStatefulBuiltin_UsesShellInvocation()
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.InvokeAsync(["-c", "cd /tmp"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs("cd /tmp"), invocation.Arguments);
    }

    [Fact]
    public async Task BufferedEnvPrefixedStatefulBuiltin_UsesShellInvocation()
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.InvokeAsync(["-c", "FOO=bar cd /tmp"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs("FOO=bar cd /tmp"), invocation.Arguments);
    }

    [Fact]
    public async Task BufferedEnvPrefixedCommand_UsesShellInvocation()
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.InvokeAsync(["-c", "FOO=bar ls"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
    }

    [Fact]
    public async Task BufferedDoubleQuotedVariable_UsesShellInvocation()
    {
        var command = "echo \"$HOME\"";
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.InvokeAsync(["-c", command]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs(command), invocation.Arguments);
    }

    [Fact]
    public async Task BufferedCommandSubstitution_UsesShellInvocation()
    {
        var command = "echo \"$(date)\"";
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.InvokeAsync(["-c", command]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs(command), invocation.Arguments);
    }

    [Fact]
    public async Task BufferedHeredocInCommandSubstitution_UsesShellInvocation()
    {
        var command = """
            git commit --allow-empty -m "$(cat <<'INNER'
            Line one

            Line three
            INNER
            )"
            """;
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.InvokeAsync(["-c", command]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs(command), invocation.Arguments);
    }

    [Fact]
    public async Task BufferedPlainCommand_UsesDirectInvocation()
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.InvokeAsync(["-c", "ls"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.NotEqual("sh", invocation.Executable);
    }

    [Fact]
    public async Task BufferedCommand_InvalidTimeoutReturnsError()
    {
        var (root, runner) = BuildRoot();

        var exitCode = await root.InvokeAsync(["--timeout-ms", "0", "-c", "echo hello"]);

        Assert.Equal(1, exitCode);
        await runner.DidNotReceive().RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>());
    }

    private static (RootCommand Root, ICommandRunner Runner) BuildRoot()
    {
        var runner = Substitute.For<ICommandRunner>();
        var root = new RootCommand();
        var command = new RunCommand(MakeService(runner), new ShellLexer());
        command.AttachTo(root);
        return (root, runner);
    }

    private static CommandRunnerService MakeService(ICommandRunner runner)
    {
        var compressor = Substitute.For<IOutputCompressor>();
        var tokenCounter = Substitute.For<ITokenCounter>();
        var artifacts = Substitute.For<IArtifactRepository>();
        var evidence = Substitute.For<IEvidenceLedger>();
        var resolver = Substitute.For<ISessionResolver>();
        var configLoader = Substitute.For<IConfigLoader>();
        var filterRepo = Substitute.For<IFilterRepository>();
        var filterEngine = Substitute.For<IFilterEngine>();
        var parseMetrics = Substitute.For<IParseMetricsRepository>();
        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();

        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(0).Length);
        var session = new ContextSession { Id = Guid.NewGuid(), ProjectRoot = "/tmp" };
        resolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Ok(session));
        configLoader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<HypaConfig, Error>.Ok(HypaConfig.Default));
        filterRepo.GetAll().Returns([]);
        filterEngine.Apply(Arg.Any<CompiledFilterDefinition>(), Arg.Any<string>())
            .Returns(ci => new FilterResult(ci.ArgAt<string>(1), "none", 0));
        parseMetrics.RecordAsync(Arg.Any<ParseMetricsRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        packageScriptResolver.TryResolve(Arg.Any<CommandInvocation>())
            .Returns((ResolvedPackageScript?)null);

        return new CommandRunnerService(
            runner,
            [compressor],
            tokenCounter,
            artifacts,
            evidence,
            resolver,
            configLoader,
            packageScriptResolver,
            new FilterService(filterRepo, filterEngine),
            filterEngine,
            parseMetrics,
            NullLogger<CommandRunnerService>.Instance);
    }
}
