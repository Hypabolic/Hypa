namespace Hypa.AgentRuntime.Domain;

/// <summary>
// / Semantic agent state for a pane.
/// Neutral protocol names — no UI terms.
/// </summary>
public enum AgentStatus
{
    Unknown = 0,
    Working = 1,
    Blocked = 2,
    Idle = 3,
    Done = 4,
}
