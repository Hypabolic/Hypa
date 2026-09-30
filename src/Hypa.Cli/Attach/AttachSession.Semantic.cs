using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

public sealed partial class AttachSession
{
    private static AttachSemanticActionHost? TryStartSemanticActionHost(
        AttachLiveState live,
        ControlPlaneClient? control,
        UnixRawTerminal tty,
        SemaphoreSlim controlGate,
        CancellationToken attemptCt)
    {
        if (control is null)
            return null;

        try
        {
            var session = string.IsNullOrWhiteSpace(live.SessionName)
                ? AttachSessionResolver.DefaultName
                : live.SessionName;
            return AttachSemanticActionHost.Start(
                MuxSessionCatalog.AttachStateDirectory(session),
                live,
                controlGate,
                (request, token) =>
                {
                    var port = LiveControlPort(
                        live,
                        new ControlPlaneAttachCommandPort(
                            LiveControlClient(live, control) ?? control));
                    if (port is not LoggingAttachCommandPort)
                        port = new LoggingAttachCommandPort(port, live);
                    return ApplySemanticActionAsync(port, live, tty, request, token);
                },
                attemptCt);
        }
        catch (Exception ex) when (
            ex is IOException
                or InvalidOperationException
                or SocketException
                or PlatformNotSupportedException
                or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>tab.focus</c>. The click path and the external command both call this.
    /// </summary>
    internal static async Task<JsonElement> ApplyTabFocusAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        string tabId,
        string source,
        CancellationToken ct,
        bool refreshChrome = true,
        bool observePending = true)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        live.UiActionSource = source;
        try
        {
            var focused = await control.CallAsync(
                    ProtocolMethods.TabFocus,
                    new JsonObject { ["tab_id"] = tabId },
                    ct)
                .ConfigureAwait(false);
            live.Dispatcher.ApplyTab(focused);
            await SyncFocusAsync(control, live, ct, observePending).ConfigureAwait(false);
            // The server view changed. A resize declined under the previous
            // view can apply now.
            lock (live.ChromeStateGate)
                live.DeclinedPaneResizes.Clear();
            if (refreshChrome)
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
            return focused;
        }
        finally
        {
            live.UiActionSource = null;
        }
    }

    /// <summary>
    // This calls
    /// <see cref="ApplyCubesConnectAsync"/>, the same body a cube click uses.
    /// </summary>
    internal static async Task<AttachSemanticReply> ApplySemanticActionAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        AttachSemanticRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId))
            request = request with { RequestId = "cli_" + Guid.NewGuid().ToString("N") };

        if (!string.IsNullOrWhiteSpace(request.AttachClientId)
            && !string.Equals(request.AttachClientId, live.AttachClientId, StringComparison.Ordinal))
        {
            return Reply(request, live, AttachSemanticOutcomes.Rejected, "attach client does not match");
        }

        var (endpoint, generation) = AttachSemanticIdentity.Read(live);
        if (!string.IsNullOrWhiteSpace(request.EndpointId)
            && !string.Equals(request.EndpointId, endpoint, StringComparison.Ordinal))
        {
            return Reply(request, live, AttachSemanticOutcomes.Rejected, "endpoint does not match");
        }

        if (request.ConnectionGeneration is { } expected && expected != generation)
            return Reply(request, live, AttachSemanticOutcomes.Rejected, "connection generation does not match");

        if (BlocksPresentationInput(live))
            return Reply(request, live, AttachSemanticOutcomes.Rejected, "attach input is frozen");

        if (request.Action == AttachSemanticActions.SettingsClose)
            return await ApplySettingsCloseActionAsync(control, live, tty, request, ct).ConfigureAwait(false);

        if (live.Engine.Mode is not AttachClientMode.Terminal)
            return Reply(request, live, AttachSemanticOutcomes.Rejected, "attach is not in the terminal");

        try
        {
            return request.Action switch
            {
                AttachSemanticActions.TabFocus => await ApplyTabFocusActionAsync(
                        control, live, tty, request, ct)
                    .ConfigureAwait(false),
                AttachSemanticActions.PlacementConnect => await ApplyPlacementConnectActionAsync(
                        control, live, tty, request, ct)
                    .ConfigureAwait(false),
                AttachSemanticActions.SettingsOpen => await ApplySettingsOpenActionAsync(
                        control, live, tty, request, ct)
                    .ConfigureAwait(false),
                _ => Reply(request, live, AttachSemanticOutcomes.Rejected, "action is unknown"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ControlPlaneException ex)
        {
            live.StatusError = FormatStatus(ex);
            PaintStatus(tty, live);
            return Reply(request, live, AttachSemanticOutcomes.Rejected, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            live.StatusError = FormatStatus(ex);
            PaintStatus(tty, live);
            return Reply(request, live, AttachSemanticOutcomes.Rejected, ex.Message);
        }
    }

    private static async Task<AttachSemanticReply> ApplyTabFocusActionAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        AttachSemanticRequest request,
        CancellationToken ct)
    {
        if (live.PendingActivation is not null)
            return Reply(request, live, AttachSemanticOutcomes.Rejected, "endpoint activation is in progress");
        if (string.IsNullOrWhiteSpace(request.TabId))
            return Reply(request, live, AttachSemanticOutcomes.Rejected, "tab id is required");

        var focused = await ApplyTabFocusAsync(
                control,
                live,
                tty,
                request.TabId,
                AttachSemanticSources.Cli,
                ct)
            .ConfigureAwait(false);
        string? detail = null;
        if (request.Verbose && focused.ValueKind is not JsonValueKind.Undefined)
            detail = focused.GetRawText();
        if (string.Equals(live.TabId, request.TabId, StringComparison.Ordinal))
            return Reply(request, live, AttachSemanticOutcomes.Applied, detail: detail);
        return Reply(request, live, AttachSemanticOutcomes.Rejected, "tab focus was not applied", detail);
    }

    private static async Task<AttachSemanticReply> ApplyPlacementConnectActionAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        AttachSemanticRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PlacementId))
            return Reply(request, live, AttachSemanticOutcomes.Rejected, "placement id is required");

        var placementId = request.PlacementId;
        var cube = live.Cubes.FirstOrDefault(item =>
            string.Equals(item.Id, placementId, StringComparison.Ordinal));
        if (cube is null)
            return Reply(request, live, AttachSemanticOutcomes.Rejected, "placement is not in the sidebar");

        await ApplyCubesConnectAsync(
                new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: placementId),
                live,
                control,
                tty,
                ct)
            .ConfigureAwait(false);

        if (string.Equals(live.ConnectedPlacementId, placementId, StringComparison.Ordinal))
            return Reply(request, live, AttachSemanticOutcomes.Applied);
        if (live.DestConnectAttempt is { } attempt
            && string.Equals(attempt.EndpointId, placementId, StringComparison.Ordinal))
        {
            return Reply(request, live, AttachSemanticOutcomes.Pending);
        }

        if (string.Equals(live.PendingActivation?.Target.EndpointId, placementId, StringComparison.Ordinal))
            return Reply(request, live, AttachSemanticOutcomes.Pending);
        return Reply(request, live, AttachSemanticOutcomes.Rejected, "placement connect was not accepted");
    }

    /// <summary>
    /// Opens settings through the same key-engine entry as the settings key.
    /// </summary>
    private static async Task<AttachSemanticReply> ApplySettingsOpenActionAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        AttachSemanticRequest request,
        CancellationToken ct)
    {
        var pageId = string.IsNullOrWhiteSpace(request.PageId) ? null : request.PageId;
        if (pageId is not null
            && !live.Engine.Settings.Pages.Any(page => string.Equals(page.Id, pageId, StringComparison.Ordinal)))
        {
            return Reply(request, live, AttachSemanticOutcomes.Rejected, "settings page is unknown");
        }

        var events = live.WithPaint(() => live.Engine.EnterSettings(pageId));
        await ApplyModeEventsAsync(events, live, control, tty, linked: null, ct).ConfigureAwait(false);
        if (live.Engine.Mode is not AttachClientMode.Settings)
            return Reply(request, live, AttachSemanticOutcomes.Rejected, "settings did not open");
        if (pageId is not null && !string.Equals(live.Engine.Settings.ActivePageId, pageId, StringComparison.Ordinal))
            return Reply(request, live, AttachSemanticOutcomes.Rejected, "settings page was not selected");
        return Reply(request, live, AttachSemanticOutcomes.Applied);
    }

    /// <summary>Closes settings the same way the escape key does.</summary>
    private static async Task<AttachSemanticReply> ApplySettingsCloseActionAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        AttachSemanticRequest request,
        CancellationToken ct)
    {
        if (live.Engine.Mode is not AttachClientMode.Settings)
            return Reply(request, live, AttachSemanticOutcomes.Rejected, "settings is not open");

        var events = live.WithPaint(
            () => live.Engine.Feed(new KeyChord(false, false, false, false, "esc")));
        await ApplyModeEventsAsync(events, live, control, tty, linked: null, ct).ConfigureAwait(false);
        return live.Engine.Mode is AttachClientMode.Settings
            ? Reply(request, live, AttachSemanticOutcomes.Rejected, "settings did not close")
            : Reply(request, live, AttachSemanticOutcomes.Applied);
    }

    private static AttachSemanticReply Reply(
        AttachSemanticRequest request,
        AttachLiveState live,
        string outcome,
        string? error = null,
        string? detail = null)
    {
        var (endpoint, generation) = AttachSemanticIdentity.Read(live);
        return new AttachSemanticReply
        {
            RequestId = request.RequestId,
            Action = request.Action,
            Source = AttachSemanticSources.Cli,
            AttachClientId = live.AttachClientId,
            EndpointId = endpoint,
            ConnectionGeneration = generation,
            Outcome = outcome,
            WorkspaceId = live.WorkspaceId,
            TabId = live.TabId,
            PaneId = string.IsNullOrWhiteSpace(live.PaneId) ? null : live.PaneId,
            ConnectedPlacementId = live.ConnectedPlacementId,
            Error = error,
            Detail = request.Verbose ? detail : null,
        };
    }

    private static void PaintStatus(UnixRawTerminal? tty, AttachLiveState live)
    {
        if (tty is null || !live.ChromeEnabled)
            return;
        try
        {
            PaintChrome(tty, live);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}
