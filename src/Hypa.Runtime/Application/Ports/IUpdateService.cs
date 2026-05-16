using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Runtime.Application.Ports;

public interface IUpdateService
{
    Task<UpdateInfo?> GetCachedInfoAsync(CancellationToken ct);
    Task<Result<UpdateInfo, Error>> GetUpdateInfoAsync(bool forceRefresh, CancellationToken ct);
    Task<Result<UpdatePlan, Error>> PlanUpdateAsync(UpdateInfo update, CancellationToken ct);
    Task<Result<Unit, Error>> ApplyUpdateAsync(UpdateInfo update, CancellationToken ct);
}
