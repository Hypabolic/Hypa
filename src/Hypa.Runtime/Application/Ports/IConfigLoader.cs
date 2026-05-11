using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;

namespace Hypa.Runtime.Application.Ports;

public interface IConfigLoader
{
    Task<Result<HypaConfig, Error>> LoadAsync(CancellationToken ct);
}
