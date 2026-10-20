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

    /// <summary>
    /// Optional. The client puts a <c>seq</c> on each <c>pane.send_keys</c>.
    /// The mux puts <c>input_ack</c> on each live cells body for that client,
    /// and sends an ack-only cells body when a write ends after a body that
    /// went out without its ack. Version 2 adds the ack-only body: a client
    /// that knows only version 1 does not see this name, so it sends no
    /// <c>seq</c> and never gets an ack-only body.
    /// </summary>
    public const string InputAckCapability = "hypa.attach.input_ack.v2";

    /// <summary>
    /// Optional. The client lists it in the hello and the mux lists it in the
    /// welcome. When both peers list it, the client sends one
    /// <c>render.ack</c> after each host paint, and the mux limits the render
    /// bytes that the client did not ack yet on that connection.
    /// </summary>
    public const string RenderCreditCapability = "hypa.attach.render_credit.v1";

    /// <summary>
    /// Optional. The client lists it in the hello and the mux lists it in the
    /// welcome. The mux then puts the termios ECHO and ICANON flags of the
    /// pane PTY, <c>echo</c> and <c>canonical</c>, on each live cells body
    /// for that client when the mode is known. A client that does not list
    /// it gets neither field.
    /// </summary>
    public const string EchoModeCapability = "hypa.attach.echo_mode.v1";

    /// <summary>
    /// Optional. The client lists it in the hello and the mux lists it in the
    /// welcome. The mux can then put a signed row count, <c>shift</c>, on a
    /// live cells delta for that client. The client moves the rows of its
    /// kept pane grid by that count before it writes the rows of the delta.
    /// A client that does not list it gets no shift.
    /// </summary>
    public const string RowShiftCapability = "hypa.attach.row_shift.v1";

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
