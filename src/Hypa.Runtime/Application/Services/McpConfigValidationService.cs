using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Services;

public sealed record McpConfigError(string ServerName, string Field, string Message);

public sealed class McpConfigValidationService
{
    public Result<Unit, IReadOnlyList<McpConfigError>> Validate(IReadOnlyList<McpServerDefinition> servers)
    {
        var errors = new List<McpConfigError>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers)
        {
            if (string.IsNullOrWhiteSpace(server.Name))
                errors.Add(new McpConfigError(server.Name, "Name", "Server name must not be empty."));
            else if (!seen.Add(server.Name))
                errors.Add(new McpConfigError(server.Name, "Name", $"Duplicate server name '{server.Name}'."));
        }

        foreach (var server in servers)
        {
            ValidateTransport(server, errors);
            ValidateAuth(server, errors);
            ValidateTimeouts(server, errors);
        }

        foreach (var server in servers)
            ValidateTls(server, errors);

        return errors.Count == 0
            ? Result<Unit, IReadOnlyList<McpConfigError>>.Ok(Unit.Value)
            : Result<Unit, IReadOnlyList<McpConfigError>>.Fail(errors);
    }

    private static void ValidateTransport(McpServerDefinition server, List<McpConfigError> errors)
    {
        var transport = server.Transport;
        switch (transport.Kind)
        {
            case McpTransportKind.Unknown:
                errors.Add(new McpConfigError(server.Name, "Transport.Kind",
                    "Unknown transport type. Valid values: stdio, streamableHttp, sse, httpAutoDetect."));
                break;
            case McpTransportKind.Stdio:
                if (string.IsNullOrWhiteSpace(transport.Endpoint))
                    errors.Add(new McpConfigError(server.Name, "Transport.Endpoint",
                        "Stdio transport requires an Endpoint containing the command to execute."));
                break;
            case McpTransportKind.Http:
            case McpTransportKind.Sse:
            case McpTransportKind.HttpAutoDetect:
                if (string.IsNullOrWhiteSpace(transport.Endpoint))
                {
                    errors.Add(new McpConfigError(server.Name, "Transport.Endpoint",
                        $"{transport.Kind} transport requires an Endpoint."));
                }
                else if (!Uri.TryCreate(transport.Endpoint, UriKind.Absolute, out _))
                {
                    errors.Add(new McpConfigError(server.Name, "Transport.Endpoint",
                        $"Endpoint '{transport.Endpoint}' is not a valid absolute URI."));
                }
                break;
        }
    }

    private static void ValidateAuth(McpServerDefinition server, List<McpConfigError> errors)
    {
        switch (server.Auth)
        {
            case BearerAuthConfig bearer:
                if (string.IsNullOrWhiteSpace(bearer.TokenRef))
                    errors.Add(new McpConfigError(server.Name, "Auth.TokenRef", "Bearer auth requires TokenRef."));
                break;

            case ApiKeyAuthConfig apiKey:
                if (string.IsNullOrWhiteSpace(apiKey.HeaderName))
                    errors.Add(new McpConfigError(server.Name, "Auth.HeaderName", "ApiKey auth requires HeaderName."));
                if (string.IsNullOrWhiteSpace(apiKey.ValueRef))
                    errors.Add(new McpConfigError(server.Name, "Auth.ValueRef", "ApiKey auth requires ValueRef."));
                break;

            case BasicAuthConfig basic:
                if (string.IsNullOrWhiteSpace(basic.UsernameRef))
                    errors.Add(new McpConfigError(server.Name, "Auth.UsernameRef", "Basic auth requires UsernameRef."));
                if (string.IsNullOrWhiteSpace(basic.PasswordRef))
                    errors.Add(new McpConfigError(server.Name, "Auth.PasswordRef", "Basic auth requires PasswordRef."));
                break;

            case OAuth2ClientCredentialsConfig oauth2Cc:
                if (string.IsNullOrWhiteSpace(oauth2Cc.TokenUrl))
                    errors.Add(new McpConfigError(server.Name, "Auth.TokenUrl", "OAuth2ClientCredentials requires TokenUrl."));
                if (string.IsNullOrWhiteSpace(oauth2Cc.ClientIdRef))
                    errors.Add(new McpConfigError(server.Name, "Auth.ClientIdRef", "OAuth2ClientCredentials requires ClientIdRef."));
                if (string.IsNullOrWhiteSpace(oauth2Cc.ClientSecretRef))
                    errors.Add(new McpConfigError(server.Name, "Auth.ClientSecretRef", "OAuth2ClientCredentials requires ClientSecretRef."));
                break;

            case OAuth2DeviceCodeConfig oauth2Dc:
                if (string.IsNullOrWhiteSpace(oauth2Dc.AuthUrl))
                    errors.Add(new McpConfigError(server.Name, "Auth.AuthUrl", "OAuth2DeviceCode requires AuthUrl."));
                if (string.IsNullOrWhiteSpace(oauth2Dc.TokenUrl))
                    errors.Add(new McpConfigError(server.Name, "Auth.TokenUrl", "OAuth2DeviceCode requires TokenUrl."));
                if (string.IsNullOrWhiteSpace(oauth2Dc.ClientId))
                    errors.Add(new McpConfigError(server.Name, "Auth.ClientId", "OAuth2DeviceCode requires ClientId."));
                break;

            case MtlsConfig mtls:
                if (string.IsNullOrWhiteSpace(mtls.ClientCertRef))
                    errors.Add(new McpConfigError(server.Name, "Auth.ClientCertRef", "mTLS requires ClientCertRef."));
                if (string.IsNullOrWhiteSpace(mtls.ClientKeyRef))
                    errors.Add(new McpConfigError(server.Name, "Auth.ClientKeyRef", "mTLS requires ClientKeyRef."));
                break;

            case UnknownAuthConfig unknown:
                var authTypeMsg = string.IsNullOrWhiteSpace(unknown.Type)
                    ? "Auth.Type is required when an auth block is present. Valid values: none, bearer, apikey, basic, oauth2clientcredentials, oauth2devicecode, mtls."
                    : $"Unknown auth type '{unknown.Type}'. Valid values: none, bearer, apikey, basic, oauth2clientcredentials, oauth2devicecode, mtls.";
                errors.Add(new McpConfigError(server.Name, "Auth.Type", authTypeMsg));
                break;
        }
    }

    private static void ValidateTimeouts(McpServerDefinition server, List<McpConfigError> errors)
    {
        if (server.ConnectTimeout is { TotalSeconds: <= 0 })
            errors.Add(new McpConfigError(server.Name, "ConnectTimeout", "ConnectTimeout must be a positive duration."));
        if (server.RequestTimeout is { TotalSeconds: <= 0 })
            errors.Add(new McpConfigError(server.Name, "RequestTimeout", "RequestTimeout must be a positive duration."));
    }

    private static void ValidateTls(McpServerDefinition server, List<McpConfigError> errors)
    {
        var tls = server.Tls;
        if (tls is null) return;

        var hasTlsMaterial = !string.IsNullOrWhiteSpace(tls.CaCertPath)
            || !string.IsNullOrWhiteSpace(tls.ClientCertPath)
            || !string.IsNullOrWhiteSpace(tls.ClientKeyPath);

        if (!hasTlsMaterial) return;

        if (server.Transport.Kind == McpTransportKind.Stdio)
        {
            errors.Add(new McpConfigError(server.Name, "Tls",
                "TLS options are not valid for stdio transport."));
            return;
        }

        var hasCert = !string.IsNullOrWhiteSpace(tls.ClientCertPath);
        var hasKey = !string.IsNullOrWhiteSpace(tls.ClientKeyPath);
        if (hasCert != hasKey)
            errors.Add(new McpConfigError(server.Name, "Tls.ClientCert",
                "TLS ClientCertPath and ClientKeyPath must be supplied together."));
    }
}
