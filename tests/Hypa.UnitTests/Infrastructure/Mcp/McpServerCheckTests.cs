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

    [Fact]
    public void Run_WhenFileNotFound_ReturnsWarn()
    {
        var check = new McpServerCheck("/nonexistent/path/settings.json");
        var result = check.Run();
        Assert.Equal(DoctorStatus.Warn, result.Status);
    }

    [Fact]
    public void Run_WhenMcpServersKeyAbsent_ReturnsWarn()
    {
        var path = WriteTempSettings("{\"other\":{}}");
        var result = new McpServerCheck(path).Run();
        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Contains("not registered", result.Value);
    }

    [Fact]
    public void Run_WhenHypaKeyAbsent_ReturnsWarn()
    {
        var path = WriteTempSettings("{\"mcpServers\":{\"other\":{}}}");
        var result = new McpServerCheck(path).Run();
        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Contains("not registered", result.Value);
    }

    [Fact]
    public void Run_WhenHypaRegistered_ReturnsOk()
    {
        var path = WriteTempSettings("{\"mcpServers\":{\"hypa\":{\"command\":\"hypa\",\"args\":[\"mcp\"]}}}");
        var result = new McpServerCheck(path).Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
    }

    [Fact]
    public void Run_WhenInvalidJson_ReturnsWarn()
    {
        var path = WriteTempSettings("not json at all {{{{");
        var result = new McpServerCheck(path).Run();
        Assert.Equal(DoctorStatus.Warn, result.Status);
    }

    [Fact]
    public void Run_WhenMcpServersIsNotObject_ReturnsWarn()
    {
        var path = WriteTempSettings("{\"mcpServers\":\"string-not-object\"}");
        var result = new McpServerCheck(path).Run();
        Assert.Equal(DoctorStatus.Warn, result.Status);
    }

    [Fact]
    public void Run_DoesNotThrow()
    {
        var ex = Record.Exception(() => new McpServerCheck().Run());
        Assert.Null(ex);
    }

    private string WriteTempSettings(string json)
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        File.WriteAllText(path, json);
        return path;
    }
}
