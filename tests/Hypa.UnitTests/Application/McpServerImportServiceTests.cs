using System.Security.Cryptography;
using System.Text;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

[Trait("Category", "McpServerImport")]
public sealed class McpServerImportServiceTests
{
    private readonly IMcpServerConfigReader _reader = Substitute.For<IMcpServerConfigReader>();
    private readonly IMcpServerConfigWriter _writer = Substitute.For<IMcpServerConfigWriter>();
    private readonly McpConfigValidationService _validator = new();

    private McpServerImportService Sut(params IMcpConnectionImportSource[] sources) =>
        new(sources, _reader, _writer, _validator);

    private static McpServerDefinition StdioServer(string name, string command = "my-server") =>
        new(name,
            new McpTransportConfig(McpTransportKind.Stdio, command),
            new NoneAuthConfig(),
            Tls: null,
            ConnectTimeout: null,
            RequestTimeout: null);

    private static IMcpConnectionImportSource SourceReturning(
        string agentKey,
        McpImportScope scope,
        params McpImportedConnection[] connections)
    {
        var src = Substitute.For<IMcpConnectionImportSource>();
        src.AgentKey.Returns(agentKey);
        src.SupportsScope(Arg.Any<McpImportScope>()).Returns(true);
        src.DiscoverAsync(
                Arg.Is<McpImportDiscoveryRequest>(r => r.Scope == scope),
                Arg.Any<CancellationToken>())
            .Returns(connections.ToList());
        return src;
    }

    private static McpImportedConnection ImportableConnection(string name, string command = "my-server")
    {
        var def = StdioServer(name, command);
        return new McpImportedConnection(
            "claude", "global", name, def,
            McpServerImportService.ComputeFingerprint(def),
            McpImportCandidateStatus.Importable, null);
    }

    private static McpImportedConnection SkippedSelf(string name) =>
        new("claude", "global", name, null, string.Empty, McpImportCandidateStatus.SkippedSelf, "Hypa self-entry");

    private static McpImportedConnection SkippedUnsafe(string name) =>
        new("claude", "global", name, null, string.Empty, McpImportCandidateStatus.SkippedUnsafeSecret, "raw secret");

    public McpServerImportServiceTests()
    {
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
        _writer.WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
    }

    [Fact]
    public async Task ImportAsync_EmptySources_ReturnsEmptyReport_DoesNotWrite()
    {
        var result = await Sut().ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.True(result.IsOk);
        var report = result.Value;
        Assert.Equal(0, report.ImportedCount);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_NewServer_ImportsAndWritesOnce()
    {
        var conn = ImportableConnection("github");
        var src = SourceReturning("claude", McpImportScope.Global, conn);

        var result = await Sut(src).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.True(result.IsOk);
        var report = result.Value;
        Assert.Equal(1, report.ImportedCount);
        await _writer.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<McpServerDefinition>>(list => list.Any(s => s.Name == "github")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_SameNameSameFingerprint_AlreadyPresent_DoesNotWrite()
    {
        var existing = StdioServer("github");
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([existing]));

        var conn = ImportableConnection("github"); // same command → same fingerprint
        var src = SourceReturning("claude", McpImportScope.Global, conn);

        var result = await Sut(src).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.True(result.IsOk);
        var report = result.Value;
        Assert.Equal(0, report.ImportedCount);
        Assert.Equal(1, report.AlreadyPresentCount);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_SameNameDifferentFingerprint_Conflict_DoesNotWrite()
    {
        var existing = StdioServer("github", "old-command");
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([existing]));

        var conn = ImportableConnection("github", "new-command");
        var src = SourceReturning("claude", McpImportScope.Global, conn);

        var result = await Sut(src).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.True(result.IsOk);
        var report = result.Value;
        Assert.Equal(0, report.ImportedCount);
        Assert.Equal(1, report.ConflictCount);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_SameNameDifferentFingerprint_WithReplace_OverwritesAndWrites()
    {
        var existing = StdioServer("github", "old-command");
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([existing]));

        var conn = ImportableConnection("github", "new-command");
        var src = SourceReturning("claude", McpImportScope.Global, conn);

        var result = await Sut(src).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: true, DryRun: false), default);

        Assert.True(result.IsOk);
        var report = result.Value;
        Assert.Equal(1, report.ImportedCount);
        await _writer.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<McpServerDefinition>>(list =>
                list.Count == 1 && list[0].Transport.Endpoint == "new-command"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_SkippedSelf_NotIncludedInWrite()
    {
        var src = SourceReturning("claude", McpImportScope.Global, SkippedSelf("hypa"));

        var result = await Sut(src).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.True(result.IsOk);
        var report = result.Value;
        Assert.Equal(0, report.ImportedCount);
        Assert.Equal(1, report.SkippedCount);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_SkippedUnsafeSecret_NotIncludedInWrite()
    {
        var src = SourceReturning("claude", McpImportScope.Global, SkippedUnsafe("secret-server"));

        var result = await Sut(src).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.True(result.IsOk);
        var report = result.Value;
        Assert.Equal(0, report.ImportedCount);
        Assert.Equal(1, report.SkippedCount);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_DryRun_DoesNotCallWriter()
    {
        var conn = ImportableConnection("github");
        var src = SourceReturning("claude", McpImportScope.Global, conn);

        var result = await Sut(src).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: true), default);

        Assert.True(result.IsOk);
        var report = result.Value;
        Assert.Equal(1, report.ImportedCount);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_TwoSources_LoadsConfigOnce_WritesOnce()
    {
        var conn1 = ImportableConnection("github", "gh-mcp");
        var conn2 = ImportableConnection("linear", "linear-mcp");
        var src1 = SourceReturning("claude", McpImportScope.Global, conn1);
        var src2 = SourceReturning("codex", McpImportScope.Global, conn2);

        var result = await Sut(src1, src2).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.True(result.IsOk);
        await _reader.Received(1).ReadEditableAsync(Arg.Any<CancellationToken>());
        await _writer.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<McpServerDefinition>>(list => list.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_AgentKeyFilter_OnlyCallsMatchingSource()
    {
        var conn = ImportableConnection("github");
        var claudeSrc = SourceReturning("claude", McpImportScope.Global, conn);
        var codexSrc = SourceReturning("codex", McpImportScope.Global);

        codexSrc.AgentKey.Returns("codex");

        var result = await Sut(claudeSrc, codexSrc).ImportAsync(
            new McpImportRequest("claude", McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.True(result.IsOk);
        await claudeSrc.Received(1).DiscoverAsync(Arg.Any<McpImportDiscoveryRequest>(), Arg.Any<CancellationToken>());
        await codexSrc.DidNotReceive().DiscoverAsync(Arg.Any<McpImportDiscoveryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ComputeFingerprint_SameDefinition_ReturnsSameHash()
    {
        var def = StdioServer("github");
        var fp1 = McpServerImportService.ComputeFingerprint(def);
        var fp2 = McpServerImportService.ComputeFingerprint(def);
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void ComputeFingerprint_DifferentEndpoint_ReturnsDifferentHash()
    {
        var def1 = StdioServer("github", "command-a");
        var def2 = StdioServer("github", "command-b");
        Assert.NotEqual(
            McpServerImportService.ComputeFingerprint(def1),
            McpServerImportService.ComputeFingerprint(def2));
    }

    [Fact]
    public void ComputeFingerprint_SecretRefChanged_HashUnchanged()
    {
        var def1 = new McpServerDefinition(
            "srv",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new BearerAuthConfig("env:TOKEN_A"),
            null, null, null);
        var def2 = def1 with { Auth = new BearerAuthConfig("env:TOKEN_B") };

        Assert.Equal(
            McpServerImportService.ComputeFingerprint(def1),
            McpServerImportService.ComputeFingerprint(def2));
    }

    [Fact]
    public async Task ImportAsync_DifferentNameSameFingerprint_DuplicateReported_Skipped()
    {
        var conn1 = ImportableConnection("github");
        var conn2 = ImportableConnection("gh-cli", "my-server"); // Same command → same fingerprint, different name
        var src = SourceReturning("claude", McpImportScope.Global, conn1, conn2);

        var result = await Sut(src).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.True(result.IsOk);
        var report = result.Value;
        // Only the first one (by order) should be imported.
        Assert.Equal(1, report.ImportedCount);
        // The second one with the same fingerprint but different name should be reported as duplicate and skipped.
        var srcResult = report.Sources.First();
        var duplicateConn = srcResult.Connections.First(c => c.SourceName == "gh-cli");
        Assert.Equal(McpImportCandidateStatus.SkippedDuplicate, duplicateConn.Status);
        Assert.Contains("duplicate", duplicateConn.Detail ?? "");

        await _writer.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<McpServerDefinition>>(list => list.Count == 1 && list[0].Name == "github"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_AuthenticatedEntry_PreservesBearerToken()
    {
        var authDef = new McpServerDefinition(
            "secure-api",
            new McpTransportConfig(McpTransportKind.Http, "https://api.example.com"),
            new BearerAuthConfig("env:API_TOKEN"),
            null, null, null);
        var conn = new McpImportedConnection(
            "claude", "global", "secure-api", authDef,
            McpServerImportService.ComputeFingerprint(authDef),
            McpImportCandidateStatus.Importable, null);
        var src = SourceReturning("claude", McpImportScope.Global, conn);

        var result = await Sut(src).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.True(result.IsOk);
        var report = result.Value;
        Assert.Equal(1, report.ImportedCount);
        await _writer.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<McpServerDefinition>>(list =>
                list.Count == 1 &&
                list[0].Name == "secure-api" &&
                list[0].Auth is BearerAuthConfig &&
                ((BearerAuthConfig)list[0].Auth).TokenRef == "env:API_TOKEN"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_ReadFails_ReturnsError()
    {
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Fail(
                new Error("ReadFailed", "Config file not found")));

        var src = SourceReturning("claude", McpImportScope.Global, ImportableConnection("my-server"));
        var result = await Sut(src).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.False(result.IsOk);
        Assert.Equal("ReadFailed", result.Error.Code);
        Assert.Equal("Config file not found", result.Error.Message);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_ValidatorFails_ReturnsError()
    {
        var badServer = new McpServerDefinition(
            string.Empty, // Empty name will fail validation
            new McpTransportConfig(McpTransportKind.Stdio, "cmd"),
            new NoneAuthConfig(),
            null, null, null);

        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));

        var src = Substitute.For<IMcpConnectionImportSource>();
        src.AgentKey.Returns("claude");
        src.SupportsScope(Arg.Any<McpImportScope>()).Returns(true);
        src.DiscoverAsync(Arg.Any<McpImportDiscoveryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new[] {
                new McpImportedConnection(
                    "claude", "global", "", badServer,
                    "fp", McpImportCandidateStatus.Importable, null)
            }.ToList());

        var result = await Sut(src).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.False(result.IsOk);
        Assert.NotNull(result.Error.Code);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_WriterFails_ReturnsError()
    {
        _writer.WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Fail(
                new Error("WriteFailed", "Permission denied")));

        var src = SourceReturning("claude", McpImportScope.Global, ImportableConnection("my-server"));
        var result = await Sut(src).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.False(result.IsOk);
        Assert.Equal("WriteFailed", result.Error.Code);
        Assert.Equal("Permission denied", result.Error.Message);
    }

    [Fact]
    public async Task ImportAsync_CrossSourceSameNameConflict_SkipsSecondAndReports()
    {
        // Two sources both contribute a server with the same name but different fingerprints
        var conn1 = ImportableConnection("shared-server", "cmd1");
        var conn2 = ImportableConnection("shared-server", "cmd2");

        var src1 = SourceReturning("claude", McpImportScope.Global, conn1);
        var src2 = SourceReturning("codex", McpImportScope.Global, conn2);

        var result = await Sut(src1, src2).ImportAsync(
            new McpImportRequest(null, McpImportScope.Global, null, Replace: false, DryRun: false), default);

        Assert.True(result.IsOk);
        var report = result.Value;

        // Only the first source's server should be imported
        Assert.Equal(1, report.ImportedCount);
        Assert.Equal(1, report.ConflictCount);

        // Verify that only conn1 is marked as imported and conn2 is marked as conflict
        var claudeResult = report.Sources.First(s => s.Agent == "claude");
        var codexResult = report.Sources.First(s => s.Agent == "codex");

        Assert.True(
            claudeResult.Connections.Any(c => c.Status == McpImportCandidateStatus.Importable),
            "First source should have importable connection");

        var conflictConn = codexResult.Connections.First(c => c.SourceName == "shared-server");
        Assert.Equal(McpImportCandidateStatus.SkippedConflict, conflictConn.Status);
        Assert.Contains("same name already accepted", conflictConn.Detail ?? "");

        // Only first server should be written
        await _writer.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<McpServerDefinition>>(list =>
                list.Count == 1 &&
                list[0].Name == "shared-server" &&
                list[0].Transport.Endpoint == "cmd1"),
            Arg.Any<CancellationToken>());
    }
}
