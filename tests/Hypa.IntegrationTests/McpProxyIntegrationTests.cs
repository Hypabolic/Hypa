using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Hypa.IntegrationTests;

/// <summary>
/// End-to-end tests for the MCP proxy layer (hypa_mcp tool).
///
/// Two-tier setup:
///   Outer  — `hypa serve` started with HYPA_storage_path set to a temp directory.
///   Upstream — a second `hypa serve` process registered in the temp mcp-servers.json.
///
/// The env var override is applied via ProcessStartInfo.Environment so it never
/// touches the real user config at ~/.hypa.
/// </summary>
[Trait("Category", "Integration")]
public sealed class McpProxyIntegrationTests : IAsyncLifetime
{
    private string _cliBinary = "";
    private string _tempDataDir = "";

    public async Task InitializeAsync()
    {
        var repoRoot = IntegrationTestHelpers.FindRepoRoot();
        _cliBinary = Path.Combine(repoRoot, "src", "Hypa.Cli", "bin", "Debug", "net10.0", "hypa.dll");
        Assert.True(File.Exists(_cliBinary), $"CLI binary not found at: {_cliBinary}");

        _tempDataDir = Path.Combine(Path.GetTempPath(), $"hypa-proxy-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDataDir);

        // Register a child `hypa serve` process as the upstream MCP server.
        // Note: no quotes around _cliBinary — ShellLexer preserves quotes as literal chars,
        // which would make dotnet receive `"/path"` (with quotes) as the argument.
        var mcpServersJson = $$"""
            {
              "servers": [
                {
                  "name": "upstream",
                  "transport": "stdio",
                  "endpoint": "dotnet {{_cliBinary}} serve"
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(
            Path.Combine(_tempDataDir, "mcp-servers.json"),
            mcpServersJson);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDataDir))
            Directory.Delete(_tempDataDir, recursive: true);
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Guard test — SDK-first: no custom MCP frame parsing anywhere in the Mcp layer
    // -------------------------------------------------------------------------

    [Fact]
    public void McpInfrastructureLayer_ContainsNoCustomMcpFrameParsing()
    {
        var repoRoot = IntegrationTestHelpers.FindRepoRoot();
        var mcpDir = Path.Combine(repoRoot, "src", "Hypa.Infrastructure", "Mcp");

        Assert.True(Directory.Exists(mcpDir), $"Mcp infrastructure layer not found: {mcpDir}");

        // All Mcp infrastructure files must delegate upstream protocol fully to the SDK.
        // Strings that indicate home-grown JSON-RPC or transport framing are forbidden
        // regardless of which subdirectory (Connection, Auth, Tools, etc.) they appear in.
        var forbidden = new[]
        {
            "ReadLineAsync", "StreamReader", "StreamWriter",
            "Content-Length", "Content-Type",
        };

        foreach (var filePath in Directory.EnumerateFiles(mcpDir, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(filePath);
            var relPath = Path.GetRelativePath(mcpDir, filePath);
            foreach (var token in forbidden)
                Assert.False(source.Contains(token, StringComparison.Ordinal),
                    $"{relPath} contains forbidden token '{token}' — use the SDK client facade instead.");

            Assert.False(source.Contains("jsonrpc", StringComparison.OrdinalIgnoreCase),
                $"{relPath} contains 'jsonrpc' — wire protocol must be handled by the SDK.");
        }
    }

    // -------------------------------------------------------------------------
    // Schema with no upstream configured (SDK client path, no env var needed)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Schema_NoUpstreamServers_ReturnsNoServersConfigured()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), $"hypa-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);
        try
        {
            var text = await CallHypaMcpAsync(
                emptyDir,
                new Dictionary<string, string> { ["action"] = "schema" },
                timeoutSeconds: 30);

            Assert.Contains("No MCP servers configured", text);
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Proxy schema discovery — outer hypa_mcp proxies to upstream hypa serve
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Schema_WithUpstreamHypaServer_ListsUpstreamTools()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;
        var text = await CallHypaMcpAsync(
            _tempDataDir,
            new Dictionary<string, string> { ["action"] = "schema" },
            timeoutSeconds: 60);

        Assert.Contains("SCHEMA", text);
        Assert.Contains("upstream", text);
        Assert.Contains("hypa_shell", text);
    }

    // -------------------------------------------------------------------------
    // Proxy invoke round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Invoke_WithUpstreamHypaServer_HypaShell_ReturnsOutput()
    {
        var text = await CallHypaMcpAsync(
            _tempDataDir,
            new Dictionary<string, string>
            {
                ["action"] = "invoke",
                ["server"] = "upstream",
                ["tool"] = "hypa_shell",
                ["arguments"] = """{"command":"echo proxy-round-trip"}""",
            },
            timeoutSeconds: 60);

        Assert.Contains("proxy-round-trip", text);
    }

    // -------------------------------------------------------------------------
    // Batch with ordered results
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Batch_WithUpstreamHypaServer_OrderPreserved()
    {
        var batchJson = """[{"server":"upstream","tool":"hypa_shell","arguments":"{\"command\":\"echo first\"}"},{"server":"upstream","tool":"hypa_shell","arguments":"{\"command\":\"echo second\"}"},{"server":"upstream","tool":"hypa_shell","arguments":"{\"command\":\"echo third\"}"}]""";

        var text = await CallHypaMcpAsync(
            _tempDataDir,
            new Dictionary<string, string> { ["action"] = "batch", ["requests"] = batchJson },
            timeoutSeconds: 60);

        Assert.Contains("RESULTS", text);
        Assert.True(text.IndexOf("[0]", StringComparison.Ordinal) < text.IndexOf("[1]", StringComparison.Ordinal));
        Assert.True(text.IndexOf("[1]", StringComparison.Ordinal) < text.IndexOf("[2]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Batch_PartialFailure_SuccessItemsUnaffected()
    {
        var batchJson = """[{"server":"upstream","tool":"hypa_shell","arguments":"{\"command\":\"echo ok\"}"},{"server":"no-such-server","tool":"some_tool"}]""";

        var text = await CallHypaMcpAsync(
            _tempDataDir,
            new Dictionary<string, string> { ["action"] = "batch", ["requests"] = batchJson },
            timeoutSeconds: 60);

        Assert.Contains("[0]", text);
        Assert.Contains("OK", text);
        Assert.Contains("[1]", text);
        Assert.Contains("ERROR", text);
    }

    // -------------------------------------------------------------------------
    // Unknown server returns structured error
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Invoke_UnknownServer_ReturnsUnknownServerError()
    {
        var text = await CallHypaMcpAsync(
            _tempDataDir,
            new Dictionary<string, string>
            {
                ["action"] = "invoke",
                ["server"] = "does-not-exist",
                ["tool"] = "echo",
            },
            timeoutSeconds: 30);

        Assert.Contains("UnknownServer", text);
    }

    // -------------------------------------------------------------------------
    // Remote isError propagation — SDK CallToolAsync isError must surface
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Invoke_RemoteToolReturnsIsError_SurfacesRemoteToolError()
    {
        // Invoke hypa_mcp on the upstream with an unknown action; the upstream
        // returns IsError=true. The outer proxy must surface this as RemoteToolError,
        // not swallow or misclassify it.
        var text = await CallHypaMcpAsync(
            _tempDataDir,
            new Dictionary<string, string>
            {
                ["action"] = "invoke",
                ["server"] = "upstream",
                ["tool"] = "hypa_mcp",
                ["arguments"] = """{"action":"__invalid_action__"}""",
            },
            timeoutSeconds: 60);

        Assert.True(
            text.Contains("RemoteToolError", StringComparison.Ordinal),
            $"Expected RemoteToolError in: {text}");
    }

    // -------------------------------------------------------------------------
    // Cancellation — pre-cancelled token aborts without starting a process
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Invoke_PreCancelledToken_ThrowsOperationCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CallHypaMcpAsync(
                _tempDataDir,
                new Dictionary<string, string>
                {
                    ["action"] = "invoke",
                    ["server"] = "upstream",
                    ["tool"] = "hypa_shell",
                    ["arguments"] = """{"command":"echo cancel-test"}""",
                },
                timeoutSeconds: 30,
                externalCt: cts.Token));
    }

    // -------------------------------------------------------------------------
    // Infrastructure-dependent stubs — require external servers or certificates.
    // Run manually or in an environment with the required infrastructure.
    // -------------------------------------------------------------------------

    [Fact(Skip = "RequiresExternalInfrastructure: needs an HTTP/SSE MCP test server")]
    public Task Invoke_HttpSseUpstream_RoundTrip() => Task.CompletedTask;

    [Fact(Skip = "RequiresExternalInfrastructure: needs OAuth2 authorization server for token refresh")]
    public Task Invoke_OAuth2ClientCredentials_TokenRefresh_Succeeds() => Task.CompletedTask;

    [Fact(Skip = "RequiresExternalInfrastructure: needs OAuth2 server with revokable tokens")]
    public Task Invoke_OAuth2DeviceCode_RevokedToken_ReturnsAuthRequired() => Task.CompletedTask;

    [Fact(Skip = "RequiresExternalInfrastructure: needs mTLS-enforcing server and client certificates")]
    public Task Invoke_Mtls_HandshakeSucceeds() => Task.CompletedTask;

    [Fact(Skip = "RequiresExternalInfrastructure: needs OAuth2 authorization server with SDK ClientOAuthOptions wiring")]
    public Task Invoke_SdkOAuthMapping_ClientOAuthOptions_TokenCacheHonoured() => Task.CompletedTask;

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Spawns `hypa serve` with HYPA_storage_path set to <paramref name="dataDir"/>,
    /// performs the MCP initialize handshake, calls `hypa_mcp` with the given
    /// arguments, and returns the concatenated text content from the result.
    /// </summary>
    private async Task<string> CallHypaMcpAsync(
        string dataDir,
        Dictionary<string, string> arguments,
        int timeoutSeconds = 30,
        CancellationToken externalCt = default)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, externalCt);
        var ct = linked.Token;

        var psi = new ProcessStartInfo("dotnet", $"\"{_cliBinary}\" serve")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(_cliBinary)!,
        };
        psi.Environment["HYPA_storage_path"] = dataDir;

        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(psi)!;
        process.StandardInput.AutoFlush = true;

        // Drain stderr asynchronously to prevent the pipe buffer from filling up
        // when the outer hypa serve writes connection/SDK log messages.
        _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);

        const string initJson = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, initJson, ct);
        await ReadFrameAsync(process.StandardOutput.BaseStream, ct); // consume init response

        const string initializedNotif = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, initializedNotif, ct);

        // Serialize arguments as a JSON object for tools/call params.
        var argsJson = new StringBuilder("{");
        var first = true;
        foreach (var (key, value) in arguments)
        {
            if (!first) argsJson.Append(',');
            argsJson.Append($"\"{key}\":\"{EscapeJson(value)}\"");
            first = false;
        }
        argsJson.Append('}');

        var callJson = $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{{\"name\":\"hypa_mcp\",\"arguments\":{argsJson}}}}}";
        await WriteFrameAsync(process.StandardInput.BaseStream, callJson, ct);
        var response = await ReadFrameAsync(process.StandardOutput.BaseStream, ct);

        process.StandardInput.Close();
        await Task.Run(() => process.WaitForExit(10_000));

        return ExtractTextFromToolCallResponse(response);
    }

    private static string ExtractTextFromToolCallResponse(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // result.content[].text
            if (root.TryGetProperty("result", out var result) &&
                result.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("text", out var text))
                        sb.Append(text.GetString());
                }
                return sb.ToString();
            }
        }
        catch { }

        return responseJson;
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static async Task WriteFrameAsync(Stream stream, string json, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var buf = new List<byte>(256);
        var oneByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(oneByte.AsMemory(), ct);
            if (read == 0)
                return buf.Count == 0 ? null : Encoding.UTF8.GetString([.. buf]).TrimEnd('\r');
            if (oneByte[0] == '\n')
                return Encoding.UTF8.GetString([.. buf]).TrimEnd('\r');
            buf.Add(oneByte[0]);
        }
    }
}
