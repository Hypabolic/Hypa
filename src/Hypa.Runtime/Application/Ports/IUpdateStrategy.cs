using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Runtime.Application.Ports;

public interface IUpdateStrategy
{
    string Name { get; }
    bool CanHandle(InstallMetadata metadata);
    Task<Result<UpdatePlan, Error>> PlanAsync(UpdateInfo update, InstallMetadata metadata, CancellationToken ct);
    Task<Result<Unit, Error>> ApplyAsync(UpdateInfo update, InstallMetadata metadata, CancellationToken ct);
}
