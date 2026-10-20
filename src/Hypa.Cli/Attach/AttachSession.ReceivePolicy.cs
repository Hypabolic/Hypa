using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

/// <summary>
/// The receive rules of the attach client queue. See
/// <see cref="ClientEventQueue"/> for the take order and the drop rule.
/// </summary>
public sealed partial class AttachSession
{
    /// <summary>
    /// Gives <paramref name="client"/> the attach receive rules. The focused
    /// pane is <see cref="AttachLiveState.PaneId"/>. The limit for the
    /// unpainted deltas of a pane without the focus applies only to a local
    /// mux connection. A remote link keeps all of its deltas until render
    /// credit covers it.
    /// </summary>
    internal static void ApplyAttachReceivePolicy(AttachLiveState live, ControlPlaneClient client)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(client);
        var local = client.UsesUnixSocket && !live.RemoteAttach && !live.RemoteDestination;
        client.SetAttachReceivePolicy(new AttachReceivePolicy
        {
            FocusedPane = () => live.PaneId,
            UnfocusedPaneDeltaBytes = local ? AttachReceivePolicy.DefaultUnfocusedPaneDeltaBytes : 0,
            // The read loop records the request. Start it on another task, so
            // the read loop never waits for a write, and the request does not
            // wait for the next event of the render loop.
            ReanchorRequested = () => _ = Task.Run(() => StartPaneReanchors(client, live, CancellationToken.None)),
        });
    }

    /// <summary>
    /// Gives <paramref name="reader"/> the attach receive rules when it has
    /// none. The render loop calls it for each client that it reads, so a
    /// client that a reconnect or a cube connect made gets the rules too.
    /// </summary>
    internal static void EnsureAttachReceivePolicy(AttachLiveState live, ControlPlaneClient reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.AttachReceivePolicy is null)
            ApplyAttachReceivePolicy(live, reader);
    }

    /// <summary>The wait before a reanchor request that failed is sent again.</summary>
    internal static readonly TimeSpan PaneReanchorRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Asks the mux for one full frame of each pane whose queued deltas the
    /// client dropped. <c>terminal.observe</c> of an observed pane posts a
    /// new attach snapshot, and later deltas chain on it. Nothing waits for
    /// these requests, so a slow reply never delays the focused echo.
    /// <para>
    /// The client keeps the drop of the deltas of the pane until its full
    /// frame comes, so a failed request is sent again after
    /// <see cref="PaneReanchorRetryDelay"/>. The drop stops only when the pane
    /// is gone or not visible. A closed link ends the retries: the attempt
    /// ends on the read loop.
    /// </para>
    /// The returned task ends when every request ended. A link or mux error
    /// does not fault it.
    /// </summary>
    internal static Task StartPaneReanchors(
        ControlPlaneClient reader,
        AttachLiveState live,
        CancellationToken ct,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(live);
        if (!reader.HasPaneReanchorRequests)
            return Task.CompletedTask;

        List<Task>? requests = null;
        foreach (var paneId in reader.TakePaneReanchorRequests())
            (requests ??= []).Add(RequestPaneReanchorAsync(reader, live, paneId, time ?? TimeProvider.System, ct));

        return requests is null ? Task.CompletedTask : Task.WhenAll(requests);
    }

    private static async Task RequestPaneReanchorAsync(
        ControlPlaneClient reader,
        AttachLiveState live,
        string paneId,
        TimeProvider time,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !reader.IsTransportClosed)
        {
            var subscription = live.RenderSub;
            if (string.IsNullOrWhiteSpace(subscription) || !IsVisiblePane(live, paneId))
            {
                // No frame of this pane is painted now. A later observe sends a full frame.
                reader.CancelPaneDrop(paneId);
                return;
            }

            try
            {
                await reader.CallAsync(
                        ProtocolMethods.TerminalObserve,
                        new JsonObject
                        {
                            ["pane_id"] = paneId,
                            ["subscription_id"] = subscription,
                            ["replace"] = false,
                        },
                        ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.NotFound)
            {
                // The pane is gone. No delta of it will come.
                reader.CancelPaneDrop(paneId);
                return;
            }
            catch (Exception ex) when (ex is ControlPlaneException or ControlPlaneClientTimeoutException)
            {
                // Keep the drop and ask again.
            }
            catch (Exception ex) when (ex is IOException
                or ObjectDisposedException
                or InvalidOperationException
                or OperationCanceledException)
            {
                return;
            }

            try
            {
                await Task.Delay(PaneReanchorRetryDelay, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
