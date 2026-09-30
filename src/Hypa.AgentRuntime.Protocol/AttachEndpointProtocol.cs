namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// Frozen attach-endpoint contract. Independent of private protocol major/minor.
/// </summary>
public static class AttachEndpointProtocol
{
    public const uint EndpointGeneration = 1;

    public const string Hello = "hypa.attach.hello.v1";
    public const string Resize = "hypa.attach.resize.v1";
    public const string SurfaceInterest = "hypa.attach.surface_interest.v1";
    public const string Focus = "hypa.attach.focus.v1";
    public const string Health = "hypa.attach.health.v1";

    public const string WelcomeEvent = "hypa.attach.welcome.v1";
    public const string ProjectionSnapshotEvent = "hypa.attach.projection_snapshot.v1";
    public const string PresentationSyncEvent = "hypa.attach.presentation_sync.v1";
    public const string PresentationReadyEvent = "hypa.attach.presentation_ready.v1";

    public const string SnapshotCodec = "hypa.attach.snapshot.v1";
    public const string SurfaceCodec = "hypa.attach.surface.v1";
    public const string InputCodec = "hypa.attach.input.v1";

    public const string SurfaceInterestCapability = "hypa.attach.surface_interest.v1";
    public const string PresentationFenceCapability = "hypa.attach.presentation_fence.v1";
    public const string HealthCapability = "hypa.attach.health.v1";
    public const string DetachedSessionCapability = "hypa.attach.detached_session.v1";
    public const string SurfacePatchCapability = "hypa.attach.surface_patch.v1";

    public static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(10);

    public static IReadOnlyList<string> Methods { get; } =
    [
        Hello,
        Resize,
        SurfaceInterest,
        Focus,
        Health,
    ];

    public static IReadOnlyList<string> Events { get; } =
    [
        WelcomeEvent,
        ProjectionSnapshotEvent,
        PresentationSyncEvent,
        PresentationReadyEvent,
    ];

    public static IReadOnlyList<string> RequiredCapabilities { get; } =
    [
        SurfaceInterestCapability,
        PresentationFenceCapability,
        HealthCapability,
    ];

    public static IReadOnlyList<string> FrozenCodecs { get; } =
    [
        SnapshotCodec,
        SurfaceCodec,
        InputCodec,
    ];
}

public static class AttachEndpointErrorCodes
{
    public const string Incompatible = "endpoint_incompatible";
    public const string ActivationTimeout = "endpoint_activation_timeout";
    public const string ActivationFailed = "endpoint_activation_failed";
    public const string Unavailable = "endpoint_unavailable";
    public const string SurfaceInactive = "surface_inactive";
    public const string StaleSurface = "stale_surface";
}
