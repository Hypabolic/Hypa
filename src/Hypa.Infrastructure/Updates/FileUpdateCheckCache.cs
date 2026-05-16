using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Infrastructure.Updates;

public sealed class FileUpdateCheckCache(IConfigLoader config) : IUpdateCheckCache
{
    public async Task<UpdateInfo?> GetAsync(CancellationToken ct)
    {
        try
        {
            var path = await GetCachePathAsync(ct);
            if (!File.Exists(path))
                return null;

            await using var stream = File.OpenRead(path);
            var cached = await JsonSerializer.DeserializeAsync(
                stream, UpdatesJsonContext.Default.CachedUpdateInfo, ct);

            return cached is null ? null : ToUpdateInfo(cached);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(UpdateInfo info, CancellationToken ct)
    {
        try
        {
            var path = await GetCachePathAsync(ct);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(
                stream, ToCached(info), UpdatesJsonContext.Default.CachedUpdateInfo, ct);
        }
        catch
        {
            // Cache failures are silently swallowed
        }
    }

    private async Task<string> GetCachePathAsync(CancellationToken ct)
    {
        try
        {
            var configResult = await config.LoadAsync(ct);
            if (configResult.IsOk)
                return Path.Combine(configResult.Value.StoragePath, "update-check.json");
        }
        catch { }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".hypa", "update-check.json");
    }

    private static UpdateInfo ToUpdateInfo(CachedUpdateInfo c) =>
        new(c.CurrentVersion, c.LatestVersion, c.ReleaseUrl, c.AssetName,
            c.DownloadUrl, c.ChecksumsUrl, c.RuntimeIdentifier,
            c.IsUpdateAvailable, c.CheckedAt, c.ETag, c.Repo, c.Channel);

    private static CachedUpdateInfo ToCached(UpdateInfo i) =>
        new(i.CurrentVersion, i.LatestVersion, i.ReleaseUrl, i.AssetName,
            i.DownloadUrl, i.ChecksumsUrl, i.RuntimeIdentifier,
            i.IsUpdateAvailable, i.CheckedAt, i.ETag, i.Repo, i.Channel);
}
