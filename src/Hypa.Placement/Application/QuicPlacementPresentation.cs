using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

public enum QuicPlacementAttention
{
    None = 0,
    Reachable = 1,
    Unreachable = 2,
    Disabled = 3,
}

public static class QuicPlacementPresentation
{
    public static bool IsQuicProvider(PlacementRecord record) => record.Quic is not null;

    public static QuicPlacementAttention ResolveAttention(PlacementRecord record) =>
        record.Quic is null
            ? QuicPlacementAttention.None
            : ResolveAttention(record.Reachability, record.Quic);

    public static QuicPlacementAttention ResolveAttention(
        PlacementReachability reachability,
        QuicPlacementProfile quic)
    {
        if (!quic.Enabled)
            return QuicPlacementAttention.Disabled;

        return reachability switch
        {
            PlacementReachability.Reachable => QuicPlacementAttention.Reachable,
            PlacementReachability.Unreachable => QuicPlacementAttention.Unreachable,
            _ => QuicPlacementAttention.Unreachable,
        };
    }

    public static string ReachabilitySuffix(QuicPlacementAttention attention) => attention switch
    {
        QuicPlacementAttention.Reachable => "QUIC · Reachable",
        QuicPlacementAttention.Unreachable => "QUIC · Unreachable",
        QuicPlacementAttention.Disabled => "QUIC · Disabled",
        _ => "",
    };

    public static bool ConnectEnabled(QuicPlacementAttention attention) =>
        attention is QuicPlacementAttention.Reachable or QuicPlacementAttention.Unreachable;
}
