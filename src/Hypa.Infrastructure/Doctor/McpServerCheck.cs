using System.Text.Json;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Doctor;

public sealed class McpServerCheck : IDoctorCheck
{
    private readonly string _settingsPath;
    private readonly string? _stateFilePath;

    public McpServerCheck()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _settingsPath = Path.Combine(home, ".claude", "settings.json");
    }

    internal McpServerCheck(string settingsPath, string? stateFilePath = null)
    {
        _settingsPath = settingsPath;
        _stateFilePath = stateFilePath;
    }

    public string Category => "MCP";

    public DoctorCheckResult Run()
    {
        var initWithMcp = _stateFilePath is null
            ? InstallStateReader.ReadInitWithMcp()
            : InstallStateReader.ReadInitWithMcp(_stateFilePath);

        if (!File.Exists(_settingsPath))
        {
            return initWithMcp
                ? new DoctorCheckResult("MCP server", "settings.json not found", DoctorStatus.Warn,
                    "Run: `hypa init --global --with-mcp`")
                : new DoctorCheckResult("MCP server", "not registered (hook mode)", DoctorStatus.Ok);
        }

        string content;
        try
        {
            content = File.ReadAllText(_settingsPath);
        }
        catch (Exception ex)
        {
            return initWithMcp
                ? new DoctorCheckResult("MCP server", $"cannot read settings.json: {ex.Message}", DoctorStatus.Warn,
                    "Run: `hypa init --global --with-mcp`")
                : new DoctorCheckResult("MCP server", "not registered (hook mode)", DoctorStatus.Ok);
        }

        bool hypaPresent;
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            hypaPresent = root.TryGetProperty("mcpServers", out var servers)
                && servers.ValueKind == JsonValueKind.Object
                && servers.TryGetProperty("hypa", out _);
        }
        catch (JsonException)
        {
            return initWithMcp
                ? new DoctorCheckResult("MCP server", "settings.json is not valid JSON", DoctorStatus.Warn,
                    "Run: `hypa init --global --with-mcp`")
                : new DoctorCheckResult("MCP server", "not registered (hook mode)", DoctorStatus.Ok);
        }

        if (initWithMcp)
        {
            return hypaPresent
                ? new DoctorCheckResult("MCP server", "registered in ~/.claude/settings.json", DoctorStatus.Ok)
                : new DoctorCheckResult("MCP server", "not registered in settings.json", DoctorStatus.Warn,
                    "Run: `hypa init --global --with-mcp`");
        }

        return hypaPresent
            ? new DoctorCheckResult("MCP server",
                "registered (not recorded as intentional — run `hypa init --with-mcp` to adopt)",
                DoctorStatus.Ok)
            : new DoctorCheckResult("MCP server", "not registered (hook mode)", DoctorStatus.Ok);
    }
}
