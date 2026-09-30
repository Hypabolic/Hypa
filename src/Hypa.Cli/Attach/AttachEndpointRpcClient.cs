using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

internal sealed class AttachEndpointRpcClient
{
    private readonly ControlPlaneClient _client;

    public AttachEndpointRpcClient(ControlPlaneClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public ControlPlaneClient Client => _client;

    // The frame is accepted onto this client's writer.
    internal bool TryAccept(
        string method,
        JsonObject payload,
        out Task<ControlPlaneCallResult> reply,
        out string rejection) =>
        _client.TryAcceptFrame(
            method,
            payload,
            AttachEndpointProtocol.PhaseTimeout,
            out reply,
            out rejection);

    public async Task<(AttachEndpointWelcome Welcome, ulong ConnectionGeneration)> HelloAsync(
        AttachEndpointHello hello,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToNode(hello, ProtocolJsonContext.Default.AttachEndpointHello)!
            .AsObject();
        var result = await _client.CallAsync(
                ProtocolMethods.AttachHello,
                payload,
                ct,
                AttachEndpointProtocol.PhaseTimeout)
            .ConfigureAwait(false);
        var welcome = JsonSerializer.Deserialize(result.GetRawText(), ProtocolJsonContext.Default.AttachEndpointWelcome)
            ?? throw new InvalidOperationException("attach hello returned empty welcome");
        var generation = welcome.ConnectionGeneration ?? 1UL;
        return (welcome, generation);
    }

    public async Task<AttachControlResult> ResizeAsync(
        AttachResizeRequest request,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToNode(request, ProtocolJsonContext.Default.AttachResizeRequest)!
            .AsObject();
        var result = await _client.CallAsync(
                ProtocolMethods.AttachResize,
                payload,
                ct,
                AttachEndpointProtocol.PhaseTimeout)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize(result.GetRawText(), ProtocolJsonContext.Default.AttachControlResult)
            ?? throw new InvalidOperationException("attach resize returned empty result");
    }

    public async Task<AttachSurfaceInterestResult> SurfaceInterestAsync(
        AttachSurfaceInterestRequest request,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToNode(
                request,
                ProtocolJsonContext.Default.AttachSurfaceInterestRequest)!
            .AsObject();
        var result = await _client.CallAsync(
                ProtocolMethods.AttachSurfaceInterest,
                payload,
                ct,
                AttachEndpointProtocol.PhaseTimeout)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize(result.GetRawText(), ProtocolJsonContext.Default.AttachSurfaceInterestResult)
            ?? throw new InvalidOperationException("attach surface_interest returned empty result");
    }

    public async Task<AttachControlResult> FocusAsync(
        AttachFocusRequest request,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToNode(request, ProtocolJsonContext.Default.AttachFocusRequest)!
            .AsObject();
        var result = await _client.CallAsync(
                ProtocolMethods.AttachFocus,
                payload,
                ct,
                AttachEndpointProtocol.PhaseTimeout)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize(result.GetRawText(), ProtocolJsonContext.Default.AttachControlResult)
            ?? throw new InvalidOperationException("attach focus returned empty result");
    }

    public async Task<AttachHealthResult> HealthAsync(
        AttachHealthRequest request,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToNode(request, ProtocolJsonContext.Default.AttachHealthRequest)!
            .AsObject();
        var result = await _client.CallAsync(
                ProtocolMethods.AttachHealth,
                payload,
                ct,
                AttachEndpointProtocol.HealthTimeout)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize(result.GetRawText(), ProtocolJsonContext.Default.AttachHealthResult)
            ?? throw new InvalidOperationException("attach health returned empty result");
    }

    /// The client loop admits <c>PresentationSyncEvent</c> once.
    public async Task RequestPresentationSyncAsync(
        AttachPresentationSyncRequest request,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToNode(
                request,
                ProtocolJsonContext.Default.AttachPresentationSyncRequest)!
            .AsObject();
        _ = await _client.CallAsync(
                ProtocolMethods.AttachPresentationSync,
                payload,
                ct,
                AttachEndpointProtocol.PhaseTimeout)
            .ConfigureAwait(false);
    }

    public async Task SendPresentationEffectsFenceAsync(
        AttachPresentationSyncRequest request,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToNode(
                request,
                ProtocolJsonContext.Default.AttachPresentationSyncRequest)!
            .AsObject();
        _ = await _client.CallAsync(
                ProtocolMethods.AttachPresentationReady,
                payload,
                ct,
                AttachEndpointProtocol.PhaseTimeout)
            .ConfigureAwait(false);
    }

}
