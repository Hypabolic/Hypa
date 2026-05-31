using System.Security.Cryptography;
using System.Text;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Services;

public sealed record McpImportRequest(
    string? AgentKey,
    McpImportScope Scope,
    string? ProjectRoot,
    bool Replace,
    bool DryRun);

public sealed record McpImportReport(
    IReadOnlyList<McpImportSourceResult> Sources,
    int ImportedCount,
    int AlreadyPresentCount,
    int SkippedCount,
    int ConflictCount);

public sealed record McpImportSourceResult(
    string Agent,
    string Scope,
    IReadOnlyList<McpImportedConnection> Connections);

public sealed class McpServerImportService(
    IEnumerable<IMcpConnectionImportSource> sources,
    IMcpServerConfigReader reader,
    IMcpServerConfigWriter writer,
    McpConfigValidationService validator) : IMcpServerImportService
{
    public async Task<Result<McpImportReport, Error>> ImportAsync(McpImportRequest request, CancellationToken ct)
    {
        var selectedSources = sources
            .Where(s => request.AgentKey is null ||
                        string.Equals(s.AgentKey, request.AgentKey, StringComparison.OrdinalIgnoreCase))
            .Where(s => s.SupportsScope(request.Scope))
            .ToList();

        if (selectedSources.Count == 0)
            return Result<McpImportReport, Error>.Ok(new McpImportReport([], 0, 0, 0, 0));

        // Discover candidates from all selected sources (global before project for All scope).
        var allDiscovery = new List<(McpImportSourceResult SourceResult, McpImportedConnection Conn)>();

        McpImportScope[] scopeSequence = request.Scope == McpImportScope.All
            ? [McpImportScope.Global, McpImportScope.Project]
            : [request.Scope];

        foreach (var src in selectedSources)
        {
            foreach (var scopeItem in scopeSequence)
            {
                var discovered = await src.DiscoverAsync(
                    new McpImportDiscoveryRequest(scopeItem, request.ProjectRoot), ct);

                var scopeLabel = scopeItem.ToString().ToLowerInvariant();
                var sourceResult = new McpImportSourceResult(src.AgentKey, scopeLabel, discovered);

                foreach (var conn in discovered)
                    allDiscovery.Add((sourceResult, conn));
            }
        }

        // Load existing config once.
        var readResult = await reader.ReadEditableAsync(ct);
        if (!readResult.IsOk)
            return Result<McpImportReport, Error>.Fail(readResult.Error);

        var existing = readResult.Value;

        var existingByName = existing.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var seenFingerprints = new HashSet<string>(existing.Select(ComputeFingerprint));
        var acceptedImportNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var toImport = new List<McpServerDefinition>();
        var sourceResultConnections = new Dictionary<McpImportSourceResult, List<McpImportedConnection>>();
        int importedCount = 0, alreadyPresentCount = 0, skippedCount = 0, conflictCount = 0;

        foreach (var (sourceResult, conn) in allDiscovery)
        {
            if (!sourceResultConnections.ContainsKey(sourceResult))
                sourceResultConnections[sourceResult] = [];

            if (conn.Status != McpImportCandidateStatus.Importable || conn.Server is null)
            {
                sourceResultConnections[sourceResult].Add(conn);
                skippedCount++;
                continue;
            }

            var fp = conn.Fingerprint;

            // Check if name exists in existing config
            if (existingByName.TryGetValue(conn.SourceName, out var existingServer))
            {
                var existingFp = ComputeFingerprint(existingServer);
                if (string.Equals(existingFp, fp, StringComparison.Ordinal))
                {
                    sourceResultConnections[sourceResult].Add(conn with
                    {
                        Status = McpImportCandidateStatus.SkippedDuplicate,
                        Detail = "already present",
                    });
                    alreadyPresentCount++;
                    continue;
                }

                if (!request.Replace)
                {
                    sourceResultConnections[sourceResult].Add(conn with
                    {
                        Status = McpImportCandidateStatus.SkippedConflict,
                        Detail = "conflict — different configuration already exists",
                    });
                    conflictCount++;
                    continue;
                }
            }

            // Check if name was already accepted from a different source in this batch
            if (acceptedImportNames.Contains(conn.SourceName))
            {
                sourceResultConnections[sourceResult].Add(conn with
                {
                    Status = McpImportCandidateStatus.SkippedConflict,
                    Detail = "conflict — same name already accepted from another source",
                });
                conflictCount++;
                continue;
            }

            // Track fingerprint for cross-source duplicate detection.
            bool isDuplicateConnection = !seenFingerprints.Add(fp);
            var importedConn = isDuplicateConnection
                ? conn with
                {
                    Status = McpImportCandidateStatus.SkippedDuplicate,
                    Detail = $"duplicate connection{(conn.Detail is not null ? $"; {conn.Detail}" : string.Empty)}"
                }
                : conn;

            sourceResultConnections[sourceResult].Add(importedConn);
            if (isDuplicateConnection)
            {
                continue;
            }

            toImport.Add(conn.Server);
            acceptedImportNames.Add(conn.SourceName);
            importedCount++;
        }

        if (toImport.Count > 0 && !request.DryRun)
        {
            List<McpServerDefinition> merged;
            if (request.Replace)
            {
                var replacedNames = new HashSet<string>(
                    toImport.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
                merged = [.. existing.Where(s => !replacedNames.Contains(s.Name)), .. toImport];
            }
            else
            {
                merged = [.. existing, .. toImport];
            }

            // Validate and propagate errors.
            var validationResult = validator.Validate(merged);
            if (!validationResult.IsOk)
            {
                var errorMsg = string.Join("; ", validationResult.Error.Select(e => $"{e.ServerName}.{e.Field}: {e.Message}"));
                return Result<McpImportReport, Error>.Fail(new Error("ValidationFailed", errorMsg));
            }

            var writeResult = await writer.WriteAsync(merged, ct);
            if (!writeResult.IsOk)
                return Result<McpImportReport, Error>.Fail(writeResult.Error);
        }

        var sourceResults = sourceResultConnections
            .Select(kv => kv.Key with { Connections = kv.Value })
            .ToList();

        var report = new McpImportReport(sourceResults, importedCount, alreadyPresentCount, skippedCount, conflictCount);
        return Result<McpImportReport, Error>.Ok(report);
    }

    public static string ComputeFingerprint(McpServerDefinition def)
    {
        var sb = new StringBuilder();
        sb.Append("v1|");
        sb.Append(def.Transport.Kind.ToString().ToLowerInvariant());
        sb.Append('|');
        sb.Append((def.Transport.Endpoint ?? string.Empty).Trim().ToLowerInvariant());
        sb.Append('|');
        AppendAuthMetadata(sb, def.Auth);
        sb.Append('|');
        if (def.Tls is { } tls)
        {
            sb.Append(tls.CaCertPath ?? string.Empty);
            sb.Append('|');
            sb.Append(tls.ClientCertPath ?? string.Empty);
            sb.Append('|');
            sb.Append(tls.ClientKeyPath ?? string.Empty);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendAuthMetadata(StringBuilder sb, McpAuthConfig auth)
    {
        switch (auth)
        {
            case NoneAuthConfig:
                sb.Append("none");
                break;
            case BearerAuthConfig:
                // TokenRef is a secret ref — excluded from fingerprint.
                sb.Append("bearer");
                break;
            case ApiKeyAuthConfig ak:
                sb.Append("apikey|");
                sb.Append(ak.HeaderName.ToLowerInvariant());
                sb.Append('|');
                sb.Append(ak.InQueryString ? "query" : "header");
                // ValueRef is a secret ref — excluded.
                break;
            case BasicAuthConfig:
                // UsernameRef and PasswordRef are secret refs — excluded.
                sb.Append("basic");
                break;
            case OAuth2ClientCredentialsConfig oc:
                // ClientIdRef and ClientSecretRef are secret refs — excluded.
                sb.Append("oauth2clientcredentials|");
                sb.Append(oc.TokenUrl.ToLowerInvariant());
                sb.Append('|');
                sb.Append(string.Join(",", (oc.Scopes ?? []).Select(s => s.ToLowerInvariant())));
                break;
            case OAuth2DeviceCodeConfig od:
                // ClientId is a public identifier (non-secret).
                sb.Append("oauth2devicecode|");
                sb.Append(od.AuthUrl.ToLowerInvariant());
                sb.Append('|');
                sb.Append(od.TokenUrl.ToLowerInvariant());
                sb.Append('|');
                sb.Append(od.ClientId.ToLowerInvariant());
                sb.Append('|');
                sb.Append(string.Join(",", (od.Scopes ?? []).Select(s => s.ToLowerInvariant())));
                break;
            case MtlsConfig:
                // ClientCertRef and ClientKeyRef are secret refs — excluded.
                sb.Append("mtls");
                break;
            default:
                sb.Append("unknown");
                break;
        }
    }
}
