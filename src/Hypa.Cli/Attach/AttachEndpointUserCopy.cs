using Hypa.Cli.Mux;
using Hypa.Connectivity.Domain;
using Hypa.AgentRuntime.Domain.Cubes.Connections;

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

    /// <summary>
    /// The connect failure line. The code is always in brackets so a report
    /// names the cause.
    /// </summary>
    public static string ConnectFailed(string cubeName, ConnectError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var code = error.Reason;
        var text = code switch
        {
            ConnectivityReasons.JoinDenied => $"{cubeName} refused the connection (join denied)",
            ConnectivityReasons.Unauthorized => $"{cubeName} did not accept this device (unauthorized)",
            ConnectivityReasons.PeerUnavailable => $"{cubeName} is not reachable (peer unavailable)",
            CubesConnectErrorReasons.PlacementChanged => $"{cubeName} changed while connecting. Try again.",
            CubesConnectErrorReasons.Unreachable => $"{cubeName} is not reachable (unreachable)",
            _ => $"{cubeName} connect failed",
        };
        // The detail shows only where it tells the user what to do next.
        if (!string.IsNullOrWhiteSpace(error.Detail)
            && (code == CubesConnectErrorReasons.DirectMaterialUnavailable
                || (code == ConnectivityReasons.JoinDenied
                    && error.Detail.Contains("add-quic --device", StringComparison.Ordinal))))
        {
            text = $"{text}. {error.Detail}";
        }

        return $"{text} [{code}]";
    }
}
