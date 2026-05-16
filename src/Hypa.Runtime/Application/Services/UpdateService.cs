using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Runtime.Application.Services;

public sealed class UpdateService(
    IUpdateChecker checker,
    IUpdateCheckCache cache,
    IInstallMetadataStore metadataStore,
    IEnumerable<IUpdateStrategy> strategies,
    IVersionProvider version,
    IRuntimeIdentifierProvider rid,
    IClock clock,
    IConfigLoader config) : IUpdateService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public async Task<UpdateInfo?> GetCachedInfoAsync(CancellationToken ct)
    {
        var configResult = await config.LoadAsync(ct);
        if (!configResult.IsOk || !configResult.Value.UpdateCheckEnabled)
            return null;

        var cached = await GetMatchingCachedInfoAsync(configResult.Value, ct);
        return cached is not null && IsCacheFresh(cached)
            ? cached
            : null;
    }

    public async Task<Result<UpdateInfo, Error>> GetUpdateInfoAsync(bool forceRefresh, CancellationToken ct)
    {
        var configResult = await config.LoadAsync(ct);
        if (!configResult.IsOk)
            return Result<UpdateInfo, Error>.Fail(configResult.Error);

        var cfg = configResult.Value;

        if (!cfg.UpdateCheckEnabled)
        {
            return Result<UpdateInfo, Error>.Ok(new UpdateInfo(
                CurrentVersion: version.CurrentVersion,
                LatestVersion: version.CurrentVersion,
                ReleaseUrl: string.Empty,
                AssetName: string.Empty,
                DownloadUrl: null,
                ChecksumsUrl: null,
                RuntimeIdentifier: rid.RuntimeIdentifier,
                IsUpdateAvailable: false,
                CheckedAt: clock.UtcNow,
                Repo: cfg.ReleaseRepository,
                Channel: cfg.UpdateChannel));
        }

        var cached = await GetMatchingCachedInfoAsync(cfg, ct);

        if (!forceRefresh && cached is not null && IsCacheFresh(cached))
            return Result<UpdateInfo, Error>.Ok(cached);

        // Only pass an ETag when the cache entry's keys match current config.
        // Sending a stale ETag for a different version/repo/channel could yield a 304
        // we have no valid entry to satisfy.
        var checkResult = await checker.CheckAsync(
            version.CurrentVersion,
            rid.RuntimeIdentifier,
            cached?.ETag,
            ct);

        if (!checkResult.IsOk)
        {
            if (cached is not null)
                return Result<UpdateInfo, Error>.Ok(cached);
            return Result<UpdateInfo, Error>.Fail(checkResult.Error);
        }

        // null means HTTP 304 — only accept when the cached entry's keys still match.
        if (checkResult.Value is null)
        {
            if (cached is not null)
            {
                var refreshed = cached with { CheckedAt = clock.UtcNow };
                await cache.SaveAsync(refreshed, ct);
                return Result<UpdateInfo, Error>.Ok(refreshed);
            }
            return Result<UpdateInfo, Error>.Fail(new Error("UpdateCheck.UnexpectedNotModified",
                "Received 304 Not Modified but no matching cache entry is available."));
        }

        var info = checkResult.Value;
        await cache.SaveAsync(info, ct);
        return Result<UpdateInfo, Error>.Ok(info);
    }

    public async Task<Result<UpdatePlan, Error>> PlanUpdateAsync(UpdateInfo update, CancellationToken ct)
    {
        var metadata = await metadataStore.GetAsync(ct);
        var strategy = SelectStrategy(metadata);
        return await strategy.PlanAsync(update, metadata, ct);
    }

    public async Task<Result<Unit, Error>> ApplyUpdateAsync(UpdateInfo update, CancellationToken ct)
    {
        var metadata = await metadataStore.GetAsync(ct);
        var strategy = SelectStrategy(metadata);
        return await strategy.ApplyAsync(update, metadata, ct);
    }

    private IUpdateStrategy SelectStrategy(InstallMetadata metadata) =>
        strategies.First(s => s.CanHandle(metadata));

    private async Task<UpdateInfo?> GetMatchingCachedInfoAsync(HypaConfig cfg, CancellationToken ct)
    {
        var cached = await cache.GetAsync(ct);
        return cached is not null && CacheKeysMatch(cached, cfg.ReleaseRepository, cfg.UpdateChannel)
            ? cached
            : null;
    }

    // Strict key match used both for freshness and fallback on network failure.
    // Null repo/channel (old cache entries) are treated as a mismatch to force a refresh,
    // which writes a cache entry with the current keys on the next successful check.
    private bool CacheKeysMatch(UpdateInfo cached, string repo, string channel)
    {
        if (cached.CurrentVersion != version.CurrentVersion) return false;
        if (cached.RuntimeIdentifier != rid.RuntimeIdentifier) return false;
        if (cached.Repo != repo) return false;
        if (cached.Channel != channel) return false;
        return true;
    }

    private bool IsCacheFresh(UpdateInfo cached) =>
        clock.UtcNow - cached.CheckedAt < CacheTtl;
}
