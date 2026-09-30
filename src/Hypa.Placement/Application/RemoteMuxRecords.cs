using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

public enum RemoteKeybindingsMode
{
    Local = 0,
    Server = 1,
}

public enum RemoteServerRestartPlan
{
    KeepRunning = 0,
    LiveHandoff = 1,
    StopRequired = 2,
}

public enum RemoteServerRestartReason
{
    None = 0,
    EndpointProtocol = 1,
    SurfaceInterest = 2,
    HealthCheck = 3,
    DaemonDetach = 4,
}

public sealed record RemoteMuxOpenRequest
{
    public PeerProfile? Profile { get; init; }
    public string? ExplicitTarget { get; init; }
    public string Session { get; init; } = "default";
    public bool ManageSshConfig { get; init; } = true;
    public bool LiveHandoff { get; init; }
    public bool LiveHandoffEnabled { get; init; }
    public bool OperatorConsent { get; init; }
    public bool Interactive { get; init; }
    public RemoteKeybindingsMode Keybindings { get; init; } = RemoteKeybindingsMode.Local;

    public static RemoteMuxOpenRequest FromProfile(PeerProfile profile) =>
        new()
        {
            Profile = profile,
            Session = profile.Session,
        };

    public static RemoteMuxOpenRequest FromTarget(string target, string session) =>
        new()
        {
            ExplicitTarget = target,
            Session = session,
        };
}

public sealed record RemoteMuxPath : IAsyncDisposable
{
    public required string LocalSocketPath { get; init; }
    public required string Session { get; init; }
    public required string Target { get; init; }
    public required ulong Generation { get; init; }
    public string? ProfileId { get; init; }
    public bool StartedRemoteServer { get; init; }
    public bool RequestedLiveHandoff { get; init; }
    public Func<ValueTask>? DisposeAsyncAction { get; init; }

    public ValueTask DisposeAsync() =>
        DisposeAsyncAction is null ? ValueTask.CompletedTask : DisposeAsyncAction();
}

public sealed record RemoteMuxOutcome
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public RemoteMuxPath? Path { get; init; }

    public static RemoteMuxOutcome Success(RemoteMuxPath path) =>
        new() { Ok = true, Path = path };

    public static RemoteMuxOutcome Failure(string reason, string detail) =>
        new() { Ok = false, Reason = reason, Detail = detail };
}
