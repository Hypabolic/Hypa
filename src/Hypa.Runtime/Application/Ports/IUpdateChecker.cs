using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Runtime.Application.Ports;

public interface IUpdateChecker
{
    // Returns null UpdateInfo on HTTP 304 (not modified); caller should use cached data.
    Task<Result<UpdateInfo?, Error>> CheckAsync(
        string currentVersion,
        string runtimeIdentifier,
        string? eTag,
        CancellationToken ct);
}
