using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Cubes;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal const string CubeShareUnavailableCode = "share_unavailable";

    internal Task<JsonElement> HandleCubeShareStatusAsync(EmptyParams _, CancellationToken ct) =>
        Task.FromResult(OkTyped(
            ToCubeShareResult(RequireCubeShare().Status),
            ProtocolJsonContext.Default.CubeShareStatusResult));

    internal async Task<JsonElement> HandleCubeShareStartAsync(CubeShareStartParams p, CancellationToken ct)
    {
        var host = RequireCubeShare();
        var bind = string.IsNullOrWhiteSpace(p.Bind) ? CubeShareSettings.DefaultBind : p.Bind.Trim();
        var port = p.Port ?? CubeShareSettings.DefaultPort;
        if (port is < 1 or > 65535)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "port must be 1-65535");

        var status = await host.StartAsync(new CubeShareSettings { Bind = bind, Port = port }, ct)
            .ConfigureAwait(false);
        return OkTyped(ToCubeShareResult(status), ProtocolJsonContext.Default.CubeShareStatusResult);
    }

    internal async Task<JsonElement> HandleCubeShareStopAsync(EmptyParams _, CancellationToken ct)
    {
        var status = await RequireCubeShare().StopAsync(ct).ConfigureAwait(false);
        return OkTyped(ToCubeShareResult(status), ProtocolJsonContext.Default.CubeShareStatusResult);
    }

    internal static CubeShareStatusResult ToCubeShareResult(CubeShareStatus status) =>
        new()
        {
            Enabled = status.Enabled,
            State = status.State,
            Bind = status.Settings?.Bind,
            Port = status.Settings?.Port,
            Listen = status.Listen is { } listen
                ? new CubeShareListenInfo
                {
                    Bind = listen.Bind,
                    Port = listen.Port,
                    CertificateSha256 = listen.CertificateSha256,
                    QuicListening = listen.QuicListening,
                    QuicDetail = listen.QuicDetail,
                }
                : null,
            Error = status.Error,
            Restarts = status.Restarts,
        };

    private ICubeShareHost RequireCubeShare() =>
        _cubeShare ?? throw new ControlPlaneException(
            ProtocolErrorCodes.InvalidState,
            "share is not available in this mux",
            CubeShareUnavailableCode);
}
