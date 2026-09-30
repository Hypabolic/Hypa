using Hypa.Cli.Attach;

namespace Hypa.Cli.Mux;

public sealed record MuxReadyInfo(
    string Session,
    string SocketPath,
    string PingJson,
    IAttachEndpoint? Endpoint = null)
{
    public IAttachEndpoint ResolveEndpoint() =>
        Endpoint ?? new UnixAttachEndpoint(SocketPath);
}

public interface IMuxSupervisor
{
    Task<MuxReadyInfo> EnsureReadyAsync(
        string session,
        string? cwd,
        string? socketOverride,
        CancellationToken ct);
}
