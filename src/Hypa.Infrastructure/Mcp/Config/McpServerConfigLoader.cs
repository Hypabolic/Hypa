using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Infrastructure.Mcp.Config;

public sealed class McpServerConfigLoader : IMcpServerDefinitionRepository
{
    private readonly string _configFilePath;

    public McpServerConfigLoader(IConfigLoader configLoader)
        : this(ResolveStoragePath(configLoader))
    { }

    internal McpServerConfigLoader(string storagePath)
    {
        _configFilePath = Path.Combine(storagePath, "mcp-servers.json");
    }

    public async Task<Result<IReadOnlyList<McpServerDefinition>, Error>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_configFilePath))
            return Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(Array.Empty<McpServerDefinition>());

        try
        {
            await using var stream = File.OpenRead(_configFilePath);
            var file = await JsonSerializer.DeserializeAsync(
                stream,
                McpServersJsonContext.Default.McpServersFileJson,
                ct);

            if (file?.Servers is null or { Count: 0 })
                return Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(Array.Empty<McpServerDefinition>());

            var definitions = file.Servers
                .Where(s => s is not null)
                .Select(MapToDefinition)
                .ToList();

            return Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(definitions);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<McpServerDefinition>, Error>.Fail(
                new Error("ParseFailed", $"Failed to parse mcp-servers.json: {ex.Message}"));
        }
    }

    private static McpServerDefinition MapToDefinition(McpServerJson json)
    {
        var name = json.Name ?? string.Empty;
        var transport = MapTransport(json);
        var auth = MapAuth(json.Auth);
        var tls = MapTls(json.Tls);
        var connectTimeout = json.ConnectTimeoutSeconds.HasValue
            ? TimeSpan.FromSeconds(json.ConnectTimeoutSeconds.Value)
            : (TimeSpan?)null;
        var requestTimeout = json.RequestTimeoutSeconds.HasValue
            ? TimeSpan.FromSeconds(json.RequestTimeoutSeconds.Value)
            : (TimeSpan?)null;

        return new McpServerDefinition(name, transport, auth, tls, connectTimeout, requestTimeout);
    }

    private static McpTransportConfig MapTransport(McpServerJson json)
    {
        var kind = json.Transport?.ToLowerInvariant() switch
        {
            null or "" or "stdio" => McpTransportKind.Stdio,
            "streamablehttp" => McpTransportKind.Http,
            "sse" => McpTransportKind.Sse,
            "http" or "httpautodetect" => McpTransportKind.HttpAutoDetect,
            _ => McpTransportKind.Unknown,
        };
        return new McpTransportConfig(kind, json.Endpoint);
    }

    private static McpAuthConfig MapAuth(McpAuthJson? auth)
    {
        if (auth is null)
            return new NoneAuthConfig();

        if (string.IsNullOrWhiteSpace(auth.Type))
            return new UnknownAuthConfig(string.Empty);

        return auth.Type.ToLowerInvariant() switch
        {
            "none" => new NoneAuthConfig(),
            "bearer" => new BearerAuthConfig(auth.TokenRef ?? string.Empty),
            "apikey" => new ApiKeyAuthConfig(
                auth.HeaderName ?? string.Empty,
                auth.ValueRef ?? string.Empty,
                auth.InQueryString ?? false),
            "basic" => new BasicAuthConfig(
                auth.UsernameRef ?? string.Empty,
                auth.PasswordRef ?? string.Empty),
            "oauth2clientcredentials" => new OAuth2ClientCredentialsConfig(
                auth.TokenUrl ?? string.Empty,
                auth.ClientIdRef ?? string.Empty,
                auth.ClientSecretRef ?? string.Empty,
                auth.Scopes),
            "oauth2devicecode" => new OAuth2DeviceCodeConfig(
                auth.AuthUrl ?? string.Empty,
                auth.TokenUrl ?? string.Empty,
                auth.ClientId ?? string.Empty,
                auth.Scopes),
            "mtls" => new MtlsConfig(auth.ClientCertRef, auth.ClientKeyRef),
            "mcpoauth" => new McpOAuthConfig(auth.ClientId, auth.ClientSecretRef, auth.Scopes),
            _ => new UnknownAuthConfig(auth.Type),
        };
    }

    private static McpTlsConfig? MapTls(McpTlsJson? tls)
    {
        if (tls is null)
            return null;
        return new McpTlsConfig(tls.CaCertPath, tls.ClientCertPath, tls.ClientKeyPath);
    }

    private static string ResolveStoragePath(IConfigLoader configLoader)
    {
        var result = configLoader.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        return result.IsOk ? result.Value.StoragePath : HypaConfig.Default.StoragePath;
    }
}
