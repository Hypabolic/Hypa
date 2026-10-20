using System.Collections.Concurrent;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Hypa.ControlPlane;

/// <summary>
/// Echo mode. A connection that offered
/// <see cref="AttachEndpointProtocol.EchoModeCapability"/> in its hello gets
/// the termios ECHO and ICANON flags of the pane PTY, <c>echo</c> and
/// <c>canonical</c>, on each live cells body when the mode is known. The
/// attach client stops predictive echo when echo is off in canonical mode.
/// A connection without the capability gets no new field.
/// <para>
/// A mode change with no new output (a silent <c>stty -echo</c>) comes from
/// <see cref="IPaneRuntime.TtyModeChanged"/>. The runtime gave the change a
/// feed generation, so the paint that this requests posts a body with the new
/// flag and no new rows.
/// </para>
/// </summary>
public sealed partial class ControlPlaneService
{
    private readonly ConcurrentDictionary<string, byte> _echoModeConnections = new(StringComparer.Ordinal);

    /// <summary>
    /// Turns echo mode on for <paramref name="connection"/> when its hello
    /// lists the capability. A hello without it turns echo mode off.
    /// </summary>
    private void ConfigureEchoMode(IClientConnection connection, AttachEndpointHello hello)
    {
        var offered = hello.OptionalCapabilities is { } optional
            && optional.Contains(AttachEndpointProtocol.EchoModeCapability, StringComparer.Ordinal);
        if (offered)
            _echoModeConnections[connection.ConnectionId] = 0;
        else
            _echoModeConnections.TryRemove(connection.ConnectionId, out _);
    }

    /// <summary>True when the cells bodies of <paramref name="connectionId"/> carry the echo flag.</summary>
    internal bool EchoModeOn(string connectionId) => _echoModeConnections.ContainsKey(connectionId);

    private void ForgetEchoMode(string connectionId) => _echoModeConnections.TryRemove(connectionId, out _);

    /// <summary>
    /// Runs on the PTY helper frame pump, so it only queues the paint. No
    /// output emit follows the change to flush a <c>RequestPty</c> request, so
    /// the queued run takes the pane emit gate and requests the paint itself,
    /// as an output emit does.
    /// </summary>
    private void OnRuntimeTtyModeChanged(IPaneRuntime runtime)
    {
        if (runtime is not IPaneVtSnapshot)
            return;
        _ = Task.Run(async () =>
        {
            var paneId = runtime.Id.Value;
            try
            {
                var paneGate = GetPaneEmitGate(paneId);
                await paneGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    lock (_gate)
                    {
                        if (!IsRegisteredOccupantUnlocked(runtime))
                            return;
                    }

                    var decision = ((IPaneVtSnapshot)runtime).LastFeedPaintDecision;
                    if (!decision.RequestPaint || !ShouldCaptureLive(paneId))
                        return;
                    if (_renderCoalescer.Request(paneId, decision.FeedGeneration) is { } due)
                        FlushImmediateUnderGate(due);
                }
                finally
                {
                    paneGate.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Echo mode paint failed for {PaneId}", paneId);
            }
        });
    }
}
