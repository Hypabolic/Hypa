using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Persist and restore the session graph (session / workspace / tab / pane metadata).
/// Journal/scrollback manifest APIs are reserved for (no-op stubs allowed).
/// </summary>
public interface IRuntimeSessionStore
{
    /// <summary>
    /// Load the named session graph if present. Returns null when no session row exists.
    /// </summary>
    Task<RuntimeResult<SessionState?>> TryLoadAsync(string sessionName, CancellationToken ct = default);

    /// <summary>
    /// Upsert the full session graph in one transaction (workspaces, tabs, panes, bindings).
    /// When <paramref name="removeMissingPanes"/> is true, durable pane, tab, and workspace
    /// rows that are absent from <paramref name="state"/> are deleted (sync graph deletions).
    /// Prefer this flag over a separate delete for control-plane closes.
    /// </summary>
    Task<RuntimeResult<RuntimeUnit>> SaveAsync(
        SessionState state,
        CancellationToken ct = default,
        bool removeMissingPanes = false);

    /// <summary>
    /// Delete a single pane row (and dependent occupants/scrollback manifests) by id.
    /// Prefer <see cref="SaveAsync"/> with <c>removeMissingPanes: true</c> from the control plane.
    /// </summary>
    Task<RuntimeResult<RuntimeUnit>> DeletePaneAsync(PaneId paneId, CancellationToken ct = default);

    /// <summary>
    /// Runtime session identity stamped in <c>runtime_meta</c> (stable across restarts of the same state root).
    /// </summary>
    Task<RuntimeResult<string>> GetRuntimeSessionIdAsync(CancellationToken ct = default);
}
