using Hypa.Infrastructure.Mcp;
using Hypa.Infrastructure.Mcp.Tools;
using Hypa.Infrastructure.Rewrite;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Rewrite;
using Hypa.Runtime.Domain.Runner;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Mcp;

public sealed class HypaShellToolTests
{
    private static ICommandRewriteRegistry PassthroughRegistry()
    {
        var registry = Substitute.For<ICommandRewriteRegistry>();
        registry.Rewrite(Arg.Any<string>(), Arg.Any<RewriteContext>())
            .Returns(RewriteDecision.Passthrough());
        return registry;
    }

    [Fact]
    public async Task HypaShell_UsesCompressedRunner_ForDefaultMode()
    {
        var compressedRunner = Substitute.For<ICommandRunnerService>();
        compressedRunner
            .RunBufferedAsync(Arg.Any<CommandInvocation>(), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput("compressed output", 0)));

        var rawRunner = Substitute.For<ICommandRunner>();
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(10);

        var result = await HypaShellTool.ExecuteAsync(
            compressedRunner,
            rawRunner,
            PassthroughRegistry(),
            new ShellLexer(),
            tokenCounter,
            NoOpLedger(),
            NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance,
            new McpRuntimeOptions(),
            CancellationToken.None,
            "echo hello",
            mode: null);

        await compressedRunner.Received(1)
            .RunBufferedAsync(Arg.Any<CommandInvocation>(), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>());
        await rawRunner.DidNotReceive()
            .RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>());
        Assert.True(result.IsError is not true);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("Command completed", text);
        Assert.Contains("compressed output", text);
    }

    [Fact]
    public async Task HypaShell_RawMode_UsesRawCommandRunner()
    {
        var rawRunner = Substitute.For<ICommandRunner>();
        rawRunner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(
                CommandOutput.Captured("raw output", "", 0, TimeSpan.Zero)));

        var compressedRunner = Substitute.For<ICommandRunnerService>();
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);

        var result = await HypaShellTool.ExecuteAsync(
            compressedRunner,
            rawRunner,
            PassthroughRegistry(),
            new ShellLexer(),
            tokenCounter,
            NoOpLedger(),
            NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance,
            new McpRuntimeOptions(),
            CancellationToken.None,
            "echo hello",
            mode: "raw");

        await rawRunner.Received(1)
            .RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>());
        await compressedRunner.DidNotReceive()
            .RunBufferedAsync(Arg.Any<CommandInvocation>(), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>());
        Assert.True(result.IsError is not true);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("raw output", text);
    }

    [Fact]
    public async Task HypaShell_DefaultMode_NonZeroExitCode_IncludesExitInfoInResult()
    {
        var compressedRunner = Substitute.For<ICommandRunnerService>();
        compressedRunner
            .RunBufferedAsync(Arg.Any<CommandInvocation>(), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput("hello", 3)));

        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);

        var result = await HypaShellTool.ExecuteAsync(
            compressedRunner,
            Substitute.For<ICommandRunner>(),
            PassthroughRegistry(),
            new ShellLexer(),
            tokenCounter,
            NoOpLedger(),
            NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance,
            new McpRuntimeOptions(),
            CancellationToken.None,
            "echo hello");

        Assert.True(result.IsError is not true);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("exit 3", text);
        Assert.Contains("hello", text);
    }

    [Theory]
    [InlineData("echo hello | wc -c")]
    [InlineData("ls > /dev/null")]
    [InlineData("git status && echo done")]
    public async Task HypaShell_ShellSyntaxCommand_UsesShellInterpreter(string command)
    {
        CommandInvocation? captured = null;
        var compressedRunner = Substitute.For<ICommandRunnerService>();
        compressedRunner
            .RunBufferedAsync(Arg.Do<CommandInvocation>(inv => captured = inv), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput("output", 0)));

        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);

        await HypaShellTool.ExecuteAsync(
            compressedRunner,
            Substitute.For<ICommandRunner>(),
            PassthroughRegistry(),
            new ShellLexer(),
            tokenCounter,
            NoOpLedger(),
            NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance,
            new McpRuntimeOptions(),
            CancellationToken.None,
            command);

        Assert.NotNull(captured);
        var expectedExe = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";
        Assert.Equal(expectedExe, captured.Executable);
        Assert.Equal(command, captured.OriginalCommand);
    }

    [Theory]
    [InlineData("echo ~/Desktop")]
    [InlineData("echo ~")]
    [InlineData("echo ~user/bin")]
    [InlineData("echo \"$HOME\"")]
    public async Task HypaShell_ExpansionCommand_UsesShellInterpreter(string command)
    {
        CommandInvocation? captured = null;
        var compressedRunner = Substitute.For<ICommandRunnerService>();
        compressedRunner
            .RunBufferedAsync(Arg.Do<CommandInvocation>(inv => captured = inv), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput("output", 0)));

        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);

        await HypaShellTool.ExecuteAsync(
            compressedRunner,
            Substitute.For<ICommandRunner>(),
            PassthroughRegistry(),
            new ShellLexer(),
            tokenCounter,
            NoOpLedger(),
            NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance,
            new McpRuntimeOptions(),
            CancellationToken.None,
            command);

        Assert.NotNull(captured);
        var expectedExe = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";
        Assert.Equal(expectedExe, captured.Executable);
        Assert.Equal(command, captured.OriginalCommand);
    }

    [Theory]
    [InlineData("echo \"~/Desktop\"")]
    [InlineData("echo a~b")]
    [InlineData("echo ~*")]
    public async Task HypaShell_NonExpandingTilde_UsesDirectProcessInvocation(string command)
    {
        CommandInvocation? captured = null;
        var compressedRunner = Substitute.For<ICommandRunnerService>();
        compressedRunner
            .RunBufferedAsync(Arg.Do<CommandInvocation>(inv => captured = inv), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput("output", 0)));

        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);

        await HypaShellTool.ExecuteAsync(
            compressedRunner,
            Substitute.For<ICommandRunner>(),
            PassthroughRegistry(),
            new ShellLexer(),
            tokenCounter,
            NoOpLedger(),
            NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance,
            new McpRuntimeOptions(),
            CancellationToken.None,
            command);

        Assert.NotNull(captured);
        var expectedShell = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";
        Assert.NotEqual(expectedShell, captured.Executable);
    }

    [Theory]
    [InlineData("echo hello | wc -c")]
    [InlineData("ls > /dev/null")]
    public async Task HypaShell_RawMode_ShellSyntaxCommand_UsesShellInterpreter(string command)
    {
        CommandInvocation? captured = null;
        var rawRunner = Substitute.For<ICommandRunner>();
        rawRunner
            .RunAsync(Arg.Do<CommandInvocation>(inv => captured = inv), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(CommandOutput.Captured("out", "", 0, TimeSpan.Zero)));

        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);

        await HypaShellTool.ExecuteAsync(
            Substitute.For<ICommandRunnerService>(),
            rawRunner,
            PassthroughRegistry(),
            new ShellLexer(),
            tokenCounter,
            NoOpLedger(),
            NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance,
            new McpRuntimeOptions(),
            CancellationToken.None,
            command,
            mode: "raw");

        Assert.NotNull(captured);
        var expectedExe = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";
        Assert.Equal(expectedExe, captured.Executable);
    }

    [Fact]
    public async Task HypaShell_SimpleCommand_UsesDirectProcessInvocation()
    {
        CommandInvocation? captured = null;
        var compressedRunner = Substitute.For<ICommandRunnerService>();
        compressedRunner
            .RunBufferedAsync(Arg.Do<CommandInvocation>(inv => captured = inv), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput("output", 0)));

        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);

        await HypaShellTool.ExecuteAsync(
            compressedRunner,
            Substitute.For<ICommandRunner>(),
            PassthroughRegistry(),
            new ShellLexer(),
            tokenCounter,
            NoOpLedger(),
            NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance,
            new McpRuntimeOptions(),
            CancellationToken.None,
            "git status");

        Assert.NotNull(captured);
        Assert.Equal("git", captured.Executable);
    }

    [Fact]
    public async Task HypaShell_WhenRewriteDenies_ReturnsMcpError()
    {
        var registry = Substitute.For<ICommandRewriteRegistry>();
        registry.Rewrite(Arg.Any<string>(), Arg.Any<RewriteContext>())
            .Returns(RewriteDecision.Deny());

        var result = await HypaShellTool.ExecuteAsync(
            Substitute.For<ICommandRunnerService>(),
            Substitute.For<ICommandRunner>(),
            registry,
            new ShellLexer(),
            Substitute.For<ITokenCounter>(),
            NoOpLedger(),
            NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance,
            new McpRuntimeOptions(),
            CancellationToken.None,
            "rm -rf /");

        Assert.True(result.IsError);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("denied", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSessionResolverFails_LogsWarning()
    {
        var compressedRunner = Substitute.For<ICommandRunnerService>();
        compressedRunner.RunBufferedAsync(Arg.Any<CommandInvocation>(), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput("output", 0)));
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);
        var sessionResolver = Substitute.For<ISessionResolver>();
        sessionResolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NO_SESSION", "No session")));
        var logger = new CapturingLogger<HypaShellTool>();

        await HypaShellTool.ExecuteAsync(
            compressedRunner, Substitute.For<ICommandRunner>(), PassthroughRegistry(), new ShellLexer(),
            tokenCounter, NoOpLedger(), sessionResolver, logger, new McpRuntimeOptions(),
            CancellationToken.None, "echo hello");

        Assert.Contains(logger.Captured, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSessionResolverFails_RecordsEvidenceWithEmptyGuid()
    {
        var compressedRunner = Substitute.For<ICommandRunnerService>();
        compressedRunner.RunBufferedAsync(Arg.Any<CommandInvocation>(), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput("output", 0)));
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);
        var sessionResolver = Substitute.For<ISessionResolver>();
        sessionResolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NO_SESSION", "No session")));
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await HypaShellTool.ExecuteAsync(
            compressedRunner, Substitute.For<ICommandRunner>(), PassthroughRegistry(), new ShellLexer(),
            tokenCounter, ledger, sessionResolver, NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
            CancellationToken.None, "echo hello");

        Assert.NotNull(recorded);
        Assert.Equal(Guid.Empty, recorded.SessionId);
        Assert.Equal("hypa_shell", recorded.ToolName);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSessionResolverSucceeds_RecordsEvidenceWithSessionId()
    {
        var compressedRunner = Substitute.For<ICommandRunnerService>();
        compressedRunner.RunBufferedAsync(Arg.Any<CommandInvocation>(), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput("output", 0)));
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);
        var sessionId = Guid.NewGuid();
        var session = new ContextSession { Id = sessionId, ProjectRoot = "/project" };
        var sessionResolver = Substitute.For<ISessionResolver>();
        sessionResolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Ok(session));
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await HypaShellTool.ExecuteAsync(
            compressedRunner, Substitute.For<ICommandRunner>(), PassthroughRegistry(), new ShellLexer(),
            tokenCounter, ledger, sessionResolver, NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
            CancellationToken.None, "echo hello");

        Assert.NotNull(recorded);
        Assert.Equal(sessionId, recorded.SessionId);
    }

    [Fact]
    public async Task ExecuteAsync_RawMode_WhenSessionResolverFails_LogsWarning()
    {
        var rawRunner = Substitute.For<ICommandRunner>();
        rawRunner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(CommandOutput.Captured("output", "", 0, TimeSpan.Zero)));
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);
        var sessionResolver = Substitute.For<ISessionResolver>();
        sessionResolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NO_SESSION", "No session")));
        var logger = new CapturingLogger<HypaShellTool>();

        await HypaShellTool.ExecuteAsync(
            Substitute.For<ICommandRunnerService>(), rawRunner, PassthroughRegistry(), new ShellLexer(),
            tokenCounter, NoOpLedger(), sessionResolver, logger, new McpRuntimeOptions(),
            CancellationToken.None, "echo hello", mode: "raw");

        Assert.Contains(logger.Captured, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteAsync_RawMode_RecordsEvidenceAfterCommandCompletes()
    {
        var rawRunner = Substitute.For<ICommandRunner>();
        rawRunner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Ok(CommandOutput.Captured("raw output", "", 0, TimeSpan.Zero)));
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await HypaShellTool.ExecuteAsync(
            Substitute.For<ICommandRunnerService>(), rawRunner, PassthroughRegistry(), new ShellLexer(),
            tokenCounter, ledger, NoSessionResolver(), NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
            CancellationToken.None, "echo hello", mode: "raw");

        Assert.NotNull(recorded);
        Assert.Equal("hypa_shell", recorded.ToolName);
        Assert.NotEmpty(recorded.ArgsHash);
        Assert.NotEmpty(recorded.OutputHash);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultMode_RecordsEvidenceAfterCommandCompletes()
    {
        var compressedRunner = Substitute.For<ICommandRunnerService>();
        compressedRunner.RunBufferedAsync(Arg.Any<CommandInvocation>(), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput("compressed output", 0)));
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(10);
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await HypaShellTool.ExecuteAsync(
            compressedRunner, Substitute.For<ICommandRunner>(), PassthroughRegistry(), new ShellLexer(),
            tokenCounter, ledger, NoSessionResolver(), NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
            CancellationToken.None, "echo hello");

        Assert.NotNull(recorded);
        Assert.Equal("hypa_shell", recorded.ToolName);
        Assert.NotEmpty(recorded.ArgsHash);
        Assert.NotEmpty(recorded.OutputHash);
    }

    [Fact]
    public async Task ExecuteAsync_ReadOnlyMode_RecordsEvidence()
    {
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await HypaShellTool.ExecuteAsync(
            Substitute.For<ICommandRunnerService>(), Substitute.For<ICommandRunner>(), PassthroughRegistry(),
            new ShellLexer(), Substitute.For<ITokenCounter>(), ledger, NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions { ReadOnly = true },
            CancellationToken.None, "echo hello");

        Assert.NotNull(recorded);
        Assert.Contains("Blocked", recorded.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_BlankCommand_RecordsEvidence()
    {
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await HypaShellTool.ExecuteAsync(
            Substitute.For<ICommandRunnerService>(), Substitute.For<ICommandRunner>(), PassthroughRegistry(),
            new ShellLexer(), Substitute.For<ITokenCounter>(), ledger, NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
            CancellationToken.None, "");

        Assert.NotNull(recorded);
        Assert.Contains("Error", recorded.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRewriteDenies_RecordsEvidence()
    {
        var registry = Substitute.For<ICommandRewriteRegistry>();
        registry.Rewrite(Arg.Any<string>(), Arg.Any<RewriteContext>())
            .Returns(RewriteDecision.Deny());
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await HypaShellTool.ExecuteAsync(
            Substitute.For<ICommandRunnerService>(), Substitute.For<ICommandRunner>(), registry,
            new ShellLexer(), Substitute.For<ITokenCounter>(), ledger, NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
            CancellationToken.None, "rm -rf /");

        Assert.NotNull(recorded);
        Assert.Contains("denied", recorded.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedMode_RecordsEvidence()
    {
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await HypaShellTool.ExecuteAsync(
            Substitute.For<ICommandRunnerService>(), Substitute.For<ICommandRunner>(), PassthroughRegistry(),
            new ShellLexer(), Substitute.For<ITokenCounter>(), ledger, NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
            CancellationToken.None, "echo hello", mode: "unsupported");

        Assert.NotNull(recorded);
        Assert.Contains("Error", recorded.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTokenisationProducesNoArguments_RecordsEvidence()
    {
        var shellLexer = Substitute.For<IShellLexer>();
        shellLexer.Lex(Arg.Any<string>()).Returns(Array.Empty<ShellToken>());
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await HypaShellTool.ExecuteAsync(
            Substitute.For<ICommandRunnerService>(), Substitute.For<ICommandRunner>(), PassthroughRegistry(),
            shellLexer, Substitute.For<ITokenCounter>(), ledger, NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
            CancellationToken.None, "echo hello");

        Assert.NotNull(recorded);
        Assert.Contains("Error", recorded.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_RawRunnerFailure_RecordsEvidence()
    {
        var rawRunner = Substitute.For<ICommandRunner>();
        rawRunner.RunAsync(Arg.Any<CommandInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Result<CommandOutput, Error>.Fail(new Error("FAIL", "runner error")));
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await HypaShellTool.ExecuteAsync(
            Substitute.For<ICommandRunnerService>(), rawRunner, PassthroughRegistry(),
            new ShellLexer(), Substitute.For<ITokenCounter>(), ledger, NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
            CancellationToken.None, "echo hello", mode: "raw");

        Assert.NotNull(recorded);
        Assert.Contains("Execution failed", recorded.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_CompressedRunnerFailure_RecordsEvidence()
    {
        var compressedRunner = Substitute.For<ICommandRunnerService>();
        compressedRunner.RunBufferedAsync(Arg.Any<CommandInvocation>(), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<BufferedRunOutput, Error>.Fail(new Error("FAIL", "runner error")));
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await HypaShellTool.ExecuteAsync(
            compressedRunner, Substitute.For<ICommandRunner>(), PassthroughRegistry(),
            new ShellLexer(), Substitute.For<ITokenCounter>(), ledger, NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
            CancellationToken.None, "echo hello");

        Assert.NotNull(recorded);
        Assert.Contains("Execution failed", recorded.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_EvidenceResultPreview_MatchesReturnedSummary()
    {
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await HypaShellTool.ExecuteAsync(
            Substitute.For<ICommandRunnerService>(), Substitute.For<ICommandRunner>(), PassthroughRegistry(),
            new ShellLexer(), Substitute.For<ITokenCounter>(), ledger, NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions { ReadOnly = true },
            CancellationToken.None, "echo hello");

        Assert.NotNull(recorded);
        var returnedText = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Equal(returnedText[..Math.Min(200, returnedText.Length)], recorded.Result);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelled_PropagatesAndDoesNotRecordEvidence()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var registry = Substitute.For<ICommandRewriteRegistry>();
        registry.Rewrite(Arg.Any<string>(), Arg.Any<RewriteContext>())
            .Returns(_ => throw new OperationCanceledException(cts.Token));
        var ledger = Substitute.For<IEvidenceLedger>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HypaShellTool.ExecuteAsync(
                Substitute.For<ICommandRunnerService>(), Substitute.For<ICommandRunner>(), registry,
                new ShellLexer(), Substitute.For<ITokenCounter>(), ledger, NoSessionResolver(),
                NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
                cts.Token, "echo hello"));

        await ledger.DidNotReceive().RecordToolCallAsync(Arg.Any<ToolCallRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenEvidenceRecordingThrows_ReturnsOriginalResult()
    {
        var compressedRunner = Substitute.For<ICommandRunnerService>();
        compressedRunner.RunBufferedAsync(Arg.Any<CommandInvocation>(), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput("output", 0)));
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Any<ToolCallRecord>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("ledger boom"));

        var result = await HypaShellTool.ExecuteAsync(
            compressedRunner, Substitute.For<ICommandRunner>(), PassthroughRegistry(),
            new ShellLexer(), tokenCounter, ledger, NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
            CancellationToken.None, "echo hello");

        Assert.True(result.IsError is not true);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("Command completed", text);
    }

    [Fact]
    public async Task ExecuteAsync_UnexpectedException_RecordsEvidence()
    {
        var registry = Substitute.For<ICommandRewriteRegistry>();
        registry.Rewrite(Arg.Any<string>(), Arg.Any<RewriteContext>())
            .Returns(_ => throw new InvalidOperationException("unexpected"));
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await HypaShellTool.ExecuteAsync(
            Substitute.For<ICommandRunnerService>(), Substitute.For<ICommandRunner>(), registry,
            new ShellLexer(), Substitute.For<ITokenCounter>(), ledger, NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
            CancellationToken.None, "echo hello");

        Assert.NotNull(recorded);
        Assert.True(result.IsError);
        Assert.Contains("Error", recorded.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_EvidenceArgs_IncludesCommandCwdModeAndTimeout()
    {
        var compressedRunner = Substitute.For<ICommandRunnerService>();
        compressedRunner.RunBufferedAsync(Arg.Any<CommandInvocation>(), Arg.Any<CompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput("output", 0)));
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(5);
        ToolCallRecord? recorded = null;
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Do<ToolCallRecord>(r => recorded = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await HypaShellTool.ExecuteAsync(
            compressedRunner, Substitute.For<ICommandRunner>(), PassthroughRegistry(),
            new ShellLexer(), tokenCounter, ledger, NoSessionResolver(),
            NullLogger<HypaShellTool>.Instance, new McpRuntimeOptions(),
            CancellationToken.None, "echo hello", cwd: "/tmp", mode: null, timeoutMs: 5000);

        Assert.NotNull(recorded);
        Assert.Contains("\"command\"", recorded.Args);
        Assert.Contains("\"cwd\"", recorded.Args);
        Assert.Contains("\"timeoutMs\"", recorded.Args);
        Assert.Contains("5000", recorded.Args);
    }

    private static IEvidenceLedger NoOpLedger()
    {
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Any<ToolCallRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return ledger;
    }

    private static ISessionResolver NoSessionResolver()
    {
        var resolver = Substitute.For<ISessionResolver>();
        resolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NO_SESSION", "No session")));
        return resolver;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Captured { get; } = [];

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Captured.Add((logLevel, formatter(state, exception)));

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
