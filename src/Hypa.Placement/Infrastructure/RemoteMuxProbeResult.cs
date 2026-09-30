using Hypa.Placement.Application;

namespace Hypa.Placement.Infrastructure;

internal sealed class RemoteMuxProbeResult
{
    public bool Ok { get; init; }
    public bool ServerRunning { get; init; }
    public RemoteServerRestartReason RestartReason { get; init; }
    public RemoteMuxOutcome? Failure { get; init; }

    public static RemoteMuxProbeResult Fail(RemoteMuxOutcome outcome) =>
        new() { Ok = false, Failure = outcome };
}
