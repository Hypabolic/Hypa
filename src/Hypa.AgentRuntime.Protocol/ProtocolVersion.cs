namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// Protocol ABI version for hypa-runtime control plane.
/// Package SemVer (<c>0.1.0</c>) is independent of protocol major.
/// Bump <see cref="Major"/> only on incompatible wire changes.
/// Clients check major on connect; refuse when major != 1 (P0 pin).
/// </summary>
/// <remarks>
/// Wire kind for <c>protocol_version</c> / <c>ping.protocol</c> is JSON <b>number</b>
/// major (<see cref="Current"/>). Typed DTOs match live ControlPlane so consumers
/// can deserialize <c>session.snapshot</c> without a special path.
/// <see cref="Wire"/> remains a human/docs display string ("1.1") only.
/// Matrix: <see cref="FixtureCatalog.ProtocolVersionMatrix"/>.
/// </remarks>
public static class ProtocolVersion
{
    /// <summary>Wire protocol major. P0 pin is 1.</summary>
    public const int Major = 1;

    /// <summary>Wire protocol minor. Additive fields may bump minor.</summary>
    public const int Minor = 1;

    /// <summary>Stable protocol family name advertised on the wire.</summary>
    public const string Name = "hypa-runtime";

    /// <summary>
    /// Human/docs display form ("1.1"). Not the wire JSON kind for protocol_version.
    /// </summary>
    public const string Wire = "1.1";

    /// <summary>
    /// Wire integer for ping / session.snapshot / SessionState.
    /// Equals <see cref="Major"/>. JSON type is number.
    /// </summary>
    public const int Current = Major;
}
