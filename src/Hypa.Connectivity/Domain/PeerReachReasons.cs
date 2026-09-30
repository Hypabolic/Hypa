namespace Hypa.Connectivity.Domain;

/// <summary>Stable peer-reach reasons. Wire as lowercase snake strings.</summary>
public static class PeerReachReasons
{
    public const string ProfileInvalid = "profile_invalid";
    public const string ProfileDisabled = "profile_disabled";
    public const string TargetInvalid = "target_invalid";
    public const string SessionInvalid = "session_invalid";
    public const string CredentialsForbidden = "credentials_forbidden";
    public const string WindowsRemoteRefused = "windows_remote_refused";
    public const string StaleGeneration = "stale_generation";
    public const string IncompatibleEndpoint = "incompatible_endpoint";
    public const string ConsentRequired = "consent_required";
    public const string LiveHandoffUnsupported = "live_handoff_unsupported";
    public const string ProviderUnsupported = "provider_unsupported";
}
