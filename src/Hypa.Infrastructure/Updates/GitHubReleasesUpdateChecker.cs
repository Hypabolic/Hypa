using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Infrastructure.Updates;

public sealed class GitHubReleasesUpdateChecker(IHttpClientFactory httpClientFactory, IConfigLoader config) : IUpdateChecker
{
    public async Task<Result<UpdateInfo?, Error>> CheckAsync(
        string currentVersion,
        string runtimeIdentifier,
        string? eTag,
        CancellationToken ct)
    {
        var configResult = await config.LoadAsync(ct);
        var repo = configResult.IsOk ? configResult.Value.ReleaseRepository : "Hypabolic/Hypa";
        var channel = configResult.IsOk ? configResult.Value.UpdateChannel : "stable";

        var url = $"https://api.github.com/repos/{repo}/releases/latest";
        var client = httpClientFactory.CreateClient("hypa-update");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (eTag is not null)
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{eTag}\"", isWeak: false));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result<UpdateInfo?, Error>.Fail(new Error("UpdateCheck.NetworkError", ex.Message));
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified)
                return Result<UpdateInfo?, Error>.Ok(null);

            if (!response.IsSuccessStatusCode)
                return Result<UpdateInfo?, Error>.Fail(new Error(
                    "UpdateCheck.HttpError",
                    $"GitHub returned {(int)response.StatusCode}."));

            GitHubRelease? release;
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(ct);
                release = await JsonSerializer.DeserializeAsync(
                    stream,
                    UpdatesJsonContext.Default.GitHubRelease,
                    ct);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return Result<UpdateInfo?, Error>.Fail(new Error("UpdateCheck.ParseError", ex.Message));
            }

            if (release is null)
                return Result<UpdateInfo?, Error>.Fail(new Error("UpdateCheck.ParseError", "Empty release response."));

            var assetName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? $"hypa-{runtimeIdentifier}.zip"
                : $"hypa-{runtimeIdentifier}.tar.gz";

            var asset = release.Assets.FirstOrDefault(a => a.Name == assetName);
            var checksums = release.Assets.FirstOrDefault(a => a.Name == "SHA256SUMS");
            var latestVersion = release.TagName.TrimStart('v');

            var responseETag = response.Headers.ETag?.Tag?.Trim('"');

            return Result<UpdateInfo?, Error>.Ok(new UpdateInfo(
                CurrentVersion: currentVersion,
                LatestVersion: latestVersion,
                ReleaseUrl: release.HtmlUrl,
                AssetName: assetName,
                DownloadUrl: asset?.BrowserDownloadUrl,
                ChecksumsUrl: checksums?.BrowserDownloadUrl,
                RuntimeIdentifier: runtimeIdentifier,
                IsUpdateAvailable: IsNewer(latestVersion, currentVersion),
                CheckedAt: DateTimeOffset.UtcNow,
                ETag: responseETag,
                Repo: repo,
                Channel: channel));
        }
    }

    private static bool IsNewer(string latest, string current) =>
        SemanticVersion.TryParse(latest, out var l) &&
        SemanticVersion.TryParse(current, out var c) &&
        l > c;
}
