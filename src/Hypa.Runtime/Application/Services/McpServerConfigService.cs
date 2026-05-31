using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Services;

public sealed record McpServerAddRequest(
    string Name,
    string Transport,
    string Endpoint,
    string AuthType,
    McpServerAddAuthOptions Auth,
    McpServerAddTlsOptions? Tls,
    int? ConnectTimeoutSeconds,
    int? RequestTimeoutSeconds,
    bool Replace,
    bool DryRun,
    bool SkipProbe = false,
    bool ForceProbeInDryRun = false);

public sealed record McpServerAddAuthOptions(
    string? TokenRef = null,
    string? HeaderName = null,
    string? ValueRef = null,
    bool? InQueryString = null,
    string? UsernameRef = null,
    string? PasswordRef = null,
    string? TokenUrl = null,
    string? ClientIdRef = null,
    string? ClientSecretRef = null,
    string[]? Scopes = null,
    string? AuthUrl = null,
    string? ClientId = null,
    string? ClientCertRef = null,
    string? ClientKeyRef = null);

public sealed record McpServerAddTlsOptions(
    string? CaCertPath,
    string? ClientCertPath,
    string? ClientKeyPath);

public sealed record McpServerAddResult(
    bool Success,
    McpServerDefinition? Server,
    IReadOnlyList<string> Errors,
    McpServerProbeResult? Probe = null);

public sealed class McpServerConfigService(
    IMcpServerConfigReader reader,
    IMcpServerConfigWriter writer,
    McpConfigValidationService validator,
    IMcpServerProbe probe)
{
    private static readonly string[] ValidSecretRefPrefixes = ["env:", "file:"];

    public async Task<McpServerAddResult> AddAsync(McpServerAddRequest request, CancellationToken ct)
    {
        var errors = new List<string>();

        ValidateTimeouts(request, errors);
        ValidateSecretRefs(request, errors);
        ValidateAuthRequiredFields(request, errors);

        if (errors.Count > 0)
            return new McpServerAddResult(false, null, errors);

        var readResult = await reader.ReadEditableAsync(ct);
        if (!readResult.IsOk)
            return new McpServerAddResult(false, null, [$"InvalidConfig: {readResult.Error.Message}"]);

        var existing = readResult.Value;

        var isDuplicate = existing.Any(s =>
            string.Equals(s.Name, request.Name, StringComparison.OrdinalIgnoreCase));

        if (isDuplicate && !request.Replace)
            return new McpServerAddResult(false, null,
                [$"DuplicateServer: MCP server '{request.Name}' already exists. Use --replace to overwrite it."]);

        var newDef = MapToDefinition(request);

        List<McpServerDefinition> candidates;
        if (request.Replace)
        {
            candidates = existing
                .Where(s => !string.Equals(s.Name, request.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            candidates.Add(newDef);
        }
        else
        {
            candidates = [.. existing, newDef];
        }

        var validationResult = validator.Validate(candidates);
        if (!validationResult.IsOk)
        {
            var validationErrors = validationResult.Error
                .Select(e => $"InvalidConfig: {e.ServerName} {e.Field}: {e.Message}")
                .ToList();
            return new McpServerAddResult(false, null, validationErrors);
        }

        if (request.DryRun && !request.ForceProbeInDryRun)
            return new McpServerAddResult(true, newDef, []);

        var isRemote = newDef.Transport.Kind is
            McpTransportKind.Http or McpTransportKind.Sse or McpTransportKind.HttpAutoDetect;

        if (!request.SkipProbe && isRemote)
        {
            var probeResult = await probe.ProbeAsync(newDef, ct);
            if (probeResult.Status != McpServerProbeStatus.Reachable)
            {
                var msg = probeResult.Status switch
                {
                    McpServerProbeStatus.AuthRequired => $"AuthRequired: {probeResult.Message}",
                    McpServerProbeStatus.InvalidConfig => $"InvalidConfig: {probeResult.Message}",
                    McpServerProbeStatus.Timeout => $"Timeout: {probeResult.Message}",
                    McpServerProbeStatus.ConnectionFailed => $"ConnectionFailed: {probeResult.Message}",
                    _ => $"ProbeFailed: {probeResult.Message}",
                };
                return new McpServerAddResult(false, newDef, [msg], probeResult);
            }

            if (request.DryRun)
                return new McpServerAddResult(true, newDef, [], probeResult);

            var writeResult = await writer.WriteAsync(candidates, ct);
            if (!writeResult.IsOk)
                return new McpServerAddResult(false, null, [$"InvalidConfig: {writeResult.Error.Message}"], probeResult);

            return new McpServerAddResult(true, newDef, [], probeResult);
        }

        var writeRes = await writer.WriteAsync(candidates, ct);
        if (!writeRes.IsOk)
            return new McpServerAddResult(false, null, [$"InvalidConfig: {writeRes.Error.Message}"]);

        return new McpServerAddResult(true, newDef, []);
    }

    private static void ValidateTimeouts(McpServerAddRequest request, List<string> errors)
    {
        if (request.ConnectTimeoutSeconds is not null and <= 0)
            errors.Add("InvalidOption: --connect-timeout-seconds must be a positive integer.");
        if (request.RequestTimeoutSeconds is not null and <= 0)
            errors.Add("InvalidOption: --request-timeout-seconds must be a positive integer.");
    }

    private static void ValidateSecretRefs(McpServerAddRequest request, List<string> errors)
    {
        var a = request.Auth;
        CheckRef(a.TokenRef, "--token-ref", errors);
        CheckRef(a.ValueRef, "--value-ref", errors);
        CheckRef(a.UsernameRef, "--username-ref", errors);
        CheckRef(a.PasswordRef, "--password-ref", errors);
        CheckRef(a.ClientIdRef, "--client-id-ref", errors);
        CheckRef(a.ClientSecretRef, "--client-secret-ref", errors);
        CheckRef(a.ClientCertRef, "--client-cert-ref", errors);
        CheckRef(a.ClientKeyRef, "--client-key-ref", errors);
    }

    private static void CheckRef(string? value, string optionName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (ValidSecretRefPrefixes.Any(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return;
        errors.Add($"InvalidSecretRef: {optionName} must use an explicit resolver prefix such as env: or file:.");
    }

    private static void ValidateAuthRequiredFields(McpServerAddRequest request, List<string> errors)
    {
        var a = request.Auth;
        switch (request.AuthType.ToLowerInvariant())
        {
            case "bearer":
                if (string.IsNullOrWhiteSpace(a.TokenRef))
                    errors.Add("MissingOption: --token-ref is required for bearer auth.");
                break;
            case "apikey":
                if (string.IsNullOrWhiteSpace(a.HeaderName))
                    errors.Add("MissingOption: --header-name is required for apiKey auth.");
                if (string.IsNullOrWhiteSpace(a.ValueRef))
                    errors.Add("MissingOption: --value-ref is required for apiKey auth.");
                break;
            case "basic":
                if (string.IsNullOrWhiteSpace(a.UsernameRef))
                    errors.Add("MissingOption: --username-ref is required for basic auth.");
                if (string.IsNullOrWhiteSpace(a.PasswordRef))
                    errors.Add("MissingOption: --password-ref is required for basic auth.");
                break;
            case "oauth2clientcredentials":
                if (string.IsNullOrWhiteSpace(a.TokenUrl))
                    errors.Add("MissingOption: --token-url is required for oauth2ClientCredentials auth.");
                if (string.IsNullOrWhiteSpace(a.ClientIdRef))
                    errors.Add("MissingOption: --client-id-ref is required for oauth2ClientCredentials auth.");
                if (string.IsNullOrWhiteSpace(a.ClientSecretRef))
                    errors.Add("MissingOption: --client-secret-ref is required for oauth2ClientCredentials auth.");
                break;
            case "oauth2devicecode":
                if (string.IsNullOrWhiteSpace(a.AuthUrl))
                    errors.Add("MissingOption: --auth-url is required for oauth2DeviceCode auth.");
                if (string.IsNullOrWhiteSpace(a.TokenUrl))
                    errors.Add("MissingOption: --token-url is required for oauth2DeviceCode auth.");
                if (string.IsNullOrWhiteSpace(a.ClientId))
                    errors.Add("MissingOption: --client-id is required for oauth2DeviceCode auth.");
                break;
            case "mtls":
                if (string.IsNullOrWhiteSpace(a.ClientCertRef))
                    errors.Add("MissingOption: --client-cert-ref is required for mtls auth.");
                if (string.IsNullOrWhiteSpace(a.ClientKeyRef))
                    errors.Add("MissingOption: --client-key-ref is required for mtls auth.");
                break;
        }
    }

    internal static McpServerDefinition MapToDefinition(McpServerAddRequest request)
    {
        var transportKind = request.Transport.ToLowerInvariant() switch
        {
            "stdio" => McpTransportKind.Stdio,
            "streamablehttp" => McpTransportKind.Http,
            "sse" => McpTransportKind.Sse,
            "http" or "httpautodetect" => McpTransportKind.HttpAutoDetect,
            _ => McpTransportKind.Unknown,
        };
        var transport = new McpTransportConfig(transportKind, request.Endpoint);

        McpAuthConfig auth = request.AuthType.ToLowerInvariant() switch
        {
            "none" => new NoneAuthConfig(),
            "bearer" => new BearerAuthConfig(request.Auth.TokenRef ?? string.Empty),
            "apikey" => new ApiKeyAuthConfig(
                request.Auth.HeaderName ?? string.Empty,
                request.Auth.ValueRef ?? string.Empty,
                request.Auth.InQueryString ?? false),
            "basic" => new BasicAuthConfig(
                request.Auth.UsernameRef ?? string.Empty,
                request.Auth.PasswordRef ?? string.Empty),
            "oauth2clientcredentials" => new OAuth2ClientCredentialsConfig(
                request.Auth.TokenUrl ?? string.Empty,
                request.Auth.ClientIdRef ?? string.Empty,
                request.Auth.ClientSecretRef ?? string.Empty,
                request.Auth.Scopes),
            "oauth2devicecode" => new OAuth2DeviceCodeConfig(
                request.Auth.AuthUrl ?? string.Empty,
                request.Auth.TokenUrl ?? string.Empty,
                request.Auth.ClientId ?? string.Empty,
                request.Auth.Scopes),
            "mtls" => new MtlsConfig(request.Auth.ClientCertRef, request.Auth.ClientKeyRef),
            "mcpoauth" => new McpOAuthConfig(request.Auth.ClientId, request.Auth.ClientSecretRef, request.Auth.Scopes),
            _ => new UnknownAuthConfig(request.AuthType),
        };

        McpTlsConfig? tls = null;
        if (request.Tls is { } t)
        {
            if (t.CaCertPath is not null || t.ClientCertPath is not null || t.ClientKeyPath is not null)
                tls = new McpTlsConfig(t.CaCertPath, t.ClientCertPath, t.ClientKeyPath);
        }

        var connectTimeout = request.ConnectTimeoutSeconds.HasValue
            ? TimeSpan.FromSeconds(request.ConnectTimeoutSeconds.Value)
            : (TimeSpan?)null;
        var requestTimeout = request.RequestTimeoutSeconds.HasValue
            ? TimeSpan.FromSeconds(request.RequestTimeoutSeconds.Value)
            : (TimeSpan?)null;

        return new McpServerDefinition(request.Name, transport, auth, tls, connectTimeout, requestTimeout);
    }
}
