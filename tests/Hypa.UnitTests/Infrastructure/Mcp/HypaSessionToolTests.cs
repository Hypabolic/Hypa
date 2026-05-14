using Hypa.Infrastructure.Mcp;
using Hypa.Infrastructure.Mcp.Tools;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Mcp;

public sealed class HypaSessionToolTests
{
    private readonly ISessionRepository _sessionRepo = Substitute.For<ISessionRepository>();
    private readonly ISessionResolver _sessionResolver = Substitute.For<ISessionResolver>();
    private readonly IEvidenceLedger _evidenceLedger = Substitute.For<IEvidenceLedger>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly McpRuntimeOptions _options = new();
    private static readonly NullLogger<HypaSessionTool> _logger = NullLogger<HypaSessionTool>.Instance;

    public HypaSessionToolTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _evidenceLedger.RecordToolCallAsync(Arg.Any<ToolCallRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private async Task<string> Execute(string action, string? sessionId = null, string? text = null, string? category = null)
    {
        var result = await HypaSessionTool.ExecuteAsync(
            _sessionRepo, _sessionResolver, _evidenceLedger, _clock, _logger, _options,
            CancellationToken.None,
            action, sessionId, text, category);
        return TextOf(result);
    }

    [Fact]
    public async Task Status_NoSession_ReturnsNoActiveSession()
    {
        _sessionResolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NOT_FOUND", "No session")));

        var result = await Execute("status");

        Assert.Contains("No active session", result);
    }

    [Fact]
    public async Task Status_WithSession_ReturnsSessionId()
    {
        var sessionId = Guid.NewGuid();
        var session = new ContextSession { Id = sessionId, ProjectRoot = "/project" };
        _sessionResolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Ok(session));

        var result = await Execute("status");

        Assert.Contains(sessionId.ToString(), result);
    }

    [Theory]
    [InlineData("init")]
    [InlineData("attach")]
    [InlineData("checkpoint")]
    public async Task MutatingActions_BlockedInReadOnlyMode(string action)
    {
        _options.ReadOnly = true;

        var result = await Execute(action);

        Assert.Contains("Read-only", result);
        Assert.Contains(action, result);
    }

    [Fact]
    public async Task Status_NotBlockedInReadOnlyMode()
    {
        _options.ReadOnly = true;
        _sessionResolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NOT_FOUND", "No session")));

        var result = await Execute("status");

        Assert.Contains("No active session", result);
    }

    [Fact]
    public async Task UnknownAction_ReturnsUnknownMessage()
    {
        _sessionResolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NOT_FOUND", "No session")));

        var result = await Execute("unknown-action");

        Assert.Contains("Unknown action", result);
    }

    [Fact]
    public async Task Execute_RecordsEvidenceOnSuccess()
    {
        _sessionResolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NOT_FOUND", "No session")));

        await Execute("status");

        await _evidenceLedger.Received(1).RecordToolCallAsync(
            Arg.Is<ToolCallRecord>(r => r.ToolName == "hypa_session"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Attach_MissingSessionId_ReturnsError()
    {
        var result = await Execute("attach", sessionId: null);

        Assert.Contains("sessionId is required", result);
    }

    [Fact]
    public async Task Attach_InvalidGuid_ReturnsError()
    {
        var result = await Execute("attach", sessionId: "not-a-guid");

        Assert.Contains("valid GUID", result);
    }
}
