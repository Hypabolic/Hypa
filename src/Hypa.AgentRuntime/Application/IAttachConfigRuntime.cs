using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Live attach-config holder. One immutable Current. Reload swaps the record.
/// ControlPlane reads Current for toast, terminal spawn, and scrollback.
/// </summary>
public interface IAttachConfigRuntime
{
    AttachClientConfig Current { get; }

    AttachConfigReloadReport ReloadFromDisk();
}

/// <summary>Fixed config. Tests and ControlPlane ctor fallback. No disk.</summary>
public sealed class StaticAttachConfigRuntime : IAttachConfigRuntime
{
    public StaticAttachConfigRuntime(AttachClientConfig current)
    {
        Current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public AttachClientConfig Current { get; }

    public AttachConfigReloadReport ReloadFromDisk() =>
        AttachConfigReloadReport.Failed(["Attach config runtime has no disk loader."]);
}
