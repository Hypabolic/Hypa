using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Opens a provider-owned byte path. Rendezvous URL is optional at open time.
/// </summary>
public interface IBytePath
{
    string Provider { get; }

    ValueTask<ConnectivityOutcome<BytePathHandle>> OpenAsync(
        BytePathRequest request,
        CancellationToken cancellationToken = default);

    ValueTask CloseAsync(BytePathHandle handle, CancellationToken cancellationToken = default);
}
