using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Hypa.Infrastructure.Mcp.Secrets;
using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Mcp;
using Microsoft.Extensions.Logging;

namespace Hypa.Infrastructure.Mcp.Auth;

internal sealed class OAuthTokenService : IOAuthTokenService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretResolver _secretResolver;
    private readonly OAuthTokenCache _cache;
    private readonly DeviceTokenStore _deviceTokenStore;
    private readonly ILogger<OAuthTokenService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public OAuthTokenService(
        IHttpClientFactory httpClientFactory,
        ISecretResolver secretResolver,
        OAuthTokenCache cache,
        HypaDataOptions dataOptions,
        ILogger<OAuthTokenService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secretResolver = secretResolver;
        _cache = cache;
        _deviceTokenStore = new DeviceTokenStore(dataOptions.DataDirectory);
        _logger = logger;
    }

    public async Task<string> GetClientCredentialsTokenAsync(
        OAuth2ClientCredentialsConfig config,
        CancellationToken ct)
    {
        var key = $"{config.ClientIdRef}@{config.TokenUrl}";

        var cached = _cache.TryGet(key);
        if (cached is not null)
            return cached;

        var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            cached = _cache.TryGet(key);
            if (cached is not null)
                return cached;

            var clientId = await _secretResolver.ResolveAsync(config.ClientIdRef, ct) ?? string.Empty;
            var clientSecret = await _secretResolver.ResolveAsync(config.ClientSecretRef, ct) ?? string.Empty;

            var formFields = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "client_credentials"),
                new("client_id", clientId),
                new("client_secret", clientSecret),
            };

            if (config.Scopes is { Length: > 0 })
                formFields.Add(new("scope", string.Join(' ', config.Scopes)));

            var http = _httpClientFactory.CreateClient("mcp-oauth");
            using var response = await http.PostAsync(
                config.TokenUrl,
                new FormUrlEncodedContent(formFields),
                ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var tokenResponse = await JsonSerializer.DeserializeAsync(
                stream,
                OAuthTokenJsonContext.Default.OAuthTokenResponse,
                ct);

            var token = tokenResponse?.AccessToken
                ?? throw new InvalidOperationException("OAuth token response missing access_token");

            _cache.Set(key, token, tokenResponse.ExpiresIn);
            return token;
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<string?> GetDeviceCodeTokenAsync(
        OAuth2DeviceCodeConfig config,
        CancellationToken ct)
    {
        var key = $"device:{config.ClientId}@{config.TokenUrl}";

        var cached = _cache.TryGet(key);
        if (cached is not null)
            return cached;

        var stored = await _deviceTokenStore.LoadAsync(ct);
        if (stored.TryGetValue(key, out var entry))
        {
            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(entry.ExpiresAtUnixMs);
            if (expiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
            {
                var remaining = (int)(expiresAt - DateTimeOffset.UtcNow).TotalSeconds;
                _cache.Set(key, entry.AccessToken, remaining);
                return entry.AccessToken;
            }
        }

        return null;
    }

    public async Task InitiateDeviceCodeFlowAsync(
        OAuth2DeviceCodeConfig config,
        CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient("mcp-oauth");

        var deviceFormFields = new List<KeyValuePair<string, string>>
        {
            new("client_id", config.ClientId),
        };

        if (config.Scopes is { Length: > 0 })
            deviceFormFields.Add(new("scope", string.Join(' ', config.Scopes)));

        using var deviceResponse = await http.PostAsync(
            config.AuthUrl,
            new FormUrlEncodedContent(deviceFormFields),
            ct);
        deviceResponse.EnsureSuccessStatusCode();

        await using var deviceStream = await deviceResponse.Content.ReadAsStreamAsync(ct);
        var deviceCode = await JsonSerializer.DeserializeAsync(
            deviceStream,
            OAuthTokenJsonContext.Default.OAuthDeviceCodeResponse,
            ct);

        if (deviceCode is null)
            throw new InvalidOperationException("Invalid device code response");

        await Console.Error.WriteLineAsync($"Open {deviceCode.VerificationUri} and enter code: {deviceCode.UserCode}");

        var pollInterval = deviceCode.Interval ?? 5;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresIn);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(pollInterval), ct);

            var pollFields = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                new("device_code", deviceCode.DeviceCode),
                new("client_id", config.ClientId),
            };

            using var pollResponse = await http.PostAsync(
                config.TokenUrl,
                new FormUrlEncodedContent(pollFields),
                ct);

            if (!pollResponse.IsSuccessStatusCode)
                continue;

            await using var pollStream = await pollResponse.Content.ReadAsStreamAsync(ct);
            var tokenResponse = await JsonSerializer.DeserializeAsync(
                pollStream,
                OAuthTokenJsonContext.Default.OAuthTokenResponse,
                ct);

            if (tokenResponse?.AccessToken is null)
                continue;

            var key = $"device:{config.ClientId}@{config.TokenUrl}";
            _cache.Set(key, tokenResponse.AccessToken, tokenResponse.ExpiresIn);

            var expiresAt = tokenResponse.ExpiresIn.HasValue
                ? DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn.Value)
                : DateTimeOffset.UtcNow.AddHours(1);

            var stored = await _deviceTokenStore.LoadAsync(ct);
            stored[key] = new DeviceTokenEntryJson(tokenResponse.AccessToken, expiresAt.ToUnixTimeMilliseconds());
            await _deviceTokenStore.SaveAsync(stored, ct);

            _logger.LogInformation("Device code authentication succeeded for client {ClientId}", config.ClientId);
            return;
        }

        throw new InvalidOperationException("Device code flow timed out");
    }
}
