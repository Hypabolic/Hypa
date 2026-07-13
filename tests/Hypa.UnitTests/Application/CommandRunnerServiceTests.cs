using System.Text.RegularExpressions;
using Hypa.Infrastructure.Filters;
using Hypa.Infrastructure.Reducers;
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
        IConfigLoader? configLoader = null,
        IFilterRepository? filterRepo = null,
        IFilterEngine? filterEngine = null,
        IParseMetricsRepository? parseMetrics = null,
        IPackageManagerScriptResolver? packageScriptResolver = null)
    {
        runner ??= Substitute.For<ICommandRunner>();
        compressor ??= MakePassthroughCompressor();
        tokenCounter ??= MakeBigTokenCounter();
        artifacts ??= Substitute.For<IArtifactRepository>();
        evidence ??= Substitute.For<IEvidenceLedger>();
        resolver ??= MakeResolver();
        configLoader ??= MakeConfigLoader(HypaConfig.Default);
        packageScriptResolver ??= Substitute.For<IPackageManagerScriptResolver>();

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
            configLoader,
            packageScriptResolver,
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

    private static IConfigLoader MakeConfigLoader(HypaConfig config)
    {
        var loader = Substitute.For<IConfigLoader>();
        loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<HypaConfig, Error>.Ok(config));
        return loader;
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
    public async Task RunBufferedAsync_Timeout_ReturnsDiagnosticAndTimeoutExitCode()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.CreateTimedOut(TimeSpan.FromSeconds(30), "partial output\n")));

        CommandMetricsRecord? commandMetrics = null;
        var evidence = Substitute.For<IEvidenceLedger>();
        evidence.RecordCommandMetricsAsync(Arg.Do<CommandMetricsRecord>(r => commandMetrics = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var service = MakeService(runner: runner, evidence: evidence);
        var result = await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(CommandOutput.TimeoutExitCode, result.Value.ExitCode);
        Assert.Contains("partial output", result.Value.Text);
        Assert.Contains("command timed out after 30s", result.Value.Text);
        Assert.NotNull(commandMetrics);
        Assert.Equal(CommandOutput.TimeoutExitCode, commandMetrics.ExitCode);
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
    public async Task RunBufferedAsync_ConfigCanHideCompressionMetadata()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 0, TimeSpan.Zero)));

        var configLoader = MakeConfigLoader(HypaConfig.Default with { ShowCompressionMetadata = false });
        var service = MakeService(runner: runner, configLoader: configLoader);

        var result = await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.DoesNotContain("[hypa:", result.Value.Text);
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
    public async Task RunBufferedAsync_ResolvedPackageScript_UsesFullResolvedCommandForFilterAndRawInvocationEverywhereElse()
    {
        var invocation = CommandInvocation.Buffered(
            "pnpm",
            ["test", "--", "--reporter=dot"],
            "pnpm test -- --reporter=dot");
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 0, TimeSpan.Zero)));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("jest", "jest --runInBand"));

        var jestFilter = new CompiledFilterDefinition
        {
            Id = "jest",
            AppliesTo = ["jest"],
            MatchCommand = @"^jest\s+--runInBand$",
            CompiledMatchCommand = new Regex(@"^jest\s+--runInBand$", RegexOptions.CultureInvariant),
            Stages = [],
        };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([jestFilter]);
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(jestFilter, Arg.Any<string>())
            .Returns(new FilterResult("jest-filtered", jestFilter.Id, 1));

        CommandMetricsRecord? commandMetrics = null;
        var evidence = Substitute.For<IEvidenceLedger>();
        evidence.RecordCommandMetricsAsync(
                Arg.Do<CommandMetricsRecord>(record => commandMetrics = record),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        ParseMetricsRecord? parseMetrics = null;
        var parseMetricsRepository = Substitute.For<IParseMetricsRepository>();
        parseMetricsRepository.RecordAsync(
                Arg.Do<ParseMetricsRecord>(record => parseMetrics = record),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var compressor = MakePassthroughCompressor();
        var service = MakeService(
            runner: runner,
            compressor: compressor,
            evidence: evidence,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            parseMetrics: parseMetricsRepository,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default,
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Contains("jest-filtered", result.Value.Text);
        await runner.Received(1).RunAsync(
            Arg.Is<CommandInvocation>(candidate => ReferenceEquals(candidate, invocation)),
            Arg.Any<CancellationToken>());
        packageScriptResolver.Received(1).TryResolve(invocation);
        compressor.Received(1).CanHandle(
            Arg.Is<CommandInvocation>(candidate => ReferenceEquals(candidate, invocation)));
        compressor.Received(1).Compress(
            Arg.Is<CommandInvocation>(candidate => ReferenceEquals(candidate, invocation)),
            Arg.Any<CommandOutput>(),
            Arg.Any<CompressionOptions>());
        filterEngine.Received(1).Apply(jestFilter, Arg.Any<string>());

        Assert.NotNull(commandMetrics);
        Assert.Equal("pnpm test -- --reporter=dot", commandMetrics.Command);
        Assert.NotNull(parseMetrics);
        Assert.Equal("pnpm", parseMetrics.Executable);
        Assert.Equal("test -- --reporter=dot", parseMetrics.Arguments);
        Assert.Equal(jestFilter.Id, parseMetrics.FilterId);
    }

    [Fact]
    public async Task RunBufferedAsync_ResolvedEslintFilter_ReceivesRawPackageScriptOutput()
    {
        const string fileName = "/workspace/src/dashboard.ts";
        const string summary = "✖ 80 problems (80 errors, 0 warnings)";
        var violations = Enumerable.Range(1, 80)
            .Select(line => $"  {line}:5  error  Unexpected console statement  no-console");
        var rawOutput = fileName + "\n" +
            string.Join('\n', violations) +
            "\n\n" + summary;
        var invocation = CommandInvocation.Buffered("pnpm", ["lint"], "pnpm lint");
        var captured = CommandOutput.Captured(rawOutput, "", 1, TimeSpan.Zero);
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(captured));

        var tokenCounter = MakeBigTokenCounter();
        var compressor = new PackageManagerOutputCompressor(tokenCounter);
        var options = CompressionOptions.Default with { ShowCompressionMetadata = false };
        var packageManagerResult = compressor.Compress(invocation, captured, options);
        Assert.DoesNotContain(fileName, packageManagerResult.Text);
        Assert.DoesNotContain(summary, packageManagerResult.Text);

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("eslint", "eslint --max-warnings 0 ."));
        var eslintFilter = BuiltInFilters.All.Single(filter => filter.Id == "eslint");
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([eslintFilter]);
        var service = MakeService(
            runner: runner,
            compressor: compressor,
            tokenCounter: tokenCounter,
            filterRepo: filterRepo,
            filterEngine: new FilterEngine(),
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(invocation, options, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Contains(fileName, result.Value.Text);
        Assert.Contains(summary, result.Value.Text);
    }

    [Fact]
    public async Task RunBufferedAsync_TimedOutResolvedFilterWithoutMerge_ReceivesCombinedStreamAndAppendsTimeoutAfterFilteredResult()
    {
        const string stderr = "fatal: test worker exited before producing stdout" +
            "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!" +
            "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!";
        const string filtered = "test worker: failed";
        const string timeoutDiagnostic =
            "[hypa: command timed out after 30s; killed process; exit=124; elapsed=31s]";
        var invocation = CommandInvocation.Buffered("pnpm", ["test"], "pnpm test");
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.CreateTimedOut(TimeSpan.FromSeconds(31), stderr: stderr)));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("jest", "jest --runInBand"));
        var filter = new CompiledFilterDefinition
        {
            Id = "jest",
            AppliesTo = ["jest"],
            MergeStderr = false,
            Stages = [],
        };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([filter]);
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(filter, Arg.Any<string>())
            .Returns(new FilterResult(filtered, filter.Id, 1));
        var service = MakeService(
            runner: runner,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with { ShowCompressionMetadata = false },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(CommandOutput.TimeoutExitCode, result.Value.ExitCode);
        Assert.Equal(filtered + "\n" + timeoutDiagnostic, result.Value.Text);
        Assert.DoesNotContain(stderr, result.Value.Text);
        Assert.Single(Regex.Matches(result.Value.Text, Regex.Escape(timeoutDiagnostic)));
        filterEngine.Received(1).Apply(filter, stderr);
    }

    [Theory]
    [InlineData("jest", "jest --runInBand")]
    [InlineData("vitest", "vitest run")]
    public async Task RunBufferedAsync_ResolvedJestOrVitest_ProcessesStderrOnlyFailureReport(
        string executable,
        string resolvedCommand)
    {
        const string stderr = "\x1B[31mFAIL src/checkout.test.ts\x1B[0m\n" +
            "Tests: 1 failed, 2 passed, 3 total\n" +
            "Expected true\n" +
            "Received false";
        const string expected = "FAIL src/checkout.test.ts\n" +
            "Tests: 1 failed, 2 passed, 3 total\n" +
            "Expected true\n" +
            "Received false";
        var invocation = CommandInvocation.Buffered("pnpm", ["test"], "pnpm test");
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("", stderr, 1, TimeSpan.Zero)));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript(executable, resolvedCommand));
        var filter = BuiltInFilters.All.Single(candidate => candidate.Id == "jest");
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([filter]);
        var realFilterEngine = new FilterEngine();
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(filter, Arg.Any<string>())
            .Returns(ci => realFilterEngine.Apply(filter, ci.ArgAt<string>(1)));
        var service = MakeService(
            runner: runner,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with { ShowCompressionMetadata = false },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(1, result.Value.ExitCode);
        Assert.Equal(expected, result.Value.Text);
        Assert.DoesNotContain("jest: ok", result.Value.Text);
        filterEngine.Received(1).Apply(filter, stderr);
    }

    [Fact]
    public async Task RunBufferedAsync_ResolvedJest_NonzeroCoverageThresholdFailureRejectsMatchOutputSuccess()
    {
        const string coverageFailure =
            "Jest: \"global\" coverage threshold for statements (90%) not met: 80%";
        const string rawOutput = "Tests: 4 passed, 4 total\n" + coverageFailure;
        const string compressedFallback = "\x1B[31m" + coverageFailure + "\x1B[0m";
        var invocation = CommandInvocation.Buffered("pnpm", ["test"], "pnpm test");
        var captured = CommandOutput.Captured(rawOutput, "", 1, TimeSpan.Zero);
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(captured));

        var compressor = Substitute.For<IOutputCompressor>();
        compressor.Id.Returns("package-manager");
        compressor.CanHandle(Arg.Any<CommandInvocation>()).Returns(true);
        compressor.Compress(
                Arg.Any<CommandInvocation>(),
                Arg.Any<CommandOutput>(),
                Arg.Any<CompressionOptions>())
            .Returns(CompressionResult.From(
                compressedFallback,
                100,
                1,
                "package-manager",
                [],
                false));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("jest", "jest --runInBand"));
        var jestFilter = BuiltInFilters.All.Single(candidate => candidate.Id == "jest");
        var universal = BuiltInFilters.All.Single(candidate => candidate.Id == "ansi-strip");
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([jestFilter, universal]);
        var realFilterEngine = new FilterEngine();
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(jestFilter, Arg.Any<string>())
            .Returns(ci => realFilterEngine.Apply(jestFilter, ci.ArgAt<string>(1)));
        filterEngine.Apply(universal, Arg.Any<string>())
            .Returns(ci => realFilterEngine.Apply(universal, ci.ArgAt<string>(1)));
        var service = MakeService(
            runner: runner,
            compressor: compressor,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with
            {
                SmallOutputThreshold = 0,
                ShowCompressionMetadata = false,
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(1, result.Value.ExitCode);
        Assert.Equal(coverageFailure, result.Value.Text);
        Assert.DoesNotContain("jest: ok (all passed)", result.Value.Text);
        compressor.Received(1).Compress(
            invocation,
            captured,
            Arg.Any<CompressionOptions>());
        filterEngine.Received(1).Apply(jestFilter, rawOutput);
        filterEngine.Received(1).Apply(universal, compressedFallback);
    }

    [Fact]
    public async Task RunBufferedAsync_ResolvedJest_ZeroExitPassingSummaryRetainsMatchOutputSuccess()
    {
        const string rawOutput = "Tests: 4 passed, 4 total";
        var invocation = CommandInvocation.Buffered("pnpm", ["test"], "pnpm test");
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(rawOutput, "", 0, TimeSpan.Zero)));

        var compressor = Substitute.For<IOutputCompressor>();
        compressor.Id.Returns("package-manager");
        compressor.CanHandle(Arg.Any<CommandInvocation>()).Returns(true);
        compressor.Compress(
                Arg.Any<CommandInvocation>(),
                Arg.Any<CommandOutput>(),
                Arg.Any<CompressionOptions>())
            .Returns(CompressionResult.From(
                "compressed output that should not win",
                100,
                1,
                "package-manager",
                [],
                false));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("jest", "jest --runInBand"));
        var jestFilter = BuiltInFilters.All.Single(candidate => candidate.Id == "jest");
        var universal = BuiltInFilters.All.Single(candidate => candidate.Id == "ansi-strip");
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([jestFilter, universal]);
        var realFilterEngine = new FilterEngine();
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(jestFilter, Arg.Any<string>())
            .Returns(ci => realFilterEngine.Apply(jestFilter, ci.ArgAt<string>(1)));
        filterEngine.Apply(universal, Arg.Any<string>())
            .Returns(ci => realFilterEngine.Apply(universal, ci.ArgAt<string>(1)));
        var service = MakeService(
            runner: runner,
            compressor: compressor,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with
            {
                SmallOutputThreshold = 0,
                ShowCompressionMetadata = false,
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal("jest: ok (all passed)", result.Value.Text);
        compressor.Received(1).Compress(
            invocation,
            Arg.Any<CommandOutput>(),
            Arg.Any<CompressionOptions>());
        filterEngine.Received(1).Apply(jestFilter, rawOutput);
        filterEngine.DidNotReceive().Apply(universal, Arg.Any<string>());
    }

    [Theory]
    [InlineData("jest", "jest --runInBand")]
    [InlineData("vitest", "vitest run")]
    public async Task RunBufferedAsync_ResolvedJestOrVitest_NonzeroEmptyOutputRejectsOnEmptySuccess(
        string executable,
        string resolvedCommand)
    {
        var invocation = CommandInvocation.Buffered("pnpm", ["test"], "pnpm test");
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("", "", 1, TimeSpan.Zero)));

        var compressor = Substitute.For<IOutputCompressor>();
        compressor.Id.Returns("package-manager");
        compressor.CanHandle(Arg.Any<CommandInvocation>()).Returns(true);

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript(executable, resolvedCommand));
        var jestFilter = BuiltInFilters.All.Single(candidate => candidate.Id == "jest");
        var universal = BuiltInFilters.All.Single(candidate => candidate.Id == "ansi-strip");
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([jestFilter, universal]);
        var realFilterEngine = new FilterEngine();
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(jestFilter, Arg.Any<string>())
            .Returns(ci => realFilterEngine.Apply(jestFilter, ci.ArgAt<string>(1)));
        filterEngine.Apply(universal, Arg.Any<string>())
            .Returns(ci => realFilterEngine.Apply(universal, ci.ArgAt<string>(1)));
        var service = MakeService(
            runner: runner,
            compressor: compressor,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with { ShowCompressionMetadata = false },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(1, result.Value.ExitCode);
        Assert.Empty(result.Value.Text);
        Assert.DoesNotContain("jest: ok", result.Value.Text);
        filterEngine.Received(1).Apply(jestFilter, "");
        filterEngine.Received(1).Apply(universal, "");
        compressor.DidNotReceive().Compress(
            Arg.Any<CommandInvocation>(), Arg.Any<CommandOutput>(), Arg.Any<CompressionOptions>());
    }

    [Fact]
    public async Task RunBufferedAsync_TimedOutResolvedVitest_WrapperOnlyOutputRejectsOnEmptySuccess()
    {
        const string wrapperOnly = "> workspace@1.0.0 test\n> vitest run";
        const string compressedFallback = "\x1B[31mcompressed package-manager wrapper\x1B[0m";
        const string filteredFallback = "compressed package-manager wrapper";
        const string timeoutDiagnostic =
            "[hypa: command timed out after 30s; killed process; exit=124; elapsed=31s]";
        var composedFallback = compressedFallback + "\n" + timeoutDiagnostic;
        var invocation = CommandInvocation.Buffered("pnpm", ["test"], "pnpm test");
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.CreateTimedOut(TimeSpan.FromSeconds(31), wrapperOnly)));

        var compressor = Substitute.For<IOutputCompressor>();
        compressor.Id.Returns("package-manager");
        compressor.CanHandle(Arg.Any<CommandInvocation>()).Returns(true);
        compressor.Compress(
                Arg.Any<CommandInvocation>(),
                Arg.Any<CommandOutput>(),
                Arg.Any<CompressionOptions>())
            .Returns(CompressionResult.From(
                compressedFallback,
                20,
                5,
                "package-manager",
                [],
                false));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("vitest", "vitest run"));
        var jestFilter = BuiltInFilters.All.Single(candidate => candidate.Id == "jest");
        var universal = BuiltInFilters.All.Single(candidate => candidate.Id == "ansi-strip");
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([jestFilter, universal]);
        var realFilterEngine = new FilterEngine();
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(jestFilter, Arg.Any<string>())
            .Returns(ci => realFilterEngine.Apply(jestFilter, ci.ArgAt<string>(1)));
        filterEngine.Apply(universal, Arg.Any<string>())
            .Returns(ci => realFilterEngine.Apply(universal, ci.ArgAt<string>(1)));
        var service = MakeService(
            runner: runner,
            compressor: compressor,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with
            {
                SmallOutputThreshold = 0,
                ShowCompressionMetadata = false,
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(CommandOutput.TimeoutExitCode, result.Value.ExitCode);
        Assert.Equal(filteredFallback + "\n" + timeoutDiagnostic, result.Value.Text);
        Assert.DoesNotContain("jest: ok", result.Value.Text);
        Assert.Single(Regex.Matches(result.Value.Text, Regex.Escape(timeoutDiagnostic)));
        filterEngine.Received(1).Apply(jestFilter, wrapperOnly);
        filterEngine.Received(1).Apply(universal, composedFallback);
    }

    [Theory]
    [InlineData("jest", "jest --runInBand")]
    [InlineData("vitest", "vitest run")]
    public async Task RunBufferedAsync_ResolvedJestOrVitest_ZeroExitEmptyOutputRetainsOnEmptySuccess(
        string executable,
        string resolvedCommand)
    {
        const string compressedFallback = "\x1B[31mcompressed output that should not win\x1B[0m";
        var invocation = CommandInvocation.Buffered("pnpm", ["test"], "pnpm test");
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("", "", 0, TimeSpan.Zero)));

        var compressor = Substitute.For<IOutputCompressor>();
        compressor.Id.Returns("package-manager");
        compressor.CanHandle(Arg.Any<CommandInvocation>()).Returns(true);
        compressor.Compress(
                Arg.Any<CommandInvocation>(),
                Arg.Any<CommandOutput>(),
                Arg.Any<CompressionOptions>())
            .Returns(CompressionResult.From(
                compressedFallback,
                10,
                3,
                "package-manager",
                [],
                false));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript(executable, resolvedCommand));
        var jestFilter = BuiltInFilters.All.Single(candidate => candidate.Id == "jest");
        var universal = BuiltInFilters.All.Single(candidate => candidate.Id == "ansi-strip");
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([jestFilter, universal]);
        var realFilterEngine = new FilterEngine();
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(jestFilter, Arg.Any<string>())
            .Returns(ci => realFilterEngine.Apply(jestFilter, ci.ArgAt<string>(1)));
        filterEngine.Apply(universal, Arg.Any<string>())
            .Returns(ci => realFilterEngine.Apply(universal, ci.ArgAt<string>(1)));
        var service = MakeService(
            runner: runner,
            compressor: compressor,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with { ShowCompressionMetadata = false },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal("jest: ok", result.Value.Text);
        filterEngine.Received(1).Apply(jestFilter, "");
        filterEngine.DidNotReceive().Apply(universal, Arg.Any<string>());
    }

    [Fact]
    public async Task RunBufferedAsync_ResolvedFilterWithoutMerge_UsesCombinedStreamWithoutAppendingRawStderr()
    {
        const string stdout = "FAIL checkout reports the underlying assertion";
        const string filtered = "checkout: failed";
        const string stderr = "fatal: test worker exited with code 1";
        var rawCombined = stdout + "\n" + stderr;
        var invocation = CommandInvocation.Buffered("pnpm", ["test"], "pnpm test");
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(stdout, stderr, 1, TimeSpan.Zero)));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("jest", "jest --runInBand"));
        var filter = new CompiledFilterDefinition
        {
            Id = "jest",
            AppliesTo = ["jest"],
            MergeStderr = false,
            Stages = [],
        };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([filter]);
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(filter, Arg.Any<string>())
            .Returns(new FilterResult(filtered, filter.Id, 1));
        var service = MakeService(
            runner: runner,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with { ShowCompressionMetadata = false },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(1, result.Value.ExitCode);
        Assert.Equal(filtered, result.Value.Text);
        Assert.DoesNotContain(stderr, result.Value.Text);
        Assert.DoesNotContain("command timed out", result.Value.Text);
        filterEngine.Received(1).Apply(filter, rawCombined);
    }

    [Fact]
    public async Task RunBufferedAsync_ResolvedSpecificFilter_PreservesIdenticalCrossStreamLineMultiplicity()
    {
        const string repeatedLine = "FAIL checkout reports the same worker diagnostic";
        var rawCombined = repeatedLine + "\n" + repeatedLine;
        var invocation = CommandInvocation.Buffered("pnpm", ["test"], "pnpm test");
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(repeatedLine, repeatedLine, 1, TimeSpan.Zero)));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("jest", "jest --runInBand"));
        var filter = new CompiledFilterDefinition
        {
            Id = "jest",
            AppliesTo = ["jest"],
            MergeStderr = false,
            Stages = [],
        };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([filter]);
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(filter, Arg.Any<string>())
            .Returns(ci => new FilterResult(ci.ArgAt<string>(1), filter.Id, 1));
        var service = MakeService(
            runner: runner,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with { ShowCompressionMetadata = false },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(rawCombined, result.Value.Text);
        Assert.Equal(2, Regex.Matches(result.Value.Text, Regex.Escape(repeatedLine)).Count);
        filterEngine.Received(1).Apply(filter, rawCombined);
    }

    [Fact]
    public async Task RunBufferedAsync_TimedOutResolvedMergeFilter_PreservesDiagnosticAfterFilteringExactlyOnce()
    {
        const string stdout = "PASS checkout completes";
        const string filteredStdout = "checkout: passed";
        const string stderr = "warning: worker exited after completing the test";
        const string timeoutDiagnostic =
            "[hypa: command timed out after 30s; killed process; exit=124; elapsed=31s]";
        var rawCombined = stdout + "\n" + stderr;
        var invocation = CommandInvocation.Buffered("pnpm", ["test"], "pnpm test");
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.CreateTimedOut(TimeSpan.FromSeconds(31), stdout, stderr)));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("jest", "jest --runInBand"));
        var filter = new CompiledFilterDefinition
        {
            Id = "jest-merged",
            AppliesTo = ["jest"],
            MergeStderr = true,
            Stages = [],
        };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([filter]);
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(filter, Arg.Any<string>())
            .Returns(ci =>
            {
                var text = ci.ArgAt<string>(1)
                    .Replace(stdout, filteredStdout, StringComparison.Ordinal)
                    .Replace(timeoutDiagnostic, string.Empty, StringComparison.Ordinal)
                    .TrimEnd();
                return new FilterResult(text, filter.Id, 1);
            });
        var service = MakeService(
            runner: runner,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with { ShowCompressionMetadata = false },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(CommandOutput.TimeoutExitCode, result.Value.ExitCode);
        Assert.Equal(filteredStdout + "\n" + stderr + "\n" + timeoutDiagnostic, result.Value.Text);
        Assert.DoesNotContain(stdout, result.Value.Text);
        Assert.Single(Regex.Matches(result.Value.Text, Regex.Escape(stderr)));
        Assert.Single(Regex.Matches(result.Value.Text, Regex.Escape(timeoutDiagnostic)));
        filterEngine.Received(1).Apply(filter, rawCombined);
    }

    [Fact]
    public async Task RunBufferedAsync_ResolvedMergeFilter_ReceivesExactRawCombinedStreamBeforeLossyCompression()
    {
        var stdout = "raw stdout header\n" + new string('s', 300);
        var stderr = "raw stderr diagnostic\n" + new string('e', 300);
        var rawCombined = stdout + "\n" + stderr;
        var invocation = CommandInvocation.Buffered("pnpm", ["test"], "pnpm test");
        var captured = CommandOutput.Captured(stdout, stderr, 1, TimeSpan.Zero);
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(captured));

        var compressor = Substitute.For<IOutputCompressor>();
        compressor.Id.Returns("lossy-package-manager");
        compressor.CanHandle(Arg.Any<CommandInvocation>()).Returns(true);
        compressor.Compress(
                Arg.Any<CommandInvocation>(),
                Arg.Any<CommandOutput>(),
                Arg.Any<CompressionOptions>())
            .Returns(CompressionResult.From(
                "lossy compressed package output",
                200,
                3,
                "lossy-package-manager",
                [],
                false));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("jest", "jest --runInBand"));
        var filter = new CompiledFilterDefinition
        {
            Id = "jest-merged",
            AppliesTo = ["jest"],
            MergeStderr = true,
            Stages = [],
        };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([filter]);
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(filter, Arg.Any<string>())
            .Returns(ci => new FilterResult(ci.ArgAt<string>(1), filter.Id, 1));
        var service = MakeService(
            runner: runner,
            compressor: compressor,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with { ShowCompressionMetadata = false },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(rawCombined, result.Value.Text);
        Assert.Single(Regex.Matches(result.Value.Text, Regex.Escape(stderr)));
        compressor.Received(1).Compress(
            invocation,
            captured,
            Arg.Any<CompressionOptions>());
        filterEngine.Received(1).Apply(filter, rawCombined);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunBufferedAsync_ResolvedFilterFallsBackToCompressedOutput_WhenNoStageAppliesOrFilterVoids(
        bool filterVoids)
    {
        const string compressedOutput = "compressed package-manager output";
        var rawOutput = string.Join(
            '\n',
            Enumerable.Range(1, 100).Select(line => $"raw package-script output {line}"));
        var invocation = CommandInvocation.Buffered("pnpm", ["test"], "pnpm test");
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(rawOutput, "", 1, TimeSpan.Zero)));

        var compressor = Substitute.For<IOutputCompressor>();
        compressor.Id.Returns("pkg-manager");
        compressor.CanHandle(Arg.Any<CommandInvocation>()).Returns(true);
        compressor.Compress(
                Arg.Any<CommandInvocation>(),
                Arg.Any<CommandOutput>(),
                Arg.Any<CompressionOptions>())
            .Returns(CompressionResult.From(
                compressedOutput,
                1_000,
                8,
                "pkg-manager",
                ["parse-errors"],
                false));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("jest", "jest --runInBand"));
        var filter = new CompiledFilterDefinition
        {
            Id = "jest",
            AppliesTo = ["jest"],
            MatchCommand = @"^jest\s+--runInBand$",
            CompiledMatchCommand = new Regex(@"^jest\s+--runInBand$", RegexOptions.CultureInvariant),
            Stages = [],
        };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([filter]);
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(filter, Arg.Any<string>())
            .Returns(filterVoids
                ? new FilterResult("", filter.Id, 1)
                : new FilterResult(rawOutput, filter.Id, 0));
        var service = MakeService(
            runner: runner,
            compressor: compressor,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);
        var options = CompressionOptions.Default with { ShowCompressionMetadata = false };

        var result = await service.RunBufferedAsync(invocation, options, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(compressedOutput, result.Value.Text);
        filterEngine.Received(1).Apply(filter, rawOutput);
    }

    [Fact]
    public async Task RunBufferedAsync_ResolvedUnsupportedTool_AppliesUniversalFilterToCompressedOutput()
    {
        const string compressedOutput = "\x1B[31mcompressed package-manager output\x1B[0m";
        var rawOutput = string.Join(
            '\n',
            Enumerable.Range(1, 100).Select(line => $"raw unsupported-tool output {line}"));
        var invocation = CommandInvocation.Buffered("pnpm", ["unsupported"], "pnpm unsupported");
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(rawOutput, "", 1, TimeSpan.Zero)));

        var compressor = Substitute.For<IOutputCompressor>();
        compressor.Id.Returns("pkg-manager");
        compressor.CanHandle(Arg.Any<CommandInvocation>()).Returns(true);
        compressor.Compress(
                Arg.Any<CommandInvocation>(),
                Arg.Any<CommandOutput>(),
                Arg.Any<CompressionOptions>())
            .Returns(CompressionResult.From(
                compressedOutput,
                1_000,
                8,
                "pkg-manager",
                [],
                false));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("unsupported-tool", "unsupported-tool --flag"));
        var universal = BuiltInFilters.All.Single(filter => filter.Id == "ansi-strip");
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([universal]);
        var realFilterEngine = new FilterEngine();
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(universal, Arg.Any<string>())
            .Returns(ci => realFilterEngine.Apply(universal, ci.ArgAt<string>(1)));
        var service = MakeService(
            runner: runner,
            compressor: compressor,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with { ShowCompressionMetadata = false },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("compressed package-manager output", result.Value.Text);
        Assert.DoesNotContain("raw unsupported-tool output", result.Value.Text);
        filterEngine.Received(1).Apply(universal, compressedOutput);
        filterEngine.DidNotReceive().Apply(universal, rawOutput);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunBufferedAsync_ResolvedPassthrough_AppliesUniversalFilterToExactCombinedDiagnostics(
        bool smallOutput)
    {
        const string repeatedDiagnostic = "fatal: package script worker exited with code 1";
        const string stdout = "package script failed\n" + repeatedDiagnostic;
        const string stderr = repeatedDiagnostic + "\n" + repeatedDiagnostic;
        const string timeoutDiagnostic =
            "[hypa: command timed out after 30s; killed process; exit=124; elapsed=31s]";
        var expectedCombined = stdout + "\n" + stderr + "\n" + timeoutDiagnostic;
        var invocation = CommandInvocation.Buffered("pnpm", ["unsupported"], "pnpm unsupported");
        var output = CommandOutput.CreateTimedOut(TimeSpan.FromSeconds(31), stdout, stderr);
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(output));

        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(100);
        var compressor = Substitute.For<IOutputCompressor>();
        compressor.Id.Returns("pkg-manager");
        compressor.CanHandle(Arg.Any<CommandInvocation>()).Returns(true);
        compressor.Compress(
                Arg.Any<CommandInvocation>(),
                Arg.Any<CommandOutput>(),
                Arg.Any<CompressionOptions>())
            .Returns(CompressionResult.From(
                "discarded compressor candidate",
                100,
                100,
                "pkg-manager",
                [],
                false));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("unsupported-tool", "unsupported-tool --flag"));
        var universal = new CompiledFilterDefinition
        {
            Id = "universal",
            AppliesTo = [],
            Stages = [],
        };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([universal]);
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(universal, Arg.Any<string>())
            .Returns(ci => new FilterResult(ci.ArgAt<string>(1), universal.Id, 1));
        var service = MakeService(
            runner: runner,
            compressor: compressor,
            tokenCounter: tokenCounter,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with
            {
                SmallOutputThreshold = smallOutput ? 100 : 0,
                ShowCompressionMetadata = false,
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(expectedCombined, result.Value.Text);
        Assert.Equal(3, Regex.Matches(result.Value.Text, Regex.Escape(repeatedDiagnostic)).Count);
        Assert.Single(Regex.Matches(result.Value.Text, Regex.Escape(timeoutDiagnostic)));
        filterEngine.Received(1).Apply(universal, expectedCombined);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunBufferedAsync_ResolvedUniversalFilter_TransformsComposedFallbackDiagnosticsExactlyOnce(
        bool timedOut)
    {
        const string decoratedStderrLine = "\x1B[31mfatal: package script failed\x1B[0m";
        const string plainStderrLine = "fatal: package script failed";
        const string stderr = decoratedStderrLine + "\n" + decoratedStderrLine;
        const string timeoutDiagnostic =
            "[hypa: command timed out after 30s; killed process; exit=124; elapsed=31s]";
        const string compressedOutput = "compressed package-manager output";
        const string filteredFallback = "compressed package-manager output\n" +
            plainStderrLine + "\n" + plainStderrLine;
        var rawOutput = string.Join(
            '\n',
            Enumerable.Range(1, 100).Select(line => $"raw unsupported-tool output {line}"));
        var invocation = CommandInvocation.Buffered("pnpm", ["unsupported"], "pnpm unsupported");
        var output = timedOut
            ? CommandOutput.CreateTimedOut(TimeSpan.FromSeconds(31), rawOutput, stderr)
            : CommandOutput.Captured(rawOutput, stderr, 1, TimeSpan.FromSeconds(31));
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(output));

        var compressor = Substitute.For<IOutputCompressor>();
        compressor.Id.Returns("pkg-manager");
        compressor.CanHandle(Arg.Any<CommandInvocation>()).Returns(true);
        compressor.Compress(
                Arg.Any<CommandInvocation>(),
                Arg.Any<CommandOutput>(),
                Arg.Any<CompressionOptions>())
            .Returns(CompressionResult.From(
                compressedOutput,
                1_000,
                8,
                "pkg-manager",
                [],
                false));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("unsupported-tool", "unsupported-tool --flag"));
        var universal = BuiltInFilters.All.Single(filter => filter.Id == "ansi-strip");
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([universal]);
        var realFilterEngine = new FilterEngine();
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(universal, Arg.Any<string>())
            .Returns(ci => realFilterEngine.Apply(universal, ci.ArgAt<string>(1)));
        var service = MakeService(
            runner: runner,
            compressor: compressor,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with { ShowCompressionMetadata = false },
            CancellationToken.None);

        var composedFallback = compressedOutput + "\n" + stderr +
            (timedOut ? "\n" + timeoutDiagnostic : string.Empty);
        var expected = filteredFallback +
            (timedOut ? "\n" + timeoutDiagnostic : string.Empty);
        Assert.True(result.IsOk);
        Assert.Equal(expected, result.Value.Text);
        Assert.Equal(timedOut ? CommandOutput.TimeoutExitCode : 1, result.Value.ExitCode);
        Assert.DoesNotContain('\u001b', result.Value.Text);
        Assert.Equal(2, Regex.Matches(result.Value.Text, Regex.Escape(plainStderrLine)).Count);
        Assert.Equal(
            timedOut ? 1 : 0,
            Regex.Matches(result.Value.Text, Regex.Escape(timeoutDiagnostic)).Count);
        filterEngine.Received(1).Apply(universal, composedFallback);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunBufferedAsync_ResolvedRejectedSpecificFilter_FallsBackToCompressedOutputAndPreservesDiagnosticsExactlyOnce(
        bool timedOut,
        bool filterVoids)
    {
        const string retainedStderrLine = "fatal: package script failed after producing its report";
        const string missingStderrLine = "caused by: test worker exited with code 1";
        const string stderr = retainedStderrLine + "\n" + missingStderrLine;
        const string compressedOutput = "compressed package-manager output\n" + retainedStderrLine;
        const string timeoutDiagnostic =
            "[hypa: command timed out after 30s; killed process; exit=124; elapsed=31s]";
        var rawOutput = string.Join(
            '\n',
            Enumerable.Range(1, 100).Select(line => $"raw package-script output {line}"));
        var rawCombined = rawOutput + "\n" + stderr;
        var invocation = CommandInvocation.Buffered("pnpm", ["test"], "pnpm test");
        var output = timedOut
            ? CommandOutput.CreateTimedOut(TimeSpan.FromSeconds(31), rawOutput, stderr)
            : CommandOutput.Captured(rawOutput, stderr, 1, TimeSpan.FromSeconds(31));
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(output));

        var compressor = Substitute.For<IOutputCompressor>();
        compressor.Id.Returns("pkg-manager");
        compressor.CanHandle(Arg.Any<CommandInvocation>()).Returns(true);
        compressor.Compress(
                Arg.Any<CommandInvocation>(),
                Arg.Any<CommandOutput>(),
                Arg.Any<CompressionOptions>())
            .Returns(CompressionResult.From(
                compressedOutput,
                1_000,
                8,
                "pkg-manager",
                [],
                false));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns(new ResolvedPackageScript("jest", "jest --runInBand"));
        var filter = new CompiledFilterDefinition
        {
            Id = "jest",
            AppliesTo = ["jest"],
            MatchCommand = @"^jest\s+--runInBand$",
            CompiledMatchCommand = new Regex(@"^jest\s+--runInBand$", RegexOptions.CultureInvariant),
            Stages = [],
        };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([filter]);
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(filter, Arg.Any<string>())
            .Returns(filterVoids
                ? new FilterResult("", filter.Id, 1)
                : new FilterResult(rawCombined, filter.Id, 0));
        var service = MakeService(
            runner: runner,
            compressor: compressor,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default with { ShowCompressionMetadata = false },
            CancellationToken.None);

        var expected = compressedOutput + "\n" + missingStderrLine +
            (timedOut ? "\n" + timeoutDiagnostic : string.Empty);
        Assert.True(result.IsOk);
        Assert.Equal(expected, result.Value.Text);
        Assert.Equal(timedOut ? CommandOutput.TimeoutExitCode : 1, result.Value.ExitCode);
        Assert.DoesNotContain("raw package-script output", result.Value.Text);
        Assert.Single(Regex.Matches(result.Value.Text, Regex.Escape(stderr)));
        Assert.Single(Regex.Matches(result.Value.Text, Regex.Escape(retainedStderrLine)));
        Assert.Single(Regex.Matches(result.Value.Text, Regex.Escape(missingStderrLine)));
        Assert.Equal(
            timedOut ? 1 : 0,
            Regex.Matches(result.Value.Text, Regex.Escape(timeoutDiagnostic)).Count);
        filterEngine.Received(1).Apply(filter, rawCombined);
    }

    [Fact]
    public async Task RunBufferedAsync_UnresolvedBuiltIn_UsesOriginalPairForFilter()
    {
        var invocation = CommandInvocation.Buffered("pnpm", ["install"], "pnpm install");
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 0, TimeSpan.Zero)));

        var packageScriptResolver = Substitute.For<IPackageManagerScriptResolver>();
        packageScriptResolver.TryResolve(invocation)
            .Returns((ResolvedPackageScript?)null);

        var installFilter = new CompiledFilterDefinition
        {
            Id = "pnpm-install",
            AppliesTo = ["pnpm"],
            MatchCommand = @"^pnpm\s+install\b",
            CompiledMatchCommand = new Regex(@"^pnpm\s+install\b", RegexOptions.CultureInvariant),
            Stages = [],
        };
        var filterRepo = Substitute.For<IFilterRepository>();
        filterRepo.GetAll().Returns([installFilter]);
        var filterEngine = Substitute.For<IFilterEngine>();
        filterEngine.Apply(installFilter, Arg.Any<string>())
            .Returns(new FilterResult("pnpm-install-filtered", installFilter.Id, 1));

        var service = MakeService(
            runner: runner,
            filterRepo: filterRepo,
            filterEngine: filterEngine,
            packageScriptResolver: packageScriptResolver);

        var result = await service.RunBufferedAsync(
            invocation,
            CompressionOptions.Default,
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Contains("pnpm-install-filtered", result.Value.Text);
        packageScriptResolver.Received(1).TryResolve(invocation);
        filterEngine.Received(1).Apply(installFilter, Arg.Any<string>());
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
    public async Task RunBuffered_WhenSessionResolverThrows_RunsCommandAndReturnsUnderlyingExitCode()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured(new string('x', 400), "", 7, TimeSpan.Zero)));

        var resolver = Substitute.For<ISessionResolver>();
        resolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<ContextSession, Error>>>(_ =>
                throw new InvalidOperationException("storage unavailable"));

        var service = MakeService(runner: runner, resolver: resolver);
        var result = await service.RunBufferedAsync(FakeInvocation, CompressionOptions.Default, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(7, result.Value.ExitCode);
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

    [Fact]
    public async Task RunPassthrough_WhenSessionResolverThrows_ReturnsUnderlyingExitCode()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("", "", 11, TimeSpan.Zero)));

        var resolver = Substitute.For<ISessionResolver>();
        resolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<ContextSession, Error>>>(_ =>
                throw new InvalidOperationException("storage unavailable"));

        var service = MakeService(runner: runner, resolver: resolver);
        var result = await service.RunPassthroughAsync(FakeInvocation, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(11, result.Value);
    }
}
