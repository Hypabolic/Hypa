using Hypa.Infrastructure.Hooks;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Infrastructure.Mcp.Import;

public sealed class CodexMcpConnectionImportSource(string globalConfigPath) : IMcpConnectionImportSource
{
    public string AgentKey => "codex";

    public bool SupportsScope(McpImportScope scope) => true;

    public async Task<IReadOnlyList<McpImportedConnection>> DiscoverAsync(
        McpImportDiscoveryRequest request, CancellationToken ct)
    {
        var results = new List<McpImportedConnection>();

        if (request.Scope is McpImportScope.Global or McpImportScope.All)
            await ParseFileAsync(globalConfigPath, "global", results, ct);

        if (request.Scope is McpImportScope.Project or McpImportScope.All)
        {
            if (!string.IsNullOrWhiteSpace(request.ProjectRoot))
            {
                var projectPath = Path.Combine(request.ProjectRoot, ".codex", "config.toml");
                await ParseFileAsync(projectPath, "project", results, ct);
            }
        }

        return results;
    }

    private static async Task ParseFileAsync(
        string filePath,
        string scopeLabel,
        List<McpImportedConnection> results,
        CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return;

        List<string> lines;
        try
        {
            var content = await File.ReadAllTextAsync(filePath, ct);
            lines = [.. content.Split('\n')];
        }
        catch (Exception ex)
        {
            results.Add(new McpImportedConnection(
                "codex", scopeLabel, "unknown", null, string.Empty,
                McpImportCandidateStatus.ParseError, ex.Message));
            return;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!TomlSectionHelper.TryParseHeaderPath(line, out var headerPath))
                continue;

            if (!headerPath.StartsWith("mcp_servers.", StringComparison.OrdinalIgnoreCase))
                continue;

            var serverName = headerPath["mcp_servers.".Length..];
            if (string.IsNullOrWhiteSpace(serverName) || serverName.Contains('.'))
                continue;

            var endIdx = TomlSectionHelper.FindNextNonDescendantSection(lines, i + 1, headerPath);
            var bodyLines = endIdx >= 0
                ? lines.GetRange(i + 1, endIdx - (i + 1))
                : lines.GetRange(i + 1, lines.Count - (i + 1));

            var conn = ParseEntry(serverName, scopeLabel, bodyLines);
            results.Add(conn);
        }
    }

    private static McpImportedConnection ParseEntry(
        string name, string scopeLabel, List<string> bodyLines)
    {
        string? command = null;
        string? args = null;
        string? url = null;
        string? endpoint = null;
        string? bearerToken = null;

        foreach (var rawLine in bodyLines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                continue;

            var eqIdx = line.IndexOf('=');
            if (eqIdx < 0) continue;

            var key = line[..eqIdx].Trim().ToLowerInvariant();
            var rawValue = line[(eqIdx + 1)..].Trim();

            switch (key)
            {
                case "command":
                    command = UnquoteToml(rawValue);
                    break;
                case "args":
                    // Multiline arrays are not supported.
                    if (!rawValue.StartsWith('[') || !rawValue.EndsWith(']'))
                    {
                        return new McpImportedConnection(
                            "codex", scopeLabel, name, null, string.Empty,
                            McpImportCandidateStatus.ParseError,
                            "multiline args array is not supported; use inline array form: args = [\"a\", \"b\"]");
                    }
                    args = ParseInlineArray(rawValue);
                    break;
                case "url":
                    url = UnquoteToml(rawValue);
                    break;
                case "endpoint":
                    endpoint = UnquoteToml(rawValue);
                    break;
                case "bearer_token":
                    bearerToken = UnquoteToml(rawValue);
                    break;
            }
        }

        // Skip Hypa self-entry.
        if (string.Equals(name, "hypa", StringComparison.OrdinalIgnoreCase))
            return new McpImportedConnection("codex", scopeLabel, name, null, string.Empty,
                McpImportCandidateStatus.SkippedSelf, "Hypa self-entry");

        if (command is not null && IsHypaServeCommand(command))
            return new McpImportedConnection("codex", scopeLabel, name, null, string.Empty,
                McpImportCandidateStatus.SkippedSelf, "Hypa self-entry");

        // Determine transport.
        if (command is not null)
        {
            var authConfigResult = ExtractAuthConfig(bearerToken);
            if (!authConfigResult.IsOk)
            {
                return new McpImportedConnection(
                    "codex", scopeLabel, name, null, string.Empty,
                    McpImportCandidateStatus.SkippedUnsupported,
                    authConfigResult.Error);
            }

            var ep = args is not null ? $"{command} {args}".Trim() : command;
            var server = new McpServerDefinition(
                name,
                new McpTransportConfig(McpTransportKind.Stdio, ep),
                authConfigResult.Value!,
                Tls: null, ConnectTimeout: null, RequestTimeout: null);
            return new McpImportedConnection(
                "codex", scopeLabel, name, server,
                McpServerImportService.ComputeFingerprint(server),
                McpImportCandidateStatus.Importable, null);
        }

        var remoteUrl = url ?? endpoint;
        if (!string.IsNullOrWhiteSpace(remoteUrl))
        {
            var authConfigResult = ExtractAuthConfig(bearerToken);
            if (!authConfigResult.IsOk)
            {
                return new McpImportedConnection(
                    "codex", scopeLabel, name, null, string.Empty,
                    McpImportCandidateStatus.SkippedUnsupported,
                    authConfigResult.Error);
            }

            var server = new McpServerDefinition(
                name,
                new McpTransportConfig(McpTransportKind.HttpAutoDetect, remoteUrl),
                authConfigResult.Value!,
                Tls: null, ConnectTimeout: null, RequestTimeout: null);
            return new McpImportedConnection(
                "codex", scopeLabel, name, server,
                McpServerImportService.ComputeFingerprint(server),
                McpImportCandidateStatus.Importable, null);
        }

        return new McpImportedConnection("codex", scopeLabel, name, null, string.Empty,
            McpImportCandidateStatus.SkippedIncomplete,
            "no command or url/endpoint found");
    }

    private static string UnquoteToml(string value)
    {
        // Strip surrounding quotes (single or double).
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }
        return value;
    }

    private static string ParseInlineArray(string raw)
    {
        var inner = raw[1..^1]; // strip [ and ]
        if (string.IsNullOrWhiteSpace(inner))
            return string.Empty;

        var parts = inner.Split(',');
        return string.Join(" ", parts.Select(p => UnquoteToml(p.Trim())));
    }

    private static bool IsHypaServeCommand(string command)
    {
        var trimmed = command.Trim();
        return string.Equals(trimmed, "hypa", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "hypa serve", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("hypa serve ", StringComparison.OrdinalIgnoreCase);
    }

    private static (bool IsOk, McpAuthConfig? Value, string? Error) ExtractAuthConfig(string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            return (true, new NoneAuthConfig(), null);

        if (bearerToken.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
            bearerToken.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return (true, new BearerAuthConfig(bearerToken), null);
        }

        // Raw secrets (not env:/ or file:/) are unsafe and must not be imported
        return (false, null, "bearer_token contains a raw secret; use env:VAR_NAME or file:/path/to/file instead");
    }
}
