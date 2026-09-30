using System.Runtime.InteropServices;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach.Copy;
using Hypa.Cli.Attach.Cubes;
using Hypa.Cli.Attach.Sidebar;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.ControlPlane;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;

namespace Hypa.Cli.Attach;

public sealed partial class AttachSession
{
    internal static bool ExclusiveClientSurfaceOpen(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return live.Worktrees.IsOpen || live.CubesPairing.IsOpen;
    }

    internal static bool ConsumesCubePairingDialogKeys(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return live.CubesPairing.IsOpen;
    }

    internal static void OpenAddCubeDialog(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Worktrees.IsOpen || AttachClientModePublication.ConflictsWithWorktreeOpen(live))
        {
            live.StatusError = "ui_busy";
            return;
        }

        live.CubesPairing.OpenAdd();
        live.Mouse.Selection.Clear();
        StampCubePairingExclusiveSurface(live);
    }

    internal static void OpenShareMuxDialog(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Worktrees.IsOpen || AttachClientModePublication.ConflictsWithWorktreeOpen(live))
        {
            live.StatusError = "ui_busy";
            return;
        }

        var enabled = live.PendingActivation is null;
        live.CubesPairing.OpenShare(
            enabled,
            RestoreShare(live, enabled),
            Environment.GetEnvironmentVariable(CubeShareState.AdvertiseHostVariable),
            live.HostReach?.ListInterfaceAddresses() ?? []);
        live.Mouse.Selection.Clear();
        StampCubePairingExclusiveSurface(live);
    }

    internal static async Task<bool> HandleCubePairingDialogKeysAsync(
        UnixRawTerminal? tty,
        AttachLiveState live,
        List<byte> decoded,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentNullException.ThrowIfNull(control);
        foreach (var decodedKey in KeyEventDecoder.Decode(CollectionsMarshal.AsSpan(decoded)))
        {
            await RouteCubePairingDialogKeyAsync(decodedKey.Chord, live, control, tty, ct)
                .ConfigureAwait(false);
        }

        return live.DetachRequested;
    }

    internal static async Task RouteCubePairingDialogKeyAsync(
        KeyChord chord,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chord);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        var dialog = live.CubesPairing;
        if (!dialog.IsOpen)
            return;
        if (chord.Key is "esc" && dialog.Cancel())
        {
            await PublishCubePairingClientModeAsync(control, live, ct).ConfigureAwait(false);
            PaintCubePairingDialog(tty, live);
            return;
        }

        if (chord.IsEnter)
        {
            await SubmitCubePairingDialogAsync(live, control, tty, ct).ConfigureAwait(false);
            return;
        }

        if (chord.Key is "tab" or "down" or "up")
        {
            var delta = chord.Key is "up" || (chord.Key is "tab" && chord.Shift) ? -1 : 1;
            dialog.MoveFocus(delta);
            PaintCubePairingDialog(tty, live);
            return;
        }

        if (chord.Key is "pageup" or "pagedown")
        {
            if (dialog.Share is { } share)
                share.ReachScroll = Math.Max(0, share.ReachScroll + (chord.Key is "pageup" ? -1 : 1) * CubePairingDialogPainter.ReachPageRows);
            PaintCubePairingDialog(tty, live);
            return;
        }

        if (chord.Key is "backspace")
        {
            dialog.BackspaceFocus();
            PaintCubePairingDialog(tty, live);
            return;
        }

        if (TryPrintableCubePairing(chord, out var text))
        {
            dialog.TypeIntoFocus(text);
            PaintCubePairingDialog(tty, live);
        }
    }

    internal static async Task<bool> TryHandleCubePairingDialogMouseAsync(
        UnixRawTerminal? tty,
        AttachLiveState live,
        IReadOnlyList<MouseEvent> events,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(control);
        if (!live.CubesPairing.IsOpen)
            return false;
        var dialog = live.CubesPairing;
        var layout = dialog.Layout
            ?? CubePairingDialogPainter.Measure(dialog, live.Chrome?.Cols ?? 80, live.Chrome?.Rows ?? 24);
        dialog.Layout = layout;
        // pointer. A miss outside the panel must not start HostRange.
        foreach (var ev in events)
            await HandleCubePairingDialogMouseAsync(tty, live, ev, control, ct).ConfigureAwait(false);
        return true;
    }

    internal static async Task RevokeCubeDeviceAsync(
        string? placementId,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(placementId))
            return;
        var cube = live.Cubes.FirstOrDefault(
            item => string.Equals(item.Id, placementId, StringComparison.Ordinal));
        if (cube is not null
            && DeviceId.TryParse(cube.EnrolledDeviceId, out var deviceId)
            && TryCreateLocalPairing(out _) is { } pairing)
        {
            await TryRevokeEnrolledOnHostAsync(placementId, pairing, deviceId, ct)
                .ConfigureAwait(false);
            await pairing
                .RevokeAsync(OperatorIdentity.LocalSelfHosted, deviceId, ct)
                .ConfigureAwait(false);
        }

        if (!ProcessLocalOperatorIdentity.TryResolve(out var owner)
            || !Hypa.Placement.Domain.PlacementId.TryParse(placementId, out var parsed))
        {
            live.StatusError = "placement could not be removed";
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        try
        {
            var directory = new PlacementDirectoryService(
                new FilePlacementDirectoryStore(PlacementStatePaths.ResolveFromEnvironment()));
            var removed = await directory
                .RemoveOwnedAsync(owner, parsed, ct)
                .ConfigureAwait(false);
            if (!removed.Ok)
            {
                live.StatusError = removed.Detail ?? removed.Reason ?? "placement was not removed";
                if (tty is not null)
                    PaintChrome(tty, live);
                return;
            }
        }
        catch (InvalidDataException)
        {
            live.StatusError = "placement store is unavailable";
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }
        catch (IOException)
        {
            live.StatusError = "placement store is unavailable";
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            live.StatusError = "placement store is unavailable";
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        live.StatusError = null;
        await ReloadCubeCatalogAsync(live, ct).ConfigureAwait(false);
        if (tty is not null)
            PaintChrome(tty, live);
    }

    private static async Task<ConnectivityOutcome> TryRevokeEnrolledOnHostAsync(
        string placementId,
        DevicePairingService pairing,
        DeviceId deviceId,
        CancellationToken ct)
    {
        if (!Hypa.Placement.Domain.PlacementId.TryParse(placementId, out var parsed))
            return ConnectivityOutcome.Success();

        try
        {
            var directory = new PlacementDirectoryService(
                new FilePlacementDirectoryStore(PlacementStatePaths.ResolveFromEnvironment()));
            var found = await directory.GetAsync(parsed, ct).ConfigureAwait(false);
            if (!found.Ok || found.Value?.Quic is not { } quic)
                return ConnectivityOutcome.Success();

            if (!QuicReachTarget.TryParseHostPort(quic.Target, out var host, out var port, out var hostError))
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.InviteInvalid,
                    hostError ?? "placement target is invalid");
            }

            if (string.IsNullOrWhiteSpace(quic.CertificateSha256))
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.InviteInvalid,
                    "certificate fingerprint is missing");
            }

            return await new OutboundHostInviteRevoke(pairing)
                .RevokeAsync(
                    new HostInviteRevokeReach
                    {
                        Host = host,
                        Port = port,
                        CertificateSha256 = quic.CertificateSha256,
                    },
                    deviceId,
                    ct)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "placement store is unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "placement store is unavailable");
        }
        catch (InvalidDataException)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "placement store is unavailable");
        }
    }

    private static async Task HandleCubePairingDialogMouseAsync(
        UnixRawTerminal? tty,
        AttachLiveState live,
        MouseEvent ev,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        var dialog = live.CubesPairing;
        var layout = dialog.Layout
            ?? CubePairingDialogPainter.Measure(dialog, live.Chrome?.Cols ?? 80, live.Chrome?.Rows ?? 24);
        dialog.Layout = layout;

        // SGR left-release keeps Button.Left. X10 release is Button.None
        // Drag/move must still extend
        // while the pointer is down.
        if (ev.Action is MouseAction.Drag
            || (ev.Action is MouseAction.Move && dialog.Selection.PointerDown))
        {
            if (!dialog.Selection.Active)
                return;
            ClampPairingPointer(layout.Panel, ev.Col, ev.Row, out var col, out var row);
            dialog.Selection.Extend(col, row);
            PaintCubePairingDialog(tty, live);
            return;
        }

        if (ev.Action is MouseAction.Release)
        {
            if (dialog.TakeIgnoreNextRelease())
            {
                dialog.Selection.EndPointer();
                return;
            }

            if (dialog.Selection.Active)
            {
                ClampPairingPointer(layout.Panel, ev.Col, ev.Row, out var col, out var row);
                dialog.Selection.Extend(col, row);
            }
            dialog.Selection.EndPointer();
            if (dialog.Selection.Dragged)
            {
                YankDialogSelection(tty, live);
                PaintCubePairingDialog(tty, live);
                return;
            }

            dialog.Selection.Clear();
            if (layout.Copy.Contains(ev.Col, ev.Row))
            {
                YankShareInvite(tty, live);
                PaintCubePairingDialog(tty, live);
            }

            return;
        }

        if (ev.Action is not MouseAction.Press || ev.Button is not MouseButton.Left)
            return;
        if (layout.Cancel.Contains(ev.Col, ev.Row))
        {
            dialog.Selection.Clear();
            if (dialog.Cancel())
                await PublishCubePairingClientModeAsync(control, live, ct).ConfigureAwait(false);
            PaintCubePairingDialog(tty, live);
            return;
        }

        if (layout.Advanced.Contains(ev.Col, ev.Row))
        {
            dialog.Selection.Clear();
            if (dialog.ToggleShareAdvanced())
                PaintCubePairingDialog(tty, live);
            return;
        }

        if (layout.Copy.Contains(ev.Col, ev.Row))
        {
            dialog.Selection.Clear();
            YankShareInvite(tty, live);
            PaintCubePairingDialog(tty, live);
            return;
        }

        if (layout.Primary.Contains(ev.Col, ev.Row))
        {
            dialog.Selection.Clear();
            dialog.IgnoreNextRelease = true;
            await SubmitCubePairingDialogAsync(live, control, tty, ct).ConfigureAwait(false);
            return;
        }

        if (layout.Panel.Contains(ev.Col, ev.Row))
        {
            ClampPairingPointer(layout.Panel, ev.Col, ev.Row, out var col, out var row);
            dialog.Selection.Begin(col, row);
            PaintCubePairingDialog(tty, live);
        }
    }

    private static void ClampPairingPointer(CellRect panel, int col, int row, out int clampedCol, out int clampedRow)
    {
        if (panel.Cols < 1 || panel.Rows < 1)
        {
            clampedCol = col;
            clampedRow = row;
            return;
        }

        clampedCol = Math.Clamp(col, panel.Col, panel.EndCol - 1);
        clampedRow = Math.Clamp(row, panel.Row, panel.EndRow - 1);
    }

    private static async Task SubmitCubePairingDialogAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        if (live.CubesPairing.Kind is CubePairingDialogKind.Share)
            await SubmitShareMuxAsync(live, tty, ct).ConfigureAwait(false);
        else if (live.CubesPairing.Kind is CubePairingDialogKind.Add)
            await SubmitAddCubeAsync(live, control, tty, ct).ConfigureAwait(false);
    }

    private static async Task SubmitShareMuxAsync(
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        var share = live.CubesPairing.Share;
        if (share is null)
            return;
        if (share.Starting)
            return;
        if (share.Running)
        {
            await StopAcceptHelperAsync(live).ConfigureAwait(false);
            share.Running = false;
            share.Invite = "";
            share.Error = null;
            PaintCubePairingDialog(tty, live);
            return;
        }

        if (!share.ShareEnabled || live.PendingActivation is not null)
        {
            share.Error = "share is disabled while painting a peer";
            PaintCubePairingDialog(tty, live);
            return;
        }

        var session = live.HomeSessionName;
        if (string.IsNullOrWhiteSpace(session))
        {
            share.Error = "home mux session is missing";
            PaintCubePairingDialog(tty, live);
            return;
        }

        if (!int.TryParse(share.Port, out var port))
        {
            share.Error = "port is invalid";
            PaintCubePairingDialog(tty, live);
            return;
        }

        var named = NamedAdvertiseHost(share);
        var bindHost = string.IsNullOrWhiteSpace(share.BindHost)
            ? CubeShareState.DefaultBindHost
            : share.BindHost.Trim();
        var resolved = HostInviteAdvertisement.Resolve(
            live.HostReach?.ListInterfaceAddresses() ?? [],
            named,
            bindHost,
            port);
        if (!resolved.Ok || resolved.Value is not { Count: > 0 } advertiseHosts)
        {
            share.Error = resolved.Detail ?? "no reachable address";
            PaintCubePairingDialog(tty, live);
            return;
        }

        share.AdvertisedHosts = advertiseHosts;
        share.Starting = true;
        share.Error = null;
        PaintCubePairingDialog(tty, live);
        try
        {
            var reused = await ExistingAcceptShare.TryIssueAsync(
                    session,
                    bindHost,
                    port,
                    advertiseHosts,
                    ct)
                .ConfigureAwait(false);
            if (reused.Ok && reused.Value is { Invite: { Length: > 0 } } listen)
            {
                ApplyShareListen(share, bindHost, listen, advertiseHosts);
                PaintCubePairingDialog(tty, live);
                return;
            }

            if (!ExistingAcceptShare.ShouldStartHelper(reused))
            {
                share.Error = reused.Detail ?? "port is already in use";
                PaintCubePairingDialog(tty, live);
                return;
            }

            var started = await AcceptHelperProcess.StartAsync(
                    session,
                    bindHost,
                    port,
                    advertiseHosts,
                    ct)
                .ConfigureAwait(false);
            if (started.Helper is null)
            {
                if (AcceptHelperProcess.LooksLikeAddressInUse(started.Error))
                {
                    reused = await ExistingAcceptShare.TryIssueAsync(
                            session,
                            bindHost,
                            port,
                            advertiseHosts,
                            ct)
                        .ConfigureAwait(false);
                    if (reused.Ok && reused.Value is { Invite: { Length: > 0 } } raced)
                    {
                        ApplyShareListen(share, bindHost, raced, advertiseHosts);
                        PaintCubePairingDialog(tty, live);
                        return;
                    }
                }

                share.Error = started.Error ?? "accept helper did not start";
                PaintCubePairingDialog(tty, live);
                return;
            }

            live.AcceptHelper = started.Helper;
            ApplyShareListen(share, bindHost, started.Helper.Listen, advertiseHosts);
            PaintCubePairingDialog(tty, live);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            share.Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "share did not start"
                : ex.Message;
            PaintCubePairingDialog(tty, live);
        }
        finally
        {
            share.Starting = false;
        }
    }

    private static void ApplyShareListen(
        CubeShareState share,
        string bindHost,
        ConnectivityAcceptListenDocument listen,
        IReadOnlyList<string> advertiseHosts)
    {
        share.Running = true;
        share.Invite = listen.Invite ?? "";
        share.Copied = false;
        share.Paired = false;
        share.PairedShown = false;
        share.PairedAt = null;
        share.AdvertisedHosts = advertiseHosts;
        share.BindHost = bindHost;
        share.Port = listen.Port.ToString();
        share.Error = null;
    }

    /// <summary>
    /// The address the person named for the invite. The environment value
    /// only pre-fills the field when the dialog opens, so a cleared field is
    /// an explicit request for every reachable address.
    /// </summary>
    internal static string? NamedAdvertiseHost(CubeShareState share)
    {
        ArgumentNullException.ThrowIfNull(share);
        return string.IsNullOrWhiteSpace(share.AdvertiseHost)
            ? null
            : share.AdvertiseHost.Trim();
    }

    private static async Task SubmitAddCubeAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        var add = live.CubesPairing.Add;
        if (add is null || add.Submitting)
            return;
        var decoded = HostInviteCodec.Decode(add.Invite);
        if (decoded.Ok && decoded.Value is not null)
            add.Invite = decoded.Value.TransferValue;
        if (!decoded.Ok || decoded.Value is null)
        {
            add.Error = decoded.Detail ?? "invite is invalid";
            PaintCubePairingDialog(tty, live);
            return;
        }

        if (string.IsNullOrWhiteSpace(decoded.Value.Session))
        {
            add.Error = "invite session is missing";
            PaintCubePairingDialog(tty, live);
            return;
        }

        if (!ProcessLocalOperatorIdentity.TryResolve(out var owner))
        {
            add.Error = "operator identity is missing";
            PaintCubePairingDialog(tty, live);
            return;
        }

        var pairing = TryCreateLocalPairing(out var pairingError);
        if (pairing is null)
        {
            add.Error = pairingError ?? "pairing store is unavailable";
            PaintCubePairingDialog(tty, live);
            return;
        }

        add.Submitting = true;
        add.Error = null;
        PaintCubePairingDialog(tty, live);
        var redeemed = await new OutboundHostInviteRedeem(pairing)
            .RedeemAsync(decoded.Value, ct)
            .ConfigureAwait(false);
        if (!redeemed.Ok || redeemed.Value is null)
        {
            add.Submitting = false;
            add.Error = redeemed.Detail ?? redeemed.Reason ?? "invite redeem failed";
            PaintCubePairingDialog(tty, live);
            return;
        }

        var dialed = decoded.Value with { Host = redeemed.Value.Address };

        var catalog = TryCreatePlacementCatalog();
        if (catalog is null)
        {
            add.Submitting = false;
            add.Error = "placement store is unavailable";
            await RevokeRedeemedEnrollmentAsync(pairing, dialed, redeemed.Value.Device.Id, ct)
                .ConfigureAwait(false);
            PaintCubePairingDialog(tty, live);
            return;
        }

        var label = FirstNonEmpty(add.Label, decoded.Value.Label, decoded.Value.Session, dialed.Host)
            ?? dialed.Host;
        var added = await catalog.AddAsync(
                new QuicPlacementAddRequest
                {
                    Owner = owner,
                    Label = label,
                    Target = FormatInviteTarget(dialed.Host, decoded.Value.Port),
                    Session = decoded.Value.Session,
                    CertificateSha256 = decoded.Value.CertificateSha256,
                    EnrolledDeviceId = redeemed.Value.Device.Id.Value,
                },
                ct)
            .ConfigureAwait(false);
        add.Submitting = false;
        if (!added.Ok)
        {
            add.Error = added.Detail ?? added.Reason ?? "placement was not saved";
            await RevokeRedeemedEnrollmentAsync(pairing, dialed, redeemed.Value.Device.Id, ct)
                .ConfigureAwait(false);
            PaintCubePairingDialog(tty, live);
            return;
        }

        live.CubesPairing.Cancel();
        await ReloadCubeCatalogAsync(live, ct).ConfigureAwait(false);
        await PublishCubePairingClientModeAsync(control, live, ct).ConfigureAwait(false);
        PaintCubePairingDialog(tty, live);
    }

    private static async Task ReloadCubeCatalogAsync(AttachLiveState live, CancellationToken ct)
    {
        var source = live.CubeCatalog ?? new EnvironmentSidebarCubeCatalogSource();
        var cubes = await source.LoadAsync(ct).ConfigureAwait(false);
        live.Cubes = cubes.Items;
        live.CubesState = cubes.State;
        BindConnectedPlacementIdentity(live);
        BindSelectedCubeIdentity(live);
        if (live.LastSnapshot is { } snap)
            RebuildSidebar(live, snap, requestGit: false, hydratePicker: false);
        live.InvalidateChrome();
    }

    private static async Task StopAcceptHelperAsync(AttachLiveState live)
    {
        if (live.AcceptHelper is not { } helper)
            return;
        live.AcceptHelper = null;
        await helper.DisposeAsync().ConfigureAwait(false);
    }

    internal static async Task<bool> TryFlushSharePairingAsync(
        AttachLiveState live,
        UnixRawTerminal? tty,
        SemaphoreSlim? controlGate,
        CancellationToken ct,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.CubesPairing.Kind is not CubePairingDialogKind.Share)
            return false;
        if (live.CubesPairing.Share is not { } share)
            return false;
        if (!share.Running || share.Starting)
            return false;

        var clock = now ?? live.Time.GetUtcNow();
        if (!share.Paired && live.AcceptHelper?.TryTakePaired(out _) == true)
            CubeSharePairingWatch.TryMarkPaired(share, clock);
        if (!share.Paired
            && await CubeSharePairingWatch.InviteWasConsumedAsync(share.Invite, ct).ConfigureAwait(false))
        {
            CubeSharePairingWatch.TryMarkPaired(share, clock);
        }

        if (share.Paired && !share.PairedShown)
        {
            if (controlGate is not null && !controlGate.Wait(0))
                return false;
            try
            {
                share.PairedShown = true;
                PaintCubePairingDialog(tty, live);
                return true;
            }
            finally
            {
                controlGate?.Release();
            }
        }

        if (!CubeSharePairingWatch.ShouldDismiss(share, clock))
            return false;
        if (controlGate is not null && !controlGate.Wait(0))
            return false;
        try
        {
            if (!live.CubesPairing.Cancel())
                return false;
            await PublishSharePairingDismissAsync(live, ct).ConfigureAwait(false);
            PaintCubePairingDialog(tty, live);
            return true;
        }
        finally
        {
            controlGate?.Release();
        }
    }

    private static async Task PublishSharePairingDismissAsync(AttachLiveState live, CancellationToken ct)
    {
        StampCubePairingExclusiveSurface(live);
        IAttachCommandPort? port = live.RenderPort;
        if (port is null && live.ControlSlot?.Client is { } client)
            port = new ControlPlaneAttachCommandPort(client);
        if (port is null)
            return;
        await ReportAttachClientModeAsync(port, live, ct).ConfigureAwait(false);
    }

    private static CubeShareState? RestoreShare(AttachLiveState live, bool enabled)
    {
        if (live.AcceptHelper is not { } helper)
            return null;
        return new CubeShareState
        {
            ShareEnabled = enabled,
            Running = true,
            BindHost = string.IsNullOrWhiteSpace(helper.Listen.Bind)
                ? CubeShareState.DefaultBindHost
                : helper.Listen.Bind,
            AdvertiseHost = live.CubesPairing.Share?.AdvertiseHost ?? "",
            AdvertisedHosts = live.CubesPairing.Share?.AdvertisedHosts ?? [],
            Port = helper.Listen.Port.ToString(),
            Invite = helper.Listen.Invite ?? "",
            Advanced = live.CubesPairing.Share?.Advanced == true,
        };
    }

    private static void StampCubePairingExclusiveSurface(AttachLiveState live)
    {
        live.Engine.ExclusiveSurfaceOpen = ExclusiveClientSurfaceOpen(live);
        live.Dispatcher.ClientMode = AttachClientModePublication.PublishedToken(live);
        ReconcilePrefixAsciiInput(live);
    }

    private static async Task PublishCubePairingClientModeAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct)
    {
        StampCubePairingExclusiveSurface(live);
        await ReportAttachClientModeAsync(control, live, ct).ConfigureAwait(false);
    }

    private static void PaintCubePairingDialog(UnixRawTerminal? tty, AttachLiveState live)
    {
        if (tty is not null)
            PaintChrome(tty, live);
    }

    private static void YankShareInvite(UnixRawTerminal? tty, AttachLiveState live)
    {
        var share = live.CubesPairing.Share;
        if (share is null || string.IsNullOrWhiteSpace(share.Invite))
            return;
        YankClipboard(tty, live, share.Invite);
        share.Copied = true;
    }

    private static void YankDialogSelection(UnixRawTerminal? tty, AttachLiveState live)
    {
        var extracted = live.CubesPairing.ExtractSelection();
        if (string.IsNullOrWhiteSpace(extracted)
            && live.CubesPairing.Selection.Active
            && live.Host.Cols > 0
            && live.CubesPairing.PanelBounds.Cols > 0)
        {
            var panel = live.CubesPairing.PanelBounds;
            CubePairingDialogSelection.Normalize(
                live.CubesPairing.Selection.AnchorCol,
                live.CubesPairing.Selection.AnchorRow,
                live.CubesPairing.Selection.EndCol,
                live.CubesPairing.Selection.EndRow,
                out var c1,
                out var r1,
                out var c2,
                out var r2);
            c1 = Math.Clamp(c1, panel.Col, panel.EndCol - 1);
            c2 = Math.Clamp(c2, panel.Col, panel.EndCol - 1);
            r1 = Math.Clamp(r1, panel.Row, panel.EndRow - 1);
            r2 = Math.Clamp(r2, panel.Row, panel.EndRow - 1);
            extracted = live.Host.ExtractRange(c1, r1, c2, r2);
        }

        if (string.IsNullOrWhiteSpace(extracted))
            return;
        live.Mouse.Selection.Clear();
        var compact = HostInviteFormat.Compact(extracted);
        var decoded = HostInviteCodec.Decode(compact);
        var text = decoded.Ok && decoded.Value is not null
            ? decoded.Value.TransferValue
            : extracted.Trim();
        YankClipboard(tty, live, text);
        if (live.CubesPairing.Share is { } share
            && !string.IsNullOrWhiteSpace(share.Invite)
            && (decoded.Ok
                || share.Invite.Contains(compact, StringComparison.Ordinal)
                || compact.Contains(share.Invite, StringComparison.Ordinal)))
        {
            share.Copied = true;
        }
    }

    internal static async Task RevokeRedeemedEnrollmentAsync(
        DevicePairingService pairing,
        HostInvite invite,
        DeviceId deviceId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(invite);
        await new OutboundHostInviteRevoke(pairing)
            .RevokeAsync(
                new HostInviteRevokeReach
                {
                    Host = invite.Host,
                    Port = invite.Port,
                    CertificateSha256 = invite.CertificateSha256,
                },
                deviceId,
                ct)
            .ConfigureAwait(false);
        await pairing
            .RevokeAsync(OperatorIdentity.LocalSelfHosted, deviceId, ct)
            .ConfigureAwait(false);
    }

    private static void YankClipboard(UnixRawTerminal? tty, AttachLiveState live, string text)
    {
        var osc = Osc52Yank.Encode(text);
        live.LastOsc52 = osc;
        tty?.WriteBytes(osc);
        ShowClipboardToast(live, "copied");
    }

    private static DevicePairingService? TryCreateLocalPairing(out string? error)
    {
        try
        {
            var directory = DevicePairingStatePaths.ResolveFromEnvironment();
            Directory.CreateDirectory(directory);
            var keys = PlatformDeviceKeyStore.CreateOrFallback(
                DevicePairingStatePaths.FallbackKeyDirectory(directory));
            error = null;
            return new DevicePairingService(new FileDevicePairingStore(directory), keys);
        }
        catch (InvalidDataException ex)
        {
            error = FirstNonEmpty(ex.Message, "pairing store is unavailable");
            return null;
        }
        catch (IOException ex)
        {
            error = FirstNonEmpty(ex.Message, "pairing store is unavailable");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = FirstNonEmpty(ex.Message, "pairing store is unavailable");
            return null;
        }
    }

    private static QuicPlacementCatalogService? TryCreatePlacementCatalog()
    {
        try
        {
            return new QuicPlacementCatalogService(
                new FilePlacementDirectoryStore(PlacementStatePaths.ResolveFromEnvironment()));
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FormatInviteTarget(string host, int port) =>
        host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

    private static bool TryPrintableCubePairing(KeyChord chord, out string text)
    {
        text = "";
        if (chord.Ctrl || chord.Alt || chord.Prefix)
            return false;
        if (chord.Key is "esc" or "enter" or "tab" or "backspace" or "up" or "down" or "left" or "right"
            or "delete" or "home" or "end" or "pageup" or "pagedown" or "insert")
        {
            return false;
        }

        text = chord.Key switch
        {
            "space" => " ",
            "minus" => "-",
            "plus" => "+",
            "comma" => ",",
            "period" => ".",
            "slash" => "/",
            "colon" => ":",
            _ when chord.Key.Length == 1 => chord.Shift && char.IsAsciiLetter(chord.Key[0])
                ? char.ToUpperInvariant(chord.Key[0]).ToString()
                : chord.Key,
            _ => "",
        };
        return text.Length > 0;
    }
}
