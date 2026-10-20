using System.Collections.Frozen;
using System.Text.Json;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Envelopes;

namespace Hypa.ControlPlane;

/// <summary>
/// The scope of the fence that a request puts between itself and the
/// <c>pane.send_keys</c> notifications of the same connection.
/// </summary>
internal enum KeyFenceScope
{
    /// <summary>A key can pass the request, and the request can pass a key.</summary>
    None,

    /// <summary>The request is a fence only for the keys of one pane.</summary>
    Pane,

    /// <summary>The request is a fence for the keys of every pane.</summary>
    All,
}

/// <summary>The key fence of one request. <see cref="PaneId"/> is set only for <see cref="KeyFenceScope.Pane"/>.</summary>
internal readonly record struct KeyFence(KeyFenceScope Scope, string? PaneId)
{
    public static KeyFence None => new(KeyFenceScope.None, null);

    public static KeyFence All => new(KeyFenceScope.All, null);

    public static KeyFence ForPane(string paneId) => new(KeyFenceScope.Pane, paneId);
}

/// <summary>
/// The key order contract of one connection. The mux reads the requests of a
/// connection in order. Each <c>pane.send_keys</c> notification goes on the
/// input lane. Each other request goes on the serial dispatch lane. The read
/// loop computes the fences between the two lanes in read order:
/// <list type="number">
/// <item>The keys for one pane stay in FIFO order. The input lane admits keys one at a time, in read order.</item>
/// <item>A <c>pane.resize</c> that the client sent before a key for the same pane is applied before the mux admits that key.</item>
/// <item>After a focus change, the mux sends the next keys to the newly focused pane.
/// A focus change is a fence for every key, in both directions.</item>
/// <item>A key never passes a request that changes the occupant or the binding of its pane.
/// Such a request is a fence for every key, in both directions.
/// <c>_bindingMutationGate</c> stays on the key path.</item>
/// </list>
/// A fence works in both directions. A key waits for each earlier fence request
/// that applies to it. A fence request waits until the input lane has finished
/// each earlier key. Each wait points at an item that the mux read earlier, so
/// the waits cannot make a cycle. The input lane is one FIFO queue: a key that
/// waits for a fence also holds the keys after it, for each pane.
/// Only the requests in the pass list below are not fences. Each of them reads
/// state and changes nothing that the key path reads. An unknown method is a
/// fence for every key.
/// </summary>
internal static class SendKeysOrderRules
{
    /// <summary>
    /// Methods that a key can pass. Each handler changes nothing that the key
    /// path reads: frozen state, surface interest, the focused tab of the
    /// client view, leases, work generation, overlay generation and occupant.
    /// </summary>
    private static readonly FrozenSet<string> PassMethods = new[]
    {
        ProtocolMethods.Ping,
        ProtocolMethods.RuntimeHealth,
        ProtocolMethods.SessionSnapshot,
        ProtocolMethods.WorkspaceList,
        ProtocolMethods.WorkspaceGet,
        ProtocolMethods.PaneList,
        ProtocolMethods.PaneGet,
        ProtocolMethods.PaneRead,
        ProtocolMethods.PaneWaitForOutput,
        ProtocolMethods.PaneCurrent,
        ProtocolMethods.PaneNeighbor,
        ProtocolMethods.PaneEdges,
        ProtocolMethods.PaneProcessInfo,
        ProtocolMethods.AgentList,
        ProtocolMethods.AgentStatus,
        ProtocolMethods.AgentGet,
        ProtocolMethods.AgentRead,
        ProtocolMethods.AgentExplain,
        ProtocolMethods.AgentWait,
        ProtocolMethods.EventsWait,
        ProtocolMethods.TabList,
        ProtocolMethods.TabGet,
        ProtocolMethods.LayoutExport,
        ProtocolMethods.RuntimeBindingGet,
        ProtocolMethods.PluginList,
        ProtocolMethods.PluginActionList,
        ProtocolMethods.PluginLogList,
        ProtocolMethods.PluginResourceList,
        ProtocolMethods.PluginResourceGet,
        ProtocolMethods.PluginConfigGet,
        ProtocolMethods.IntegrationList,
        ProtocolMethods.WorktreeList,
        ProtocolMethods.CubeShareStatus,
        ProtocolMethods.ServerAgentManifests,
        ProtocolMethods.AttachHealth,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// True for a <c>pane.send_keys</c> notification that can go on the input
    /// lane. A key with an id gets a reply, so it stays on the dispatch lane.
    /// A key with a plugin grant token also stays there, because the grant
    /// marks the connection in read order.
    /// </summary>
    public static bool UsesInputLane(RpcRequest request) =>
        string.IsNullOrEmpty(request.Id)
        && string.IsNullOrWhiteSpace(request.GrantToken)
        && string.Equals(request.Method, ProtocolMethods.PaneSendKeys, StringComparison.Ordinal);

    /// <summary>The key fence of a request on the dispatch lane.</summary>
    public static KeyFence FenceOf(string method, JsonElement? parameters)
    {
        if (PassMethods.Contains(method))
            return KeyFence.None;

        // A resize changes the size of one pane. Keys for other panes can pass it.
        // A key on the dispatch lane (with an id) keeps FIFO order with the keys of its pane.
        if (string.Equals(method, ProtocolMethods.PaneResize, StringComparison.Ordinal)
            || string.Equals(method, ProtocolMethods.PaneSendKeys, StringComparison.Ordinal))
        {
            return TryReadPaneId(parameters) is { } paneId
                ? KeyFence.ForPane(paneId)
                : KeyFence.All;
        }

        return KeyFence.All;
    }

    /// <summary>The <c>pane_id</c> string of the params, or null.</summary>
    public static string? TryReadPaneId(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } p)
            return null;
        if (!p.TryGetProperty("pane_id", out var paneEl) || paneEl.ValueKind != JsonValueKind.String)
            return null;
        var paneId = paneEl.GetString();
        return string.IsNullOrWhiteSpace(paneId) ? null : paneId;
    }
}
