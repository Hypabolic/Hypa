using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Filters;
using Hypa.Runtime.Domain.Metrics;
using Hypa.Runtime.Domain.Runner;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class CommandRunnerServiceTests
{
    private static readonly CommandInvocation FakeInvocation =
        CommandInvocation.Buffered("echo", ["hello"], "echo hello");

    private static CommandRunnerService MakeService(
        ICommandRunner? runner = null,
        IOutputCompressor? compressor = null,
        ITokenCounter? tokenCounter = null,
        IArtifactRepository? artifacts = null,
        IEvidenceLedger? evidence = null,
        ISessionResolver? resolver = null,
        IFilterRepository? filterRepo = null,
        IFilterEngine? filterEngine = null,
        IParseMetricsRepository? parseMetrics = null)
    {
        runner ??= Substitute.For<ICommandRunner>();
        compressor ??= MakePassthroughCompressor();
        tokenCounter ??= MakeBigTokenCounter();
        artifacts ??= Substitute.For<IArtifactRepository>();
        evidence ??= Substitute.For<IEvidenceLedger>();
        resolver ??= MakeResolver();

        if (filterRepo is null)
        {
            filterRepo = Substitute.For<IFilterRepository>();
            filterRepo.GetAll().Returns([]);
        }
        if (filterEngine is null)
        {
            filterEngine = Substitute.For<IFilterEngine>();
            filterEngine.Apply(Arg.Any<CompiledFilterDefinition>(), Arg.Any<string>())
                .Returns(ci => new FilterResult(ci.ArgAt<string>(1), "none", 0));
        }
        var filterService = new FilterService(filterRepo, filterEngine);

        parseMetrics ??= Substitute.For<IParseMetricsRepository>();
        parseMetrics.RecordAsync(Arg.Any<ParseMetricsRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        return new CommandRunnerService(
            runner,
            [compressor],
            tokenCounter,
            artifacts,
            evidence,
            resolver,
            filterService,
            filterEngine,
            parseMetrics,
            NullLogger<CommandRunnerService>.Instance);
    }

    private static IOutputCompressor MakePassthroughCompressor()
    {
        var c = Substitute.For<IOutputCompressor>();
        c.Id.Returns("test");
        c.CanHandle(Arg.Any<CommandInvocation>()).Returns(true);
        c.Compress(Arg.Any<CommandInvocation>(), Arg.Any<CommandOutput>(), Arg.Any<CompressionOptions>())
            .Returns(ci =>
            {
                var output = ci.ArgAt<CommandOutput>(1);
                var tokens = output.Stdout.Length / 4 + 1;
                var half = Math.Max(1, tokens / 2);
                return CompressionResult.From(
                    output.Stdout[..(output.Stdout.Length / 2)],
                    tokens, half, "test", [], false);
            });
        return c;
    }

    private static ITokenCounter MakeBigTokenCounter()
    {
        var c = Substitute.For<ITokenCounter>();
        c.EstimateTokens(Arg.Any<string>()).Returns(ci =>
        {
            var s = ci.ArgAt<string>(0);
            return Math.Max(1, s.Length / 4);
        });
        return c;
    }

    private static IOutputCompressor MakeStdoutOnlyCompressor()
    {
        var c = Substitute.For<IOutputCompressor>();
        c.Id.Returns("stdout-only");
        c.CanHandle(Arg.Any<CommandInvocation>()).Returns(true);
        c.Compress(Arg.Any<CommandInvocation>(), Arg.Any<CommandOutput>(), Arg.Any<CompressionOptions>())
            .Returns(ci =>
            {
                var output = ci.ArgAt<CommandOutput>(1);
                return CompressionResult.From(output.Stdout, 100, 10, "stdout-only", [], false);
            });
        return c;
    }

    private static ISessionResolver MakeResolver()
    {
        var r = Substitute.For<ISessionResolver>();
        var session = new ContextSession { Id = Guid.NewGuid(), ProjectRoot = "/tmp" };
        r.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Ok(session));
        return r;
    }

    [Fact]
    public async Task RunBufferedAsync_Success_ReturnsText()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 0, TimeSpan.Zero)));

        var service = MakeService(runner: runner);
        var result = await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.NotEmpty(result.Value.Text);
    }

    [Fact]
    public async Task RunBufferedAsync_PreservesExitCode()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 42, TimeSpan.Zero)));

        var service = MakeService(runner: runner);
        var result = await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value.ExitCode);
    }

    [Fact]
    public async Task RunBufferedAsync_RunnerFail_PropagatesError()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Fail(new Error("ERR", "fail")));

        var service = MakeService(runner: runner);
        var result = await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("ERR", result.Error.Code);
    }

    [Fact]
    public async Task RunBufferedAsync_SmallOutput_SkipsCompression()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("hi", "", 0, TimeSpan.Zero)));

        var compressor = Substitute.For<IOutputCompressor>();
        var service = MakeService(runner: runner, compressor: compressor);

        var opts = CompressionOptions.Default with { SmallOutputThreshold = 1000 };
        await service.RunBufferedAsync(FakeInvocation, opts, CancellationToken.None);

        compressor.DidNotReceive().Compress(
            Arg.Any<CommandInvocation>(), Arg.Any<CommandOutput>(), Arg.Any<CompressionOptions>());
    }

    [Fact]
    public async Task RunBufferedAsync_TeeOnFailure_StoresArtifact()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 1, TimeSpan.Zero)));

        var artifacts = Substitute.For<IArtifactRepository>();
        artifacts.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result<ArtifactRef, Error>.Ok(new ArtifactRef
            {
                Id = Guid.NewGuid(),
                SessionId = Guid.NewGuid(),
                MimeType = "text/plain",
                SizeBytes = 0,
                StoragePath = "/tmp/artifact",
            }));

        var service = MakeService(runner: runner, artifacts: artifacts);
        var opts = CompressionOptions.Default with { TeeOnFailure = true };
        await service.RunBufferedAsync(FakeInvocation, opts, CancellationToken.None);

        await artifacts.Received(1).StoreAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBufferedAsync_TeeOnFailureFalse_DoesNotStore()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 1, TimeSpan.Zero)));

        var artifacts = Substitute.For<IArtifactRepository>();
        var service = MakeService(runner: runner, artifacts: artifacts);

        await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        await artifacts.DidNotReceive().StoreAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBufferedAsync_RecordsCommandMetrics()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 0, TimeSpan.Zero)));

        var evidence = Substitute.For<IEvidenceLedger>();
        var service = MakeService(runner: runner, evidence: evidence);

        await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        await evidence.Received(1).RecordCommandMetricsAsync(
            Arg.Any<CommandMetricsRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBufferedAsync_RecordsParseMetricsWithCommandMetricsId()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 0, TimeSpan.Zero)));

        CommandMetricsRecord? commandMetrics = null;
        var evidence = Substitute.For<IEvidenceLedger>();
        evidence.RecordCommandMetricsAsync(Arg.Do<CommandMetricsRecord>(r => commandMetrics = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ParseMetricsRecord? parseMetricsRecord = null;
        var parseMetrics = Substitute.For<IParseMetricsRepository>();
        parseMetrics.RecordAsync(Arg.Do<ParseMetricsRecord>(r => parseMetricsRecord = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var service = MakeService(runner: runner, evidence: evidence, parseMetrics: parseMetrics);

        await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        Assert.NotNull(commandMetrics);
        Assert.NotNull(parseMetricsRecord);
        Assert.Equal(commandMetrics.Id.ToString(), parseMetricsRecord.RunId);
    }

    [Fact]
    public async Task RunBufferedAsync_RecordsCompressedTokensAfterFilters()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 0, TimeSpan.Zero)));

        var filter = new CompiledFilterDefinition { Id = "summary", AppliesTo = ["echo"], Stages = [] };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([filter]);

        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(filter, Arg.Any<string>())
            .Returns(new FilterResult("short summary", filter.Id, 1));

        CommandMetricsRecord? commandMetrics = null;
        var evidence = Substitute.For<IEvidenceLedger>();
        evidence.RecordCommandMetricsAsync(Arg.Do<CommandMetricsRecord>(r => commandMetrics = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var service = MakeService(
            runner: runner,
            evidence: evidence,
            filterRepo: filterRepo,
            filterEngine: filterEngine);

        var result = await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.NotNull(commandMetrics);
        Assert.Equal(100, commandMetrics.OriginalTokens);
        Assert.Equal(3, commandMetrics.CompressedTokens);
        Assert.Contains("100\u21923 tok", result.Value.Text);
    }

    [Fact]
    public async Task RunBufferedAsync_AppliesSpecificFilterBeforeUniversalFilter()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("\x1B[31mProgram.cs(1,1): error CS1001: bad\x1B[0m\nnoise", "", 1, TimeSpan.Zero)));

        var universal = new CompiledFilterDefinition { Id = "ansi-strip", AppliesTo = [], Stages = [] };
        var dotnet = new CompiledFilterDefinition { Id = "dotnet-msbuild-noise", AppliesTo = ["dotnet"], Stages = [] };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([universal, dotnet]);

        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(universal, Arg.Any<string>())
            .Returns(ci => new FilterResult(ci.ArgAt<string>(1).Replace("\x1B[31m", "").Replace("\x1B[0m", ""), universal.Id, 1));
        filterEngine.Apply(dotnet, Arg.Any<string>())
            .Returns(new FilterResult("Program.cs(1,1): error CS1001: bad", dotnet.Id, 2));

        var parseMetrics = Substitute.For<IParseMetricsRepository>();
        ParseMetricsRecord? recorded = null;
        parseMetrics.RecordAsync(Arg.Do<ParseMetricsRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var service = MakeService(
            runner: runner,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            parseMetrics: parseMetrics);
        var invocation = CommandInvocation.Buffered("dotnet", ["build"], "dotnet build");

        var result = await service.RunBufferedAsync(invocation, CompressionOptions.Default, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("Program.cs(1,1): error CS1001: bad", result.Value.Text);
        Assert.NotNull(recorded);
        Assert.Equal("dotnet-msbuild-noise", recorded.FilterId);
        filterEngine.Received(1).Apply(dotnet, Arg.Any<string>());
        filterEngine.DidNotReceive().Apply(universal, Arg.Any<string>());
    }

    [Fact]
    public async Task RunBufferedAsync_SkipsFilter_WhenFilterVoidsNonEmptyOutput()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 0, TimeSpan.Zero)));

        var voiding = new CompiledFilterDefinition { Id = "keep-errors", AppliesTo = [], Stages = [] };
        var useful = new CompiledFilterDefinition { Id = "ansi-strip", AppliesTo = [], Stages = [] };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([voiding, useful]);

        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(voiding, Arg.Any<string>())
            .Returns(ci => new FilterResult("", voiding.Id, 1));
        filterEngine.Apply(useful, Arg.Any<string>())
            .Returns(ci => new FilterResult(ci.ArgAt<string>(1) + "-filtered", useful.Id, 1));

        var service = MakeService(runner: runner, filterRepo: filterRepo, filterEngine: filterEngine);
        var result = await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Contains("-filtered", result.Value.Text);
    }

    [Fact]
    public async Task RunBufferedAsync_PreservesOriginalOutput_WhenAllFiltersVoid()
    {
        var runner = Substitute.For<ICommandRunner>();
        var outputText = new string('x', 400);
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(outputText, "", 0, TimeSpan.Zero)));

        var voiding = new CompiledFilterDefinition { Id = "keep-errors", AppliesTo = [], Stages = [] };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([voiding]);

        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(voiding, Arg.Any<string>())
            .Returns(ci => new FilterResult("", voiding.Id, 1));

        var service = MakeService(runner: runner, filterRepo: filterRepo, filterEngine: filterEngine);
        var result = await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.Text));
    }

    [Fact]
    public async Task RunBufferedAsync_MergesStderr_WhenFilterRequestsMergeStderr()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "stderr diagnostic", 1, TimeSpan.Zero)));

        var filter = new CompiledFilterDefinition { Id = "liquibase", AppliesTo = ["echo"], MergeStderr = true, Stages = [] };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([filter]);

        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(filter, Arg.Any<string>())
            .Returns(ci => new FilterResult(ci.ArgAt<string>(1), filter.Id, 0));

        var service = MakeService(
            runner: runner,
            compressor: MakeStdoutOnlyCompressor(),
            filterRepo: filterRepo,
            filterEngine: filterEngine);

        await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        filterEngine.Received(1).Apply(
            filter,
            Arg.Is<string>(text => text.Contains("stderr diagnostic") && text.StartsWith(new string('x', 400))));
    }

    [Fact]
    public async Task RunBufferedAsync_DoesNotMergeStderr_WhenFlagIsFalse()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "stderr diagnostic", 1, TimeSpan.Zero)));

        var filter = new CompiledFilterDefinition { Id = "stdout-filter", AppliesTo = ["echo"], MergeStderr = false, Stages = [] };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([filter]);

        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(filter, Arg.Any<string>())
            .Returns(ci => new FilterResult(ci.ArgAt<string>(1), filter.Id, 0));

        var service = MakeService(
            runner: runner,
            compressor: MakeStdoutOnlyCompressor(),
            filterRepo: filterRepo,
            filterEngine: filterEngine);

        await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        filterEngine.Received(1).Apply(
            filter,
            Arg.Is<string>(text => !text.Contains("stderr diagnostic") && text == new string('x', 400)));
    }

    [Fact]
    public async Task RunPassthroughAsync_ReturnsExitCode()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("", "", 7, TimeSpan.Zero)));

        var service = MakeService(runner: runner);
        var result = await service.RunPassthroughAsync(FakeInvocation, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(7, result.Value);
    }

    [Fact]
    public async Task RunPassthroughAsync_DoesNotCallCompressor()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("", "", 0, TimeSpan.Zero)));

        var compressor = Substitute.For<IOutputCompressor>();
        var service = MakeService(runner: runner, compressor: compressor);

        await service.RunPassthroughAsync(FakeInvocation, CancellationToken.None);

        compressor.DidNotReceive().Compress(
            Arg.Any<CommandInvocation>(), Arg.Any<CommandOutput>(), Arg.Any<CompressionOptions>());
    }

    [Fact]
    public async Task RunPassthroughAsync_PassesModeThroughToRunner()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("", "", 0, TimeSpan.Zero)));

        var service = MakeService(runner: runner);
        await service.RunPassthroughAsync(FakeInvocation, CancellationToken.None);

        await runner.Received(1).RunAsync(
            Arg.Is<CommandInvocation>(i => i.Mode == ToolRunMode.Passthrough),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBuffered_WhenSessionSaveFails_RunsCommandAndReturnsUnderlyingExitCode()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 5, TimeSpan.Zero)));

        var resolver = Substitute.For<ISessionResolver>();
        resolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("storage.access_denied", "DB is read-only")));

        var service = MakeService(runner: runner, resolver: resolver);
        var result = await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(5, result.Value.ExitCode);
    }

    [Fact]
    public async Task RunBuffered_WhenTelemetryWriteFails_RunsCommandAndReturnsUnderlyingExitCode()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 0, TimeSpan.Zero)));

        var evidence = Substitute.For<IEvidenceLedger>();
        evidence.RecordCommandMetricsAsync(Arg.Any<CommandMetricsRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("storage unavailable"));

        var service = MakeService(runner: runner, evidence: evidence);
        var result = await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(0, result.Value.ExitCode);
    }

    [Fact]
    public async Task RunPassthrough_WhenTelemetryWriteFails_ReturnsUnderlyingExitCode()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("", "", 9, TimeSpan.Zero)));

        var evidence = Substitute.For<IEvidenceLedger>();
        evidence.RecordCommandMetricsAsync(Arg.Any<CommandMetricsRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("storage unavailable"));

        var service = MakeService(runner: runner, evidence: evidence);
        var result = await service.RunPassthroughAsync(FakeInvocation, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(9, result.Value);
    }
}
