namespace Hypa.Placement.Domain;

/// <summary>
/// Transport discriminator on a peer row. This is not a Placement kind.
/// Kind stays <see cref="PlacementDirectoryKind.Peer"/>.
/// </summary>
public static class PeerProviders
{
    public const string Ssh = "ssh";
    public const string Quic = "quic";

    public static bool IsKnown(string? provider) =>
        string.Equals(provider, Ssh, StringComparison.Ordinal)
        || string.Equals(provider, Quic, StringComparison.Ordinal);
}
