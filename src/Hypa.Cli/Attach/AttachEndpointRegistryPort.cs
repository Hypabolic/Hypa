using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

internal sealed class AttachEndpointRegistryPort : IEndpointRegistryPort
{
    private readonly AttachLiveState _live;
    private readonly AttachEndpointRpcClient? _sourceRpc;
    private readonly AttachEndpointRpcClient? _targetRpc;
    private readonly EndpointActivationLease? _sourceLease;
    private readonly EndpointActivationLease _targetLease;
    private readonly string _sourceId;
    private readonly string _targetId;
    private readonly string _clientId;
    private readonly Action<EndpointActivationRpcCompletion> _postCompletion;
    private readonly Action<string, ulong, string> _postFailure;

    internal AttachEndpointRegistryPort(
        AttachLiveState live,
        AttachEndpointRpcClient? sourceRpc,
        AttachEndpointRpcClient? targetRpc,
        EndpointActivationLease? sourceLease,
        EndpointActivationLease targetLease,
        Action<EndpointActivationRpcCompletion> postCompletion,
        Action<string, ulong, string> postFailure)
    {
        _live = live;
        _sourceRpc = sourceRpc;
        _targetRpc = targetRpc;
        _sourceLease = sourceLease;
        _targetLease = targetLease;
        _sourceId = sourceLease?.EndpointId ?? live.ConnectedPlacementId ?? "local";
        _targetId = targetLease.EndpointId;
        _clientId = live.EndpointClientId;
        _postCompletion = postCompletion;
        _postFailure = postFailure;
    }

    public string ActiveId => _live.ConnectedPlacementId ?? "local";

    public bool SurfaceActive(string endpointId) =>
        _live.GetEndpointSurfaceActive(endpointId);

    public bool SupportsSurfaceInterest(string endpointId) => true;

    /// <summary>
    /// <c>Sent</c> means the frame entered that endpoint's writer queue.
    /// A send error is <c>record_failure</c> for that endpoint and <c>NotSent</c>.
    /// A missing client is not-connected and does not record a failure.
    /// </summary>
    public EndpointSendOutcome SendTo(string endpointId, EndpointActivationMessage message)
    {
        var rpc = ResolveRpc(endpointId);
        if (rpc is null)
            return EndpointSendOutcome.NotConnected;

        if (!TryEncode(endpointId, message, out var method, out var payload))
            return EndpointSendOutcome.NotSent;

        if (!rpc.TryAccept(method, payload, out var reply, out var rejection))
        {
            var (generation, _) = LeaseIdentity(endpointId);
            AttachSession.RecordEndpointTransportFailure(
                _live,
                endpointId,
                generation,
                rejection,
                rpc.Client);
            return EndpointSendOutcome.NotSent;
        }

        _ = ObserveReplyAsync(endpointId, message, reply);
        return EndpointSendOutcome.Sent;
    }

    /// disconnects that one transport. The timer later calls handle_endpoint_disconnect.
    public void Fail(string endpointId, string error)
    {
        var (generation, _) = LeaseIdentity(endpointId);
        var connection = ResolveRpc(endpointId)?.Client;
        AttachSession.RecordEndpointTransportFailure(_live, endpointId, generation, error, connection);
    }

    public void SetSurfaceActive(string endpointId, bool active) =>
        _live.SetEndpointSurfaceActive(endpointId, active);

    public bool SetActive(string endpointId) =>
        AttachSession.ApplyEndpointActivationSetActive(_live, endpointId, _sourceId, _targetId);

    public void FreezeInput() => _live.InputFrozen = true;

    public void UnfreezeInput() => _live.InputFrozen = false;

    private AttachEndpointRpcClient? ResolveRpc(string endpointId)
    {
        if (string.Equals(_targetId, endpointId, StringComparison.Ordinal))
            return _targetRpc;
        if (string.Equals(_sourceId, endpointId, StringComparison.Ordinal))
            return _sourceRpc;
        return null;
    }

    private (ulong Generation, string BootId) LeaseIdentity(string endpointId)
    {
        if (_sourceLease is not null
            && string.Equals(_sourceLease.EndpointId, endpointId, StringComparison.Ordinal))
            return (_sourceLease.ConnectionGeneration, _sourceLease.BootId);
        if (string.Equals(_targetLease.EndpointId, endpointId, StringComparison.Ordinal))
            return (_targetLease.ConnectionGeneration, _targetLease.BootId);
        return (0, string.Empty);
    }

    /// <summary>
    /// The activation message already carries the id that is written.
    /// This method returns that same id. It does not build a different one.
    /// </summary>
    private static bool TryRequestId(EndpointActivationMessage message, out string requestId)
    {
        switch (message)
        {
            case EndpointActivationMessage.FocusRevoke(var focusOffId):
                requestId = focusOffId;
                return true;
            case EndpointActivationMessage.HostFocusBaseline(_, var baselineId):
                requestId = baselineId;
                return true;
            case EndpointActivationMessage.Resize(_, var resizeId):
                requestId = resizeId;
                return true;
            case EndpointActivationMessage.SurfaceInterest(var surfaceId, _):
                requestId = surfaceId;
                return true;
            case EndpointActivationMessage.NavigationFocus(var focusId, _):
                requestId = focusId;
                return true;
            case EndpointActivationMessage.PresentationSync(var syncId):
                requestId = syncId;
                return true;
            case EndpointActivationMessage.PresentationEffectsFence(var fenceId):
                requestId = fenceId;
                return true;
            case EndpointActivationMessage.HostTheme(_, var themeId):
                requestId = themeId;
                return true;
            default:
                requestId = string.Empty;
                return false;
        }
    }

    private bool TryEncode(
        string endpointId,
        EndpointActivationMessage message,
        out string method,
        out JsonObject payload)
    {
        switch (message)
        {
            case EndpointActivationMessage.FocusRevoke(var requestId):
                method = ProtocolMethods.AttachFocus;
                payload = Node(new AttachFocusRequest
                {
                    RequestId = requestId,
                    ClientId = _clientId,
                    Focused = false,
                }, ProtocolJsonContext.Default.AttachFocusRequest);
                return true;
            case EndpointActivationMessage.HostFocusBaseline(var focused, var requestId):
                method = ProtocolMethods.AttachFocus;
                payload = Node(new AttachFocusRequest
                {
                    RequestId = requestId,
                    ClientId = _clientId,
                    Focused = focused,
                }, ProtocolJsonContext.Default.AttachFocusRequest);
                return true;
            case EndpointActivationMessage.Resize(var geometry, var requestId):
                method = ProtocolMethods.AttachResize;
                payload = Node(new AttachResizeRequest
                {
                    RequestId = requestId,
                    ClientId = _clientId,
                    Geometry = geometry,
                }, ProtocolJsonContext.Default.AttachResizeRequest);
                return true;
            case EndpointActivationMessage.SurfaceInterest(var requestId, var active):
                method = ProtocolMethods.AttachSurfaceInterest;
                payload = Node(new AttachSurfaceInterestRequest
                {
                    RequestId = requestId,
                    ClientId = _clientId,
                    Active = active,
                    GeometryRevision = _live.PendingActivation?.Resize.GeometryRevision ?? 1,
                }, ProtocolJsonContext.Default.AttachSurfaceInterestRequest);
                return true;
            case EndpointActivationMessage.NavigationFocus(var requestId, var target):
                method = ProtocolMethods.AttachFocus;
                payload = Node(
                    BuildNavigationFocus(requestId, target),
                    ProtocolJsonContext.Default.AttachFocusRequest);
                return true;
            case EndpointActivationMessage.PresentationSync(var requestId):
                method = ProtocolMethods.AttachPresentationSync;
                payload = Node(new AttachPresentationSyncRequest
                {
                    RequestId = requestId,
                    ClientId = _clientId,
                }, ProtocolJsonContext.Default.AttachPresentationSyncRequest);
                return true;
            case EndpointActivationMessage.HostTheme(var theme, _):
                method = ProtocolMethods.ClientHostThemeSet;
                payload = Node(theme, ProtocolJsonContext.Default.HostThemeSetParams);
                return true;
            case EndpointActivationMessage.PresentationEffectsFence(var token):
                if (!AttachSession.TryBuildEffectsFenceRequest(
                        _live,
                        _sourceLease,
                        _targetLease,
                        _sourceId,
                        endpointId,
                        token,
                        _clientId,
                        out var request,
                        out _))
                {
                    method = string.Empty;
                    payload = null!;
                    return false;
                }

                method = ProtocolMethods.AttachPresentationReady;
                payload = Node(request, ProtocolJsonContext.Default.AttachPresentationSyncRequest);
                return true;
            default:
                method = string.Empty;
                payload = null!;
                return false;
        }
    }

    private static JsonObject Node<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.SerializeToNode(value, info)!.AsObject();

    /// An application reply error keeps the written request id. It is not a
    /// transport failure. A writer fault is <c>record_failure</c>.
    private async Task ObserveReplyAsync(
        string endpointId,
        EndpointActivationMessage message,
        Task<ControlPlaneCallResult> reply)
    {
        var (generation, bootId) = LeaseIdentity(endpointId);
        ControlPlaneCallResult result;
        try
        {
            result = await reply.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PublishFault(endpointId, generation, bootId, message, ex);
            return;
        }

        if (result.Error is not null)
        {
            PublishFault(endpointId, generation, bootId, message, result.Error);
            return;
        }

        try
        {
            PublishSuccess(endpointId, generation, message, result.Result);
        }
        catch (Exception ex)
        {
            PublishFault(endpointId, generation, bootId, message, ex);
        }
    }

    private void PublishSuccess(
        string endpointId,
        ulong generation,
        EndpointActivationMessage message,
        JsonElement result)
    {
        switch (message)
        {
            case EndpointActivationMessage.SurfaceInterest(var requestId, _):
                PostSurface(
                    endpointId,
                    generation,
                    requestId,
                    JsonSerializer.Deserialize(result.GetRawText(), ProtocolJsonContext.Default.AttachSurfaceInterestResult)
                    ?? throw new InvalidOperationException("attach surface_interest returned empty result"));
                break;
            case EndpointActivationMessage.FocusRevoke:
            case EndpointActivationMessage.HostFocusBaseline:
            case EndpointActivationMessage.Resize:
            case EndpointActivationMessage.NavigationFocus:
                PostControl(
                    endpointId,
                    generation,
                    JsonSerializer.Deserialize(result.GetRawText(), ProtocolJsonContext.Default.AttachControlResult)
                    ?? throw new InvalidOperationException("attach control returned empty result"));
                break;
            case EndpointActivationMessage.PresentationSync:
            case EndpointActivationMessage.PresentationEffectsFence:
            case EndpointActivationMessage.HostTheme:
                break;
            default:
                break;
        }
    }

    private void PublishFault(
        string endpointId,
        ulong generation,
        string bootId,
        EndpointActivationMessage message,
        Exception ex)
    {
        if (IsWriterFault(ex) || !TryRequestId(message, out var requestId))
        {
            _postFailure(endpointId, generation, ex.Message);
            return;
        }

        PostControl(
            endpointId,
            generation,
            new AttachControlResult
            {
                Error = new AttachEndpointError
                {
                    Code = AttachEndpointErrorCodes.ActivationFailed,
                    Message = ex.Message,
                },
                RequestId = requestId,
                BootId = bootId,
                ProjectionRevision = 0,
                GeometryRevision = 0,
            });
    }

    /// A socket or writer failure is a transport fault. An application reply
    /// error, including a focus-baseline RPC error, is not.
    private static bool IsWriterFault(Exception ex) =>
        ex is IOException or ObjectDisposedException;

    private AttachFocusRequest BuildNavigationFocus(string requestId, EndpointFocusTarget target) =>
        target.Kind switch
        {
            EndpointFocusTargetKind.Pane => new AttachFocusRequest
            {
                RequestId = requestId,
                ClientId = _clientId,
                Focused = true,
                PaneId = target.Id,
            },
            EndpointFocusTargetKind.Tab => new AttachFocusRequest
            {
                RequestId = requestId,
                ClientId = _clientId,
                Focused = true,
                TabId = target.Id,
            },
            EndpointFocusTargetKind.Workspace => new AttachFocusRequest
            {
                RequestId = requestId,
                ClientId = _clientId,
                Focused = true,
                WorkspaceId = target.Id,
            },
            _ => throw new InvalidOperationException("unknown focus target"),
        };

    private void PostControl(string endpointId, ulong generation, AttachControlResult result) =>
        _postCompletion(new EndpointActivationRpcCompletion.Control(endpointId, generation, result));

    private void PostSurface(
        string endpointId,
        ulong generation,
        string requestId,
        AttachSurfaceInterestResult result) =>
        _postCompletion(new EndpointActivationRpcCompletion.Surface(endpointId, generation, requestId, result));

}

internal abstract record EndpointActivationRpcCompletion
{
    internal sealed record Control(string EndpointId, ulong Generation, AttachControlResult Result)
        : EndpointActivationRpcCompletion;

    internal sealed record Surface(
        string EndpointId,
        ulong Generation,
        string RequestId,
        AttachSurfaceInterestResult Result) : EndpointActivationRpcCompletion;

    internal sealed record PresentationSync(
        string EndpointId,
        ulong Generation,
        string RequestId,
        AttachPresentationSync Sync) : EndpointActivationRpcCompletion;

    internal sealed record EffectsReady(
        string EndpointId,
        ulong Generation,
        string Token,
        AttachPresentationSync Ready) : EndpointActivationRpcCompletion;
}
