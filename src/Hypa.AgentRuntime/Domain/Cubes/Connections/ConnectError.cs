namespace Hypa.AgentRuntime.Domain.Cubes.Connections;

/// <summary>
/// Why a Cubes Connect did not produce an endpoint. Reason is a stable
/// snake_case code. Detail is one short line for the log.
/// </summary>
public sealed record ConnectError(string Reason, string? Detail = null);

/// <summary>
/// Resolver failure codes. Connectivity codes pass through unchanged.
/// </summary>
public static class CubesConnectErrorReasons
{
    public const string LocalDestination = "local_destination";
    public const string Unreachable = "unreachable";
    public const string ConnectDisabled = "connect_disabled";
    public const string PlacementIdInvalid = "placement_id_invalid";
    public const string OperatorIdentityUnavailable = "operator_identity_unavailable";
    public const string DirectoryUnreadable = "directory_unreadable";
    public const string PlacementNotFound = "placement_not_found";
    public const string PlacementDisabled = "placement_disabled";
    public const string PlacementChanged = "placement_changed";
    public const string JoinMaterialUnavailable = "join_material_unavailable";
    public const string DirectMaterialUnavailable = "direct_material_unavailable";
    public const string DialFailed = "dial_failed";
    public const string SshOpenFailed = "ssh_open_failed";
    public const string ResolveTimedOut = "resolve_timed_out";
    public const string NoAttachEndpoint = "no_attach_endpoint";
}
