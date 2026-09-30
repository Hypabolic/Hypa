using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach;

/// <summary>
/// Process-local navigation hint per endpoint. Survives detach. Process
/// exit drops the store. A changed boot id discards the old hint.
/// </summary>
internal sealed class AttachClientViewHintStore
{
    private readonly Dictionary<string, AttachClientView> _hints = new(StringComparer.Ordinal);

    public void Remember(AttachClientView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (string.IsNullOrWhiteSpace(view.EndpointId))
            return;
        _hints[view.EndpointId] = view;
    }

    public void Remember(
        string endpointId,
        string bootId,
        string? workspaceId,
        string? tabId,
        string? paneId)
    {
        if (string.IsNullOrWhiteSpace(endpointId) || string.IsNullOrWhiteSpace(bootId))
            return;

        var activeTabs = new Dictionary<string, string>(StringComparer.Ordinal);
        var focusedPanes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(workspaceId) && !string.IsNullOrWhiteSpace(tabId))
            activeTabs[workspaceId] = tabId;
        if (!string.IsNullOrWhiteSpace(tabId) && !string.IsNullOrWhiteSpace(paneId))
            focusedPanes[tabId] = paneId;

        Remember(new AttachClientView
        {
            ClientId = string.Empty,
            EndpointId = endpointId.Trim(),
            BootId = bootId.Trim(),
            FocusedWorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim(),
            ActiveTabIds = activeTabs,
            FocusedPaneIds = focusedPanes,
        });
    }

    public AttachClientView? Present(string endpointId, string bootId)
    {
        if (string.IsNullOrWhiteSpace(endpointId) || string.IsNullOrWhiteSpace(bootId))
            return null;
        if (!_hints.TryGetValue(endpointId, out var hint))
            return null;
        if (!string.Equals(hint.BootId, bootId, StringComparison.Ordinal))
        {
            _hints.Remove(endpointId);
            return null;
        }

        return hint;
    }

    public EndpointActivationIntent Present(EndpointActivationIntent intent, string bootId)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.FocusTarget() is not null)
            return intent;

        var hint = Present(intent.EndpointId, bootId);
        if (hint is null)
            return intent;

        return intent with
        {
            WorkspaceId = hint.FocusedWorkspaceId,
            TabId = hint.FocusedTabId(),
            PaneId = hint.FocusedPaneId(),
        };
    }

    public void Discard(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return;
        _hints.Remove(endpointId);
    }

    public bool Contains(string endpointId) =>
        !string.IsNullOrWhiteSpace(endpointId) && _hints.ContainsKey(endpointId);
}
