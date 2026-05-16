using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Infrastructure.Updates;

public sealed class ManualUpdateStrategy : IUpdateStrategy
{
    public string Name => "manual";

    public bool CanHandle(InstallMetadata metadata) => true;

    public Task<Result<UpdatePlan, Error>> PlanAsync(UpdateInfo update, InstallMetadata metadata, CancellationToken ct)
    {
        var plan = new UpdatePlan(
            Strategy: Name,
            CanAutoUpdate: false,
            Summary: "Manual update required",
            Command: null,
            Detail: $"Hypa cannot safely determine ownership of the current binary.\nDownload the latest release from: {update.ReleaseUrl}\nOr re-run the installer: curl -fsSL https://raw.githubusercontent.com/Hypabolic/Hypa/main/install.sh | sh");

        return Task.FromResult(Result<UpdatePlan, Error>.Ok(plan));
    }

    public Task<Result<Unit, Error>> ApplyAsync(UpdateInfo update, InstallMetadata metadata, CancellationToken ct)
    {
        return Task.FromResult(Result<Unit, Error>.Fail(new Error(
            "Update.ManualRequired",
            "Manual update required.")));
    }
}
