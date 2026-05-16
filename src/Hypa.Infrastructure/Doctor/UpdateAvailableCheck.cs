using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Infrastructure.Doctor;

public sealed class UpdateAvailableCheck(
    IConfigLoader config,
    IInstallMetadataStore metadataStore,
    IUpdateService updateService) : IDoctorCheck
{
    // 2s keeps worst-case doctor latency acceptable; Update is always registered last
    // so it never delays the checks that precede it.
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(2);

    public string Category => "Update";

    public DoctorCheckResult Run()
    {
        try
        {
            var cfgResult = config.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (cfgResult.IsOk && !cfgResult.Value.UpdateCheckEnabled)
                return new DoctorCheckResult("Update", "update checks disabled", DoctorStatus.Ok);

            var cached = updateService.GetCachedInfoAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            if (cached is not null)
                return BuildResult(cached);

            using var cts = new CancellationTokenSource(CheckTimeout);
            var infoResult = updateService.GetUpdateInfoAsync(forceRefresh: false, cts.Token)
                .GetAwaiter().GetResult();

            if (!infoResult.IsOk)
                return new DoctorCheckResult("Update", "check failed", DoctorStatus.Warn, infoResult.Error.Message);

            var info = infoResult.Value;
            return BuildResult(info);
        }
        catch (OperationCanceledException)
        {
            return new DoctorCheckResult("Update", "check timed out", DoctorStatus.Warn);
        }
        catch (Exception ex)
        {
            return new DoctorCheckResult("Update", "check failed", DoctorStatus.Warn, ex.Message);
        }
    }

    private DoctorCheckResult BuildResult(UpdateInfo info)
    {
        if (!info.IsUpdateAvailable)
            return new DoctorCheckResult("Update", $"up to date (v{info.CurrentVersion})", DoctorStatus.Ok);

        var hint = BuildHint();
        return new DoctorCheckResult(
            "Update",
            $"v{info.LatestVersion} available (current: v{info.CurrentVersion})",
            DoctorStatus.Warn,
            hint);
    }

    private string BuildHint()
    {
        try
        {
            var metadata = metadataStore.GetAsync(CancellationToken.None).GetAwaiter().GetResult();
            var command = metadata.Source.ToLowerInvariant() switch
            {
                "homebrew" => "brew upgrade hypa",
                "winget" => "winget upgrade hypa",
                "scoop" => "scoop update hypa",
                "apt" => "sudo apt update && sudo apt install --only-upgrade hypa",
                "dnf" => "sudo dnf upgrade hypa",
                _ => "hypa update",
            };
            return $"Run: `{command}`";
        }
        catch
        {
            return "Run: `hypa update`";
        }
    }
}
