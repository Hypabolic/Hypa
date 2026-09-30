namespace Hypa.Placement.Application;

/// <summary>
/// Remote server replacement plan.
/// enablement and a request. <c>src/remote/attach.rs:1349-1405</c> requires
/// consent before a destructive restart.
/// </summary>
public static class RemoteRestartPolicy
{
    public static RemoteServerRestartPlan Decide(
        RemoteServerRestartReason reason,
        bool liveHandoff,
        bool liveHandoffEnabled)
    {
        if (reason == RemoteServerRestartReason.None)
            return RemoteServerRestartPlan.KeepRunning;

        if (liveHandoffEnabled && liveHandoff)
            return RemoteServerRestartPlan.LiveHandoff;

        return RemoteServerRestartPlan.StopRequired;
    }

    public static bool RequiresConsent(RemoteServerRestartPlan plan) =>
        plan is RemoteServerRestartPlan.StopRequired or RemoteServerRestartPlan.LiveHandoff;

    public static bool LiveHandoffAllowed(bool unix, bool requested) =>
        unix && requested;

    public static string ConsentCopy() =>
        "Remote restart requires approval. Active remote processes can stop.";
}
