namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// Hypa-native attach endpoint compatibility constants.
/// independent from the private binary protocol at
/// <c>src/protocol/wire.rs:19-20</c>.
// / Hypa uses these names.
/// </summary>
public static class ProtocolAttachEndpoint
{
    /// <summary>
    /// Stable attach endpoint generation. Independent of
    /// <see cref="ProtocolVersion.Major"/> / <see cref="ProtocolVersion.Minor"/>
    /// and of connection generation.
    /// </summary>
    public const uint EndpointGeneration = 1;

    public const string HelloMethod = "hypa.attach.hello.v1";
    public const string ResizeMethod = "hypa.attach.resize.v1";
    public const string SurfaceInterestMethod = "hypa.attach.surface_interest.v1";
    public const string FocusMethod = "hypa.attach.focus.v1";
    public const string HealthMethod = "hypa.attach.health.v1";

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

    public const string IncompatibleCode = "endpoint_incompatible";
    public const string IncompatibleMessage = "endpoint compatibility rejected";
    public const string UnknownDisplay = "unknown";

    public const string IdentityRecord = "AttachEndpointIdentity";
    public const string GeometryRecord = "AttachGeometry";
    public const string HelloRecord = "AttachEndpointHello";
    public const string WelcomeRecord = "AttachEndpointWelcome";
    public const string ErrorRecord = "AttachEndpointError";
    public const string LeaseRecord = "AttachEndpointLease";
    public const string SurfaceInterestRequestRecord = "AttachSurfaceInterestRequest";
    public const string SurfaceInterestResultRecord = "AttachSurfaceInterestResult";
    public const string ResizeRequestRecord = "AttachResizeRequest";
    public const string FocusRequestRecord = "AttachFocusRequest";
    public const string ControlResultRecord = "AttachControlResult";
    public const string HealthRequestRecord = "AttachHealthRequest";
    public const string HealthResultRecord = "AttachHealthResult";
    public const string PresentationSyncRecord = "AttachPresentationSync";
    public const string PresentationReadyRecord = "AttachPresentationReady";
    public const string ProjectionSnapshotRecord = "AttachProjectionSnapshot";
    public const string ActivationEvidenceRecord = "AttachActivationEvidence";

    public static IReadOnlyList<string> Methods { get; } =
    [
        HelloMethod,
        ResizeMethod,
        SurfaceInterestMethod,
        FocusMethod,
        HealthMethod,
    ];

    public static IReadOnlyList<string> Events { get; } =
    [
        WelcomeEvent,
        ProjectionSnapshotEvent,
        PresentationSyncEvent,
        PresentationReadyEvent,
    ];

    public static IReadOnlyList<string> Codecs { get; } =
    [
        SnapshotCodec,
        SurfaceCodec,
        InputCodec,
    ];

    public static IReadOnlyList<string> RequiredCapabilities { get; } =
    [
        SurfaceInterestCapability,
        PresentationFenceCapability,
        HealthCapability,
    ];

    public static IReadOnlyList<string> OptionalCapabilities { get; } =
    [
        DetachedSessionCapability,
        SurfacePatchCapability,
    ];

    public static IReadOnlyList<string> Records { get; } =
    [
        IdentityRecord,
        GeometryRecord,
        HelloRecord,
        WelcomeRecord,
        ErrorRecord,
        LeaseRecord,
        SurfaceInterestRequestRecord,
        SurfaceInterestResultRecord,
        ResizeRequestRecord,
        FocusRequestRecord,
        ControlResultRecord,
        HealthRequestRecord,
        HealthResultRecord,
        PresentationSyncRecord,
        PresentationReadyRecord,
        ProjectionSnapshotRecord,
        ActivationEvidenceRecord,
    ];

    public static IReadOnlyList<string> KnownCapabilities { get; } =
    [
        SurfaceInterestCapability,
        PresentationFenceCapability,
        HealthCapability,
        DetachedSessionCapability,
        SurfacePatchCapability,
    ];

    public static IReadOnlyList<string> KnownDisplays { get; } =
    [
        "dark",
        "light",
    ];

    /// <summary>Map an unknown display enum to <see cref="UnknownDisplay"/>.</summary>
    public static string DisplayOrUnknown(string? value, IReadOnlyList<string>? known = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return UnknownDisplay;
        if (known is null || known.Count == 0)
            return value;
        return known.Contains(value, StringComparer.Ordinal) ? value : UnknownDisplay;
    }

    /// <summary>True when a connection generation is used as endpoint generation.</summary>
    public static bool ConnectionGenerationSubstitutesEndpoint(
        uint endpointGeneration,
        ulong connectionGeneration) =>
        endpointGeneration != EndpointGeneration
        && endpointGeneration == connectionGeneration;
}
