using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;

namespace Hypa.ControlPlane;

public interface IControlPlaneService
{
    /// <summary>
    /// Dispatch a JSON-RPC method. <paramref name="connection"/> is required for
    /// subscription methods so they can bind transient observer state to the socket.
    /// </summary>
    Task<JsonElement> DispatchAsync(
        string method,
        JsonElement? parameters,
        IClientConnection? connection,
        CancellationToken ct);

    /// <summary>
    /// Load journal range for a registered subscription before writing the subscribe result.
    /// On failure the subscription is unregistered; the server must fail the RPC with
    /// <c>persistence_unavailable</c> (id-correlated), not a freestanding error line.
    /// </summary>
    Task<RuntimeResult<IReadOnlyList<RuntimeEventRecord>>> PrepareEventsSubscribeAsync(
        string subscriptionId,
        CancellationToken ct);

    /// <summary>
    /// After the subscribe RPC success response has been written, push prepared replay
    /// records then enable live. May run on a connection-scoped task while the request
    /// reader continues (design §8.1).
    /// </summary>
    Task CompleteEventsSubscribeAsync(
        string subscriptionId,
        IReadOnlyList<RuntimeEventRecord> replayRecords,
        IClientConnection connection,
        CancellationToken ct);

    /// <summary>Drop connection-local subscriptions on disconnect.</summary>
    void OnClientDisconnected(IClientConnection connection);

    /// <summary>
    /// Drop connection-local attachments and leases. Stall, detach, and
    /// disconnect keep the mux. None of these call <c>server.stop</c>.
    /// </summary>
    void OnClientDisconnected(IClientConnection connection, AttachClientLoss loss) =>
        OnClientDisconnected(connection);

    /// <summary>
    /// ClientWriterDrained). Default is a no-op so test doubles compile.
    /// </summary>
    void OnClientWriterDrained(IClientConnection connection)
    {
    }

    /// <summary>
    /// Dispose every pane runtime, unsubscribe events, and clear intelligence state.
    /// Called on host shutdown so child processes do not outlive the server.
    /// </summary>
    Task ShutdownAsync(CancellationToken ct);

    /// <summary>
    /// After the <c>server.stop</c> RPC result is written, shut the session and host.
    /// Default is a no-op so existing test doubles compile.
    /// </summary>
    Task CompleteServerStopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Optional hook after <c>server.live_handoff</c> returns. Production mux
    /// hosts may replace the process here. The default is a no-op.
    /// </summary>
    Task CompleteLiveHandoffAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// JSON-RPC 2.0 notifications omit <c>id</c> and must not receive an RpcResponse.
    /// Live <c>pane.send_keys</c> faults emit <c>pane.input_rejected</c> instead.
    /// Default is a no-op so existing test doubles compile.
    /// </summary>
    Task EmitNotificationFaultAsync(
        string method,
        JsonElement? parameters,
        int code,
        string message,
        IClientConnection? connection,
        CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>
    /// Mux start: start a fresh shell in every saved pane cwd after graph replace
    /// and before the client socket accepts. Default is a no-op so existing test
    // / doubles compile.
    /// </summary>
    Task RestoreSpawnAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// After the control socket accepts connections, type pending official
    /// resume commands. Restore stores the plan and does not type it.
    /// <c>src/app/agent_resume.rs:205-268</c> <c>start_pending_agent_resume</c>.
    /// Default is a no-op so existing test doubles compile.
    /// </summary>
    Task FlushOfficialAgentResumesAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// After restore and after the control socket accepts connections, run
    /// plugin startup hooks. One-shot. Must not block the mux.
    /// </summary>
    Task FlushPluginStartupHooksAsync(CancellationToken ct) => Task.CompletedTask;
}
