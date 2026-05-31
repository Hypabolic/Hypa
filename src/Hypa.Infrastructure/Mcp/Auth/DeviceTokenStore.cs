using System.Text.Json;

namespace Hypa.Infrastructure.Mcp.Auth;

internal sealed record DeviceTokenStoreJson(
    Dictionary<string, DeviceTokenEntryJson> Tokens);

internal sealed record DeviceTokenEntryJson(
    string AccessToken,
    long ExpiresAtUnixMs);

internal sealed class DeviceTokenStore
{
    private readonly string _filePath;

    public DeviceTokenStore(string storagePath)
    {
        _filePath = Path.Combine(storagePath, "mcp-tokens.json");
    }

    public async Task<Dictionary<string, DeviceTokenEntryJson>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, DeviceTokenEntryJson>();

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var store = await JsonSerializer.DeserializeAsync(
                stream,
                OAuthTokenJsonContext.Default.DeviceTokenStoreJson,
                ct);
            return store?.Tokens ?? new Dictionary<string, DeviceTokenEntryJson>();
        }
        catch
        {
            return new Dictionary<string, DeviceTokenEntryJson>();
        }
    }

    public async Task SaveAsync(Dictionary<string, DeviceTokenEntryJson> tokens, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(
            stream,
            new DeviceTokenStoreJson(tokens),
            OAuthTokenJsonContext.Default.DeviceTokenStoreJson,
            ct);
    }
}
