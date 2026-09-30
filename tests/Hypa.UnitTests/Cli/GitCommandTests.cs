using System.CommandLine;
using Hypa.Cli.Commands;
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

public sealed class GitCommandTests
{
    [Fact]
    public async Task GitStatus_BuffersThroughCommandRunner()
    {
        var runner = Substitute.For<ICommandRunner>();
        CommandInvocation? invocation = null;
        runner.RunAsync(Arg.Do<CommandInvocation>(i => invocation = i), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("ok", "", 0, TimeSpan.Zero)));

        var root = new RootCommand();
        root.Add(new GitCommand(MakeService(runner)).Build());

        var exit = await root.Parse(["git", "status"]).InvokeAsync();

        Assert.Equal(0, exit);
        Assert.NotNull(invocation);
        Assert.Equal("git", invocation.Executable);
        Assert.Equal(["status"], invocation.Arguments);
        Assert.Equal(ToolRunMode.Buffered, invocation.Mode);
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
