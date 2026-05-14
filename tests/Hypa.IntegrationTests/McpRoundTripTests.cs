using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Hypa.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class McpRoundTripTests : IAsyncLifetime
{
    private string _cliBinary = "";

    public Task InitializeAsync()
    {
        var repoRoot = FindRepoRoot();
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

    private static async Task WriteFrameAsync(Stream stream, string json, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        int contentLength = -1;

        while (true)
        {
            var line = await ReadLineAsync(stream, ct);
            if (line is null)
                return null;
            if (line.Length == 0)
                break;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out var n))
                contentLength = n;
        }

        if (contentLength <= 0)
            return string.Empty;

        var body = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var chunk = await stream.ReadAsync(body.AsMemory(read, contentLength - read), ct);
            if (chunk == 0)
                break;
            read += chunk;
        }

        return Encoding.UTF8.GetString(body, 0, read);
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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypa.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate Hypa repo root (Hypa.slnx not found).");
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
