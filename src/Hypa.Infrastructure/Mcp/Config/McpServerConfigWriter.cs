using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Infrastructure.Mcp.Config;

public sealed class McpServerConfigWriter : IMcpServerConfigReader, IMcpServerConfigWriter
{
    private readonly string _configFilePath;
    private readonly string _configDir;

    public McpServerConfigWriter(IConfigLoader configLoader)
        : this(ResolveStoragePath(configLoader))
    { }

    internal McpServerConfigWriter(string storagePath)
    {
        _configDir = storagePath;
        _configFilePath = Path.Combine(storagePath, "mcp-servers.json");
    }

    public async Task<Result<IReadOnlyList<McpServerDefinition>, Error>> ReadEditableAsync(CancellationToken ct)
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

    public async Task<Result<Unit, Error>> WriteAsync(IReadOnlyList<McpServerDefinition> servers, CancellationToken ct)
    {
        Directory.CreateDirectory(_configDir);

        var tempPath = Path.Combine(_configDir, $"mcp-servers.{Guid.NewGuid():N}.tmp");
        try
        {
            var jsonList = servers.Select(MapToJson).ToList();
            var file = new McpServersFileJson(jsonList);

            await using (var stream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    file,
                    McpServersJsonContext.Default.McpServersFileJson,
                    ct);
                await stream.FlushAsync(ct);
            }

            File.Move(tempPath, _configFilePath, overwrite: true);
            return Result<Unit, Error>.Ok(Unit.Value);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            return Result<Unit, Error>.Fail(
                new Error("WriteFailed", $"Failed to write mcp-servers.json: {ex.Message}"));
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private static McpServerDefinition MapToDefinition(McpServerJson json)
    {
        var transportKind = json.Transport?.ToLowerInvariant() switch
        {
            null or "" or "stdio" => McpTransportKind.Stdio,
            "streamablehttp" => McpTransportKind.Http,
            "sse" => McpTransportKind.Sse,
            "http" or "httpautodetect" => McpTransportKind.HttpAutoDetect,
            _ => McpTransportKind.Unknown,
        };
        var transport = new McpTransportConfig(transportKind, json.Endpoint);

        McpAuthConfig auth = json.Auth is null
            ? new NoneAuthConfig()
            : string.IsNullOrWhiteSpace(json.Auth.Type)
                ? new UnknownAuthConfig(string.Empty)
                : json.Auth.Type.ToLowerInvariant() switch
                {
                    "none" => (McpAuthConfig)new NoneAuthConfig(),
                    "bearer" => new BearerAuthConfig(json.Auth.TokenRef ?? string.Empty),
                    "apikey" => new ApiKeyAuthConfig(
                        json.Auth.HeaderName ?? string.Empty,
                        json.Auth.ValueRef ?? string.Empty,
                        json.Auth.InQueryString ?? false),
                    "basic" => new BasicAuthConfig(
                        json.Auth.UsernameRef ?? string.Empty,
                        json.Auth.PasswordRef ?? string.Empty),
                    "oauth2clientcredentials" => new OAuth2ClientCredentialsConfig(
                        json.Auth.TokenUrl ?? string.Empty,
                        json.Auth.ClientIdRef ?? string.Empty,
                        json.Auth.ClientSecretRef ?? string.Empty,
                        json.Auth.Scopes),
                    "oauth2devicecode" => new OAuth2DeviceCodeConfig(
                        json.Auth.AuthUrl ?? string.Empty,
                        json.Auth.TokenUrl ?? string.Empty,
                        json.Auth.ClientId ?? string.Empty,
                        json.Auth.Scopes),
                    "mtls" => new MtlsConfig(json.Auth.ClientCertRef, json.Auth.ClientKeyRef),
                    "mcpoauth" => new McpOAuthConfig(json.Auth.ClientId, json.Auth.ClientSecretRef, json.Auth.Scopes),
                    _ => new UnknownAuthConfig(json.Auth.Type),
                };

        McpTlsConfig? tls = json.Tls is null
            ? null
            : new McpTlsConfig(json.Tls.CaCertPath, json.Tls.ClientCertPath, json.Tls.ClientKeyPath);

        var connectTimeout = json.ConnectTimeoutSeconds.HasValue
            ? TimeSpan.FromSeconds(json.ConnectTimeoutSeconds.Value)
            : (TimeSpan?)null;
        var requestTimeout = json.RequestTimeoutSeconds.HasValue
            ? TimeSpan.FromSeconds(json.RequestTimeoutSeconds.Value)
            : (TimeSpan?)null;

        return new McpServerDefinition(
            json.Name ?? string.Empty, transport, auth, tls, connectTimeout, requestTimeout);
    }

    internal static McpServerJson MapToJson(McpServerDefinition def)
    {
        var transport = def.Transport.Kind switch
        {
            McpTransportKind.Stdio => "stdio",
            McpTransportKind.Http => "streamableHttp",
            McpTransportKind.Sse => "sse",
            McpTransportKind.HttpAutoDetect => "httpAutoDetect",
            _ => def.Transport.Kind.ToString().ToLowerInvariant(),
        };

        McpAuthJson? auth = def.Auth switch
        {
            NoneAuthConfig => new McpAuthJson(
                "none", null, null, null, null, null, null, null, null, null, null, null, null, null, null),
            BearerAuthConfig b => new McpAuthJson(
                "bearer", b.TokenRef, null, null, null, null, null, null, null, null, null, null, null, null, null),
            ApiKeyAuthConfig ak => new McpAuthJson(
                "apiKey", null, ak.HeaderName, ak.ValueRef, ak.InQueryString, null, null, null, null, null, null, null, null, null, null),
            BasicAuthConfig ba => new McpAuthJson(
                "basic", null, null, null, null, ba.UsernameRef, ba.PasswordRef, null, null, null, null, null, null, null, null),
            OAuth2ClientCredentialsConfig cc => new McpAuthJson(
                "oauth2ClientCredentials", null, null, null, null, null, null, cc.TokenUrl, cc.ClientIdRef, cc.ClientSecretRef, null, null, cc.Scopes, null, null),
            OAuth2DeviceCodeConfig dc => new McpAuthJson(
                "oauth2DeviceCode", null, null, null, null, null, null, dc.TokenUrl, null, null, dc.AuthUrl, dc.ClientId, dc.Scopes, null, null),
            MtlsConfig m => new McpAuthJson(
                "mtls", null, null, null, null, null, null, null, null, null, null, null, null, m.ClientCertRef, m.ClientKeyRef),
            McpOAuthConfig oauth => new McpAuthJson(
                "mcpOAuth", null, null, null, null, null, null, null, null, oauth.ClientSecretRef, null, oauth.ClientId, oauth.Scopes, null, null),
            _ => null,
        };

        McpTlsJson? tls = def.Tls is null
            ? null
            : new McpTlsJson(def.Tls.CaCertPath, def.Tls.ClientCertPath, def.Tls.ClientKeyPath);

        return new McpServerJson(
            def.Name,
            transport,
            def.Transport.Endpoint,
            auth,
            tls,
            def.ConnectTimeout.HasValue ? (int)def.ConnectTimeout.Value.TotalSeconds : null,
            def.RequestTimeout.HasValue ? (int)def.RequestTimeout.Value.TotalSeconds : null);
    }

    private static string ResolveStoragePath(IConfigLoader configLoader)
    {
        var result = configLoader.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        return result.IsOk ? result.Value.StoragePath : HypaConfig.Default.StoragePath;
    }
}
