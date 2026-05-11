using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;

namespace Hypa.Runtime.Application.Services;

public sealed class ConfigService(IConfigLoader loader)
{
    public Task<Result<HypaConfig, Error>> GetConfigAsync(CancellationToken ct) =>
        loader.LoadAsync(ct);
}
