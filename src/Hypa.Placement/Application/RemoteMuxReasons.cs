namespace Hypa.Placement.Application;

/// <summary>Stable remote mux open failures. Wire as lowercase snake strings.</summary>
public static class RemoteMuxReasons
{
    public const string TargetInvalid = "remote_target_invalid";
    public const string ProfileInvalid = "placement_profile_invalid";
    public const string ProfileDisabled = "remote_profile_disabled";
    public const string ProviderUnsupported = "remote_provider_unsupported";
    public const string PlatformUnsupported = "remote_platform_unsupported";
    public const string HandoffUnsupported = "remote_handoff_unsupported";
    public const string RestartRequired = "ssh_restart_required";
    public const string HandoffRequired = "remote_handoff_required";
    public const string Incompatible = "remote_incompatible";
    public const string AuthenticationFailed = "ssh_authentication_failed";
    public const string ApprovalRequired = "ssh_approval_required";
    public const string GenerationStale = "remote_generation_stale";
    public const string Internal = "internal";
}
