using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;

namespace Hypa.Placement.Application;

public enum SshPlacementAttention
{
    None = 0,
    Reachable = 1,
    Unreachable = 2,
    ApprovalRequired = 3,
    PreparationRequired = 4,
    Disabled = 5,
}

public static class SshPlacementPresentation
{
    public static bool IsSshProvider(PlacementRecord record) => record.Ssh is not null;

    public static SshPlacementAttention ResolveAttention(PlacementRecord record) =>
        record.Ssh is null
            ? SshPlacementAttention.None
            : ResolveAttention(record.Reachability, record.Ssh);

    public static SshPlacementAttention ResolveAttention(
        PlacementReachability reachability,
        SshPlacementProfile ssh)
    {
        if (!ssh.Enabled)
        {
            return ssh.Attention switch
            {
                SshPlacementAttentionStates.ApprovalRequired => SshPlacementAttention.ApprovalRequired,
                SshPlacementAttentionStates.PreparationRequired => SshPlacementAttention.PreparationRequired,
                _ => SshPlacementAttention.Disabled,
            };
        }

        return reachability switch
        {
            PlacementReachability.Reachable => SshPlacementAttention.Reachable,
            PlacementReachability.Unreachable => SshPlacementAttention.Unreachable,
            _ => SshPlacementAttention.Unreachable,
        };
    }

    public static string ReachabilitySuffix(SshPlacementAttention attention) => attention switch
    {
        SshPlacementAttention.Reachable => "SSH · Reachable",
        SshPlacementAttention.Unreachable => "SSH · Unreachable",
        SshPlacementAttention.ApprovalRequired => "SSH · Approval required",
        SshPlacementAttention.PreparationRequired => "SSH · Preparation required",
        SshPlacementAttention.Disabled => "SSH · Disabled",
        _ => "",
    };

    public static bool ConnectEnabled(SshPlacementAttention attention) =>
        attention is SshPlacementAttention.Reachable or SshPlacementAttention.Unreachable;

    public static string? ConnectDetail(SshPlacementAttention attention, SshPlacementProfile? ssh)
    {
        if (ssh is null)
            return null;
        return attention switch
        {
            SshPlacementAttention.ApprovalRequired =>
                $"OpenSSH approval required for {ssh.Target}. Run ssh {OpenSshArgumentBuilder.Quote(ssh.Target)} in a terminal, then retry.",
            SshPlacementAttention.PreparationRequired =>
                $"Remote preparation failed for {ssh.Label}. Placement remains disabled.",
            SshPlacementAttention.Disabled => null,
            _ => null,
        };
    }
}
