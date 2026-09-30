namespace Hypa.Cli.Mux;

/// <summary>
/// Product mux does not present Continuity. The value is a constant.
/// Tests may construct <see cref="FullProfile"/>. Argv and environment
/// do not change it.
/// </summary>
public sealed record MuxReleaseCapability
{
    public bool ContinuityEnabled { get; init; }

    public static MuxReleaseCapability Product { get; } = new() { ContinuityEnabled = false };

    public static MuxReleaseCapability FullProfile { get; } = new() { ContinuityEnabled = true };
}
