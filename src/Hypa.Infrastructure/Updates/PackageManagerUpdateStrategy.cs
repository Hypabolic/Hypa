using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Infrastructure.Updates;

public sealed class PackageManagerUpdateStrategy : IUpdateStrategy
{
    private static readonly HashSet<string> KnownPackageManagers =
        new(StringComparer.OrdinalIgnoreCase) { "homebrew", "winget", "scoop", "apt", "dnf" };

    public string Name => "package-manager";

    public bool CanHandle(InstallMetadata metadata) =>
        KnownPackageManagers.Contains(metadata.Source);

    public Task<Result<UpdatePlan, Error>> PlanAsync(UpdateInfo update, InstallMetadata metadata, CancellationToken ct)
    {
        var command = GetCommand(metadata.Source);
        var plan = new UpdatePlan(
            Strategy: Name,
            CanAutoUpdate: false,
            Summary: $"Update via {metadata.Source} package manager",
            Command: command,
            Detail: $"Run: {command}");

        return Task.FromResult(Result<UpdatePlan, Error>.Ok(plan));
    }

    public Task<Result<Unit, Error>> ApplyAsync(UpdateInfo update, InstallMetadata metadata, CancellationToken ct)
    {
        return Task.FromResult(Result<Unit, Error>.Fail(new Error(
            "Update.PackageManagerRequired",
            $"This install is managed by {metadata.Source}.")));
    }

    private static string GetCommand(string source) => source.ToLowerInvariant() switch
    {
        "homebrew" => "brew upgrade hypa",
        "winget" => "winget upgrade hypa",
        "scoop" => "scoop update hypa",
        "apt" => "sudo apt update && sudo apt install --only-upgrade hypa",
        "dnf" => "sudo dnf upgrade hypa",
        _ => $"Use your package manager to upgrade hypa",
    };
}
