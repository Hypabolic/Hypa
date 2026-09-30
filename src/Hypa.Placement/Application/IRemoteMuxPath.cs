namespace Hypa.Placement.Application;

/// <summary>
/// Open a remote mux path from a peer profile or an explicit SSH target.
/// The path is a local Unix socket. Existing attach uses
/// <c>IAttachEndpoint</c> and <c>ControlPlaneClient</c> against that socket.
/// OpenSSH is the only adapter in this slice.
/// </summary>
public interface IRemoteMuxPath
{
    ValueTask<RemoteMuxOutcome> OpenAsync(
        RemoteMuxOpenRequest request,
        CancellationToken cancellationToken = default);
}
