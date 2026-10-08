using System.CommandLine;
using Hypa.Cli.Attach;
using Hypa.Cli.Commands;
using Hypa.Cli.Mux;
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

        var exitCode = await root.Parse(["-c", "pnpm build"]).InvokeAsync();

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

        var exitCode = await root.Parse(["--timeout-ms", "1234", "-c", "pnpm build"]).InvokeAsync();

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

        var exitCode = await root.Parse(["-c", "echo hello"]).InvokeAsync();

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

        var exitCode = await root.Parse(["-c", "cd /tmp"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs("cd /tmp"), invocation.Arguments);
    }

    [Fact]
    public async Task BufferedShellOnlyBuiltin_UsesShellInvocation()
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.Parse(["-c", "command -v git"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs("command -v git"), invocation.Arguments);
    }

    [Fact]
    public async Task BufferedEnvPrefixedStatefulBuiltin_UsesShellInvocation()
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.Parse(["-c", "FOO=bar cd /tmp"]).InvokeAsync();

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

        var exitCode = await root.Parse(["-c", "FOO=bar ls"]).InvokeAsync();

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

        var exitCode = await root.Parse(["-c", command]).InvokeAsync();

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

        var exitCode = await root.Parse(["-c", command]).InvokeAsync();

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

        var exitCode = await root.Parse(["-c", command]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs(command), invocation.Arguments);
    }

    [Fact]
    public async Task BufferedTildeArg_UsesShellInvocation()
    {
        var command = "echo ~/Desktop";
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.Parse(["-c", command]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs(command), invocation.Arguments);
    }

    [Fact]
    public async Task BufferedBareTilde_UsesShellInvocation()
    {
        var command = "echo ~";
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.Parse(["-c", command]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs(command), invocation.Arguments);
    }

    [Fact]
    public async Task BufferedTildeUser_UsesShellInvocation()
    {
        var command = "echo ~user/bin";
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.Parse(["-c", command]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs(command), invocation.Arguments);
    }

    [Fact]
    public async Task BufferedDoubleQuotedTilde_UsesDirectInvocation()
    {
        // A tilde inside double quotes is not expanded by POSIX shells, so the
        // direct-execution path is correct and must be preserved.
        var command = "echo \"~/Desktop\"";
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.Parse(["-c", command]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.NotEqual(ExpectedShell, invocation.Executable);
    }

    [Fact]
    public async Task BufferedTildeNotAtStart_UsesDirectInvocation()
    {
        // A tilde that is not at the start of an unquoted word is not expanded,
        // so the direct-execution path is correct and must be preserved.
        var command = "echo a~b";
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.Parse(["-c", command]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.NotEqual(ExpectedShell, invocation.Executable);
    }

    [Fact]
    public async Task BufferedTildeGlobPattern_UsesShellInvocation()
    {
        // ~* is not a POSIX tilde word, but the unquoted * requires pathname
        // expansion, so the shell path is intentional.
        var command = "echo ~*";
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.Parse(["-c", command]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs(command), invocation.Arguments);
    }

    [Theory]
    [InlineData("ls path/*.json")]
    [InlineData("echo file?")]
    [InlineData("ls file[ab].txt")]
    [InlineData("echo {a,b}")]
    [InlineData("echo {1..3}")]
    [InlineData("echo {a,\"b\"}")]
    public async Task BufferedGlobOrBraceCommand_UsesShellInvocation(string command)
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.Parse(["-c", command]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs(command), invocation.Arguments);
    }

    // A newline, comment, escape or heredoc must reach a real shell. Executing it
    // directly joined lines into one argv: "rm -rf a<NL>ls b" ran "rm -rf a ls b"
    // and "rm -rf a # not b" ran "rm -rf a # not b" (deleting ./not and ./b).
    [Theory]
    [InlineData("rm -rf /tmp/x/a\nls /tmp/x/b")]
    [InlineData("printf a\n\nprintf b")]
    [InlineData("printf a\r\nprintf b")]
    [InlineData("rm -rf /tmp/x/a # not /tmp/x/b")]
    [InlineData("# comment\nprintf b")]
    [InlineData("printf x \\\n y")]
    [InlineData("echo a\\; rm b")]
    [InlineData("cat <<'EOF'\nhello\nEOF")]
    public async Task BufferedNewlineCommentEscapeOrHeredoc_UsesShellInvocation(string command)
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.Parse(["-c", command]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.Equal(ExpectedShell, invocation.Executable);
        Assert.Equal(ExpectedShellArgs(command), invocation.Arguments);
    }

    [Theory]
    [InlineData("printf \"a\nb\"", new[] { "a\nb" })]
    [InlineData("echo a#b", new[] { "a#b" })]
    [InlineData("ls\n", new string[0])]
    public async Task BufferedQuotedNewlineInWordHashOrTrailingNewline_UsesDirectInvocation(
        string command, string[] expectedArgs)
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.Parse(["-c", command]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.NotEqual(ExpectedShell, invocation.Executable);
        Assert.Equal(expectedArgs, invocation.Arguments);
    }

    [Theory]
    [InlineData("echo \"*.ts\"")]
    [InlineData("echo '{a,b}'")]
    [InlineData("echo {x}")]
    public async Task BufferedQuotedOrNonExpandingGlobBrace_UsesDirectInvocation(string command)
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.Parse(["-c", command]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.NotEqual(ExpectedShell, invocation.Executable);
    }

    [Fact]
    public async Task BufferedPlainCommand_UsesDirectInvocation()
    {
        var (root, runner) = BuildRoot();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var exitCode = await root.Parse(["-c", "ls"]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(invocation);
        Assert.NotEqual("sh", invocation.Executable);
    }

    [Fact]
    public async Task BufferedCommand_InvalidTimeoutReturnsError()
    {
        var (root, runner) = BuildRoot();

        var exitCode = await root.Parse(["--timeout-ms", "0", "-c", "echo hello"]).InvokeAsync();

        Assert.Equal(1, exitCode);
        await runner.DidNotReceive().RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>());
    }

    private static (RootCommand Root, ICommandRunner Runner) BuildRoot()
    {
        var runner = Substitute.For<ICommandRunner>();
        var root = new RootCommand();
        var supervisor = Substitute.For<IMuxSupervisor>();
        supervisor.EnsureReadyAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new MuxReadyInfo("default", "/tmp/hypa.sock", """{"ok":true,"protocol":1}"""));
        var command = new RunCommand(
            MakeService(runner),
            new ShellLexer(),
            new MuxAttachService(supervisor, new NullMuxAttachDriver()));
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

    private sealed class NullMuxAttachDriver : IMuxAttachDriver
    {
        public Task<int> RunAsync(MuxReadyInfo ready, MuxAttachRequest request, CancellationToken ct) =>
            Task.FromResult(0);
    }
}
