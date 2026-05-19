using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Hypa.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class McpRoundTripTests : IAsyncLifetime
{
    private string _cliBinary = "";

    public Task InitializeAsync()
    {
        var repoRoot = IntegrationTestHelpers.FindRepoRoot();
        _cliBinary = Path.Combine(repoRoot, "src", "Hypa.Cli", "bin", "Debug", "net10.0", "hypa.dll");
        Assert.True(File.Exists(_cliBinary), $"CLI binary not found: {_cliBinary}");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task InitializeAndToolsList_RoundTrip()
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{_cliBinary}\" serve")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)!;
        process.StandardInput.AutoFlush = true;

        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

        // Step 1: initialize
        const string initJson = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, initJson, ct);

        var initResponse = await ReadFrameAsync(process.StandardOutput.BaseStream, ct);
        Assert.NotNull(initResponse);
        Assert.NotEmpty(initResponse);

        var initDoc = JsonSerializer.Deserialize<InitResponse>(initResponse, McpTestJsonContext.Default.InitResponse);
        Assert.NotNull(initDoc);
        Assert.Equal("2024-11-05", initDoc.Result?.ProtocolVersion);
        Assert.Equal("hypa", initDoc.Result?.ServerInfo?.Name);

        // Step 1.5: send initialized notification (required by spec; no response expected)
        const string initializedNotif = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, initializedNotif, ct);

        // Step 2: tools/list
        const string listJson = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, listJson, ct);

        var listResponse = await ReadFrameAsync(process.StandardOutput.BaseStream, ct);
        Assert.NotNull(listResponse);
        Assert.NotEmpty(listResponse);

        var listDoc = JsonSerializer.Deserialize<ToolsListResponse>(listResponse, McpTestJsonContext.Default.ToolsListResponse);
        Assert.NotNull(listDoc);
        var tools = listDoc.Result?.Tools ?? [];
        Assert.Equal(6, tools.Length);

        var expectedNames = new HashSet<string>
        {
            "hypa_session", "hypa_shell", "hypa_read",
            "hypa_search", "hypa_code", "hypa_compress"
        };
        foreach (var tool in tools)
            Assert.Contains(tool.Name, expectedNames);

        // Step 3: shut down cleanly
        process.StandardInput.Close();
        var exited = await Task.Run(() => process.WaitForExit(10_000));
        Assert.True(exited, "Process did not exit within 10 seconds");
    }

    [Fact]
    public async Task UnknownNotification_ServerRemainsAliveAndRespondsToNextRequest()
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{_cliBinary}\" serve")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)!;
        process.StandardInput.AutoFlush = true;
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

        const string initJson = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, initJson, ct);
        await ReadFrameAsync(process.StandardOutput.BaseStream, ct);

        const string initializedNotif = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, initializedNotif, ct);

        const string unknownNotif = """{"jsonrpc":"2.0","method":"$/unknownNotification","params":{}}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, unknownNotif, ct);

        const string listJson = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, listJson, ct);

        var listResponse = await ReadFrameAsync(process.StandardOutput.BaseStream, ct);
        Assert.NotNull(listResponse);
        Assert.Contains("hypa_shell", listResponse);

        process.StandardInput.Close();
        var exited = await Task.Run(() => process.WaitForExit(10_000));
        Assert.True(exited, "Process did not exit within 10 seconds");
    }

    [Fact]
    public async Task ServeProcess_ExitsCleanly_OnStdinClose()
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{_cliBinary}\" serve")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)!;
        process.StandardInput.AutoFlush = true;
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

        const string initJson = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, initJson, ct);
        await ReadFrameAsync(process.StandardOutput.BaseStream, ct);

        process.StandardInput.Close();

        var exited = await Task.Run(() => process.WaitForExit(10_000));
        Assert.True(exited, "Server did not exit within 10 seconds of stdin close");
    }

    [Fact]
    public async Task ManualJsonLine_Initialize_ReturnsJsonLineResponse()
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{_cliBinary}\" serve")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)!;
        process.StandardInput.AutoFlush = true;
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

        const string initJson = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, initJson, ct);

        var response = await ReadFrameAsync(process.StandardOutput.BaseStream, ct);

        Assert.NotNull(response);
        Assert.StartsWith("{", response);
        Assert.Contains("protocolVersion", response);

        process.StandardInput.Close();
        var exited = await Task.Run(() => process.WaitForExit(10_000));
        Assert.True(exited, "Process did not exit within 10 seconds");
    }

    [Fact]
    public async Task ManualJsonLine_ToolsList_ReturnsExpectedTools()
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{_cliBinary}\" serve")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)!;
        process.StandardInput.AutoFlush = true;
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

        const string initJson = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, initJson, ct);
        await ReadFrameAsync(process.StandardOutput.BaseStream, ct); // consume init response

        const string initializedNotif = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, initializedNotif, ct);

        const string listJson = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";
        await WriteFrameAsync(process.StandardInput.BaseStream, listJson, ct);

        var response = await ReadFrameAsync(process.StandardOutput.BaseStream, ct);

        Assert.NotNull(response);
        var listDoc = JsonSerializer.Deserialize<ToolsListResponse>(response, McpTestJsonContext.Default.ToolsListResponse);
        Assert.NotNull(listDoc);
        var tools = listDoc.Result?.Tools ?? [];
        Assert.Equal(6, tools.Length);
        var expectedNames = new HashSet<string>
        {
            "hypa_session", "hypa_shell", "hypa_read",
            "hypa_search", "hypa_code", "hypa_compress"
        };
        foreach (var tool in tools)
            Assert.Contains(tool.Name, expectedNames);

        process.StandardInput.Close();
        var exited = await Task.Run(() => process.WaitForExit(10_000));
        Assert.True(exited, "Process did not exit within 10 seconds");
    }

    private static async Task WriteFrameAsync(Stream stream, string json, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        return await ReadLineAsync(stream, ct);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buf = new List<byte>(128);
        var oneByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(oneByte.AsMemory(), ct);
            if (read == 0)
                return buf.Count == 0 ? null : Encoding.ASCII.GetString([.. buf]).TrimEnd('\r');
            if (oneByte[0] == '\n')
                return Encoding.ASCII.GetString([.. buf]).TrimEnd('\r');
            buf.Add(oneByte[0]);
        }
    }

}

[Trait("Category", "Integration")]
public sealed class McpSdkClientTests : IAsyncLifetime
{
    private string _cliBinary = "";

    public Task InitializeAsync()
    {
        var repoRoot = IntegrationTestHelpers.FindRepoRoot();
        _cliBinary = Path.Combine(repoRoot, "src", "Hypa.Cli", "bin", "Debug", "net10.0", "hypa.dll");
        Assert.True(File.Exists(_cliBinary), $"CLI binary not found: {_cliBinary}");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Initialize_UsingSdkStdioClient_ReturnsProtocolVersionAndServerInfo()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [_cliBinary, "serve"]
        });

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

        Assert.Equal("hypa", client.ServerInfo.Name);
    }

    [Fact]
    public async Task ToolsList_UsingSdkStdioClient_Returns6ToolsWithExpectedNames()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [_cliBinary, "serve"]
        });

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);

        Assert.Equal(6, tools.Count);
        var expectedNames = new HashSet<string>
        {
            "hypa_session", "hypa_shell", "hypa_read",
            "hypa_search", "hypa_code", "hypa_compress"
        };
        foreach (var tool in tools)
            Assert.Contains(tool.Name, expectedNames);
    }

    [Fact]
    public async Task ToolsCall_HypaShell_EchoHello_ReturnsSummaryAndDetails()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
        // WorkingDirectory ensures the serve process runs from the CLI bin dir so that any
        // hypa subprocess spawned by HypaShellTool's generic wrapper can resolve hypa.dll.
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [_cliBinary, "serve"],
            WorkingDirectory = Path.GetDirectoryName(_cliBinary)!
        });

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        var result = await client.CallToolAsync(
            "hypa_shell",
            new Dictionary<string, object?> { ["command"] = "echo hello" },
            cancellationToken: ct);

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("SUMMARY", text);
        Assert.Contains("Command completed", text);
        Assert.Contains("DETAILS", text);
        Assert.Contains("hello", text);
    }
}

// JSON DTOs for AOT-safe deserialization

internal sealed record InitResponse(
    [property: JsonPropertyName("result")] InitResult? Result);

internal sealed record InitResult(
    [property: JsonPropertyName("protocolVersion")] string? ProtocolVersion,
    [property: JsonPropertyName("serverInfo")] ServerInfoDto? ServerInfo);

internal sealed record ServerInfoDto(
    [property: JsonPropertyName("name")] string? Name);

internal sealed record ToolsListResponse(
    [property: JsonPropertyName("result")] ToolsListResult? Result);

internal sealed record ToolsListResult(
    [property: JsonPropertyName("tools")] ToolDto[]? Tools);

internal sealed record ToolDto(
    [property: JsonPropertyName("name")] string Name);

[JsonSerializable(typeof(InitResponse))]
[JsonSerializable(typeof(ToolsListResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class McpTestJsonContext : JsonSerializerContext { }
