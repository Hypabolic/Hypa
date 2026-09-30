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

public sealed class MuxAttachCommandTests
{
    [Fact]
    public async Task EmptyArgv_CallsSupervisor()
    {
        var (root, supervisor, runner) = BuildRoot();

        var exit = await root.Parse([]).InvokeAsync();

        Assert.Equal(0, exit);
        await supervisor.Received(1).EnsureReadyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await runner.DidNotReceive().RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AttachOnce_CallsSupervisor()
    {
        var (root, supervisor, runner) = BuildRoot();

        var exit = await root.Parse(["attach", "--once"]).InvokeAsync();

        Assert.Equal(0, exit);
        await supervisor.Received(1).EnsureReadyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await runner.DidNotReceive().RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BufferedDashC_DoesNotAttach()
    {
        var (root, supervisor, runner) = BuildRoot();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("hello", "", 0, TimeSpan.Zero)));

        var exit = await root.Parse(["-c", "echo hello"]).InvokeAsync();

        Assert.Equal(0, exit);
        await supervisor.DidNotReceive().EnsureReadyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RootSessionOption_CallsAttachForThatName()
    {
        var (root, supervisor, runner) = BuildRoot();

        var exit = await root.Parse(["--session", "named"]).InvokeAsync();

        Assert.Equal(0, exit);
        await supervisor.Received(1).EnsureReadyAsync(
            "named",
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await runner.DidNotReceive().RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Session_flag_still_wins_over_resolver()
    {
        var (root, supervisor, _) = BuildRoot();

        var exit = await root.Parse(["--session", "explicit"]).InvokeAsync();

        Assert.Equal(0, exit);
        await supervisor.Received(1).EnsureReadyAsync(
            "explicit",
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyArgv_UsesResolverDefault()
    {
        var (root, supervisor, _) = BuildRoot();

        var exit = await root.Parse([]).InvokeAsync();

        Assert.Equal(0, exit);
        await supervisor.Received(1).EnsureReadyAsync(
            "default",
            Arg.Is<string?>(cwd => cwd == null),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bare_attach_omits_implicit_cwd()
    {
        var (root, supervisor, _) = BuildRoot();

        var exit = await root.Parse([]).InvokeAsync();

        Assert.Equal(0, exit);
        await supervisor.Received(1).EnsureReadyAsync(
            Arg.Any<string>(),
            Arg.Is<string?>(cwd => cwd == null),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Attach_explicit_cwd_is_forwarded()
    {
        var (root, supervisor, _) = BuildRoot();

        var exit = await root.Parse(["attach", "--cwd", "/tmp/explicit-h35"]).InvokeAsync();

        Assert.Equal(0, exit);
        await supervisor.Received(1).EnsureReadyAsync(
            Arg.Any<string>(),
            "/tmp/explicit-h35",
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DefaultConfig_DoesNotCallSupervisor()
    {
        var (root, supervisor, _) = BuildRoot();
        var captured = new StringWriter();
        var old = Console.Out;
        Console.SetOut(captured);
        try
        {
            var exit = await root.Parse(["--default-config"]).InvokeAsync();
            Assert.Equal(0, exit);
        }
        finally
        {
            Console.SetOut(old);
        }

        Assert.Contains("[keys]", captured.ToString(), StringComparison.Ordinal);
        await supervisor.DidNotReceive().EnsureReadyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Attach_remote_destination_omits_mux_stop_on_live_banner()
    {
        var (root, _, _, driver) = BuildRootWithRecorder();

        var exit = await root.Parse(["attach", "--remote-destination", "--once"]).InvokeAsync();

        Assert.Equal(0, exit);
        Assert.NotNull(driver.Last);
        Assert.True(driver.Last!.RemoteDestination);
        var banner = AttachSession.FormatLiveDetachBanner("connection closed", driver.Last);
        Assert.DoesNotContain("hypa mux stop", banner, StringComparison.Ordinal);
        Assert.Contains("still running", banner, StringComparison.Ordinal);
        Assert.Equal(
            "disconnected from destination. Destination mux is still running.",
            banner);
        Assert.DoesNotContain("Attach again after the relay returns", banner, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bare_remote_destination_sets_attach_request()
    {
        var (root, _, _, driver) = BuildRootWithRecorder();

        var exit = await root.Parse(["--remote-destination"]).InvokeAsync();

        Assert.Equal(0, exit);
        Assert.NotNull(driver.Last);
        Assert.True(driver.Last!.RemoteDestination);
        Assert.DoesNotContain(
            "hypa mux stop",
            AttachSession.FormatLiveDetachBanner("connection closed", driver.Last),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remote_destination_help_keeps_attach_local()
    {
        var (root, _, _) = BuildRoot();
        AssertRemoteDestinationHelp(root.Options);
        var attach = Assert.Single(root.Subcommands, c => c.Name == "attach");
        AssertRemoteDestinationHelp(attach.Options);

        var captured = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        Console.SetOut(captured);
        Console.SetError(captured);
        try
        {
            Assert.Equal(0, await root.Parse(["attach", "--help"]).InvokeAsync());
            Assert.Equal(0, await root.Parse(["--help"]).InvokeAsync());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }

        var help = captured.ToString();
        Assert.Contains(AttachCommand.RemoteDestinationOptionDescription, help, StringComparison.Ordinal);
        Assert.DoesNotContain("Attach to a remote destination mux", help, StringComparison.Ordinal);
        Assert.DoesNotContain("remote destination mux", help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Help_DoesNotAttach()
    {
        var (root, supervisor, runner) = BuildRoot();

        var exit = await root.Parse(["--help"]).InvokeAsync();

        Assert.Equal(0, exit);
        await supervisor.DidNotReceive().EnsureReadyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await runner.DidNotReceive().RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>());
    }

    private static void AssertRemoteDestinationHelp(IEnumerable<Option> options)
    {
        var option = Assert.Single(
            options,
            o => o.Name.Contains("remote-destination", StringComparison.Ordinal)
                || o.Aliases.Contains("--remote-destination"));
        Assert.Equal(AttachCommand.RemoteDestinationOptionDescription, option.Description);
        Assert.DoesNotContain("Attach to a remote destination mux", option.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("remote destination mux", option.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static (RootCommand Root, IMuxSupervisor Supervisor, ICommandRunner Runner) BuildRoot()
    {
        var runner = Substitute.For<ICommandRunner>();
        var supervisor = Substitute.For<IMuxSupervisor>();
        supervisor.EnsureReadyAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new MuxReadyInfo("default", "/tmp/hypa.sock", """{"ok":true,"protocol":1}"""));

        var attach = new MuxAttachService(supervisor, new StubMuxAttachDriver());
        var root = new RootCommand("hypa — workspace mux and context optimisation for AI harnesses.");
        root.Add(new AttachCommand(attach).Build());
        new RunCommand(MakeService(runner), new ShellLexer(), attach).AttachTo(root);
        return (root, supervisor, runner);
    }

    private static (RootCommand Root, IMuxSupervisor Supervisor, ICommandRunner Runner, RecordingMuxAttachDriver Driver)
        BuildRootWithRecorder()
    {
        var runner = Substitute.For<ICommandRunner>();
        var supervisor = Substitute.For<IMuxSupervisor>();
        supervisor.EnsureReadyAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new MuxReadyInfo("default", "/tmp/hypa.sock", """{"ok":true,"protocol":1}"""));

        var driver = new RecordingMuxAttachDriver();
        var attach = new MuxAttachService(supervisor, driver);
        var root = new RootCommand("hypa — workspace mux and context optimisation for AI harnesses.");
        root.Add(new AttachCommand(attach).Build());
        new RunCommand(MakeService(runner), new ShellLexer(), attach).AttachTo(root);
        return (root, supervisor, runner, driver);
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

    private sealed class StubMuxAttachDriver : IMuxAttachDriver
    {
        public Task<int> RunAsync(MuxReadyInfo ready, MuxAttachRequest request, CancellationToken ct) =>
            Task.FromResult(0);
    }

    private sealed class RecordingMuxAttachDriver : IMuxAttachDriver
    {
        public MuxAttachRequest? Last { get; private set; }

        public Task<int> RunAsync(MuxReadyInfo ready, MuxAttachRequest request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(0);
        }
    }
}
