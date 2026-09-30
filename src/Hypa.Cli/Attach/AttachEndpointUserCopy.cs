namespace Hypa.Cli.Attach;

internal static class AttachEndpointUserCopy
{
    public static string Connecting(string label) =>
        $"Connecting to {label}. Input paused.";

    public static string Incompatible(string label) =>
        $"Placement {label} does not support this attach version.";

    public static string ActivationTimeout =>
        "Connect timed out. Restoring the previous Placement.";

    public static string ActivationFailed =>
        "Connect failed. Previous Placement restored.";

    public static string Unavailable =>
        "No Placement is available. Input paused. Retry Connect or select Local in Cubes.";

    public static string DestLeaseDenied =>
        "Destination pane is already held. Connect does not take the lease.";
}
