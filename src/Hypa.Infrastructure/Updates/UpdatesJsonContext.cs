using System.Text.Json.Serialization;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Infrastructure.Updates;

[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(CachedUpdateInfo))]
[JsonSerializable(typeof(InstallMetadata))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
internal sealed partial class UpdatesJsonContext : JsonSerializerContext { }

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets
);

internal sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl
);

internal sealed record CachedUpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string AssetName,
    string? DownloadUrl,
    string? ChecksumsUrl,
    string RuntimeIdentifier,
    bool IsUpdateAvailable,
    DateTimeOffset CheckedAt,
    string? ETag,
    string? Repo,
    string? Channel
);
