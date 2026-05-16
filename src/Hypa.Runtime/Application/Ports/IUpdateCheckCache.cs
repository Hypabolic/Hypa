using Hypa.Runtime.Domain.Updates;

namespace Hypa.Runtime.Application.Ports;

public interface IUpdateCheckCache
{
    Task<UpdateInfo?> GetAsync(CancellationToken ct);
    Task SaveAsync(UpdateInfo info, CancellationToken ct);
}
