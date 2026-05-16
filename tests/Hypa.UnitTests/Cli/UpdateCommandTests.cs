using Hypa.Cli.Commands;
using Hypa.Runtime.Domain.Updates;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class UpdateCommandTests
{
    [Fact]
    public void WriteFallbackGuidance_PlanFailure_PrintsReleaseAndInstallerFallback()
    {
        var writer = new StringWriter();
        var info = MakeUpdate();

        UpdateCommand.WriteFallbackGuidance(writer, info);

        var output = writer.ToString();
        Assert.Contains("Download manually: https://example.com/releases/v0.2.0", output);
        Assert.Contains("Or re-run the installer:", output);
    }

    [Fact]
    public void WriteFallbackGuidance_ApplyFailureForPackageManager_PrintsSelectedCommand()
    {
        var writer = new StringWriter();
        var info = MakeUpdate();
        var plan = new UpdatePlan(
            Strategy: "package-manager",
            CanAutoUpdate: false,
            Summary: "Update via homebrew package manager",
            Command: "brew upgrade hypa",
            Detail: "Run: brew upgrade hypa");

        UpdateCommand.WriteFallbackGuidance(writer, info, plan);

        var output = writer.ToString();
        Assert.Contains("Download manually: https://example.com/releases/v0.2.0", output);
        Assert.Contains("Run: brew upgrade hypa", output);
    }

    private static UpdateInfo MakeUpdate() =>
        new(CurrentVersion: "0.1.0",
            LatestVersion: "0.2.0",
            ReleaseUrl: "https://example.com/releases/v0.2.0",
            AssetName: "hypa-linux-x64.tar.gz",
            DownloadUrl: "https://example.com/hypa-linux-x64.tar.gz",
            ChecksumsUrl: "https://example.com/SHA256SUMS",
            RuntimeIdentifier: "linux-x64",
            IsUpdateAvailable: true,
            CheckedAt: DateTimeOffset.UtcNow);
}
