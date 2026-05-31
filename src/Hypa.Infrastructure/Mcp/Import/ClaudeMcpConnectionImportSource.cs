using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Infrastructure.Mcp.Import;

public sealed class ClaudeMcpConnectionImportSource(string globalHome) : IMcpConnectionImportSource
{
    public string AgentKey => "claude";

    public bool SupportsScope(McpImportScope scope) => true;

    public async Task<IReadOnlyList<McpImportedConnection>> DiscoverAsync(
        McpImportDiscoveryRequest request, CancellationToken ct)
    {
        var results = new List<McpImportedConnection>();

        if (request.Scope is McpImportScope.Global or McpImportScope.All)
        {
            var globalPath = Path.Combine(globalHome, "settings.json");
            await ParseFileAsync(globalPath, "global", results, ct);
        }

        if (request.Scope is McpImportScope.Project or McpImportScope.All)
        {
            if (!string.IsNullOrWhiteSpace(request.ProjectRoot))
            {
                var projectPath = Path.Combine(request.ProjectRoot, ".claude", "settings.local.json");
                await ParseFileAsync(projectPath, "project", results, ct);
            }
        }

        return results;
    }

    private async Task ParseFileAsync(
        string filePath,
        string scopeLabel,
        List<McpImportedConnection> results,
        CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return;

        ClaudeSettingsJson? settings;
        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            settings = JsonSerializer.Deserialize(json, ClaudeSettingsJsonContext.Default.ClaudeSettingsJson);
        }
        catch (Exception ex)
        {
            results.Add(new McpImportedConnection(
                AgentKey, scopeLabel, "unknown", null, string.Empty,
                McpImportCandidateStatus.ParseError,
                ex.Message));
            return;
        }

        if (settings?.McpServers is null)
            return;

        foreach (var (name, entry) in settings.McpServers)
        {
            if (entry is null)
                continue;

            results.Add(ClassifyEntry(name, scopeLabel, entry));
        }
    }

    private McpImportedConnection ClassifyEntry(string name, string scopeLabel, ClaudeMcpServerEntry entry)
    {
        // Skip Hypa self-entry: name is "hypa", command is bare "hypa", or command starts with "hypa serve".
        if (string.Equals(name, "hypa", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Command?.Trim(), "hypa", StringComparison.OrdinalIgnoreCase) ||
            IsHypaServeCommand(entry.Command))
        {
            return new McpImportedConnection(
                AgentKey, scopeLabel, name, null, string.Empty,
                McpImportCandidateStatus.SkippedSelf, "Hypa self-entry");
        }

        // Check for unsafe raw secrets in env dict.
        if (entry.Env is { Count: > 0 } env)
        {
            foreach (var value in env.Values)
            {
                if (!string.IsNullOrEmpty(value) &&
                    !value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) &&
                    !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    return new McpImportedConnection(
                        AgentKey, scopeLabel, name, null, string.Empty,
                        McpImportCandidateStatus.SkippedUnsafeSecret,
                        "env map contains raw values that cannot be imported safely");
                }
            }
        }

        McpTransportKind transportKind;
        string? endpoint;

        var typeStr = (entry.Type ?? string.Empty).ToLowerInvariant();

        if (typeStr == "stdio" || (string.IsNullOrEmpty(typeStr) && entry.Command is not null))
        {
            if (string.IsNullOrWhiteSpace(entry.Command))
            {
                return new McpImportedConnection(
                    AgentKey, scopeLabel, name, null, string.Empty,
                    McpImportCandidateStatus.SkippedIncomplete,
                    "stdio entry missing command");
            }

            transportKind = McpTransportKind.Stdio;
            var args = entry.Args is { Length: > 0 }
                ? " " + string.Join(" ", entry.Args)
                : string.Empty;
            endpoint = (entry.Command + args).Trim();
        }
        else if (typeStr is "streamablehttp" or "streamable_http")
        {
            var url = entry.Url ?? entry.Endpoint;
            if (string.IsNullOrWhiteSpace(url))
                return new McpImportedConnection(
                    AgentKey, scopeLabel, name, null, string.Empty,
                    McpImportCandidateStatus.SkippedIncomplete,
                    "remote entry missing url/endpoint");
            transportKind = McpTransportKind.Http;
            endpoint = url;
        }
        else if (typeStr == "sse")
        {
            var url = entry.Url ?? entry.Endpoint;
            if (string.IsNullOrWhiteSpace(url))
                return new McpImportedConnection(
                    AgentKey, scopeLabel, name, null, string.Empty,
                    McpImportCandidateStatus.SkippedIncomplete,
                    "remote entry missing url/endpoint");
            transportKind = McpTransportKind.Sse;
            endpoint = url;
        }
        else if (typeStr is "httpautodetect" or "http")
        {
            var url = entry.Url ?? entry.Endpoint;
            if (string.IsNullOrWhiteSpace(url))
                return new McpImportedConnection(
                    AgentKey, scopeLabel, name, null, string.Empty,
                    McpImportCandidateStatus.SkippedIncomplete,
                    "remote entry missing url/endpoint");
            transportKind = McpTransportKind.HttpAutoDetect;
            endpoint = url;
        }
        else
        {
            return new McpImportedConnection(
                AgentKey, scopeLabel, name, null, string.Empty,
                McpImportCandidateStatus.SkippedUnsupported,
                $"unsupported transport type: {entry.Type}");
        }

        var authResult = ExtractAuthConfig(entry.Env);
        if (!authResult.IsOk)
            return new McpImportedConnection(
                AgentKey, scopeLabel, name, null, string.Empty,
                McpImportCandidateStatus.SkippedUnsupported,
                authResult.Error);

        var server = new McpServerDefinition(
            name,
            new McpTransportConfig(transportKind, endpoint),
            authResult.Value,
            Tls: null,
            ConnectTimeout: null,
            RequestTimeout: null);

        var fingerprint = McpServerImportService.ComputeFingerprint(server);

        return new McpImportedConnection(
            AgentKey, scopeLabel, name, server, fingerprint,
            McpImportCandidateStatus.Importable, null);
    }

    private static bool IsHypaServeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var trimmed = command.Trim();
        return string.Equals(trimmed, "hypa serve", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("hypa serve ", StringComparison.OrdinalIgnoreCase);
    }

    private static Result<McpAuthConfig, string> ExtractAuthConfig(Dictionary<string, string>? env)
    {
        if (env is null || env.Count == 0)
            return Result<McpAuthConfig, string>.Ok(new NoneAuthConfig());

        // Look for Authorization header (Bearer token).
        if (env.TryGetValue("Authorization", out var authValue) && !string.IsNullOrWhiteSpace(authValue))
        {
            if (authValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var tokenRef = authValue["Bearer ".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(tokenRef) &&
                    (tokenRef.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
                     tokenRef.StartsWith("file:", StringComparison.OrdinalIgnoreCase)))
                {
                    return Result<McpAuthConfig, string>.Ok(new BearerAuthConfig(tokenRef));
                }
            }
            else if (authValue.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
                     authValue.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return Result<McpAuthConfig, string>.Ok(new BearerAuthConfig(authValue));
            }
        }

        return Result<McpAuthConfig, string>.Ok(new NoneAuthConfig());
    }
}
