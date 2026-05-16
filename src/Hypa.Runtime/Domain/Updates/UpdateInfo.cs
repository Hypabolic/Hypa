namespace Hypa.Runtime.Domain.Updates;

public sealed record UpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string AssetName,
    string? DownloadUrl,
    string? ChecksumsUrl,
    string RuntimeIdentifier,
    bool IsUpdateAvailable,
    DateTimeOffset CheckedAt,
    string? ETag = null,
    string? Repo = null,
    string? Channel = null
);
