using System.CommandLine;
using Hypa.Cli.Attach;
using Hypa.Cli.Commands;
using Hypa.Cli.Mux;
using Hypa.Infrastructure.Rewrite;
using Hypa.Placement.Application;
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

public sealed class RemoteAttachCommandTests
{
    [Fact]
    public void Parse_accepts_remote_and_server_keybindings()
    {
        Assert.True(RemoteAttachArgs.TryParse(
            ["--remote", "dev", "--remote-keybindings", "server", "--handoff"],
            out var remote,
            out var error));
        Assert.Null(error);
        Assert.NotNull(remote);
        Assert.Equal("dev", remote.Target);
        Assert.Equal(RemoteKeybindingsMode.Server, remote.Keybindings);
        Assert.True(remote.LiveHandoff);
    }

    [Fact]
    public void Parse_rejects_keybindings_without_remote()
    {
        Assert.False(RemoteAttachArgs.TryParse(
            ["--remote-keybindings", "server"],
            out _,
            out var error));
        Assert.Equal("--remote-keybindings requires --remote", error);
    }

    [Fact]
    public void Parse_rejects_password_target()
    {
        Assert.False(RemoteAttachArgs.TryParse(
            ["--remote", "user:pw@host"],
            out _,
            out var error));
        Assert.Equal("SSH target must not contain a password", error);
    }

    [Fact]
    public void Parse_rejects_handoff_without_remote()
    {
        Assert.False(RemoteAttachArgs.TryParse(["--handoff"], out _, out var error));
        Assert.Equal("--handoff requires --remote", error);
    }

    [Fact]
    public void Parse_rejects_duplicate_remote()
    {
        Assert.False(RemoteAttachArgs.TryParse(
            ["--remote", "dev", "--remote", "other"],
            out _,
            out var error));
        Assert.Equal("--remote can only be specified once", error);
    }

    [Fact]
    public void Parse_rejects_invalid_remote_keybindings()
    {
        Assert.False(RemoteAttachArgs.TryParse(
            ["--remote", "dev", "--remote-keybindings", "bogus"],
            out _,
            out var error));
        Assert.Equal("--remote-keybindings must be 'local' or 'server'", error);
    }

    [Fact]
    public async Task Root_remote_opens_path_and_skips_local_supervisor()
    {
        var remoteMux = new StubRemoteMux();
        var (root, supervisor, driver) = BuildRoot(remoteMux);

        var exit = await root.Parse(["--remote", "dev", "--session", "agents"]).InvokeAsync();

        Assert.Equal(0, exit);
        await supervisor.DidNotReceive().EnsureReadyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        Assert.NotNull(driver.Last);
        Assert.True(driver.Last!.RemoteAttach);
        Assert.True(driver.Last.RemoteDestination);
        Assert.Equal(RemoteKeybindingsMode.Local, driver.Last.RemoteKeybindings);
        Assert.Empty(driver.Last.AttachConfig.Keys.Commands);
        Assert.Null(driver.Last.FocusPaneId);
        Assert.False(driver.Last.LiveHandoff);
        Assert.Equal(1, remoteMux.OpenCount);
        Assert.Equal("dev", remoteMux.Last!.ExplicitTarget);
        Assert.Equal("agents", remoteMux.Last.Session);
    }

    [Fact]
    public async Task Remote_keybindings_server_uses_default_keys()
    {
        var remoteMux = new StubRemoteMux();
        var (root, _, driver) = BuildRoot(remoteMux);

        var exit = await root.Parse(
                ["--remote", "dev", "--remote-keybindings", "server"])
            .InvokeAsync();

        Assert.Equal(0, exit);
        Assert.Equal(RemoteKeybindingsMode.Server, driver.Last!.RemoteKeybindings);
        Assert.Empty(driver.Last.AttachConfig.Keys.Commands);
    }

    [Fact]
    public void Help_separates_ssh_from_atomic_gateway()
    {
        Assert.Contains("not Atomic", AttachCommand.RemoteOptionDescription, StringComparison.Ordinal);
        Assert.Contains("experimental", AttachCommand.HandoffOptionDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not move Work", AttachCommand.HandoffOptionDescription, StringComparison.Ordinal);
    }

    private static (RootCommand Root, IMuxSupervisor Supervisor, RecordingMuxAttachDriver Driver) BuildRoot(
        IRemoteMuxPath remoteMux)
    {
        var runner = Substitute.For<ICommandRunner>();
        var supervisor = Substitute.For<IMuxSupervisor>();
        supervisor.EnsureReadyAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new MuxReadyInfo("default", "/tmp/hypa.sock", """{"ok":true,"protocol":1}"""));
        var driver = new RecordingMuxAttachDriver();
        var attach = new MuxAttachService(supervisor, driver, remoteMux: remoteMux);
        var root = new RootCommand("hypa");
        root.Add(new AttachCommand(attach).Build());
        new RunCommand(MakeService(runner), new ShellLexer(), attach).AttachTo(root);
        return (root, supervisor, driver);
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

    private sealed class RecordingMuxAttachDriver : IMuxAttachDriver
    {
        public MuxAttachRequest? Last { get; private set; }

        public Task<int> RunAsync(MuxReadyInfo ready, MuxAttachRequest request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(0);
        }
    }

    private sealed class StubRemoteMux : IRemoteMuxPath
    {
        public int OpenCount { get; private set; }
        public RemoteMuxOpenRequest? Last { get; private set; }

        public ValueTask<RemoteMuxOutcome> OpenAsync(
            RemoteMuxOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            OpenCount++;
            Last = request;
            return ValueTask.FromResult(RemoteMuxOutcome.Success(new RemoteMuxPath
            {
                LocalSocketPath = "/tmp/hypa-remote-stub.sock",
                Session = request.Session,
                Target = request.ExplicitTarget ?? "dev",
                Generation = 1,
            }));
        }
    }
}
