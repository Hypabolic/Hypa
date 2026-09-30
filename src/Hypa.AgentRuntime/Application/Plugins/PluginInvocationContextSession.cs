using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
/// Resolves the focused pane native session reference for plugin context.
/// Publishes only when the pane holds semantic authority and stores a valid ref.
/// </summary>
public static class PluginInvocationContextSession
{
    public static NativeAgentSessionRef? FromPane(PaneState? pane)
    {
        if (pane is null || !pane.HoldsSemanticAuthority)
            return null;
        var session = pane.AgentSession;
        return session is not null && IsValid(session) ? session : null;
    }

    internal static bool IsValid(NativeAgentSessionRef session) =>
        session.Kind switch
        {
            NativeAgentSessionRef.KindId => NativeAgentSessionRef.IsValidId(session.Value),
            NativeAgentSessionRef.KindPath => NativeAgentSessionRef.IsValidPath(session.Value),
            _ => false,
        };
}
