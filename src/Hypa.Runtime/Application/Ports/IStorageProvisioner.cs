using Hypa.Runtime.Domain.Common;

namespace Hypa.Runtime.Application.Ports;

public interface IStorageProvisioner
{
    Task<Result<Unit, Error>> ProvisionAsync(CancellationToken ct);
}
