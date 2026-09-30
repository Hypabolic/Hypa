namespace Hypa.Connectivity.Domain;

/// <summary>First QUIC path policy. 0-RTT stays off until a later adapter slice.</summary>
public static class QuicTransportPolicy
{
    public const bool ZeroRttEnabled = false;

    /// <summary>Outbound QUIC dial budget before TLS/TCP fallback.</summary>
    public static TimeSpan DialTimeout { get; } = TimeSpan.FromSeconds(5);
}

/// <summary>Runtime QUIC capability for one process and RID. Does not select the product default.</summary>
public sealed record QuicTransportCapabilityReport
{
    public required string RuntimeIdentifier { get; init; }

    public required bool IsSupported { get; init; }

    public required bool NativeLibraryFound { get; init; }

    public required string NativeLibraryLocation { get; init; }

    public required string QuicProvider { get; init; }

    public required string FallbackProvider { get; init; }

    public required bool ZeroRttEnabled { get; init; }

    public string? Reason { get; init; }

    public string? Detail { get; init; }
}
