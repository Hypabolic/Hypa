using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Cli.Commands.Work;

/// <summary>
/// Open a framed dest-worker session. Dest HOME is not on this type.
/// </summary>
public interface IDestWorkSessionOpener
{
    ValueTask<ConnectivityOutcome<IFramedSession>> OpenAsync(
        string destPlacementId,
        CancellationToken cancellationToken = default);
}
