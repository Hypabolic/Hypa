using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Infrastructure.Doctor;

public sealed class McpServerCheck : IDoctorCheck
{
    private readonly string _settingsPath;

    public McpServerCheck()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _settingsPath = Path.Combine(home, ".claude", "settings.json");
    }

    internal McpServerCheck(string settingsPath) => _settingsPath = settingsPath;

    public string Category => "MCP";

    public DoctorCheckResult Run()
    {
        if (!File.Exists(_settingsPath))
            return new DoctorCheckResult("MCP server", "settings.json not found", DoctorStatus.Warn,
                "Run: `hypa init --global` to install");

        string content;
        try
        {
            content = File.ReadAllText(_settingsPath);
        }
        catch (Exception ex)
        {
            return new DoctorCheckResult("MCP server", $"cannot read settings.json: {ex.Message}", DoctorStatus.Warn,
                "Run: `hypa init --global` to install");
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (!root.TryGetProperty("mcpServers", out var servers) ||
                servers.ValueKind != JsonValueKind.Object ||
                !servers.TryGetProperty("hypa", out _))
            {
                return new DoctorCheckResult("MCP server", "not registered in settings.json", DoctorStatus.Warn,
                    "Run: `hypa init --global` to install MCP server entry");
            }
        }
        catch (JsonException)
        {
            return new DoctorCheckResult("MCP server", "settings.json is not valid JSON", DoctorStatus.Warn,
                "Run: `hypa init --global` to reinstall");
        }

        return new DoctorCheckResult("MCP server", "registered in ~/.claude/settings.json", DoctorStatus.Ok);
    }
}
