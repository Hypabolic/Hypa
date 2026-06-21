using System.IO;
using Hypa.Infrastructure.Doctor;
using Hypa.Runtime.Application.Ports;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Mcp;

public sealed class McpServerCheckTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
            try { File.Delete(path); } catch { /* best-effort */ }
    }

    [Fact]
    public void Category_IsMcp()
    {
        Assert.Equal("MCP", new McpServerCheck().Category);
    }

    // ── Case: initWithMcp = false (no state file) ────────────────────────────

    [Fact]
    public void Run_NoStateFile_SettingsNotFound_ReturnsOkHookMode()
    {
        var result = new McpServerCheck("/nonexistent/path/settings.json").Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
        Assert.Contains("hook mode", result.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_NoStateFile_HypaAbsent_ReturnsOkHookMode()
    {
        var settings = WriteTempSettings("{\"other\":{}}");
        var result = new McpServerCheck(settings).Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
        Assert.Contains("hook mode", result.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_NoStateFile_McpServersKeyAbsent_ReturnsOkHookMode()
    {
        var settings = WriteTempSettings("{\"mcpServers\":{\"other\":{}}}");
        var result = new McpServerCheck(settings).Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
        Assert.Contains("hook mode", result.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_NoStateFile_InvalidJson_ReturnsOkHookMode()
    {
        var settings = WriteTempSettings("not json at all {{{{");
        var result = new McpServerCheck(settings).Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
        Assert.Contains("hook mode", result.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_NoStateFile_McpServersIsNotObject_ReturnsOkHookMode()
    {
        var settings = WriteTempSettings("{\"mcpServers\":\"string-not-object\"}");
        var result = new McpServerCheck(settings).Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
    }

    [Fact]
    public void Run_NoStateFile_HypaPresent_ReturnsOkNotRecordedAsIntentional()
    {
        var settings = WriteTempSettings("{\"mcpServers\":{\"hypa\":{\"command\":\"hypa\",\"args\":[\"serve\"]}}}");
        var result = new McpServerCheck(settings).Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
        Assert.Contains("not recorded as intentional", result.Value, StringComparison.OrdinalIgnoreCase);
    }

    // ── Case: initWithMcp = true (state file present) ────────────────────────

    [Fact]
    public void Run_InitWithMcp_SettingsNotFound_ReturnsWarn()
    {
        var state = WriteTempState("{\"init_with_mcp\":true}");
        var result = new McpServerCheck("/nonexistent/path/settings.json", state).Run();
        Assert.Equal(DoctorStatus.Warn, result.Status);
    }

    [Fact]
    public void Run_InitWithMcp_HypaAbsent_ReturnsWarn()
    {
        var state = WriteTempState("{\"init_with_mcp\":true}");
        var settings = WriteTempSettings("{\"other\":{}}");
        var result = new McpServerCheck(settings, state).Run();
        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Contains("not registered", result.Value, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Detail);
        Assert.Contains("--with-mcp", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_InitWithMcp_McpServersKeyAbsent_ReturnsWarn()
    {
        var state = WriteTempState("{\"init_with_mcp\":true}");
        var settings = WriteTempSettings("{\"mcpServers\":{\"other\":{}}}");
        var result = new McpServerCheck(settings, state).Run();
        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Contains("not registered", result.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_InitWithMcp_InvalidJson_ReturnsWarn()
    {
        var state = WriteTempState("{\"init_with_mcp\":true}");
        var settings = WriteTempSettings("not json at all {{{{");
        var result = new McpServerCheck(settings, state).Run();
        Assert.Equal(DoctorStatus.Warn, result.Status);
    }

    [Fact]
    public void Run_InitWithMcp_HypaPresent_ReturnsOk()
    {
        var state = WriteTempState("{\"init_with_mcp\":true}");
        var settings = WriteTempSettings("{\"mcpServers\":{\"hypa\":{\"command\":\"hypa\",\"args\":[\"serve\"]}}}");
        var result = new McpServerCheck(settings, state).Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
        Assert.Contains("registered", result.Value, StringComparison.OrdinalIgnoreCase);
    }

    // ── State file: initWithMcp = false explicitly ───────────────────────────

    [Fact]
    public void Run_StateFileInitWithMcpFalse_HypaAbsent_ReturnsOkHookMode()
    {
        var state = WriteTempState("{\"init_with_mcp\":false}");
        var settings = WriteTempSettings("{\"other\":{}}");
        var result = new McpServerCheck(settings, state).Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
        Assert.Contains("hook mode", result.Value, StringComparison.OrdinalIgnoreCase);
    }

    // ── Resilience ───────────────────────────────────────────────────────────

    [Fact]
    public void Run_BrokenStateFile_TreatedAsNoStateFile()
    {
        var state = WriteTempState("not valid json {{{");
        var settings = WriteTempSettings("{\"other\":{}}");
        // broken state file → initWithMcp=false → hook mode
        var result = new McpServerCheck(settings, state).Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
        Assert.Contains("hook mode", result.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_DoesNotThrow()
    {
        var ex = Record.Exception(() => new McpServerCheck().Run());
        Assert.Null(ex);
    }

    private string WriteTempSettings(string json) => WriteTempFile(json);

    private string WriteTempState(string json) => WriteTempFile(json);

    private string WriteTempFile(string content)
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        File.WriteAllText(path, content);
        return path;
    }
}
