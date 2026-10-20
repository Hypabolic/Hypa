using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Integrations;
using Hypa.AgentRuntime.Application.Status;
using Hypa.AgentRuntime.Application.WindowTitle;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Infrastructure.Logging;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Commands;
using Hypa.Cli.Attach.Connections;
using Hypa.Cli.Attach.Cubes;
using Hypa.Cli.Attach.Config;
using Hypa.Cli.Attach.Copy;
using Hypa.Cli.Attach.EditScrollback;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Latency;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Onboarding;
using Hypa.Cli.Attach.ReleaseNotes;
using Hypa.Cli.Attach.Overlay;
using Hypa.Cli.Attach.Popup;
using Hypa.Cli.Attach.Prediction;
using Hypa.Cli.Attach.Settings;
using Hypa.Cli.Attach.Sidebar;
using Hypa.Cli.Attach.Splash;
using Hypa.AgentRuntime.Application.Attach.Status;
using Hypa.Cli.Attach.Theme;
using Hypa.Cli.Attach.Worktrees;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Placement.Infrastructure;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.ControlPlane;
using Hypa.AgentRuntime.Domain.Cubes.Connections;

namespace Hypa.Cli.Attach;

/// <summary>
/// One-connection attach client. Unix is the local path. Connectivity is a
/// joined session. A stall reconnects and repaints. One reader demultiplexes
/// RPC replies, reliable control/lifecycle events, and replaceable render.
/// Detach does not call <c>server.stop</c>. Remote drop does not print mux stop.
/// </summary>
public sealed partial class AttachSession : IMuxAttachDriver, IMuxRestartSource
{
    public const int LeaseTtlMs = LeaseRenewLoop.DefaultTtlMs;

    private readonly TimeProvider _time;
    private readonly FocusRedrawPolicy _focus;
    private readonly MuxSessionCatalog _catalog;
    private readonly Func<string, ControlPlaneClient> _clientFactory;
    private readonly bool _useInjectedFactory;
    private readonly IAttachReconnect _reconnect;
    private readonly DeveloperJoinCapabilityIssuer _capabilityIssuer = new();
    private IFramedSession? _framedClient;
    private IFramedSession? _framedMux;
    private int _stallReconnects;
    private readonly ICubesConnectEndpointResolver? _cubesResolver;
    private readonly ICubesConnectRetargeter? _cubesConnect;
    private readonly IMoveWorkMenu? _moveWork;
    private readonly IMoveWorkDestExecutorFactory? _destExecutorFactory;
    private readonly MuxReleaseCapability _release;
    private readonly ISidebarCubeCatalogSource? _cubeCatalog;
    private readonly ProcessOperatorPaths? _operatorPaths;
    private readonly IHostReachCatalog? _hostReach;
    private readonly IMuxUpdateNoticeSource? _updateNotices;
    private MuxRestartRequest? _restartRequest;
    private static readonly IEventPayloadRedactor s_attachFailRedactor =
        new DefaultEventPayloadRedactor();

    public AttachSession(
        TimeProvider? time = null,
        FocusRedrawPolicy? focus = null,
        MuxSessionCatalog? catalog = null,
        Func<string, ControlPlaneClient>? clientFactory = null,
        IAttachReconnect? reconnect = null,
        IFramedSession? framed = null,
        IFramedSession? muxFramed = null,
        ICubesConnectEndpointResolver? cubesResolver = null,
        ICubesConnectRetargeter? cubesConnect = null,
        IMoveWorkMenu? moveWork = null,
        IMoveWorkDestExecutorFactory? destExecutorFactory = null,
        MuxReleaseCapability? release = null,
        ISidebarCubeCatalogSource? cubeCatalog = null,
        ProcessOperatorPaths? operatorPaths = null,
        IHostReachCatalog? hostReach = null,
        IMuxUpdateNoticeSource? updateNotices = null)
    {
        _time = time ?? TimeProvider.System;
        _focus = focus ?? new FocusRedrawPolicy();
        _catalog = catalog ?? new MuxSessionCatalog();
        _useInjectedFactory = clientFactory is not null;
        _clientFactory = clientFactory ?? (socket => new ControlPlaneClient(socket));
        _reconnect = reconnect ?? new AttachReconnectService(_time);
        _framedClient = framed;
        _framedMux = muxFramed;
        _cubesResolver = cubesResolver;
        _cubesConnect = cubesConnect;
        _moveWork = moveWork;
        _destExecutorFactory = destExecutorFactory;
        _release = release ?? MuxReleaseCapability.Product;
        _cubeCatalog = cubeCatalog;
        _operatorPaths = operatorPaths;
        _hostReach = hostReach;
        _updateNotices = updateNotices;
        _latency = new AttachLatencyMeter(_time);
    }

    private readonly AttachLatencyMeter _latency;

    /// <summary>
    /// The latency meter of this attach. The session owns one instance and
    /// gives it to its live state. A test may inject its own meter.
    /// </summary>
    internal AttachLatencyMeter Latency
    {
        get => _latency;
        init => _latency = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal IHostReachCatalog? HostReach => _hostReach;

    public MuxRestartRequest? TakeRestartRequest()
    {
        var request = _restartRequest;
        _restartRequest = null;
        return request;
    }

    public bool CalledServerStop { get; private set; }

    internal ControlPlaneClient CreateClient(MuxReadyInfo ready) =>
        _useInjectedFactory
            ? _clientFactory(ready.SocketPath)
            : ready.ResolveEndpoint().CreateClient();

    internal static ControlPlaneClient? LiveControlClient(
        AttachLiveState live,
        ControlPlaneClient? fallback)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.ControlSlot?.Client is { } active)
            return active;
        // The fallback is the Local client. It never serves a cube: a request
        // to the Local mux with the identity of a cube fails there. A lost cube
        // gets a closed client, so its work fails here and does not reach Local.
        return IsLocalEndpointActive(live) ? fallback : live.UnavailableEndpointClient;
    }

    internal static IAttachCommandPort LiveControlPort(
        AttachLiveState live,
        IAttachCommandPort fallback)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(fallback);
        if (live.RenderPort is not null)
            return live.RenderPort;
        if (live.ControlSlot?.Client is { } client)
            return new ControlPlaneAttachCommandPort(client);
        return fallback;
    }

    /// <summary>
    // / <c>surface_active: true</c>.
    /// puts that flag on endpoint hello. ApplyHello still starts inactive, so
    /// local attach then sends <c>attach.surface_interest</c> active.
    /// </summary>
    internal static AttachGeometry BuildLocalAttachGeometry(
        AttachLiveState live,
        UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        var cols = live.Host.Cols;
        var rows = live.Host.Rows;
        if (cols < 1 || rows < 1)
        {
            cols = live.Chrome?.FocusedContent?.Cols ?? 0;
            rows = live.Chrome?.FocusedContent?.Rows ?? 0;
        }

        if ((cols < 1 || rows < 1) && tty is not null
            && tty.TryGetSize(out var ttyCols, out var ttyRows)
            && ttyCols > 0 && ttyRows > 0)
        {
            cols = ttyCols;
            rows = ttyRows;
        }

        if (cols < 1)
            cols = 80;
        if (rows < 1)
            rows = 24;

        return new AttachGeometry
        {
            Columns = (ushort)Math.Clamp(cols, 1, ushort.MaxValue),
            Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
            CellWidthPx = 8,
            CellHeightPx = 16,
            GeometryRevision = 1,
        };
    }

    /// <summary>
    // / Distinct ClientShell identity per attach process.
    /// client id per connection; reusing the id drops the older view.
    /// </summary>
    internal static string NewAttachClientId() => "cli_" + Guid.NewGuid().ToString("N");

    /// <param name="keyboard">
    /// False for mouse reports, focus reports and paste. The body then has
    /// <c>keyboard: false</c>, and the mux opens no echo window. A body for
    /// keyboard input has no field, which means true.
    /// </param>
    internal static JsonObject BuildPaneSendKeys(
        string paneId,
        byte[] payload,
        string? leaseId,
        bool keyboard = true)
    {
        var keys = new JsonObject
        {
            ["pane_id"] = paneId,
            ["encoding"] = "base64",
            ["data"] = Convert.ToBase64String(payload),
        };
        if (!string.IsNullOrWhiteSpace(leaseId))
            keys["lease_id"] = leaseId;
        if (!keyboard)
            keys["keyboard"] = false;
        return keys;
    }

    internal static string EnsureAttachClientId(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(live.AttachClientId))
            live.AttachClientId = NewAttachClientId();
        return live.AttachClientId!;
    }

    /// <summary>True when Local is the active endpoint.</summary>
    internal static bool IsLocalEndpointActive(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return live.LocalRoot is null
            || string.IsNullOrWhiteSpace(live.ConnectedPlacementId)
            || string.Equals(live.ConnectedPlacementId, "local", StringComparison.Ordinal);
    }

    /// <summary>
    /// Records a new Local mux connection. The identity goes to
    /// <see cref="AttachLiveState.LocalRoot"/>: client, subscriptions, attach
    /// client id, pane, leases, and port. The live fields describe the active
    /// endpoint. They change only when Local is the active endpoint. A cube
    /// keeps its own identity while the Local connection is replaced.
    /// </summary>
    internal static void ApplyLocalConnectionIdentity(
        AttachLiveState live,
        ControlPlaneClient client,
        LocalConnectionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(identity);
        if (live.LocalRoot is { } localRoot)
        {
            live.LocalRoot = localRoot with
            {
                SourceClient = client,
                SourcePort = identity.Port ?? localRoot.SourcePort,
                ControlSub = identity.ControlSub,
                RenderSub = identity.RenderSub,
                AttachClientId = identity.AttachClientId,
                PaneId = string.IsNullOrWhiteSpace(identity.PaneId) ? localRoot.PaneId : identity.PaneId,
                InputLease = identity.InputLease,
                ResizeLease = identity.ResizeLease,
            };
        }

        if (!IsLocalEndpointActive(live))
            return;

        live.ControlSub = identity.ControlSub;
        live.RenderSub = identity.RenderSub;
        live.AttachClientId = identity.AttachClientId;
        if (!string.IsNullOrWhiteSpace(identity.PaneId))
            live.PaneId = identity.PaneId;
        live.InputLease = identity.InputLease;
        live.ResizeLease = identity.ResizeLease;
        live.Dispatcher.ResizeLease = identity.ResizeLease;
        if (identity.Port is { } port)
        {
            live.Dispatcher.RebindPort(port);
            live.RenderPort = port;
        }
    }

    /// <summary>
    /// Binds the Local client after its hello. The endpoint id is the id of
    /// the Local connection, not the id of an active cube.
    /// </summary>
    internal static void BindLocalAttachConnection(
        AttachLiveState live,
        ControlPlaneClient client,
        ulong generation,
        string? bootId,
        LocalConnectionIdentity? identity)
    {
        var localActive = IsLocalEndpointActive(live);
        var endpointId = localActive
            ? (string.IsNullOrWhiteSpace(live.ConnectedPlacementId) ? "local" : live.ConnectedPlacementId)
            : (string.IsNullOrWhiteSpace(live.LocalRoot?.ConnectedPlacementId) ? "local" : live.LocalRoot!.ConnectedPlacementId!);
        live.Connections.Bind(client, new ConnectionId(endpointId, generation), bootId ?? "");
        if (identity is not null)
            ApplyLocalConnectionIdentity(live, client, identity);
        else if (live.LocalRoot is { } localRoot && !ReferenceEquals(localRoot.SourceClient, client))
            live.LocalRoot = localRoot with { SourceClient = client };
    }

    internal static async Task ActivateLocalAttachSurfaceAsync(
        ControlPlaneClient client,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct,
        LocalConnectionIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(live.AttachClientId))
            throw new InvalidOperationException("local attach has no attach client id");

        var clientId = live.EndpointClientId;
        var geometry = BuildLocalAttachGeometry(live, tty);
        var rpc = new AttachEndpointRpcClient(client);
        // A remote link uses render credit. A local mux connection keeps the
        // delta limit of its receive queue (ApplyAttachReceivePolicy).
        var remoteLink = !(client.UsesUnixSocket && !live.RemoteAttach && !live.RemoteDestination);
        var (welcome, generation) = await rpc.HelloAsync(
                AttachEndpointPreflight.BuildHello(clientId, geometry, surfaceActive: true, renderCredit: remoteLink),
                ct)
            .ConfigureAwait(false);
        if (welcome.Error is not null)
        {
            throw new InvalidOperationException(
                $"local attach hello failed: {welcome.Error.Message}");
        }

        // Endpoint id of this connection. It matches the id the lease and the
        // pump use: the connected placement, else "local". Local is the
        // sidebar cube with id "local" (SidebarCubeCatalog.LocalCubeId).
        var endpointId = string.IsNullOrWhiteSpace(live.ConnectedPlacementId)
            ? "local"
            : live.ConnectedPlacementId;
        BindLocalAttachConnection(live, client, generation, welcome.BootId, identity);

        live.SourceTransportEnvelope.StampServerGeneration(generation);
        live.SourceEndpointBootId = welcome.BootId;
        if (live.LastSnapshotConnectionGeneration is null or 0)
            live.LastSnapshotConnectionGeneration = generation;

        var interest = await rpc.SurfaceInterestAsync(
                new AttachSurfaceInterestRequest
                {
                    RequestId = "local-surface-on",
                    ClientId = clientId,
                    Active = true,
                    GeometryRevision = geometry.GeometryRevision,
                },
                ct)
            .ConfigureAwait(false);
        if (!interest.Active || string.IsNullOrWhiteSpace(interest.LeaseId))
            throw new InvalidOperationException("local attach surface_interest did not activate");

        // Boot and generation are already
        // on this live state. Snapshot boot is applied with the session snapshot
        if (!string.IsNullOrEmpty(welcome.BootId) && generation > 0 && IsLocalEndpointActive(live))
        {
            live.ActiveProjectionEndpointId = endpointId;
            live.SetEndpointSurfaceActive(endpointId, true);
        }
    }

    /// <summary>
    /// Hello and surface interest run before the first chrome
    /// <c>pane.resize</c>. That resize has no controller lease. An active
    /// shell surface admits it. The mux lease check stays in place.
    /// </summary>
    internal static async Task StartLocalAttachChromeAsync(
        ControlPlaneClient client,
        IAttachCommandPort chromePort,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(chromePort);
        ArgumentNullException.ThrowIfNull(live);
        if (!string.IsNullOrWhiteSpace(live.AttachClientId))
        {
            try
            {
                await ActivateLocalAttachSurfaceAsync(client, live, tty, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                live.Status.Set(StatusOwner.Connection, StatusSeverity.Error, ex.Message);
            }
        }

        await RefreshChromeAsync(chromePort, live, tty, ct).ConfigureAwait(false);
    }

    public FocusRedrawPolicy Focus => _focus;

    internal IAttachReconnect ReconnectPort => _reconnect;

    internal AttachReconnectRequest? LastReconnectRequest { get; private set; }

    internal AttachReconnectOffer? LastReconnectOffer { get; private set; }

    internal IFramedSession? FramedSession => _framedClient;

    internal string? LastFramedReconnectJson { get; private set; }

    internal static readonly TimeSpan EscapeFlushTimeout = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan MouseCsiFlushTimeout = TimeSpan.FromMilliseconds(150);

    private int _focusRedrawBusy;

    // Outer TTY focus events are forwarded only from the focused pane's
    // assembled snapshot, where DECSET 1004 is represented by focus_reporting.
    internal enum OuterFocusEvent
    {
        FocusIn,
        FocusOut,
    }

    public async Task<int> RunAsync(MuxReadyInfo ready, MuxAttachRequest request, CancellationToken ct)
    {
        // A restart belongs to the run that asked for it. Callers that do not
        // take it must not hand it to a later attach.
        _restartRequest = null;
        if (request.PrintSnapshotAndExit)
            return await PrintSnapshotAsync(ready, ct).ConfigureAwait(false);

        if (OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync(
                    "hypa attach: Windows TTY attach is not supported.")
                .ConfigureAwait(false);
            return 1;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            await Console.Error.WriteLineAsync(
                    "hypa attach: Unix TTY attach requires Linux or macOS.")
                .ConfigureAwait(false);
            return 1;
        }

        return await RunTtyAsync(ready, request, ct).ConfigureAwait(false);
    }

    internal static AttachConfigResult<ThemeRuntime> CreateThemeRuntime(AttachClientConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return ThemeRuntime.FromConfig(config.Theme, config.Ui.Accent);
    }

    /// <summary>
    /// <c>logging::shutdown("client")</c>. Hypa opens the attach process
    /// log first because attach owns UI keys, then records a redacted
    /// failure and error detach/disconnect.
    /// </summary>
    internal static async Task<AttachTtyPrelude> OpenAttachThenValidateAsync(
        string socketPath,
        int pid,
        bool remoteAttach,
        string session,
        AttachClientConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var attachLog = ProcessLogSinkFactory.OpenAttach(socketPath, pid, remoteAttach);
        var compiled = KeysConfigMapper.Compile(config.Keys);
        if (!compiled.IsOk)
        {
            var err = compiled.Error.ToString();
            RecordStartupFailure(attachLog, "run", session, attachClientId: null, err);
            RecordSessionEnd(attachLog, session, null, "run", failed: true);
            await Console.Error.WriteLineAsync($"hypa attach: {compiled.Error}").ConfigureAwait(false);
            return new AttachTtyPrelude { AttachLog = attachLog, ExitCode = 1 };
        }

        var theme = CreateThemeRuntime(config);
        if (!theme.IsOk)
        {
            var err = theme.Error.ToString();
            RecordStartupFailure(attachLog, "run", session, attachClientId: null, err);
            RecordSessionEnd(attachLog, session, null, "run", failed: true);
            await Console.Error.WriteLineAsync($"hypa attach: {theme.Error}").ConfigureAwait(false);
            return new AttachTtyPrelude
            {
                AttachLog = attachLog,
                Table = compiled.Value,
                ExitCode = 1,
            };
        }

        return new AttachTtyPrelude
        {
            AttachLog = attachLog,
            Table = compiled.Value,
            Theme = theme.Value,
            ExitCode = 0,
        };
    }

    /// <summary>
    /// <c>attach --once</c> stdout. Install smoke greps session, ping
    /// <c>ok:true</c>, and <c>protocol:1</c> from this block. Print ping
    /// only after a successful snapshot.
    /// </summary>
    internal static void WriteOnceStdout(MuxReadyInfo ready, string snapshotJson, TextWriter output)
    {
        output.WriteLine($"session={ready.Session}");
        output.WriteLine($"socket={ready.SocketPath}");
        output.WriteLine(ready.PingJson);
        output.WriteLine(snapshotJson);
    }

    internal async Task<int> PrintSnapshotAsync(MuxReadyInfo ready, CancellationToken ct)
    {
        try
        {
            await using var client = CreateClient(ready);
            await client.ConnectAsync(ct).ConfigureAwait(false);
            var snap = await EnsurePaneAndSnapshotAsync(client, ct).ConfigureAwait(false);
            WriteOnceStdout(ready, snap.GetRawText(), Console.Out);
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"hypa attach: snapshot failed: {ex.Message}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    private async Task<int> RunTtyAsync(MuxReadyInfo ready, MuxAttachRequest request, CancellationToken ct)
    {
        UnixRawTerminal? tty = null;
        ControlPlaneClient? render = null;
        ControlPlaneClient? control = null;
        AttachLiveState? liveState = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runCt = linked.Token;
        var assembler = new SnapshotAssembler();
        var controlGate = new SemaphoreSlim(1, 1);
        string? paneId = null;
        string? inputLease = null;
        string? resizeLease = null;
        string? renderSub = null;
        string? controlSub = null;
        string? attachClientId = NewAttachClientId();
        AttachLiveState live;
        KeyEngine engine;
        KeyBindingTable table;
        ThemeRuntime themeRuntime;

        void TearDownSockets()
        {
            var current = liveState?.ControlSlot?.Client;
            if (liveState is not null)
            {
                foreach (var torn in new[] { current, render, control })
                {
                    if (torn is not null)
                        liveState.Connections.Unbind(torn);
                }
            }

            try { current?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* ignore */ }
            if (render is not null && !ReferenceEquals(render, current))
            {
                try { render.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* ignore */ }
            }
            if (control is not null
                && !ReferenceEquals(control, render)
                && !ReferenceEquals(control, current))
            {
                try { control.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* ignore */ }
            }
            render = null;
            control = null;
            if (liveState is not null)
                liveState.ControlSlot = null;
        }

        List<IDisposable> signals = [];
        string? detachReason = null;
        var stage = "connect";
        var startupFailed = false;
        var sessionFinalized = false;
        IProcessLogSink attachLog = NullProcessLogSink.Instance;
        try
        {
            var prelude = await OpenAttachThenValidateAsync(
                    ready.SocketPath,
                    Environment.ProcessId,
                    request.RemoteAttach,
                    ready.Session,
                    request.AttachConfig)
                .ConfigureAwait(false);
            attachLog = prelude.AttachLog;
            if (prelude.ExitCode != 0)
            {
                sessionFinalized = true;
                return prelude.ExitCode;
            }

            table = prelude.Table!;
            themeRuntime = prelude.Theme!;
            engine = new KeyEngine(
                table,
                chrome: AttachChromePolicy.FromUi(request.AttachConfig.Ui),
                settingsPages: SettingsPageRegistry.Product());

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
                {
                    if (liveState is { } state)
                        state.ChromeEnabled = false;
                    try { tty?.Restore(); }
                    catch { /* restore before cancel */ }
                    ctx.Cancel = true;
                    linked.Cancel();
                }));
                signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
                {
                    if (liveState is { } state)
                        state.ChromeEnabled = false;
                    try { tty?.Restore(); }
                    catch { /* restore before cancel */ }
                    ctx.Cancel = true;
                    linked.Cancel();
                }));
                signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
                {
                    if (liveState is { } state)
                        state.ChromeEnabled = false;
                    try { tty?.Restore(); }
                    catch { /* restore before cancel */ }
                    ctx.Cancel = true;
                    linked.Cancel();
                }));
            }

            render = CreateClient(ready);
            await render.ConnectAsync(runCt).ConfigureAwait(false);
            control = render;
            AttachProcessLog.Connect(attachLog, ready.Session);

            stage = "snapshot";
            var snap = await EnsurePaneAndSnapshotAsync(control, runCt).ConfigureAwait(false);
            // src/client/startup.rs:8-15 enters run_terminal_attach.
            paneId = ChooseFocusedPaneId(snap);
            if (!string.IsNullOrWhiteSpace(request.FocusPaneId)
                && IsAlivePane(snap, request.FocusPaneId))
            {
                paneId = request.FocusPaneId;
                if (!string.Equals(ChooseFocusedPaneId(snap), paneId, StringComparison.Ordinal))
                {
                    await control.CallAsync(
                            ProtocolMethods.PaneFocus,
                            new JsonObject { ["pane_id"] = paneId },
                            runCt)
                        .ConfigureAwait(false);
                }
            }

            if (string.IsNullOrWhiteSpace(paneId))
            {
                startupFailed = true;
                detachReason = "no-live-pane";
                RecordStartupFailure(attachLog, "snapshot", ready.Session, attachClientId, "no live pane");
                await Console.Error.WriteLineAsync("hypa attach: no live pane after spawn.")
                    .ConfigureAwait(false);
                return 1;
            }

            AttachProcessLog.Snapshot(attachLog, ready.Session);

            stage = "subscribe";
            inputLease = "";
            resizeLease = "";
            var recoveredSnap = false;
            var subscribed = await SubscribeAsync(
                    control,
                    ["control", "lifecycle", "render"],
                    runCt,
                    onSnapshot: refreshed =>
                    {
                        snap = refreshed;
                        recoveredSnap = true;
                    })
                .ConfigureAwait(false);
            if (recoveredSnap)
            {
                var focused = ChooseFocusedPaneId(snap);
                if (!string.IsNullOrWhiteSpace(focused))
                    paneId = focused;
            }
            controlSub = subscribed.SubscriptionId;
            if (!string.IsNullOrWhiteSpace(subscribed.AttachClientId))
                attachClientId = subscribed.AttachClientId;
            AttachProcessLog.Subscribe(attachLog, ready.Session, attachClientId, controlSub);
            stage = "run";
            // Observe, do not attach_terminal_client.
            await ObserveAsync(control, paneId, controlSub, runCt).ConfigureAwait(false);
            _ = control.DrainPendingEvents();

            var commandPort = new ControlPlaneAttachCommandPort(control);
            var dispatcher = new AttachCommandDispatcher(
                commandPort,
                ChooseWorkspaceIdForPane(snap, paneId) ?? ChooseFocusedWorkspaceId(snap),
                ChooseTabIdForPane(snap, paneId) ?? ChooseFocusedTabId(snap),
                paneId,
                resizeLease,
                request.AttachConfig.Ui.AgentPanelSort)
            {
                Commands = request.RemoteAttach ? [] : table.Commands,
                DetachedLauncher = new UnixDetachedCommandLauncher(),
                SocketPath = ready.SocketPath,
                BinPath = Environment.ProcessPath,
            };
            LeaseRenewLoop renew = null!;
            renew = new LeaseRenewLoop(
                async (leaseId, ttl, token) =>
                {
                    await controlGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (!renew.IsTracked(leaseId))
                            return;
                        try
                        {
                            var renewParams = new JsonObject { ["lease_id"] = leaseId, ["ttl_ms"] = ttl };
                            if (liveState is { } modeLive
                                && string.Equals(leaseId, modeLive.InputLease, StringComparison.Ordinal))
                            {
                                renewParams["client_mode"] =
                                    AttachClientModePublication.PublishedToken(modeLive);
                            }

                            var liveControl = liveState is { } liveNow
                                ? LiveControlClient(liveNow, control) ?? control
                                : control;
                            await liveControl.CallAsync(
                                    ProtocolMethods.RuntimeLeaseRenew,
                                    renewParams,
                                    token)
                                .ConfigureAwait(false);
                            if (liveState is { } renewLive)
                            {
                                await DrainOverlayEventsAsync(
                                        liveControl, renewLive, tty, token, linked)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                _ = liveControl.DrainPendingEvents();
                            }
                        }
                        catch (ControlPlaneException ex) when (LeaseRenewLoop.IsStaleLease(ex))
                        {
                            renew.Untrack(leaseId);
                        }
                    }
                    finally
                    {
                        controlGate.Release();
                    }
                },
                _time);
            renew.Track(inputLease);
            renew.Track(resizeLease);
            live = new AttachLiveState
            {
                Engine = engine,
                Table = table,
                Dispatcher = dispatcher,
                Renew = renew,
                SessionName = ready.Session,
                HomeSessionName = ready.Session,
                WorkspaceId = dispatcher.WorkspaceId,
                TabId = dispatcher.TabId,
                AttachClientId = attachClientId,
                PaneId = paneId,
                InputLease = inputLease,
                ResizeLease = resizeLease!,
                ControlSub = controlSub!,
                RenderSub = controlSub,
                RenderPort = commandPort,
                WakeRender = render.WakeRead,
                Ui = request.AttachConfig.Ui,
                Theme = themeRuntime,
                ChromeEnabled = true,
                Time = _time,
                Latency = _latency,
                Prediction = new EchoPredictor(
                    _time,
                    EchoDisplayPolicies.For(request.AttachConfig.Experimental.PredictiveEcho),
                    _latency),
                Hostname = Environment.MachineName,
                WindowTitleOverride = ReadWindowTitleOverride(snap),
                Notifications = Notification.NotificationDirector.CreateProduction(
                    request.AttachConfig.Ui,
                    _time),
                ConfigLoader = request.ConfigLoader,
                AttachConfig = request.AttachConfig,
                CjkIme = CjkImeRevealFilter.From(request.AttachConfig.Experimental),
                PrefixAsciiSource = PlatformPrefixAsciiInputSource.Create(),
                ControlSlot = new AttachControlSlot { Client = control },
                CubesConnect = _cubesConnect
                    ?? (_cubesResolver is null
                        ? null
                        : new CubesConnectRetargetService(_cubesResolver)),
                MoveWork = _moveWork,
                DestExecutorFactory = _destExecutorFactory,
                SourceMuxSocketPath = ready.SocketPath,
                UpdateNotices = _updateNotices,
                SourceAlive = string.IsNullOrWhiteSpace(ready.SocketPath)
                    ? null
                    : new UnixMuxAliveProbe(ready.SocketPath),
                ConnectedPlacementId = string.IsNullOrWhiteSpace(request.PlacementId)
                    ? null
                    : request.PlacementId.Trim(),
                CubesConnectAction = CubesConnectActions.Noop,
                RemoteDestination = request.RemoteDestination,
                RemoteAttach = request.RemoteAttach,
                PlacementDisplayName = request.PlacementDisplayName,
                PlacementKind = request.PlacementKind,
                Release = _release,
                CubeCatalog = _cubeCatalog,
                HostReach = _hostReach,
                PlacementStoreDir = _operatorPaths?.PlacementDirectory,

                ProcessLog = attachLog,
                RequestedConnectPlacementId = string.IsNullOrWhiteSpace(request.ConnectPlacementId)
                    ? null
                    : request.ConnectPlacementId.Trim(),

            };
            // The sender binds each batch seq to the predictions of its keys.
            _latency.SeqObserver = live.Prediction;
            var loggingPort = new LoggingAttachCommandPort(commandPort, live);
            dispatcher.RebindPort(loggingPort);
            live.RenderPort = loggingPort;
            StartPlacementCatalogReloader(live);
            live.ReconnectPort = _reconnect;
            live.Snapshots = assembler;
            live.PrefixAscii.Enabled = request.AttachConfig.Experimental.SwitchAsciiInputSourceInPrefix;
            live.Engine.Settings.Bind(live.Theme, live.Ui);
            live.Notifications.SetFocus(dispatcher.TabId, paneId);
            dispatcher.LastNotificationPaneId = live.Notifications.LastTargetPaneId;
            HydratePaneRightClick(live, snap);
            ApplyPopupFromSnapshot(live, snap);
            liveState = live;
            // enter the command lane for this client. The lane stays on live.
            commandPort.UseShellLane(live);
            try
            {
                var health = await control.CallAsync(ProtocolMethods.RuntimeHealth, null, runCt)
                    .ConfigureAwait(false);
                live.ApplyProcessVtHealth(health);
            }
            catch
            {
                // Fail closed to Ghostty. Production Ghostty panes implement
                // IPaneVtSnapshot. Chrome live bytes never CUP-remap onto
                // the host TTY.
                live.NoteProcessVtProvider(VtFloorDefaults.Provider);
            }

            tty = new UnixRawTerminal();
            tty.EnterRaw();
            tty.EnterClientOverlay();
            tty.EnableFocusReport();
            tty.EnableBracketedPaste();
            live.SidebarOpen = SidebarOpenAtAttach(live.Ui);
            live.SidebarCollapsed = !live.SidebarOpen;
            live.SidebarRequestedWidth = live.Ui.SidebarWidth;
            live.SidebarWidth = live.Ui.SidebarWidth;
            RestoreSidebarSectionSplit(live);
            live.Mouse.Configure(MouseEngineOptions.FromUi(live.Ui, live.Ui.MouseCapture));
            tty.EnableMouseCapture(live.Ui.MouseCapture);
            TryStartStartupSplash(live);
            if (live.Splash is not null)
                PaintChrome(tty, live);
            ApplyOnboardingAtStart(live, request.AttachConfig);
            ApplyPackNotesAtStart(live, request.AttachConfig);
            await ReportAttachClientModeAsync(
                    new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control), live, runCt)
                .ConfigureAwait(false);
            var localClient = LiveControlClient(live, control);
            var chromePort = new ControlPlaneAttachCommandPort(localClient ?? control);
            if (localClient is not null)
            {
                await StartLocalAttachChromeAsync(localClient, chromePort, live, tty, runCt)
                    .ConfigureAwait(false);
            }
            else
            {
                await RefreshChromeAsync(chromePort, live, tty, runCt).ConfigureAwait(false);
            }

            live.Splash?.NoteMuxReady();
            PaintChrome(tty, live);
            BeginReconnectSession(live);

            var seedCols = live.Chrome?.FocusedContent?.Cols ?? 0;
            var seedRows = live.Chrome?.FocusedContent?.Rows ?? 0;
            if (seedCols < 1 || seedRows < 1)
            {
                if (tty.TryGetSize(out var localCols, out var localRows))
                {
                    seedCols = localCols;
                    seedRows = localRows;
                }
            }

            if (seedCols > 0 && seedRows > 0)
            {
                live.ResetAppliedBlit(paneId);
                await ResizeAsync(
                        control, controlGate, paneId, resizeLease, seedCols, seedRows, runCt,
                        ignoreErrors: true)
                    .ConfigureAwait(false);
            }

            // Restore dest connect after local attach is live. Do not
            // await: a dest timeout must not hold splash or abort attach.
            var restoreControl = new ControlPlaneAttachCommandPort(
                localClient ?? control);
            _ = MaybeRestoreSelectedCubeConnectAsync(
                restoreControl, live, tty, runCt);
            _ = MaybeBeginRequestedCubeConnectAsync(
                restoreControl, live, tty, runCt);

            renderSub = controlSub;
            live.RenderSub = renderSub;

            _catalog.WriteAttachState(ready.Session, new MuxAttachClientState(
                true, ready.Session, paneId, inputLease, resizeLease, Connections: 1));
            live.RenderSub = renderSub;
            ApplyAttachReceivePolicy(live, render);

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var resizeLane = CreateHostResizeLane(() => control, controlGate, live, tty, runCt);
                signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGWINCH, ctx =>
                {
                    ctx.Cancel = true;
                    tty.RefreshKittyKeyboard();
                    if (tty.TryGetSize(out var winCols, out var winRows))
                        resizeLane.Request(winCols, winRows);
                }));
            }

            CancellationTokenSource? attemptCts = null;
            AttachInputSender? inputSender = null;
            AttachStdinProducer? stdinProducer = null;
            var hostThemeQueried = false;
            Task renderTask = Task.CompletedTask;
            Task inputTask = Task.CompletedTask;
            Task renewTask = Task.CompletedTask;
            Task beatTask = Task.CompletedTask;
            Task tabBarTask = Task.CompletedTask;
            var keptEndpointRestart = false;
            try
            {
                while (!runCt.IsCancellationRequested)
                {
                    if (control is null || render is null)
                        break;

                    attemptCts?.Dispose();
                    attemptCts = CancellationTokenSource.CreateLinkedTokenSource(runCt);
                    var attemptCt = attemptCts.Token;

                    if (inputSender is not null)
                    {
                        await inputSender.DisposeAsync().ConfigureAwait(false);
                        inputSender = null;
                    }

                    if (stdinProducer is not null)
                    {
                        await stdinProducer.DisposeAsync().ConfigureAwait(false);
                        stdinProducer = null;
                    }

                    inputSender = new AttachInputSender(
                        LiveControlClient(live, control) ?? control,
                        () => (live.PaneId, live.InputLease),
                        onFault: fault =>
                            ApplyInputSenderFault(live, fault, attemptCts, linked),
                        blocksForward: () => BlocksPaneKeys(live),
                        latency: live.Latency);
                    live.InputSender = inputSender;
                    renderTask = ReadRenderAsync(
                        render, control, controlGate, assembler, tty, live, linked, attemptCt);
                    if (!keptEndpointRestart)
                    {
                        await ObserveVisiblePanesAsync(
                                commandPort, live, attemptCt)
                            .ConfigureAwait(false);
                    }

                    keptEndpointRestart = false;
                    // before the main event loop waits. Do not call blocking
                    // PollReadable on this supervision thread.
                    stdinProducer = AttachStdinProducer.Start(0, attemptCt, clock: live.Latency.Now);
                    // then writes the host theme query. Do not query first.
                    if (!hostThemeQueried)
                    {
                        WriteHostStartQueries(tty, live);
                        hostThemeQueried = true;
                    }
                    else
                    {
                        // A reconnect keeps the host TTY. Set the kitty flags
                        // again, as after focus-in.
                        tty.RefreshKittyKeyboard();
                    }

                    inputTask = ReadInputAsync(
                        tty, live, control, controlGate, linked, attemptCt, stdinProducer);
                    renewTask = renew.RunAsync(attemptCt);
                    beatTask = ControlHeartbeatAsync(control, controlGate, live, tty, linked, attemptCt);
                    tabBarTask = TabBarTickLoopAsync(live, tty, attemptCt, controlGate);

                    AttachSemanticActionHost? actionHost = null;
                    EndpointConnectionSupervisor? endpointConnections = null;
                    Task finished;
                    try
                    {
                        actionHost = TryStartSemanticActionHost(
                            live, control, tty, controlGate, attemptCt);
                        endpointConnections = StartEndpointConnectionSupervisor(
                            live, control, tty, controlGate, attemptCt);
                        // A failed host write on the writer thread ends the
                        // attach too, also when no paint follows it.
                        finished = await Task.WhenAny(
                                renderTask, inputTask, renewTask, beatTask, tabBarTask, tty.HostWriteFaulted)
                            .ConfigureAwait(false);
                        TraceLoopFault(live, finished, renderTask, inputTask, renewTask, beatTask, tabBarTask);
                        try { await finished.ConfigureAwait(false); }
                        catch (TtyDisconnectException ex) when (IsCleanTtyDetach(ex))
                        {
                            detachReason = "tty hangup";
                        }
                        catch (OperationCanceledException) when (runCt.IsCancellationRequested)
                        {
                            // detach
                        }
                        catch (OperationCanceledException) when (
                            live.InputStallRequested && !live.DetachRequested)
                        {
                            detachReason ??= "input sender fault";
                        }
                        catch (IOException ex) when (IsCleanTtyDetach(ex))
                        {
                            detachReason = "connection closed";
                        }
                        catch (ObjectDisposedException ex) when (IsCleanTtyDetach(ex))
                        {
                            detachReason = "connection closed";
                        }
                        catch (ControlPlaneException ex) when (IsCleanTtyDetach(ex))
                        {
                            detachReason = "control plane closed";
                        }
                        catch (ControlPlaneClientTimeoutException ex) when (IsCleanTtyDetach(ex))
                        {
                            detachReason = "control ping timed out";
                        }
                        catch (InvalidOperationException ex) when (IsCleanTtyDetach(ex))
                        {
                            detachReason = "connection closed";
                        }
                    }
                    finally
                    {
                        if (endpointConnections is not null)
                            await endpointConnections.DisposeAsync().ConfigureAwait(false);
                        if (actionHost is not null)
                            await actionHost.DisposeAsync().ConfigureAwait(false);
                    }

                    if (live.InputStallRequested && !live.DetachRequested)
                        detachReason ??= "input sender fault";

                    // A failed host write is a lost host terminal. The host
                    // writer keeps the failure, so a reconnect cannot paint:
                    // end the attach as a hangup, not as a lost connection.
                    if (tty.HostWriteFaulted.IsFaulted)
                        detachReason = "tty hangup";

                    detachReason ??= DescribeAttachExit(
                        finished,
                        inputTask,
                        renderTask,
                        renewTask,
                        beatTask,
                        tabBarTask,
                        live.DetachRequested);

                    var loss = AttachLossPolicy.Classify(live.DetachRequested, detachReason);
                    // end the local client.
                    if (AttachLossPolicy.ShouldReconnect(loss)
                        && !live.DetachRequested
                        && KeepCommittedEndpointAfterStall(
                            live,
                            control!,
                            render!,
                            detachReason ?? "",
                            finished.Exception?.GetBaseException()))
                    {
                        attemptCts.Cancel();
                        try
                        {
                            await Task.WhenAll(
                                    Drain(renderTask),
                                    Drain(inputTask),
                                    Drain(renewTask),
                                    Drain(beatTask),
                                    Drain(tabBarTask))
                                .ConfigureAwait(false);
                        }
                        catch { /* ignore */ }

                        await inputSender.DisposeAsync().ConfigureAwait(false);
                        inputSender = null;
                        live.InputSender = null;
                        if (stdinProducer is not null)
                        {
                            await stdinProducer.DisposeAsync().ConfigureAwait(false);
                            stdinProducer = null;
                        }

                        live.InputStallRequested = false;
                        detachReason = null;
                        keptEndpointRestart = true;
                        continue;
                    }

                    if (!AttachLossPolicy.ShouldReconnect(loss)
                        || live.DetachRequested
                        || BlocksPresentationInput(live))
                        break;
                    RecordAttachStall(
                        live, detachReason, finished, renderTask, inputTask, renewTask, beatTask, tabBarTask);
                    if (_stallReconnects >= AttachReconnectLimits.MaxReconnectAttempts)
                    {
                        detachReason ??= "reconnect failed";
                        break;
                    }

                    live.Reconnecting = true;
                    live.Status.Set(StatusOwner.Transport, StatusSeverity.Error, FormatStallCopy(live));
                    PaintChrome(tty, live);

                    attemptCts.Cancel();
                    try
                    {
                        await Task.WhenAll(
                                Drain(renderTask),
                                Drain(inputTask),
                                Drain(renewTask),
                                Drain(beatTask),
                                Drain(tabBarTask))
                            .ConfigureAwait(false);
                    }
                    catch { /* ignore */ }

                    await inputSender.DisposeAsync().ConfigureAwait(false);
                    inputSender = null;
                    live.InputSender = null;
                    if (stdinProducer is not null)
                    {
                        await stdinProducer.DisposeAsync().ConfigureAwait(false);
                        stdinProducer = null;
                    }

                    TearDownSockets();

                    var window = await ReconnectInStallWindowAsync(
                            ready,
                            commandPort,
                            live,
                            assembler,
                            tty,
                            controlGate,
                            renew,
                            runCt)
                        .ConfigureAwait(false);
                    if (window.Detached || window.Reconnect is not { } recovered)
                    {
                        // The detach key or the end of stdin, while the reconnect ran.
                        live.DetachRequested = window.ExitReason is null;
                        detachReason = window.ExitReason;
                        break;
                    }

                    if (!recovered.Ok || recovered.Render is null || recovered.Control is null)
                    {
                        detachReason = recovered.Detail ?? "reconnect failed";
                        break;
                    }

                    render = recovered.Render;
                    control = recovered.Control;
                    _stallReconnects++;
                    live.Reconnecting = false;
                    live.Status.Clear(StatusOwner.Transport);
                    live.InputStallRequested = false;
                    detachReason = null;
                    PaintChrome(tty, live);
                }
            }
            finally
            {
                try { attemptCts?.Cancel(); }
                catch { /* ignore */ }
                Task destShutdown = Task.CompletedTask;
                try { destShutdown = ShutdownDestConnectPrepAsync(live); }
                catch { /* ignore */ }
                try
                {
                    await Task.WhenAll(
                            Drain(renderTask),
                            Drain(inputTask),
                            Drain(renewTask),
                            Drain(beatTask),
                            Drain(tabBarTask),
                            Drain(destShutdown))
                        .ConfigureAwait(false);
                }
                catch { /* ignore */ }
                if (inputSender is not null)
                {
                    try { await inputSender.DisposeAsync().ConfigureAwait(false); }
                    catch { /* ignore */ }
                }

                if (stdinProducer is not null)
                {
                    try { await stdinProducer.DisposeAsync().ConfigureAwait(false); }
                    catch { /* ignore */ }
                }

                attemptCts?.Dispose();
            }

            live.ChromeEnabled = false;
            try { await live.TabBarCommands.DrainAsync().ConfigureAwait(false); }
            catch { /* display-only */ }
            try { tty.Restore(); }
            catch { /* restore before Drain so a stuck stdin cannot leave raw mode */ }
            TearDownSockets();
            InterruptStdin();
            linked.Cancel();

            if (!engine.WantsServerStop)
                CalledServerStop = false;
            if (live.MuxRestartRequested)
            {
                _restartRequest = new MuxRestartRequest(
                    live.HomeSessionName ?? ready.Session,
                    live.SourceMuxSocketPath ?? ready.SocketPath);
            }

            return 0;
        }
        catch (OperationCanceledException ex)
        {
            if (TryRecordStartupCancellation(
                    attachLog,
                    stage,
                    ready.Session,
                    liveState?.AttachClientId ?? attachClientId,
                    ex.Message,
                    liveSessionEstablished: liveState is not null,
                    out var cancelReason))
            {
                startupFailed = true;
                detachReason ??= cancelReason;
            }

            return 0;
        }
        catch (Exception ex)
        {
            startupFailed = true;
            detachReason ??= stage + "-error";
            RecordStartupFailure(
                attachLog,
                stage,
                ready.Session,
                liveState?.AttachClientId ?? attachClientId,
                ex.Message);
            try { await Console.Error.WriteLineAsync($"hypa attach: {ex.Message}").ConfigureAwait(false); }
            catch { /* ignore */ }
            return 1;
        }
        finally
        {
            foreach (var s in signals)
            {
                try { s.Dispose(); }
                catch { /* ignore */ }
            }

            try
            {
                if (!sessionFinalized)
                {
                    RecordSessionEnd(
                        attachLog,
                        ready.Session,
                        liveState?.AttachClientId ?? attachClientId,
                        detachReason,
                        startupFailed);
                }
            }
            catch
            {
            }

            // A prefix detach has no LeaveMode event, so restore the prefix
            // input here. The terminal is restored before the log drain.
            RestoreTerminalThenDrainLog(liveState, tty, attachLog);

            if (liveState?.CatalogReloader is IAsyncDisposable catalogReloader)
            {
                try { catalogReloader.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* ignore */ }
            }

            if (liveState?.SshEndpoints is { } sshEndpoints)
                sshEndpoints.RetireExcept(new HashSet<string>(StringComparer.Ordinal));

            if (liveState?.PendingActivation is not null)
            {
                lock (liveState.ActivationGate)
                {
                    ClearPendingActivationWithoutActivated(liveState);
                }
            }

            if (liveState?.HealthMonitor is { } monitor)
            {
                try { monitor.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* session is ending */ }
            }

            TearDownSockets();
            _catalog.ClearAttachState(ready.Session);

            try
            {
                Console.WriteLine();
                Console.WriteLine(FormatLiveDetachBanner(detachReason, request, liveState));
            }
            catch
            {
                // ignore
            }
        }
    }

    internal static bool IsCleanTtyDetach(Exception ex) =>
        ex is TtyDisconnectException
        or IOException
        or OperationCanceledException
        or ObjectDisposedException
        or InvalidOperationException
        or ControlPlaneException
        or ControlPlaneClientTimeoutException;

    internal static bool IsInputEof(int n) => n == 0;

    internal const string DetachBanner = AttachLossPolicy.LocalDetachBanner;

    internal static string FormatDetachBanner(string? reason) =>
        FormatDetachBanner(reason, remoteDestination: false);

    internal static string FormatLiveDetachBanner(
        string? reason,
        MuxAttachRequest request,
        AttachLiveState? live = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var remote = live?.RemoteDestination ?? request.RemoteDestination;
        var name = live?.PlacementDisplayName ?? request.PlacementDisplayName;
        var kind = live?.PlacementKind ?? request.PlacementKind;
        if (live is not null)
            ResolvePlacementCopy(live, ref remote, ref name, ref kind);
        return AttachLossPolicy.FormatBanner(reason, remote, name, kind);
    }

    internal static string FormatDetachBanner(
        string? reason,
        bool remoteDestination,
        string? displayName = null,
        SidebarCubeKind? kind = null) =>
        AttachLossPolicy.FormatBanner(reason, remoteDestination, displayName, kind);

    internal static string FormatStallCopy(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var remote = live.RemoteDestination;
        var name = live.PlacementDisplayName;
        var kind = live.PlacementKind;
        ResolvePlacementCopy(live, ref remote, ref name, ref kind);
        return AttachLossPolicy.FormatStall(remote, name, kind);
    }

    internal static void ApplyPlacementCopy(AttachLiveState live, SidebarCubeItem cube)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(cube);
        live.ConnectedPlacementId = cube.Id;
        live.PlacementDisplayName = cube.Name;
        live.PlacementKind = cube.Kind;
        live.RemoteDestination = AttachLossPolicy.IsRemoteDestination(false, cube.Kind);
        live.ConnectedSshPlacement = IsSshCube(cube);
        if (cube.Kind == SidebarCubeKind.Local)
        {
            live.ActiveMuxSocketPath = null;
            live.ConnectedSshPlacement = false;
        }
    }

    private void StartPlacementCatalogReloader(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (_cubeCatalog is null)
            return;
        if (!ProcessLocalOperatorIdentity.TryResolve(out var actor))
            return;

        live.SshEndpoints = new SshPlacementEndpointRegistry();
        live.CatalogReloader = new PlacementCatalogReloader(_cubeCatalog, actor, live.SshEndpoints);
        live.CatalogReloader.Changed += (items, state) =>
        {
            string? retiredPlacementId = null;
            if (live.ConnectedPlacementId is { } connected
                && !items.Any(item => item.Id == connected && item.ConnectEnabled))
            {
                retiredPlacementId = connected;
            }

            live.QueueCatalogReload(items, state, retiredPlacementId);
        };
        live.CatalogReloader.Start();
    }

    /// <summary>
    /// supervisors on the same client loop before freeze.
    /// after <c>supervisor.rs:187-199</c> <c>record_status</c> on that loop.
    /// Peek the queued catalog without consuming chrome paint. Do not wait
    /// on <c>controlGate</c>. A later dest success must not install.
    /// </summary>
    internal static void ApplyPendingCatalogFence(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.TryPeekCatalogReload(out var items, out _, out var retiredPlacementId))
            return;

        var freezeOwner = retiredPlacementId is not null
            && string.Equals(live.ConnectedPlacementId, retiredPlacementId, StringComparison.Ordinal);
        if (freezeOwner)
            ApplyUnavailableOwnerFreeze(live);
        else
            RetireDestConnectIfCatalogStale(live, items);
    }

    internal static bool TryFlushCatalogReloadPaint(
        AttachLiveState live,
        UnixRawTerminal? tty,
        SemaphoreSlim? controlGate = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.HasCatalogReloadPending)
            return false;
        if (controlGate is not null && !controlGate.Wait(0))
        {
            ApplyPendingCatalogFence(live);
            return false;
        }

        try
        {
            if (!live.TakeCatalogReload(out var items, out var state, out var retiredPlacementId))
                return false;

            // record_status cannot apply Connected (supervisor.rs:187-199).
            // Take ActivationGate before paint. Paint then ActivationGate
            // inverts CompleteEndpointActivation (ActivationGate then paint).
            var freezeOwner = retiredPlacementId is not null
                && string.Equals(live.ConnectedPlacementId, retiredPlacementId, StringComparison.Ordinal);
            if (freezeOwner)
                ApplyUnavailableOwnerFreeze(live);
            else
                RetireDestConnectIfCatalogStale(live, items);

            live.WithPaint(() =>
            {
                live.Cubes = items;
                live.CubesState = state;
                BindConnectedPlacementIdentity(live);
                BindSelectedCubeIdentity(live);

                if (retiredPlacementId is not null
                    && !items.Any(item => string.Equals(item.Id, retiredPlacementId, StringComparison.Ordinal)))
                {
                    live.ClientViewHints.Discard(retiredPlacementId);
                }

                if (live.LastSnapshot is { } snap)
                    RebuildSidebar(live, snap, requestGit: false, hydratePicker: false);
                RecomputeLiveChromeFromSidebar(live);
                live.InvalidateChrome();
            });
            if (tty is not null && live.ChromeEnabled)
                PaintChrome(tty, live);
            return true;
        }
        finally
        {
            controlGate?.Release();
        }
    }

    /// <c>Activated</c>. Hypa reads pane geometry with <c>layout.export</c>.
    /// One shot, off <c>ActivationGate</c> and off <c>WithPaint</c>.
    internal static async Task<bool> TryFlushCommitChromeRefreshAsync(
        AttachLiveState live,
        UnixRawTerminal? tty,
        SemaphoreSlim? controlGate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.CommitChromeRefreshPending)
            return false;
        if (live.PendingActivation is not null)
            return false;
        if (live.DestConnectAttempt is not null)
            return false;
        if (controlGate is not null && !controlGate.Wait(0))
            return false;

        try
        {
            IAttachCommandPort? port = live.RenderPort;
            if (port is null && live.ControlSlot?.Client is { } client)
                port = new ControlPlaneAttachCommandPort(client);
            if (port is null)
                return false;

            live.CommitChromeRefreshPending = false;
            await RefreshChromeAsync(port, live, tty, ct).ConfigureAwait(false);
            return true;
        }
        catch (ControlPlaneException ex)
        {
            live.Status.Set(StatusOwner.Connection, StatusSeverity.Error, FormatStatus(ex));
            return false;
        }
        catch (InvalidOperationException ex)
        {
            live.Status.Set(StatusOwner.Connection, StatusSeverity.Error, FormatStatus(ex));
            return false;
        }
        finally
        {
            controlGate?.Release();
        }
    }

    /// <summary>
    /// <c>supervisors.reconcile_profiles</c> before freeze so a fenced
    /// generation cannot apply Connected. Hypa dest prep is
    /// <c>AttachDestConnectAttempt</c>. Retire that attempt under
    /// ActivationGate, then freeze. Connect retarget rollback uses this
    /// when target-off or source restore cannot complete safely.
    /// </summary>
    internal static void ApplyUnavailableOwnerFreeze(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var connected = live.ConnectedPlacementId;
        lock (live.ActivationGate)
        {
            RetireDestConnectAttemptLocked(live);
            AbortPendingActivationForUnavailableOwner(live);
            live.PlacementOwnerUnavailable = true;
            live.PresentationFrozen = true;
            live.ActiveMuxSocketPath = null;
            live.InputSender?.DiscardQueued();
            live.ClearActivationCapturedFramesCore();
            live.Status.Set(StatusOwner.Connection, StatusSeverity.Error, AttachEndpointUserCopy.Unavailable);
            live.FrozenChromePaintArmed = true;
            live.HostEncoder.RequestRepaint();
        }

        if (connected is not null)
            live.SshEndpoints?.Retire(connected);
    }

    internal static bool BlocksPresentationInput(AttachLiveState live) =>
        live.InputFrozen || live.PlacementOwnerUnavailable;

    /// <summary>
    /// Pane keys must not reach the source PTY while presentation is frozen.
    /// Chrome mouse stays on <see cref="BlocksPresentationInput"/>, which does
    /// not include <see cref="AttachLiveState.PresentationFrozen"/>. Pre-install
    /// dest deny does not freeze, so pane keys still reach the live source.
    /// </summary>
    internal static bool BlocksPaneKeys(AttachLiveState live) =>
        BlocksPresentationInput(live) || live.PresentationFrozen;

    internal static bool RequiresSshReconnectPath(AttachLiveState live) =>
        live.ConnectedSshPlacement || live.PlacementOwnerUnavailable;

    internal static bool IsSshPlacementSelected(AttachLiveState live) =>
        !string.IsNullOrWhiteSpace(live.ConnectedPlacementId)
        && live.Cubes.Any(c =>
            string.Equals(c.Id, live.ConnectedPlacementId, StringComparison.Ordinal)
            && IsSshCube(c));

    internal static void BindAttachRequestPlacement(AttachLiveState live, MuxAttachRequest request)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.PlacementId))
            live.ConnectedPlacementId = request.PlacementId.Trim();
        if (!string.IsNullOrWhiteSpace(request.PlacementDisplayName))
            live.PlacementDisplayName = request.PlacementDisplayName;
        if (request.PlacementKind is { } kind)
            live.PlacementKind = kind;
        live.RemoteDestination = request.RemoteDestination;
        live.RemoteAttach = request.RemoteAttach;
    }

    internal static void BindConnectedPlacementIdentity(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!string.IsNullOrWhiteSpace(live.ConnectedPlacementId))
        {
            foreach (var cube in live.Cubes)
            {
                if (string.Equals(cube.Id, live.ConnectedPlacementId, StringComparison.Ordinal))
                {
                    ApplyPlacementCopy(live, cube);
                    return;
                }
            }

            return;
        }

        if (live.RemoteDestination)
            return;

        SidebarCubeItem? local = null;
        foreach (var cube in live.Cubes)
        {
            if (cube.Kind != SidebarCubeKind.Local || string.IsNullOrWhiteSpace(cube.Id))
                continue;
            if (local is not null)
                return;
            local = cube;
        }

        if (local is not null)
            ApplyPlacementCopy(live, local);
    }

    /// <summary>
    /// <c>active_endpoint_id</c>. Missing selection falls back to the
    /// connected or sole local cube.
    /// </summary>
    internal static void BindSelectedCubeIdentity(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!string.IsNullOrWhiteSpace(live.SelectedCubeId)
            && live.Cubes.Any(cube =>
                string.Equals(cube.Id, live.SelectedCubeId, StringComparison.Ordinal)))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(live.ConnectedPlacementId)
            && live.Cubes.Any(cube =>
                string.Equals(cube.Id, live.ConnectedPlacementId, StringComparison.Ordinal)))
        {
            live.SelectedCubeId = live.ConnectedPlacementId;
            return;
        }

        SidebarCubeItem? local = null;
        foreach (var cube in live.Cubes)
        {
            if (cube.Kind != SidebarCubeKind.Local || string.IsNullOrWhiteSpace(cube.Id))
                continue;
            if (local is not null)
                return;
            local = cube;
        }

        if (local is not null)
            live.SelectedCubeId = local.Id;
    }

    internal static string CubeSnapshotKey(AttachLiveState live, string? placementId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!string.IsNullOrWhiteSpace(placementId))
            return placementId;
        SidebarCubeItem? local = null;
        foreach (var cube in live.Cubes)
        {
            if (cube.Kind != SidebarCubeKind.Local || string.IsNullOrWhiteSpace(cube.Id))
                continue;
            if (local is not null)
                return LocalCubeSnapshotKey;
            local = cube;
        }

        return local?.Id ?? LocalCubeSnapshotKey;
    }

    /// <c>active_endpoint_id</c>. Selected-unconnected uses trailing status.
    internal static string? ChromeConnectedCubeId(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return CubeSnapshotKey(live, live.ConnectedPlacementId);
    }

    internal static string? ChromeFocusedCubeId(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return live.SelectedCubeId ?? live.ConnectedPlacementId;
    }

    internal static void RememberCubeSnapshot(
        AttachLiveState live,
        string cubeId,
        JsonElement snapshot)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(cubeId))
            return;
        live.CubeSnapshots[cubeId] = snapshot.Clone();
    }

    internal static JsonElement? SnapshotForSelectedCube(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var key = CubeSnapshotKey(live, live.SelectedCubeId ?? live.ConnectedPlacementId);
        return live.CubeSnapshots.TryGetValue(key, out var snap) ? snap : null;
    }

    /// <summary>
    /// connected endpoint. Selected-unconnected chrome is pending.
    /// </summary>
    internal static JsonElement? SnapshotForConnectedCube(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var key = CubeSnapshotKey(live, live.ConnectedPlacementId);
        return live.CubeSnapshots.TryGetValue(key, out var snap) ? snap : null;
    }

    private static void ResolvePlacementCopy(
        AttachLiveState live,
        ref bool remote,
        ref string? name,
        ref SidebarCubeKind? kind)
    {
        if (string.IsNullOrWhiteSpace(live.ConnectedPlacementId))
            return;
        foreach (var cube in live.Cubes)
        {
            if (!string.Equals(cube.Id, live.ConnectedPlacementId, StringComparison.Ordinal))
                continue;
            name = cube.Name;
            kind = cube.Kind;
            remote = AttachLossPolicy.IsRemoteDestination(remote, cube.Kind);
            return;
        }
    }

    internal static bool NoteInputSenderFault(AttachLiveState live, Exception? fault)
    {
        ArgumentNullException.ThrowIfNull(live);
        var kind = (fault as AttachInputFault)?.Kind ?? AttachInputFaultKind.SenderFault;
        live.Status.Set(
            StatusOwner.Transport,
            StatusSeverity.Error,
            kind == AttachInputFaultKind.Undeliverable
                ? "input undeliverable"
                : "input sender fault");
        if (kind is AttachInputFaultKind.SenderFault or AttachInputFaultKind.AdmissionRejected)
        {
            live.InputStallRequested = true;
            return true;
        }

        live.InputDetachRequested = true;
        live.DetachRequested = true;
        return false;
    }

    internal static bool ApplyInputSenderFault(
        AttachLiveState live,
        AttachInputFault fault,
        CancellationTokenSource? attempt,
        CancellationTokenSource session)
    {
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentNullException.ThrowIfNull(session);
        var stall = NoteInputSenderFault(live, fault);
        try
        {
            if (stall)
                attempt?.Cancel();
            else
                session.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // session or attempt already leaving
        }

        return stall;
    }

    /// <summary>
    /// <c>logging::shutdown("client")</c>. Hypa writes a process-log
    /// failure first because attach owns UI keys.
    /// </summary>
    internal static void RecordStartupFailure(
        IProcessLogSink sink,
        string stage,
        string? sessionId,
        string? attachClientId,
        string? err)
    {
        ArgumentNullException.ThrowIfNull(sink);
        AttachProcessLog.Failed(
            sink,
            stage,
            sessionId,
            attachClientId,
            ProcessLogApi.RedactFailErr(s_attachFailRedactor, err));
    }

    /// <summary>
    /// User cancel after the live session exists is a successful detach.
    /// Cancel during connect, snapshot, lease, or subscribe is a failed startup.
    /// </summary>
    internal static bool TryRecordStartupCancellation(
        IProcessLogSink sink,
        string stage,
        string? sessionId,
        string? attachClientId,
        string? err,
        bool liveSessionEstablished,
        out string? detachReason)
    {
        detachReason = null;
        if (liveSessionEstablished)
            return false;
        detachReason = stage + "-cancelled";
        RecordStartupFailure(sink, stage, sessionId, attachClientId, err);
        return true;
    }

    internal static void RecordSessionEnd(
        IProcessLogSink sink,
        string? sessionId,
        string? attachClientId,
        string? reason,
        bool failed)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (failed)
        {
            AttachProcessLog.Detach(sink, sessionId, attachClientId, ProcessLogEvents.OutcomeError);
            AttachProcessLog.Disconnect(
                sink,
                sessionId,
                attachClientId,
                reason ?? "error",
                ProcessLogEvents.OutcomeError);
            return;
        }

        AttachProcessLog.Detach(sink, sessionId, attachClientId);
        AttachProcessLog.Disconnect(sink, sessionId, attachClientId, reason ?? "detach");
    }

    internal static void TraceLoopFault(
        AttachLiveState live,
        Task finished,
        Task renderTask,
        Task inputTask,
        Task renewTask,
        Task beatTask,
        Task tabBarTask)
    {
        if (!finished.IsFaulted || finished.Exception?.GetBaseException() is not { } error)
            return;
        var loop = AttachLoopName(finished, renderTask, inputTask, renewTask, beatTask, tabBarTask);
        AttachProcessLog.LoopFault(live.ProcessLog, live.SessionName, live.AttachClientId, loop, error);
    }

    internal static string? DescribeAttachExit(
        Task finished,
        Task inputTask,
        Task renderTask,
        Task renewTask,
        Task beatTask,
        Task tabBarTask,
        bool userDetach)
    {
        if (userDetach)
            return null;
        if (ReferenceEquals(finished, inputTask))
            return "stdin closed";
        if (ReferenceEquals(finished, renderTask))
            return "render closed";
        if (ReferenceEquals(finished, renewTask))
            return "lease ended";
        if (ReferenceEquals(finished, beatTask))
            return "control ping failed";
        if (ReferenceEquals(finished, tabBarTask))
            return "chrome tick ended";
        return "session ended";
    }

    /// A closed remote reader is one endpoint failure. An open remote
    /// reader stays with the health monitor. A local loss stays on the
    /// stall path.
    internal static bool KeepCommittedEndpointAfterStall(
        AttachLiveState live,
        ControlPlaneClient control,
        ControlPlaneClient render,
        string reason,
        Exception? fault)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(render);
        var endpoint = live.ControlSlot?.Client;
        if (endpoint is null)
            return false;
        if (ReferenceEquals(endpoint, control) || ReferenceEquals(endpoint, render))
            return false;
        if (control.IsTransportClosed || render.IsTransportClosed)
            return false;

        var transport = "open";
        if (endpoint.IsTransportClosed)
        {
            transport = "closed";
            RecordEndpointTransportFailure(
                live,
                live.ConnectedPlacementId!,
                live.TransportEnvelope.Generation,
                "connection was lost",
                endpoint);
        }

        var err = $"{reason} {fault?.GetType().Name} {fault?.Message} {transport}";
        AttachProcessLog.Failed(
            live.ProcessLog,
            "endpoint_stall_kept",
            live.SessionName,
            live.AttachClientId,
            err);
        return true;
    }

    internal static readonly TimeSpan ControlHeartbeatPeriod = TimeSpan.FromMilliseconds(500);

    internal static readonly TimeSpan VisibleSetRenewPeriod = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan TabBarTickPeriod = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan SidebarGitTickPeriod = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Occupant detection publishes after the output that started it.
    /// A second snapshot after the pane goes quiet picks up the new agent.
    /// </summary>
    internal static readonly TimeSpan SidebarLiveFollowUp = TimeSpan.FromMilliseconds(900);

    internal static async Task TabBarTickLoopAsync(
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct,
        SemaphoreSlim? controlGate = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = ActivationPumpLoopAsync(live, tty, linked.Token);
        var chrome = TabBarChromeTickLoopAsync(live, tty, linked.Token, controlGate);
        try
        {
            var finished = await Task.WhenAny(pump, chrome).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
            try
            {
                await Task.WhenAll(pump, chrome).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Wait for a coalesced activation wake or the sidebar tick, then run
    /// one pump tick. The chrome loop keeps the same 200 ms period for
    /// sidebar and chrome flushes. A phase with no wake still expires on
    /// this timer.
    /// </summary>
    internal static async Task ActivationPumpLoopAsync(
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        // Keep one timer wait and one wake wait across iterations. A pending
        // wake wait keeps its place, so no release is lost to a timer win.
        using var timer = new PeriodicTimer(SidebarGitTickPeriod, time ?? TimeProvider.System);
        Task<bool>? tick = null;
        Task? wake = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                tick ??= timer.WaitForNextTickAsync(ct).AsTask();
                wake ??= live.ActivationPumpWake.WaitAsync(ct);
                var done = await Task.WhenAny(wake, tick).ConfigureAwait(false);
                await done.ConfigureAwait(false);
                if (wake.IsCompleted)
                    wake = null;
                if (tick.IsCompleted)
                    tick = null;
                if (ct.IsCancellationRequested)
                    return;
                await AttachEndpointActivationPump.RunTick(live, tty).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static async Task TabBarChromeTickLoopAsync(
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct,
        SemaphoreSlim? controlGate)
    {
        using var timer = new PeriodicTimer(SidebarGitTickPeriod);
        var ticks = 0;
        var tabEvery = Math.Max(
            1,
            (int)(TabBarTickPeriod.TotalMilliseconds / SidebarGitTickPeriod.TotalMilliseconds));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            TryFlushSidebarGitPaint(live, tty, controlGate);
            TryFlushSettingsPaint(live, tty, controlGate);
            var sidebarClient = live.ControlSlot?.Client;
            IAttachCommandPort? sidebarPort = sidebarClient is not null && !sidebarClient.IsDisposed
                ? new ControlPlaneAttachCommandPort(sidebarClient)
                : null;
            await TryFlushDeferredSidebarRefreshAsync(live, sidebarPort, tty, controlGate, ct)
                .ConfigureAwait(false);
            TryFlushCatalogReloadPaint(live, tty, controlGate);
            await TryFlushCommitChromeRefreshAsync(live, tty, controlGate, ct).ConfigureAwait(false);
            await TryFlushSharePairingAsync(live, tty, controlGate, ct).ConfigureAwait(false);
            ticks++;
            if (ticks % tabEvery == 0)
                TickTabBarRight(live, tty, ct);
        }
    }

    internal static void TickTabBarRight(
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.ChromeEnabled)
            return;

        live.TabBarCommands.KickRefresh(
            live.Ui.TabBarRight,
            live.Time,
            ct,
            () => ApplyTabBarRightPaint(live, tty));
        ApplyTabBarRightPaint(live, tty);
    }

    internal static void ApplyTabBarRightPaint(AttachLiveState live, UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.ChromeEnabled)
            return;

        live.WithPaint(() =>
        {
            if (!live.ChromeEnabled)
                return;
            RebuildTabBarRight(live);
            if (tty is not null && live.ChromeEnabled)
                PaintChrome(tty, live);
        });
    }

    private static void RebuildTabBarRight(AttachLiveState live)
    {
        if (!live.ChromeEnabled)
            return;
        if (live.Chrome is not { TabBarVisible: true } source)
            return;
        if (ModeBarModel.ReplacesTabRow(live.Engine.PaintMode, source.TabSlot))
            return;
        if (KeyEngine.IsPromptMode(live.Engine.PaintMode))
            return;

        var tabs = live.TabHits;
        if (tabs.Count == 0)
        {
            var visible = new List<TabBarTabSpec>(source.TabBar.Tabs.Count);
            foreach (var tab in source.TabBar.Tabs)
            {
                visible.Add(new TabBarTabSpec(
                    tab.TabId,
                    tab.Label,
                    tab.Active,
                    tab.Zoomed,
                    tab.CustomLabel,
                    tab.Badge));
            }

            tabs = visible;
        }

        if (tabs.Count == 0)
            return;

        var nextBar = TabBarModel.Build(
            tabs,
            source.TabRow?.Cols ?? source.NamedSurfaces.Main.Cols,
            source.TabBar.Row,
            source.TabSlot,
            live.TabOverflowOffset,
            live.Ui,
            live.Time,
            commandOutputs: live.TabBarCommands.Snapshot(),
            originCol: source.TabRow?.Col ?? source.NamedSurfaces.Main.Col,
            revealFocused: false);
        if (live.Chrome is not { } current)
            return;
        if (!SameLayoutTree(source, current))
            return;
        live.TabOverflowOffset = nextBar.OverflowOffset;
        live.Chrome = current with { TabBar = nextBar };
    }

    private static bool SameLayoutTree(LayoutChromeGeometry left, LayoutChromeGeometry right)
    {
        if (left.Cols != right.Cols
            || left.Rows != right.Rows
            || left.Content != right.Content
            || left.Client != right.Client
            || left.Zoomed != right.Zoomed
            || left.TabBarVisible != right.TabBarVisible
            || left.TabSlot != right.TabSlot
            || left.FocusedPaneId != right.FocusedPaneId
            || left.Panes.Count != right.Panes.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Panes.Count; i++)
        {
            var a = left.Panes[i];
            var b = right.Panes[i];
            if (a.PaneId != b.PaneId || a.Content != b.Content || a.Frame != b.Frame)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The client that the heartbeat pings. It is the local mux client of the
    /// attempt. While Local is the active endpoint, the active client is that
    /// same client, so the target is the live control client as before. While
    /// a cube is active, the target stays the local client: a lost cube must
    /// not end the attempt.
    /// </summary>
    internal static ControlPlaneClient HeartbeatPingTarget(
        AttachLiveState live,
        ControlPlaneClient control)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        return IsLocalEndpointActive(live)
            ? live.ControlSlot?.Client ?? control
            : control;
    }

    /// <summary>
    /// The heartbeat work that belongs to the active cube. A failure here is
    /// a cube loss. The endpoint health monitor and the connection supervisor
    /// already handle it, so the heartbeat does not fault.
    /// </summary>
    private static async Task ServeActiveCubeFromHeartbeatAsync(
        ControlPlaneClient active,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct,
        CancellationTokenSource linked)
    {
        if (active.IsDisposed || active.IsTransportClosed)
            return;
        try
        {
            await DrainOverlayEventsAsync(active, live, tty, ct, linked).ConfigureAwait(false);
            await RenewVisibleSetIfDueAsync(new ControlPlaneCallPort(active), live, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException
            or ObjectDisposedException
            or ControlPlaneException
            or ControlPlaneClientTimeoutException
            or InvalidOperationException)
        {
        }
    }

    private static void InterruptStdin()
    {
        // Do not set O_NONBLOCK on fd 0. That flag is shared with the parent
        // shell. The next attach then inherits a non-blocking TTY and detaches.
        try { UnixRawTerminal.SetNonBlocking(0, enabled: false); }
        catch { /* ignore */ }
    }

    private static async Task Drain(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (ControlPlaneException) { }
        catch (ControlPlaneClientTimeoutException) { }
    }

    /// shared queue. Hypa ends the parked read when the live client changes.
    internal static async Task<JsonElement?> ReadEventUntilReaderSwitchAsync(
        ControlPlaneClient reader,
        CancellationToken readerSwitch,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, readerSwitch);
        try
        {
            return await reader.ReadEventAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="reader"/> is a cube client and not a Local
    /// client. The connection supervisor owns the recovery of that client. A
    /// Local reader that fails ends the attempt. The caller has already
    /// handled a reader that a live client replaced.
    /// </summary>
    internal static bool IsSupervisedCubeReader(
        AttachLiveState live,
        ControlPlaneClient reader,
        ControlPlaneClient render,
        ControlPlaneClient control)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(reader);
        return !ReferenceEquals(reader, render)
            && !ReferenceEquals(reader, control)
            && !IsLocalRootClient(live, reader)
            && !string.IsNullOrWhiteSpace(live.ConnectedPlacementId);
    }

    /// <summary>
    /// Waits until the reader changes or the session ends. The wait has no
    /// timer and no retry, so it does not use the processor. A switch that
    /// already happened ends the wait at once.
    /// </summary>
    internal static async Task ParkUntilReaderSwitchAsync(
        CancellationToken readerSwitch,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, readerSwitch);
        try
        {
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }

    internal async Task ReadRenderAsync(
        ControlPlaneClient render,
        ControlPlaneClient control,
        SemaphoreSlim controlGate,
        SnapshotAssembler assembler,
        UnixRawTerminal tty,
        AttachLiveState live,
        CancellationTokenSource linked,
        CancellationToken ct)
    {
        // Events that a cells batch took from the queue after its last cells
        // event. Each one runs through this loop in order, with the reader
        // that read it.
        var carried = new Queue<(JsonElement Event, ControlPlaneClient Reader)>();
        while (!ct.IsCancellationRequested)
        {
            if (live.InputDetachRequested || live.DetachRequested)
            {
                live.DetachRequested = true;
                linked.Cancel();
                return;
            }

            await AttachEndpointActivationPump.RunTick(live, tty).ConfigureAwait(false);

            // Read the switch token first, then the client. A switch between
            // the two reads cancels this wait.
            var switchToken = live.ReaderSwitchToken;
            var fromCarry = carried.TryDequeue(out var carry);
            // A cube without a live client has no reader. The supervisor
            // redials it. Wait for the reader to change.
            if (!fromCarry && !IsLocalEndpointActive(live) && live.ControlSlot?.Client is null)
            {
                live.NoteReaderPark();
                await ParkUntilReaderSwitchAsync(switchToken, ct).ConfigureAwait(false);
                continue;
            }

            var reader = fromCarry ? carry.Reader : live.ControlSlot?.Client ?? render;
            if (!fromCarry)
                EnsureAttachReceivePolicy(live, reader);
            JsonElement ev = carry.Event;
            try
            {
                if (!fromCarry)
                {
                    var read = await ReadEventUntilReaderSwitchAsync(reader, switchToken, ct)
                        .ConfigureAwait(false);
                    if (read is null)
                        continue;
                    ev = read.Value;
                    // Socket read to this loop: parse, lane queue, and wait.
                    live.Latency.RecordSinceStopwatch(AttachLatencyHop.ClientRecv, reader.LastTakenEventReceivedAt);
                    // The test observes this counter. The admit below is the production path.
                    live.NoteRenderLoopRead();
                }
            }
            catch (IOException) when (!ReferenceEquals(reader, live.ControlSlot?.Client)
                && live.ControlSlot?.Client is not null)
            {
                continue;
            }
            catch (ObjectDisposedException) when (!ReferenceEquals(reader, live.ControlSlot?.Client)
                && live.ControlSlot?.Client is not null)
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException
                && IsSupervisedCubeReader(live, reader, render, control))
            {
                // The connection supervisor recovers a lost cube. Report the
                // loss once, then wait for the reader to change. Do not read
                // the dead client again and do not end the attempt.
                // A client that was reported before is no longer bound.
                if (ReferenceEquals(reader, live.ControlSlot?.Client)
                    || live.Connections.TryGet(reader, out _))
                {
                    RecordEndpointTransportFailure(
                        live,
                        live.ConnectedPlacementId ?? "",
                        live.TransportEnvelope.Generation,
                        "connection was lost",
                        reader);
                }

                live.NoteReaderPark();
                await ParkUntilReaderSwitchAsync(switchToken, ct).ConfigureAwait(false);
                continue;
            }
            catch (IOException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                throw;
            }

            // admits ClientShellEndpointResponseChunk and calls complete.
            // final_chunk comes from the line.
            if (TryAdmitReadLoopShellResponse(live, reader, ev) is not null)
                continue;

            // once. Pane-surface Ready calls complete at mod.rs:1286-1296.
            // This read and the pump can each take the next queued event.
            // A presentation-sync line taken here is that same admit.
            if (live.PendingActivation is not null
                && AttachEndpointActivationPump.TryForwardReadLoopActivationEvent(live, reader, ev, tty))
                continue;

            await ObservePendingUnderGateAsync(
                    new ControlPlaneAttachCommandPort(LiveControlClient(live, render)!), live, controlGate, tty, ct)
                .ConfigureAwait(false);
            StartChainReanchorsOffLoop(live, render);

            if (reader.ConsumeReanchorRequest()
                || assembler.ConsumeReanchorRequest()
                || live.TakePendingVisibleObserve())
            {
                live.ResetAppliedBlit();
                await ObserveVisiblePanesAsync(
                        new ControlPlaneAttachCommandPort(LiveControlClient(live, render)!), live, ct)
                    .ConfigureAwait(false);
            }

            try
            {
                if (!TryGetEventType(ev, out var type, out var payload))
                    continue;
                TraceAttachEventReceived(live, type, payload);
                if (AttachEndpointActivationPump.ShouldDropFrozenPresentationEvent(live, type))
                {
                    TraceAttachEventOutcome(
                        live, type, payload, ProcessLogEvents.OutcomeDropped, "presentation_frozen");
                    continue;
                }

                if (TryConsumeLeaseChanged(live, type, payload))
                    continue;
                if (string.Equals(type, ProtocolEventTypes.PopupLifecycle, StringComparison.Ordinal))
                {
                    await controlGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await FinishPopupLifecycleAsync(
                                new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control),
                                live,
                                ev,
                                tty,
                                ct)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        controlGate.Release();
                    }

                    continue;
                }

                if (IsCommandOverlayLiveEvent(type)
                    && !string.IsNullOrWhiteSpace(live.Dispatcher.CommandOverlayPaneId))
                {
                    await controlGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await TryRestoreCommandOverlayFocusAsync(
                                [ev],
                                live,
                                new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control),
                                tty,
                                ct)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        controlGate.Release();
                    }

                    TraceAttachEventOutcome(live, type, payload, ProcessLogEvents.OutcomeApplied);
                    continue;
                }

                if (IsStructuralPresentationEvent(type))
                {
                    var queued = new List<JsonElement> { ev };
                    foreach (var more in reader.DrainPendingEvents())
                    {
                        if (!TryGetEventType(more, out var moreType, out var morePayload))
                        {
                            queued.Add(more);
                            continue;
                        }

                        TraceAttachEventReceived(live, moreType, morePayload);
                        if (AttachEndpointActivationPump.ShouldDropFrozenPresentationEvent(live, moreType))
                        {
                            TraceAttachEventOutcome(
                                live,
                                moreType,
                                morePayload,
                                ProcessLogEvents.OutcomeDropped,
                                "presentation_frozen");
                            continue;
                        }

                        queued.Add(more);
                    }

                    await controlGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await ApplyPresentationBatchAsync(
                                queued,
                                new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control),
                                tty,
                                live,
                                ct)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        controlGate.Release();
                    }

                    continue;
                }

                if (string.Equals(type, ProtocolEventTypes.PaneScrollChanged, StringComparison.Ordinal))
                {
                    var scrollApplied = TryApplyPaneScrollChanged(live, payload, tty);
                    StartPaneChromeResizeOffLoop(live, control, ct);
                    TraceScrollChangedOutcome(live, type, payload, scrollApplied);
                    continue;
                }

                if (IsLiveNotifyEvent(type))
                {
                    if (string.Equals(type, ProtocolEventTypes.PaneAgentStatusChanged, StringComparison.Ordinal))
                        _ = ApplyNotifyEvent(live, ev, tty, linked);
                    else
                    {
                        _ = ApplyNotifyEvent(live, ev, tty, linked);
                        TraceAttachEventOutcome(live, type, payload, ProcessLogEvents.OutcomeApplied);
                    }
                    await FlushSeenReportAsync(
                            new ControlPlaneAttachCommandPort(LiveControlClient(live, control)!),
                            live,
                            ct)
                        .ConfigureAwait(false);
                    if (live.InputDetachRequested || live.DetachRequested)
                    {
                        live.DetachRequested = true;
                        linked.Cancel();
                        return;
                    }

                    continue;
                }

                if (!string.Equals(type, ProtocolEventTypes.TerminalRender, StringComparison.Ordinal))
                {
                    TraceAttachEventOutcome(
                        live, type, payload, ProcessLogEvents.OutcomeDropped, "unhandled");
                    continue;
                }

                // its own endpoint id and generation. transport.rs:61-65
                // carries both fields on ServerMessage. DrainClient uses
                // that same label for the client it just read.
                if (live.PendingActivation is not null)
                {
                    if (AttachEndpointActivationPump.TryResolveDrainedClient(
                            live,
                            reader,
                            out var admitEndpoint,
                            out var admitGeneration))
                    {
                        TryCaptureActivationTerminalRender(
                            live,
                            admitEndpoint,
                            admitGeneration,
                            payload,
                            assembler);
                        _ = AttachEndpointActivationPump.TryAdmitTerminalRenderPayload(
                            live,
                            admitEndpoint,
                            admitGeneration,
                            payload,
                            tty);
                    }

                    AckConsumedRender(reader, payload);
                    continue;
                }

                await ObservePendingUnderGateAsync(
                        new ControlPlaneAttachCommandPort(LiveControlClient(live, render) ?? render), live, controlGate, tty, ct)
                    .ConfigureAwait(false);

                if (IsPopupRenderPayload(payload))
                {
                    ApplyPopupRenderPayload(payload, assembler, tty, live);
                    AckConsumedRender(reader, payload);
                    continue;
                }

                if (!payload.TryGetProperty("pane_id", out var pid)
                    || !IsVisiblePane(live, pid.GetString()))
                {
                    NoteInvisibleRender(live, payload);
                    AckConsumedRender(reader, payload);
                    continue;
                }

                if (payload.TryGetProperty("kind", out var kind)
                    && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() == TerminalRenderBlitPayload.KindBlit)
                {
                    live.BlitReanchorPending = true;
                    continue;
                }

                if (payload.TryGetProperty("kind", out kind)
                    && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() == TerminalRenderCellsPayload.KindCells)
                {
                    var decodeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    var cells = DeserializePayload(
                        payload, ProtocolJsonContext.Default.TerminalRenderCellsPayload);
                    if (cells is null)
                        continue;
                    live.Latency.RecordSinceStopwatch(AttachLatencyHop.ClientDecode, decodeStart);
                    // Apply every queued cells event first, then compose and
                    // write the host once. herdr sends one update for all
                    // changed panes in each render pass
                    // (src/server/headless/retained_surface.rs:227-612).
                    var batch = new List<TerminalRenderCellsPayload> { cells };
                    if (!(cells.Full || cells.Reanchor))
                        TakeQueuedCells(live, reader, batch, carried);
                    var painted = TryWriteCellsBatch(tty, live, batch, reanchorBeforePaint: true);
                    // The paint gave its bytes to the host writer. Wait,
                    // off every paint lock, until the host terminal took
                    // them. A slow host terminal then holds the render
                    // credit, so the mux sends fewer frames, as it did when
                    // the paint wrote fd 1 itself.
                    await tty.WhenHostOutputWrittenAsync(ct).ConfigureAwait(false);
                    // After the host write: give the render credit of this
                    // paint back to the connection that sent it.
                    AckPaintedCells(reader, live, batch);
                    // A delta that did not chain asks for a full frame. The
                    // loop does not wait for the reply.
                    StartChainReanchorsOffLoop(live, render);
                    var batchReanchor = false;
                    for (var i = 0; i < batch.Count; i++)
                    {
                        batchReanchor |= batch[i].Reanchor;
                        if (!painted[i])
                            continue;
                        live.NoteSnapshotRender(batch[i].PaneId);
                        if (live.Chrome is not null)
                            live.InitialSnapshotPainted = true;
                    }

                    if (assembler.ConsumeReanchorRequest() || batchReanchor || live.BlitReanchorPending)
                    {
                        live.BlitReanchorPending = false;
                        await ObservePendingUnderGateAsync(
                                new ControlPlaneAttachCommandPort(LiveControlClient(live, render) ?? render), live, controlGate, tty, ct)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                if (payload.TryGetProperty("kind", out kind)
                    && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() == TerminalRenderSnapshotPayload.KindSnapshot)
                {
                    var slice = DeserializePayload(
                        payload, ProtocolJsonContext.Default.TerminalRenderSnapshotPayload);
                    if (slice is null)
                        continue;
                    // Paint the complete frame first. Note snapshot-only only after
                    // SnapshotPainter has written cells. Chrome live bytes never
                    // CUP-remap pane ANSI onto the host TTY.
                    var slicePainted = assembler.TryAdd(slice, out var frame) && frame is not null
                        && PaintAssembled(tty, frame, live);
                    // Each slice gives its render credit back when the loop
                    // takes it, so the next slice of the batch can come.
                    AckConsumedRender(reader, payload);
                    if (slicePainted)
                    {
                        live.NoteSnapshotRender(slice.PaneId);
                        if (live.Chrome is not null)
                            live.InitialSnapshotPainted = true;
                        else if (!live.InitialSnapshotPainted)
                        {
                            await controlGate.WaitAsync(ct).ConfigureAwait(false);
                            try
                            {
                                await AfterInitialSnapshotPaintAsync(
                                        new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control), live, tty, ct)
                                    .ConfigureAwait(false);
                            }
                            finally
                            {
                                controlGate.Release();
                            }
                        }

                        await TryRefreshSidebarLiveAsync(
                                new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control),
                                live,
                                tty,
                                controlGate,
                                ct,
                                armFollowUp: true)
                            .ConfigureAwait(false);
                        await ReseedCopyIfNeededAsync(
                                live,
                                new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control),
                                tty,
                                controlGate,
                                ct)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                if (payload.TryGetProperty("data", out var dataEl)
                    && dataEl.ValueKind == JsonValueKind.String
                    && dataEl.GetString() is { Length: > 0 } b64)
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(b64);
                        var paneKey = pid.GetString();
                        // Snapshot-capable panes never remap. Capability is process
                        // Ghostty / Basic IPaneVtSnapshot, or cells/snapshot
                        // ownership. Chrome live bytes never CUP-remap either.
                        if (live.IsSnapshotCapable(paneKey))
                        {
                            if (!string.IsNullOrWhiteSpace(paneKey))
                                live.NotePaneMouseFromLive(paneKey, bytes);
                            await TryRefreshSidebarLiveAsync(
                                    new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control),
                                    live,
                                    tty,
                                    controlGate,
                                    ct,
                                    armFollowUp: true)
                                .ConfigureAwait(false);
                            continue;
                        }

                        var needSnap = ApplyLiveRender(
                            tty, bytes, live, paneKey, ReadRenderRoute(payload));
                        await TryRefreshSidebarLiveAsync(
                                new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control),
                                live,
                                tty,
                                controlGate,
                                ct,
                                armFollowUp: true)
                            .ConfigureAwait(false);
                        if (live.Engine.PaintMode is AttachClientMode.Copy)
                        {
                            if (needSnap || live.HasCopyRefreshPending)
                            {
                                await RequestCopyRefreshSnapshotAsync(
                                        live,
                                        new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control),
                                        tty,
                                        ct)
                                    .ConfigureAwait(false);
                            }
                        }
                        else if (needSnap)
                        {
                            await RequestLiveLeaveAltSnapshotAsync(
                                    paneKey,
                                    render,
                                    control,
                                    controlGate,
                                    live,
                                    tty,
                                    linked,
                                    ct)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (FormatException)
                    {
                        // ignore bad payload
                    }
                }
            }
            finally
            {
                // The pane resize waits for one reply for each pane. The
                // loop does not wait for it: the next frames paint, or the
                // size check drops them until the new size comes.
                StartPaneChromeResizeOffLoop(live, control, ct);
            }
        }
    }

    internal Task ReadInputForTests(
        UnixRawTerminal tty,
        AttachLiveState live,
        ControlPlaneClient control,
        SemaphoreSlim controlGate,
        CancellationTokenSource linked,
        IAttachStdinSource stdin,
        CancellationToken ct) =>
        ReadInputAsync(tty, live, control, controlGate, linked, ct, stdin);

    internal AttachIoLoops BeginAttachIoForTests(
        UnixRawTerminal tty,
        AttachLiveState live,
        ControlPlaneClient control,
        SemaphoreSlim controlGate,
        CancellationTokenSource linked,
        IAttachStdinSource stdin,
        LeaseRenewLoop renew,
        Func<CancellationToken, Task> ping,
        CancellationToken ct,
        TimeSpan? beatPeriod = null)
    {
        ArgumentNullException.ThrowIfNull(renew);
        ArgumentNullException.ThrowIfNull(ping);
        var inputTask = ReadInputAsync(tty, live, control, controlGate, linked, ct, stdin);
        var renewTask = renew.RunAsync(ct);
        var beatTask = RunPeriodicAsync(ping, beatPeriod ?? ControlHeartbeatPeriod, TimeProvider.System, ct);
        var tabBarTask = TabBarTickLoopAsync(live, tty, ct, controlGate);
        return new AttachIoLoops(inputTask, renewTask, beatTask, tabBarTask);
    }

    private async Task ReadInputAsync(
        UnixRawTerminal tty,
        AttachLiveState live,
        ControlPlaneClient control,
        SemaphoreSlim controlGate,
        CancellationTokenSource linked,
        CancellationToken ct,
        IAttachStdinSource stdin)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        var buf = new byte[4096];
        live.HostInput.BeginAttempt();
        var csi = live.HostInput.Pending;
        while (!ct.IsCancellationRequested)
        {
            if (live.InputStallRequested)
                return;

            if (live.InputDetachRequested || live.DetachRequested)
            {
                live.DetachRequested = true;
                linked.Cancel();
                return;
            }

            int n;
            long readAt;
            try
            {
                Action? onIdle = live.Splash is null
                    ? null
                    : () => TickStartupSplash(tty, live);
                var timeoutMs = StdinPollTimeoutMs(
                    csi,
                    animateIdle: onIdle is not null,
                    mouseCapture: live.Ui.MouseCapture,
                    framer: live.HostInput);
                var read = await stdin.ReadAsync(buf, timeoutMs, ct).ConfigureAwait(false);
                if (live.InputDetachRequested || live.DetachRequested)
                {
                    live.DetachRequested = true;
                    linked.Cancel();
                    return;
                }

                if (read.Eof)
                {
                    linked.Cancel();
                    return;
                }

                if (read.Idle)
                {
                    if (csi.Count > 0)
                    {
                        var flushed = FlushPendingCsi(csi, live.HostInput);
                        if (flushed.Count > 0
                            && await RouteKeysAsync(
                                    tty, live, flushed, control, controlGate, linked, ct)
                                .ConfigureAwait(false))
                        {
                            return;
                        }
                    }
                    else
                        onIdle?.Invoke();

                    continue;
                }

                n = read.Count;
                readAt = read.ReadTimestamp != 0 ? read.ReadTimestamp : live.Latency.Now();
            }
            catch (OperationCanceledException) when (live.InputDetachRequested || live.DetachRequested)
            {
                live.DetachRequested = true;
                linked.Cancel();
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && csi.Count > 0)
            {
                var flushed = FlushPendingCsi(csi, live.HostInput);
                if (flushed.Count > 0
                    && await RouteKeysAsync(
                            tty, live, flushed, control, controlGate, linked, ct)
                        .ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            if (IsInputEof(n))
            {
                linked.Cancel();
                return;
            }

            // Producer already drained waiting bytes on the stdin thread.
            // try_recv until empty. Do not drop decode here: chrome
            // clicks must still reach Cubes after a dest handoff fail.
            List<byte[]>? mouseReports = AttachPathTrace.IsEnabled ? [] : null;
            var decoded = DecodeInput(
                buf.AsSpan(0, n),
                csi,
                out var focusIn,
                out _,
                out var mouseEvents,
                out var outerFocusEvents,
                out var hostTheme,
                mouseReports,
                live.HostInput);
            if (live.HostInput.TakeKittyKeyboardReply())
                ActivateHostKittyKeyboard(tty, live);
            if (decoded.Count > 0)
            {
                // The meter counts the keys and keeps the read time. The
                // sender stamps the pane bytes of this read with that time.
                live.Latency.NoteStdinRead(CollectionsMarshal.AsSpan(decoded), readAt);
                live.Latency.RecordHop(AttachLatencyHop.Decode, readAt);
                AttachPathTrace.RecordInput(AttachPathTrace.StageDecode, live.PaneId, CollectionsMarshal.AsSpan(decoded));
            }

            if (mouseEvents.Count > 0 && mouseReports is { Count: > 0 })
            {
                // Trace only the decoded report bytes. The whole stdin read
                // can carry unrelated keys, and inflated mouse byte counts
                // made the J7 wheel accounting wrong.
                var reports = new List<byte>();
                foreach (var report in mouseReports)
                    reports.AddRange(report);
                AttachPathTrace.RecordInput(
                    AttachPathTrace.StageMouseDecode,
                    live.PaneId,
                    CollectionsMarshal.AsSpan(reports));
            }
            try
            {
                await ApplyDecodedHostThemeAsync(
                        live,
                        hostTheme,
                        new ControlPlaneAttachCommandPort(control),
                        controlGate,
                        tty,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Theme RPC must not tear down attach. Ghostty replies
                // arrive on this stdin task.
                live.Status.Set(StatusOwner.Theme, StatusSeverity.Error, FormatStatus(ex.Message));
            }
            if (hostTheme.HostStatusReply)
            {
                live.HostStatus.NoteHostReply();
                ApplyHostStatus(tty, live);
            }

            if (TryConsumeStartupSplashInput(live, decoded, mouseEvents))
            {
                PaintChrome(tty, live, requestRepaint: true);
                // First host reply (Ghostty theme, focus, DA, mouse) is
                // only a skip. Do not dispatch it as detach or pane keys.
                continue;
            }

            if (focusIn)
            {
                // The host can reset its modes while it has no focus, for
                // example after a window reload.
                tty.RefreshKittyKeyboard();
                // The redraw takes the control gate and waits for a reply. A
                // host resize holds that gate across several round trips to
                // the mux, and a host can send a focus report with a resize.
                // The redraw is a repaint, not ordered with the keys, so the
                // keys after this report do not wait for it.
                StartFocusRedrawOffLoop(control, controlGate, live, tty, linked, ct);
            }

            if (outerFocusEvents.Count > 0)
            {
                foreach (var focusEvent in outerFocusEvents)
                    live.PrefixAscii.OuterFocused = focusEvent == OuterFocusEvent.FocusIn;
                ReconcilePrefixAsciiInput(live);
                await ForwardOuterFocusIfRequestedAsync(
                        outerFocusEvents,
                        new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control),
                        controlGate,
                        live,
                        tty,
                        linked,
                        ct)
                    .ConfigureAwait(false);
            }

            if (decoded.Count > 0 && mouseEvents.Count > 0)
            {
                if (await RouteKeysAsync(tty, live, decoded, control, controlGate, linked, ct)
                        .ConfigureAwait(false))
                {
                    return;
                }

                decoded.Clear();
            }

            if (mouseEvents.Count > 0)
            {
                var i = 0;
                while (i < mouseEvents.Count)
                {
                    if (mouseEvents[i].IsWheel)
                    {
                        var start = i;
                        i++;
                        while (i < mouseEvents.Count && mouseEvents[i].IsWheel)
                            i++;
                        if (await DispatchMouseAttachAsync(
                                tty,
                                live,
                                mouseEvents.GetRange(start, i - start),
                                LiveControlPort(
                                    live,
                                    new ControlPlaneAttachCommandPort(
                                        LiveControlClient(live, control) ?? control)),
                                controlGate,
                                linked,
                                ct)
                            .ConfigureAwait(false))
                        {
                            return;
                        }
                    }
                    else if (IsMenuHoverMotion(live, mouseEvents[i]))
                    {
                        // motion, then one render. src/app/input/mouse.rs:147-153
                        // and :1025-1030 mutate highlight only.
                        var start = i;
                        i++;
                        while (i < mouseEvents.Count && IsMenuHoverMotion(live, mouseEvents[i]))
                            i++;
                        if (await DispatchMouseAttachAsync(
                                tty,
                                live,
                                mouseEvents.GetRange(start, i - start),
                                LiveControlPort(
                                    live,
                                    new ControlPlaneAttachCommandPort(control)),
                                controlGate,
                                linked,
                                ct)
                            .ConfigureAwait(false))
                        {
                            return;
                        }
                    }
                    else
                    {
                        if (await DispatchMouseAsync(
                                tty, live, mouseEvents[i], LiveControlClient(live, control) ?? control, controlGate, linked, ct)
                            .ConfigureAwait(false))
                        {
                            return;
                        }

                        i++;
                    }
                }
            }

            if (decoded.Count == 0)
                continue;

            if (await RouteKeysAsync(tty, live, decoded, control, controlGate, linked, ct)
                    .ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Keys go to an open client dialog or menu first, then to the key
    /// engine. A held lone ESC flushed on idle takes this same route, so
    /// Escape closes a dialog instead of leaking into the pane.
    /// </summary>
    private async Task<bool> RouteKeysAsync(
        UnixRawTerminal tty,
        AttachLiveState live,
        List<byte> decoded,
        ControlPlaneClient control,
        SemaphoreSlim controlGate,
        CancellationTokenSource linked,
        CancellationToken ct)
    {
        if (ConsumesWorktreeDialogKeys(live))
        {
            return await HandleWorktreeDialogKeysAsync(
                    tty,
                    live,
                    decoded,
                    new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control),
                    ct)
                .ConfigureAwait(false);
        }

        if (ConsumesCubePairingDialogKeys(live))
        {
            return await HandleCubePairingDialogKeysAsync(
                    tty,
                    live,
                    decoded,
                    LiveControlPort(
                        live,
                        new ControlPlaneAttachCommandPort(
                            LiveControlClient(live, control) ?? control)),
                    ct)
                .ConfigureAwait(false);
        }

        if (ConsumesContextMenuKeys(live))
        {
            return await HandleContextMenuKeysAsync(
                    tty,
                    live,
                    decoded,
                    LiveControlPort(
                        live,
                        new ControlPlaneAttachCommandPort(
                            LiveControlClient(live, control) ?? control)),
                    controlGate,
                    linked,
                    ct)
                .ConfigureAwait(false);
        }

        return await DispatchKeysAsync(tty, live, decoded, control, controlGate, linked, ct)
            .ConfigureAwait(false);
    }

    internal static bool ApplyLiveRender(
        UnixRawTerminal tty,
        ReadOnlySpan<byte> bytes,
        AttachLiveState? live = null,
        string? paneId = null,
        string? hostRoute = null)
    {
        ArgumentNullException.ThrowIfNull(tty);
        if (live is null)
            return WriteLiveBytes(tty, bytes, live: null, paneId, hostRoute);

        live.BeforePaintLock?.Invoke();
        live.EnterPaint();
        var requestSnapshot = false;
        Action? delayedCompose = null;
        try
        {
            live.AfterPaintEnter?.Invoke();
            var id = paneId ?? live.PaneId;
            if (!string.IsNullOrWhiteSpace(id))
                live.NotePaneMouseFromLive(id, bytes);
            if (!string.IsNullOrWhiteSpace(id) && live.IsSnapshotCapable(id))
                return false;
            var focused = string.IsNullOrWhiteSpace(id)
                || string.Equals(id, live.PaneId, StringComparison.Ordinal);
            var suppressesLive = KeyEngine.SuppressesLiveRemap(live.Engine.PaintMode);
            if (focused && !suppressesLive)
                live.DeferLive(bytes);
            else if (focused && live.Engine.PaintMode is AttachClientMode.Copy)
                live.DeferCopySeed(bytes);

            if (suppressesLive)
            {
                if (live.Engine.PaintMode is AttachClientMode.Copy && focused)
                {
                    requestSnapshot = TrackCopyLiveTransition(tty, live, id, bytes);
                    if (requestSnapshot)
                        live.MarkCopyRefreshPending();
                    else if (!live.Engine.Copy.IsAltScreen)
                        live.Engine.Copy.AppendLive(bytes);
                    delayedCompose = () => WriteHostComposed(tty, live, requestRepaint: true);
                    return requestSnapshot;
                }

                if (live.Engine.PaintMode is AttachClientMode.Copy && !focused)
                    return WriteLiveBytes(tty, bytes, live, id, hostRoute);

                delayedCompose = () => PaintChrome(tty, live);
                return false;
            }

            return WriteLiveBytes(tty, bytes, live, id, hostRoute);
        }
        finally
        {
            live.ExitPaint();
            delayedCompose?.Invoke();
        }
    }

    internal static void WriteComposedPaint(UnixRawTerminal tty, Action paint, Func<bool>? accept = null)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(paint);
        tty.WriteComposedPaint(paint, accept);
    }

    internal static bool WriteComposedBytes(UnixRawTerminal tty, string payload)
    {
        ArgumentNullException.ThrowIfNull(tty);
        if (string.IsNullOrEmpty(payload))
            return false;

        return tty.WriteComposedBytes(Encoding.UTF8.GetBytes(payload));
    }

    private static IDisposable PushPaintTrace(
        string route,
        string? paneId,
        AttachLiveState? live,
        AssembledSnapshot? frame = null,
        int cursorColBefore = -1,
        int cursorRowBefore = -1,
        int cursorColAfter = -1,
        int cursorRowAfter = -1) =>
        AttachPathTrace.PushOutput(
            route,
            paneId,
            snapshotGeneration: frame?.Generation ?? 0,
            occupantGeneration: frame?.OccupantGeneration
                ?? live?.OccupantGenerationOf(paneId)
                ?? 0,
            cursorColBefore: cursorColBefore,
            cursorRowBefore: cursorRowBefore,
            cursorColAfter: cursorColAfter,
            cursorRowAfter: cursorRowAfter);

    private static string? ReadRenderRoute(JsonElement payload)
    {
        if (payload.TryGetProperty("route", out var route)
            && route.ValueKind == JsonValueKind.String
            && route.GetString() is { Length: > 0 } value)
            return value;
        return null;
    }

    private static void NotePaintedSnapshotPanes(
        AttachLiveState live,
        IReadOnlyList<LastPaintedPane> pending)
    {
        foreach (var pane in pending)
        {
            if (SnapshotContentWasPainted(live, pane.Frame.PaneId))
                live.NoteSnapshotRender(pane.Frame.PaneId);
        }
    }

    internal static bool WriteLiveBytes(
        UnixRawTerminal tty,
        ReadOnlySpan<byte> bytes,
        AttachLiveState? live,
        string? paneId = null,
        string? hostRoute = null)
    {
        ArgumentNullException.ThrowIfNull(tty);
        _ = hostRoute;
        var plan = PlanPaneLivePaint(tty, live, paneId, bytes);
        if (live?.Chrome is { } chrome)
        {
            var id = paneId ?? live.PaneId;
            if (!(IsPopupPaneId(id) && chrome.PopupFrame is not null)
                && !TryPaneContent(chrome, id, out _))
            {
                PaintChrome(tty, live);
            }

            // buf[(area.x + x, area.y + y)]. Do not CUP-remap pane ANSI
            // onto chrome.
            return plan.RequestSnapshot;
        }

        // Chrome-off still composes one host-size frame, then encodes
        // once. Do not write pane-relative ANSI to the host TTY. Reject
        // this fallback until structured cells exist.
        if (live is not null && !IsPopupPaneId(paneId))
            AppendHistoryLive(live, paneId ?? live.PaneId, bytes);
        return plan.RequestSnapshot;
    }

    /// <summary>
    /// Paint a complete assembled grid. Returns true only when this pane's
    /// content box was written so the caller may set the snapshot-only marker.
    /// </summary>
    internal static bool PaintAssembled(
        UnixRawTerminal tty,
        AssembledSnapshot frame,
        AttachLiveState? live = null)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(frame);
        if (live is null)
            return PaintAssembledCore(tty, frame, live: null);

        if (IsPopupPaneId(frame.PaneId))
        {
            live.PopupSnapshot = frame;
            if (live.HasNewerPainted(frame))
                return false;
            if (!WriteHostComposed(
                    tty,
                    live,
                    frame,
                    afterAccepted: () => CommitAcceptedPaneFrame(tty, live, frame)))
                return false;
            return SnapshotContentWasPainted(live, frame.PaneId);
        }

        if (!IsVisiblePane(live, frame.PaneId))
        {
            // focus. Do not drop a full grid because chrome still lists
            // the outgoing space.
            if (!live.HasNewerPainted(frame))
                live.SetPaneFrame(frame);
            return false;
        }
        if (live.HasNewerPainted(frame))
            return false;
        if (OverlayIsVisiblePane(live, frame.PaneId) && frame.IngestFull)
            live.Overlay.TryAcceptReveal(frame);
        if (!PaintAssembledCore(tty, frame, live))
            return false;
        return SnapshotContentWasPainted(live, frame.PaneId);
    }

    /// <summary>
    /// True when SnapshotPainter wrote this pane's content box. A 0-size box
    /// or an overlay that skips pane cells must not set snapshot-only.
    /// </summary>
    internal static bool SnapshotContentWasPainted(AttachLiveState live, string? paneId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (IsPopupPaneId(paneId))
            return live.Chrome?.PopupFrame is { Inner.Cols: > 0, Inner.Rows: > 0 };

        if (live.Engine.Mode is AttachClientMode.KeybindHelp
            or AttachClientMode.Settings
            or AttachClientMode.Onboarding)
        {
            return false;
        }

        if (OverlayIsVisiblePane(live, paneId)
            && live.TryGetPaneFrame(paneId!, out var overlaySnap)
            && overlaySnap is not null)
        {
            return live.Overlay.CanStamp(overlaySnap);
        }

        if (live.Chrome is { } chrome)
            return TryPaneContent(chrome, paneId, out _);

        return true;
    }

    private static bool PaintAssembledCore(
        UnixRawTerminal tty,
        AssembledSnapshot frame,
        AttachLiveState? live)
    {
        if (live is not null)
        {
            NoteCopySnapshot(live, frame);
            return WriteHostComposed(
                tty,
                live,
                frame,
                afterAccepted: () => CommitAcceptedPaneFrame(tty, live, frame));
        }

        return WriteOneShotHostFrame(tty, frame);
    }

    /// <summary>
    // / Test seam without live state.
    /// encodes one host-size frame. Do not call <c>SnapshotPainter.Paint</c> as
    /// the host writer.
    /// </summary>
    private static bool WriteOneShotHostFrame(UnixRawTerminal tty, AssembledSnapshot frame)
    {
        var host = new HostFrame();
        var cols = Math.Max(1, frame.Cols);
        var rows = Math.Max(1, frame.Rows);
        host.Resize(cols, rows);
        host.StampSnapshot(frame, new CellRect(0, 0, cols, rows));
        if (frame.Cursor.HasCursor)
        {
            host.Cursor = new HostCursor(
                Math.Clamp(frame.Cursor.Col, 0, cols - 1),
                Math.Clamp(frame.Cursor.Row, 0, rows - 1),
                frame.Cursor.Visible,
                frame.Cursor.Shape);
        }
        else
            host.Cursor = HostCursor.None;

        var encoder = new HostBlitEncoder();
        var encoded = encoder.Encode(host);
        using (PushPaintTrace(AttachPathTrace.RouteChrome, frame.PaneId, live: null, frame))
            return tty.WriteHostEncoded(encoded.Span);
    }

    /// <summary>
    /// chrome-off terminal (inner rect is the host) and overlay stamp.
    /// Chrome is not required.
    /// </summary>
    private static bool UsesHostFrame(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return true;
    }

    private static bool WriteHostComposed(
        UnixRawTerminal tty,
        AttachLiveState live,
        AssembledSnapshot? generationFrame = null,
        Action? afterAccepted = null,
        bool recoverStale = false,
        bool noteSnapshotPanes = false,
        bool requestRepaint = false,
        bool overlayOnly = false,
        HostPatchRequest? patch = null)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        // client. src/client/mod.rs:1675-1700 applies ServerMessage::Frame
        // sequentially: encode, write, commit. Hold _paintGate across
        // ComposeHostFrame + Encode + WriteHostEncoded + Commit so a second
        // task cannot Stamp or Encode against _last. WriteHostEncoded gives
        // the bytes to the host writer and returns: the fd 1 write runs on
        // the writer thread, off _paintGate. The paint lock alone does not
        // hold the key engine lock, so the key feed does not wait for this
        // compose. The compose takes the key engine lock where it reads key
        // engine state.
        return live.WithFramePaint(() =>
        {
            // state.rs:122-127 present_frozen_chrome lifts one chrome frame.
            if (live.PresentationFrozen)
            {
                if (!live.FrozenChromePaintArmed)
                    return false;
                live.FrozenChromePaintArmed = false;
                if (patch is not null)
                {
                    live.Latency.Paint.NoteFallback(HostPatchFallback.Frozen);
                    patch = null;
                }
            }
            else
            {
                live.FrozenChromePaintArmed = false;
            }

            if (!live.PatchPaintEnabled)
                patch = null;
            if (requestRepaint)
                live.HostEncoder.RequestRepaint();
            var pending = new List<LastPaintedPane>();
            var parent = live.BeginComposePainted(pending);
            try
            {
                // The baseline before this paint. Each compose clears the
                // live baseline before it stamps, so a failed or rejected
                // write leaves no baseline and the next paint is a full
                // compose.
                HostPatchBaseline? prior = null;
                HostComposeKey? composeKey = null;
                var patched = false;

                EncodedHostBlit? EncodeNow(bool allowPatch)
                {
                    prior = live.PatchBaseline ?? prior;
                    live.PatchBaseline = null;
                    var composeStart = live.Latency.Now();
                    composeKey = CaptureComposeKey(tty, live, generationFrame);
                    if (allowPatch && patch is not null)
                    {
                        var reason = TryStampPatch(live, patch, composeKey, prior, generationFrame, out var hostSpans);
                        live.Latency.RecordHop(AttachLatencyHop.Compose, composeStart);
                        if (reason is null)
                        {
                            var patchEncodeStart = live.Latency.Now();
                            var patchBlit = live.HostEncoder.EncodeRows(live.Host, hostSpans);
                            live.Latency.RecordHop(AttachLatencyHop.Encode, patchEncodeStart);
                            if (patchBlit is not null)
                            {
                                patched = true;
                                return patchBlit;
                            }

                            reason = HostPatchFallback.NoBaseline;
                        }

                        live.Latency.Paint.NoteFallback(reason.Value);
                        composeStart = live.Latency.Now();
                    }

                    patched = false;
                    pending.Clear();
                    ComposeHostOrOverlay(tty, live, generationFrame, recoverStale, overlayOnly);
                    live.Latency.Paint.NoteFullCompose();
                    live.Latency.RecordHop(AttachLatencyHop.Compose, composeStart);
                    if (live.Host.Cols < 1 || live.Host.Rows < 1)
                        return null;
                    var encodeStart = live.Latency.Now();
                    var blit = live.HostEncoder.Encode(live.Host);
                    live.Latency.RecordHop(AttachLatencyHop.Encode, encodeStart);
                    return blit;
                }

                if (tty.IsComposing)
                {
                    // commit(). Nested CaptureWrites would wrap a second CSI ?2026
                    // around Encode() which already emitted ?2026h/l. Stamp only;
                    // the outer WriteHostEncoded path Encodes, writes, and Commits
                    // once.
                    if (patch is not null)
                        live.Latency.Paint.NoteFallback(HostPatchFallback.Composing);
                    live.PatchBaseline = null;
                    ComposeHostOrOverlay(tty, live, generationFrame, recoverStale, overlayOnly);
                    live.Latency.Paint.NoteFullCompose();
                    live.MergeComposePainted(parent, pending);
                    MergePredictionRows(live);
                    return false;
                }

                tty.DuringCompose?.Invoke();
                var encoded = EncodeNow(allowPatch: true);
                if (encoded is null)
                    return false;
                var route = AttachPathTrace.RouteChrome;
                var writeStart = live.Latency.Now();
                bool wrote;
                try
                {
                    wrote = tty.WriteHostEncoded(
                        encoded.Value.Span,
                        accept: () => live.CanAcceptPainted(pending, generationFrame),
                        onAccepted: () =>
                        {
                            live.HostEncoder.Commit(live.Host, encoded.Value);
                            live.PatchBaseline = NextPatchBaseline(prior, composeKey!, pending, patched, overlayOnly);
                            // A patch stamped each pending gutter, and a full
                            // compose painted the whole chrome.
                            if (!overlayOnly)
                                live.PendingGutterPanes.Clear();
                            if (patched)
                                live.Latency.Paint.NotePatch();
                            if (encoded.Value.NoOp)
                                live.Latency.Paint.NoteNoOp();
                            live.Host.ClearDirty();
                            live.PublishPainted(pending);
                            CommitPredictionPaint(live, overlayOnly);
                            live.CommitChrome();
                            if (noteSnapshotPanes)
                                NotePaintedSnapshotPanes(live, pending);
                            using (PushPaintTrace(AttachPathTrace.RouteChrome, live.PaneId, live))
                                ApplyWindowTitle(tty, live);
                            ApplyHostStatus(tty, live);
                            // A caller can change key engine state after
                            // the write, for example CommitPaintMode.
                            if (afterAccepted is not null)
                                live.WithEngine(afterAccepted);
                        },
                        recover: !recoverStale
                            ? null
                            : () =>
                            {
                                pending.Clear();
                                var retry = EncodeNow(allowPatch: false);
                                if (retry is null
                                    || !live.CanAcceptPainted(pending, generationFrame: null))
                                {
                                    return null;
                                }

                                encoded = retry;
                                return new HostWriteRetry(retry.Value.Utf8, retry.Value.NoOp);
                            },
                        route: route,
                        noOp: encoded.Value.NoOp);
                }
                catch
                {
                    // A failed write can leave a part of the bytes on the
                    // host. The kept frame no longer matches the host, so
                    // the next paint must rewrite every cell. A diff or a
                    // no-op against the kept frame cannot repair the host.
                    live.HostEncoder.RequestRepaint();
                    throw;
                }

                live.Latency.RecordHop(AttachLatencyHop.HostWrite, writeStart);
                if (!wrote)
                    live.AbortChrome();
                return wrote;
            }
            finally
            {
                live.EndComposePainted(parent);
            }
        });
    }

    /// <summary>
    /// The baseline after an accepted paint. A full compose shows the panes
    /// that it stamped. A patch keeps the other panes of the prior baseline.
    /// An overlay-only paint does not restamp panes, so it keeps the prior
    /// pane frames only when the prior baseline was valid.
    /// </summary>
    private static HostPatchBaseline? NextPatchBaseline(
        HostPatchBaseline? prior,
        HostComposeKey key,
        IReadOnlyList<LastPaintedPane> painted,
        bool patched,
        bool overlayOnly)
    {
        if (patched)
            return prior?.WithPatched(key, painted);
        if (overlayOnly)
            return prior?.WithKey(key);
        return HostPatchBaseline.FromCompose(key, painted);
    }

    /// <summary>
    /// The inputs of a host compose other than pane cells and the cursor.
    /// Read before the stamp, so a later change makes the baseline older,
    /// not newer, than the host.
    /// </summary>
    private static HostComposeKey CaptureComposeKey(
        UnixRawTerminal tty,
        AttachLiveState live,
        AssembledSnapshot? generationFrame)
    {
        var (cols, rows) = ResolveHostSize(tty, live, generationFrame);
        var chrome = live.Chrome;
        return new HostComposeKey
        {
            Cols = cols,
            Rows = rows,
            Chrome = chrome,
            Ui = live.Ui,
            Theme = live.Theme,
            Palette = live.Theme.Palette,
            StatusError = live.StatusError,
            StatusNotice = live.StatusNotice,
            Chips = chrome is null ? null : ChipInputOf(live, chrome.Cols),
            ToastCount = live.Toasts.Count,
            Toasts = live.Toasts,
            PopupOpen = live.PopupOpen,
            OverlayModal = live.Overlay.OwnsModal,
            HistoryOverlay = live.History.ShowsOverlay,
            PaintMode = LayoutPaintMode(live),
            DialogOpen = live.Worktrees.IsOpen || live.CubesPairing.IsOpen,
            SplashPlaying = live.Splash is { IsPlaying: true },
            PaneSelection = live.Mouse.Selection is { Active: true, HostRange: false },
            HostSelection = live.Mouse.Selection is { Active: true, HostRange: true },
            HasLivePane = live.HasLivePane,
            FocusedPaneId = chrome is null ? live.PaneId : chrome.FocusedPaneId ?? live.PaneId,
            DrawnCursor = HostCursorPolicy.IsDrawn(live.Ui.HostCursor),
        };
    }

    /// <summary>
    /// The pane box of <paramref name="paneId"/> in the host frame: the
    /// chrome content box, or the whole host when the chrome is off and the
    /// pane is the live pane.
    /// </summary>
    private static bool TryPatchPaneBox(
        AttachLiveState live,
        HostComposeKey key,
        string paneId,
        out CellRect box)
    {
        if (key.Chrome is { } chrome)
            return TryPaneContent(chrome, paneId, out box);
        box = new CellRect(0, 0, key.Cols, key.Rows);
        return string.Equals(paneId, live.PaneId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stamp the changed rows of each patched pane into the kept host frame,
    /// with no clear and no chrome. Returns the reason for a full compose, or
    /// null after a stamp. All checks run before the first stamp, so a
    /// rejected patch leaves the host frame as it was.
    /// <paramref name="hostSpans"/> gets the host cells that the stamp can
    /// change: the pane box columns of each stamped row and the gutter
    /// columns of each stamped gutter row. Each other host cell stays equal
    /// to the kept frame, so the encode compares only these cells.
    /// herdr <c>apply_pane_surface_patch</c>
    /// (<c>src/client/shell/surface_patch.rs:109-256</c>).
    /// </summary>
    private static HostPatchFallback? TryStampPatch(
        AttachLiveState live,
        HostPatchRequest patch,
        HostComposeKey key,
        HostPatchBaseline? prior,
        AssembledSnapshot? generationFrame,
        out List<HostRowSpan> hostSpans)
    {
        hostSpans = [];
        // The predicted cells of the focused pane ride on the same patch:
        // the rows that show predictions now and the rows that showed them.
        live.ComposePrediction = ComposePredictionView(live, generationFrame);
        AddPredictionRows(live, patch);
        if (patch.Blocker() is { } paneReason)
            return paneReason;
        if (key.Chrome is { } occluding)
        {
            foreach (var pane in patch.Panes)
            {
                if (TryPaneContent(occluding, pane.PaneId, out var paneBox)
                    && TiledPopupOcclusion(live, occluding, pane.PaneId) is { } occlusion
                    && occlusion.Intersects(paneBox))
                {
                    return HostPatchFallback.PopupOcclusion;
                }
            }
        }

        if (key.Blocker() is { } layerReason)
            return layerReason;
        if (prior is null)
            return HostPatchFallback.NoBaseline;
        if (key.DifferenceFrom(prior.Key) is { } changed)
            return changed;
        if (live.HostEncoder.RepaintPending)
            return HostPatchFallback.Repaint;
        if (live.Host.Cols != key.Cols || live.Host.Rows != key.Rows)
            return HostPatchFallback.HostSize;
        if (!live.HostEncoder.CanEncodeRows(live.Host))
            return HostPatchFallback.NoBaseline;

        foreach (var pane in patch.Panes)
        {
            if (pane.Frame is not { } frame)
                return HostPatchFallback.Baseline;
            if (!TryPatchPaneBox(live, key, pane.PaneId, out var box))
                return HostPatchFallback.OtherPane;
            if (!prior.Panes.TryGetValue(pane.PaneId, out var shown)
                || !ReferenceEquals(shown, pane.BaseFrame))
            {
                return HostPatchFallback.Baseline;
            }

            if (frame.Cols != box.Cols || frame.Rows != box.Rows)
                return HostPatchFallback.SizeMismatch;
            if (live.PaneIsScrolledBack(pane.PaneId))
                return HostPatchFallback.Viewport;
            foreach (var row in pane.Rows)
            {
                if (HostFrame.RowHasWideCellOnEdge(frame, box, row))
                    return HostPatchFallback.WideEdge;
            }
        }

        // A full compose restamps every visible pane from its stored frame.
        // Each pane that this patch does not stamp must show that frame now.
        if (key.Chrome is { } layout)
        {
            foreach (var visible in layout.Panes)
            {
                if (!TryPaneContent(layout, visible.PaneId, out _)
                    || patch.Has(visible.PaneId))
                {
                    continue;
                }

                live.TryGetPaneFrame(visible.PaneId, out var stored);
                prior.Panes.TryGetValue(visible.PaneId, out var shown);
                if (!ReferenceEquals(stored, shown))
                    return HostPatchFallback.OtherPane;
            }
        }

        if (PendingGutterStamps(live, key) is not { } gutters)
            return HostPatchFallback.Chrome;

        var palette = live.Theme.Palette;
        if (gutters.Count > 0)
        {
            // A scroll event moved a thumb after the last host paint. Stamp
            // the gutter of that pane as the full compose stamps it.
            var gutterSink = new HostFrameCellSink(live.Host);
            foreach (var pane in gutters)
            {
                PaneChromeGutter.Stamp(gutterSink, pane, palette);
                var gutter = pane.Gutter!.Value;
                for (var row = gutter.Row; row < gutter.EndRow; row++)
                    hostSpans.Add(new HostRowSpan(row, gutter.Col, gutter.EndCol));
            }
        }

        foreach (var pane in patch.Panes)
        {
            var frame = pane.Frame!;
            TryPatchPaneBox(live, key, pane.PaneId, out var box);
            foreach (var row in pane.Rows)
            {
                if ((uint)row >= (uint)box.Rows)
                    continue;
                live.Host.StampSnapshotRow(frame, box, row);
                StampPrediction(live, pane.PaneId, box, occlude: null, onlyRow: row);
                if (key.Chrome is not null && palette.Chrome.DesktopStatusBar)
                    PaintDesktopPaneDefaults(live.Host, new CellRect(box.Col, box.Row + row, box.Cols, 1), palette);
                hostSpans.Add(new HostRowSpan(box.Row + row, box.Col, box.EndCol));
            }

            live.NotePendingPainted(frame, box.Col, box.Row, box.Cols, box.Rows);
        }

        // The host cursor, as the full compose sets it.
        var focusedId = key.FocusedPaneId;
        var cursorBox = key.Chrome is { } focusChrome
            && TryPaneContent(focusChrome, focusedId, out var focusedBox)
                ? focusedBox
                : new CellRect(0, 0, key.Cols, key.Rows);
        StampHostCursor(live, cursorBox, focusedId, generationFrame);
        return null;
    }

    /// <summary>
    /// The panes of <see cref="AttachLiveState.PendingGutterPanes"/> whose
    /// gutter a patch must stamp. Null when a gutter stamp could differ from
    /// the full compose: a later layer (the status row or a tiled popup)
    /// covers a part of the gutter.
    /// </summary>
    private static List<ChromePaneFrame>? PendingGutterStamps(AttachLiveState live, HostComposeKey key)
    {
        var stamps = new List<ChromePaneFrame>();
        if (live.PendingGutterPanes.Count == 0 || key.Chrome is not { } chrome)
            return stamps;
        foreach (var pane in chrome.Panes)
        {
            if (!live.PendingGutterPanes.Contains(pane.PaneId) || pane.Gutter is not { } gutter)
                continue;
            if (chrome.StatusRow is { } status && status.Intersects(gutter))
                return null;
            if (TiledPopupOcclusion(live, chrome, pane.PaneId) is { } occlusion && occlusion.Intersects(gutter))
                return null;
            stamps.Add(pane);
        }

        return stamps;
    }

    private static void ComposeHostOrOverlay(
        UnixRawTerminal tty,
        AttachLiveState live,
        AssembledSnapshot? generationFrame,
        bool restoreCover,
        bool overlayOnly)
    {
        if (overlayOnly && live.Host.Cols > 0 && live.Host.Rows > 0)
        {
            // Frame. Pane cells are already in Host from the last compose.
            // Do not Clear or StampSnapshot on hover.
            var sink = new HostFrameCellSink(live.Host);
            StampPaintModeOverlay(
                live,
                sink,
                live.Host.Cols,
                live.Host.Rows,
                new CellRect(0, 0, live.Host.Cols, live.Host.Rows));
            StampStartupSplash(live, sink, live.Host.Cols, live.Host.Rows);
            return;
        }

        ComposeHostFrame(tty, live, generationFrame, restoreCover);
    }

    private static void ComposeHostFrame(
        UnixRawTerminal tty,
        AttachLiveState live,
        AssembledSnapshot? generationFrame,
        bool restoreCover = false)
    {
        var (cols, rows) = ResolveHostSize(tty, live, generationFrame);
        live.Host.Resize(cols, rows);
        live.Host.Clear();
        // A restore cover stamps other frames. It shows no prediction.
        live.ComposePrediction = restoreCover ? null : ComposePredictionView(live, generationFrame);
        var sink = new HostFrameCellSink(live.Host);
        CellRect paneBox;
        string? focusedId;
        if (live.Chrome is { } chrome)
        {
            // notifications, then popup. ToastHit.Place is inset into the
            // content rect, so toasts stamp after pane cells.
            LayoutChromePainter.Stamp(
                sink,
                chrome,
                live.Ui,
                live.StatusError,
                ChipInputOf(live, chrome.Cols),
                live.Theme.Palette,
                includePopup: false,
                includeToasts: false,
                notice: live.StatusNotice);

            // origin cells at inner_rect. Wheel must not stamp a copy overlay.
            foreach (var pane in chrome.Panes)
            {
                if (!TryPaneContent(chrome, pane.PaneId, out var box))
                    continue;
                var stored = restoreCover
                    ? live.GetRestorePaintFrame(pane.PaneId)
                    : ResolveComposeFrame(live, pane.PaneId, generationFrame);
                if (stored is null)
                    continue;
                var occlusion = TiledPopupOcclusion(live, chrome, pane.PaneId);
                live.Host.StampSnapshot(stored, box, occlusion);
                StampPrediction(live, pane.PaneId, box, occlusion);
                live.NotePendingPainted(stored, box.Col, box.Row, box.Cols, box.Rows);
                if (live.Theme.Palette.Chrome.DesktopStatusBar)
                    PaintDesktopPaneDefaults(live.Host, box, live.Theme.Palette);
            }

            if (live.Mouse.Selection is { Active: true, HostRange: false }
                && TryPaneContent(chrome, live.Mouse.Selection.PaneId ?? live.PaneId, out var selBox))
            {
                StampSelection(live.Host, live.Mouse.Selection, selBox);
            }

            LayoutChromePainter.StampToasts(sink, chrome, live.Theme.Palette);

            if (live.Overlay.OwnsModal
                && live.Overlay.Geometry is { } overlayGeo
                && live.Overlay.PaneId is { } overlayPane
                && ResolveComposeFrame(live, overlayPane, generationFrame) is { } overlaySnap
                && live.Overlay.CanStamp(overlaySnap))
            {
                ClientOverlayComposer.Stamp(
                    live.Host,
                    overlaySnap,
                    overlayGeo,
                    live.Theme.Palette,
                    OverlayPaneTitle(live, overlayPane));
            }
            else if (live.PopupOpen && chrome.PopupFrame is { } popup)
            {
                LayoutChromePainter.StampPopupChrome(sink, popup, live.Theme.Palette);
                if (live.PopupSnapshot is { } popupSnap)
                    live.Host.StampSnapshot(popupSnap, popup.Inner);
            }

            if (live.Overlay.OwnsModal
                && live.Overlay.Frame() is { } overlayFocus
                && live.Overlay.PaneId is { } overlayFocusPane
                && ResolveComposeFrame(live, overlayFocusPane, generationFrame) is { } overlayFocusSnap
                && live.Overlay.CanStamp(overlayFocusSnap))
            {
                focusedId = overlayFocusPane;
                paneBox = overlayFocus.Inner;
            }
            else
            {
                focusedId = chrome.FocusedPaneId ?? live.PaneId;
                paneBox = TryPaneContent(chrome, focusedId, out var focusedBox)
                    ? focusedBox
                    : new CellRect(0, 0, cols, rows);
            }
        }
        else
        {
            // Chrome-off: the host is the inner rect. Stamp pane cells at
            // origin then encode once. Do not invent a second painter.
            paneBox = new CellRect(0, 0, cols, rows);
            focusedId = live.PaneId;
            var stored = restoreCover
                ? live.GetRestorePaintFrame(focusedId)
                : ResolveComposeFrame(live, focusedId, generationFrame);
            if (stored is not null)
            {
                live.Host.StampSnapshot(stored, paneBox);
                StampPrediction(live, focusedId, paneBox, occlude: null);
                live.NotePendingPainted(stored, 0, 0, cols, rows);
            }

            if (live.Mouse.Selection is { Active: true, HostRange: false })
                StampSelection(live.Host, live.Mouse.Selection, paneBox);
        }

        StampHostCursor(live, paneBox, focusedId, generationFrame);
        // and pane cells. LayoutPaintMode is the destination during restore.
        StampPaintModeOverlay(live, sink, cols, rows, paneBox);
        StampStartupSplash(live, sink, cols, rows);
        if (live.Mouse.Selection is { Active: true, HostRange: true } hostSel)
        {
            live.Host.StampInverseRange(
                hostSel.AnchorCol,
                hostSel.AnchorRow,
                hostSel.EndCol,
                hostSel.EndRow);
        }
    }

    private static (int Cols, int Rows) ResolveHostSize(
        UnixRawTerminal tty,
        AttachLiveState live,
        AssembledSnapshot? generationFrame)
    {
        if (live.Splash is { IsPlaying: true }
            && tty.TryGetSize(out var splashCols, out var splashRows)
            && splashCols > 0
            && splashRows > 0)
        {
            return (splashCols, splashRows);
        }

        if (live.Chrome is { Cols: > 0, Rows: > 0 } chrome)
            return (chrome.Cols, chrome.Rows);
        if (tty.TryGetSize(out var cols, out var rows) && cols > 0 && rows > 0)
            return (cols, rows);
        var mode = LayoutPaintMode(live);
        if (ExclusiveOverlayMode(mode) || KeyEngine.IsChrome(mode) || KeyEngine.IsPromptMode(mode))
            return (80, 24);
        if (generationFrame is { Cols: > 0, Rows: > 0 })
            return (generationFrame.Cols, generationFrame.Rows);
        if (!string.IsNullOrWhiteSpace(live.PaneId)
            && live.TryGetPaneFrame(live.PaneId, out var stored)
            && stored is { Cols: > 0, Rows: > 0 })
        {
            return (stored.Cols, stored.Rows);
        }

        if (live.LastComplete is { Cols: > 0, Rows: > 0 } last)
            return (last.Cols, last.Rows);
        return (80, 24);
    }

    private static void CommitAcceptedPaneFrame(
        UnixRawTerminal tty,
        AttachLiveState live,
        AssembledSnapshot frame)
    {
        var wasAlt = live.GetPaneAlt(frame.PaneId);
        live.SetPaneFrame(frame);
        NotePaneSnapshotAlt(tty, live, frame.PaneId, frame, wasAlt);
        // SetPaneFrame Seed() clears DrawnActive. StampHostCursor already
        // painted the block; restore those flags so the next live remap can
        // restore the previous glyph.
        if (!HostCursorPolicy.IsDrawn(live.Ui.HostCursor)
            || !frame.Cursor.HasCursor
            || !frame.Cursor.Visible)
            return;
        CellRect box;
        if (live.Chrome is { } chrome)
        {
            if (!string.Equals(
                    frame.PaneId,
                    chrome.FocusedPaneId ?? live.PaneId,
                    StringComparison.Ordinal)
                || !TryPaneContent(chrome, frame.PaneId, out box))
            {
                return;
            }
        }
        else if (!string.Equals(frame.PaneId, live.PaneId, StringComparison.Ordinal))
            return;
        else
            box = new CellRect(0, 0, Math.Max(1, live.Host.Cols), Math.Max(1, live.Host.Rows));

        var cursor = live.GetLiveCursor(frame.PaneId);
        var col = Math.Clamp(frame.Cursor.Col, 0, Math.Max(0, box.Cols - 1));
        var row = Math.Clamp(frame.Cursor.Row, 0, Math.Max(0, box.Rows - 1));
        cursor.DrawnHost = true;
        cursor.DrawnActive = true;
        cursor.DrawnCol = col;
        cursor.DrawnRow = row;
    }

    private static AssembledSnapshot? ResolveComposeFrame(
        AttachLiveState live,
        string paneId,
        AssembledSnapshot? generationFrame)
    {
        if (generationFrame is not null
            && string.Equals(paneId, generationFrame.PaneId, StringComparison.Ordinal))
        {
            return generationFrame;
        }

        return live.TryGetPaneFrame(paneId, out var stored) ? stored : null;
    }

    private static void StampSelection(HostFrame host, Mouse.MouseSelection selection, CellRect box)
    {
        if (!selection.Active)
            return;
        var view = selection.Buffer.CapturePaintSnapshot();
        host.StampCopyView(view, box);
        CopyModeBuffer.Normalize(
            selection.AnchorRow,
            selection.AnchorCol,
            selection.EndRow,
            selection.EndCol,
            out var r1,
            out var c1,
            out var r2,
            out var c2);
        var top = Math.Max(0, view.ViewportTop);
        for (var i = 0; i < box.Rows; i++)
        {
            var srcRow = top + i;
            for (var c = 0; c < box.Cols; c++)
            {
                if (srcRow < r1 || srcRow > r2)
                    continue;
                if (srcRow == r1 && c < c1)
                    continue;
                if (srcRow == r2 && c > c2)
                    continue;
                var existing = host.CellAt(box.Col + c, box.Row + i);
                host.Stamp(
                    box.Col + c,
                    box.Row + i,
                    existing with { Style = existing.Style with { Inverse = true } });
            }
        }
    }

    private static void StampHostCursor(
        AttachLiveState live,
        LayoutChromeGeometry chrome,
        string? paneId,
        AssembledSnapshot? generationFrame = null)
    {
        if (string.IsNullOrWhiteSpace(paneId) || !TryPaneContent(chrome, paneId, out var box))
        {
            live.Host.Cursor = HostCursor.None;
            return;
        }

        StampHostCursor(live, box, paneId, generationFrame);
    }

    internal static void StampHostCursor(
        AttachLiveState live,
        CellRect box,
        string? paneId,
        AssembledSnapshot? generationFrame = null)
    {
        if (string.IsNullOrWhiteSpace(paneId) || box.Cols < 1 || box.Rows < 1)
        {
            live.Host.Cursor = HostCursor.None;
            return;
        }

        // Gate on destination
        // LayoutPaintMode so LeaveMode restore stamps the pane caret before
        // CommitPaintMode in WriteHostEncoded onAccepted.
        if (OverlayOwnsHostCaret(live) || live.PaneIsScrolledBack(paneId))
        {
            live.Host.Cursor = HostCursor.None;
            return;
        }

        var cursor = live.GetLiveCursor(paneId);
        var composeFrame = generationFrame is not null
            && string.Equals(paneId, generationFrame.PaneId, StringComparison.Ordinal)
            ? generationFrame
            : live.TryGetPaneFrame(paneId, out var stored)
                ? stored
                : null;
        // focused_terminal_suppresses_host_cursor. Gate 4 is None, not
        // visible:false. Do not mutate pane Cursor.Visible.
        if (composeFrame is { SynchronizedOutput: true })
        {
            live.Host.Cursor = HostCursor.None;
            return;
        }

        // Reveal
        // exposes a hardware cursor even when the pane requested ?25l.
        var reveal = live.CjkIme.ShouldReveal(DetectedAgentOf(live, paneId));
        if (composeFrame is null || !composeFrame.Cursor.HasCursor)
        {
            if (reveal)
            {
                live.Host.Cursor = new HostCursor(box.Col, box.Row, true, live.CjkIme.CursorShape);
                return;
            }

            live.Host.Cursor = HostCursor.None;
            return;
        }

        var shape = composeFrame.Cursor.Shape;
        // A shown prediction moves the caret. The gates above and the drawn
        // cursor below apply to the predicted caret as to the pane caret.
        var (cursorCol, cursorRow) = live.ComposePrediction is { Cursor: { } predicted } prediction
            && string.Equals(prediction.PaneId, paneId, StringComparison.Ordinal)
                ? (predicted.Col, predicted.Row)
                : (composeFrame.Cursor.Col, composeFrame.Cursor.Row);
        var col = Math.Clamp(cursorCol, 0, Math.Max(0, box.Cols - 1));
        var row = Math.Clamp(cursorRow, 0, Math.Max(0, box.Rows - 1));
        var visible = composeFrame.Cursor.Visible;
        if (reveal)
        {
            visible = true;
            shape = live.CjkIme.CursorShape;
        }

        // Some {visible:false}
        // including origin CUPs to inner_rect+viewport and hides. None parks
        // last-visible, else the host bottom-right cell. write_host_cursor_state
        // at render_ansi.rs:612-623 always CUPs that parked cell.

        if (HostCursorPolicy.IsDrawn(live.Ui.HostCursor) && visible && !reveal)
        {
            DrawnCursorPainter.Stamp(
                live.Host,
                box,
                col,
                row,
                CursorGlyphOf(live, paneId, cursor, col, row, composeFrame));
            cursor.DrawnHost = true;
            cursor.DrawnCol = col;
            cursor.DrawnRow = row;
            cursor.DrawnActive = true;
            live.Host.Cursor = new HostCursor(box.Col + col, box.Row + row, false, 0);
            return;
        }

        live.Host.Cursor = new HostCursor(
            box.Col + col,
            box.Row + row,
            visible,
            shape);
    }

    /// <summary>
    /// after chrome and pane cells. <c>Mode::Terminal => {}</c>.
    /// </summary>
    private static void StampPaintModeOverlay(
        AttachLiveState live,
        IHostCellSink sink,
        int cols,
        int rows,
        CellRect paneBox) =>
        // The overlay models (copy, help, navigator, settings, onboarding,
        // release notes) are key engine state. The key feed changes them.
        live.WithEngine(() => StampPaintModeOverlayUnderEngine(live, sink, cols, rows, paneBox));

    private static void StampPaintModeOverlayUnderEngine(
        AttachLiveState live,
        IHostCellSink sink,
        int cols,
        int rows,
        CellRect paneBox)
    {
        var mode = LayoutPaintMode(live);
        var theme = live.Theme.Palette;
        if (live.Worktrees.IsOpen)
        {
            WorktreeDialogPainter.Stamp(sink, live.Worktrees, cols, rows, theme);
            return;
        }

        if (live.CubesPairing.IsOpen)
        {
            CubePairingDialogPainter.Stamp(sink, live.CubesPairing, cols, rows, theme);
            return;
        }

        switch (mode)
        {
            case AttachClientMode.Terminal:
                if (!string.IsNullOrWhiteSpace(live.StatusError)
                    || !string.IsNullOrWhiteSpace(live.StatusNotice))
                    StampModeBar(live, sink, cols, rows, mode);
                return;
            case AttachClientMode.Copy:
                if (live.Engine.Copy.IsSeeded)
                {
                    live.GetLiveCursor(live.Chrome?.FocusedPaneId ?? live.PaneId).ClearDrawn();
                    CopyModePainter.Stamp(live.Host, live.Engine.Copy, paneBox, theme);
                    var view = live.Engine.Copy.PreparePaintSnapshot(paneBox.Rows);
                    var cursorCol = Math.Clamp(view.CursorCol, 0, Math.Max(0, paneBox.Cols - 1));
                    var cursorRow = Math.Clamp(view.CursorRow - Math.Max(0, view.ViewportTop), 0, Math.Max(0, paneBox.Rows - 1));
                    if (view.CursorRow >= view.ViewportTop
                        && view.CursorRow < view.ViewportTop + paneBox.Rows)
                    {
                        live.Host.Cursor = new HostCursor(
                            paneBox.Col + cursorCol,
                            paneBox.Row + cursorRow,
                            true,
                            0);
                    }
                    else
                        live.Host.Cursor = HostCursor.None;
                }

                StampModeBar(live, sink, cols, rows, mode);
                return;
            case AttachClientMode.Prefix:
            case AttachClientMode.Navigate:
            case AttachClientMode.Resize:
                StampModeBar(live, sink, cols, rows, mode);
                return;
            case AttachClientMode.KeybindHelp:
                StampOverlayLines(sink, live.Engine.Help.RenderLines(cols), cols, rows, theme);
                return;
            case AttachClientMode.Navigator:
            case AttachClientMode.WorkspacePicker:
            case AttachClientMode.TransferPicker:
                StampOverlayLines(sink, live.Engine.Navigator.RenderLines(cols), cols, rows, theme);
                return;
            case AttachClientMode.WhatsNew:
                ReleaseNotesPainter.Stamp(sink, live.Engine.ReleaseNotes, cols, rows, theme);
                return;
            case AttachClientMode.Onboarding:
                OnboardingPainter.Stamp(sink, live.Engine.Onboarding, cols, rows, theme, live.StatusError);
                return;
            case AttachClientMode.Settings:
                SettingsPainter.Stamp(sink, live.Engine.Settings, cols, rows, theme);
                return;
            case AttachClientMode.MobileSwitcher:
                if (live.Chrome is { MobileSwitcher: { Open: true } switcher } chrome)
                {
                    MobileSwitcherPainter.Stamp(
                        sink,
                        switcher,
                        chrome.MobileHeader,
                        cols,
                        rows,
                        theme);
                }

                return;
            case AttachClientMode.ContextMenu:
            case AttachClientMode.GlobalMenu:
                if ((live.Mouse.Menu ?? live.MouseMenu) is { } menu)
                    ContextMenuPainter.Stamp(sink, menu, theme, cols, rows);
                return;
            default:
                if (KeyEngine.IsPromptMode(mode))
                {
                    var slot = ModeBarModel.SlotFor(live.Ui.TabBarPosition);
                    var label = mode switch
                    {
                        AttachClientMode.RenameTab => "RENAME tab",
                        AttachClientMode.RenamePane => "RENAME pane",
                        AttachClientMode.NewTabName => "NEW tab",
                        AttachClientMode.NewWorkspaceName => "NEW workspace",
                        AttachClientMode.ConfirmClose => MoveWorkConfirmPrompt.ClosePaneText,
                        AttachClientMode.ConfirmMoveWork => live.Engine.PromptText,
                        _ => "RENAME workspace",
                    };
                    var text = mode is AttachClientMode.ConfirmClose
                        or AttachClientMode.ConfirmMoveWork
                        ? label
                        : $"{label}: {live.Engine.PromptText}_";
                    StampPromptBar(live, sink, slot, cols, rows, text, theme);
                    return;
                }

                if (!live.HasLivePane)
                {
                    var slot = ModeBarModel.SlotFor(live.Ui.TabBarPosition);
                    StampPromptBar(
                        live,
                        sink,
                        slot,
                        cols,
                        rows,
                        "NO PANE  prefix+c new-tab  prefix+shift+n new-workspace",
                        theme);
                    return;
                }

                if (live.Chrome is null
                    && (!string.IsNullOrWhiteSpace(live.StatusError)
                        || !string.IsNullOrWhiteSpace(live.StatusNotice)))
                {
                    var slot = ModeBarModel.SlotFor(live.Ui.TabBarPosition);
                    ModeBarPainter.StampLine(
                        sink,
                        slot is ModeBarSlot.Top ? 0 : Math.Max(0, rows - 1),
                        cols,
                        string.IsNullOrWhiteSpace(live.StatusError) ? live.StatusNotice! : live.StatusError,
                        theme);
                }

                return;
        }
    }

    private static void StampPromptBar(
        AttachLiveState live,
        IHostCellSink sink,
        ModeBarSlot slot,
        int cols,
        int rows,
        string text,
        ThemePalette theme)
    {
        var originCol = 0;
        var barCols = cols;
        var paintRow = slot is ModeBarSlot.Top ? 0 : Math.Max(0, rows - 1);
        if (live.Chrome is { TabRow: { Rows: > 0 } tabRow })
        {
            originCol = tabRow.Col;
            barCols = tabRow.Cols;
            paintRow = tabRow.Row;
        }
        else if (live.Chrome is { } chrome)
        {
            originCol = chrome.NamedSurfaces.Main.Col;
            barCols = chrome.NamedSurfaces.Main.Cols;
        }

        ModeBarPainter.StampLine(sink, paintRow, barCols, text, theme, originCol);
    }

    private static void StampModeBar(
        AttachLiveState live,
        IHostCellSink sink,
        int cols,
        int rows,
        AttachClientMode mode)
    {
        var slot = ModeBarModel.SlotFor(live.Ui.TabBarPosition);
        var area = ModeBarModel.OverlayRow(live.Chrome, cols, rows);
        ModeBarPainter.Stamp(
            sink,
            mode,
            slot,
            area.Cols,
            rows,
            live.Table,
            copy: mode is AttachClientMode.Copy ? live.Engine.Copy : null,
            row: area.Row,
            theme: live.Theme.Palette,
            originCol: area.Col,
            endpointError: live.StatusError,
            notice: live.StatusNotice);
    }

    private static void StampOverlayLines(
        IHostCellSink sink,
        IReadOnlyList<string> lines,
        int cols,
        int rows,
        ThemePalette theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        for (var i = 0; i < rows; i++)
        {
            var text = i < lines.Count ? lines[i] : "";
            sink.Write(0, i, SafeDisplayText.PadRight(text, cols), theme.Text, theme.PanelBg, cols);
        }
    }

    private static bool ExclusiveOverlayMode(AttachClientMode mode) =>
        KeyEngine.SuppressesLiveRemap(mode) && mode is not AttachClientMode.Copy;

    private static void NoteCopySnapshot(AttachLiveState live, AssembledSnapshot frame)
    {
        if (live.Engine.PaintMode is not AttachClientMode.Copy)
            return;
        if (!string.Equals(frame.PaneId, live.PaneId, StringComparison.Ordinal))
            return;
        live.Engine.Copy.MergeSnapshot(frame);
    }

    private static ClientOverlay.LivePaintPlan PlanPaneLivePaint(
        UnixRawTerminal tty,
        AttachLiveState? live,
        string? paneId,
        ReadOnlySpan<byte> bytes)
    {
        var id = paneId ?? live?.PaneId;
        var focused = live?.Chrome?.FocusedPaneId ?? live?.PaneId;
        ClientOverlayCarry carry;
        bool wasAlt;
        if (live is not null && !string.IsNullOrWhiteSpace(id))
        {
            carry = live.GetOverlayCarry(id);
            wasAlt = live.GetPaneAlt(id);
        }
        else
        {
            carry = tty.OverlayCarry;
            wasAlt = tty.AltScreenEntered;
        }

        var plan = ClientOverlay.PlanLivePaint(
            tty.ClientOverlayActive, wasAlt, bytes, carry);
        if (live is not null && !string.IsNullOrWhiteSpace(id))
        {
            var wasPaneAlt = live.GetPaneAlt(id);
            live.NotePaneAlt(id, plan.NowAlt);
            if (wasPaneAlt != plan.NowAlt)
                SyncPaneChromeGeometry(live, tty);
        }

        if (live is null
            || string.IsNullOrWhiteSpace(id)
            || string.IsNullOrWhiteSpace(focused)
            || string.Equals(id, focused, StringComparison.Ordinal))
        {
            tty.NoteAltScreen(plan.NowAlt);
        }

        return plan;
    }

    private static void NotePaneSnapshotAlt(
        UnixRawTerminal tty,
        AttachLiveState live,
        string paneId,
        AssembledSnapshot stored,
        bool wasAlt)
    {
        live.GetOverlayCarry(paneId).Clear();
        live.NotePaneAlt(paneId, stored.IsAlternateScreen);
        if (wasAlt != stored.IsAlternateScreen)
            SyncPaneChromeGeometry(live, tty);
        var focusedId = live.Chrome?.FocusedPaneId ?? live.PaneId;
        if (string.Equals(paneId, focusedId, StringComparison.Ordinal))
            tty.NoteAltScreen(SnapshotPainter.UsesAltScreen(stored));
    }

    private static bool TryPaneContent(LayoutChromeGeometry chrome, string? paneId, out CellRect box)
    {
        box = default;
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        foreach (var pane in chrome.Panes)
        {
            if (!string.Equals(pane.PaneId, paneId, StringComparison.Ordinal))
                continue;
            box = pane.Content;
            return box.Cols > 0 && box.Rows > 0;
        }

        return false;
    }

    private static bool TryPaneScrollbar(LayoutChromeGeometry chrome, string? paneId, out CellRect bar)
    {
        bar = default;
        if (!TryPaneFrame(chrome, paneId, out var pane) || pane.Scrollbar is not { } scrollbar)
            return false;
        bar = scrollbar;
        return bar.Rows > 0;
    }

    private static bool TryPaneFrame(
        LayoutChromeGeometry chrome,
        string? paneId,
        out ChromePaneFrame pane)
    {
        pane = default!;
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        foreach (var item in chrome.Panes)
        {
            if (!string.Equals(item.PaneId, paneId, StringComparison.Ordinal))
                continue;
            pane = item;
            return true;
        }

        return false;
    }

    internal static void ApplyCellsViewportOrigin(AttachLiveState live, TerminalRenderCellsPayload cells)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(cells);
        var paneId = cells.PaneId;
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        // WhenWritingDefault omits 0. A history Full can arrive before the
        // pane.scroll reply; apply origin>0 so caret policy cannot lag the
        // grid. Origin 0 stays on the reply / pane.scroll_changed clamp.
        if (cells.ViewportOrigin <= 0)
            return;
        var viewport = 0;
        if (live.Chrome is { } chrome && TryPaneContent(chrome, paneId, out var box))
            viewport = box.Rows;
        // The frame can be older than the last pane.scroll reply, so it only
        // raises the offset.
        if (live.RaisePaneScrollOrigin(paneId, cells.ViewportOrigin, viewport))
            SyncPaneScrollChrome(live, tty: null);
    }

    private static CellRect? TiledPopupOcclusion(
        AttachLiveState live,
        LayoutChromeGeometry chrome,
        string? paneId)
    {
        if (IsPopupPaneId(paneId))
            return null;
        if (live.Overlay.OwnsModal && live.Overlay.Frame() is { Outer.Cols: > 0, Outer.Rows: > 0 } overlay)
            return overlay.Outer;
        if (!live.PopupOpen)
            return null;
        return chrome.PopupFrame?.Outer is { Cols: > 0, Rows: > 0 } outer ? outer : null;
    }

    private static void HidePopupFrame(AttachLiveState live)
    {
        if (live.Chrome is { } chrome)
            live.Chrome = chrome with { PopupFrame = null };
    }

    private static bool PopupOwnsHostCaret(AttachLiveState live) =>
        live.PopupOpen && live.Chrome?.PopupFrame is { Inner.Cols: > 0, Inner.Rows: > 0 };

    private static bool OverlayOwnsHostCaret(AttachLiveState live) =>
        HostCursorPolicy.OverlayOwnsCaret(
            LayoutPaintMode(live),
            live.History.ShowsOverlay,
            PopupOwnsHostCaret(live))
        || live.Splash is { IsPlaying: true };

    private static bool IsPopupPaneId(string? paneId) =>
        string.Equals(paneId, ProtocolEventTypes.TerminalRenderTargetPopup, StringComparison.Ordinal);

    private static string? CursorGlyphOf(
        AttachLiveState live,
        string paneId,
        PaneLiveCursor cursor,
        int col,
        int row,
        AssembledSnapshot? generationFrame = null)
    {
        var liveGlyph = cursor.GlyphAt(col, row);
        if (liveGlyph is not null)
            return liveGlyph.Length == 0 ? " " : liveGlyph;
        var frame = generationFrame is not null
            && string.Equals(paneId, generationFrame.PaneId, StringComparison.Ordinal)
            ? generationFrame
            : live.TryGetPaneFrame(paneId, out var stored)
                ? stored
                : null;
        if (frame is null)
            return null;
        if (row < 0 || row >= frame.Rows || col < 0 || col >= frame.Cols)
            return null;
        if (frame.Cells is null || row >= frame.Cells.Count)
            return null;
        var cells = frame.Cells[row];
        if (cells is null || col >= cells.Count)
            return null;
        return cells[col].Text;
    }

    internal static void ApplyWindowTitle(UnixRawTerminal tty, AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        var sequence = WindowTitleApplier.Sequence(
            live.WindowTitleOverride,
            live.Ui.WindowTitle,
            TitleValuesOf(live));
        if (sequence is null)
            return;
        tty.WriteWindowTitle(sequence);
    }

    /// <summary>
    /// Sends the session summary to the host terminal through the one host writer.
    /// The writer drops a report that did not change.
    /// </summary>
    internal static void ApplyHostStatus(UnixRawTerminal tty, AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        tty.WriteHostStatus(live.HostStatus.Sequence(live.Ui.HostStatus, live.SessionStatus));
    }

    internal static bool IsVisiblePane(AttachLiveState live, string? paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        if (string.Equals(paneId, live.PaneId, StringComparison.Ordinal))
            return true;
        if (OverlayIsVisiblePane(live, paneId))
            return true;
        if (live.Chrome is not { } chrome)
            return false;
        foreach (var pane in chrome.Panes)
        {
            if (string.Equals(pane.PaneId, paneId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void NoteInvisibleRender(AttachLiveState live, JsonElement payload)
    {
        if (!payload.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String
            || !string.Equals(
                kind.GetString(),
                TerminalRenderSnapshotPayload.KindSnapshot,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!payload.TryGetProperty("pane_id", out var pid) || pid.ValueKind != JsonValueKind.String)
            return;
        var paneId = pid.GetString();
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        AttachProcessLog.Observe(
            live.ProcessLog,
            live.SessionName,
            live.AttachClientId,
            paneId,
            ProcessLogEvents.OutcomeDropped,
            "not_visible");
    }

    private static void NoteObserveFault(AttachLiveState live, Exception ex)
    {
        AttachProcessLog.Observe(
            live.ProcessLog,
            live.SessionName,
            live.AttachClientId,
            live.PaneId,
            ProcessLogEvents.OutcomeError,
            ex.GetType().Name + ": " + ex.Message);
    }

    internal static bool IsPopupRenderPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return false;
        if (!payload.TryGetProperty("target", out var target)
            || target.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return string.Equals(
            target.GetString(),
            ProtocolEventTypes.TerminalRenderTargetPopup,
            StringComparison.Ordinal);
    }

    internal static void ApplyPopupFromSnapshot(AttachLiveState live, JsonElement snap)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (snap.ValueKind != JsonValueKind.Object
            || !snap.TryGetProperty("popup", out var popup)
            || popup.ValueKind != JsonValueKind.Object)
        {
            SetPopupOpen(live, open: false);
            SyncPopupChrome(live);
            return;
        }

        var open = popup.TryGetProperty("open", out var openEl)
            && openEl.ValueKind == JsonValueKind.True;
        var cols = popup.TryGetProperty("cols", out var colsEl) && colsEl.TryGetInt32(out var c)
            ? c
            : 0;
        var rows = popup.TryGetProperty("rows", out var rowsEl) && rowsEl.TryGetInt32(out var r)
            ? r
            : 0;
        var outerCols = popup.TryGetProperty("outer_cols", out var ocEl) && ocEl.TryGetInt32(out var oc)
            ? oc
            : 0;
        var outerRows = popup.TryGetProperty("outer_rows", out var orEl) && orEl.TryGetInt32(out var orv)
            ? orv
            : 0;
        SetPopupOpen(
            live,
            open,
            cols,
            rows,
            TryReadPopupSize(popup, "width"),
            TryReadPopupSize(popup, "height"),
            outerCols,
            outerRows);
        SyncPopupChrome(live);
    }

    internal static void SetPopupOpen(
        AttachLiveState live,
        bool open,
        int cols = 0,
        int rows = 0,
        PopupSize? width = null,
        PopupSize? height = null,
        int outerCols = 0,
        int outerRows = 0)
    {
        ArgumentNullException.ThrowIfNull(live);
        var wasOpen = live.PopupOpen;
        live.PopupOpen = open;
        live.Engine.PopupOpen = open;
        if (!open)
        {
            live.ClearSnapshotRender(ProtocolEventTypes.TerminalRenderTargetPopup);
            live.PopupSawClosed = true;
            live.PopupClosedSeq = Math.Max(live.PopupClosedSeq, live.PopupOpenedSeq);
            live.PopupSnapshot = null;
            live.PopupFillInner = false;
            live.PopupInnerCols = 0;
            live.PopupInnerRows = 0;
            live.PopupOuterCols = 0;
            live.PopupOuterRows = 0;
            live.PopupLockInnerGeometry = false;
            live.PopupPaintContentCols = 0;
            live.PopupPaintContentRows = 0;
            live.PopupRequestedWidth = null;
            live.PopupRequestedHeight = null;
            return;
        }

        // Closed-to-open reopen drops a stale snapshot-only marker so a non-VT
        // popup can Remap. Stay-open session.snapshot must keep the marker.
        if (!wasOpen)
            live.ClearSnapshotRender(ProtocolEventTypes.TerminalRenderTargetPopup);

        live.PopupSawClosed = false;
        live.PopupFillInner = true;
        live.PopupInnerCols = cols;
        live.PopupInnerRows = rows;
        if (outerCols > 0)
            live.PopupOuterCols = outerCols;
        if (outerRows > 0)
            live.PopupOuterRows = outerRows;
        live.PopupLockInnerGeometry = true;
        live.PopupPaintContentCols = 0;
        live.PopupPaintContentRows = 0;
        live.PopupRequestedWidth = width ?? live.PopupRequestedWidth ?? PopupGeometry.DefaultSize;
        live.PopupRequestedHeight = height ?? live.PopupRequestedHeight ?? PopupGeometry.DefaultSize;
    }

    internal static void SyncPopupChrome(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.ChromeEnabled)
            return;
        if (live.ChromeSeed is not null)
        {
            RecomputeChromeFromSeed(live, tty: null);
            return;
        }

        if (live.Chrome is not { } chrome)
            return;
        if (!live.PopupOpen)
        {
            live.Chrome = chrome with { PopupFrame = null };
            return;
        }

        UnlockPopupGeometryIfContentChanged(live, chrome.Content);
        var resolved = ResolveLivePopupGeometry(live, chrome.Content);
        if (resolved is null)
        {
            live.Chrome = chrome with { PopupFrame = null };
            return;
        }

        live.Chrome = chrome with { PopupFrame = PopupPainter.FrameFromGeometry(chrome.Content, resolved) };
    }

    internal static PopupGeometryResult? ResolveLivePopupGeometry(AttachLiveState live, CellRect content)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.PopupLockInnerGeometry)
        {
            if (live.PopupOuterCols >= PopupGeometry.MinOuterCols
                && live.PopupOuterRows >= PopupGeometry.MinOuterRows)
            {
                return PopupGeometry.TryFromOuter(
                    content.Cols,
                    content.Rows,
                    live.PopupOuterCols,
                    live.PopupOuterRows,
                    live.PopupInnerCols,
                    live.PopupInnerRows);
            }

            if (live.PopupInnerCols > 0 && live.PopupInnerRows > 0)
            {
                return PopupGeometry.TryFromInner(
                    content.Cols,
                    content.Rows,
                    live.PopupInnerCols,
                    live.PopupInnerRows);
            }
        }

        if (live.PopupRequestedWidth is not null || live.PopupRequestedHeight is not null)
        {
            return PopupGeometry.TryResolve(
                content.Cols,
                content.Rows,
                live.PopupRequestedWidth,
                live.PopupRequestedHeight);
        }

        if (live.PopupInnerCols > 0 && live.PopupInnerRows > 0)
        {
            return PopupGeometry.TryFromInner(
                content.Cols,
                content.Rows,
                live.PopupInnerCols,
                live.PopupInnerRows);
        }

        return PopupGeometry.TryResolve(content.Cols, content.Rows);
    }

    private static void UnlockPopupGeometryIfContentChanged(AttachLiveState live, CellRect content)
    {
        if (!live.PopupLockInnerGeometry)
            return;
        if (live.PopupPaintContentCols < 1 || live.PopupPaintContentRows < 1)
        {
            live.PopupPaintContentCols = content.Cols;
            live.PopupPaintContentRows = content.Rows;
            return;
        }

        if (content.Cols != live.PopupPaintContentCols
            || content.Rows != live.PopupPaintContentRows)
        {
            live.PopupLockInnerGeometry = false;
        }

        live.PopupPaintContentCols = content.Cols;
        live.PopupPaintContentRows = content.Rows;
    }

    private static PopupSize? TryReadPopupSize(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object
            || !obj.TryGetProperty(name, out var el)
            || !PopupSize.TryRead(el, out var size))
        {
            return null;
        }

        return size;
    }

    internal static void ApplyPopupRenderPayload(
        JsonElement payload,
        SnapshotAssembler assembler,
        UnixRawTerminal tty,
        AttachLiveState live)
    {
        if (!live.PopupOpen)
            return;

        if (payload.TryGetProperty("kind", out var kind)
            && kind.ValueKind == JsonValueKind.String
            && kind.GetString() == TerminalRenderSnapshotPayload.KindSnapshot)
        {
            var slice = DeserializePayload(
                payload, ProtocolJsonContext.Default.TerminalRenderSnapshotPayload);
            if (slice is null)
                return;
            slice = slice with
            {
                PaneId = ProtocolEventTypes.TerminalRenderTargetPopup,
                Target = ProtocolEventTypes.TerminalRenderTargetPopup,
            };
            if (slice.GridCols > 0)
                live.PopupInnerCols = slice.GridCols;
            if (slice.GridRows > 0)
                live.PopupInnerRows = slice.GridRows;
            if (assembler.TryAdd(slice, out var frame) && frame is not null)
            {
                live.PopupSnapshot = frame;
                live.SetPaneFrame(frame);
                SyncPopupChrome(live);
                PaintChrome(tty, live, paintPopupInnerSnapshot: true);
                if (SnapshotContentWasPainted(live, ProtocolEventTypes.TerminalRenderTargetPopup))
                    live.NoteSnapshotRender(ProtocolEventTypes.TerminalRenderTargetPopup);
            }

            return;
        }

        SyncPopupChrome(live);
        // Popup live bytes never CUP-remap leftover ANSI onto chrome.
        if (live.IsSnapshotCapable(ProtocolEventTypes.TerminalRenderTargetPopup))
        {
            if (live.Chrome?.PopupFrame is not null)
                PaintChrome(tty, live);
            return;
        }

        if (payload.TryGetProperty("data", out var dataEl)
            && dataEl.ValueKind == JsonValueKind.String
            && dataEl.GetString() is { Length: > 0 } b64)
        {
            try
            {
                ApplyLiveRender(
                    tty,
                    Convert.FromBase64String(b64),
                    live,
                    ProtocolEventTypes.TerminalRenderTargetPopup,
                    ReadRenderRoute(payload));
            }
            catch (FormatException)
            {
            }
        }
        else if (live.Chrome?.PopupFrame is not null)
        {
            PaintChrome(tty, live);
        }
    }

    private static int ReadStdin(
        byte[] buf,
        List<byte> csi,
        CancellationToken ct,
        Func<bool>? abort = null,
        Action? onIdle = null)
    {
        var pendingEscape = csi.Count > 0;
        var timeoutMs = StdinPollTimeoutMs(csi, animateIdle: onIdle is not null);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (abort?.Invoke() == true)
                throw new OperationCanceledException(ct);
            if (UnixRawTerminal.PollReadable(0, timeoutMs))
            {
                var n = UnixRawTerminal.TryRead(0, buf);
                if (n < 0)
                    continue;
                return n;
            }

            if (pendingEscape)
                throw new OperationCanceledException(ct);
            onIdle?.Invoke();
        }
    }

    private async Task<bool> DispatchKeysAsync(
        UnixRawTerminal tty,
        AttachLiveState live,
        List<byte> decoded,
        ControlPlaneClient control,
        SemaphoreSlim controlGate,
        CancellationTokenSource linked,
        CancellationToken ct)
    {
        control = LiveControlClient(live, control) ?? control;
        var uiPort = LiveControlPort(live, new ControlPlaneAttachCommandPort(control));
        var (events, serializeFeed) = await FeedKeysAfterControlGateAsync(
                live, decoded, controlGate, ct)
            .ConfigureAwait(false);
        if (events.Count > 0 && DismissStatusErrorOnInput(live))
            PaintChrome(tty, live);
        events = CoalescePaneBytes(events);
        try
        {
            foreach (var ev in events)
            {
                if (ev.Kind == KeyEngineEventKind.HideOverlay)
                {
                    await ApplyHideOverlayEventAsync(
                            tty,
                            live,
                            uiPort,
                            ct)
                        .ConfigureAwait(false);
                    continue;
                }

                if (ev.Kind == KeyEngineEventKind.Detach)
                {
                    AttachPathTrace.RecordInput(
                        AttachPathTrace.StageDetachRequest,
                        live.PaneId,
                        CollectionsMarshal.AsSpan(decoded));
                    live.DetachRequested = true;
                    linked.Cancel();
                    return true;
                }

                if ((ev.Kind == KeyEngineEventKind.SendPopupBytes
                        || (ev.Kind == KeyEngineEventKind.SendPaneBytes
                            && string.Equals(ev.TargetId, "popup", StringComparison.Ordinal)))
                    && ev.Bytes is { Length: > 0 } popupPayload)
                {
                    await SendPaneKeysUnderGateAsync(
                            async token =>
                            {
                                var keys = new JsonObject
                                {
                                    ["encoding"] = "base64",
                                    ["data"] = Convert.ToBase64String(popupPayload),
                                };
                                if (!string.IsNullOrWhiteSpace(live.InputLease))
                                    keys["lease_id"] = live.InputLease;
                                await (LiveControlClient(live, control) ?? control).CallAsync(
                                        ProtocolMethods.PopupSendKeys,
                                        keys,
                                        token)
                                    .ConfigureAwait(false);
                            },
                            controlGate,
                            live,
                            tty,
                            linked,
                            ct,
                            gateHeld: serializeFeed,
                            drain: token => DrainOverlayEventsAsync(
                                control, live, tty, token, linked))
                        .ConfigureAwait(false);

                    continue;
                }

                if (ev.Kind == KeyEngineEventKind.SendPaneBytes
                    && ev.Bytes is { Length: > 0 } payload)
                {
                    if (OverlayDispatchReady(live, ev.TargetId))
                    {
                        await SendPaneKeysUnderGateAsync(
                                async token =>
                                {
                                    await control.NotifyAsync(
                                            ProtocolMethods.PaneSendKeys,
                                            OverlaySendKeysBody(live, payload, keyboard: !ev.Paste),
                                            token)
                                        .ConfigureAwait(false);
                                },
                                controlGate,
                                live,
                                tty,
                                linked,
                                ct,
                                gateHeld: serializeFeed,
                                drain: token => DrainOverlayEventsAsync(
                                    control, live, tty, token, linked))
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (!live.HasLivePane)
                        continue;

                    AttachPathTrace.RecordInput(AttachPathTrace.StageDispatch, live.PaneId, payload);

                    if (BlocksPaneKeys(live))
                        continue;

                    if (live.InputSender is { } sender)
                    {
                        if (sender.IsFaulted)
                        {
                            if (!NoteInputSenderFault(live, sender.Fault))
                                linked.Cancel();
                            continue;
                        }

                        // Wait for buffer space rather than drop: a paste
                        // stalls stdin reads (the host TTY holds the rest)
                        // instead of losing text or detaching.
                        // The mark binds the predictions of these keys to
                        // the seq of their batch. Prediction changes only
                        // the paint, after the bytes are queued.
                        var mark = live.Prediction.Predicts ? live.Prediction.ReserveMark(live.PaneId) : 0;
                        // The frame before these keys go out. A frame read
                        // after the send can already hold their echo, and a
                        // prediction from it would draw one cell too far.
                        AssembledSnapshot? preSend = null;
                        var preSendPane = mark != 0 ? live.PaneId : null;
                        if (preSendPane is not null)
                            live.TryGetPaneFrame(preSendPane, out preSend);
                        if (!await sender.EnqueueAsync(payload, ct, live.Latency.StampKeys(payload) with { Mark = mark }, keyboard: !ev.Paste)
                                .ConfigureAwait(false))
                        {
                            live.Prediction.Reset(PredictionResetReason.InputDropped);
                            if (sender.IsFaulted && !NoteInputSenderFault(live, sender.Fault))
                                linked.Cancel();
                        }
                        else if (mark != 0)
                        {
                            ObservePaneInput(tty, live, payload, mark, ev.Paste, preSend, preSendPane);
                        }

                        continue;
                    }

                    // Fallback pane.send_keys is one-way.
                    // writes and continues (src/client/mod.rs:1594-1597).
                    // It sends no seq, so its keys end every epoch.
                    NoteUnsequencedPaneInput(tty, live);
                    await SendPaneKeysUnderGateAsync(
                            async token =>
                            {
                                await control.NotifyAsync(
                                        ProtocolMethods.PaneSendKeys,
                                        BuildPaneSendKeys(live.PaneId!, payload, live.InputLease, keyboard: !ev.Paste),
                                        token)
                                    .ConfigureAwait(false);
                            },
                            controlGate,
                            live,
                            tty,
                            linked,
                            ct,
                            gateHeld: serializeFeed,
                            drain: token => DrainOverlayEventsAsync(
                                control, live, tty, token, linked))
                        .ConfigureAwait(false);

                    continue;
                }

                if (ev.Kind == KeyEngineEventKind.Dispatch && ev.ToRequest() is { } request)
                {
                    request = BindCloseRequestTarget(live, request);
                    if (!serializeFeed)
                        await controlGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        try
                        {
                            await DispatchLayoutActionAsync(
                                    request, live, uiPort, ct, tty)
                                .ConfigureAwait(false);
                            if (request.Action is not KeyActionId.ReloadConfig)
                                ClearTransientStatusError(live, StatusOwner.Command);
                            if (!IsEmptyRenameEnter(request))
                            {
                                await SyncFocusAsync(
                                        uiPort,
                                        live,
                                        ct,
                                        observePending: false)
                                    .ConfigureAwait(false);
                            }
                            await RefreshChromeAsync(
                                    uiPort, live, tty, ct)
                                .ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                            linked.Cancel();
                            throw;
                        }
                        catch (ObjectDisposedException)
                        {
                            linked.Cancel();
                            throw;
                        }
                        catch (ControlPlaneException ex)
                        {
                            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                            await RefreshChromeAfterCloseFaultAsync(
                                    request,
                                    live,
                                    uiPort,
                                    tty,
                                    ct)
                                .ConfigureAwait(false);
                        }
                        catch (InvalidOperationException ex)
                        {
                            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                            await RefreshChromeAfterCloseFaultAsync(
                                    request,
                                    live,
                                    uiPort,
                                    tty,
                                    ct)
                                .ConfigureAwait(false);
                        }
                        catch (ControlPlaneClientTimeoutException)
                        {
                            linked.Cancel();
                            throw;
                        }
                        finally
                        {
                            if (!linked.IsCancellationRequested)
                            {
                                await DrainOverlayEventsAsync(
                                        control, live, tty, ct, linked)
                                    .ConfigureAwait(false);
                            }
                        }
                    }
                    finally
                    {
                        if (!serializeFeed)
                            controlGate.Release();
                    }

                    PaintChrome(tty, live);
                    continue;
                }

                ApplyPendingModeEvent(live, ev);
                if (ev.Kind is KeyEngineEventKind.EnterMode or KeyEngineEventKind.LeaveMode)
                {
                    await PublishClientModeAsync(
                            uiPort,
                            live,
                            ct)
                        .ConfigureAwait(false);
                }

                if (ev.Kind is KeyEngineEventKind.LeaveMode
                    && ev.Mode is AttachClientMode.ConfirmMoveWork)
                {
                    await CompletePendingMoveWorkAsync(
                            live,
                            uiPort,
                            tty,
                            ct)
                        .ConfigureAwait(false);
                }

                if (ev.Kind is KeyEngineEventKind.LeaveMode
                    && ev.Mode is AttachClientMode.TransferPicker)
                {
                    await CompletePendingTransferPickAsync(
                            live,
                            uiPort,
                            tty,
                            ct)
                        .ConfigureAwait(false);
                }

                if (await ApplyMenuKeyEventAsync(ev, live, uiPort, tty, ct)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                if (ev.Kind is KeyEngineEventKind.EnterMode or KeyEngineEventKind.HelpFilter)
                {
                    await HandleSettingsOverlayAsync(
                            ev,
                            live,
                            uiPort,
                            ct)
                        .ConfigureAwait(false);
                }

                var settingsLeave = ev.Kind is KeyEngineEventKind.HelpFilter
                    && await TryLeaveSettingsAfterPatchAsync(
                            live,
                            uiPort,
                            ct)
                        .ConfigureAwait(false);
                var onboardingDone = ev.Kind is KeyEngineEventKind.HelpFilter
                    && await TryCompleteOnboardingAsync(live, uiPort, ct).ConfigureAwait(false);
                var releaseNotesOpen = ev.Kind is KeyEngineEventKind.HelpFilter
                    && TryOpenReleaseNotesFromSettings(live);
                var releaseNotesDismissed = ev.Kind is KeyEngineEventKind.HelpFilter
                    && ev.Filter == ReleaseNotesOverlayModel.FilterDismiss
                    && TryDismissReleaseNotes(live);
                var overlayCommit = settingsLeave || onboardingDone || releaseNotesOpen || releaseNotesDismissed;

                if (ev.Kind is KeyEngineEventKind.EnterMode
                    or KeyEngineEventKind.LeaveMode
                    or KeyEngineEventKind.HelpFilter
                    or KeyEngineEventKind.PromptEdit
                    or KeyEngineEventKind.CopyChanged
                    or KeyEngineEventKind.CopyYank)
                {
                    if (ev.Kind is KeyEngineEventKind.EnterMode
                        && ev.Mode is AttachClientMode.Copy
                        && ShouldSeedCopyOnEnter(live))
                    {
                        if (!serializeFeed)
                            await controlGate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            await SeedCopyModeAsync(
                                    live, uiPort, ct)
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            if (!serializeFeed)
                                controlGate.Release();
                        }
                    }

                    if (ev.Kind is KeyEngineEventKind.LeaveMode && ev.Mode is AttachClientMode.Copy)
                        live.Engine.Copy.Reset();

                    if (ev.Kind is KeyEngineEventKind.CopyYank && ev.Bytes is { Length: > 0 } osc)
                    {
                        live.LastOsc52 = osc;
                        tty.WriteBytes(osc);
                        ShowClipboardToast(live, ev.PromptText ?? "copied");
                    }

                    if (ev.Kind is KeyEngineEventKind.EnterMode
                        && ev.Mode is AttachClientMode.Navigator
                            or AttachClientMode.WorkspacePicker
                            or AttachClientMode.TransferPicker)
                    {
                        HydratePickerCatalog(live);
                    }

                    var restoreFrame = OverlayRestoreFrame(ev, settingsLeave, live);
                    var noteSnapshot = OverlayLeaveNotesSnapshot(restoreFrame, live);
                    if (ev.Kind is KeyEngineEventKind.EnterMode)
                        live.Engine.CommitPaintMode();
                    if (ev.Kind is KeyEngineEventKind.EnterMode
                        or KeyEngineEventKind.LeaveMode
                        || overlayCommit)
                    {
                        if (!serializeFeed)
                            await controlGate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            if (ShouldReportAttachClientMode(ev, overlayCommit))
                            {
                                await ReportAttachClientModeAsync(
                                        uiPort, live, ct)
                                    .ConfigureAwait(false);
                            }

                            await RefreshChromeForPaintModeAsync(
                                    uiPort, live, tty, ct)
                                .ConfigureAwait(false);
                            await DrainOverlayEventsAsync(control, live, tty, ct, linked)
                                .ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                            linked.Cancel();
                            throw;
                        }
                        catch (ObjectDisposedException)
                        {
                            linked.Cancel();
                            throw;
                        }
                        catch (ControlPlaneClientTimeoutException)
                        {
                            linked.Cancel();
                            throw;
                        }
                        finally
                        {
                            if (!serializeFeed)
                                controlGate.Release();
                        }
                    }

                    ApplyOverlayPaint(tty, live, restoreFrame, noteSnapshot);
                }
            }

            return false;
        }
        finally
        {
            if (serializeFeed)
                controlGate.Release();
        }
    }

    private async Task<bool> DispatchMouseAsync(
        UnixRawTerminal tty,
        AttachLiveState live,
        MouseEvent ev,
        ControlPlaneClient control,
        SemaphoreSlim controlGate,
        CancellationTokenSource linked,
        CancellationToken ct) =>
        await DispatchMouseAttachAsync(
                tty,
                live,
                ev,
                LiveControlPort(
                    live,
                    new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control)),
                controlGate,
                linked,
                ct)
            .ConfigureAwait(false);

    internal static Task<bool> DispatchMouseAttachAsync(
        UnixRawTerminal tty,
        AttachLiveState live,
        MouseEvent ev,
        IAttachCommandPort port,
        SemaphoreSlim controlGate,
        CancellationTokenSource linked,
        CancellationToken ct) =>
        DispatchMouseAttachAsync(tty, live, [ev], port, controlGate, linked, ct);

    internal static async Task<bool> DispatchMouseAttachAsync(
        UnixRawTerminal tty,
        AttachLiveState live,
        IReadOnlyList<MouseEvent> events,
        IAttachCommandPort port,
        SemaphoreSlim controlGate,
        CancellationTokenSource linked,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(controlGate);
        ArgumentNullException.ThrowIfNull(linked);
        port = LiveControlPort(live, port);
        if (events.Count == 0)
            return live.DetachRequested;
        if (DismissStatusErrorOnInput(live))
            PaintChrome(tty, live);
        if (await TryHandleWorktreeDialogMouseAsync(tty, live, events, port, ct).ConfigureAwait(false))
            return live.DetachRequested;
        if (await TryHandleCubePairingDialogMouseAsync(tty, live, events, port, ct).ConfigureAwait(false))
            return live.DetachRequested;
        if (live.Mouse.Menu is not null && AreMenuHoverMotions(events))
        {
            var changed = false;
            var ctx = MouseFeedContextFor(live);
            foreach (var item in events)
            {
                var fed = live.Mouse.Feed(item, ctx);
                if (fed.Count > 0)
                    changed = true;
            }

            if (changed)
            {
                try
                {
                    PaintMenuHover(tty, live);
                }
                catch (IOException)
                {
                    linked.Cancel();
                    throw;
                }
                catch (ObjectDisposedException)
                {
                    linked.Cancel();
                    throw;
                }
            }

            return live.DetachRequested;
        }

        var ev = events[0];
        if (live.Engine.Mode is AttachClientMode.Onboarding)
        {
            if (ev.IsWheel)
                return live.DetachRequested;
        }
        else if (live.Engine.Mode is AttachClientMode.WhatsNew && ev.IsWheel)
        {
            var delta = 0;
            foreach (var item in events)
            {
                if (item.IsWheel)
                    delta += item.Button is MouseButton.WheelUp ? -1 : 1;
            }

            if (delta != 0)
                ApplyReleaseNotesWheel(live, delta, tty);
            return live.DetachRequested;
        }
        else if (live.Overlay.OwnsModal
            && ev.IsWheel
            && ClientOverlayInput.SwallowOutsideClick(live.Overlay, ev))
        {
            return live.DetachRequested;
        }
        else if (live.Engine.Mode is AttachClientMode.Settings && ev.IsWheel)
        {
            var delta = 0;
            foreach (var item in events)
            {
                if (item.IsWheel)
                    delta += item.Button is MouseButton.WheelUp ? -1 : 1;
            }

            if (delta != 0)
                ApplySettingsWheel(live, delta, tty);
            return live.DetachRequested;
        }

        var hit = ChromeForHitTest(live) is { } geo
            ? ChromeHitTest.Hit(
                geo,
                ev.Col,
                ev.Row,
                live.Mouse.Menu,
                live.Engine.PaintMode,
                live.PopupOpen)
            : null;
        var seedPane = live.PopupOpen
            ? null
            : MouseHistorySeedPaneId(hit, ev, live.Mouse);
        if (seedPane is not null)
        {
            await controlGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EnsureHistorySeededAsync(live, port, seedPane, ct).ConfigureAwait(false);
            }
            finally
            {
                controlGate.Release();
            }
        }

        // The release that opens settings is outside the panel. Do not treat
        // that same release as a click that dismisses the overlay.
        var settingsWasOpen = live.Engine.Mode is AttachClientMode.Settings;
        List<MouseEngineResult>? collected = null;
        foreach (var item in events)
        {
            var fed = FeedMouseWithAttachContext(live, item, seedPane, hit);
            if (fed.Count == 0)
                continue;
            collected ??= new List<MouseEngineResult>(fed.Count);
            collected.AddRange(fed);
        }

        if (collected is null || collected.Count == 0)
        {
            await ApplyExclusiveUiPointerIfClickAsync(
                    tty, live, ev, port, linked, ct, controlGate, settingsWasOpen)
                .ConfigureAwait(false);
            return live.DetachRequested;
        }

        IReadOnlyList<MouseEngineResult> results = CollapseConsecutiveScrollHistory(collected);

        if (AreLocalMenuHoverResults(results) && live.Mouse.Menu is not null)
        {
            // highlight only. src/app/runtime.rs:102-119 drains then renders
            // once. Do not share the control ping gate with origin paint.
            try
            {
                PaintMenuHover(tty, live);
            }
            catch (IOException)
            {
                linked.Cancel();
                throw;
            }
            catch (ObjectDisposedException)
            {
                linked.Cancel();
                throw;
            }

            return live.DetachRequested;
        }

        List<MouseEngineResult>? gated = null;
        List<MouseEngineResult>? serverScroll = null;
        foreach (var result in results)
        {
            if (IsServerScrollMouseResult(result))
                (serverScroll ??= []).Add(result);
            else
                (gated ??= []).Add(result);
        }

        if (gated is { Count: > 0 })
        {
            await controlGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var result in gated)
                {
                    if (await ApplyMouseResultAsync(
                                result,
                                live,
                                port,
                                tty,
                                linked,
                                ct,
                                controlGate,
                                controlGateHeld: true)
                            .ConfigureAwait(false))
                    {
                        return true;
                    }
                }
            }
            catch (IOException)
            {
                linked.Cancel();
                throw;
            }
            catch (ObjectDisposedException)
            {
                linked.Cancel();
                throw;
            }
            catch (ControlPlaneException ex)
            {
                live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                PaintChrome(tty, live);
            }
            catch (InvalidOperationException ex)
            {
                live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                PaintChrome(tty, live);
            }
            catch (ControlPlaneClientTimeoutException)
            {
                linked.Cancel();
                throw;
            }
            finally
            {
                controlGate.Release();
            }
        }

        if (serverScroll is { Count: > 0 })
        {
            // does not share a control ping gate with origin paint.
            try
            {
                foreach (var result in serverScroll)
                {
                    if (await ApplyMouseResultAsync(result, live, port, tty, linked, ct)
                        .ConfigureAwait(false))
                    {
                        return true;
                    }
                }
            }
            catch (IOException)
            {
                linked.Cancel();
                throw;
            }
            catch (ObjectDisposedException)
            {
                linked.Cancel();
                throw;
            }
            catch (ControlPlaneException ex)
            {
                live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                PaintChrome(tty, live);
            }
            catch (InvalidOperationException ex)
            {
                live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                PaintChrome(tty, live);
            }
            catch (ControlPlaneClientTimeoutException)
            {
                linked.Cancel();
                throw;
            }
        }

        await ApplyExclusiveUiPointerIfClickAsync(
                tty, live, ev, port, linked, ct, controlGate, settingsWasOpen)
            .ConfigureAwait(false);
        return live.DetachRequested;
    }

    private static async Task ApplyExclusiveUiPointerIfClickAsync(
        UnixRawTerminal tty,
        AttachLiveState live,
        MouseEvent ev,
        IAttachCommandPort port,
        CancellationTokenSource linked,
        CancellationToken ct,
        SemaphoreSlim controlGate,
        bool settingsWasOpen)
    {
        if (ev.Button is not MouseButton.Left || ev.Action is not MouseAction.Release)
            return;
        if (live.Mouse.Selection.Active || live.Mouse.State is MouseEngineState.Selecting)
            return;
        if (live.CubesPairing.IsOpen || live.Worktrees.IsOpen)
            return;

        if (live.Engine.Mode is AttachClientMode.Onboarding)
        {
            await ApplyOnboardingPointerAsync(live, ev.Col, ev.Row, port, tty, ct)
                .ConfigureAwait(false);
            return;
        }

        if (live.Engine.Mode is AttachClientMode.WhatsNew)
        {
            await ApplyReleaseNotesPointerAsync(live, ev.Col, ev.Row, port, tty, ct)
                .ConfigureAwait(false);
            return;
        }

        if (live.Overlay.OwnsModal)
        {
            if (!live.Overlay.ContainsInner(ev.Col, ev.Row))
                return;
            var press = new MouseEvent(
                ev.Button,
                MouseAction.Press,
                ev.Col,
                ev.Row,
                ev.Shift,
                ev.Alt,
                ev.Ctrl);
            if (ClientOverlayInput.ForwardInner(live.Overlay, press) is { } overlayPress)
            {
                await ApplyMouseResultAsync(overlayPress, live, port, tty, linked, ct, controlGate)
                    .ConfigureAwait(false);
            }

            if (ClientOverlayInput.ForwardInner(live.Overlay, ev) is { } overlayRelease)
            {
                await ApplyMouseResultAsync(overlayRelease, live, port, tty, linked, ct, controlGate)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (live.Engine.Mode is AttachClientMode.Settings)
        {
            if (!settingsWasOpen)
                return;
            await ApplySettingsPointerUnderGateAsync(live, ev.Col, ev.Row, port, tty, controlGate, ct)
                .ConfigureAwait(false);
        }
    }

    private static bool IsServerScrollMouseResult(MouseEngineResult result) =>
        result.Kind is MouseCommandKind.ScrollHistory or MouseCommandKind.SetHistoryTop or MouseCommandKind.RefreshScrollMetrics;

    private static bool IsLocalMenuHoverResult(MouseEngineResult result) =>
        result.Kind is MouseCommandKind.None
        && result.State is MouseEngineState.Menu
        && result.Menu is not null;

    private static bool AreLocalMenuHoverResults(IReadOnlyList<MouseEngineResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
            return false;
        foreach (var result in results)
        {
            if (!IsLocalMenuHoverResult(result))
                return false;
        }

        return true;
    }

    private static bool IsMenuHoverMotion(AttachLiveState live, MouseEvent ev)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(ev);
        if (!ConsumesContextMenuKeys(live) || ev.IsWheel)
            return false;
        return ev.Action is MouseAction.Move or MouseAction.Drag;
    }

    private static bool AreMenuHoverMotions(IReadOnlyList<MouseEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
            return false;
        foreach (var ev in events)
        {
            if (ev.IsWheel || ev.Action is not (MouseAction.Move or MouseAction.Drag))
                return false;
        }

        return true;
    }

    internal static int DrainStdinAvailable(byte[] buf, int filled)
    {
        ArgumentNullException.ThrowIfNull(buf);
        if (filled < 0)
            filled = 0;
        if (filled > buf.Length)
            filled = buf.Length;
        while (filled < buf.Length && UnixRawTerminal.PollReadable(0, timeoutMs: 0))
        {
            var n = UnixRawTerminal.TryRead(0, buf, filled);
            if (n <= 0)
                break;
            filled += n;
        }

        return filled;
    }

    internal static void PaintMenuHover(UnixRawTerminal tty, AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        WriteHostComposed(tty, live, overlayOnly: live.Host.Cols > 0 && live.Host.Rows > 0);
    }

    internal static IReadOnlyList<MouseEngineResult> CollapseConsecutiveScrollHistory(
        IReadOnlyList<MouseEngineResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count <= 1)
            return results;

        List<MouseEngineResult>? collapsed = null;
        var i = 0;
        while (i < results.Count)
        {
            var current = results[i];
            if (current.Kind is MouseCommandKind.ScrollHistory
                && current.PaneId is { Length: > 0 } pane
                && current.ScrollDelta is int delta)
            {
                var j = i + 1;
                var sum = delta;
                while (j < results.Count
                    && results[j].Kind is MouseCommandKind.ScrollHistory
                    && string.Equals(results[j].PaneId, pane, StringComparison.Ordinal)
                    && results[j].ScrollDelta is int next)
                {
                    sum += next;
                    j++;
                }

                if (j - i > 1)
                {
                    collapsed ??= CopyPrefix(results, i);
                    collapsed.Add(current with { ScrollDelta = sum });
                    i = j;
                    continue;
                }
            }

            collapsed?.Add(current);
            i++;
        }

        return collapsed ?? results;
    }

    private static List<MouseEngineResult> CopyPrefix(
        IReadOnlyList<MouseEngineResult> results,
        int count)
    {
        var copy = new List<MouseEngineResult>(results.Count);
        for (var i = 0; i < count; i++)
            copy.Add(results[i]);
        return copy;
    }

    internal static bool ConsumesContextMenuKeys(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return live.Engine.Mode is AttachClientMode.ContextMenu or AttachClientMode.GlobalMenu
            || live.Mouse.Menu is not null
            || live.MouseMenu is not null;
    }

    internal static async Task<bool> HandleContextMenuKeysAsync(
        UnixRawTerminal? tty,
        AttachLiveState live,
        List<byte> decoded,
        IAttachCommandPort control,
        SemaphoreSlim controlGate,
        CancellationTokenSource? linked,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(controlGate);
        await controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var decodedKey in KeyEventDecoder.Decode(CollectionsMarshal.AsSpan(decoded)))
            {
                foreach (var result in live.Mouse.FeedKey(decodedKey.Chord))
                {
                    if (await ApplyMouseResultAsync(result, live, control, tty, linked, ct)
                        .ConfigureAwait(false))
                    {
                        return true;
                    }
                }
            }

            return live.DetachRequested;
        }
        catch (IOException)
        {
            linked?.Cancel();
            throw;
        }
        catch (ObjectDisposedException)
        {
            linked?.Cancel();
            throw;
        }
        catch (ControlPlaneException ex)
        {
            if (!ClosePopupIfNotOpen(live, ex))
                live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
            if (tty is not null)
            {
                if (!live.PopupOpen)
                    PaintAfterPopupClosed(tty, live);
                else
                    PaintChrome(tty, live);
            }
            return live.DetachRequested;
        }
        catch (InvalidOperationException ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
            if (tty is not null)
                PaintChrome(tty, live);
            return live.DetachRequested;
        }
        catch (ControlPlaneClientTimeoutException)
        {
            linked?.Cancel();
            throw;
        }
        finally
        {
            controlGate.Release();
        }
    }

    internal static void ClampSidebarScrolls(AttachLiveState live, int? hostRows = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        var frame = live.SidebarFrame;
        if (frame is null || frame.Display is SidebarCollapseDisplay.Hidden)
        {
            live.SidebarSpacesScroll = 0;
            live.SidebarAgentsScroll = 0;
            return;
        }

        var rows = hostRows ?? live.Chrome?.Rows ?? live.ChromeSeed?.Rows ?? 24;
        var width = frame.Width > 0 ? frame.Width : Math.Max(1, live.SidebarWidth);
        var sidebar = new CellRect(0, 0, width, rows);
        var compact = frame.Display is SidebarCollapseDisplay.Compact;
        var resourceSectionIds = LayoutChromeGeometry.VisibleResourceSectionIds(frame);
        var resourceWanted = SidebarTwoPaneLayoutPolicy.WantedResourceRows(frame.Panes, compact);
        var layout = compact
            ? SidebarTwoPaneLayoutPolicy.Compact(sidebar, resourceSectionIds, resourceWanted)
            : SidebarTwoPaneLayoutPolicy.Expanded(
                sidebar,
                live.SidebarSectionSplit,
                resourceSectionIds,
                resourceWanted);
        var spaces = frame.Pane(SidebarPaneSlot.Spaces);
        var agents = frame.Pane(SidebarPaneSlot.Agents);
        live.SidebarSpacesScroll = SidebarTwoPaneLayoutPolicy.ClampCardScroll(
            live.SidebarSpacesScroll,
            spaces?.Rows ?? [],
            layout.VisibleBodyRows(SidebarPaneSlot.Spaces, compact));
        live.SidebarAgentsScroll = SidebarTwoPaneLayoutPolicy.ClampCardScroll(
            live.SidebarAgentsScroll,
            agents?.Rows ?? [],
            layout.VisibleBodyRows(SidebarPaneSlot.Agents, compact));
        if (frame is not null)
        {
            foreach (var pane in frame.Panes)
            {
                if (pane.Slot is not (SidebarPaneSlot.Resource or SidebarPaneSlot.Cubes) || !pane.Visible)
                    continue;
                var scroll = live.SidebarResourceScrolls.GetValueOrDefault(pane.Id);
                live.SidebarResourceScrolls[pane.Id] = SidebarTwoPaneLayoutPolicy.ClampCardScroll(
                    scroll,
                    pane.Rows,
                    layout.BodyRectForSection(pane.Slot, compact, pane.Id).Rows);
            }
        }
    }

    /// <summary>
    /// persisted split. Missing or malformed files keep the default.
    /// </summary>
    internal static void RestoreSidebarSectionSplit(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var store = live.ClientViewPreferences
            ?? new FileClientViewPreferencesStore(live.AttachEnvironment);
        live.ClientViewPreferences = store;
        var prefs = store.Load();
        if (prefs.CollapsedGroups is { Length: > 0 } groups)
            live.CollapsedWorktreeGroups = new HashSet<string>(groups, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(prefs.LastPlacementId))
            live.SelectedCubeId = prefs.LastPlacementId.Trim();
        if (prefs.SidebarSectionSplit is { } saved)
        {
            live.SidebarSectionSplit = SidebarTwoPaneLayoutPolicy.ClampSplitRatio(saved);
            live.SidebarSectionSplitSource = SidebarSectionSplitSource.Manual;
            return;
        }

        live.SidebarSectionSplit = SidebarTwoPaneLayoutPolicy.DefaultSplitRatio;
        live.SidebarSectionSplitSource = SidebarSectionSplitSource.Config;
    }

    /// <summary>
    /// only after a manual drag.
    /// </summary>
    internal static void PersistSidebarSectionSplit(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.SidebarSectionSplitSource is not SidebarSectionSplitSource.Manual)
            return;
        PersistClientViewPreferences(live);
    }

    /// <summary>
    /// <c>collapsed_groups</c> together.
    /// </summary>
    internal static void PersistClientViewPreferences(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.ClientViewPreferences is not { } store)
            return;
        var current = store.Load();
        store.Save(new ClientViewPreferences
        {
            SidebarSectionSplit = live.SidebarSectionSplitSource is SidebarSectionSplitSource.Manual
                ? live.SidebarSectionSplit
                : current.SidebarSectionSplit,
            CollapsedGroups = live.CollapsedWorktreeGroups is { } groups
                ? groups.OrderBy(key => key, StringComparer.Ordinal).ToArray()
                : current.CollapsedGroups,
            LastPlacementId = live.SelectedCubeId ?? live.ConnectedPlacementId ?? current.LastPlacementId,
        });
    }

    internal static bool SidebarOpenAtAttach(AttachUiConfig ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        return !ui.SidebarStartCollapsed;
    }

    /// <summary>
    // / session snapshot.
    /// <c>apply_active_snapshot</c> replaces the client list. Apply that
    /// snapshot even when <c>layout.export</c> of a closed tab fails.
    /// </summary>
    internal static async Task ApplySessionSnapshotToSidebarAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        if (!live.ChromeEnabled)
            return;
        try
        {
            var session = await control.CallAsync(ProtocolMethods.SessionSnapshot, null, ct)
                .ConfigureAwait(false);
            HydratePaneRightClick(live, session);
            ApplyPopupFromSnapshot(live, session);
            RebuildSidebar(live, session);
            AlignLiveFocusFromSnapshot(live, session);
            // replaces the client list before the next paint. RebuildSidebar
            // updates SidebarFrame; live.Chrome still holds the previous
            // host geometry until this recompute.
            RecomputeLiveChromeFromSidebar(live);
        }
        catch (ControlPlaneException ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
        }
        catch (InvalidOperationException ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
        }
    }

    private static void RecomputeLiveChromeFromSidebar(AttachLiveState live)
    {
        if (!live.ChromeEnabled)
            return;
        if (live.ChromeSeed is not null)
            RecomputeChromeFromSeed(live, tty: null);
        else
            PaintSidebarAfterExportFailure(live, tty: null);
    }

    internal static void AlignLiveFocusFromSnapshot(AttachLiveState live, JsonElement snapshot)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (SnapshotHasWorkspace(snapshot, live.WorkspaceId)
            && SnapshotHasWorkspace(snapshot, live.Dispatcher.WorkspaceId))
        {
            return;
        }

        live.Dispatcher.ApplyFocusFromSnapshot(snapshot);
        live.WorkspaceId = live.Dispatcher.WorkspaceId;
        live.TabId = live.Dispatcher.TabId;
        live.ChromeSeed = null;
    }

    private static void AdoptPresentedFocus(AttachLiveState live, JsonElement snapshot)
    {
        live.Dispatcher.ApplyFocusFromSnapshot(snapshot);
        if (!string.IsNullOrWhiteSpace(live.Dispatcher.WorkspaceId))
            live.WorkspaceId = live.Dispatcher.WorkspaceId;
        if (!string.IsNullOrWhiteSpace(live.Dispatcher.TabId))
            live.TabId = live.Dispatcher.TabId;
        live.PaneId = live.Dispatcher.PaneId ?? "";
        live.ChromeSeed = null;
    }

    /// <summary>
    /// Present <paramref name="eventTabId"/> when that tab is the mux focus.
    /// The connection snapshot overlays the client's own tab, so this decision
    /// uses <c>mux_focused_tab_id</c> and the event payload.
    /// An event that does not name that tab leaves the client's tab in place.
    /// </summary>
    private static void PresentMuxTab(
        AttachLiveState live,
        JsonElement snapshot,
        string? eventTabId,
        string? eventPaneId)
    {
        if (string.IsNullOrWhiteSpace(eventTabId))
            return;

        var muxTab = TryJsonString(snapshot, "mux_focused_tab_id");
        if (muxTab is null)
        {
            if (string.Equals(
                    TryJsonString(snapshot, "focused_tab_id"),
                    eventTabId,
                    StringComparison.Ordinal))
            {
                AdoptPresentedFocus(live, snapshot);
            }

            return;
        }

        if (!string.Equals(muxTab, eventTabId, StringComparison.Ordinal))
            return;

        var workspace = TryJsonString(snapshot, "mux_focused_workspace_id");
        var pane = !string.IsNullOrWhiteSpace(eventPaneId)
            ? eventPaneId
            : TryJsonString(snapshot, "mux_focused_pane_id");
        live.Dispatcher.PresentTab(workspace, eventTabId, pane);
        if (!string.IsNullOrWhiteSpace(live.Dispatcher.WorkspaceId))
            live.WorkspaceId = live.Dispatcher.WorkspaceId;
        if (!string.IsNullOrWhiteSpace(live.Dispatcher.TabId))
            live.TabId = live.Dispatcher.TabId;
        if (!string.IsNullOrWhiteSpace(live.Dispatcher.PaneId))
            live.PaneId = live.Dispatcher.PaneId;
        live.ChromeSeed = null;
    }

    private static string? OverlayPaneTitle(AttachLiveState live, string paneId)
    {
        if (live.SidebarInput is not { } input)
            return null;
        foreach (var pane in input.Panes)
        {
            if (!string.Equals(pane.Id, paneId, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrWhiteSpace(pane.Label))
                return pane.Label;
        }

        return null;
    }

    /// <summary>
    /// True when this batch closed the client's tab or hid its pane.
    /// </summary>
    internal static bool ClientTabEmptiedByEvents(
        AttachLiveState live,
        IReadOnlyList<JsonElement> events)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(events);
        foreach (var ev in events)
        {
            if (!TryGetEventType(ev, out var type, out var payload))
                continue;
            if (string.Equals(type, ProtocolEventTypes.TabLifecycle, StringComparison.Ordinal)
                && string.Equals(TryJsonString(payload, "action"), "closed", StringComparison.Ordinal)
                && string.Equals(TryJsonString(payload, "tab_id"), live.TabId, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.Equals(type, ProtocolEventTypes.PanePlacementChanged, StringComparison.Ordinal)
                || !string.Equals(TryJsonString(payload, "to"), "hidden", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(TryJsonString(payload, "tab_id"), live.TabId, StringComparison.Ordinal)
                || string.Equals(TryJsonString(payload, "pane_id"), live.PaneId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Adopt mux focus when this client's tab was removed, or when a hide
    /// or close in this refresh emptied it. A tab the client chose with no
    /// pane stays selected. This method does not request detach.
    /// </summary>
    internal static void FollowMuxFocusWhenTabHasNoLeaf(
        AttachLiveState live,
        JsonElement snapshot,
        bool tabEmptiedByRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (snapshot.ValueKind != JsonValueKind.Object)
            return;
        var removed = !string.IsNullOrWhiteSpace(live.TabId)
            && !SnapshotHasTab(snapshot, live.TabId);
        var emptied = tabEmptiedByRefresh && !SnapshotTabHasLeaf(snapshot, live.TabId);
        if (!removed && !emptied)
            return;
        if (SnapshotTabHasLeaf(snapshot, live.TabId))
            return;

        var muxTab = TryJsonString(snapshot, "mux_focused_tab_id")
            ?? TryJsonString(snapshot, "focused_tab_id");
        var muxWorkspace = TryJsonString(snapshot, "mux_focused_workspace_id")
            ?? TryJsonString(snapshot, "focused_workspace_id");
        string? destTab = null;
        string? destPane = null;
        string? destWorkspace = muxWorkspace;
        if (!string.IsNullOrWhiteSpace(muxTab) && SnapshotTabHasLeaf(snapshot, muxTab))
        {
            destTab = muxTab;
            destPane = TryJsonString(snapshot, "mux_focused_pane_id")
                ?? FocusedPaneOfSnapshotTab(snapshot, muxTab);
            destWorkspace = WorkspaceOfSnapshotTab(snapshot, muxTab) ?? muxWorkspace;
        }
        else
        {
            destTab = FirstSnapshotTabWithLeaf(
                snapshot,
                live.WorkspaceId ?? muxWorkspace,
                out destPane,
                out destWorkspace);
        }

        if (string.IsNullOrWhiteSpace(destTab))
            return;
        if (string.Equals(destTab, live.TabId, StringComparison.Ordinal))
            return;

        live.Dispatcher.PresentTab(destWorkspace, destTab, destPane);
        if (!string.IsNullOrWhiteSpace(live.Dispatcher.WorkspaceId))
            live.WorkspaceId = live.Dispatcher.WorkspaceId;
        if (!string.IsNullOrWhiteSpace(live.Dispatcher.TabId))
            live.TabId = live.Dispatcher.TabId;
        if (!string.IsNullOrWhiteSpace(live.Dispatcher.PaneId))
            live.PaneId = live.Dispatcher.PaneId;
        live.ChromeSeed = null;
    }

    private static bool SnapshotTabHasLeaf(JsonElement snapshot, string? tabId)
    {
        if (!TrySnapshotTab(snapshot, tabId, out var tab))
            return false;
        if (tab.TryGetProperty("layout", out var layout))
        {
            if (layout.ValueKind == JsonValueKind.Null)
                return false;
            if (layout.ValueKind == JsonValueKind.Object)
                return true;
            return false;
        }

        return !string.IsNullOrWhiteSpace(TryJsonString(tab, "focused_pane_id"));
    }

    private static bool TrySnapshotTab(JsonElement snapshot, string? tabId, out JsonElement tab)
    {
        tab = default;
        if (string.IsNullOrWhiteSpace(tabId) || snapshot.ValueKind != JsonValueKind.Object)
            return false;
        if (!snapshot.TryGetProperty("tabs", out var tabs) || tabs.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in tabs.EnumerateArray())
        {
            if (!string.Equals(TryJsonString(item, "tab_id"), tabId, StringComparison.Ordinal))
                continue;
            tab = item;
            return true;
        }

        return false;
    }

    private static string? FocusedPaneOfSnapshotTab(JsonElement snapshot, string tabId) =>
        TrySnapshotTab(snapshot, tabId, out var tab)
            ? TryJsonString(tab, "focused_pane_id")
            : null;

    private static string? WorkspaceOfSnapshotTab(JsonElement snapshot, string tabId) =>
        TrySnapshotTab(snapshot, tabId, out var tab)
            ? TryJsonString(tab, "workspace_id")
            : null;

    private static string? FirstSnapshotTabWithLeaf(
        JsonElement snapshot,
        string? workspaceId,
        out string? paneId,
        out string? tabWorkspace)
    {
        paneId = null;
        tabWorkspace = null;
        if (snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("tabs", out var tabs)
            || tabs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in tabs.EnumerateArray())
        {
            var id = TryJsonString(item, "tab_id");
            if (string.IsNullOrWhiteSpace(id) || !SnapshotTabHasLeaf(snapshot, id))
                continue;
            var itemWorkspace = TryJsonString(item, "workspace_id");
            if (!string.IsNullOrWhiteSpace(workspaceId)
                && !string.Equals(itemWorkspace, workspaceId, StringComparison.Ordinal))
            {
                continue;
            }

            paneId = TryJsonString(item, "focused_pane_id");
            tabWorkspace = itemWorkspace;
            return id;
        }

        return null;
    }

    internal static bool SnapshotHasWorkspace(JsonElement snapshot, string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || snapshot.ValueKind != JsonValueKind.Object)
            return false;
        if (!snapshot.TryGetProperty("workspaces", out var workspaces)
            || workspaces.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in workspaces.EnumerateArray())
        {
            if (string.Equals(TryJsonString(item, "workspace_id"), workspaceId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool SnapshotAllowsLayoutExport(AttachLiveState live)
    {
        if (live.LastSnapshot is not { } snapshot)
            return true;
        var tabId = live.TabId;
        var paneId = live.PaneId;
        if (string.IsNullOrWhiteSpace(tabId) && string.IsNullOrWhiteSpace(paneId))
            return false;
        if (!string.IsNullOrWhiteSpace(tabId) && SnapshotHasTab(snapshot, tabId))
            return true;
        if (!string.IsNullOrWhiteSpace(paneId) && SnapshotHasPane(snapshot, paneId))
            return true;
        return false;
    }

    internal static bool SnapshotHasTab(JsonElement snapshot, string? tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId) || snapshot.ValueKind != JsonValueKind.Object)
            return false;
        if (!snapshot.TryGetProperty("tabs", out var tabs) || tabs.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in tabs.EnumerateArray())
        {
            if (string.Equals(TryJsonString(item, "tab_id"), tabId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    internal static bool SnapshotHasPane(JsonElement snapshot, string? paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId) || snapshot.ValueKind != JsonValueKind.Object)
            return false;
        if (!snapshot.TryGetProperty("panes", out var panes) || panes.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in panes.EnumerateArray())
        {
            if (string.Equals(TryJsonString(item, "pane_id"), paneId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    internal const string LocalCubeSnapshotKey = "local";

    /// <summary>
    /// <c>apply_active_snapshot</c> and
    /// <c>src/client/shell/endpoints.rs:173-178</c> compose spaces from
    /// the selected endpoint snapshot. Incoming mux events still cache
    /// under the attached cube.
    /// </summary>
    internal static void RebuildSidebar(
        AttachLiveState live,
        JsonElement snapshot,
        bool requestGit = true,
        bool hydratePicker = true,
        string? snapshotOwner = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.GitStatus ??= new ProcessSidebarGitStatus(time: live.Time);
        if (live.SidebarRequestedWidth <= 0)
        {
            live.SidebarRequestedWidth = live.SidebarWidth > 0
                ? live.SidebarWidth
                : live.Ui.SidebarWidth;
        }

        var requested = live.SidebarRequestedWidth > 0
            ? live.SidebarRequestedWidth
            : live.Ui.SidebarWidth;
        var owner = snapshotOwner ?? CubeSnapshotKey(live, live.ConnectedPlacementId);
        RememberCubeSnapshot(live, owner, snapshot);
        var attachedKey = CubeSnapshotKey(live, live.ConnectedPlacementId);
        var replaceLastSnapshot = snapshotOwner is null
            || string.Equals(owner, attachedKey, StringComparison.Ordinal);
        var composeSnap = SnapshotForConnectedCube(live) ?? snapshot;
        // The summary follows the session that the attach shows. A new mux, or another
        // cube, replaces it. The next paint sends the change to the host terminal.
        live.SessionStatus = SidebarLiveModel.ReadSessionStatus(composeSnap);
        var input = SidebarLiveModel.FromSnapshot(
            composeSnap,
            live.Ui,
            expanded: live.SidebarOpen,
            requestedWidth: requested,
            git: live.GitStatus,
            collapsedSectionIds: live.CollapsedSectionIds,
            collapsedWorktreeGroups: live.CollapsedWorktreeGroups,
            collapsedTreeIds: live.CollapsedTreeIds,
            cubes: live.Cubes,
            cubesState: live.CubesState,
            globalMenuAttentionBadgeVisible: live.GlobalMenuAttentionBadgeVisible,
            navigatedPaneId: live.NavigatedPaneId,
            linkedPluginIds: live.LinkedPluginIds,
            focusedCubeId: ChromeFocusedCubeId(live),
            connectedCubeId: ChromeConnectedCubeId(live));
        input = input with
        {
            FocusedCubeId = ChromeFocusedCubeId(live),
            ConnectedCubeId = ChromeConnectedCubeId(live),
        };
        live.WithPaint(() =>
        {
            if (replaceLastSnapshot)
            {
                live.LastSnapshot = snapshot;
                live.LastSnapshotConnectionGeneration = ConnectionGenerationOf(live, live.ControlSlot?.Client);
                // inside apply_active_snapshot (state.rs:1295).
                if (EndpointActivationProjection.TryParseSnapshotIdentity(snapshot, out var bootId, out _))
                    live.EndpointCommands.SnapshotBootId = bootId;
            }

            live.PluginResources = ParsePluginResources(composeSnap);
            live.SidebarInput = input;
            live.SidebarWidth = requested;
            RecomposeLiveSidebar(live);
            if (hydratePicker)
                HydratePickerCatalog(live);
        });
        if (requestGit
            && live.SidebarFrame?.Display is SidebarCollapseDisplay.Expanded
            && live.GitStatus is not null)
        {
            foreach (var workspace in input.Workspaces.Where(workspace => workspace.Git is null))
                live.GitStatus.RequestRefresh(workspace.Cwd);
        }
    }

    /// <summary>
    /// Compose <see cref="AttachLiveState.SidebarFrame"/> from
    // / <see cref="AttachLiveState.SidebarInput"/>.
    /// <c>src/ui.rs:51-63</c> compute_view builds one host view.
    /// <c>:130</c> commit stay one pass. Live attach keeps the
    /// pane-aware frame; it does not store a flattened stub list.
    /// </summary>
    internal static void RecomposeLiveSidebar(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var input = live.SidebarInput;
        if (input is null)
        {
            live.SidebarFrame = null;
            live.SidebarRows = [];
            live.SidebarSpacesScroll = 0;
            live.SidebarAgentsScroll = 0;
            return;
        }

        input = input with
        {
            ContinuityEnabled = live.Release.ContinuityEnabled,
            FocusedCubeId = ChromeFocusedCubeId(live),
            ConnectedCubeId = ChromeConnectedCubeId(live),
            UpdateNotice = live.UpdateNotice,
        };
        live.SidebarInput = input;
        var registry = SidebarPluginSectionRegistry.Build(
            input.Ui,
            live.Release.ContinuityEnabled,
            live.LinkedPluginIds);
        live.SidebarFrame = SidebarSectionComposer.Compose(input, registry);
        live.SidebarRows = SidebarLiveModel.ToStubRows(live.SidebarFrame);
        ClampSidebarScrolls(live);
    }

    internal static void HydratePickerCatalog(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Engine.Mode is AttachClientMode.TransferPicker
            || live.Engine.PaintMode is AttachClientMode.TransferPicker)
        {
            HydrateTransferCatalog(live);
            return;
        }

        var input = live.SidebarInput;
        if (input is null && live.LastSnapshot is { } snap)
        {
            input = SidebarLiveModel.FromSnapshot(
                snap,
                live.Ui,
                expanded: live.SidebarOpen,
                requestedWidth: live.SidebarRequestedWidth > 0
                    ? live.SidebarRequestedWidth
                    : live.Ui.SidebarWidth,
                git: live.GitStatus,
                collapsedSectionIds: live.CollapsedSectionIds,
                collapsedWorktreeGroups: live.CollapsedWorktreeGroups,
                collapsedTreeIds: live.CollapsedTreeIds,
                cubes: live.Cubes,
                cubesState: live.CubesState,
                globalMenuAttentionBadgeVisible: live.GlobalMenuAttentionBadgeVisible,
                navigatedPaneId: live.NavigatedPaneId,
                focusedCubeId: ChromeFocusedCubeId(live),
                connectedCubeId: ChromeConnectedCubeId(live));
            live.SidebarInput = input;
        }

        if (input is null)
            return;
        if (live.Engine.Mode is AttachClientMode.WorkspacePicker
            || live.Engine.PaintMode is AttachClientMode.WorkspacePicker)
        {
            live.Engine.Navigator.WorkspaceOnly = true;
            live.Engine.Navigator.TransferMode = false;
            var catalog = SidebarLiveModel.WorkspaceCatalog(input);
            if (!live.Engine.Navigator.CatalogKeysEqual(catalog))
                live.Engine.Navigator.SetCatalog(catalog);
            return;
        }

        if (live.Engine.Mode is AttachClientMode.Navigator
            || live.Engine.PaintMode is AttachClientMode.Navigator)
        {
            live.Engine.Navigator.WorkspaceOnly = false;
            live.Engine.Navigator.TransferMode = false;
            var catalog = SidebarLiveModel.Catalog(input, live.LinkedPlugins);
            if (!live.Engine.Navigator.CatalogKeysEqual(catalog))
                live.Engine.Navigator.SetCatalog(catalog);
        }
    }

    internal static void HydrateTransferCatalog(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.Release.ContinuityEnabled)
            return;
        live.Engine.Navigator.WorkspaceOnly = false;
        live.Engine.Navigator.TransferMode = true;
        BindConnectedPlacementIdentity(live);
        if (live.CubesState is SidebarCubeCatalogState.Unavailable)
        {
            live.Engine.Navigator.CatalogReason = SidebarTokenGrammar.CubesUnavailableText;
            live.Engine.Navigator.SetCatalog([]);
            return;
        }

        live.Engine.Navigator.CatalogReason = "";
        var catalog = TransferDestinations(live);
        if (!live.Engine.Navigator.CatalogKeysEqual(catalog))
            live.Engine.Navigator.SetCatalog(catalog);
    }

    internal static IReadOnlyList<GotoTarget> TransferDestinations(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var rows = new List<GotoTarget>();
        foreach (var cube in live.Cubes)
        {
            if (string.IsNullOrWhiteSpace(cube.Id))
                continue;
            if (!string.IsNullOrWhiteSpace(live.ConnectedPlacementId)
                && string.Equals(cube.Id, live.ConnectedPlacementId, StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(new GotoTarget(
                GotoTargetKind.Cube,
                cube.Id,
                string.IsNullOrWhiteSpace(cube.Name) ? cube.Id : cube.Name,
                KindText: SidebarTokenGrammar.CubeKindText(cube.Kind),
                ReachabilityText: SidebarTokenGrammar.CubeReachabilityText(cube.Reachability)));
        }

        return rows;
    }

    internal static bool TryFlushSidebarGitPaint(
        AttachLiveState live,
        UnixRawTerminal? tty,
        SemaphoreSlim? controlGate = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.ChromeEnabled)
            return false;
        if (live.GitStatus?.TryConsumeRefresh() == true)
            live.MarkSidebarGitPaintPending();
        if (!live.HasSidebarGitPaintPending)
            return false;
        if (controlGate is not null && !controlGate.Wait(0))
            return false;

        try
        {
            if (!live.TakeSidebarGitPaintPending())
                return false;
            if (live.LastSnapshot is not { } snap)
                return false;
            live.WithPaint(() =>
            {
                RebuildSidebar(live, snap, requestGit: false, hydratePicker: false);
                RecomputeChromeFromSeed(live, tty);
            });
            if (tty is not null && live.ChromeEnabled)
                PaintChrome(tty, live);
            return true;
        }
        finally
        {
            controlGate?.Release();
        }
    }

    internal static async Task TryFlushDeferredSidebarRefreshAsync(
        AttachLiveState live,
        IAttachCommandPort? control,
        UnixRawTerminal? tty,
        SemaphoreSlim? controlGate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.SidebarRefreshAfter is not { } due || live.Time.GetUtcNow() < due)
            return;
        if (control is null)
            return;
        live.SidebarRefreshAfter = null;
        await TryRefreshSidebarLiveAsync(
                control,
                live,
                tty,
                controlGate,
                ct,
                ignoreThrottle: true)
            .ConfigureAwait(false);
    }

    internal static async Task TryRefreshSidebarLiveAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        SemaphoreSlim? controlGate,
        CancellationToken ct,
        bool ignoreThrottle = false,
        bool armFollowUp = false)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        if (!live.ChromeEnabled)
            return;
        var now = live.Time.GetUtcNow();
        if (armFollowUp)
        {
            var followUp = now + SidebarLiveFollowUp;
            if (live.SidebarRefreshAfter is not { } existing || followUp > existing)
                live.SidebarRefreshAfter = followUp;
        }

        if (!ignoreThrottle
            && live.LastSidebarLiveRefresh is { } last
            && now - last < TimeSpan.FromMilliseconds(200))
        {
            return;
        }

        if (controlGate is not null)
        {
            if (!await controlGate.WaitAsync(0, ct).ConfigureAwait(false))
                return;
        }

        try
        {
            live.LastSidebarLiveRefresh = now;
            var revision = live.SidebarAgentRevision;
            var session = await control.CallAsync(ProtocolMethods.SessionSnapshot, null, ct)
                .ConfigureAwait(false);
            if (revision != live.SidebarAgentRevision)
                return;
            RebuildSidebar(live, session, requestGit: false);
            if (tty is not null)
            {
                if (live.ChromeSeed is not null)
                {
                    await RefreshChromeForPaintModeAsync(control, live, tty, ct)
                        .ConfigureAwait(false);
                }

                PaintChrome(tty, live);
            }
        }
        catch (ControlPlaneException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            controlGate?.Release();
        }
    }

    internal static async Task ReloadAttachConfigAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        try
        {
            var result = await control.CallAsync(
                    ProtocolMethods.ServerReloadConfig,
                    new JsonObject(),
                    ct)
                .ConfigureAwait(false);
            ApplyReloadRpcResult(live, result, tty);
        }
        catch (ControlPlaneException ex)
        {
            live.Status.Set(StatusOwner.Settings, StatusSeverity.Error, FormatStatus(ex));
        }
        catch (InvalidOperationException ex)
        {
            live.Status.Set(StatusOwner.Settings, StatusSeverity.Error, FormatStatus(ex));
        }
    }

    internal static void ApplyReloadRpcResult(
        AttachLiveState live,
        JsonElement result,
        UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        var status = result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("status", out var statusEl)
            ? statusEl.GetString()
            : null;
        if (string.Equals(status, ConfigReloadStatuses.Applied, StringComparison.Ordinal))
        {
            if (ApplyLiveAttachConfigFromDisk(live, tty))
                live.SuppressNextConfigReloadedLoad = true;
            return;
        }

        live.Status.Set(StatusOwner.Settings, StatusSeverity.Error, FormatReloadDiagnostics(result));
    }

    internal static bool ApplyLiveAttachConfigFromDisk(AttachLiveState live, UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        var loader = live.ConfigLoader ?? new FileAttachConfigLoader();
        var loaded = loader.Load();
        if (!loaded.IsOk)
        {
            live.Status.Set(StatusOwner.Settings, StatusSeverity.Error, loaded.Error.ToString());
            return false;
        }

        if (!ApplyLiveAttachConfig(live, loaded.Value, tty))
            return false;
        live.Status.Clear(StatusOwner.Settings);
        return true;
    }

    /// <summary>
    /// Apply reloadable attach keys. Startup-only keys stay. Idempotent.
    /// Existing pane command, cwd, and pid stay on the server.
    /// </summary>
    internal static bool ApplyLiveAttachConfig(
        AttachLiveState live,
        AttachClientConfig config,
        UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(config);

        var compiled = KeysConfigMapper.Compile(config.Keys);
        if (!compiled.IsOk)
        {
            live.Status.Set(StatusOwner.Settings, StatusSeverity.Error, compiled.Error.ToString());
            return false;
        }

        var theme = CreateThemeRuntime(config);
        if (!theme.IsOk)
        {
            live.Status.Set(StatusOwner.Theme, StatusSeverity.Error, theme.Error.ToString());
            return false;
        }

        var appearance = live.Theme.Appearance;
        var appearanceExplicit = live.Theme.AppearanceExplicit;
        live.Engine.ReplaceBindings(compiled.Value, AttachChromePolicy.FromUi(config.Ui));
        live.Table = compiled.Value;
        live.Dispatcher.Commands = live.RemoteAttach ? [] : compiled.Value.Commands;
        live.Dispatcher.AgentPanelSort = config.Ui.AgentPanelSort;
        live.Ui = config.Ui;
        live.AttachConfig = config;
        live.CjkIme = CjkImeRevealFilter.From(config.Experimental);
        live.PrefixAscii.Enabled = config.Experimental.SwitchAsciiInputSourceInPrefix;
        live.Theme = theme.Value;
        if (appearance is { } hostAppearance)
            live.Theme.SetAppearance(hostAppearance, appearanceExplicit);
        live.Engine.Settings.Bind(live.Theme, live.Ui);
        live.Notifications.Rebind(live.Ui);
        live.Mouse.Configure(MouseEngineOptions.FromUi(live.Ui, live.Ui.MouseCapture));
        tty?.EnableMouseCapture(live.Ui.MouseCapture);
        // A change of ui.host_status applies now. Off removes the report that is outstanding.
        if (tty is not null)
            ApplyHostStatus(tty, live);

        // sidebar_section_split. Config reload does not reset it.
        var nextWidth = live.SidebarWidthSource is SidebarWidthSource.Manual
            ? (live.SidebarRequestedWidth > 0 ? live.SidebarRequestedWidth : live.SidebarWidth)
            : live.Ui.SidebarWidth;
        live.SidebarRequestedWidth = SidebarHitModel.ClampWidth(
            nextWidth,
            live.Ui.SidebarMinWidth,
            live.Ui.SidebarMaxWidth);
        live.SidebarWidth = live.SidebarRequestedWidth;
        if (live.LastSnapshot is { } snap)
            RebuildSidebar(live, snap);
        if (live.ChromeSeed is not null)
        {
            RecomputeChromeFromSeed(live, tty);
            if (tty is not null && live.ChromeEnabled)
                PaintChrome(tty, live);
        }

        ReconcilePrefixAsciiInput(live);
        return true;
    }

    private static string FormatReloadDiagnostics(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("diagnostics", out var diagnostics)
            && diagnostics.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in diagnostics.EnumerateArray())
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text);
            }

            if (parts.Count > 0)
                return string.Join("; ", parts);
        }

        return "config reload failed";
    }

    internal static void ApplyOnboardingAtStart(AttachLiveState live, AttachClientConfig config)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(config);
        if (!config.ShouldShowOnboarding)
            return;
        foreach (var ev in live.Engine.EnterOnboarding())
            ApplyPendingModeEvent(live, ev);
        live.Engine.CommitPaintMode();
    }

    internal static void ApplyPackNotesAtStart(AttachLiveState live, AttachClientConfig config)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(config);
        if (config.ShouldShowOnboarding || !live.PackNotes.ShouldShowOnStartup())
            return;
        TryEnterReleaseNotes(live);
    }

    internal static bool TryEnterReleaseNotes(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var notes = live.PackNotes.TryLoad();
        if (notes is null)
            return false;
        foreach (var ev in live.Engine.EnterWhatsNew(notes))
            ApplyPendingModeEvent(live, ev);
        live.Engine.CommitPaintMode();
        return live.Engine.Mode is AttachClientMode.WhatsNew;
    }

    internal static bool TryDismissReleaseNotes(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return live.PackNotes.MarkSeen();
    }

    internal static bool TryOpenReleaseNotesFromSettings(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.Engine.Settings.PendingOpenReleaseNotes)
            return false;
        live.Engine.Settings.ClearPendingOpenReleaseNotes();
        foreach (var leave in live.WithPaint(() => live.Engine.LeaveSettings(restore: false)))
            ApplyPendingModeEvent(live, leave);
        return TryEnterReleaseNotes(live);
    }

    internal static Task<bool> TryCompleteOnboardingAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        _ = ct;
        return Task.FromResult(TryCompleteOnboarding(live, control));
    }

    private static bool TryCompleteOnboarding(AttachLiveState live, IAttachCommandPort control)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (live.Engine.Mode is not AttachClientMode.Onboarding)
            return false;
        if (!live.Engine.Onboarding.PendingComplete)
            return false;

        var loader = live.ConfigLoader ?? new FileAttachConfigLoader();
        var patched = loader.Patch([new AttachConfigAssignment("onboarding", "false")]);
        if (!patched.IsOk)
        {
            live.Status.Set(StatusOwner.Settings, StatusSeverity.Error, patched.Error.ToString());
            live.Engine.Onboarding.ClearPendingComplete();
            return false;
        }

        live.Engine.Onboarding.ClearPendingComplete();
        live.Engine.Settings.Bind(live.Theme, live.Ui);
        live.Status.Clear(StatusOwner.Settings);
        foreach (var ev in live.WithPaint(() => live.Engine.EnterSettings(SettingsPageRegistry.IntegrationsId)))
            ApplyPendingModeEvent(live, ev);
        StartSettingsIntegrationsLoad(live, control);
        return true;
    }

    internal static void TryStartStartupSplash(AttachLiveState live, bool loadLogoOverlay = true)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.Ui.StartupSplash)
            return;
        live.Splash = StartupSplashSurface.Start(loadLogoOverlay);
    }

    /// <summary>
    /// The queries that the attach writes to the host once, at start. The
    /// kitty keyboard probe goes first: DA1 ends it, so a host that does not
    /// know <c>CSI ? u</c> needs no timeout, and the push waits for the
    /// <c>CSI ? flags u</c> reply. Ghostty can drop replies of a long query
    /// burst, so the probe goes before the 258 colour queries, not after
    /// them. Then the theme queries (their OSC 4/10/11 replies are held so
    /// they do not reach a pane as keys) and the status query.
    /// </summary>
    internal static void WriteHostStartQueries(UnixRawTerminal tty, AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        if (live.Ui.KittyKeyboard)
        {
            tty.WriteBytes(HostKeyboardMode.ProbeSequence);
            live.HostInput.NoteKeyboardProbeSent();
        }

        tty.WriteBytes(HostThemeParser.AttachStartSequence);
        live.HostInput.NoteHostColorQuerySent();
        if (live.HostStatus.TakeQuery(live.Ui.HostStatus) is { } statusQuery)
            tty.WriteBytes(statusQuery);
    }

    /// <summary>
    /// The host answered the kitty keyboard query. Push flag 1 on the host
    /// and convert kitty key reports to legacy bytes for panes. The config
    /// key <c>ui.kitty_keyboard = false</c> keeps the host in legacy mode.
    /// </summary>
    internal static bool ActivateHostKittyKeyboard(UnixRawTerminal tty, AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        if (!live.Ui.KittyKeyboard || tty.KittyKeyboardPushed)
            return false;
        // With no armed host mode guard there is no push: a crash must not
        // leave the host in kitty mode.
        if (!tty.PushKittyKeyboard())
            return false;

        live.Engine.HostKittyKeyboard = true;
        return true;
    }

    internal static bool TryConsumeStartupSplashInput(
        AttachLiveState live,
        IReadOnlyList<byte> decoded,
        IReadOnlyList<MouseEvent> mouseEvents)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Splash is not { IsPlaying: true })
            return false;
        var click = false;
        if (mouseEvents is { Count: > 0 })
        {
            for (var i = 0; i < mouseEvents.Count; i++)
            {
                if (mouseEvents[i].Action is MouseAction.Press)
                {
                    click = true;
                    break;
                }
            }
        }

        if ((decoded is null || decoded.Count == 0) && !click)
            return false;
        live.Splash.Skip();
        live.Splash = null;
        return true;
    }

    internal static void TickStartupSplash(UnixRawTerminal tty, AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        if (live.Splash is null)
            return;
        if (!live.Splash.IsPlaying)
        {
            live.Splash = null;
            PaintChrome(tty, live, requestRepaint: true);
            return;
        }

        if (!tty.TryGetSize(out var cols, out var rows) || cols < 1 || rows < 1)
        {
            cols = live.Host.Cols;
            rows = live.Host.Rows;
            if (cols < 1 || rows < 1)
            {
                cols = 80;
                rows = 24;
            }
        }

        live.Splash.Tick(cols, rows);
        if (!live.Splash.IsPlaying)
        {
            live.Splash = null;
            PaintChrome(tty, live, requestRepaint: true);
            return;
        }

        PaintChrome(tty, live);
    }

    private static void StampStartupSplash(
        AttachLiveState live,
        IHostCellSink sink,
        int cols,
        int rows)
    {
        if (live.Splash is not { IsPlaying: true })
            return;
        live.Splash.Stamp(sink, cols, rows);
        live.Host.Cursor = HostCursor.None;
    }

    internal static async Task ApplyOnboardingPointerAsync(
        AttachLiveState live,
        int col,
        int row,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (live.Engine.Mode is not AttachClientMode.Onboarding)
            return;

        if (live.Engine.Onboarding.Layout is null)
            live.Engine.Onboarding.Layout = OnboardingPainter.Measure(live.Engine.Onboarding, 80, 24);

        var hit = OnboardingHitTest.Hit(live.Engine.Onboarding.Layout, col, row);
        if (hit.Kind is not OnboardingHitKind.Continue)
            return;

        live.Engine.Onboarding.RequestComplete();
        await ApplyModeEventsAsync(
                [new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: OnboardingOverlayModel.FilterComplete)],
                live,
                control,
                tty,
                linked: null,
                ct)
            .ConfigureAwait(false);
        if (tty is not null)
            PaintChrome(tty, live);
    }

    internal static async Task<bool> ApplySettingsPatchAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        var pending = live.Engine.Settings.PendingPatch;
        if (pending is null || pending.Count == 0)
            return false;

        var first = pending[0];
        var projected = SettingsValuePolicy.Project(first.Path, first.TomlLiteral);
        AttachProcessLog.SettingsRequested(
            live.ProcessLog,
            first.Path,
            live.SessionName,
            live.AttachClientId,
            projected.Value,
            projected.ValueKind,
            projected.Digest);

        var loader = live.ConfigLoader ?? new FileAttachConfigLoader();
        var patched = loader.Patch(pending);
        if (!patched.IsOk)
        {
            live.Status.Set(StatusOwner.Settings, StatusSeverity.Error, patched.Error.ToString());
            live.Engine.Settings.ClearPendingPatch();
            AttachProcessLog.SettingsOutcome(
                live.ProcessLog,
                first.Path,
                ProcessLogEvents.OutcomeError,
                live.SessionName,
                live.AttachClientId,
                projected.Value,
                projected.ValueKind,
                projected.Digest);
            return false;
        }

        var themePatched = false;
        foreach (var assignment in pending)
        {
            if (assignment.Path is "theme.name")
            {
                themePatched = true;
                break;
            }
        }

        ApplyLiveUiFromConfig(live, patched.Value, pending);
        live.Engine.Settings.Bind(live.Theme, live.Ui);
        if (themePatched)
            live.Engine.Settings.AcceptPreview();
        live.Notifications.Rebind(live.Ui);
        live.Engine.Settings.ClearPendingPatch();
        live.Status.Clear(StatusOwner.Settings);

        // push server.reload_config.
        try
        {
            var result = await control.CallAsync(
                    ProtocolMethods.ServerReloadConfig,
                    new JsonObject(),
                    ct)
                .ConfigureAwait(false);
            var status = result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("status", out var statusEl)
                ? statusEl.GetString()
                : null;
            if (string.Equals(status, ConfigReloadStatuses.Applied, StringComparison.Ordinal))
            {
                live.SuppressNextConfigReloadedLoad = true;
                AttachProcessLog.SettingsOutcome(
                    live.ProcessLog,
                    first.Path,
                    ProcessLogEvents.OutcomeApplied,
                    live.SessionName,
                    live.AttachClientId,
                    projected.Value,
                    projected.ValueKind,
                    projected.Digest);
                return true;
            }

            live.Status.Set(StatusOwner.Settings, StatusSeverity.Error, status ?? "reload failed");
            AttachProcessLog.SettingsOutcome(
                live.ProcessLog,
                first.Path,
                ProcessLogEvents.OutcomeError,
                live.SessionName,
                live.AttachClientId,
                projected.Value,
                projected.ValueKind,
                projected.Digest);
            return false;
        }
        catch (Exception ex) when (ex is ControlPlaneException or InvalidOperationException)
        {
            live.Status.Set(StatusOwner.Settings, StatusSeverity.Error, FormatStatus(ex.Message));
            AttachProcessLog.SettingsOutcome(
                live.ProcessLog,
                first.Path,
                ProcessLogEvents.OutcomeError,
                live.SessionName,
                live.AttachClientId,
                projected.Value,
                projected.ValueKind,
                projected.Digest);
            return false;
        }
    }

    internal static async Task HandleSettingsOverlayAsync(
        KeyEngineEvent ev,
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (ev.Kind is KeyEngineEventKind.EnterMode && ev.Mode is AttachClientMode.Settings)
        {
            StartSettingsIntegrationsLoad(live, control);
            await PluginSettingsAttachSupport.RefreshAsync(live, control, ct).ConfigureAwait(false);
            return;
        }

        if (ev.Kind is not KeyEngineEventKind.HelpFilter)
            return;
        if (ev.Filter is SettingsOverlayModel.FilterPage)
        {
            StartSettingsIntegrationsLoad(live, control);
            await PluginSettingsAttachSupport.RefreshAsync(live, control, ct).ConfigureAwait(false);
        }

        if (ev.Filter is SettingsOverlayModel.FilterPreview
            && live.Engine.Settings.PendingPatch is { Count: > 0 })
        {
            await ApplySettingsPatchAsync(live, control, ct).ConfigureAwait(false);
        }

        if (ev.Filter is SettingsOverlayModel.FilterApply)
        {
            await ApplySettingsIntegrationsAsync(live, control, ct).ConfigureAwait(false);
            await PluginSettingsAttachSupport.ApplyPendingAsync(live, control, ct).ConfigureAwait(false);
        }

        if (ev.Filter is SettingsOverlayModel.FilterUninstall)
            await ApplySettingsUninstallAsync(live, control, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows the loading line now and lists integrations in the background, the
    /// way herdr queues its list request. The tick loop repaints when it ends.
    /// </summary>
    internal static void StartSettingsIntegrationsLoad(AttachLiveState live, IAttachCommandPort control)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        var shows = live.WithPaint(() =>
        {
            if (!live.Engine.Settings.ShowsIntegrations)
                return false;
            live.Engine.Settings.BeginLoadingIntegrations();
            return true;
        });
        if (!shows)
            return;

        live.SettingsIntegrationsLoad = LoadInBackgroundAsync();

        async Task LoadInBackgroundAsync()
        {
            try
            {
                await RefreshSettingsIntegrationsAsync(live, control, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                live.MarkSettingsPaintPending();
            }
        }
    }

    /// <summary>Repaints settings after a background integrations load.</summary>
    internal static bool TryFlushSettingsPaint(
        AttachLiveState live,
        UnixRawTerminal? tty,
        SemaphoreSlim? controlGate = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.HasSettingsPaintPending)
            return false;
        if (controlGate is not null && !controlGate.Wait(0))
            return false;

        try
        {
            if (!live.TakeSettingsPaintPending())
                return false;
            if (tty is not null && live.ChromeEnabled && live.Engine.Mode is AttachClientMode.Settings)
                PaintChrome(tty, live);
            return true;
        }
        finally
        {
            controlGate?.Release();
        }
    }

    internal static async Task RefreshSettingsIntegrationsAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct,
        bool consumeRetry = false,
        bool onlyIfNeeded = false)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        var commitsBefore = live.WithPaint(() => live.Engine.Settings.IntegrationCommits);
        await live.IntegrationListGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A load that committed while this call waited already covers the page.
            if (!onlyIfNeeded
                && live.WithPaint(() => live.Engine.Settings.IntegrationCommits != commitsBefore
                    && IntegrationRefreshNeed(live) is null))
                return;

            if (onlyIfNeeded)
            {
                // Decide under the gate. A load that finished while this call
                // waited already covers the current endpoint.
                var need = live.WithPaint(() => IntegrationRefreshNeed(live));
                if (need is null)
                    return;
                consumeRetry = need.Value;
            }

            await RefreshSettingsIntegrationsCoreAsync(live, control, ct, consumeRetry)
                .ConfigureAwait(false);
        }
        finally
        {
            live.IntegrationListGate.Release();
        }
    }

    private static async Task RefreshSettingsIntegrationsCoreAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct,
        bool consumeRetry)
    {
        var settings = live.Engine.Settings;
        // Model writes share the paint lock with input and paint.
        var start = live.WithPaint(() => BeginIntegrationRefresh(live, consumeRetry));
        if (start is not { } load)
            return;

        IntegrationListResult? listed;
        try
        {
            var result = await control.CallAsync(ProtocolMethods.IntegrationList, null, ct)
                .ConfigureAwait(false);
            listed = result.Deserialize(ProtocolJsonContext.Default.IntegrationListResult);
        }
        catch (Exception ex)
        {
            live.WithPaint(() =>
            {
                if (!AcceptIntegrationLoad(live, load.Token, load.Endpoint, load.Generation))
                {
                    settings.DiscardIntegrationLoad(load.Token);
                    return;
                }

                live.Status.Set(StatusOwner.Settings, StatusSeverity.Error, FormatStatus(ex.Message));
                settings.NoteIntegrationLoadError(ex.Message);
                if (IsIntegrationRetryable(ex) && settings.ArmIntegrationRetry())
                    return;
                NoteCurrentIntegrationsEndpoint(live);
            });
            return;
        }

        live.WithPaint(() =>
        {
            if (!AcceptIntegrationLoad(live, load.Token, load.Endpoint, load.Generation))
            {
                settings.DiscardIntegrationLoad(load.Token);
                return;
            }

            settings.BindIntegrations(MapIntegrationStatuses(listed));
            NoteCurrentIntegrationsEndpoint(live);
        });
    }

    private readonly record struct IntegrationLoad(
        int Token,
        string Endpoint,
        (ulong Source, ulong Destination) Generation);

    /// <summary>
    /// A same-cube reconnect changes only the destination generation, so the
    /// integrations list keys on both transport generations.
    /// </summary>
    private static (ulong Source, ulong Destination) IntegrationsGeneration(
        AttachLiveState live,
        string endpointId) =>
        (AttachShellEndpointDispatch.GenerationFor(live, endpointId), live.TransportEnvelope.Generation);

    /// <summary>
    /// Caller holds the paint lock. Returns null when no list call should start.
    /// </summary>
    private static IntegrationLoad? BeginIntegrationRefresh(AttachLiveState live, bool consumeRetry)
    {
        NoteActiveMuxHost(live);
        var settings = live.Engine.Settings;
        if (!settings.ShowsIntegrations)
            return null;

        if (!consumeRetry)
            settings.ResetIntegrationRetryBudget();
        else if (settings.IntegrationRetryAttempts >= SettingsOverlayModel.IntegrationRetryLimit)
        {
            settings.ResetIntegrationRetryBudget();
            NoteCurrentIntegrationsEndpoint(live);
            return null;
        }

        if (!AttachShellEndpointDispatch.SurfaceReady(live))
        {
            settings.NoteIntegrationLoadError(AttachEndpointCommands.InterruptedMessage);
            if (!settings.ArmIntegrationRetry())
                NoteCurrentIntegrationsEndpoint(live);
            return null;
        }

        if (consumeRetry)
            settings.ConsumeIntegrationRetry();

        var endpoint = IntegrationEndpointId(live);
        var generation = IntegrationsGeneration(live, endpoint);
        return new IntegrationLoad(settings.BeginIntegrationLoad(), endpoint, generation);
    }

    /// <summary>
    /// Caller holds the paint lock. Null means the rows are current and no retry is armed.
    /// Otherwise the value says whether the refresh consumes an armed retry.
    /// </summary>
    private static bool? IntegrationRefreshNeed(AttachLiveState live)
    {
        var settings = live.Engine.Settings;
        if (!settings.ShowsIntegrations)
            return null;
        var endpointId = IntegrationEndpointId(live);
        var generation = IntegrationsGeneration(live, endpointId);
        var armed = settings.IntegrationRetryArmed;
        var stale = !settings.IntegrationsMatchEndpoint(endpointId, generation);
        return armed || stale ? armed : null;
    }

    internal static async Task ApplySettingsIntegrationsAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        await live.IntegrationListGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ApplySettingsIntegrationsCoreAsync(live, control, ct).ConfigureAwait(false);
        }
        finally
        {
            live.IntegrationListGate.Release();
        }
    }

    private static async Task ApplySettingsIntegrationsCoreAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        var pending = live.WithPaint(() => live.Engine.Settings.PendingInstallTargets);
        if (pending is null || pending.Count == 0)
            return;

        var messages = new List<string>();
        var attempts = new List<(OfficialIntegrationTarget Target, IntegrationRowResultKind Kind, string Detail)>();
        var skillInstalled = false;
        foreach (var target in pending)
        {
            try
            {
                var result = await control.CallAsync(
                        ProtocolMethods.IntegrationInstall,
                        new JsonObject { ["target"] = target.WireName() },
                        ct)
                    .ConfigureAwait(false);
                var dto = result.Deserialize(ProtocolJsonContext.Default.IntegrationActionResult);
                var lines = dto?.Messages ?? [];
                messages.AddRange(lines);
                skillInstalled |= SettingsOverlayModel.InstalledASkill(lines);
                attempts.Add((
                    target,
                    SettingsOverlayModel.ClassifyInstallResult(true, lines),
                    string.Join(" ", lines)));
            }
            catch (Exception ex)
            {
                var detail = IntegrationConsentText.WithNextStep(target, ex.Message);
                messages.Add(detail);
                attempts.Add((target, IntegrationRowResultKind.Failed, detail));
            }
        }

        var settings = live.Engine.Settings;
        var started = live.WithPaint(() =>
        {
            settings.ClearPendingPatch();
            // Settings can close while the install RPCs run. Do not list for a closed page.
            if (!settings.ShowsIntegrations)
                return (IntegrationLoad?)null;
            var endpoint = IntegrationEndpointId(live);
            var generation = IntegrationsGeneration(live, endpoint);
            return new IntegrationLoad(settings.BeginIntegrationLoad(), endpoint, generation);
        });
        if (started is not { } load)
            return;

        IntegrationListResult? listed;
        try
        {
            var result = await control.CallAsync(ProtocolMethods.IntegrationList, null, ct)
                .ConfigureAwait(false);
            listed = result.Deserialize(ProtocolJsonContext.Default.IntegrationListResult);
        }
        catch (Exception ex)
        {
            live.WithPaint(() =>
            {
                if (!AcceptIntegrationLoad(live, load.Token, load.Endpoint, load.Generation))
                {
                    settings.DiscardIntegrationLoad(load.Token);
                    return;
                }

                live.Status.Set(StatusOwner.Settings, StatusSeverity.Error, FormatStatus(ex.Message));
                settings.BindIntegrations(settings.Integrations, messages);
                settings.NoteIntegrationResults(attempts, skillInstalled);
                settings.NoteIntegrationLoadError(ex.Message);
                if (IsIntegrationRetryable(ex) && settings.ArmIntegrationRetry())
                    return;
                NoteCurrentIntegrationsEndpoint(live);
            });
            return;
        }

        live.WithPaint(() =>
        {
            if (!AcceptIntegrationLoad(live, load.Token, load.Endpoint, load.Generation))
            {
                settings.DiscardIntegrationLoad(load.Token);
                return;
            }

            settings.BindIntegrations(MapIntegrationStatuses(listed), messages);
            settings.NoteIntegrationResults(attempts, skillInstalled);
            NoteCurrentIntegrationsEndpoint(live);
        });
    }

    internal static async Task ApplySettingsUninstallAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        await live.IntegrationListGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var target = live.WithPaint(() =>
            {
                var pending = live.Engine.Settings.PendingUninstallTarget;
                live.Engine.Settings.ClearPendingPatch();
                return pending;
            });
            if (target is not { } remove)
                return;

            try
            {
                await control.CallAsync(
                        ProtocolMethods.IntegrationUninstall,
                        new JsonObject { ["target"] = remove.WireName() },
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                live.WithPaint(() => live.Status.Set(StatusOwner.Settings, StatusSeverity.Error, FormatStatus(ex.Message)));
                return;
            }

            await RefreshSettingsIntegrationsCoreAsync(live, control, ct, consumeRetry: false)
                .ConfigureAwait(false);
        }
        finally
        {
            live.IntegrationListGate.Release();
        }
    }

    private static string IntegrationEndpointId(AttachLiveState live)
    {
        var endpointId = live.ActiveProjectionEndpointId;
        if (string.IsNullOrEmpty(endpointId))
            endpointId = live.ConnectedPlacementId ?? "local";
        return endpointId;
    }

    private static void NoteCurrentIntegrationsEndpoint(AttachLiveState live)
    {
        NoteActiveMuxHost(live);
        var endpointId = IntegrationEndpointId(live);
        live.Engine.Settings.NoteIntegrationsEndpoint(
            endpointId,
            IntegrationsGeneration(live, endpointId));
    }

    private static void NoteActiveMuxHost(AttachLiveState live)
    {
        var endpointId = IntegrationEndpointId(live);
        var remote = AttachLossPolicy.IsRemoteDestination(live.RemoteDestination, live.PlacementKind)
            || !string.Equals(endpointId, "local", StringComparison.Ordinal);
        var name = remote
            ? AttachLossPolicy.ResolveDisplayName(live.PlacementDisplayName, live.PlacementKind)
            : "";
        live.Engine.Settings.NoteMuxHost(remote, name);
    }

    private static bool AcceptIntegrationLoad(
        AttachLiveState live,
        int token,
        string capturedEndpoint,
        (ulong Source, ulong Destination) capturedGeneration)
    {
        var endpointId = IntegrationEndpointId(live);
        return live.Engine.Settings.IntegrationLoadIsCurrent(token)
            && string.Equals(capturedEndpoint, endpointId, StringComparison.Ordinal)
            && capturedGeneration == IntegrationsGeneration(live, endpointId);
    }

    private static bool IsIntegrationRetryable(Exception ex) =>
        ex is ControlPlaneException cancelled
        && string.Equals(cancelled.ErrorCode, AttachEndpointCommands.CancelledCode, StringComparison.Ordinal);

    private static IReadOnlyList<OfficialIntegrationStatus> MapIntegrationStatuses(
        IntegrationListResult? dto)
    {
        if (dto?.Integrations is null || dto.Integrations.Count == 0)
            return [];
        var list = new List<OfficialIntegrationStatus>(dto.Integrations.Count);
        foreach (var item in dto.Integrations)
        {
            if (!OfficialIntegrationTargets.TryParse(item.Target, out var target))
                continue;
            var state = item.State switch
            {
                "current" => OfficialIntegrationStatusKind.Current,
                "outdated" => OfficialIntegrationStatusKind.Outdated,
                _ => OfficialIntegrationStatusKind.NotInstalled,
            };
            list.Add(new OfficialIntegrationStatus
            {
                Target = target,
                Path = item.Path ?? "",
                State = state,
                InstalledVersion = item.InstalledVersion,
                ExpectedVersion = item.ExpectedVersion,
                Available = item.Available,
                SkillState = OfficialIntegrationStatusWire.ParseSkillState(item.SkillState),
                Consent = item.Consent ?? [],
            });
        }

        return list;
    }

    private static async Task<bool> TryLeaveSettingsAfterPatchAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        var close = live.Engine.Settings.PendingCloseAfterPatch;
        if (!await ApplySettingsPatchAsync(live, control, ct).ConfigureAwait(false) || !close)
            return false;

        foreach (var leave in live.WithPaint(() => live.Engine.LeaveSettings(restore: false)))
            ApplyPendingModeEvent(live, leave);
        return true;
    }

    private static bool OverlayRestoreFrame(
        KeyEngineEvent ev,
        bool settingsLeave,
        AttachLiveState live) =>
        (ev.Kind is KeyEngineEventKind.LeaveMode || settingsLeave)
        && live.Engine.Mode is not AttachClientMode.Copy;

    private static bool OverlayLeaveNotesSnapshot(bool restoreFrame, AttachLiveState live) =>
        restoreFrame && KeyEngine.SuppressesLiveRemap(live.Engine.PaintMode);

    internal static void ApplySettingsWheel(AttachLiveState live, int delta, UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Engine.Mode is not AttachClientMode.Settings)
            return;
        if (!live.Engine.Settings.MoveList(delta))
            return;
        if (tty is not null)
            PaintChrome(tty, live);
    }

    internal static void ApplyReleaseNotesWheel(AttachLiveState live, int delta, UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Engine.Mode is not AttachClientMode.WhatsNew)
            return;
        var maxScroll = live.Engine.ReleaseNotes.Layout?.MaxScroll ?? 0;
        if (!live.Engine.ReleaseNotes.ScrollBy(delta, maxScroll))
            return;
        if (tty is not null)
            PaintChrome(tty, live);
    }

    internal static async Task ApplyReleaseNotesPointerAsync(
        AttachLiveState live,
        int col,
        int row,
        IAttachCommandPort port,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(port);
        if (live.Engine.Mode is not AttachClientMode.WhatsNew)
            return;
        if (live.Engine.ReleaseNotes.Layout is null)
            live.Engine.ReleaseNotes.Layout = ReleaseNotesPainter.Measure(
                live.Engine.ReleaseNotes,
                live.Host.Cols,
                live.Host.Rows);
        var layout = live.Engine.ReleaseNotes.Layout;
        var hit = ReleaseNotesHitTest.Hit(layout, col, row);
        if (hit.Kind is ReleaseNotesHitKind.ScrollTrack)
        {
            if (live.Engine.ReleaseNotes.SetScroll(hit.ScrollOffset, layout.MaxScroll) && tty is not null)
                PaintChrome(tty, live);
            return;
        }

        if (hit.Kind is not ReleaseNotesHitKind.Close)
            return;

        await ApplyModeEventsAsync(
                live.Engine.Feed(new KeyChord(false, false, false, false, "esc")),
                live,
                port,
                tty,
                linked: null,
                ct)
            .ConfigureAwait(false);
        if (tty is not null)
            PaintChrome(tty, live);
    }

    internal static async Task ApplySettingsPointerUnderGateAsync(
        AttachLiveState live,
        int col,
        int row,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        SemaphoreSlim controlGate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(controlGate);
        await controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ApplySettingsPointerAsync(live, col, row, control, tty, ct).ConfigureAwait(false);
        }
        finally
        {
            controlGate.Release();
        }
    }

    internal static async Task ApplySettingsPointerAsync(
        AttachLiveState live,
        int col,
        int row,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        var settings = live.Engine.Settings;
        // Model changes share the input/paint lock. RPCs run after it is released.
        var hit = live.WithPaint(() =>
        {
            if (live.Engine.Mode is not AttachClientMode.Settings)
                return null;
            settings.Layout ??= SettingsPainter.Measure(settings, 80, 24);
            return SettingsHitTest.Hit(settings.Layout, col, row);
        });
        if (hit is null)
            return;
        switch (hit.Kind)
        {
            case SettingsHitKind.Tab when hit.PageId is { } pageId:
                live.WithPaint(() => settings.SelectPage(pageId));
                StartSettingsIntegrationsLoad(live, control);
                break;
            case SettingsHitKind.Selection when hit.Index is { } boxIndex:
                live.WithPaint(() => settings.ToggleSelection(boxIndex));
                break;
            case SettingsHitKind.Item when hit.Index is { } index:
                var themePage = live.WithPaint(() =>
                {
                    settings.SelectItem(index);
                    if (settings.ActivePage.Kind is SettingsPageKind.Integrations)
                        return (bool?)null;
                    if (settings.ActivePage.Kind is SettingsPageKind.Theme)
                        return true;
                    _ = settings.Apply();
                    return false;
                });
                if (themePage is null)
                    break;
                if (themePage.Value)
                {
                    await ApplySettingsPatchAsync(live, control, ct).ConfigureAwait(false);
                    break;
                }

                await ApplySettingsIntegrationsAsync(live, control, ct).ConfigureAwait(false);
                if (await TryCommitSettingsPointerAsync(live, control, tty, ct).ConfigureAwait(false))
                    return;
                break;
            case SettingsHitKind.Apply:
                live.WithPaint(() => _ = settings.Apply());
                await ApplySettingsIntegrationsAsync(live, control, ct).ConfigureAwait(false);
                if (await TryCommitSettingsPointerAsync(live, control, tty, ct).ConfigureAwait(false))
                    return;
                break;
            case SettingsHitKind.Ignore:
                break;
            case SettingsHitKind.Close:
            case SettingsHitKind.Outside:
                await ApplyModeEventsAsync(
                        live.WithPaint(() => live.Engine.LeaveSettings(restore: true)),
                        live,
                        control,
                        tty,
                        linked: null,
                        ct)
                    .ConfigureAwait(false);
                return;
        }

        if (tty is not null)
            PaintChrome(tty, live);
    }

    private static async Task<bool> TryCommitSettingsPointerAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        if (live.Engine.Settings.PendingPluginConfigWrite is not null)
        {
            await PluginSettingsAttachSupport.ApplyPendingAsync(live, control, ct).ConfigureAwait(false);
            return false;
        }

        var close = live.Engine.Settings.PendingCloseAfterPatch;
        if (!await ApplySettingsPatchAsync(live, control, ct).ConfigureAwait(false))
            return false;
        if (!close)
            return false;

        await ApplyModeEventsAsync(
                live.WithPaint(() => live.Engine.LeaveSettings(restore: false)),
                live,
                control,
                tty,
                linked: null,
                ct)
            .ConfigureAwait(false);
        return true;
    }

    private static void ApplyLiveUiFromConfig(
        AttachLiveState live,
        AttachClientConfig config,
        IReadOnlyList<AttachConfigAssignment> assignments)
    {
        live.Ui = live.Ui with
        {
            StatusIndicators = config.Ui.StatusIndicators,
            ShowAgentLabelsOnPaneBorders = config.Ui.ShowAgentLabelsOnPaneBorders,
            StartupSplash = config.Ui.StartupSplash,
            Sound = live.Ui.Sound with { Enabled = config.Ui.Sound.Enabled },
            Toast = live.Ui.Toast with { Delivery = config.Ui.Toast.Delivery },
        };

        var themePatched = false;
        foreach (var assignment in assignments)
        {
            if (assignment.Path is "theme.name" or "theme.auto_switch")
            {
                themePatched = true;
                break;
            }
        }

        if (themePatched && !string.IsNullOrWhiteSpace(config.Theme.Name))
            _ = live.Theme.SetName(config.Theme.Name);

        live.Notifications.Rebind(live.Ui);
    }

    internal static async Task ApplyGotoJumpAsync(
        KeyActionRequest request,
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        var id = request.PromptText;
        if (string.IsNullOrWhiteSpace(id))
            return;
        var kind = request.Index is int n && n >= 0 && n <= (int)GotoTargetKind.PluginAction
            ? (GotoTargetKind)n
            : GotoTargetKind.Workspace;
        switch (kind)
        {
            case GotoTargetKind.Cube:
                return;
            case GotoTargetKind.PluginAction:
                await live.Dispatcher.InvokePluginActionAsync(id, ct).ConfigureAwait(false);
                return;
            case GotoTargetKind.Workspace:
                await live.Dispatcher.FocusWorkspaceAsync(id, ct).ConfigureAwait(false);
                break;
            case GotoTargetKind.Tab:
                var focused = await control.CallAsync(
                        ProtocolMethods.TabFocus,
                        new JsonObject { ["tab_id"] = id },
                        ct)
                    .ConfigureAwait(false);
                live.Dispatcher.ApplyTab(focused);
                break;
            default:
                live.Dispatcher.FocusPane(id);
                break;
        }
    }

    internal static string? MouseHistorySeedPaneId(ChromeHit? hit, MouseEvent ev, MouseEngine? mouse = null)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (ev.Action is MouseAction.Press
            && hit is { Kind: ChromeHitKind.Pane, IsFrame: false, PaneId: { Length: > 0 } pressPane })
        {
            return pressPane;
        }

        // Seed the in-progress select pane only. Scrollbar wheel/drag uses
        // pane.scroll, not pane.read.
        // A split/tab/sidebar drag must not pane.read a sibling under the cursor.
        if (ev.Action is MouseAction.Drag && mouse?.ActiveHistoryPaneId is { Length: > 0 } selecting)
            return selecting;

        return null;
    }

    /// <summary>
    /// In-progress select or pending click keeps the press pane, not the current hit.
    /// </summary>
    internal static string? MouseFeedPaneId(string? seedPane, MouseEngine mouse, ChromeHit? hit)
    {
        ArgumentNullException.ThrowIfNull(mouse);
        if (!string.IsNullOrWhiteSpace(seedPane))
            return seedPane;
        if (mouse.ActiveHistoryPaneId is { Length: > 0 } active)
            return active;
        return hit?.PaneId;
    }

    internal static IReadOnlyList<MouseEngineResult> FeedMouseWithAttachContext(
        AttachLiveState live,
        MouseEvent ev,
        string? seedPane,
        ChromeHit? hit)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(ev);
        IReadOnlyList<MouseEngineResult> results = [];
        live.WithPaint(() =>
        {
            results = live.Mouse.Feed(
                ev,
                MouseFeedContextFor(live, MouseFeedPaneId(seedPane, live.Mouse, hit)));
        });
        return results;
    }

    internal static async Task<IReadOnlyList<MouseEngineResult>> FeedMouseAttachAsync(
        AttachLiveState live,
        IAttachCommandPort port,
        MouseEvent ev,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(ev);
        var hit = ChromeForHitTest(live) is { } geo
            ? ChromeHitTest.Hit(
                geo,
                ev.Col,
                ev.Row,
                live.Mouse.Menu,
                live.Engine.PaintMode,
                live.PopupOpen)
            : null;
        var seedPane = live.PopupOpen
            ? null
            : MouseHistorySeedPaneId(hit, ev, live.Mouse);
        if (seedPane is not null)
            await EnsureHistorySeededAsync(live, port, seedPane, ct).ConfigureAwait(false);

        return FeedMouseWithAttachContext(live, ev, seedPane, hit);
    }

    internal static MouseFeedContext MouseFeedContextFor(AttachLiveState live, string? paneId = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        var id = string.IsNullOrWhiteSpace(paneId) ? live.PaneId : paneId;
        var policy = "hypa";
        if (!string.IsNullOrWhiteSpace(id)
            && live.PaneRightClick.TryGetValue(id, out var stored)
            && !string.IsNullOrWhiteSpace(stored))
        {
            policy = stored;
        }

        AssembledSnapshot? snap = null;
        if (!string.IsNullOrWhiteSpace(id) && live.TryGetPaneFrame(id, out var frame))
            snap = frame;
        else if (live.LastComplete is { } last
            && !string.IsNullOrWhiteSpace(id)
            && string.Equals(last.PaneId, id, StringComparison.Ordinal))
        {
            snap = last;
        }

        PaneHistoryView? history = live.History;
        if (history.IsSeeded
            && !string.IsNullOrWhiteSpace(id)
            && !string.Equals(history.PaneId, id, StringComparison.Ordinal))
        {
            history = null;
        }

        var mouseId = live.PopupOpen
            ? ProtocolEventTypes.TerminalRenderTargetPopup
            : id;
        var maxOffset = 0;
        if (!string.IsNullOrWhiteSpace(id))
            live.TryGetPaneScrollMetrics(id, out _, out maxOffset, out _);
        return new MouseFeedContext(
            ChromeForHitTest(live),
            ReadMouseMode(live, mouseId),
            policy,
            live.PaneId,
            snap,
            history,
            live.PopupOpen,
            maxOffset,
            ReadMouseEncoding(live, mouseId),
            live.Cubes)
        {
            ContinuityEnabled = live.Release.ContinuityEnabled,
            Workspaces = live.SidebarInput?.Workspaces,
            CollapsedWorktreeGroups = live.CollapsedWorktreeGroups,
            OverlayPaneId = live.OverlayPaneId,
            LinkedPlugins = live.LinkedPlugins,
            ModalBlocksChrome = ExclusiveUiBlocksChromeHits(live),
        };
    }

    private static bool ExclusiveUiBlocksChromeHits(AttachLiveState live) =>
        live.CubesPairing.IsOpen
        || live.Worktrees.IsOpen
        || live.Overlay.OwnsModal
        || live.Engine.Mode is AttachClientMode.Settings
            or AttachClientMode.WhatsNew
            or AttachClientMode.Onboarding;

    internal static void HydratePaneRightClick(AttachLiveState live, JsonElement json)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (json.ValueKind != JsonValueKind.Object)
            return;

        if (json.TryGetProperty("panes", out var panes) && panes.ValueKind == JsonValueKind.Array)
        {
            foreach (var pane in panes.EnumerateArray())
                TakePaneRightClick(live, pane);
        }

        TakePaneRightClick(live, json);
        if (json.TryGetProperty("root", out var root))
            WalkLayoutRightClick(live, root);
        if (json.TryGetProperty("layout", out var layout))
            WalkLayoutRightClick(live, layout);
    }

    private static void WalkLayoutRightClick(AttachLiveState live, JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;
        TakePaneRightClick(live, node);
        if (node.TryGetProperty("first", out var first))
            WalkLayoutRightClick(live, first);
        if (node.TryGetProperty("second", out var second))
            WalkLayoutRightClick(live, second);
    }

    private static void TakePaneRightClick(AttachLiveState live, JsonElement pane)
    {
        var id = TryJsonString(pane, "pane_id");
        var policy = TryJsonString(pane, "right_click");
        if (string.IsNullOrWhiteSpace(id) || !PaneRightClick.IsValid(policy))
            return;
        live.PaneRightClick[id] = policy!;
    }

    private static void AppendHistoryLive(AttachLiveState live, string? paneId, ReadOnlySpan<byte> bytes)
    {
        if (!live.History.IsSeeded || bytes.IsEmpty || string.IsNullOrWhiteSpace(paneId))
            return;
        if (!string.Equals(live.History.PaneId, paneId, StringComparison.Ordinal))
            return;
        live.History.AppendLive(bytes);
    }

    internal static string ReadMouseMode(AssembledSnapshot? snap)
    {
        if (snap is null || !PaneMouseMode.TryRead(snap.Snapshot, out var mode))
            return PaneMouseMode.None;
        return mode;
    }

    internal static string ReadMouseMode(AttachLiveState live, string? paneId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!string.IsNullOrWhiteSpace(paneId) && live.TryGetPaneMouseMode(paneId, out var tracked))
            return tracked;

        AssembledSnapshot? snap = null;
        if (!string.IsNullOrWhiteSpace(paneId) && live.TryGetPaneFrame(paneId, out var frame))
            snap = frame;
        else if (live.LastComplete is { } last
            && !string.IsNullOrWhiteSpace(paneId)
            && string.Equals(last.PaneId, paneId, StringComparison.Ordinal))
        {
            snap = last;
        }

        return ReadMouseMode(snap);
    }

    internal static MouseProtocolEncoding ReadMouseEncoding(AttachLiveState live, string? paneId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!string.IsNullOrWhiteSpace(paneId))
            return live.GetPaneMouseEncoding(paneId);
        return MouseProtocolEncoding.Sgr;
    }

    /// <summary>
    /// True when the assembled pane snapshot requested DECSET 1004 outer focus reports.
    /// </summary>
    internal static bool ReadFocusReporting(AssembledSnapshot? snap)
    {
        if (snap is null || snap.Snapshot.ValueKind != JsonValueKind.Object)
            return false;
        if (!snap.Snapshot.TryGetProperty("modes", out var modes)
            || modes.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return modes.TryGetProperty("focus_reporting", out var reporting)
            && reporting.ValueKind == JsonValueKind.True;
    }

    internal static bool ReadFocusReporting(AttachLiveState live, string? paneId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(paneId))
            return false;

        AssembledSnapshot? snap = null;
        if (live.TryGetPaneFrame(paneId, out var frame) && frame is not null)
            snap = frame;
        else if (live.LastComplete is { } last
            && string.Equals(last.PaneId, paneId, StringComparison.Ordinal))
        {
            snap = last;
        }

        return ReadFocusReporting(snap);
    }

    /// <summary>
    /// Forward outer TTY focus CSI to the focused pane when that pane requested DECSET 1004.
    /// </summary>
    internal static async Task ForwardOuterFocusIfRequestedAsync(
        IReadOnlyList<OuterFocusEvent> events,
        IAttachCommandPort control,
        SemaphoreSlim controlGate,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationTokenSource linked,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(controlGate);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(linked);

        if (events.Count == 0
            || !live.HasLivePane
            || !ReadFocusReporting(live, live.PaneId))
        {
            return;
        }

        foreach (var focusEvent in events)
        {
            var report = focusEvent == OuterFocusEvent.FocusIn
                ? SnapshotPainter.FocusInReport
                : SnapshotPainter.FocusOutReport;
            // The report goes on the input sender with the keys of the pane.
            // It thus keeps its place before the keys that follow it, and it
            // does not wait for the control gate, which a host resize can
            // hold across several round trips.
            if (live.InputSender is { IsFaulted: false } sender)
            {
                if (!BlocksPaneKeys(live))
                {
                    _ = await sender.EnqueueAsync(
                            Encoding.UTF8.GetBytes(report),
                            ct,
                            keyboard: false)
                        .ConfigureAwait(false);
                }

                continue;
            }

            await SendPaneKeysUnderGateAsync(
                    async token =>
                    {
                        await control.CallAsync(
                                ProtocolMethods.PaneSendKeys,
                                BuildPaneSendKeys(
                                    live.PaneId!,
                                    Encoding.UTF8.GetBytes(report),
                                    live.InputLease,
                                    keyboard: false),
                                token)
                            .ConfigureAwait(false);
                    },
                    controlGate,
                    live,
                    tty,
                    linked,
                    ct)
                .ConfigureAwait(false);
        }
    }

    internal async Task RequestFocusSnapshotAsync(
        Func<CancellationToken, Task> refresh,
        CancellationToken ct)
    {
        if (!_focus.OnFocusIn())
            return;
        if (Interlocked.CompareExchange(ref _focusRedrawBusy, 1, 0) != 0)
            return;

        try
        {
            await refresh(ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _focusRedrawBusy, 0);
        }
    }

    private async Task RequestFocusSnapshotAsync(
        ControlPlaneClient control,
        SemaphoreSlim controlGate,
        AttachLiveState live,
        UnixRawTerminal tty,
        CancellationTokenSource linked,
        CancellationToken ct)
    {
        await RequestFocusSnapshotAsync(
                async token =>
                {
                    await RefreshFocusControlAsync(
                            async (paneId, leaseId, controlSub, callCt) =>
                            {
                                await control.CallAsync(
                                        ProtocolMethods.TerminalControl,
                                        new JsonObject
                                        {
                                            ["pane_id"] = paneId,
                                            ["lease_id"] = leaseId,
                                            ["subscription_id"] = controlSub,
                                        },
                                        callCt,
                                        timeout: TimeSpan.FromSeconds(2))
                                    .ConfigureAwait(false);
                                await DrainOverlayEventsAsync(
                                        control, live, tty, callCt, linked)
                                    .ConfigureAwait(false);
                            },
                            controlGate,
                            live,
                            tty,
                            linked,
                            token)
                        .ConfigureAwait(false);
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the focus redraw of a host focus-in report off the input loop.
    /// The task that runs it keeps every exception: the redraw cancels
    /// <paramref name="linked"/> itself on a lost connection, and any other
    /// failure becomes a status error, not a stop of the input loop.
    /// </summary>
    private void StartFocusRedrawOffLoop(
        ControlPlaneClient control,
        SemaphoreSlim controlGate,
        AttachLiveState live,
        UnixRawTerminal tty,
        CancellationTokenSource linked,
        CancellationToken ct)
    {
        var redraw = Task.Run(
            () => RequestFocusSnapshotAsync(control, controlGate, live, tty, linked, ct),
            CancellationToken.None);
        live.FocusRedraw = ObserveFocusRedrawAsync(redraw, live, tty);
    }

    private static async Task ObserveFocusRedrawAsync(
        Task redraw,
        AttachLiveState live,
        UnixRawTerminal tty)
    {
        try
        {
            await redraw.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException
            or ObjectDisposedException
            or ControlPlaneClientTimeoutException)
        {
            // RefreshFocusControlAsync cancelled the attach before it threw.
        }
        catch (Exception ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex.Message));
            try
            {
                PaintChrome(tty, live);
            }
            catch (Exception paint) when (paint is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // A lost host terminal ends the attach on its own path.
            }
        }
    }

    /// <summary>
    /// Focused 1049l uses <c>terminal.control</c>. A sibling 1049l re-observes
    /// that pane. Do not snapshot the focused pane for a sibling leave-alt.
    /// </summary>
    private async Task RequestLiveLeaveAltSnapshotAsync(
        string? paneId,
        ControlPlaneClient render,
        ControlPlaneClient control,
        SemaphoreSlim controlGate,
        AttachLiveState live,
        UnixRawTerminal tty,
        CancellationTokenSource linked,
        CancellationToken ct)
    {
        if (IsFocusedLivePane(live, paneId))
        {
            await RequestFocusSnapshotAsync(
                    control, controlGate, live, tty, linked, ct)
                .ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(paneId))
            return;

        var paintError = false;
        await controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequestSiblingSnapshotAsync(
                    new ControlPlaneAttachCommandPort(LiveControlClient(live, render) ?? render), live, paneId, ct)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            linked.Cancel();
            throw;
        }
        catch (ObjectDisposedException)
        {
            linked.Cancel();
            throw;
        }
        catch (ControlPlaneException ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
            paintError = true;
        }
        catch (InvalidOperationException ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
            paintError = true;
        }
        catch (ControlPlaneClientTimeoutException)
        {
            linked.Cancel();
            throw;
        }
        finally
        {
            controlGate.Release();
        }

        if (paintError)
            PaintChrome(tty, live);
    }

    internal static bool IsFocusedLivePane(AttachLiveState live, string? paneId)
    {
        ArgumentNullException.ThrowIfNull(live);
        var id = paneId ?? live.PaneId;
        var focused = live.Chrome?.FocusedPaneId ?? live.PaneId;
        return !string.IsNullOrWhiteSpace(id)
            && !string.IsNullOrWhiteSpace(focused)
            && string.Equals(id, focused, StringComparison.Ordinal);
    }

    internal static async Task RequestLiveLeaveAltSnapshotAsync(
        string? paneId,
        IAttachCommandPort render,
        Func<CancellationToken, Task> requestFocus,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(requestFocus);
        ArgumentNullException.ThrowIfNull(live);

        if (IsFocusedLivePane(live, paneId))
        {
            await requestFocus(ct).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(paneId))
            return;

        await RequestSiblingSnapshotAsync(render, live, paneId, ct).ConfigureAwait(false);
    }

    internal static async Task RequestSiblingSnapshotAsync(
        IAttachCommandPort render,
        AttachLiveState live,
        string paneId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(paneId) || string.IsNullOrWhiteSpace(live.RenderSub))
            return;

        await render.CallAsync(
                ProtocolMethods.TerminalObserve,
                new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["subscription_id"] = live.RenderSub,
                    ["replace"] = false,
                },
                ct)
            .ConfigureAwait(false);
        lock (live.ChromeStateGate)
            live.ObservedPaneIds.Add(paneId);
    }

    internal static async Task RefreshFocusControlAsync(
        Func<string, string, string, CancellationToken, Task> callControl,
        SemaphoreSlim controlGate,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationTokenSource linked,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(callControl);
        ArgumentNullException.ThrowIfNull(controlGate);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(linked);

        var paintError = false;
        await controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var paneId = live.PaneId;
            var leaseId = live.InputLease;
            var controlSub = live.ControlSub;
            if (string.IsNullOrWhiteSpace(paneId) || string.IsNullOrWhiteSpace(leaseId))
                return;

            await callControl(paneId, leaseId, controlSub, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            linked.Cancel();
            throw;
        }
        catch (ObjectDisposedException)
        {
            linked.Cancel();
            throw;
        }
        catch (ControlPlaneException ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
            paintError = true;
        }
        catch (InvalidOperationException ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
            paintError = true;
        }
        catch (ControlPlaneClientTimeoutException)
        {
            linked.Cancel();
            throw;
        }
        finally
        {
            controlGate.Release();
        }

        if (paintError && tty is not null)
            PaintChrome(tty, live);
    }

    private static async Task ForwardMouseKeysIfAllowedAsync(
        AttachLiveState live,
        Func<CancellationToken, Task> send,
        SemaphoreSlim? controlGate,
        UnixRawTerminal? tty,
        CancellationTokenSource? linked,
        CancellationToken ct,
        bool gateHeld = false)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(send);
        if (controlGate is not null && linked is not null)
        {
            await SendPaneKeysUnderGateAsync(
                    send,
                    controlGate,
                    live,
                    tty,
                    linked,
                    ct,
                    gateHeld: gateHeld)
                .ConfigureAwait(false);
            return;
        }

        if (BlocksPaneKeys(live))
            return;
        await send(ct).ConfigureAwait(false);
    }

    internal static async Task SendPaneKeysUnderGateAsync(
        Func<CancellationToken, Task> send,
        SemaphoreSlim controlGate,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationTokenSource linked,
        CancellationToken ct,
        bool gateHeld = false,
        Func<CancellationToken, Task>? drain = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(controlGate);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(linked);

        var paintError = false;
        var closedPopup = false;
        if (!gateHeld)
        {
            var gateStart = live.Latency.Now();
            await controlGate.WaitAsync(ct).ConfigureAwait(false);
            live.Latency.RecordHop(AttachLatencyHop.StdinGateWait, gateStart);
        }

        try
        {
            try
            {
                // The input-wire hop is the sender's pane notification only.
                // This path also carries popup calls and overlay keys.
                if (!BlocksPaneKeys(live))
                    await send(ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                linked.Cancel();
                throw;
            }
            catch (ObjectDisposedException)
            {
                linked.Cancel();
                throw;
            }
            catch (ControlPlaneException ex)
            {
                closedPopup = ClosePopupIfNotOpen(live, ex);
                if (!closedPopup)
                    live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                paintError = true;
            }
            catch (InvalidOperationException ex)
            {
                live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                paintError = true;
            }
            catch (ControlPlaneClientTimeoutException)
            {
                linked.Cancel();
                throw;
            }
            finally
            {
                // Overlay close may already sit in the demux buffer. Drain after
                // send_keys even when the pane is gone.
                if (drain is not null && !linked.IsCancellationRequested)
                    await drain(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!gateHeld)
                controlGate.Release();
        }

        if (paintError && tty is not null)
        {
            if (closedPopup)
                PaintAfterPopupClosed(tty, live);
            else if (!string.IsNullOrWhiteSpace(live.StatusError))
                PaintChrome(tty, live);
        }
    }

    internal static bool ClosePopupIfNotOpen(AttachLiveState live, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is not ControlPlaneException cpe
            || !string.Equals(cpe.Message, "popup_not_open", StringComparison.Ordinal))
        {
            return false;
        }

        SetPopupOpen(live, open: false);
        SyncPopupChrome(live);
        return true;
    }

    internal static bool ClosePopupIfAreaTooSmall(AttachLiveState live, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is not ControlPlaneException cpe
            || !string.Equals(cpe.Message, "area too small for popup", StringComparison.Ordinal))
        {
            return false;
        }

        HidePopupFrame(live);
        return true;
    }

    internal static async Task ResizeOpenPopupAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        if (!live.PopupOpen || live.Chrome is not { } chrome)
            return;

        if (chrome.Content.Cols < PopupGeometry.MinOuterCols
            || chrome.Content.Rows < PopupGeometry.MinOuterRows)
        {
            HidePopupFrame(live);
            if (tty is not null)
                PaintAfterPopupClosed(tty, live);
            return;
        }

        try
        {
            var result = await control.CallAsync(
                    ProtocolMethods.PopupResize,
                    new JsonObject
                    {
                        ["area_cols"] = chrome.Content.Cols,
                        ["area_rows"] = chrome.Content.Rows,
                    },
                    ct)
                .ConfigureAwait(false);
            ApplyPopupResizeResult(live, result);
            if (tty is not null)
            {
                if (live.PopupOpen && live.Chrome?.PopupFrame is not null)
                    PaintChrome(tty, live, paintPopupInnerSnapshot: live.PopupSnapshot is not null);
                else
                    PaintAfterPopupClosed(tty, live);
            }
        }
        catch (ControlPlaneException ex)
        {
            if (ClosePopupIfNotOpen(live, ex) || ClosePopupIfAreaTooSmall(live, ex))
            {
                if (tty is not null)
                    PaintAfterPopupClosed(tty, live);
                return;
            }

            live.Status.Set(StatusOwner.Overlay, StatusSeverity.Error, FormatStatus(ex));
            if (tty is not null)
                PaintChrome(tty, live);
        }
    }

    internal static bool ApplyPopupResizeResult(AttachLiveState live, JsonElement result)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.PopupOpen || result.ValueKind != JsonValueKind.Object)
            return false;

        var cols = result.TryGetProperty("cols", out var colsEl) && colsEl.TryGetInt32(out var c)
            ? c
            : 0;
        var rows = result.TryGetProperty("rows", out var rowsEl) && rowsEl.TryGetInt32(out var r)
            ? r
            : 0;
        var outerCols = result.TryGetProperty("outer_cols", out var ocEl) && ocEl.TryGetInt32(out var oc)
            ? oc
            : 0;
        var outerRows = result.TryGetProperty("outer_rows", out var orEl) && orEl.TryGetInt32(out var orv)
            ? orv
            : 0;
        if (cols < 1 && rows < 1)
            return false;
        if (cols > 0)
            live.PopupInnerCols = cols;
        if (rows > 0)
            live.PopupInnerRows = rows;
        if (outerCols > 0)
            live.PopupOuterCols = outerCols;
        if (outerRows > 0)
            live.PopupOuterRows = outerRows;
        live.PopupLockInnerGeometry = false;
        if (live.Chrome is not { } chrome)
            return true;

        var resolved = outerCols >= PopupGeometry.MinOuterCols && outerRows >= PopupGeometry.MinOuterRows
            ? PopupGeometry.TryFromOuter(
                chrome.Content.Cols,
                chrome.Content.Rows,
                outerCols,
                outerRows,
                cols > 0 ? cols : live.PopupInnerCols,
                rows > 0 ? rows : live.PopupInnerRows)
            : ResolveLivePopupGeometry(live, chrome.Content);
        if (resolved is null)
        {
            live.Chrome = chrome with { PopupFrame = null };
            return true;
        }

        live.PopupPaintContentCols = chrome.Content.Cols;
        live.PopupPaintContentRows = chrome.Content.Rows;
        live.Chrome = chrome with { PopupFrame = PopupPainter.FrameFromGeometry(chrome.Content, resolved) };
        return true;
    }

    internal static List<byte> DecodeInput(ReadOnlySpan<byte> raw, List<byte> csi, out bool focusIn) =>
        DecodeInput(raw, csi, out focusIn, out List<MouseEvent> _);

    internal static List<byte> DecodeInput(
        ReadOnlySpan<byte> raw,
        List<byte> csi,
        out bool focusIn,
        out List<MouseEvent> mouse) =>
        DecodeInput(raw, csi, out focusIn, out mouse, out HostThemeDecode _);

    internal static List<byte> DecodeInput(
        ReadOnlySpan<byte> raw,
        List<byte> csi,
        out bool focusIn,
        out List<MouseEvent> mouse,
        out HostThemeDecode theme) =>
        DecodeInput(raw, csi, out focusIn, out _, out mouse, out _, out theme);

    internal static List<byte> DecodeInput(
        ReadOnlySpan<byte> raw,
        List<byte> csi,
        out bool focusIn,
        out bool focusOut,
        out List<MouseEvent> mouse) =>
        DecodeInput(raw, csi, out focusIn, out focusOut, out mouse, out _);

    internal static List<byte> DecodeInput(
        ReadOnlySpan<byte> raw,
        List<byte> csi,
        out bool focusIn,
        out bool focusOut,
        out List<MouseEvent> mouse,
        out List<OuterFocusEvent> outerFocusEvents) =>
        DecodeInput(raw, csi, out focusIn, out focusOut, out mouse, out outerFocusEvents, out _);

    internal static List<byte> DecodeInput(
        ReadOnlySpan<byte> raw,
        List<byte> csi,
        out bool focusIn,
        out bool focusOut,
        out List<MouseEvent> mouse,
        out List<OuterFocusEvent> outerFocusEvents,
        out HostThemeDecode theme) =>
        DecodeInput(raw, csi, out focusIn, out focusOut, out mouse, out outerFocusEvents, out theme, mouseReports: null);

    internal static List<byte> DecodeInput(
        ReadOnlySpan<byte> raw,
        List<byte> csi,
        out bool focusIn,
        out bool focusOut,
        out List<MouseEvent> mouse,
        out List<OuterFocusEvent> outerFocusEvents,
        out HostThemeDecode theme,
        List<byte[]>? mouseReports,
        AttachHostInputFramer? framer = null)
    {
        focusIn = false;
        focusOut = false;
        mouse = [];
        outerFocusEvents = [];
        theme = default;
        var keys = new List<byte>(raw.Length);
        if (framer is not null)
        {
            framer.ExpireUnansweredHostQueries();
            // Bytes after the idle limit are never part of the abandoned reply.
            framer.DropAbandonedHostColorReply(csi);
        }

        var discardOsc = framer is { DiscardUntilOscTerminator: true };
        var discardedTail = framer?.DiscardedTailBytes ?? 0;
        var lastWasEsc = false;
        foreach (var b in raw)
        {
            if (discardOsc)
            {
                // Do not leak the tail of an oversized palette reply as keys.
                discardedTail++;
                if (b == 0x07 || (lastWasEsc && b == (byte)'\\'))
                {
                    discardOsc = false;
                    discardedTail = 0;
                    lastWasEsc = false;
                    continue;
                }

                if (discardedTail > AttachHostInputFramer.MaxDiscardedOscTailBytes)
                {
                    discardOsc = false;
                    discardedTail = 0;
                }

                lastWasEsc = b == 0x1b;
                continue;
            }

            if (IsX10Pending(csi))
            {
                csi.Add(b);
                if (csi.Count == 6)
                {
                    if (MouseDecoder.TryParse(CollectionsMarshal.AsSpan(csi), out var x10))
                    {
                        mouse.Add(x10);
                        mouseReports?.Add([.. CollectionsMarshal.AsSpan(csi)]);
                    }

                    csi.Clear();
                }

                continue;
            }

            if (IsOscPending(csi) && !CanBeHostReplyPrefix(csi, b))
            {
                // No host reply starts like this. The bytes are keys, for
                // example Alt+] and then typed text. Do not drop them.
                keys.AddRange(csi);
                csi.Clear();
            }

            if (IsOscPending(csi))
            {
                csi.Add(b);
                if (AttachHostInputFramer.IsIncompleteDefaultColorReply(csi)
                    && csi.Count >= AttachHostInputFramer.MaxDiscardedOscTailBytes)
                {
                    // No real OSC 10 or OSC 11 reply is this long. Drop it,
                    // so that the bytes after it are keys again.
                    theme.OscDropped = true;
                    csi.Clear();
                    continue;
                }

                if (csi.Count > MaxOscBytes)
                {
                    theme.OscDropped = true;
                    csi.Clear();
                    discardOsc = true;
                    discardedTail = 0;
                    lastWasEsc = b == 0x1b;
                    continue;
                }

                if (HostThemeParser.IsOscTerminated(CollectionsMarshal.AsSpan(csi)))
                {
                    TakeOscReport(csi, ref theme);
                    csi.Clear();
                }

                continue;
            }

            if (csi.Count == 0)
            {
                if (b == 0x1b)
                {
                    csi.Add(b);
                    continue;
                }

                keys.Add(b);
                continue;
            }

            if (csi.Count == 1 && csi[0] == 0x1b && b == 0x1b)
            {
                // ESC ESC is legacy ctrl+alt+[ (tmux reads it as M-Escape).
                // Hold both bytes. The next byte decides: [, O or ] starts a
                // new sequence, so the first ESC is a key on its own.
                csi.Add(b);
                continue;
            }

            if (csi.Count == 2 && csi[0] == 0x1b && csi[1] == 0x1b)
            {
                if (b is (byte)'[' or (byte)'O' or (byte)']')
                {
                    // ESC and then a CSI, SS3 or OSC: two keys, or a key
                    // and a host reply.
                    keys.Add(0x1b);
                    csi.RemoveAt(0);
                }
                else
                {
                    keys.Add(0x1b);
                    keys.Add(0x1b);
                    csi.Clear();
                    if (b == 0x1b)
                    {
                        csi.Add(b);
                        continue;
                    }

                    keys.Add(b);
                    continue;
                }
            }

            csi.Add(b);
            if (csi.Count == 2 && b == (byte)'O')
            {
                // ESC O is an incomplete SS3. The final byte may arrive
                // in the next read. Hold it. A lone Alt+O flushes on idle.
                continue;
            }

            if (csi.Count == 3 && csi[0] == 0x1b && csi[1] == (byte)'O')
            {
                if (b == 0x1b)
                {
                    keys.Add(0x1b);
                    keys.Add((byte)'O');
                    csi.Clear();
                    csi.Add(0x1b);
                    continue;
                }

                keys.AddRange(csi);
                csi.Clear();
                continue;
            }

            if (csi.Count == 2 && b != (byte)'[' && b != (byte)']')
            {
                keys.AddRange(csi);
                csi.Clear();
                continue;
            }

            if (csi.Count == 2 && b == (byte)']')
                continue;

            if (csi.Count > MaxCsiBytes)
            {
                if (IsMouseCsi(csi) || IsOscPending(csi))
                    csi.Clear();
                else
                {
                    keys.AddRange(csi);
                    csi.Clear();
                }

                continue;
            }

            if (csi.Count == 3 && b == (byte)'M')
                continue;

            if (csi.Count >= 3 && IsCsiFinalByte(b))
            {
                // Outer TTY focus reports (DECSET 1004): strip CSI, keep parse order.
                if (csi.Count == 3 && b == (byte)'I')
                {
                    focusIn = true;
                    outerFocusEvents.Add(OuterFocusEvent.FocusIn);
                    csi.Clear();
                    continue;
                }

                if (csi.Count == 3 && b == (byte)'O')
                {
                    focusOut = true;
                    outerFocusEvents.Add(OuterFocusEvent.FocusOut);
                    csi.Clear();
                    continue;
                }

                if (HostThemeParser.TryParseColorSchemeReport(CollectionsMarshal.AsSpan(csi), out var appearance))
                {
                    theme.Explicit = appearance;
                    csi.Clear();
                    continue;
                }

                // Replies to the keyboard probe are never keys.
                if (TryParseKittyKeyboardFlagsReply(csi, out var kittyFlags))
                {
                    framer?.NoteKittyKeyboardReply(kittyFlags);
                    csi.Clear();
                    continue;
                }

                if (IsPrimaryDeviceAttributesReply(csi))
                {
                    framer?.NotePrimaryDeviceAttributesReply();
                    csi.Clear();
                    continue;
                }

                if (MouseDecoder.TryParse(CollectionsMarshal.AsSpan(csi), out var parsed))
                {
                    mouse.Add(parsed);
                    mouseReports?.Add([.. CollectionsMarshal.AsSpan(csi)]);
                    csi.Clear();
                    continue;
                }

                keys.AddRange(csi);
                csi.Clear();
            }
        }

        if (framer is not null)
        {
            framer.DiscardUntilOscTerminator = discardOsc;
            framer.DiscardedTailBytes = discardedTail;
            framer.NoteHostColorReplies(theme.ColorReports);
            framer.NotePendingAfterRead(csi);
        }

        return keys;
    }

    // Host replies that start with ESC ]. OSC 4, OSC 10 and OSC 11 answer
    // the theme query. OSC 7501 answers the host status detection query.
    private static readonly byte[][] HostOscReplyIntroducers =
    [
        "\u001b]4;"u8.ToArray(),
        "\u001b]10;"u8.ToArray(),
        "\u001b]11;"u8.ToArray(),
        "\u001b]7501;"u8.ToArray(),
    ];

    /// <summary>
    /// <paramref name="pending"/> plus <paramref name="next"/> can still be a
    /// host OSC reply: it is a prefix of a reply introducer, or it starts with
    /// one.
    /// </summary>
    internal static bool CanBeHostReplyPrefix(List<byte> pending, byte next)
    {
        var length = pending.Count + 1;
        foreach (var introducer in HostOscReplyIntroducers)
        {
            var matched = true;
            var compare = Math.Min(length, introducer.Length);
            for (var i = 0; i < compare && matched; i++)
            {
                var b = i < pending.Count ? pending[i] : next;
                matched = b == introducer[i];
            }

            if (matched)
                return true;
        }

        return false;
    }

    /// <summary>
    /// <paramref name="pending"/> can still be a host OSC reply. See
    /// <see cref="CanBeHostReplyPrefix(List{byte}, byte)"/>.
    /// </summary>
    internal static bool CanBeHostReplyPrefix(List<byte> pending)
    {
        if (pending.Count == 0)
            return false;
        foreach (var introducer in HostOscReplyIntroducers)
        {
            var compare = Math.Min(pending.Count, introducer.Length);
            var matched = true;
            for (var i = 0; i < compare && matched; i++)
                matched = pending[i] == introducer[i];
            if (matched)
                return true;
        }

        return false;
    }

    /// <summary>
    /// <paramref name="pending"/> starts with a complete host reply
    /// introducer.
    /// </summary>
    private static bool HasHostReplyIntroducer(List<byte> pending)
    {
        foreach (var introducer in HostOscReplyIntroducers)
        {
            if (pending.Count < introducer.Length)
                continue;
            var matched = true;
            for (var i = 0; i < introducer.Length && matched; i++)
                matched = pending[i] == introducer[i];
            if (matched)
                return true;
        }

        return false;
    }

    internal const int MaxCsiBytes = 64;

    /// <summary>
    /// bytes (<c>src/raw_input.rs:917-929</c>). Ghostty may batch OSC 4.
    /// </summary>
    internal const int MaxOscBytes = 16384;

    internal const int SplashPollTimeoutMs = 16;

    /// <summary>
    /// The wait for a lone ESC when mouse capture is off. No mouse report can
    /// then start with that ESC. <c>src/raw_input.rs:42</c>
    /// <c>RAW_INPUT_IDLE_FLUSH_TIMEOUT_MS</c>.
    /// </summary>
    internal static readonly TimeSpan EscapeFlushTimeoutNoMouse = TimeSpan.FromMilliseconds(10);

    internal static int StdinPollTimeoutMs(
        List<byte> csi,
        bool animateIdle = false,
        bool mouseCapture = true,
        AttachHostInputFramer? framer = null)
    {
        if (animateIdle && csi.Count == 0)
            return SplashPollTimeoutMs;
        if (csi.Count == 0)
            return (int)EscapeFlushTimeout.TotalMilliseconds;
        if (IsMouseCsi(csi))
            return (int)MouseCsiFlushTimeout.TotalMilliseconds;
        // A host reply can split at its ESC. Keep the full wait while the
        // attach waits for replies.
        if (!mouseCapture
            && csi.Count == 1
            && csi[0] == 0x1b
            && framer is not ({ AwaitingHostColorReplies: true } or { KeyboardProbeOutstanding: true }))
        {
            return (int)EscapeFlushTimeoutNoMouse.TotalMilliseconds;
        }

        return (int)EscapeFlushTimeout.TotalMilliseconds;
    }

    /// <summary>
    /// <c>CSI ? flags u</c>: the host answers the kitty keyboard query
    /// <c>CSI ? u</c> with its current flags.
    /// </summary>
    internal static bool TryParseKittyKeyboardFlagsReply(List<byte> csi, out int flags)
    {
        flags = 0;
        if (csi.Count < 4 || csi[0] != 0x1b || csi[1] != (byte)'[' || csi[2] != (byte)'?' || csi[^1] != (byte)'u')
            return false;
        for (var i = 3; i < csi.Count - 1; i++)
        {
            var b = csi[i];
            if (b is < (byte)'0' or > (byte)'9')
                return false;
            flags = Math.Min(1 << 16, (flags * 10) + (b - '0'));
        }

        return true;
    }

    /// <summary><c>CSI ? ... c</c>: the host answers DA1 (<c>CSI c</c>).</summary>
    internal static bool IsPrimaryDeviceAttributesReply(List<byte> csi)
    {
        if (csi.Count < 4 || csi[0] != 0x1b || csi[1] != (byte)'[' || csi[2] != (byte)'?' || csi[^1] != (byte)'c')
            return false;
        for (var i = 3; i < csi.Count - 1; i++)
        {
            if (csi[i] is not ((>= (byte)'0' and <= (byte)'9') or (byte)';'))
                return false;
        }

        return true;
    }

    internal static bool IsCsiFinalByte(byte b) => b is >= 0x40 and <= 0x7E;

    internal static List<byte> FlushPendingCsi(List<byte> csi, AttachHostInputFramer? framer = null)
    {
        if (csi.Count == 0)
            return [];
        framer?.ExpireUnansweredHostQueries();
        if (IsOscPending(csi))
        {
            // A half-received OSC 10 or OSC 11 reply that waited too long
            // can no longer hold input. Drop it.
            if (framer is not null && framer.DropAbandonedHostColorReply(csi))
                return [];

            // While the reply window is open, keep a pending OSC that can be
            // a host reply across one idle flush (Unix attach also queried
            // OSC 4, HOST_COLOR_QUERY_REPLIES = 258). The next Ghostty chunk
            // then stitches instead of leaking rgb: tails.
            if (framer is { AwaitingHostColorReplies: true } && CanBeHostReplyPrefix(csi))
                return [];

            // With no framer there is no reply window to check. Keep the old
            // rule: a reply body that the host did not finish is not a key.
            if (framer is null && HasHostReplyIntroducer(csi))
            {
                csi.Clear();
                return [];
            }

            // The window is closed, or no host reply starts like this. The
            // bytes are keys, for example Alt+] alone. Do not drop them.
            var oscKeys = new List<byte>(csi);
            csi.Clear();
            return oscKeys;
        }

        if (IsMouseCsi(csi) || IsX10Pending(csi))
        {
            csi.Clear();
            return [];
        }

        if (framer is { AwaitingHostColorReplies: true }
            && csi.Count == 2
            && csi[0] == 0x1b
            && csi[1] == 0x1b)
        {
            // The second ESC can start a host reply that split at its
            // introducer. Send the first ESC and hold the second one.
            csi.RemoveAt(0);
            framer.HeldPendingHostReplyEsc = true;
            return [0x1b];
        }

        if (framer is { AwaitingHostColorReplies: true }
            && csi.Count == 1
            && csi[0] == 0x1b)
        {
            // so OSC ST split at the introducer stitches.
            if (!framer.HeldPendingHostReplyEsc)
            {
                framer.HeldPendingHostReplyEsc = true;
                return [];
            }

            framer.HeldPendingHostReplyEsc = false;
        }

        var keys = new List<byte>(csi);
        csi.Clear();
        return keys;
    }

    internal static bool IsOscPending(List<byte> csi) =>
        csi.Count >= 2
        && csi[0] == 0x1b
        && csi[1] == (byte)']';

    internal static HostTerminalTheme MergeHostTheme(AttachLiveState live, HostThemeDecode decode)
    {
        ArgumentNullException.ThrowIfNull(live);
        var next = live.HostTheme;
        if (decode.Foreground is { } fg)
            next = next.WithColor(HostDefaultColorKind.Foreground, fg);
        if (decode.Background is { } bg)
            next = next.WithColor(HostDefaultColorKind.Background, bg);
        if (decode.Explicit is { } explicitTheme)
            next = next.WithAppearance(explicitTheme);
        else if (decode.Inferred is { } inferredTheme && live.Theme.AppearanceExplicit is false)
            next = next.WithAppearance(inferredTheme);
        next = next with { Palette = next.Palette.Merge(decode.Palette) };
        return next;
    }

    internal static HostThemeApplyResult ApplyHostThemeDecode(AttachLiveState live, HostThemeDecode decode)
    {
        ArgumentNullException.ThrowIfNull(live);
        var chromeChanged = false;
        if (decode.Explicit is { } explicitAppearance)
            chromeChanged |= live.Theme.SetAppearance(explicitAppearance, explicitReport: true);
        if (decode.Inferred is { } inferred)
            chromeChanged |= live.Theme.SetAppearance(inferred, explicitReport: false);

        var next = MergeHostTheme(live, decode);
        var muxThemeChanged = next != live.HostTheme;
        live.HostTheme = next;
        return new HostThemeApplyResult(chromeChanged, muxThemeChanged);
    }

    /// <summary>
    /// Stdin host-theme path. Mux and chrome state stay pending until
    /// <c>client.host_theme.set</c> succeeds. A failed or cancelled RPC
    /// leaves the previous theme so the next OSC report retries.
    /// </summary>
    internal static async Task ApplyDecodedHostThemeAsync(
        AttachLiveState live,
        HostThemeDecode decode,
        IAttachCommandPort control,
        SemaphoreSlim controlGate,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(controlGate);

        // A key-only chunk has no theme fields. The merge below is then the
        // same theme, so the gated path makes no RPC and paints nothing.
        // Do not wait for the gate in that case: a heartbeat or a lease
        // renew that holds it must not delay a plain key.
        if (!decode.HasThemeFields)
        {
            // Keep the cancel check that WaitAsync(ct) made before.
            ct.ThrowIfCancellationRequested();
            return;
        }

        await controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = MergeHostTheme(live, decode);
            if (next != live.HostTheme)
            {
                try
                {
                    await control.CallAsync(
                            ProtocolMethods.ClientHostThemeSet,
                            HostThemeJson(next),
                            ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ControlPlaneException ex)
                {
                    live.Status.Set(StatusOwner.Theme, StatusSeverity.Error, FormatStatus(ex));
                    return;
                }
                catch (ControlPlaneClientTimeoutException ex)
                {
                    live.Status.Set(StatusOwner.Theme, StatusSeverity.Error, FormatStatus(ex.Message));
                    return;
                }
                catch (Exception ex)
                {
                    live.Status.Set(StatusOwner.Theme, StatusSeverity.Error, FormatStatus(ex.Message));
                    return;
                }
            }

            var apply = ApplyHostThemeDecode(live, decode);
            if (apply.ChromeChanged && tty is not null)
                PaintChrome(tty, live);
        }
        finally
        {
            controlGate.Release();
        }
    }

    private static void TakeOscReport(List<byte> csi, ref HostThemeDecode theme)
    {
        var sequence = CollectionsMarshal.AsSpan(csi);
        if (HostStatusReport.IsDetectionReply(sequence))
        {
            theme.HostStatusReply = true;
            return;
        }

        if (HostThemeParser.TryParseOscPaletteColor(sequence, out var index, out var paletteColor))
        {
            theme.Palette = theme.Palette.WithColor(index, paletteColor);
            theme.ColorReports++;
            return;
        }

        if (!HostThemeParser.TryParseOscDefaultColor(sequence, out var kind, out var color))
            return;
        theme.ColorReports++;
        if (kind is HostDefaultColorKind.Foreground)
            theme.Foreground = color;
        else
        {
            theme.Background = color;
            theme.Inferred = color.InferredAppearance();
        }
    }

    internal static JsonObject HostThemeJson(HostTerminalTheme theme)
    {
        var json = JsonSerializer.Serialize(
            ToHostThemeSetParams(theme),
            ProtocolJsonContext.Default.HostThemeSetParams);
        return JsonNode.Parse(json)!.AsObject();
    }

    internal static HostThemeSetParams ToHostThemeSetParams(HostTerminalTheme theme)
    {
        List<HostThemePaletteEntry>? palette = null;
        for (var i = 0; i < HostPalette.Size; i++)
        {
            if (theme.Palette[i] is not { } color)
                continue;
            palette ??= [];
            palette.Add(new HostThemePaletteEntry
            {
                I = i,
                R = color.R,
                G = color.G,
                B = color.B,
            });
        }

        return new HostThemeSetParams
        {
            Fg = ToRgb(theme.Foreground),
            Bg = ToRgb(theme.Background),
            Appearance = theme.Appearance is { } appearance
                ? (appearance is HostAppearance.Light ? "light" : "dark")
                : null,
            Palette = palette?.ToArray(),
        };
    }

    private static HostThemeRgb? ToRgb(HostRgb? color) =>
        color is { } c
            ? new HostThemeRgb { R = c.R, G = c.G, B = c.B }
            : null;

    internal static bool IsMouseCsi(List<byte> csi) =>
        csi.Count >= 3
        && csi[0] == 0x1b
        && csi[1] == (byte)'['
        && csi[2] is (byte)'<' or (byte)'M';

    internal static bool IsX10Pending(List<byte> csi) =>
        csi.Count >= 3
        && csi.Count < 6
        && csi[0] == 0x1b
        && csi[1] == (byte)'['
        && csi[2] == (byte)'M';

    /// <summary>
    /// The host resize lane of one attach run. Each SIGWINCH puts the host
    /// size on the lane and returns. The lane runs one
    /// <see cref="ResizeChromeAsync"/> at a time, with the newest size.
    /// <paramref name="control"/> reads the control client when a resize
    /// starts, so a reconnect that replaces the client is seen.
    /// </summary>
    internal static AttachHostResizeLane CreateHostResizeLane(
        Func<ControlPlaneClient?> control,
        SemaphoreSlim controlGate,
        AttachLiveState live,
        UnixRawTerminal tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(controlGate);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(tty);
        return new AttachHostResizeLane(
            (cols, rows, token) =>
            {
                var resizeClient = LiveControlClient(live, control());
                if (string.IsNullOrWhiteSpace(live.PaneId) || resizeClient is null)
                    return Task.CompletedTask;
                return ResizeChromeAsync(resizeClient, controlGate, live, tty, cols, rows, token);
            },
            ct);
    }

    private static async Task ResizeChromeAsync(
        ControlPlaneClient control,
        SemaphoreSlim gate,
        AttachLiveState live,
        UnixRawTerminal tty,
        int cols,
        int rows,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            NotifyActivationResize(live, cols, rows, tty);
            // The generation chain of each pane stays. A host resize does not
            // change a pane frame: a delta that comes before the new layout
            // still chains and paints. A pane whose box changes gets a
            // pane.resize, and the mux then posts a full frame at the new
            // size. Until it comes, the size check drops each frame of the old
            // size (AwaitsOwnPaneSize), so no frame of the old size paints
            // into the new layout.
            await RefreshChromeAsync(
                    new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control), live, tty, ct)
                .ConfigureAwait(false);

            if (live.Chrome is null && !string.IsNullOrWhiteSpace(live.PaneId)
                && !string.IsNullOrWhiteSpace(live.ResizeLease))
            {
                await control.CallAsync(
                        ProtocolMethods.PaneResize,
                        new JsonObject
                        {
                            ["pane_id"] = live.PaneId,
                            ["cols"] = cols,
                            ["rows"] = rows,
                            ["lease_id"] = live.ResizeLease,
                        },
                        ct)
                    .ConfigureAwait(false);
            }

            await DrainOverlayEventsAsync(control, live, tty, ct).ConfigureAwait(false);
            if (live.InputDetachRequested || live.DetachRequested)
                return;
        }
        catch
        {
            // SIGWINCH is best-effort
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task ResizeAsync(
        ControlPlaneClient control,
        SemaphoreSlim gate,
        string paneId,
        string leaseId,
        int cols,
        int rows,
        CancellationToken ct,
        bool ignoreErrors = true)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var resizeParams = new JsonObject
            {
                ["pane_id"] = paneId,
                ["cols"] = cols,
                ["rows"] = rows,
            };
            if (!string.IsNullOrWhiteSpace(leaseId))
                resizeParams["lease_id"] = leaseId;
            await control.CallAsync(
                    ProtocolMethods.PaneResize,
                    resizeParams,
                    ct)
                .ConfigureAwait(false);
            _ = control.DrainPendingEvents();
        }
        catch when (ignoreErrors)
        {
            // SIGWINCH is best-effort
        }
        finally
        {
            gate.Release();
        }
    }

    internal static async Task<JsonElement> EnsurePaneAndSnapshotAsync(
        ControlPlaneClient client,
        CancellationToken ct)
    {
        var snap = await client.CallAsync(ProtocolMethods.SessionSnapshot, ct: ct)
            .ConfigureAwait(false);
        // Restored sessions already have pane ids (live or dead). Create-on-empty
        // is only for a session with no pane id. Do not split a dead restored row.
        if (CountAlivePanes(snap) > 0 || TryFirstPaneId(snap) is not null)
            return snap;

        if (TryFirstWorkspaceId(snap) is { } wsId)
        {
            await client.CallAsync(
                    ProtocolMethods.PaneCreate,
                    new JsonObject { ["workspace_id"] = wsId },
                    ct)
                .ConfigureAwait(false);
        }
        else
        {
            await client.CallAsync(
                    ProtocolMethods.WorkspaceCreate,
                    new JsonObject
                    {
                        ["create_pane"] = true,
                    },
                    ct)
                .ConfigureAwait(false);
        }

        return await client.CallAsync(ProtocolMethods.SessionSnapshot, ct: ct)
            .ConfigureAwait(false);
    }

    internal static JsonObject BuildEventsSubscribeParams(
        IReadOnlyList<string> types,
        IReadOnlyList<ChannelCursor>? lastReceived,
        bool snapshotPaint)
    {
        ArgumentNullException.ThrowIfNull(types);
        var nodes = new JsonNode?[types.Count];
        for (var i = 0; i < types.Count; i++)
            nodes[i] = JsonValue.Create(types[i]);
        var fromSeq = 0L;
        var budget = 0;
        if (!snapshotPaint
            && lastReceived is not null
            && !AttachReconnectRules.RequiresSnapshot(lastReceived))
        {
            foreach (var cursor in lastReceived)
            {
                if (cursor.ChannelId == 0)
                    fromSeq = (long)cursor.LastReceivedSequence;
            }

            budget = 1000;
        }

        var parameters = new JsonObject
        {
            ["types"] = new JsonArray(nodes),
            ["live"] = true,
            ["replay_budget"] = budget,
        };
        // A fresh subscribe starts at the live edge. Only a resume sends a cursor,
        // so only a resume can get cursor_expired.
        if (budget > 0)
            parameters["from_seq"] = fromSeq;
        return parameters;
    }

    private void BeginReconnectSession(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.ReconnectPort = _reconnect;
        var capability = IssueJoinCapability();
        var attached = _reconnect.Attach(capability);
        if (!attached.Ok)
        {
            throw new InvalidOperationException(
                attached.Detail ?? "attach reconnect coordinator failed");
        }

        EnsureLocalFramedPair();
    }

    private JoinCapability IssueJoinCapability()
    {
        if (!JoinNonce.TryParse(Guid.NewGuid().ToString("N"), out var nonce))
            throw new InvalidOperationException("join nonce mint failed");
        var previous = _reconnect.Capability;
        var placement = previous?.PlacementId ?? ParseLocalPlacement();
        var device = previous?.DeviceId ?? ParseLocalDevice();
        var role = previous?.Role ?? JoinRole.Client;
        var now = _time.GetUtcNow();
        var issued = _capabilityIssuer.Issue(
            placement,
            device,
            role,
            nonce,
            now.AddMinutes(1),
            now);
        if (!issued.Ok || issued.Value is null)
        {
            throw new InvalidOperationException(
                issued.Detail ?? "join capability mint failed");
        }

        return issued.Value;
    }

    private static PlacementId ParseLocalPlacement()
    {
        if (!PlacementId.TryParse("plc_local1", out var placement))
            throw new InvalidOperationException("placement id mint failed");
        return placement;
    }

    private static DeviceId ParseLocalDevice()
    {
        if (!DeviceId.TryParse("dev_local1", out var device))
            throw new InvalidOperationException("device id mint failed");
        return device;
    }

    private void EnsureLocalFramedPair()
    {
        if (_framedClient is not null)
            return;

        var placement = _reconnect.Capability?.PlacementId ?? ParseLocalPlacement();
        var pair = ChannelFramedSession.Pair(placement);
        _framedClient = pair.Client;
        _framedMux = pair.Mux;
    }

    private AttachReconnectRequest? BuildReconnectRequest(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.ReconnectPort = _reconnect;
        LastReconnectRequest = null;
        LastReconnectOffer = null;
        LastFramedReconnectJson = null;
        if (_reconnect.State == AttachReconnectState.Connected)
        {
            var stalled = _reconnect.Stall(AttachReconnectLimits.StallWindow);
            if (!stalled.Ok)
                return null;
        }

        if (_reconnect.State is not AttachReconnectState.Stalled
            and not AttachReconnectState.Reconnecting)
        {
            return null;
        }

        IReadOnlyList<ChannelCursor> lastReceived;
        if (_framedClient is not null && _framedClient.LastReceived.Count > 0)
            lastReceived = _framedClient.LastReceived;
        else
            lastReceived = _reconnect.LastReceived;

        var previousAttempt = _reconnect.AttemptId;
        var capability = IssueJoinCapability();
        var request = new AttachReconnectRequest
        {
            PreviousAttemptId = previousAttempt,
            AttemptId = _reconnect.MintAttemptId(),
            Capability = capability,
            LastReceived = AttachReconnectRules.BindCursors(
                lastReceived,
                previousAttempt,
                capability),
        };
        LastReconnectRequest = request;
        LastFramedReconnectJson = AttachReconnectCodec.Write(request);
        return request;
    }

    private async Task<AttachReconnectOffer?> HandshakeReconnectAsync(
        AttachReconnectRequest request,
        CancellationToken ct)
    {
        EnsureLocalFramedPair();
        if (_framedClient is null)
            return null;

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<ConnectivityOutcome>? serveTask = null;
        if (_framedMux is not null)
        {
            var muxSession = _framedMux;
            var token = handshakeCts.Token;
            serveTask = Task.Run(
                () => FramedAttachReconnect.ServeAsync(muxSession, _reconnect, token).AsTask(),
                token);
        }

        var offeredTask = FramedAttachReconnect.RequestAsync(
                _framedClient,
                request,
                handshakeCts.Token)
            .AsTask();
        if (serveTask is not null)
        {
            var first = await Task.WhenAny(serveTask, offeredTask).ConfigureAwait(false);
            if (first == serveTask)
            {
                var servedEarly = await serveTask.ConfigureAwait(false);
                if (!servedEarly.Ok)
                    handshakeCts.Cancel();
            }
        }

        var offered = await offeredTask.ConfigureAwait(false);
        if (!offered.Ok)
            handshakeCts.Cancel();
        if (serveTask is not null)
        {
            try
            {
                var served = await serveTask.ConfigureAwait(false);
                if (!served.Ok && offered.Ok)
                    return null;
            }
            catch (OperationCanceledException)
            {
                if (offered.Ok)
                    return null;
            }
        }

        if (!offered.Ok || offered.Value is null)
            return null;

        LastReconnectOffer = offered.Value;
        return offered.Value;
    }

    private static void NoteReconnectEvent(AttachLiveState live, JsonElement ev)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.ReconnectPort is null)
            return;
        var seq = ReadEventSeq(ev);
        if (seq <= 0)
            return;
        live.ReconnectPort.NoteObserved(
            StreamFrame.Control(
                StreamDirection.MuxToClient,
                (ulong)seq,
                ReadOnlyMemory<byte>.Empty));
    }

    internal static async Task RefreshChromeAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct,
        bool observeVisible = true,
        bool failClosedOnTransportError = false,
        bool followMuxFocus = false,
        string? presentedTabId = null,
        string? presentedPaneId = null,
        bool tabEmptiedByRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        if (!live.ChromeEnabled)
        {
            await ConsumeHeldPendingObserveAsync(control, live, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            // session snapshot is the space list. Apply it before layout.export so a
            // closed tab cannot skip the sidebar rebuild.
            try
            {
                var session = await control.CallAsync(ProtocolMethods.SessionSnapshot, null, ct)
                    .ConfigureAwait(false);
                HydratePaneRightClick(live, session);
                NoteSnapshotGeometryOwners(live, session);
                ApplyPopupFromSnapshot(live, session);
                if (live.CubeCatalog is not null)
                {
                    var cubes = await live.CubeCatalog.LoadAsync(ct).ConfigureAwait(false);
                    live.Cubes = cubes.Items;
                    live.CubesState = cubes.State;
                }
                else if (live.Release.ContinuityEnabled)
                {
                    live.Cubes = [];
                    live.CubesState = SidebarCubeCatalogState.Ready;
                }
                else
                {
                    live.Cubes = [];
                    live.CubesState = SidebarCubeCatalogState.Ready;
                }

                BindConnectedPlacementIdentity(live);
                BindSelectedCubeIdentity(live);
                await RefreshLinkedPluginsAsync(control, live, ct).ConfigureAwait(false);
                RebuildSidebar(live, session);
                NoteSeenFromSnapshot(live, session);
                await FlushSeenReportAsync(control, live, ct).ConfigureAwait(false);
                AlignLiveFocusFromSnapshot(live, session);
                if (followMuxFocus)
                    PresentMuxTab(live, session, presentedTabId, presentedPaneId);
                FollowMuxFocusWhenTabHasNoLeaf(live, session, tabEmptiedByRefresh);
            }
            catch (ControlPlaneException ex)
            {
                if (failClosedOnTransportError)
                    throw;
                live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus(ex));
            }
            catch (ControlPlaneClientTimeoutException)
            {
                if (failClosedOnTransportError)
                    throw;
                live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus("compose timed out"));
            }
            catch (InvalidOperationException ex)
            {
                if (failClosedOnTransportError)
                    throw;
                live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus(ex));
            }

            // session snapshot. When that snapshot has no remaining tab or
            // pane, skip layout.export and compose the sidebar-only host
            // frame.
            if (!SnapshotAllowsLayoutExport(live))
            {
                PaintSidebarAfterExportFailure(live, tty);
                await ConsumeHeldPendingObserveAsync(control, live, ct).ConfigureAwait(false);
                return;
            }

            var exportParams = live.TabId is { } tab
                ? new JsonObject { ["tab_id"] = tab }
                : live.PaneId is { Length: > 0 } pane
                    ? new JsonObject { ["pane_id"] = pane }
                    : null;
            var exported = await control.CallAsync(ProtocolMethods.LayoutExport, exportParams, ct)
                .ConfigureAwait(false);
            var listed = await control.CallAsync(
                    ProtocolMethods.TabList,
                    live.WorkspaceId is { } ws ? new JsonObject { ["workspace_id"] = ws } : null,
                    ct)
                .ConfigureAwait(false);
            HydratePaneRightClick(live, exported);

            var tabs = ReadTabHits(listed, live.TabId, live.Ui.StatusIndicators);
            ReplaceTabHits(live, tabs);
            var cols = 80;
            var rows = 24;
            if (tty is not null && tty.TryGetSize(out var tc, out var tr))
            {
                cols = tc;
                rows = tr;
            }

            var root = TryLayoutRoot(exported);
            var zoomed = exported.ValueKind == JsonValueKind.Object
                && exported.TryGetProperty("zoomed", out var z)
                && z.ValueKind == JsonValueKind.True;
            var zoomedPane = TryJsonString(exported, "zoomed_pane_id")
                ?? (zoomed ? TryJsonString(exported, "focused_pane_id") : null);
            var focused = TryJsonString(exported, "focused_pane_id") ?? live.PaneId;
            CloseSwitcherIfWide(live, cols);
            var nextChrome = ComputeLiveChrome(
                live,
                cols,
                rows,
                root,
                zoomed,
                zoomedPane,
                focused);
            var seed = new ChromeComputeSeed(root, zoomed, zoomedPane, focused, cols, rows);
            live.WithPaint(() =>
            {
                if (!live.ChromeEnabled)
                    return;
                live.TabHits = tabs;
                live.ChromeSeed = seed;
                live.Chrome = nextChrome;
            });
            if (!live.ChromeEnabled)
            {
                await ConsumeHeldPendingObserveAsync(control, live, ct).ConfigureAwait(false);
                return;
            }
            live.TabBarCommands.KickRefresh(
                live.Ui.TabBarRight,
                live.Time,
                ct,
                () => ApplyTabBarRightPaint(live, tty));
        }
        catch (ControlPlaneException ex)
        {
            if (failClosedOnTransportError)
                throw;
            live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus(ex));
            PaintSidebarAfterExportFailure(live, tty);
            await ConsumeHeldPendingObserveAsync(control, live, ct).ConfigureAwait(false);
            return;
        }
        catch (ControlPlaneClientTimeoutException)
        {
            if (failClosedOnTransportError)
                throw;
            live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus("compose timed out"));
            PaintSidebarAfterExportFailure(live, tty);
            await ConsumeHeldPendingObserveAsync(control, live, ct).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException ex)
        {
            if (failClosedOnTransportError)
                throw;
            live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus(ex));
            PaintSidebarAfterExportFailure(live, tty);
            await ConsumeHeldPendingObserveAsync(control, live, ct).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (failClosedOnTransportError)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (failClosedOnTransportError)
                throw;
            live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus(ex.Message));
            PaintSidebarAfterExportFailure(live, tty);
            await ConsumeHeldPendingObserveAsync(control, live, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await ResizeVisiblePanesAsync(control, live, ct).ConfigureAwait(false);
        }
        catch (ControlPlaneException ex)
        {
            if (failClosedOnTransportError)
                throw;
            live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus(ex));
        }
        catch (ControlPlaneClientTimeoutException)
        {
            if (failClosedOnTransportError)
                throw;
            live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus("compose timed out"));
        }
        catch (InvalidOperationException ex) when (IsZeroPaneContent(ex))
        {
            AttachProcessLog.Observe(
                live.ProcessLog,
                live.SessionName,
                live.AttachClientId,
                live.PaneId,
                ProcessLogEvents.OutcomeDropped,
                "pane content has no size");
            live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus(ex));
        }
        catch (InvalidOperationException ex)
        {
            AttachProcessLog.LoopFault(
                live.ProcessLog,
                live.SessionName,
                live.AttachClientId,
                "pane.resize",
                ex);
            throw;
        }

        // change workspace state, then stamp the active runtime.
        await ConsumeHeldPendingObserveAsync(control, live, ct).ConfigureAwait(false);

        if (observeVisible)
        {
            try
            {
                await ObserveVisiblePanesAsync(live.RenderPort ?? control, live, ct).ConfigureAwait(false);
            }
            catch (ControlPlaneException ex)
            {
                NoteObserveFault(live, ex);
                live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus(ex));
            }
            catch (InvalidOperationException ex)
            {
                NoteObserveFault(live, ex);
                live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus(ex));
            }
        }

        if (live.PopupOpen)
        {
            await ResizeOpenPopupAsync(control, live, tty, ct).ConfigureAwait(false);
        }

        // Same-set layout changes do not observe or snapshot. Blit stored frames.
        if (tty is not null)
            PaintStoredChromeFrames(tty, live);
    }

    private static void PaintSidebarAfterExportFailure(AttachLiveState live, UnixRawTerminal? tty)
    {
        if (!live.ChromeEnabled)
            return;
        if (live.ChromeSeed is not null)
        {
            RecomputeChromeFromSeed(live, tty);
            return;
        }

        var cols = 80;
        var rows = 24;
        if (tty is not null && tty.TryGetSize(out var tc, out var tr))
        {
            cols = tc;
            rows = tr;
        }

        CloseSwitcherIfWide(live, cols);
        var nextChrome = ComputeLiveChrome(
            live,
            cols,
            rows,
            root: null,
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: live.PaneId);
        live.WithPaint(() =>
        {
            if (!live.ChromeEnabled)
                return;
            live.Chrome = nextChrome;
        });
        if (tty is not null)
            PaintStoredChromeFrames(tty, live);
    }

    internal static void RecomputeChromeFromSeed(AttachLiveState live, UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.ChromeEnabled)
            return;
        if (live.ChromeSeed is not { } seed)
            return;

        var cols = seed.Cols;
        var rows = seed.Rows;
        if (tty is not null && tty.TryGetSize(out var tc, out var tr))
        {
            cols = tc;
            rows = tr;
        }

        CloseSwitcherIfWide(live, cols);
        var resized = live.Chrome is { } previous && (previous.Cols != cols || previous.Rows != rows);
        var nextChrome = ComputeLiveChrome(
            live,
            cols,
            rows,
            seed.Root,
            seed.Zoomed,
            seed.ZoomedPaneId,
            seed.FocusedPaneId);
        live.WithPaint(() =>
        {
            if (!live.ChromeEnabled)
                return;
            live.ChromeSeed = seed with { Cols = cols, Rows = rows };
            live.Chrome = nextChrome;
        });
        if (resized)
            live.InvalidateChrome();
    }

    internal static bool IsLiveNarrow(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var cols = live.Chrome?.Cols ?? live.ChromeSeed?.Cols ?? 80;
        return NarrowLayout.IsNarrow(cols, live.Ui.MobileWidthThreshold);
    }

    internal static void CloseSwitcherIfWide(AttachLiveState live, int cols)
    {
        ArgumentNullException.ThrowIfNull(live);
        var narrow = NarrowLayout.IsNarrow(cols, live.Ui.MobileWidthThreshold);
        live.Engine.NarrowLayout = narrow;
        if (narrow)
            return;
        if (live.Engine.Mode is AttachClientMode.MobileSwitcher)
        {
            _ = live.Engine.LeaveMobileSwitcher();
            live.Engine.CommitPaintMode();
        }
        live.SwitcherScroll = 0;
        live.SwitcherSelected = 0;
    }

    internal static LayoutChromeGeometry ComputeLiveChrome(
        AttachLiveState live,
        int cols,
        int rows,
        LayoutNodeDto? root,
        bool zoomed,
        string? zoomedPaneId,
        string? focusedPaneId,
        int? sidebarWidth = null,
        float? sidebarSectionSplit = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.Engine.NarrowLayout = NarrowLayout.IsNarrow(cols, live.Ui.MobileWidthThreshold);
        ClampSidebarScrolls(live, rows);
        var layoutMode = LayoutPaintMode(live);
        var switcherOpen = layoutMode is AttachClientMode.MobileSwitcher;
        var geo = LayoutChromeGeometry.Compute(
            cols,
            rows,
            root,
            zoomed,
            zoomedPaneId,
            focusedPaneId,
            live.Ui,
            Math.Max(1, live.TabHits.Count),
            layoutMode,
            time: live.Time,
            commandOutputs: live.TabBarCommands.Snapshot(),
            sidebarOpen: live.SidebarOpen,
            sidebarWidth: sidebarWidth ?? live.SidebarWidth,
            toasts: live.Toasts,
            sidebarCollapsed: live.SidebarCollapsed,
            switcherOpen: switcherOpen,
            switcherScroll: live.SwitcherScroll,
            workspaceLabel: WorkspaceLabelOf(live),
            tabLabel: TabLabelOf(live),
            activeTabIndex: ActiveTabIndexOf(live),
            agentSummary: AgentNameOf(live),
            focusedWorkspaceId: live.WorkspaceId,
            focusedTabId: live.TabId,
            sessionName: live.SessionName,
            agentState: AgentStateOf(live),
            sidebarFrame: live.SidebarFrame,
            spacesScroll: live.SidebarSpacesScroll,
            agentsScroll: live.SidebarAgentsScroll,
            resourceScrolls: live.SidebarResourceScrolls,
            sidebarSectionSplit: sidebarSectionSplit ?? live.SidebarSectionSplit,
            revealFocused: live.RevealFocusedTab,
            lastTabBarWidth: live.LastTabBarWidth,
            tabSpecs: live.TabHits.Count > 0 ? live.TabHits : null,
            overflowOffset: live.TabOverflowOffset,
            singlePaneFrame: live.Theme.Palette.Chrome.SinglePaneFrame);
        geo = BindLivePaneChrome(geo, live);
        if (live.Theme.Palette.Chrome.DesktopStatusBar)
            geo = ReserveDesktopStatusBar(geo);
        if (live.PopupOpen)
        {
            UnlockPopupGeometryIfContentChanged(live, geo.Content);
            var resolved = ResolveLivePopupGeometry(live, geo.Content);
            geo = geo with
            {
                PopupFrame = resolved is null
                    ? null
                    : PopupPainter.FrameFromGeometry(geo.Content, resolved),
            };
        }
        else
        {
            geo = geo with { PopupFrame = null };
        }

        live.TabOverflowOffset = geo.TabBar.OverflowOffset;
        live.RevealFocusedTab = false;
        live.LastTabBarWidth = geo.TabBarVisible
            ? geo.TabRow?.Cols ?? geo.NamedSurfaces.Main.Cols
            : 0;
        geo = geo with { HasEndpointError = HasStatusRow(live) };

        if (switcherOpen && geo.MobileSwitcher is { Open: true } built)
        {
            var marked = built.WithSelectedIndex(live.SwitcherSelected);
            live.SwitcherScroll = marked.Scroll;
            live.SwitcherSelected = marked.SelectedIndex;
            return geo with { MobileSwitcher = marked };
        }

        return geo;
    }

    internal static void ReplaceTabHits(AttachLiveState live, IReadOnlyList<TabBarTabSpec> tabs)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(tabs);
        if (TabHitsChanged(live.TabHits, tabs))
            live.RevealFocusedTab = true;
        live.TabHits = tabs;
    }

    private static bool TabHitsChanged(
        IReadOnlyList<TabBarTabSpec> current,
        IReadOnlyList<TabBarTabSpec> next)
    {
        if (current.Count != next.Count)
            return true;
        for (var i = 0; i < current.Count; i++)
        {
            // tab id, workspace, label, or zoomed changes.
            if (!string.Equals(current[i].Id, next[i].Id, StringComparison.Ordinal)
                || current[i].Active != next[i].Active
                || current[i].Zoomed != next[i].Zoomed
                || !string.Equals(current[i].Label, next[i].Label, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }



    /// <summary>
    /// Turbo Vision panes are white-on-blue. Snapshot default cells follow the
    /// host terminal; recolor defaults so the desktop color shows through.
    /// </summary>
    internal static void PaintDesktopPaneDefaults(HostFrame host, CellRect box, ThemePalette theme)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(theme);
        if (box.Cols < 1 || box.Rows < 1)
            return;
        if (!theme.PanelBg.IsRgb || !theme.Text.IsRgb)
            return;

        var style = new AssembledStyle(
            VtColorPack.FromRgb(theme.Text.R, theme.Text.G, theme.Text.B),
            VtColorPack.FromRgb(theme.PanelBg.R, theme.PanelBg.G, theme.PanelBg.B),
            Bold: false,
            Dim: false,
            Italic: false,
            Underline: false,
            Inverse: false,
            Invisible: false,
            Strikethrough: false);
        for (var r = 0; r < box.Rows; r++)
        {
            for (var c = 0; c < box.Cols; c++)
            {
                var cell = host.CellAt(box.Col + c, box.Row + r);
                if (!cell.Style.IsDefault)
                    continue;
                host.Stamp(
                    box.Col + c,
                    box.Row + r,
                    new AssembledCell(cell.Text, cell.Width, cell.IsContinuation, style));
            }
        }
    }

    internal static LayoutChromeGeometry ReserveDesktopStatusBar(LayoutChromeGeometry geo)
    {
        ArgumentNullException.ThrowIfNull(geo);
        if (geo.Rows < 3 || geo.StatusRow is { Rows: > 0 })
            return geo;

        var status = new CellRect(0, geo.Rows - 1, geo.Cols, 1);
        var panes = new List<ChromePaneFrame>(geo.Panes.Count);
        foreach (var pane in geo.Panes)
        {
            var frame = pane.Frame;
            var content = pane.Content;
            if (frame.EndRow > status.Row)
            {
                var rows = Math.Max(0, status.Row - frame.Row);
                frame = new CellRect(frame.Col, frame.Row, frame.Cols, rows);
            }
            if (content.EndRow > status.Row)
            {
                var rows = Math.Max(0, status.Row - content.Row);
                content = new CellRect(content.Col, content.Row, content.Cols, rows);
            }
            panes.Add(pane with { Frame = frame, Content = content });
        }

        var contentRect = geo.Content;
        if (contentRect.EndRow > status.Row)
        {
            contentRect = new CellRect(
                contentRect.Col,
                contentRect.Row,
                contentRect.Cols,
                Math.Max(0, status.Row - contentRect.Row));
        }

        return geo with
        {
            Panes = panes,
            Content = contentRect,
            StatusRow = status,
        };
    }

    internal static LayoutChromeGeometry BindLivePaneChrome(
        LayoutChromeGeometry geometry,
        AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(live);
        if (geometry.Panes.Count == 0)
            return geometry;

        var states = new Dictionary<string, PaneChromeScrollState>(StringComparer.Ordinal);
        foreach (var pane in geometry.Panes)
            states[pane.PaneId] = LivePaneScrollState(live, pane.PaneId);

        return PaneChromeGutter.Bind(geometry, live.Ui, states);
    }

    internal static bool SyncPaneChromeGeometry(AttachLiveState live, UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.ChromeEnabled || live.ChromeSeed is null)
            return false;

        var before = SnapshotPaneContentSizes(live.Chrome);
        RecomputeChromeFromSeed(live, tty);
        var changed = !SamePaneContentSizes(before, live.Chrome);
        if (changed)
            live.PendingPaneChromeResize = true;
        return changed;
    }

    /// <summary>
    /// Tells the mux which panes the person has seen: one call for the whole batch, and no call when no pane is pending.
    /// A failed call keeps the panes for the next chance.
    /// </summary>
    internal static async Task FlushSeenReportAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        if (!live.SeenReports.HasPending)
            return;
        var batch = live.SeenReports.Take();
        if (batch.Count == 0)
            return;
        var ids = new JsonArray();
        foreach (var id in batch)
            ids.Add((JsonNode)JsonValue.Create(id));
        try
        {
            await control.CallAsync(ProtocolMethods.PaneMarkSeen, new JsonObject { ["pane_ids"] = ids }, ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
            live.SeenReports.RestoreLastBatch();
        }
        catch (InvalidOperationException)
        {
            live.SeenReports.RestoreLastBatch();
        }
    }

    /// <summary>The snapshot lists the panes. A done or error that is not seen on the tab in view is pending.</summary>
    internal static void NoteSeenFromSnapshot(AttachLiveState live, JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("panes", out var panes)
            || panes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var pane in panes.EnumerateArray())
        {
            if (TryJsonString(pane, "pane_id") is not { Length: > 0 } paneId)
                continue;
            var seen = !pane.TryGetProperty("seen", out var seenEl) || seenEl.ValueKind != JsonValueKind.False;
            live.SeenReports.NotePane(
                paneId,
                TryJsonString(pane, "tab_id"),
                TryJsonString(pane, "state"),
                seen,
                live.Notifications.FocusedTabId);
        }
    }

    /// <summary>
    /// Starts a pending pane resize off the render loop and returns. The
    /// render loop does not wait for the replies of <c>pane.resize</c>, or for
    /// <see cref="AttachLiveState.PaneChromeResizeGate"/>, which a host resize
    /// holds across its own <c>pane.resize</c> calls. On a cube each reply is
    /// one round trip. While the loop waited, every received frame waited too.
    /// <para>
    /// The flush runs on the caller up to its first wait, so it marks the
    /// resized panes (<see cref="AwaitsOwnPaneSize"/>) before the loop takes
    /// the next frame. One flush of this lane runs at a time. A resize that
    /// is marked while it runs goes when it ends. A failed flush sets the
    /// status. It does not end the render loop.
    /// </para>
    /// </summary>
    internal static void StartPaneChromeResizeOffLoop(
        AttachLiveState live,
        ControlPlaneClient control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.PendingPaneChromeResize || ct.IsCancellationRequested)
            return;
        if (Interlocked.Exchange(ref live.PaneChromeResizeOffLoop, 1) != 0)
            return;
        live.PaneChromeResizeOffLoopTask = RunPaneChromeResizeOffLoopAsync(live, control, ct);
    }

    private static async Task RunPaneChromeResizeOffLoopAsync(
        AttachLiveState live,
        ControlPlaneClient control,
        CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await FlushPaneChromeResizeAsync(
                        new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control),
                        live,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (ControlPlaneException ex)
            {
                live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus(ex));
            }
            catch (ControlPlaneClientTimeoutException)
            {
                live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus("compose timed out"));
            }
            catch (InvalidOperationException ex)
            {
                live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus(ex));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The link is gone. The reader sees the loss and recovers it.
            }

            Volatile.Write(ref live.PaneChromeResizeOffLoop, 0);
            // A resize that was marked after the flush took its request goes
            // now, unless it waits for its backoff. The heartbeat sends that one.
            if (ct.IsCancellationRequested
                || !live.PendingPaneChromeResize
                || live.Time.GetUtcNow() < live.PaneChromeResizeRetryAt
                || Interlocked.Exchange(ref live.PaneChromeResizeOffLoop, 1) != 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Send a pending pane resize. The render lane and the control heartbeat both
    /// call this. One gate runs one flush at a time, so an older resize cannot
    /// finish after a newer one.
    /// </summary>
    internal static async Task FlushPaneChromeResizeAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        if (!live.PendingPaneChromeResize)
            return;
        await live.PaneChromeResizeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FlushPaneChromeResizeCoreAsync(control, live, ct).ConfigureAwait(false);
        }
        finally
        {
            live.PaneChromeResizeGate.Release();
        }
    }

    private static async Task FlushPaneChromeResizeCoreAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct)
    {
        if (!live.PendingPaneChromeResize)
            return;
        // After a lease refusal, wait for the backoff. The request stays pending.
        if (live.Time.GetUtcNow() < live.PaneChromeResizeRetryAt)
            return;
        live.PendingPaneChromeResize = false;
        try
        {
            // The flush already holds the gate.
            await ResizeVisiblePanesCoreAsync(control, live, ct).ConfigureAwait(false);
            live.PaneChromeResizeRefusals = 0;
            live.PaneChromeResizeRetryAt = DateTimeOffset.MinValue;
        }
        catch (InvalidOperationException ex) when (IsZeroPaneContent(ex))
        {
            if (string.IsNullOrWhiteSpace(live.StatusError))
                live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus("pane content has no size"));
        }
        catch (ControlPlaneException ex)
        {
            // The mux refused the resize. Keep the request and send it again
            // after a backoff (1 s, 2 s, 4 s, then 8 s). A lease refusal is
            // routine, so do not throw: it must not close the render reader.
            // Other refusals still propagate to the caller.
            live.PendingPaneChromeResize = true;
            var refusals = Math.Min(live.PaneChromeResizeRefusals, 3);
            live.PaneChromeResizeRefusals++;
            live.PaneChromeResizeRetryAt = live.Time.GetUtcNow() + TimeSpan.FromSeconds(1 << refusals);
            if (!IsLeaseRefusal(ex))
                throw;
            AttachProcessLog.Observe(
                live.ProcessLog,
                live.SessionName,
                live.AttachClientId,
                live.PaneId,
                ProcessLogEvents.OutcomeDropped,
                "resize_refused " + ex.ErrorCode);
        }
        catch (ControlPlaneClientTimeoutException)
        {
            // No reply in time. Keep the request so that the heartbeat sends
            // it again after the same backoff as a refusal.
            live.PendingPaneChromeResize = true;
            var refusals = Math.Min(live.PaneChromeResizeRefusals, 3);
            live.PaneChromeResizeRefusals++;
            live.PaneChromeResizeRetryAt = live.Time.GetUtcNow() + TimeSpan.FromSeconds(1 << refusals);
            throw;
        }
        catch (InvalidOperationException ex)
        {
            AttachProcessLog.LoopFault(
                live.ProcessLog,
                live.SessionName,
                live.AttachClientId,
                "pane.resize",
                ex);
            throw;
        }
    }

    private static bool IsLeaseRefusal(ControlPlaneException ex) =>
        ex.ErrorCode is ProtocolErrors.LeaseRequired or ProtocolErrors.LeaseExpired;

    internal static bool IsZeroPaneContent(InvalidOperationException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Message.Contains("pane content has no size", StringComparison.Ordinal);
    }

    private static Dictionary<string, (int Cols, int Rows)> SnapshotPaneContentSizes(
        LayoutChromeGeometry? chrome)
    {
        var sizes = new Dictionary<string, (int Cols, int Rows)>(StringComparer.Ordinal);
        if (chrome is null)
            return sizes;
        foreach (var pane in chrome.Panes)
            sizes[pane.PaneId] = (pane.Content.Cols, pane.Content.Rows);
        return sizes;
    }

    private static bool SamePaneContentSizes(
        Dictionary<string, (int Cols, int Rows)> before,
        LayoutChromeGeometry? after)
    {
        if (after is null)
            return before.Count == 0;
        if (before.Count != after.Panes.Count)
            return false;
        foreach (var pane in after.Panes)
        {
            if (!before.TryGetValue(pane.PaneId, out var size)
                || size.Cols != pane.Content.Cols
                || size.Rows != pane.Content.Rows)
            {
                return false;
            }
        }

        return true;
    }

    internal static string? WorkspaceLabelOf(AttachLiveState live)
    {
        if (live.SidebarInput is { } input)
        {
            foreach (var workspace in input.Workspaces)
            {
                if (string.Equals(workspace.Id, live.WorkspaceId, StringComparison.Ordinal))
                    return workspace.Label;
            }
        }

        var spaces = live.SidebarFrame?.Pane(SidebarPaneSlot.Spaces);
        if (spaces is null)
            return null;
        foreach (var row in spaces.Rows)
        {
            if (row.Kind is SidebarRowKind.Workspace
                && row.Selected
                && row.CardRowIndex == 0
                && !string.IsNullOrWhiteSpace(row.Label))
            {
                return row.Label;
            }
        }

        return null;
    }

    internal static string? TabLabelOf(AttachLiveState live)
    {
        foreach (var tab in live.TabHits)
        {
            if (tab.Active || string.Equals(tab.Id, live.TabId, StringComparison.Ordinal))
                return tab.Label;
        }

        return live.TabId;
    }

    internal static int ActiveTabIndexOf(AttachLiveState live)
    {
        for (var i = 0; i < live.TabHits.Count; i++)
        {
            if (live.TabHits[i].Active
                || string.Equals(live.TabHits[i].Id, live.TabId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    internal static string? AgentSummaryOf(AttachLiveState live) => AgentNameOf(live);

    internal static string? AgentStateOf(AttachLiveState live)
    {
        if (live.SidebarInput is { } input)
        {
            foreach (var pane in input.Panes)
            {
                if (string.Equals(pane.Id, live.PaneId, StringComparison.Ordinal))
                    return pane.State;
            }
        }

        return null;
    }

    internal static string? AgentNameOf(AttachLiveState live) =>
        DetectedAgentOf(live, live.PaneId);

    internal static string? DetectedAgentOf(AttachLiveState live, string? paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId) || live.SidebarInput is not { } input)
            return null;
        foreach (var pane in input.Panes)
        {
            if (!string.Equals(pane.Id, paneId, StringComparison.Ordinal))
                continue;
            return string.IsNullOrWhiteSpace(pane.Agent) ? null : pane.Agent.Trim();
        }

        return null;
    }

    internal static string? PaneLabelOf(AttachLiveState live)
    {
        if (live.Chrome is { } chrome)
        {
            foreach (var pane in chrome.Panes)
            {
                if (string.Equals(pane.PaneId, live.PaneId, StringComparison.Ordinal))
                    return string.IsNullOrEmpty(pane.Label) ? pane.PaneId : pane.Label;
            }
        }

        return live.PaneId;
    }

    internal static string? TerminalTitleOf(AttachLiveState live)
    {
        if (live.SidebarInput is { } input)
        {
            foreach (var pane in input.Panes)
            {
                if (string.Equals(pane.Id, live.PaneId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(pane.TerminalTitle))
                {
                    return pane.TerminalTitle;
                }
            }
        }

        return live.TerminalTitle;
    }

    internal static StatusChipComposeInput ChipInputOf(AttachLiveState live, int cols) =>
        new()
        {
            SessionName = live.SessionName,
            WorkspaceLabel = WorkspaceLabelOf(live),
            PaneLabel = PaneLabelOf(live),
            PaneId = live.PaneId,
            AgentName = AgentNameOf(live),
            AgentState = AgentStateOf(live),
            StatusIndicators = live.Ui.StatusIndicators,
            Extras = StatusResourceChipComposer.Compose(
                live.Ui.TabBarRight,
                PluginResourcesOf(live)),
            Error = live.StatusError,
            MaxCols = cols,
        };

    internal static IReadOnlyList<PluginResourceDto> PluginResourcesOf(AttachLiveState live)
    {
        if (live.PluginResources.Count > 0)
            return live.PluginResources;
        return ParsePluginResources(live.LastSnapshot);
    }

    internal static IReadOnlyList<PluginResourceDto> ParsePluginResources(JsonElement? snapshot)
    {
        if (snapshot is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("resources", out var resources)
            || resources.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<PluginResourceDto>(resources.GetArrayLength());
        foreach (var item in resources.EnumerateArray())
        {
            var parsed = JsonSerializer.Deserialize(item.GetRawText(), ProtocolJsonContext.Default.PluginResourceDto);
            if (parsed is not null)
                list.Add(parsed);
        }

        return list;
    }

    internal static WindowTitleValues TitleValuesOf(AttachLiveState live) =>
        new()
        {
            Hostname = live.Hostname ?? Environment.MachineName,
            Workspace = WorkspaceLabelOf(live) ?? "",
            Tab = TabLabelOf(live) ?? "",
            Pane = PaneLabelOf(live) ?? live.PaneId,
            TerminalTitle = TerminalTitleOf(live) ?? "",
        };

    internal static string? ReadWindowTitleOverride(JsonElement snap)
    {
        if (snap.ValueKind != JsonValueKind.Object)
            return null;
        if (!snap.TryGetProperty("window_title_override", out var title)
            || title.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = title.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static async Task LeaveSwitcherAfterApplyAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        if (live.Engine.Mode is not AttachClientMode.MobileSwitcher)
            return;
        await ApplyModeEventsAsync(
                live.Engine.LeaveMobileSwitcher(),
                live,
                control,
                tty,
                linked: null,
                ct)
            .ConfigureAwait(false);
    }

    private static void ApplySwitcherSelectionDelta(
        AttachLiveState live,
        int delta,
        UnixRawTerminal? tty)
    {
        if (live.Engine.Mode is not AttachClientMode.MobileSwitcher)
            return;
        if (live.SwitcherSelected < 0
            && live.Chrome?.MobileSwitcher is { Open: true } current)
        {
            live.SwitcherSelected = current.WithSelectedIndex(-1).SelectedIndex;
        }

        live.SwitcherSelected += delta;
        if (live.ChromeSeed is not null)
            RecomputeChromeFromSeed(live, tty);
        else if (live.Chrome?.MobileSwitcher is { Open: true } sw)
        {
            var marked = sw.WithSelectedIndex(live.SwitcherSelected);
            live.SwitcherSelected = marked.SelectedIndex;
            live.SwitcherScroll = marked.Scroll;
            live.Chrome = live.Chrome with { MobileSwitcher = marked };
        }

        if (live.Chrome?.MobileSwitcher is { Open: true } built)
            live.SwitcherScroll = Math.Clamp(built.Scroll, 0, built.MaxScroll);
    }

    private static MobileSwitcherRow? SelectedSwitcherRow(MobileSwitcherModel switcher, int selected)
    {
        var marked = switcher.WithSelectedIndex(selected);
        foreach (var row in marked.Rows)
        {
            if (row.Selected && row.Hit)
                return row;
        }

        return marked.SelectedRow();
    }

    internal static async Task RefreshChromeForPaintModeAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        if (!live.ChromeEnabled)
            return;
        if (live.ChromeSeed is null)
        {
            await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
            return;
        }

        RecomputeChromeFromSeed(live, tty);

        try
        {
            await ResizeVisiblePanesAsync(control, live, ct).ConfigureAwait(false);
        }
        catch (ControlPlaneException ex)
        {
            live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus(ex));
        }
        catch (InvalidOperationException ex)
        {
            live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus(ex));
        }

        SyncChromePaintMode(live);
    }

    internal static AttachClientMode LayoutPaintMode(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        // Destination Mode drives chrome while PaintMode stays on the overlay
        // until restore onAccepted. Prefix+Copy keeps Copy paint.
        if (live.Engine.Mode != live.Engine.PaintMode
            && live.Engine.Mode is not AttachClientMode.Prefix)
        {
            return live.Engine.Mode;
        }

        return live.Engine.PaintMode;
    }

    internal static void SyncChromePaintMode(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Chrome is not { } chrome)
            return;
        var dest = LayoutPaintMode(live);
        if (chrome.PaintMode == dest)
            return;
        live.Chrome = chrome with { PaintMode = dest };
    }

    /// <summary>
    /// True when the mode bar row shows an error or a neutral notice.
    /// Use this for paint and layout only. Input and outcome checks use
    /// <see cref="AttachLiveState.StatusError"/>.
    /// </summary>
    internal static bool HasStatusRow(AttachLiveState live) =>
        !string.IsNullOrWhiteSpace(live.StatusError)
        || !string.IsNullOrWhiteSpace(live.StatusNotice);

    internal static LayoutChromeGeometry? ChromeForHitTest(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Chrome is not { } chrome)
            return null;
        var mode = LayoutPaintMode(live);
        var hasError = HasStatusRow(live);
        if (chrome.PaintMode == mode && chrome.HasEndpointError == hasError)
            return chrome;
        return chrome with { PaintMode = mode, HasEndpointError = hasError };
    }

    internal static bool IsSamePaneFocusOnly(ChromeHitApplyResult plan, AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(plan.FocusPaneId)
            || !string.Equals(plan.FocusPaneId, live.PaneId, StringComparison.Ordinal))
        {
            return false;
        }

        if (plan.Request is not null
            || plan.Detach
            || plan.OpenGlobalMenu
            || plan.OpenWhatsNew
            || plan.OpenMobileSwitcher
            || plan.CloseMobileSwitcher
            || !string.IsNullOrWhiteSpace(plan.FocusWorkspaceId)
            || !string.IsNullOrWhiteSpace(plan.SidebarSectionId)
            || !string.IsNullOrWhiteSpace(plan.SidebarTreeId)
            || live.Engine.Mode is not AttachClientMode.Terminal)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(plan.FocusTabId)
            && !string.Equals(plan.FocusTabId, live.TabId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static void ApplyLocalMenuMode(AttachLiveState live, IReadOnlyList<KeyEngineEvent> events)
    {
        foreach (var ev in events)
        {
            ApplyPendingModeEvent(live, ev);
            if (ev.Kind is KeyEngineEventKind.EnterMode)
                live.Engine.CommitPaintMode();
        }
    }

    internal static async Task ApplyChromeHitAsync(
        ChromeHit hit,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        control = LiveControlPort(live, control);
        var plan = ChromeHitApply.Apply(
            hit,
            live.TabOverflowOffset,
            live.Chrome?.TabBar.MaxOverflowOffset ?? 0);
        live.TabOverflowOffset = plan.TabOverflowOffset;

        if (plan.CloseMobileSwitcher && live.Engine.Mode is AttachClientMode.MobileSwitcher)
        {
            await ApplyModeEventsAsync(
                    live.Engine.LeaveMobileSwitcher(),
                    live,
                    control,
                    tty,
                    linked: null,
                    ct)
                .ConfigureAwait(false);
        }

        if (plan.OpenMobileSwitcher)
        {
            if (live.Engine.Mode is AttachClientMode.MobileSwitcher)
            {
                await ApplyModeEventsAsync(
                        live.Engine.LeaveMobileSwitcher(),
                        live,
                        control,
                        tty,
                        linked: null,
                        ct)
                    .ConfigureAwait(false);
            }
            else
            {
                live.SwitcherScroll = 0;
                await ApplyModeEventsAsync(
                        live.Engine.EnterMobileSwitcher(),
                        live,
                        control,
                        tty,
                        linked: null,
                        ct)
                    .ConfigureAwait(false);
            }

            if (plan.Request is null
                && string.IsNullOrWhiteSpace(plan.FocusPaneId)
                && string.IsNullOrWhiteSpace(plan.FocusWorkspaceId)
                && string.IsNullOrWhiteSpace(plan.FocusTabId)
                && !plan.OpenGlobalMenu
                && !plan.OpenWhatsNew
                && !plan.OpenAddCube
                && !plan.OpenShareMux
                && !plan.OpenUpdateNotice
                && !plan.Detach)
            {
                if (plan.RebuildChrome)
                    await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(plan.ConnectPlacementId))
        {
            await ApplyCubesConnectAsync(
                    new MouseEngineResult(
                        MouseCommandKind.ApplyMenu,
                        PlacementId: plan.ConnectPlacementId),
                    live,
                    LiveControlPort(live, control),
                    tty,
                    ct)
                .ConfigureAwait(false);
            // Hypa session.snapshot does not carry the pane layout tree, so
            // the active endpoint still needs layout.export + tab.list.
            // Dest prep in flight is not "prep finished". Skip source chrome
            // refresh so dest click does not hold controlGate on layout.export.
            // Deny drain paints via PresentConnectFailure. Install sets
            // PendingChromeRefresh.
            var destPrepInFlight = false;
            lock (live.ActivationGate)
                destPrepInFlight = live.DestConnectAttempt is not null;
            if (live.PendingActivation is null && !destPrepInFlight)
            {
                await RefreshChromeAsync(
                        LiveControlPort(live, control),
                        live,
                        tty,
                        ct)
                    .ConfigureAwait(false);
                live.PendingChromeRefresh = false;
            }

            return;
        }

        if (plan.ToggleAgentSort)
        {
            var nextSort = live.Ui.AgentPanelSort is AgentPanelSort.Priority
                ? AgentPanelSort.Spaces
                : AgentPanelSort.Priority;
            live.Ui = live.Ui with { AgentPanelSort = nextSort };
            live.Dispatcher.AgentPanelSort = nextSort;
            if (live.LastSnapshot is { } sortSnap)
                RebuildSidebar(live, sortSnap);
            if (plan.RebuildChrome)
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
            return;
        }

        if (plan.OpenWhatsNew)
        {
            if (TryEnterReleaseNotes(live) && tty is not null)
                PaintChrome(tty, live);
            return;
        }

        if (plan.OpenAddCube)
        {
            OpenAddCubeDialog(live);
            await ReportAttachClientModeAsync(control, live, ct).ConfigureAwait(false);
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        if (plan.OpenShareMux)
        {
            OpenShareMuxDialog(live);
            await ReportAttachClientModeAsync(control, live, ct).ConfigureAwait(false);
            if (tty is not null)
                PaintChrome(tty, live);
            await RefreshShareFromMuxAsync(live, tty, ct).ConfigureAwait(false);
            return;
        }

        if (plan.OpenUpdateNotice)
        {
            await PromptMuxRestartAsync(live, control, tty, ct).ConfigureAwait(false);
            return;
        }

        if (KeyEngine.HoldsPromptTarget(live.Engine.Mode) && IsFocusOnlyChromePlan(plan))
            return;

        if (live.Engine.Mode is AttachClientMode.Settings)
            return;

        if (KeyEngine.IsDismissibleOverlay(live.Engine.Mode))
        {
            await DismissOverlayForMouseAsync(live, control, tty, linked: null, ct)
                .ConfigureAwait(false);
        }

        if (plan.OpenGlobalMenu)
        {
            await ApplyModeEventsAsync(
                    live.Engine.EnterGlobalMenu(),
                    live,
                    control,
                    tty,
                    linked: null,
                    ct)
                .ConfigureAwait(false);
            var geo = live.Chrome;
            var menuCol = geo?.Sidebar?.Col ?? 0;
            var menuRow = 1;
            if (geo is not null && geo.TryGlobalMenuAnchor(out var anchorCol, out var anchorRow))
            {
                menuCol = anchorCol;
                menuRow = anchorRow;
            }

            live.MouseMenu = ContextMenuModel.ForGlobal(
                menuCol,
                menuRow,
                geo?.Cols ?? 80,
                geo?.Rows ?? 24,
                live.LinkedPlugins);
            live.Mouse.AdoptOpenMenu(live.MouseMenu);
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        if (!string.IsNullOrWhiteSpace(plan.SidebarSectionId))
        {
            AttachProcessLog.SidebarRequested(
                live.ProcessLog,
                plan.SidebarSectionId,
                live.SessionName,
                live.AttachClientId);
            live.CollapsedSectionIds ??= new HashSet<string>(StringComparer.Ordinal);
            if (!live.CollapsedSectionIds.Add(plan.SidebarSectionId))
                live.CollapsedSectionIds.Remove(plan.SidebarSectionId);
            AttachProcessLog.SidebarOutcome(
                live.ProcessLog,
                plan.SidebarSectionId,
                ProcessLogEvents.OutcomeOk,
                live.SessionName,
                live.AttachClientId);
            if (live.LastSnapshot is { } last)
                RebuildSidebar(live, last);
            if (plan.RebuildChrome)
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
            return;
        }

        if (hit.Kind is ChromeHitKind.SidebarWorktreeGroupToggle
            || !string.IsNullOrWhiteSpace(plan.ToggleWorktreeGroupKey))
        {
            var groupKey = FirstNonEmpty(
                plan.ToggleWorktreeGroupKey,
                hit.GroupKey,
                ResolveWorktreeTarget(live, hit.WorkspaceId ?? "")?.GroupKey);
            ToggleWorktreeGroup(live, groupKey);
            if (plan.RebuildChrome)
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(plan.SidebarTreeId))
        {
            live.CollapsedTreeIds ??= new HashSet<string>(StringComparer.Ordinal);
            if (!live.CollapsedTreeIds.Add(plan.SidebarTreeId))
                live.CollapsedTreeIds.Remove(plan.SidebarTreeId);
            if (live.LastSnapshot is { } treeSnap)
                RebuildSidebar(live, treeSnap);
            if (plan.RebuildChrome)
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
            return;
        }

        if (hit.Kind is ChromeHitKind.SidebarHiddenPane
            && !string.IsNullOrWhiteSpace(plan.FocusPaneId))
        {
            live.NavigatedPaneId = plan.FocusPaneId;
            if (live.LastSnapshot is { } hiddenSnap)
                RebuildSidebar(live, hiddenSnap);
            if (plan.RebuildChrome)
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
            return;
        }

        if (plan.Detach || plan.Request?.Action is KeyActionId.Detach)
        {
            live.DetachRequested = true;
            var detachEvents = live.Engine.RequestAction(KeyActionId.Detach);
            if (tty is not null)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await ApplyEngineEventsAsync(tty, live, detachEvents, control, linked, ct)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (plan.InvokeCollectionAction && plan.CollectionActivation is { } collectionActivation)
        {
            await InvokeCollectionActionAsync(live, control, collectionActivation, ct)
                .ConfigureAwait(false);
            if (plan.RebuildChrome)
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(plan.FocusPaneId))
        {
            live.NavigatedPaneId = plan.FocusPaneId;
            if (IsSamePaneFocusOnly(plan, live))
                return;

            await LeaveSwitcherAfterApplyAsync(live, control, tty, ct).ConfigureAwait(false);
            live.Dispatcher.FocusPane(plan.FocusPaneId);
            await SyncFocusAsync(control, live, ct, observePending: !plan.RebuildChrome)
                .ConfigureAwait(false);
            if (plan.RebuildChrome)
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(plan.FocusWorkspaceId))
        {
            await LeaveSwitcherAfterApplyAsync(live, control, tty, ct).ConfigureAwait(false);
            await live.Dispatcher.FocusWorkspaceAsync(plan.FocusWorkspaceId, ct).ConfigureAwait(false);
            await SyncFocusAsync(control, live, ct, observePending: !plan.RebuildChrome)
                .ConfigureAwait(false);
            if (plan.RebuildChrome)
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
            return;
        }

        if (plan.Request is { } request)
        {
            await LeaveSwitcherAfterApplyAsync(live, control, tty, ct).ConfigureAwait(false);
            if (request.Action is KeyActionId.ClosePane)
                ArmPendingClose(live, plan.PaneId, tabId: null);
            else if (request.Action is KeyActionId.CloseTab)
                ArmPendingClose(live, paneId: null, plan.FocusTabId);
            else if (request.Action is KeyActionId.CloseWorkspace)
                ArmPendingClose(live, paneId: null, tabId: null, request.TargetId ?? plan.FocusWorkspaceId);
            else
                ClearPendingCloseTargets(live);

            if (request.Action is KeyActionId.Settings)
                live.Engine.Settings.Bind(live.Theme, live.Ui);

            var events = live.Engine.RequestAction(
                request.Action,
                index: null,
                promptName: request.Action is not KeyActionId.NewTab);
            if (tty is not null)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await ApplyEngineEventsAsync(tty, live, events, control, linked, ct)
                    .ConfigureAwait(false);
                return;
            }

            await ApplyModeEventsAsync(events, live, control, tty: null, linked: null, ct)
                .ConfigureAwait(false);
            foreach (var ev in events)
            {
                if (ev.Kind == KeyEngineEventKind.Dispatch && ev.ToRequest() is { } dispatched)
                {
                    await DispatchLayoutActionAsync(dispatched, live, control, ct)
                        .ConfigureAwait(false);
                    if (!IsEmptyRenameEnter(dispatched))
                        await SyncFocusAsync(control, live, ct).ConfigureAwait(false);
                }
            }

            if (plan.RebuildChrome && !KeyEngine.IsPromptMode(live.Engine.Mode))
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
            return;
        }
        else if (plan.FocusTabId is { } tabId)
        {
            await LeaveSwitcherAfterApplyAsync(live, control, tty, ct).ConfigureAwait(false);
            await ApplyTabFocusAsync(
                    control,
                    live,
                    tty,
                    tabId,
                    source: "mouse",
                    ct,
                    refreshChrome: plan.RebuildChrome,
                    observePending: !plan.RebuildChrome)
                .ConfigureAwait(false);
            return;
        }

        if (plan.RebuildChrome)
            await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
    }

    internal static async Task<bool> ApplyMouseResultAsync(
        MouseEngineResult result,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationTokenSource? linked,
        CancellationToken ct,
        SemaphoreSlim? controlGate = null,
        bool controlGateHeld = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        control = LiveControlPort(live, control);

        try
        {
            return await ApplyMouseResultCoreAsync(
                    result,
                    live,
                    control,
                    tty,
                    linked,
                    ct,
                    controlGate,
                    controlGateHeld)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            linked?.Cancel();
            throw;
        }
        catch (ObjectDisposedException)
        {
            linked?.Cancel();
            throw;
        }
        catch (ControlPlaneException ex)
        {
            if (!ClosePopupIfNotOpen(live, ex))
                live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
            if (tty is not null)
            {
                if (!live.PopupOpen)
                    PaintAfterPopupClosed(tty, live);
                else
                    PaintChrome(tty, live);
            }
            return live.DetachRequested;
        }
        catch (InvalidOperationException ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
            if (tty is not null)
                PaintChrome(tty, live);
            return live.DetachRequested;
        }
        catch (ControlPlaneClientTimeoutException)
        {
            linked?.Cancel();
            throw;
        }
    }

    private static async Task<bool> ApplyMouseResultCoreAsync(
        MouseEngineResult result,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationTokenSource? linked,
        CancellationToken ct,
        SemaphoreSlim? controlGate = null,
        bool controlGateHeld = false)
    {
        if (live.Engine.Mode is AttachClientMode.Settings)
            return live.DetachRequested;

        // shell_runtime.rs:721 pane input and non-focus host effects do not
        // cross the frozen handoff. A later result in the same host read must
        // not apply after an earlier result froze input.
        if (BlocksPresentationInput(live))
            return live.DetachRequested;

        if (!await PrepareMouseApplyAsync(result, live, control, tty, linked, ct)
                .ConfigureAwait(false))
        {
            return live.DetachRequested;
        }

        switch (result.Kind)
        {
            case MouseCommandKind.ApplyChromeHit when result.Hit is { } hit:
                await ApplyChromeHitAsync(hit, live, control, tty, ct).ConfigureAwait(false);
                return live.DetachRequested;
            case MouseCommandKind.Detach:
                live.DetachRequested = true;
                RetireDestConnectAttempt(live);
                var detachEvents = live.Engine.RequestAction(KeyActionId.Detach);
                if (tty is not null && linked is not null)
                    return await ApplyEngineEventsAsync(tty, live, detachEvents, control, linked, ct)
                        .ConfigureAwait(false);
                return true;
            case MouseCommandKind.SetSplitRatio when result.Path is not null && result.Ratio is { } ratio:
                await live.Dispatcher.SetSplitRatioAsync(result.Path, ratio, ct).ConfigureAwait(false);
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
                break;
            case MouseCommandKind.MoveTab when result.TabId is { } tabId && result.TabIndex is { } index:
                await live.Dispatcher.MoveTabToAsync(tabId, index, ct).ConfigureAwait(false);
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
                break;
            case MouseCommandKind.SetSidebarWidth when result.SidebarWidth is { } width:
                live.SidebarRequestedWidth = SidebarHitModel.ClampWidth(
                    width,
                    live.Ui.SidebarMinWidth,
                    live.Ui.SidebarMaxWidth);
                live.SidebarWidth = live.SidebarRequestedWidth;
                live.SidebarWidthSource = SidebarWidthSource.Manual;
                live.SidebarOpen = true;
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
                break;
            case MouseCommandKind.SetSidebarSectionSplit when result.Ratio is { } split:
                live.SidebarSectionSplit = SidebarTwoPaneLayoutPolicy.ClampSplitRatio((float)split);
                live.SidebarSectionSplitSource = SidebarSectionSplitSource.Manual;
                PersistSidebarSectionSplit(live);
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
                break;
            case MouseCommandKind.OpenMenu when result.Menu is { } opened:
                ApplyLocalMenuMode(
                    live,
                    opened.Kind is ContextMenuKind.Global
                        ? live.Engine.EnterGlobalMenu()
                        : live.Engine.EnterContextMenu());
                ClearPendingCloseTargets(live);
                live.MouseMenu = opened;
                await PublishClientModeAsync(control, live, ct).ConfigureAwait(false);
                if (tty is not null)
                    PaintChrome(tty, live);
                break;
            case MouseCommandKind.CloseMenu:
                {
                    var noteSnapshot = OverlayLeaveNotesSnapshot(restoreFrame: true, live);
                    ApplyLocalMenuMode(
                        live,
                        live.Engine.Mode is AttachClientMode.GlobalMenu
                            ? live.Engine.LeaveGlobalMenu()
                            : live.Engine.LeaveContextMenu());
                    ClearPendingCloseTargets(live);
                    live.MouseMenu = null;
                    await PublishClientModeAsync(control, live, ct).ConfigureAwait(false);
                    if (tty is not null)
                        ApplyOverlayPaint(tty, live, restoreFrame: true, noteSnapshot);
                    else
                        live.Engine.CommitPaintMode();
                    break;
                }
            case MouseCommandKind.ApplyMenu when result.MenuItem is { } item:
                await ApplyModeEventsAsync(
                        live.Engine.Mode is AttachClientMode.GlobalMenu
                            ? live.Engine.LeaveGlobalMenu()
                            : live.Engine.LeaveContextMenu(),
                        live,
                        control,
                        tty,
                        linked,
                        ct)
                    .ConfigureAwait(false);
                ClearPendingCloseTargets(live);
                live.MouseMenu = null;
                await ApplyContextMenuItemAsync(item, result, live, control, tty, ct)
                    .ConfigureAwait(false);
                break;
            case MouseCommandKind.Yank:
                {
                    if (tty is not null && live.Mouse.Selection.HostRange)
                        PaintChrome(tty, live);
                    var yankOsc = result.Osc52;
                    if ((yankOsc is null || yankOsc.Length == 0) && live.Mouse.Selection.HostRange)
                    {
                        var yanked = live.Mouse.Selection.ExtractHost(live.Host);
                        if (yanked.Length > 0)
                            yankOsc = Osc52Yank.Encode(yanked);
                    }

                    if (yankOsc is { Length: > 0 } osc)
                    {
                        live.LastOsc52 = osc;
                        tty?.WriteBytes(osc);
                        ShowClipboardToast(live, "copied");
                    }

                    live.Mouse.Selection.Clear();
                    if (tty is not null)
                        PaintChrome(tty, live);

                    break;
                }
            case MouseCommandKind.PreviewSplit:
            case MouseCommandKind.PreviewSidebar:
            case MouseCommandKind.PreviewSidebarSection:
                ApplyMouseChromePreview(live, result);
                if (tty is not null)
                    PaintChrome(tty, live);
                break;
            case MouseCommandKind.None:
                if (tty is not null
                    && (result.State is MouseEngineState.Selecting or MouseEngineState.Menu
                        || live.Mouse.Selection.Active
                        || live.Mouse.Menu is not null))
                {
                    PaintChrome(tty, live);
                }

                break;
            case MouseCommandKind.ForwardSgr when result.ForwardKeys is { Length: > 0 } keys
                && string.Equals(result.PaneId, "popup", StringComparison.Ordinal):
                await ForwardMouseKeysIfAllowedAsync(
                        live,
                        async token =>
                        {
                            var popupKeys = new JsonObject
                            {
                                ["encoding"] = "base64",
                                ["data"] = Convert.ToBase64String(keys),
                            };
                            if (!string.IsNullOrWhiteSpace(live.InputLease))
                                popupKeys["lease_id"] = live.InputLease;
                            await control.CallAsync(ProtocolMethods.PopupSendKeys, popupKeys, token)
                                .ConfigureAwait(false);
                        },
                        controlGate,
                        tty,
                        linked,
                        ct,
                        controlGateHeld)
                    .ConfigureAwait(false);
                break;
            case MouseCommandKind.ForwardSgr when result.ForwardKeys is { Length: > 0 } keys
                && live.Overlay.OwnsModal
                && string.Equals(result.PaneId, live.Overlay.PaneId, StringComparison.Ordinal)
                && live.Overlay.Admit(live.Overlay.Generation)
                && !string.IsNullOrWhiteSpace(live.OverlayLeases?.InputLease):
                await ForwardMouseKeysIfAllowedAsync(
                        live,
                        token => control.CallAsync(
                            ProtocolMethods.PaneSendKeys,
                            OverlaySendKeysBody(live, keys, keyboard: false),
                            token),
                        controlGate,
                        tty,
                        linked,
                        ct,
                        controlGateHeld)
                    .ConfigureAwait(false);
                break;
            case MouseCommandKind.ForwardSgr when result.ForwardKeys is { Length: > 0 } keys
                && result.PaneId is { } fwdPane:
                if (!string.Equals(fwdPane, live.PaneId, StringComparison.Ordinal))
                {
                    live.Dispatcher.FocusPane(fwdPane);
                    // click-focus stays in current chrome; observe now.
                    await SyncFocusAsync(control, live, ct)
                        .ConfigureAwait(false);
                }

                if (string.Equals(fwdPane, live.PaneId, StringComparison.Ordinal)
                    && live.HasLivePane)
                {
                    await ForwardMouseKeysIfAllowedAsync(
                            live,
                            token => control.CallAsync(
                                ProtocolMethods.PaneSendKeys,
                                BuildPaneSendKeys(fwdPane, keys, live.InputLease, keyboard: false),
                                token),
                            controlGate,
                            tty,
                            linked,
                            ct,
                            controlGateHeld)
                        .ConfigureAwait(false);
                }

                break;
            case MouseCommandKind.ScrollSwitcher when result.ScrollDelta is { } switchDelta:
                ApplySwitcherSelectionDelta(live, switchDelta, tty);
                if (tty is not null)
                    PaintChrome(tty, live);
                break;
            case MouseCommandKind.ScrollSidebar when result.ScrollDelta is { } sidebarDelta:
                if (result.SidebarSlot is SidebarPaneSlot.Agents)
                    live.SidebarAgentsScroll = Math.Max(0, live.SidebarAgentsScroll + sidebarDelta);
                else if (result.SidebarSlot is SidebarPaneSlot.Resource or SidebarPaneSlot.Cubes
                    && !string.IsNullOrWhiteSpace(result.SidebarSectionId))
                {
                    var current = live.SidebarResourceScrolls.GetValueOrDefault(result.SidebarSectionId);
                    live.SidebarResourceScrolls[result.SidebarSectionId] =
                        Math.Max(0, current + sidebarDelta);
                }
                else
                    live.SidebarSpacesScroll = Math.Max(0, live.SidebarSpacesScroll + sidebarDelta);
                ClampSidebarScrolls(live);
                RecomputeChromeFromSeed(live, tty);
                if (tty is not null)
                    PaintChrome(tty, live);
                break;
            case MouseCommandKind.ScrollHistory when result.PaneId is { } scrollPane
                && result.ScrollDelta is { } delta:
                // Ghostty viewport only. Wheel-up delta is negative, so
                // -delta lines go up into the history.
                await ScrollPaneHistoryAsync(live, control, scrollPane, -delta, ct)
                    .ConfigureAwait(false);
                RecomposeAfterServerScroll(tty, live);
                break;
            case MouseCommandKind.SetHistoryTop when result.PaneId is { } barPane:
                await PublishServerScrollbarAsync(live, control, barPane, result, ct)
                    .ConfigureAwait(false);
                RecomposeAfterServerScroll(tty, live);
                break;
            case MouseCommandKind.RefreshScrollMetrics when result.PaneId is { } dragPane:
                await RefreshScrollMetricsForDragAsync(live, control, dragPane, ct)
                    .ConfigureAwait(false);
                break;
            case MouseCommandKind.ActivateLink when result.PaneId is { } linkPane:
                await ApplyLinkActivateAsync(result, linkPane, live, control, tty, linked, ct)
                    .ConfigureAwait(false);
                break;
        }

        return live.DetachRequested;
    }

    /// <summary>
    // Handled true consumes.
    /// Handled false with no plugin match replays the original gesture.
    /// Stale content or target does not replay.
    /// </summary>
    private static async Task ApplyLinkActivateAsync(
        MouseEngineResult result,
        string paneId,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationTokenSource? linked,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["pane_id"] = paneId,
            ["viewport_row"] = result.ViewportRow ?? 0,
            ["col"] = result.Col ?? 0,
        };
        if (result.Generation is { } generation)
            body["generation"] = generation;
        if (result.Offset is { } offset)
            body["offset_from_bottom"] = offset;

        try
        {
            var json = await control.CallAsync(ProtocolMethods.PaneLinkActivate, body, ct)
                .ConfigureAwait(false);
            var handled = json.ValueKind == JsonValueKind.Object
                && json.TryGetProperty("handled", out var handledEl)
                && handledEl.ValueKind == JsonValueKind.True;
            if (handled)
                return;
            await ReplayUnhandledLinkClickAsync(result, live, control, tty, linked, ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException ex)
            when (ex.Code is ProtocolErrorCodes.NotFound or ProtocolErrorCodes.InvalidState)
        {
        }
        catch (ControlPlaneException)
        {
            await ReplayUnhandledLinkClickAsync(result, live, control, tty, linked, ct)
                .ConfigureAwait(false);
        }
    }

    private static async Task ReplayUnhandledLinkClickAsync(
        MouseEngineResult result,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationTokenSource? linked,
        CancellationToken ct)
    {
        if (result.SourceEvent is not { } source)
            return;
        var ctx = MouseFeedContextFor(live) with { ReplayingLinkActivate = true };
        foreach (var next in live.Mouse.Feed(source, ctx))
        {
            if (await ApplyMouseResultCoreAsync(next, live, control, tty, linked, ct)
                    .ConfigureAwait(false))
            {
                return;
            }
        }
    }

    internal static async Task ApplyContextMenuItemAsync(
        ContextMenuItem item,
        MouseEngineResult result,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        var paneId = result.PaneId ?? live.PaneId;
        var tabId = result.TabId ?? live.TabId;
        var workspaceId = result.WorkspaceId ?? live.WorkspaceId;
        if (!item.Enabled)
            return;
        if (!string.IsNullOrWhiteSpace(item.PluginQualifiedActionId))
        {
            await live.Dispatcher.InvokePluginActionAsync(item.PluginQualifiedActionId, ct)
                .ConfigureAwait(false);
            return;
        }

        if (item.Id == ContextMenuModel.ToggleWorktreeGroup)
        {
            var group = ResolveWorktreeTarget(live, workspaceId ?? "")?.HasWorktreeChildren == true
                ? live.SidebarInput?.Workspaces.FirstOrDefault(w => w.Id == workspaceId)?.WorktreeKey
                : null;
            ToggleWorktreeGroup(live, group);
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        if (item.Action is KeyActionId.NewWorktree
            or KeyActionId.OpenWorktree
            or KeyActionId.RemoveWorktree)
        {
            await BeginWorktreeActionAsync(item.Action.Value, workspaceId, live, control, tty, ct)
                .ConfigureAwait(false);
            return;
        }
        control = LiveControlPort(live, control);
        if (!live.Release.ContinuityEnabled && ContextMenuModel.IsContinuityAction(item.Id))
            return;
        if (item.Id == ContextMenuModel.Transfer)
        {
            await BeginTransferPickerAsync(item, result, live, control, tty, ct)
                .ConfigureAwait(false);
            return;
        }
        if (live.Engine.Mode is AttachClientMode.TransferPicker)
        {
            live.Status.Set(StatusOwner.UiBusy, StatusSeverity.Error, UiBusyDetail);
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }
        if (ContextMenuModel.IsMoveWork(item.Id))
        {
            await BeginMoveWorkConfirmAsync(item, result, live, control, tty, ct)
                .ConfigureAwait(false);
            return;
        }
        if (item.Id == ContextMenuModel.Connect)
        {
            await ApplyCubesConnectAsync(result, live, control, tty, ct).ConfigureAwait(false);
            return;
        }
        if (item.Id == ContextMenuModel.RevokeDevice)
        {
            await RevokeCubeDeviceAsync(result.PlacementId, live, tty, ct).ConfigureAwait(false);
            return;
        }
        if (item.Id == GlobalMenuModel.HiddenPanes)
        {
            OpenHiddenPaneList(live, tty);
            await PublishClientModeAsync(control, live, ct).ConfigureAwait(false);
            return;
        }
        if (result.Menu?.Kind is ContextMenuKind.HiddenList)
        {
            OpenHiddenPaneActions(live, item.Id, result, tty);
            await PublishClientModeAsync(control, live, ct).ConfigureAwait(false);
            return;
        }
        // Hidden pane ids share "close" / "split_right" with workspace, tab,
        // and pane menus. Those menus set KeyActionId. Parse only when the
        // item is a hidden-only action.
        if (item.Action is null && HiddenPaneActions.Parse(item.Id) is { } hiddenAction)
        {
            await ApplyHiddenPaneActionAsync(hiddenAction, paneId, live, control, tty, ct)
                .ConfigureAwait(false);
            return;
        }
        if (item.Id == GlobalMenuModel.WhatsNew)
        {
            if (TryEnterReleaseNotes(live) && tty is not null)
                PaintChrome(tty, live);
            return;
        }

        if (item.Id == GlobalMenuModel.Settings)
        {
            live.Engine.Settings.Bind(live.Theme, live.Ui);
            await ApplyModeEventsAsync(
                    live.Engine.EnterSettings(),
                    live,
                    control,
                    tty,
                    linked: null,
                    ct)
                .ConfigureAwait(false);
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        switch (item.Id)
        {
            case ContextMenuModel.ClearName:
                if (!string.IsNullOrWhiteSpace(paneId))
                    await live.Dispatcher.ClearPaneNameAsync(paneId, ct).ConfigureAwait(false);
                break;
            case ContextMenuModel.Swap:
                if (!string.IsNullOrWhiteSpace(paneId) && live.PaneId is { Length: > 0 } focused
                    && focused != paneId)
                {
                    await live.Dispatcher.SwapWithAsync(paneId, focused, ct).ConfigureAwait(false);
                }

                break;
            case ContextMenuModel.SplitRight:
                if (!string.IsNullOrWhiteSpace(paneId))
                    await live.Dispatcher.SplitPaneAsync(paneId, "right", ct).ConfigureAwait(false);
                break;
            case ContextMenuModel.SplitDown:
                if (!string.IsNullOrWhiteSpace(paneId))
                    await live.Dispatcher.SplitPaneAsync(paneId, "down", ct).ConfigureAwait(false);
                break;
            case ContextMenuModel.RightClickHypa:
                if (!string.IsNullOrWhiteSpace(paneId))
                {
                    await live.Dispatcher.SetRightClickAsync(paneId, "hypa", ct).ConfigureAwait(false);
                    live.PaneRightClick[paneId] = "hypa";
                }

                break;
            case ContextMenuModel.RightClickPane:
                if (!string.IsNullOrWhiteSpace(paneId))
                {
                    await live.Dispatcher.SetRightClickAsync(paneId, "pane", ct).ConfigureAwait(false);
                    live.PaneRightClick[paneId] = "pane";
                }

                break;
            default:
                if (item.Action is { } action)
                {
                    if (IsPromptOrConfirmMenuAction(action))
                        ArmMenuFocusRestore(live);

                    if (!string.IsNullOrWhiteSpace(paneId)
                        && action is KeyActionId.Zoom
                            or KeyActionId.SplitVertical
                            or KeyActionId.SplitHorizontal)
                    {
                        live.Dispatcher.FocusPane(paneId);
                    }

                    if (action is KeyActionId.Detach)
                        live.DetachRequested = true;

                    if (action is KeyActionId.RenamePane && !string.IsNullOrWhiteSpace(paneId))
                        live.PendingRenamePaneId = paneId;
                    else if (action is KeyActionId.RenameTab && !string.IsNullOrWhiteSpace(tabId))
                        live.PendingRenameTabId = tabId;
                    else if (action is KeyActionId.RenameWorkspace && !string.IsNullOrWhiteSpace(workspaceId))
                        live.PendingRenameWorkspaceId = workspaceId;

                    if (action is KeyActionId.ClosePane)
                        ArmPendingClose(live, paneId, tabId: null);
                    else if (action is KeyActionId.CloseTab)
                        ArmPendingClose(live, paneId: null, tabId);
                    else if (action is KeyActionId.CloseWorkspace)
                        ArmPendingClose(live, paneId: null, tabId: null, workspaceId);
                    var events = live.Engine.RequestAction(
                        action,
                        index: null,
                        promptName: action is not KeyActionId.NewTab);
                    if (tty is not null)
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        await ApplyEngineEventsAsync(tty, live, events, control, linked, ct)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        foreach (var ev in events)
                        {
                            ApplyPendingModeEvent(live, ev);
                            if (ev.Kind == KeyEngineEventKind.Dispatch && ev.ToRequest() is { } dispatched)
                            {
                                await DispatchLayoutActionAsync(dispatched, live, control, ct)
                                    .ConfigureAwait(false);
                                if (!IsEmptyRenameEnter(dispatched))
                                    await SyncFocusAsync(control, live, ct, observePending: false)
                                        .ConfigureAwait(false);
                            }
                        }
                    }

                    if (action is KeyActionId.Zoom
                        or KeyActionId.SplitVertical
                        or KeyActionId.SplitHorizontal)
                    {
                        RevertUncommittedDispatcherFocus(live);
                    }
                }

                break;
        }

        if (!KeyEngine.IsPromptMode(live.Engine.Mode))
            await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
        else
            await ConsumeHeldPendingObserveAsync(control, live, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<HiddenPaneRecord> CatalogFromLive(AttachLiveState live)
    {
        if (live.LastSnapshot is not { } snap)
            return [];
        return HiddenPaneSnapshotMapper.Parse(snap);
    }

    private static void OpenHiddenPaneList(AttachLiveState live, UnixRawTerminal? tty)
    {
        ApplyLocalMenuMode(live, live.Engine.EnterContextMenu());
        var geo = live.Chrome;
        live.MouseMenu = HiddenPaneMenuModel.ForList(
            CatalogFromLive(live),
            geo?.Sidebar?.Col ?? 0,
            geo?.TryGlobalMenuAnchor(out var col, out var row) == true ? row : 1,
            geo?.Cols ?? 80,
            geo?.Rows ?? 24);
        live.Mouse.AdoptOpenMenu(live.MouseMenu);
        if (tty is not null)
            PaintChrome(tty, live);
    }

    private static void OpenHiddenPaneActions(
        AttachLiveState live,
        string paneId,
        MouseEngineResult result,
        UnixRawTerminal? tty)
    {
        HiddenPaneRecord? record = null;
        foreach (var item in CatalogFromLive(live))
        {
            if (string.Equals(item.PaneId, paneId, StringComparison.Ordinal))
            {
                record = item;
                break;
            }
        }

        var hidden = record?.Hidden ?? true;
        var overlay = string.Equals(paneId, live.OverlayPaneId, StringComparison.Ordinal);
        var geo = live.Chrome;
        ApplyLocalMenuMode(live, live.Engine.EnterContextMenu());
        live.MouseMenu = HiddenPaneMenuModel.ForPane(
            paneId,
            result.Hit?.PaneId is null ? geo?.Sidebar?.Col ?? 0 : live.MouseMenu?.Rect.Col ?? 0,
            live.MouseMenu?.Rect.Row ?? 1,
            geo?.Cols ?? 80,
            geo?.Rows ?? 24,
            hidden,
            overlay);
        live.Mouse.AdoptOpenMenu(live.MouseMenu);
        if (tty is not null)
            PaintChrome(tty, live);
    }

    private static async Task ApplyHiddenPaneActionAsync(
        HiddenPaneAction action,
        string? paneId,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        if (action is HiddenPaneAction.Close)
        {
            ArmPendingClose(live, paneId, tabId: null);
            var events = live.Engine.RequestAction(KeyActionId.ClosePane);
            if (tty is not null)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await ApplyEngineEventsAsync(tty, live, events, control, linked, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                foreach (var ev in events)
                    ApplyPendingModeEvent(live, ev);
            }

            return;
        }

        var catalog = CatalogFromLive(live);
        if (catalog.Count == 0 && live.LastSnapshot is { } snap)
            catalog = HiddenPaneSnapshotMapper.FromPaneItems(SidebarLiveModel.FromSnapshot(
                snap,
                live.Ui,
                expanded: true,
                requestedWidth: 0).Panes);

        var target = HiddenPaneVisibleTarget.Find(catalog, live.TabId, paneId);
        var request = new HiddenPaneActionRequest
        {
            Action = action,
            PaneId = paneId,
            CurrentTabId = live.TabId,
            WorkspaceId = live.WorkspaceId,
            PriorTabId = live.TabId,
            PriorWorkspaceId = live.WorkspaceId,
            VisibleTargetPaneId = target,
            AttachClientId = live.AttachClientId,
            OverlayVisible = string.Equals(paneId, live.OverlayPaneId, StringComparison.Ordinal),
        };
        var outcome = await HiddenPaneActionExecutor.RunAsync(request, catalog, control, ct)
            .ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, outcome.Error?.Message ?? "hidden pane action failed");
            if (!string.IsNullOrWhiteSpace(outcome.RestoredTabId))
                live.TabId = outcome.RestoredTabId;
            if (!string.IsNullOrWhiteSpace(outcome.RestoredWorkspaceId))
                live.WorkspaceId = outcome.RestoredWorkspaceId;
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        live.Status.Clear(StatusOwner.Command);
        live.NavigatedPaneId = paneId;
        if (!string.IsNullOrWhiteSpace(outcome.TargetLeaseId)
            && action is HiddenPaneAction.ShowNewTab
                or HiddenPaneAction.SplitRight
                or HiddenPaneAction.SplitBelow)
        {
            live.SiblingResizeLeases[paneId] = outcome.TargetLeaseId;
            live.Renew?.Track(outcome.TargetLeaseId);
        }

        if (action is HiddenPaneAction.ShowModal)
        {
            live.OverlayPaneId = paneId;
            if (!string.IsNullOrWhiteSpace(outcome.TargetLeaseId))
            {
                live.OverlayLeases?.Untrack(live.Renew);
                live.OverlayLeases = new TargetPaneLeasePair
                {
                    PaneId = paneId,
                    InputLease = outcome.TargetInputLeaseId ?? outcome.TargetLeaseId,
                    ResizeLease = outcome.TargetLeaseId,
                    InputNewlyGranted = outcome.TargetInputLeaseNewlyGranted,
                    ResizeNewlyGranted = outcome.TargetLeaseNewlyGranted,
                };
                live.OverlayLeases.Track(live.Renew);
            }
        }
        else if (action is HiddenPaneAction.Hide
            && string.Equals(live.OverlayPaneId, paneId, StringComparison.Ordinal))
        {
            live.OverlayPaneId = null;
        }

        if (!string.IsNullOrWhiteSpace(outcome.FocusTabId))
        {
            live.TabId = outcome.FocusTabId;
            if (!string.IsNullOrWhiteSpace(outcome.FocusWorkspaceId))
                live.WorkspaceId = outcome.FocusWorkspaceId;
            using var tabDoc = JsonDocument.Parse(new JsonObject
            {
                ["tab_id"] = outcome.FocusTabId,
                ["workspace_id"] = outcome.FocusWorkspaceId,
                ["focused_pane_id"] = paneId,
            }.ToJsonString());
            live.Dispatcher.ApplyTab(tabDoc.RootElement.Clone());
        }

        try
        {
            await PublishTiledVisibilityAfterHiddenActionAsync(control, live, action, paneId, ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
        }
        catch (InvalidOperationException ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
        }

        if (tty is not null)
            await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
    }

    internal static void ApplyLocalOverlayVisibility(AttachLiveState live, JsonElement payload)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.OverlayPaneId = HiddenPaneOverlayVisibility.Apply(
            live.OverlayPaneId,
            live.AttachClientId,
            TryJsonString(payload, "pane_id"),
            TryJsonString(payload, "mode"),
            TryJsonString(payload, "attach_client_id"));
    }

    internal static void ApplyMouseChromePreview(AttachLiveState live, MouseEngineResult result)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(result);
        if (live.ChromeSeed is not { } seed)
            return;

        var root = seed.Root;
        var sidebarWidth = live.SidebarWidth;
        float? sidebarSectionSplit = null;
        if (result.Kind is MouseCommandKind.PreviewSplit
            && result.Path is not null
            && result.Ratio is { } ratio
            && LayoutNode.IsValidRatio(ratio))
        {
            var node = LayoutTreeOperations.FromDto(root, keepPaneId: true);
            if (node is not null)
            {
                try
                {
                    node = LayoutTreeOperations.SetSplitRatio(node, result.Path, ratio);
                    root = LayoutTreeOperations.ToDto(node, includePaneId: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        if (result.Kind is MouseCommandKind.PreviewSidebar && result.SidebarWidth is { } width)
            sidebarWidth = width;
        if (result.Kind is MouseCommandKind.PreviewSidebarSection && result.Ratio is { } previewSplit)
            sidebarSectionSplit = SidebarTwoPaneLayoutPolicy.ClampSplitRatio((float)previewSplit);

        live.Chrome = ComputeLiveChrome(
            live,
            seed.Cols,
            seed.Rows,
            root,
            seed.Zoomed,
            seed.ZoomedPaneId,
            seed.FocusedPaneId,
            sidebarWidth,
            sidebarSectionSplit);
    }

    internal static async Task PublishServerScrollAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        string paneId,
        int offset,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        var reply = await control.CallAsync(
                ProtocolMethods.PaneScroll,
                new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["offset"] = Math.Max(0, offset),
                },
                ct)
            .ConfigureAwait(false);
        ApplyPaneScrollReply(live, paneId, reply);
    }

    internal static async Task PublishServerScrollbarAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        string paneId,
        MouseEngineResult result,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(result);
        live.TryGetPaneScrollMetrics(paneId, out var current, out var maxOffset, out _);
        var mapped = Math.Max(0, result.Offset ?? 0);
        // The stored metrics can be old. While a pane follows its output,
        // the mux does not send each change of max_offset. While the view is
        // scrolled back, output moves the offset and the max, and the event
        // waits up to 100 ms for the coalesce. A track jump reads them again
        // before it maps the row. A drag step does not: the press on the
        // thumb read them (RefreshScrollMetricsForDragAsync).
        if (maxOffset <= 0 || (result.TrackJump && CanReadScrollMetrics(live, paneId, current)))
        {
            // at click and no-ops when max_offset_from_bottom is 0. Attach does
            // not have that VT; hydrate from pane.scroll then map the track.
            await ScrollPaneHistoryAsync(live, control, paneId, 0, ct)
                .ConfigureAwait(false);
            live.TryGetPaneScrollMetrics(paneId, out current, out maxOffset, out _);
            live.Mouse.NoteScrollMaxOffset(maxOffset);
            if (maxOffset <= 0)
                return;
            if (result.TrackRow is int row
                && live.Chrome is { } chrome
                && TryPaneFrame(chrome, paneId, out var pane)
                && pane.Scrollbar is { } bar)
            {
                mapped = pane.Scroll.ShowsScrollbar
                    ? PaneChromeGutter.OffsetFromRow(pane.Scroll, bar, row)
                    : MouseEngine.MapScrollbarOffset(row, bar, maxOffset);
            }
        }

        if (mapped != current)
        {
            await PublishServerScrollAsync(live, control, paneId, mapped, ct)
                .ConfigureAwait(false);
            live.TryGetPaneScrollMetrics(paneId, out _, out maxOffset, out _);
            live.Mouse.NoteScrollMaxOffset(maxOffset);
        }
    }

    /// <summary>
    /// A press on the thumb starts a drag. The stored metrics can be old
    /// (see <see cref="PublishServerScrollbarAsync"/>), so read them with a
    /// step of 0 rows: the view does not move. The drag then maps its rows
    /// with the current max.
    /// </summary>
    internal static async Task RefreshScrollMetricsForDragAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        string paneId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (!live.TryGetPaneScrollMetrics(paneId, out var current, out _, out _)
            || !CanReadScrollMetrics(live, paneId, current))
        {
            return;
        }

        await ScrollPaneHistoryAsync(live, control, paneId, 0, ct).ConfigureAwait(false);
        live.TryGetPaneScrollMetrics(paneId, out _, out var maxOffset, out _);
        live.Mouse.NoteScrollMaxOffset(maxOffset);
    }

    /// <summary>
    /// True when a <c>pane.scroll</c> step of 0 rows reads the metrics and
    /// does not move the view. At offset 0 each mux keeps the view at the
    /// bottom. While the view is scrolled back, only a mux that takes
    /// <c>delta</c> keeps it: that mux also sends a revision. A mux with no
    /// <c>delta</c> would set the view to the old offset that the attach sends.
    /// The revision must come from the mux that serves the attach now. A
    /// reconnect keeps the revisions of the earlier mux.
    /// </summary>
    private static bool CanReadScrollMetrics(AttachLiveState live, string paneId, int current) =>
        current == 0 || live.ServingMuxSentPaneScrollRevision(paneId);

    private static void RecomposeAfterServerScroll(UnixRawTerminal? tty, AttachLiveState live)
    {
        if (tty is null || !UsesHostFrame(live))
            return;
        // Do not stamp the retained live grid as history. Origin Full
        // cells compose when they arrive (ApplyCellsViewportOrigin).
        if (live.PaneIsScrolledBack(live.PaneId))
            live.Host.Cursor = HostCursor.None;
    }

    internal static void ApplyPaneScrollReply(AttachLiveState live, string paneId, JsonElement reply)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(paneId) || reply.ValueKind != JsonValueKind.Object)
            return;
        if (!TryJsonInt32(reply, "offset", out var offset))
            return;
        if (!TryJsonInt32(reply, "max_offset", out var maxOffset))
            live.TryGetPaneScrollMetrics(paneId, out _, out maxOffset, out _);
        var id = TryJsonString(reply, "pane_id") ?? paneId;
        var viewport = 0;
        if (live.Chrome is { } chrome && TryPaneContent(chrome, id, out var box))
            viewport = box.Rows;
        // A pane.scroll_changed that the mux sent after this state can be
        // applied first. Then this reply is older, and it must not set the
        // offset back.
        if (!live.TrySetPaneScrollMetrics(id, offset, maxOffset, viewport, PaneScrollRevision.TryRead(reply), fromReply: true))
            return;
        SyncPaneScrollChrome(live, tty: null);
    }

    /// <summary>
    /// Apply one <c>pane.scroll_changed</c>. Returns false for a bad payload
    /// and for a state older than the last applied state of the pane: the
    /// event can wait behind other events while a newer <c>pane.scroll</c>
    /// reply is applied.
    /// </summary>
    internal static bool TryApplyPaneScrollChanged(
        AttachLiveState live,
        JsonElement payload,
        UnixRawTerminal? tty = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        var paneId = TryJsonString(payload, "pane_id");
        if (paneId is null || !TryJsonInt32(payload, "offset", out var offset))
            return false;
        var maxOffset = 0;
        _ = TryJsonInt32(payload, "max_offset", out maxOffset);
        var viewport = 0;
        if (live.Chrome is { } chrome && TryPaneContent(chrome, paneId, out var box))
            viewport = box.Rows;
        if (!live.TrySetPaneScrollMetrics(paneId, offset, maxOffset, viewport, PaneScrollRevision.TryRead(payload)))
            return false;
        SyncPaneScrollChrome(live, tty);
        return true;
    }

    /// <summary>
    /// One wheel step over the history of a pane. The mux adds
    /// <paramref name="linesUp"/> to its own offset. While the view is
    /// scrolled back, new output moves that offset up, so an offset that the
    /// attach computes from its last known value would move the view down.
    /// The request keeps <c>offset</c> for a mux that has no <c>delta</c>.
    /// </summary>
    internal static async Task ScrollPaneHistoryAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        string paneId,
        int linesUp,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        live.TryGetPaneScrollMetrics(paneId, out var currentOffset, out _, out _);
        var reply = await control.CallAsync(
                ProtocolMethods.PaneScroll,
                new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["offset"] = Math.Max(0, currentOffset + linesUp),
                    ["delta"] = linesUp,
                },
                ct)
            .ConfigureAwait(false);
        ApplyPaneScrollReply(live, paneId, reply);
    }

    /// <summary>
    /// The scroll state of a pane changed. Bind the new state into the
    /// current chrome and keep all other parts of it, so that a scroll event
    /// does not make the next cells delta a full compose. When the host shows
    /// the old chrome (the patch baseline has it), the baseline moves to the
    /// new chrome. A pane whose scrollbar cells change goes into
    /// <see cref="AttachLiveState.PendingGutterPanes"/>, and the next patch
    /// paint stamps its gutter. A change of the pane rects or of the host
    /// size builds the chrome again from the seed.
    /// </summary>
    internal static bool SyncPaneScrollChrome(AttachLiveState live, UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.ChromeEnabled || live.ChromeSeed is null)
            return false;

        var hostCols = -1;
        var hostRows = -1;
        if (tty is not null && tty.TryGetSize(out var tc, out var tr))
        {
            hostCols = tc;
            hostRows = tr;
        }

        var rebound = live.WithPaint(() =>
        {
            if (!live.ChromeEnabled || live.Chrome is not { } chrome)
                return false;
            if (hostCols >= 0 && (chrome.Cols != hostCols || chrome.Rows != hostRows))
                return false;

            List<ChromePaneFrame>? panes = null;
            List<string>? gutters = null;
            for (var i = 0; i < chrome.Panes.Count; i++)
            {
                var pane = chrome.Panes[i];
                if (!PaneChromeGutter.TryRebindScroll(pane, LivePaneScrollState(live, pane.PaneId), out var next))
                    return false;
                if (ReferenceEquals(next, pane))
                    continue;
                panes ??= [.. chrome.Panes];
                panes[i] = next;
                if (!PaneChromeGutter.StampsSameCells(pane, next))
                    (gutters ??= []).Add(pane.PaneId);
            }

            if (panes is null)
                return true;
            var nextChrome = chrome with { Panes = panes };
            if (live.PatchBaseline is { } baseline && ReferenceEquals(baseline.Key.Chrome, chrome))
            {
                live.PatchBaseline = baseline.WithKey(baseline.Key with { Chrome = nextChrome });
                if (gutters is not null)
                    live.PendingGutterPanes.UnionWith(gutters);
            }

            live.Chrome = nextChrome;
            return true;
        });
        return !rebound && SyncPaneChromeGeometry(live, tty);
    }

    /// <summary>The scroll state of a pane, as <see cref="BindLivePaneChrome"/> binds it.</summary>
    private static PaneChromeScrollState LivePaneScrollState(AttachLiveState live, string paneId)
    {
        var alt = live.GetPaneAlt(paneId);
        var known = live.TryGetPaneScrollMetrics(paneId, out var offset, out var maxOffset, out var viewport);
        return new PaneChromeScrollState(alt, known, offset, maxOffset, viewport);
    }

    internal static async Task EnsureHistorySeededAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        string paneId,
        CancellationToken ct)
    {
        if (live.History.IsSeeded && string.Equals(live.History.PaneId, paneId, StringComparison.Ordinal))
            return;

        AssembledSnapshot? snap = null;
        if (live.TryGetPaneFrame(paneId, out var frame))
            snap = frame;
        else if (string.Equals(paneId, live.PaneId, StringComparison.Ordinal))
            snap = live.LastComplete;

        string? recent = null;
        var alt = live.GetPaneAlt(paneId) || snap is { IsAlternateScreen: true };
        if (!alt && live.HasLivePane)
        {
            try
            {
                var cols = snap is { Cols: > 0 } ? snap.Cols : 80;
                var result = await control.CallAsync(
                        ProtocolMethods.PaneRead,
                        new JsonObject
                        {
                            ["pane_id"] = paneId,
                            ["source"] = "recent",
                            ["lines"] = CopyModeBuffer.ResolveSeedRecentLines(cols),
                        },
                        ct)
                    .ConfigureAwait(false);
                if (result.ValueKind == JsonValueKind.Object
                    && result.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    recent = text.GetString();
                }
            }
            catch (ControlPlaneException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }
        }

        var viewport = 24;
        if (live.Chrome is { } chrome && TryPaneContent(chrome, paneId, out var box))
            viewport = box.Rows;
        else if (snap is { Rows: > 0 })
            viewport = snap.Rows;
        // History.Paint and Selection.Seed enumerate Buffer.Lines under this lock.
        live.WithPaint(() => live.History.Seed(snap, recent, viewport, paneId));
    }

    /// <summary>
    /// Resize every visible pane to its chrome content size. Every caller goes
    /// through <see cref="AttachLiveState.PaneChromeResizeGate"/>, so an older
    /// resize cannot finish after a newer one.
    /// </summary>
    internal static async Task ResizeVisiblePanesAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct)
    {
        await live.PaneChromeResizeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ResizeVisiblePanesCoreAsync(control, live, ct).ConfigureAwait(false);
        }
        finally
        {
            live.PaneChromeResizeGate.Release();
        }
    }

    private static async Task ResizeVisiblePanesCoreAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct)
    {
        if (live.Chrome is not { } chrome)
            return;

        var visible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pane in chrome.Panes)
            visible.Add(pane.PaneId);

        var staleLeases = new List<(string PaneId, string LeaseId)>();
        lock (live.ChromeStateGate)
        {
            foreach (var (paneId, leaseId) in live.SiblingResizeLeases.ToArray())
            {
                if (visible.Contains(paneId) && paneId != live.PaneId)
                    continue;
                live.SiblingResizeLeases.Remove(paneId);
                staleLeases.Add((paneId, leaseId));
            }
        }

        // A stale lease is of a pane that is not visible, or of the attach
        // pane, which uses its own lease. The choice of lease below does not
        // read it, so the panes are marked before its release call.
        var sends = new List<(string PaneId, int Cols, int Rows, JsonObject Params)>();
        InvalidOperationException? noSize = null;
        foreach (var pane in chrome.Panes)
        {
            if (!ShouldDrivePane(live, pane.PaneId))
                continue;
            if (pane.Content.Cols < 1 || pane.Content.Rows < 1)
            {
                live.Status.Set(StatusOwner.Chrome, StatusSeverity.Error, FormatStatus("pane content has no size"));
                noSize = new InvalidOperationException(live.StatusError);
                break;
            }

            var requestCols = pane.Content.Cols;
            var requestRows = pane.Content.Rows;
            lock (live.ChromeStateGate)
            {
                if (live.LastSentPaneSizes.TryGetValue(pane.PaneId, out var sent)
                    && sent.Cols == requestCols
                    && sent.Rows == requestRows)
                {
                    continue;
                }

                var ownerNow = live.PaneGeometryOwners.TryGetValue(pane.PaneId, out var known)
                    ? known
                    : "";
                if (live.DeclinedPaneResizes.TryGetValue(pane.PaneId, out var declined)
                    && declined.Cols == requestCols
                    && declined.Rows == requestRows
                    && string.Equals(declined.Owner, ownerNow, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            string? lease = null;
            lock (live.ChromeStateGate)
            {
                if (pane.PaneId == live.PaneId)
                    lease = live.ResizeLease;
                else if (live.SiblingResizeLeases.TryGetValue(pane.PaneId, out var existing))
                    lease = existing;
            }

            var resizeParams = new JsonObject
            {
                ["pane_id"] = pane.PaneId,
                ["cols"] = requestCols,
                ["rows"] = requestRows,
            };
            if (!string.IsNullOrWhiteSpace(lease))
                resizeParams["lease_id"] = lease;
            sends.Add((pane.PaneId, requestCols, requestRows, resizeParams));
        }

        // Mark each pane before the first request goes. Each of these panes
        // has a new box now, and its frames of the old size must not paint
        // into it while a request waits for its reply.
        if (sends.Count > 0)
        {
            var sentAt = live.Time.GetUtcNow();
            lock (live.ChromeStateGate)
            {
                foreach (var send in sends)
                    live.OwnPaneResizeSentAt[send.PaneId] = sentAt;
            }
        }

        // All requests go at once, so the last pane does not wait one round
        // trip for each pane before it. The mux applies each one and posts
        // the full frame of its pane before its reply.
        var calls = new Task<JsonElement>[sends.Count];
        for (var i = 0; i < sends.Count; i++)
        {
            try
            {
                calls[i] = control.CallAsync(ProtocolMethods.PaneResize, sends[i].Params, ct);
            }
            catch (Exception ex)
            {
                calls[i] = Task.FromException<JsonElement>(ex);
            }
        }

        // The stale leases go after the requests. No request uses them, and
        // a slow release then cannot use up the grace of the marks above.
        // Stop the renewal of each one first, so a failed release cannot
        // leave a later one renewed with no owner.
        foreach (var stale in staleLeases)
            live.Renew?.Untrack(stale.LeaseId);
        foreach (var stale in staleLeases)
            await ReleaseLeaseQuietAsync(control, stale.LeaseId, ct).ConfigureAwait(false);

        Exception? failed = null;
        for (var i = 0; i < sends.Count; i++)
        {
            var (paneId, requestCols, requestRows, _) = sends[i];
            JsonElement resized;
            try
            {
                resized = await calls[i].ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Read every reply first. The first failure then goes to the
                // caller, as it did when the requests went one at a time.
                failed ??= ex;
                continue;
            }

            var appliedCols = requestCols;
            var appliedRows = requestRows;
            if (TryJsonInt32(resized, "cols", out var gotCols)
                && TryJsonInt32(resized, "rows", out var gotRows))
            {
                appliedCols = gotCols;
                appliedRows = gotRows;
            }

            var owner = TryJsonString(resized, "geometry_owner") ?? "";
            if (appliedCols != requestCols || appliedRows != requestRows)
            {
                AttachProcessLog.Observe(
                    live.ProcessLog,
                    live.SessionName,
                    live.AttachClientId,
                    paneId,
                    ProcessLogEvents.OutcomeDropped,
                    "resize " + appliedCols + "x" + appliedRows
                        + "!=" + requestCols + "x" + requestRows);
                lock (live.ChromeStateGate)
                {
                    live.PaneGeometryOwners[paneId] = owner;
                    live.DeclinedPaneResizes[paneId] = new DeclinedPaneResize(
                        requestCols,
                        requestRows,
                        owner);
                }
            }
            else
            {
                lock (live.ChromeStateGate)
                {
                    live.DeclinedPaneResizes.Remove(paneId);
                    live.PaneGeometryOwners[paneId] = owner;
                    live.LastSentPaneSizes[paneId] = (appliedCols, appliedRows);
                }
            }
        }

        if (failed is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
        if (noSize is not null)
            throw noSize;
    }

    internal static async Task ObserveVisiblePanesAsync(
        IAttachCommandPort render,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(live);
        if (live.Chrome is null || string.IsNullOrWhiteSpace(live.RenderSub))
        {
            AttachProcessLog.Observe(
                live.ProcessLog,
                live.SessionName,
                live.AttachClientId,
                live.PaneId,
                ProcessLogEvents.OutcomeDropped,
                live.Chrome is null ? "no_chrome" : "no_render_sub");
            return;
        }

        var depth = Interlocked.Increment(ref live.ObserveVisibleDepth);
        try
        {
            var desired = CollectDesiredVisiblePaneIds(live);

            await PublishVisibleSetAsync(render, live, desired, ct).ConfigureAwait(false);

            var stale = false;
            string? keep = null;
            lock (live.ChromeStateGate)
            {
                foreach (var id in live.ObservedPaneIds)
                {
                    if (!desired.Contains(id, StringComparer.Ordinal))
                    {
                        stale = true;
                        break;
                    }
                }

                if (stale && desired.Count > 0)
                    keep = desired[0];
            }

            if (stale && keep is not null)
            {
                string[] previous;
                lock (live.ChromeStateGate)
                {
                    previous = [.. live.ObservedPaneIds];
                    live.ObservedPaneIds.Clear();
                    live.ObservedPaneIds.Add(keep);
                }

                try
                {
                    await render.CallAsync(
                            ProtocolMethods.TerminalObserve,
                            new JsonObject
                            {
                                ["pane_id"] = keep,
                                ["subscription_id"] = live.RenderSub,
                                ["replace"] = true,
                            },
                            ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    lock (live.ChromeStateGate)
                    {
                        live.ObservedPaneIds.Clear();
                        foreach (var id in previous)
                            live.ObservedPaneIds.Add(id);
                    }

                    await MarkVisibleSetFailedAsync(render, live, ct).ConfigureAwait(false);
                    throw;
                }
            }

            foreach (var paneId in desired)
            {
                bool already;
                lock (live.ChromeStateGate)
                {
                    already = live.ObservedPaneIds.Contains(paneId);
                    live.ObservedPaneIds.Add(paneId);
                }

                var hasFrame = live.TryGetPaneFrame(paneId, out var stored) && stored is not null;
                if (already && hasFrame)
                {
                    if (string.Equals(paneId, live.PaneId, StringComparison.Ordinal))
                    {
                        AttachProcessLog.Observe(
                            live.ProcessLog,
                            live.SessionName,
                            live.AttachClientId,
                            paneId,
                            ProcessLogEvents.OutcomeDropped,
                            "has_frame");
                    }

                    continue;
                }

                if (already && !hasFrame && depth > 1)
                {
                    AttachProcessLog.Observe(
                        live.ProcessLog,
                        live.SessionName,
                        live.AttachClientId,
                        paneId,
                        ProcessLogEvents.OutcomeDropped,
                        "nested");
                    continue;
                }

                try
                {
                    var size = "unknown";
                    if (live.Chrome is { } sized && TryPaneContent(sized, paneId, out var box))
                        size = box.Cols + "x" + box.Rows;
                    AttachProcessLog.Observe(
                        live.ProcessLog,
                        live.SessionName,
                        live.AttachClientId,
                        paneId,
                        ProcessLogEvents.OutcomeStarted,
                        size);
                    await render.CallAsync(
                            ProtocolMethods.TerminalObserve,
                            new JsonObject
                            {
                                ["pane_id"] = paneId,
                                ["subscription_id"] = live.RenderSub,
                                ["replace"] = false,
                            },
                            ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    if (!already)
                    {
                        lock (live.ChromeStateGate)
                            live.ObservedPaneIds.Remove(paneId);
                    }

                    await MarkVisibleSetFailedAsync(render, live, ct).ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref live.ObserveVisibleDepth);
        }
    }

    /// <summary>
    /// One-connection control subscribe id. Distinct from
    /// <see cref="AttachLiveState.AttachClientId"/>.
    /// </summary>
    internal static string? OwnedControlSubscriptionId(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!string.IsNullOrWhiteSpace(live.ControlSub))
            return live.ControlSub;
        return string.IsNullOrWhiteSpace(live.RenderSub) ? null : live.RenderSub;
    }

    /// <summary>
    /// Hidden-modal overlay id. The command popup overlay wins when set.
    /// </summary>
    internal static string? CurrentPublishedOverlayPaneId(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var command = live.Dispatcher.CommandOverlayPaneId;
        if (!string.IsNullOrWhiteSpace(command))
            return command;
        if (live.Overlay.OwnsModal && !string.IsNullOrWhiteSpace(live.Overlay.PaneId))
            return live.Overlay.PaneId;
        return string.IsNullOrWhiteSpace(live.OverlayPaneId) ? null : live.OverlayPaneId;
    }

    /// <summary>
    /// ids from the active client view.
    /// </summary>
    internal static List<string> CollectDesiredVisiblePaneIds(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var desired = new List<string>();
        if (live.Chrome is not { } chrome)
        {
            if (live.LastPublishedVisiblePaneIds is { Count: > 0 } last)
                desired.AddRange(last);
            else if (!string.IsNullOrWhiteSpace(live.PaneId))
                desired.Add(live.PaneId);
            return desired;
        }

        if (!string.IsNullOrWhiteSpace(live.PaneId))
            desired.Add(live.PaneId);
        foreach (var pane in chrome.Panes)
        {
            if (desired.Contains(pane.PaneId, StringComparer.Ordinal))
                continue;
            if (!ShouldDrivePane(live, pane.PaneId))
                continue;
            if (desired.Count >= 1 + AttachLiveState.MaxSiblingObserves)
                break;
            desired.Add(pane.PaneId);
        }

        return desired;
    }

    /// <summary>
    /// Publishes tiled ids after hidden list, new-tab, split, hide, or modal.
    /// Uses the owned control subscription, not attach_client_id.
    /// </summary>
    internal static Task PublishTiledVisibilityAfterHiddenActionAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        HiddenPaneAction action,
        string paneId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        var desired = CollectDesiredVisiblePaneIds(live);
        if (!string.IsNullOrWhiteSpace(paneId))
        {
            if (action is HiddenPaneAction.ShowNewTab
                or HiddenPaneAction.SplitRight
                or HiddenPaneAction.SplitBelow)
            {
                if (!desired.Contains(paneId, StringComparer.Ordinal))
                    desired.Add(paneId);
            }
            else if (action is HiddenPaneAction.Hide)
            {
                desired.RemoveAll(id => string.Equals(id, paneId, StringComparison.Ordinal));
            }
        }

        return PublishVisibleSetAsync(control, live, desired, ct);
    }

    internal static async Task PublishVisibleSetAsync(
        IAttachCommandPort render,
        AttachLiveState live,
        IReadOnlyList<string> paneIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(paneIds);
        var subscriptionId = OwnedControlSubscriptionId(live);
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return;

        var nodes = new JsonNode?[paneIds.Count];
        for (var i = 0; i < paneIds.Count; i++)
            nodes[i] = JsonValue.Create(paneIds[i]);
        var ids = new JsonArray(nodes);

        var payload = new JsonObject
        {
            ["subscription_id"] = subscriptionId,
            ["pane_ids"] = ids,
        };
        var overlay = CurrentPublishedOverlayPaneId(live);
        if (!string.IsNullOrWhiteSpace(overlay))
            payload["overlay_pane_id"] = overlay;

        try
        {
            await render.CallAsync(ProtocolMethods.TerminalVisibleSet, payload, ct)
                .ConfigureAwait(false);
            live.LastVisibleSetPublishUtc = DateTimeOffset.UtcNow;
            live.LastPublishedVisiblePaneIds = [.. paneIds];
        }
        catch
        {
            await MarkVisibleSetFailedAsync(render, live, ct).ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task RenewVisibleSetIfDueAsync(
        IAttachCommandPort render,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(OwnedControlSubscriptionId(live)))
            return;
        if (live.LastVisibleSetPublishUtc is { } last
            && DateTimeOffset.UtcNow - last < VisibleSetRenewPeriod)
        {
            return;
        }

        IReadOnlyList<string> ids = live.LastPublishedVisiblePaneIds ?? [];
        if (ids.Count == 0 && live.Chrome is { } chrome)
        {
            var desired = new List<string>();
            if (!string.IsNullOrWhiteSpace(live.PaneId))
                desired.Add(live.PaneId);
            foreach (var pane in chrome.Panes)
            {
                if (desired.Contains(pane.PaneId, StringComparer.Ordinal))
                    continue;
                desired.Add(pane.PaneId);
            }

            ids = desired;
        }

        try
        {
            await PublishVisibleSetAsync(render, live, ids, ct).ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class ControlPlaneCallPort(ControlPlaneClient client) : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
            client.CallAsync(method, parameters, ct);
    }

    internal static async Task MarkVisibleSetFailedAsync(
        IAttachCommandPort render,
        AttachLiveState live,
        CancellationToken ct)
    {
        var subscriptionId = OwnedControlSubscriptionId(live);
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return;
        try
        {
            await render.CallAsync(
                    ProtocolMethods.TerminalVisibleSet,
                    new JsonObject
                    {
                        ["subscription_id"] = subscriptionId,
                        ["pane_ids"] = new JsonArray(),
                        ["failed"] = true,
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// First complete snapshot. Chrome already on screen skips RefreshChrome
    /// so a parked snapshot during terminal.observe cannot observe again.
    /// </summary>
    internal static async Task AfterInitialSnapshotPaintAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        if (live.InitialSnapshotPainted)
            return;
        live.InitialSnapshotPainted = true;
        if (live.Chrome is not null)
            return;
        await RefreshChromeAsync(control, live, tty, ct, observeVisible: false)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The badge text for a tab. The mux computes the tab status view.
    /// The attach only formats it.
    /// </summary>
    private static string TabStatusBadge(JsonElement tab, StatusIndicatorStyle style) =>
        tab.TryGetProperty("program_status_view", out var statusView)
        && ProgramStatusViewWire.TryReadTab(statusView) is { } view
            ? SidebarProgramStatusText.TabBadge(view, style)
            : "";

    internal static List<TabBarTabSpec> ReadTabHits(
        JsonElement listed,
        string? activeTabId,
        StatusIndicatorStyle style = StatusIndicatorStyle.Dots)
    {
        var tabs = new List<TabBarTabSpec>();
        if (listed.ValueKind != JsonValueKind.Array)
            return tabs;
        foreach (var item in listed.EnumerateArray())
        {
            var id = TryJsonString(item, "tab_id");
            if (id is null)
                continue;
            var label = TryJsonString(item, "label") ?? id;
            tabs.Add(new TabBarTabSpec(
                id,
                label,
                id == activeTabId,
                TryJsonBool(item, "zoomed"),
                TryJsonBool(item, "custom_label"),
                TabStatusBadge(item, style)));
        }

        return tabs;
    }

    private static LayoutNodeDto? TryLayoutRoot(JsonElement exported)
    {
        if (exported.ValueKind != JsonValueKind.Object
            || !exported.TryGetProperty("root", out var root)
            || root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return DeserializePayload(root, ProtocolJsonContext.Default.LayoutNodeDto);
    }

    private static string? TryJsonString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        var value = prop.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool TryJsonBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return false;
        if (!el.TryGetProperty(name, out var prop))
            return false;
        return prop.ValueKind is JsonValueKind.True
            || (prop.ValueKind is JsonValueKind.Number && prop.TryGetInt64(out var n) && n != 0);
    }

    private static bool TryJsonInt32(JsonElement el, string name, out int value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object)
            return false;
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number)
            return false;
        return prop.TryGetInt32(out value);
    }

    /// <summary>
    /// Decode a wire record from an already-parsed JSON node.
    /// the payload bytes once into <c>M</c>. The NDJSON analogue is
    /// <c>payload.Deserialize</c> on the envelope node. Do not
    /// <c>GetRawText</c> then parse that string again.
    /// </summary>
    internal static T? DeserializePayload<T>(JsonElement payload, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return payload.Deserialize(typeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    internal static bool TryCaptureActivationTerminalRender(
        AttachLiveState live,
        string endpointId,
        ulong generation,
        JsonElement payload,
        SnapshotAssembler? assembler)
    {
        ArgumentNullException.ThrowIfNull(live);
        lock (live.ActivationGate)
        {
            if (live.PendingActivation is null)
                return false;
            if (!payload.TryGetProperty("pane_id", out var paneProp)
                || paneProp.GetString() is not { Length: > 0 })
                return false;
            if (!payload.TryGetProperty("kind", out var kindProp)
                || kindProp.ValueKind != JsonValueKind.String)
                return false;

            var kind = kindProp.GetString();
            if (string.Equals(kind, TerminalRenderCellsPayload.KindCells, StringComparison.Ordinal))
            {
                var cells = DeserializePayload(payload, ProtocolJsonContext.Default.TerminalRenderCellsPayload);
                // An ack-only body has no cells to capture.
                if (cells is null || cells.AckOnly || live.PendingActivation is null)
                    return false;
                var frame = VtCellUnpacker.Apply(cells, previous: null);
                return StoreOrHoldActivationCapture(live, endpointId, generation, payload, frame);
            }

            if (string.Equals(kind, TerminalRenderSnapshotPayload.KindSnapshot, StringComparison.Ordinal)
                && assembler is not null)
            {
                var slice = DeserializePayload(payload, ProtocolJsonContext.Default.TerminalRenderSnapshotPayload);
                if (slice is null || !assembler.TryAdd(slice, out var frame) || frame is null)
                    return false;
                if (live.PendingActivation is null)
                    return false;
                return StoreOrHoldActivationCapture(live, endpointId, generation, payload, frame);
            }

            return false;
        }
    }

    internal static bool TryResolveActivationCaptureBinding(
        AttachLiveState live,
        string endpointId,
        ulong generation,
        JsonElement payload,
        out ActivationCaptureBinding binding)
    {
        binding = null!;
        if (live.PendingActivation is not { } pending)
            return false;
        if (!TryActivationLease(pending, endpointId, generation, out var lease))
            return false;

        var evidence = EvidenceForEndpoint(pending, endpointId);
        ulong projectionRevision;
        ulong surfaceRevision;
        if (evidence?.Surface is { } surface
            && string.Equals(surface.BootId, lease.BootId, StringComparison.Ordinal))
        {
            projectionRevision = surface.ProjectionRevision;
            surfaceRevision = surface.SurfaceRevision;
        }
        else
        {
            // availability needs a snapshot. Do not stamp surface
            // revision from the snapshot revision. Hold the render
            // until matching surface evidence arrives.
            return false;
        }

        binding = new ActivationCaptureBinding(
            lease.EndpointId,
            lease.ConnectionGeneration,
            lease.BootId,
            projectionRevision,
            surfaceRevision);
        return true;
    }

    /// dest observe can emit cells before attach.projection_snapshot.
    /// Hold that frame and bind it once snapshot or surface evidence lands.
    internal static void TryBindUnboundActivationRender(
        AttachLiveState live,
        string endpointId,
        ulong generation)
    {
        ArgumentNullException.ThrowIfNull(live);
        lock (live.ActivationGate)
        {
            if (!live.TryPeekUnboundActivationRender(endpointId, generation, out var frame)
                || frame is null)
                return;
            if (!TryResolveActivationCaptureBinding(
                    live,
                    endpointId,
                    generation,
                    default,
                    out var binding))
                return;
            StoreBoundActivationCapture(live, binding, frame);
            live.ClearUnboundActivationRender(endpointId);
        }
    }

    private static bool StoreOrHoldActivationCapture(
        AttachLiveState live,
        string endpointId,
        ulong generation,
        JsonElement payload,
        AssembledSnapshot frame)
    {
        if (TryResolveActivationCaptureBinding(
                live,
                endpointId,
                generation,
                payload,
                out var binding))
        {
            StoreBoundActivationCapture(live, binding, frame);
            live.ClearUnboundActivationRender(endpointId);
            return true;
        }

        if (live.PendingActivation is { } pending
            && TryActivationLease(pending, endpointId, generation, out _))
            live.HoldUnboundActivationRender(endpointId, generation, frame);
        return false;
    }

    // / completing lease is active.
    /// surface that matches the retained snapshot. A cells frame that
    /// arrives after that surface is already installed still paints.
    /// The stored capture stays so a later <c>SetPaneSurface</c> can take it.
    private static void StoreBoundActivationCapture(
        AttachLiveState live,
        ActivationCaptureBinding binding,
        AssembledSnapshot frame)
    {
        live.StoreActivationCapturedFrameCore(new ActivationCapturedFrame(binding, frame));
        if (live.CoherentPaneSurface is not { } installed)
            return;
        if (!string.Equals(binding.BootId, installed.BootId, StringComparison.Ordinal)
            || binding.ProjectionRevision != installed.ProjectionRevision
            || binding.SurfaceRevision != installed.SurfaceRevision)
            return;
        live.SetPaneFrame(frame);
    }

    private static bool TryActivationLease(
        PendingEndpointActivation pending,
        string endpointId,
        ulong generation,
        out EndpointActivationLease lease)
    {
        lease = null!;
        // An unavailable source has no live connection and is never a lease match.
        if (pending.SourceAvailable
            && string.Equals(pending.Source.EndpointId, endpointId, StringComparison.Ordinal)
            && pending.Source.ConnectionGeneration == generation)
        {
            lease = pending.Source;
            return true;
        }

        if (string.Equals(pending.Target.EndpointId, endpointId, StringComparison.Ordinal)
            && pending.Target.ConnectionGeneration == generation)
        {
            lease = pending.Target;
            return true;
        }

        return false;
    }

    private static EndpointActivationEvidence? EvidenceForEndpoint(
        PendingEndpointActivation pending,
        string endpointId)
    {
        return pending.Phase switch
        {
            ActivationPhase.ActivatingTarget target
                when string.Equals(pending.Target.EndpointId, endpointId, StringComparison.Ordinal) =>
                target.Evidence,
            ActivationPhase.RestoringSource restore
                when string.Equals(pending.Source.EndpointId, endpointId, StringComparison.Ordinal) =>
                restore.Evidence,
            ActivationPhase.SynchronizingPresentation sync
                when string.Equals(sync.Lease.EndpointId, endpointId, StringComparison.Ordinal) =>
                sync.Evidence,
            _ => null,
        };
    }

    /// <summary>
    /// Take the cells events that wait after a cells event, for one host
    /// paint. The batch first takes the leading cells events of
    /// <paramref name="carried"/>. When that queue is empty, it takes from the
    /// queue of <paramref name="reader"/>. The batch takes only cells events
    /// of visible panes, and it ends after a full or reanchor body. Any other
    /// event, and every event after it, goes to <paramref name="carried"/> in
    /// order, so the render loop runs it next.
    /// </summary>
    internal static void TakeQueuedCells(
        AttachLiveState live,
        ControlPlaneClient reader,
        List<TerminalRenderCellsPayload> batch,
        Queue<(JsonElement Event, ControlPlaneClient Reader)> carried)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(carried);
        if (live.PendingActivation is not null)
            return;
        var open = true;
        while (open && carried.TryPeek(out var head))
        {
            if (!ReferenceEquals(head.Reader, reader) || QueuedCellsOf(live, head.Event) is not { } next)
                return;
            carried.Dequeue();
            batch.Add(next);
            open = !(next.Full || next.Reanchor);
        }

        if (!open)
            return;
        foreach (var ev in reader.DrainPendingAllEvents())
        {
            if (open && QueuedCellsOf(live, ev) is { } cells)
            {
                batch.Add(cells);
                open = !(cells.Full || cells.Reanchor);
                continue;
            }

            open = false;
            carried.Enqueue((ev, reader));
        }
    }

    /// <summary>
    /// The cells body of <paramref name="ev"/> when the render loop would
    /// paint it at once: a terminal render cells event of a visible tiled
    /// pane that presentation does not drop. Else null.
    /// </summary>
    private static TerminalRenderCellsPayload? QueuedCellsOf(AttachLiveState live, JsonElement ev)
    {
        if (!TryGetEventType(ev, out var type, out var payload)
            || !string.Equals(type, ProtocolEventTypes.TerminalRender, StringComparison.Ordinal)
            || AttachEndpointActivationPump.ShouldDropFrozenPresentationEvent(live, type)
            || IsPopupRenderPayload(payload)
            || !payload.TryGetProperty("pane_id", out var pid)
            || !IsVisiblePane(live, pid.GetString())
            || !payload.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String
            || kind.GetString() != TerminalRenderCellsPayload.KindCells)
        {
            return null;
        }

        var cells = DeserializePayload(payload, ProtocolJsonContext.Default.TerminalRenderCellsPayload);
        if (cells is not null)
            TraceAttachEventReceived(live, type, payload);
        return cells;
    }

    internal static bool TryWriteCells(
        UnixRawTerminal tty,
        AttachLiveState live,
        TerminalRenderCellsPayload cells,
        bool reanchorBeforePaint)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(cells);

        return live.WithFramePaint(() => TryWriteCellsUnlocked(tty, live, [cells], reanchorBeforePaint)[0]);
    }

    /// <summary>
    /// Apply each cells delta in order, then compose and write the host
    /// once. Each delta keeps its generation and occupant checks and its
    /// reanchor handling. The input acks and the applied generations of all
    /// deltas commit after the one accepted write. Returns, for each delta,
    /// true when the host shows it.
    /// </summary>
    internal static bool[] TryWriteCellsBatch(
        UnixRawTerminal tty,
        AttachLiveState live,
        IReadOnlyList<TerminalRenderCellsPayload> batch,
        bool reanchorBeforePaint)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
            return [];
        return live.WithFramePaint(() => TryWriteCellsUnlocked(tty, live, batch, reanchorBeforePaint));
    }

    private static bool[] TryWriteCellsUnlocked(
        UnixRawTerminal tty,
        AttachLiveState live,
        IReadOnlyList<TerminalRenderCellsPayload> batch,
        bool reanchorBeforePaint)
    {
        var results = new bool[batch.Count];
        var context = new CellsBatchContext();
        var patch = new HostPatchRequest();
        var applied = new List<(int Index, AppliedCells Cells)>(batch.Count);
        for (var i = 0; i < batch.Count; i++)
        {
            // The apply can change key engine state: the copy mode snapshot
            // and the chrome geometry. Hold the key engine lock for the
            // apply only, not for the compose and the write.
            var index = i;
            if (live.WithEngine(() => ApplyCellsDelta(tty, live, batch[index], reanchorBeforePaint, context, patch)) is { } one)
                applied.Add((i, one));
        }

        if (applied.Count == 0)
        {
            CloseAckOnlyKeys(live, context, composed: false);
            // An ack-only body can confirm or delete predictions with no
            // frame to paint.
            if (context.PredictionChanged)
                PaintPrediction(tty, live);
            ArmPredictionTimer(tty, live);
            return results;
        }

        var last = applied[^1].Cells;
        var composed = WriteHostComposed(
            tty,
            live,
            last.Frame,
            afterAccepted: () => CommitAppliedCells(tty, live, applied, context),
            patch: patch);
        if (!composed)
            RestorePriorPaneFrames(live, context);
        CloseAckOnlyKeys(live, context, composed);
        ArmPredictionTimer(tty, live);
        foreach (var (index, one) in applied)
        {
            results[index] = composed;
            var cells = one.Cells;
            if (cells.Full || cells.Reanchor || !composed)
            {
                NoteFrameDecision(
                    live,
                    one.PaneId,
                    composed ? ProcessLogEvents.OutcomeApplied : ProcessLogEvents.OutcomeDropped,
                    composed
                        ? "admitted " + cells.GridCols + "x" + cells.GridRows + one.LocalSize
                        : "compose");
            }
        }

        return results;
    }

    /// <summary>
    /// The state of one cells batch before its write commits: the applied
    /// generation and the alternate-screen state of each pane after the
    /// deltas so far, and the panes that a later delta rejected.
    /// </summary>
    private sealed class CellsBatchContext
    {
        public Dictionary<string, (long Generation, int Occupant)> Applied { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, bool> Alt { get; } = new(StringComparer.Ordinal);

        /// <summary>Panes whose newest delta in this batch was dropped.</summary>
        public HashSet<string> Rejected { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The stored frame of each pane before the first frame of this batch
        /// went into storage. Null when the pane had no frame.
        /// </summary>
        public Dictionary<string, AssembledSnapshot?> Prior { get; } = new(StringComparer.Ordinal);

        /// <summary>The last frame of each pane that this batch stored.</summary>
        public Dictionary<string, AssembledSnapshot> Stored { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The cells that each frame of this batch overwrote in a storage that
        /// it reused, in apply order. A refused write copies them back.
        /// </summary>
        public Dictionary<string, List<PaneCellsUndo>> Undo { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// For each pane, the grid of the frame before this batch and the
        /// separate grid that a body of this batch unpacked into in its place.
        /// After the accepted write, the first grid is the spare grid of the
        /// pane. After a refused write, the second grid is.
        /// </summary>
        public Dictionary<string, (AssembledCell[] Prior, AssembledCell[] Taken)> Swapped { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The grid that <see cref="VtCellUnpacker.Apply"/> writes
        /// <paramref name="cells"/> into, or null to write into the storage of
        /// <paramref name="previous"/>. Call it after
        /// <see cref="Prior"/> has the frame of the pane before this batch.
        /// <para>
        /// A refused write puts back only the frame from before this batch.
        /// A full body and a row shift change every row. When such a body
        /// would write into the grid of that frame, it takes a separate grid
        /// (the spare grid of the pane) and the grid of that frame does not
        /// change. Thus no undo copy of the whole grid is necessary.
        /// </para>
        /// </summary>
        public AssembledCell[]? SeparateGrid(
            AttachLiveState live,
            string paneId,
            AssembledSnapshot? previous,
            TerminalRenderCellsPayload cells)
        {
            if (!(cells.Full || cells.Shift != 0)
                || !WritesPriorGrid(paneId, previous, cells, out var storage))
            {
                return null;
            }

            var taken = live.TakeSpareGrid(paneId, storage.Length, storage);
            Swapped[paneId] = (storage, taken);
            return taken;
        }

        /// <summary>
        /// Before <see cref="VtCellUnpacker.Apply"/> writes the rows of a delta
        /// <paramref name="cells"/> into the storage of <paramref name="previous"/>,
        /// keep the cells that it overwrites. Only the grid of the frame from
        /// before this batch needs an undo: a refused write puts back only
        /// that frame. A full body and a row shift take a separate grid (see
        /// <see cref="SeparateGrid"/>). When the size changes, the unpacker
        /// takes a new storage and the old cells stay as they are.
        /// </summary>
        public void KeepOverwritten(string paneId, AssembledSnapshot? previous, TerminalRenderCellsPayload cells)
        {
            if (cells.Full || cells.Shift != 0 || !WritesPriorGrid(paneId, previous, cells, out var storage))
                return;
            var cols = Math.Max(1, cells.GridCols);
            var rows = Math.Max(1, cells.GridRows);
            if (!Undo.TryGetValue(paneId, out var list))
            {
                list = [];
                Undo[paneId] = list;
            }

            if (cells.Rows is not { } packed)
                return;
            foreach (var row in packed)
            {
                if ((uint)row.I >= (uint)rows)
                    continue;
                list.Add(new PaneCellsUndo(storage, row.I * cols, storage.AsSpan(row.I * cols, cols).ToArray()));
            }
        }

        /// <summary>
        /// True when the unpacker writes <paramref name="cells"/> into the
        /// grid of the frame of <paramref name="paneId"/> from before this
        /// batch: <paramref name="previous"/> uses that grid and has the size
        /// of the body.
        /// </summary>
        private bool WritesPriorGrid(
            string paneId,
            AssembledSnapshot? previous,
            TerminalRenderCellsPayload cells,
            out AssembledCell[] storage)
        {
            storage = [];
            if (previous?.Storage is not { Length: > 0 } grid
                || !Prior.TryGetValue(paneId, out var prior)
                || prior is null
                || !ReferenceEquals(prior.Storage, grid))
            {
                return false;
            }

            var cols = Math.Max(1, cells.GridCols);
            var rows = Math.Max(1, cells.GridRows);
            if (previous.Cols != cols || previous.Rows != rows || grid.Length != cols * rows)
                return false;
            storage = grid;
            return true;
        }

        public (long Generation, int Occupant) LastApplied(AttachLiveState live, string paneId) =>
            Applied.TryGetValue(paneId, out var applied) ? applied : live.LastAppliedBlit(paneId);

        public bool WasAlt(AttachLiveState live, string paneId) =>
            Alt.TryGetValue(paneId, out var alt) ? alt : live.GetPaneAlt(paneId);

        /// <summary>
        /// The ack-only bodies of this batch that matched the generation of
        /// their pane at their place in the batch. <c>NeedsWrite</c> is true
        /// when that generation came in this batch, so the host shows it only
        /// after the write of the batch.
        /// </summary>
        public List<(string PaneId, long InputAck, bool NeedsWrite)> AckOnly { get; } = [];

        /// <summary>True when a body of this batch changed the shown predictions.</summary>
        public bool PredictionChanged { get; set; }
    }

    /// <summary>
    /// An ack-only body confirms the ack and nothing else. It closes the keys
    /// at or below its ack only when the host shows the frame that the mux
    /// sent last: at its place in the batch, the generation of the pane was
    /// the generation of the body. When that frame came in the same batch,
    /// the keys close only after the host accepted the write.
    /// </summary>
    private static void CloseAckOnlyKeys(AttachLiveState live, CellsBatchContext context, bool composed)
    {
        foreach (var (paneId, inputAck, needsWrite) in context.AckOnly)
        {
            if (!needsWrite || composed)
                live.Latency.NoteInputAck(paneId, inputAck);
        }
    }

    /// <summary>
    /// The host refused the write of a cells batch, so no frame of the batch
    /// is applied. Put back the stored frame of each pane from before the
    /// batch. The stored frame then has the generation of the last applied
    /// frame of the pane, as the applied generation does.
    /// <para>
    /// The unpacker writes the rows of a delta into the storage of the
    /// previous frame. The undo copies put those rows back. A full body and a
    /// row shift took a separate grid, so the grid of the restored frame did
    /// not change. The next delta of the pane does not chain, so the client
    /// asks the mux for a full frame, and that frame replaces the cells.
    /// </para>
    /// </summary>
    private static void RestorePriorPaneFrames(AttachLiveState live, CellsBatchContext context)
    {
        foreach (var (paneId, prior) in context.Prior)
        {
            if (context.Stored.TryGetValue(paneId, out var stored))
            {
                context.Undo.TryGetValue(paneId, out var undo);
                // The frame before the batch did not change its grid when a
                // body took a separate grid. That grid is free again.
                if (live.TryRestorePaneFrame(paneId, stored, prior, undo)
                    && context.Swapped.TryGetValue(paneId, out var swapped))
                {
                    live.KeepSpareGrid(paneId, swapped.Taken);
                }
            }

            // The chain of the pane is broken: the host does not show the
            // frames of this batch, and the mux chains its next delta on
            // them. Ask for a full frame now, also when no later delta
            // comes, and also when this batch held the full frame of an
            // earlier request. A second refusal in a row waits for the
            // resend time.
            live.ChainReanchors.Reopen(paneId, live.Time.GetUtcNow());
        }
    }

    /// <summary>
    /// The cells at <paramref name="Offset"/> of <paramref name="Storage"/>
    /// before a cells body overwrote them.
    /// </summary>
    internal sealed record PaneCellsUndo(AssembledCell[] Storage, int Offset, AssembledCell[] Cells)
    {
        public void Apply() => Array.Copy(Cells, 0, Storage, Offset, Cells.Length);
    }

    /// <summary>One applied cells delta that waits for the host write.</summary>
    private sealed record AppliedCells(
        string PaneId,
        TerminalRenderCellsPayload Cells,
        AssembledSnapshot Frame,
        long? ClosesKeysThrough,
        string LocalSize);

    /// <summary>
    /// After the one accepted write: each pane commits its newest frame, and
    /// each delta stores its applied generation and closes its keys, in order.
    /// </summary>
    private static void CommitAppliedCells(
        UnixRawTerminal tty,
        AttachLiveState live,
        List<(int Index, AppliedCells Cells)> applied,
        CellsBatchContext context)
    {
        for (var i = 0; i < applied.Count; i++)
        {
            var one = applied[i].Cells;
            var newest = true;
            for (var j = i + 1; j < applied.Count; j++)
            {
                if (string.Equals(applied[j].Cells.PaneId, one.PaneId, StringComparison.Ordinal))
                {
                    newest = false;
                    break;
                }
            }

            // An older frame shares storage with the newest frame of its pane.
            // Commit only the newest frame, so the stored frame keeps the newest
            // generation.
            if (newest)
                CommitAcceptedPaneFrame(tty, live, one.Frame);
            if (!context.Rejected.Contains(one.PaneId))
            {
                live.StoreAppliedBlit(one.PaneId, one.Cells.Generation, one.Cells.OccupantGeneration);
                // The host shows a full or reanchor frame of the pane: its
                // open reanchor request is done.
                if (one.Cells.Full || one.Cells.Reanchor)
                    live.ChainReanchors.Clear(one.PaneId);
            }
            // The host accepted the write: the keys that this frame acks
            // are on the screen now.
            if (one.ClosesKeysThrough is { } ack)
                live.Latency.NoteInputAck(one.PaneId, ack);
        }

        // The host shows the frames of this batch. No refused write can put
        // back a frame from before it, so its grid is free for the next body
        // that rewrites every row.
        foreach (var (paneId, (prior, _)) in context.Swapped)
            live.KeepSpareGrid(paneId, prior);
    }

    /// <summary>
    /// Check one cells delta and apply it to the stored pane frame. Returns
    /// null when the delta is dropped. Adds the changed rows of the delta, or
    /// the reason that a patch is not safe, to <paramref name="patch"/>.
    /// </summary>
    private static AppliedCells? ApplyCellsDelta(
        UnixRawTerminal tty,
        AttachLiveState live,
        TerminalRenderCellsPayload cells,
        bool reanchorBeforePaint,
        CellsBatchContext context,
        HostPatchRequest patch)
    {
        var paneId = cells.PaneId ?? live.PaneId;
        if (string.IsNullOrWhiteSpace(paneId))
            return null;

        // An ack-only body is not a frame. It does not chain, reset or
        // apply: the pane keeps its frame and its modes.
        if (cells.AckOnly)
        {
            // The ack belongs to the frame that the mux sent last. The stored
            // frame is that frame only when the generations match here.
            if (cells.InputAck is { } ackOnly
                && context.LastApplied(live, paneId).Generation == cells.Generation)
            {
                var needsWrite = live.LastAppliedBlit(paneId).Generation != cells.Generation;
                context.AckOnly.Add((paneId, ackOnly, needsWrite));
                if (live.TryGetPaneFrame(paneId, out var shown)
                    && shown is not null
                    && live.Prediction.NoteAck(paneId, ackOnly, shown))
                {
                    context.PredictionChanged = true;
                }
            }

            return null;
        }

        var applied = context.LastApplied(live, paneId);
        var occupantOk = cells.OccupantGeneration <= 0
            || applied.Occupant == 0
            || cells.OccupantGeneration == applied.Occupant;
        if (reanchorBeforePaint && (cells.Full || cells.Reanchor))
        {
            live.ResetAppliedBlit(paneId);
            context.Applied.Remove(paneId);
            applied = (0, 0);
        }

        if (!(cells.Full || cells.Reanchor)
            && (!occupantOk
                || cells.BaseGeneration <= 0
                || applied.Generation <= 0
                || cells.BaseGeneration != applied.Generation))
        {
            // A delta of the old size of a pane whose own resize waits: the
            // resize posts a full frame at the new size. A full frame at the
            // old size would be dropped too, so the pane does not ask for one.
            if (OldSizeWhileOwnResizeWaits(live, paneId, cells) is { } oldSize)
            {
                NoteFrameDecision(live, paneId, ProcessLogEvents.OutcomeDropped, oldSize);
                RejectCellsKeepHostBaseline(live, paneId);
                context.Applied[paneId] = (0, 0);
                context.Rejected.Add(paneId);
                return null;
            }

            NoteFrameDecision(
                live,
                paneId,
                ProcessLogEvents.OutcomeDropped,
                occupantOk ? "generation" : "occupant");
            RejectCellsKeepHostBaseline(live, paneId, chainBroken: true);
            context.Applied[paneId] = (0, 0);
            context.Rejected.Add(paneId);
            return null;
        }

        var localSize = "";
        var overlayBox = false;
        if (live.Chrome is { } chrome)
        {
            if (!TryPaneContent(chrome, paneId, out var box)
                && OverlayIsVisiblePane(live, paneId)
                && live.Overlay.Geometry is { } overlayGeo
                && overlayGeo.InnerCols > 0
                && overlayGeo.InnerRows > 0)
            {
                // buffer. Overlay inner is not a tiled chrome pane.
                box = new CellRect(
                    overlayGeo.InnerCol,
                    overlayGeo.InnerRow,
                    overlayGeo.InnerCols,
                    overlayGeo.InnerRows);
                overlayBox = true;
            }

            if (box.Cols <= 0 || box.Rows <= 0)
            {
                if (!TryPaneContent(chrome, paneId, out box))
                {
                    if (cells.Full || cells.Reanchor)
                    {
                        live.TryGetPaneFrame(paneId, out var retained);
                        ApplyCellsViewportOrigin(live, cells);
                        live.SetPaneFrame(VtCellUnpacker.Apply(cells, retained));
                    }

                    NoteFrameDecision(live, paneId, ProcessLogEvents.OutcomeDropped, "no_rect");
                    RejectCellsKeepHostBaseline(live, paneId);
                    context.Applied[paneId] = (0, 0);
                    context.Rejected.Add(paneId);
                    PaintChrome(tty, live);
                    return null;
                }

                overlayBox = false;
            }

            if (cells.GridCols > 0
                && cells.GridRows > 0
                && (cells.GridCols != box.Cols || cells.GridRows != box.Rows))
            {
                var mismatch = "size " + cells.GridCols + "x" + cells.GridRows + "!=" + box.Cols + "x" + box.Rows;
                if (AwaitsOwnPaneSize(live, paneId, box))
                {
                    NoteFrameDecision(live, paneId, ProcessLogEvents.OutcomeDropped, mismatch);
                    RejectCellsKeepHostBaseline(live, paneId);
                    context.Applied[paneId] = (0, 0);
                    context.Rejected.Add(paneId);
                    return null;
                }

                // Another attach owns the pane geometry, so the mux keeps the
                // pane at its size, not ours. Admit the frame: the compose clips
                // or pads it into the local rect. Dropping it would freeze this
                // view, and each reanchor would come back at the same size.
                localSize = " in " + box.Cols + "x" + box.Rows;
            }
        }

        live.TryGetPaneFrame(paneId, out var previous);
        if (!(cells.Full || cells.Reanchor) && previous is null)
        {
            NoteFrameDecision(live, paneId, ProcessLogEvents.OutcomeDropped, "no_baseline");
            RejectCellsKeepHostBaseline(live, paneId, chainBroken: true);
            context.Applied[paneId] = (0, 0);
            context.Rejected.Add(paneId);
            return null;
        }

        // A row shift moves the kept grid of the same size. Without that
        // grid, the rows of the delta do not make the frame. A shift of the
        // grid height or more keeps no row. Compare each sign: the absolute
        // value of int.MinValue does not fit in an int.
        if (!cells.Full
            && cells.Shift != 0
            && (previous is null
                || previous.Cols != cells.GridCols
                || previous.Rows != cells.GridRows
                || cells.Shift >= cells.GridRows
                || cells.Shift <= -cells.GridRows))
        {
            NoteFrameDecision(live, paneId, ProcessLogEvents.OutcomeDropped, "shift");
            RejectCellsKeepHostBaseline(live, paneId, chainBroken: true);
            context.Applied[paneId] = (0, 0);
            context.Rejected.Add(paneId);
            return null;
        }

        var hadScroll = live.TryGetPaneScrollMetrics(paneId, out var scrollOffset, out var scrollMax, out var scrollRows);
        ApplyCellsViewportOrigin(live, cells);
        var hasScroll = live.TryGetPaneScrollMetrics(paneId, out var nextOffset, out var nextMax, out var nextRows);
        // Only a frame that changed cells or the cursor can show an echo.
        // A frame without a visible change does not close a key.
        var before = cells.InputAck is null ? null : MarkFrame(previous, cells);
        var patchPane = patch.For(paneId, previous);
        var applyStart = live.Latency.Now();
        // The first frame of the pane in this batch keeps the frame before
        // the batch, for a refused write.
        context.Prior.TryAdd(paneId, previous);
        var into = context.SeparateGrid(live, paneId, previous, cells);
        context.KeepOverwritten(paneId, previous, cells);
        var frame = VtCellUnpacker.Apply(cells, previous, into);
        live.Latency.RecordHop(AttachLatencyHop.Apply, applyStart);
        // Judge the predictions now: the next delta writes into this storage.
        if (live.Prediction.Reconcile(
                paneId,
                frame,
                cells.InputAck,
                // A full body that the mux sent only because it is smaller
                // than the delta is not a reanchor. It keeps the predictions.
                cells.Reanchor,
                PaneRunsAgent(live, paneId)))
        {
            context.PredictionChanged = true;
        }

        long? closesKeysThrough = cells.InputAck is { } inputAck
            && FrameShowsChange(before, frame, cells)
            ? inputAck
            : null;
        if (OverlayIsVisiblePane(live, paneId) && frame.IngestFull)
            live.Overlay.TryAcceptReveal(frame);
        // History overlay stamps the same inner_rect (src/pane/terminal.rs:2170).
        // Keep last_frame; CSI 2J is first-frame only (render_ansi.rs:91).
        // revisions match, then compose reads it. Do not wait for a host write.
        // only after a successful write (src/client/mod.rs:1698-1700).
        NoteCopySnapshot(live, frame);
        var wasAlt = context.WasAlt(live, paneId);
        live.SetPaneFrame(frame);
        context.Stored[paneId] = frame;
        // Alternate screen has no scrollbar gutter, so the content width changes.
        // Sync before compose so this frame paints without the old gutter, and
        // mark the pane resize now. A later chrome recompute must not find the
        // new width first and leave the mux at the old size.
        if (wasAlt != frame.IsAlternateScreen)
            SyncPaneChromeGeometry(live, tty);
        context.Alt[paneId] = frame.IsAlternateScreen;
        context.Applied[paneId] = (cells.Generation, cells.OccupantGeneration);
        context.Rejected.Remove(paneId);

        // The patch path stamps only the changed rows. Each check below makes
        // the paint a full compose, as herdr fast_path_blocker does.
        patchPane.Frame = frame;
        var restatesGrid = RestatesKeptGrid(cells, previous, frame);
        patchPane.Blocker ??= (cells.Full || cells.Reanchor) && !restatesGrid ? HostPatchFallback.FullFrame
            : localSize.Length > 0 ? HostPatchFallback.SizeMismatch
            : overlayBox || OverlayIsVisiblePane(live, paneId) ? HostPatchFallback.Overlay
            : wasAlt != frame.IsAlternateScreen ? HostPatchFallback.AltScreen
            : cells.ViewportOrigin > 0
                || hadScroll != hasScroll
                || scrollOffset != nextOffset
                || scrollMax != nextMax
                || scrollRows != nextRows
                ? HostPatchFallback.Viewport
                : null;
        if (restatesGrid || (!cells.Full && cells.Shift != 0))
        {
            // The shift moved every row of the pane. A full body blank-fills
            // the grid and leaves out the blank rows, so each row can change.
            for (var row = 0; row < frame.Rows; row++)
                patchPane.Rows.Add(row);
        }
        else if (cells.Rows is { } rows)
        {
            foreach (var row in rows)
            {
                if ((uint)row.I < (uint)frame.Rows)
                    patchPane.Rows.Add(row.I);
            }
        }

        return new AppliedCells(paneId, cells, frame, closesKeysThrough, localSize);
    }

    /// <summary>
    /// True when <paramref name="cells"/> is a full body that is not a
    /// reanchor and that restates the kept grid of the pane: the grid has the
    /// same size and the same occupant. The mux sends this body when it is
    /// smaller than the delta, for example when all rows of the pane change.
    /// The patch path can stamp all rows of the pane, as for a row shift. A
    /// reanchor, a new size, a new occupant or a pane with no kept grid stays
    /// a full compose. The other checks (alternate screen, viewport, overlay)
    /// apply as for each delta.
    /// </summary>
    private static bool RestatesKeptGrid(
        TerminalRenderCellsPayload cells,
        AssembledSnapshot? previous,
        AssembledSnapshot frame) =>
        cells.Full
        && !cells.Reanchor
        && previous is not null
        && previous.Cols == frame.Cols
        && previous.Rows == frame.Rows
        && previous.OccupantGeneration == frame.OccupantGeneration;

    /// <summary>
    /// What a pane frame showed before a body is applied: the cursor, the
    /// size, and a hash of the cells. The unpacker writes into the storage of
    /// the previous frame, so the mark is taken before the apply.
    /// </summary>
    internal readonly record struct PaneFrameMark(AssembledCursor Cursor, int Cols, int Rows, ulong Cells);

    /// <summary>
    /// Marks <paramref name="frame"/>. The cell hash is computed only for a
    /// full or reanchor body, because only that body needs it.
    /// </summary>
    internal static PaneFrameMark? MarkFrame(AssembledSnapshot? frame, TerminalRenderCellsPayload cells) =>
        frame is null
            ? null
            : new PaneFrameMark(
                frame.Cursor,
                frame.Cols,
                frame.Rows,
                cells.Full || cells.Reanchor ? CellsHash(frame) : 0);

    /// <summary>
    /// True when the applied frame shows a change against the mark of the
    /// frame before it: the cursor, or a cell. A delta lists only rows that
    /// differ from the baseline it chains on, so a delta row is a change. A
    /// full or reanchor body restates the grid, so its cells are compared.
    /// </summary>
    internal static bool FrameShowsChange(
        PaneFrameMark? before,
        AssembledSnapshot frame,
        TerminalRenderCellsPayload cells)
    {
        if (before is not { } mark || !mark.Cursor.Equals(frame.Cursor))
            return true;
        if (!(cells.Full || cells.Reanchor))
            return cells.Rows is { Count: > 0 } || cells.Shift != 0;
        return mark.Cols != frame.Cols || mark.Rows != frame.Rows || mark.Cells != CellsHash(frame);
    }

    private static ulong CellsHash(AssembledSnapshot frame)
    {
        var hash = 14695981039346656037UL;
        for (var row = 0; row < frame.Rows; row++)
        {
            for (var col = 0; col < frame.Cols; col++)
            {
                hash ^= (uint)frame.CellAt(col, row).GetHashCode();
                hash *= 1099511628211UL;
            }
        }

        return hash;
    }

    /// <summary>
    /// How long after this client's own <c>pane.resize</c> a frame at another
    /// size is treated as stale (sized before the resize landed) and dropped.
    /// </summary>
    internal static readonly TimeSpan OwnPaneResizeGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True while this client's own resize of the pane to <paramref name="box"/>
    /// should still land: it was sent recently and the mux did not decline it.
    /// </summary>
    private static bool AwaitsOwnPaneSize(AttachLiveState live, string paneId, CellRect box)
    {
        lock (live.ChromeStateGate)
        {
            if (!live.OwnPaneResizeSentAt.TryGetValue(paneId, out var sentAt)
                || live.Time.GetUtcNow() - sentAt >= OwnPaneResizeGrace)
            {
                return false;
            }

            return !(live.DeclinedPaneResizes.TryGetValue(paneId, out var declined)
                && declined.Cols == box.Cols
                && declined.Rows == box.Rows);
        }
    }

    /// <summary>
    /// The drop reason (<c>size AxB!=CxD</c>) when <paramref name="cells"/> has
    /// another size than the box of its tiled pane and this client's own
    /// resize of the pane to that box waits. Otherwise null.
    /// </summary>
    private static string? OldSizeWhileOwnResizeWaits(
        AttachLiveState live,
        string paneId,
        TerminalRenderCellsPayload cells)
    {
        if (cells.GridCols <= 0
            || cells.GridRows <= 0
            || live.Chrome is not { } chrome
            || !TryPaneContent(chrome, paneId, out var box)
            || box.Cols <= 0
            || box.Rows <= 0
            || (cells.GridCols == box.Cols && cells.GridRows == box.Rows)
            || !AwaitsOwnPaneSize(live, paneId, box))
        {
            return null;
        }

        return "size " + cells.GridCols + "x" + cells.GridRows + "!=" + box.Cols + "x" + box.Rows;
    }

    internal static void NoteSnapshotGeometryOwners(AttachLiveState live, JsonElement snapshot)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("panes", out var panes)
            || panes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        lock (live.ChromeStateGate)
        {
            foreach (var pane in panes.EnumerateArray())
            {
                var paneId = TryJsonString(pane, "pane_id");
                if (string.IsNullOrWhiteSpace(paneId)
                    || !pane.TryGetProperty("geometry_owner", out var owner)
                    || owner.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                live.PaneGeometryOwners[paneId] = owner.GetString() ?? "";
            }
        }
    }

    private static string FrameReasonKind(string reason)
    {
        var space = reason.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? reason : reason[..space];
    }

    private static void NoteFrameDecision(
        AttachLiveState live,
        string paneId,
        string outcome,
        string reason)
    {
        string? summary = null;
        long summaryCount = 0;
        long? dropCount = null;
        lock (live.FrameLogGate)
        {
            live.FrameDropNotices.TryGetValue(paneId, out var notice);
            if (string.Equals(outcome, ProcessLogEvents.OutcomeDropped, StringComparison.Ordinal))
            {
                // Compare the reason kind (size, generation, ...), not the full
                // text: a size mismatch that changes dimensions is still one kind.
                if (notice is not null
                    && string.Equals(FrameReasonKind(notice.Reason), FrameReasonKind(reason), StringComparison.Ordinal))
                {
                    notice.Suppressed++;
                    return;
                }

                if (notice is { Suppressed: > 0 })
                {
                    summary = "suppressed " + notice.Suppressed + " " + notice.Reason;
                    summaryCount = notice.Count;
                }

                live.FrameDropNotices[paneId] = new FrameDropNotice { Reason = reason };
                dropCount = 1;
            }
            else
            {
                if (notice is not null)
                {
                    if (notice.Suppressed > 0)
                    {
                        summary = "suppressed " + notice.Suppressed + " " + notice.Reason;
                        summaryCount = notice.Count;
                    }

                    live.FrameDropNotices.Remove(paneId);
                }
                else if (live.FrameAdmitNotices.TryGetValue(paneId, out var lastAdmit)
                    && string.Equals(lastAdmit, reason, StringComparison.Ordinal))
                {
                    // The same admit as the last one logged for this pane.
                    return;
                }

                live.FrameAdmitNotices[paneId] = reason;
            }
        }

        // The summary carries the number of drops in the row: the logged
        // drop and the suppressed ones.
        if (summary is not null)
        {
            AttachProcessLog.Observe(
                live.ProcessLog,
                live.SessionName,
                live.AttachClientId,
                paneId,
                ProcessLogEvents.OutcomeDropped,
                summary,
                summaryCount);
        }

        AttachProcessLog.Observe(
            live.ProcessLog,
            live.SessionName,
            live.AttachClientId,
            paneId,
            outcome,
            reason,
            dropCount);
    }

    /// <summary>
    /// The number of frames of <paramref name="paneId"/> that the client
    /// dropped in a row for one reason, or zero when its last frame applied.
    /// </summary>
    internal static long FrameDropsInRow(AttachLiveState live, string paneId)
    {
        ArgumentNullException.ThrowIfNull(live);
        lock (live.FrameLogGate)
            return live.FrameDropNotices.TryGetValue(paneId, out var notice) ? notice.Count : 0;
    }

    /// <summary>
    /// Drops a cells frame of <paramref name="paneId"/> and keeps the host
    /// baseline. With <paramref name="chainBroken"/>, the drop broke the
    /// generation chain of the pane: the client queues one reanchor request,
    /// because no later delta of the pane can apply until a full frame comes.
    /// A drop for the pane size or rectangle does not queue a request: a full
    /// frame at that size would be dropped again, and the resize brings a
    /// full frame.
    /// </summary>
    private static void RejectCellsKeepHostBaseline(AttachLiveState live, string paneId, bool chainBroken = false)
    {
        live.BlitReanchorPending = true;
        live.ResetAppliedBlit(paneId);
        if (chainBroken)
            live.ChainReanchors.Request(paneId, live.Time.GetUtcNow());
        // InvalidateChrome: RequestRepaint keeps last_frame (render_ansi.rs:91).
        live.HostEncoder.RequestRepaint();
    }

    private static void NoteOccupantGeneration(AttachLiveState live, JsonElement payload)
    {
        var paneId = TryJsonString(payload, "pane_id");
        if (paneId is null || !TryJsonInt32(payload, "occupant_generation", out var generation))
            return;
        live.NoteOccupantGeneration(paneId, generation);
        live.ResetAppliedBlit(paneId);
    }

    internal static void PaintStoredChromeFrames(
        UnixRawTerminal tty,
        AttachLiveState live,
        bool forceFullRedraw = false)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        if (!KeyEngine.SuppressesLiveRemap(live.Engine.PaintMode))
        {
            _ = live.TryCopyRestore(live.PaneId, out var frame, out var tail);
            RestoreChromeFrames(tty, live, frame, tail, forceFullRedraw);
            return;
        }

        WriteHostComposed(tty, live, requestRepaint: forceFullRedraw);
    }

    internal static void PaintAfterPopupClosed(UnixRawTerminal tty, AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        if (live.Chrome is not null)
            PaintStoredChromeFrames(tty, live, forceFullRedraw: true);
        else
            RestoreLastFrame(tty, live);
    }

    internal static void PaintChrome(
        UnixRawTerminal tty,
        AttachLiveState live,
        AssembledSnapshot? frame = null,
        bool paintPopupInnerSnapshot = false,
        bool requestRepaint = false)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        var (cols, rows) = NotifyGridSize(live, tty);
        live.Toasts = live.Notifications.Tick(cols, rows);
        live.Dispatcher.LastNotificationPaneId = live.Notifications.LastTargetPaneId;
        // The paint lock only. This body and the compose take the key
        // engine lock where they read or change key engine state.
        live.WithFramePaint(() =>
        {
            if (requestRepaint)
                live.HostEncoder.RequestRepaint();
            if (live.Chrome is { } sized && (sized.Cols != cols || sized.Rows != rows))
            {
                // A resize rewrote the whole display. The retained chrome frame
                // describes a grid that no longer exists.
                live.InvalidateChrome();
            }

            // A narrow layout can recompute the chrome here, and that
            // changes key engine state.
            live.WithEngine(() => PlaceToastsOnChrome(live, tty));
            // Compose a host frame even with a blank inner_rect. Do not
            // CUP-remap pane ANSI onto chrome. Chrome-off and overlays use
            // the same encoder (src/ui.rs:431-461).
            _ = paintPopupInnerSnapshot;
            // not every overlay Frame. WriteChangedCells walks every cell vs
            // last (render_ansi.rs:773-818).
            WriteHostComposed(tty, live, frame);
        });
    }

    internal static void ApplyOverlayPaint(
        UnixRawTerminal tty,
        AttachLiveState live,
        bool restoreFrame,
        bool noteSnapshotPanes = false)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        AssembledSnapshot? frame = null;
        byte[] tail = [];
        live.WithPaint(() =>
        {
            if (restoreFrame)
            {
                if (!noteSnapshotPanes)
                    noteSnapshotPanes = KeyEngine.SuppressesLiveRemap(live.Engine.PaintMode);
                _ = live.TryCopyRestore(live.PaneId, out frame, out tail);
                SeedRestoreCursor(live, frame);
            }

            SyncChromePaintMode(live);
        });

        // Overlay ticks diff through WriteChangedCells. Restore still force-
        // writes overlay-covered cells without CSI 2J (render_ansi.rs:91)
        // because PaintMode stays on the overlay until afterAccepted.
        WriteHostComposed(
            tty,
            live,
            afterAccepted: restoreFrame
                ? () =>
                {
                    live.Engine.CommitPaintMode();
                    live.AfterSnapshotBeforeTail?.Invoke();
                    RemapRestoredTail(tty, live, tail);
                }
        : null,
            recoverStale: restoreFrame,
            noteSnapshotPanes: restoreFrame && noteSnapshotPanes,
            requestRepaint: restoreFrame
                && live.Engine.PaintMode is not AttachClientMode.Terminal);
    }

    internal static async Task SeedCopyModeAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (!live.TryGetPaneFrame(live.PaneId, out var snap) || snap is null)
            snap = live.LastComplete;

        var snapAtStart = snap;
        var paneAlt = !string.IsNullOrWhiteSpace(live.PaneId) && live.GetPaneAlt(live.PaneId);
        if (!paneAlt && snap is { IsAlternateScreen: true })
            snap = null;
        live.BeginCopySeedCapture();
        string? recent = null;
        if (paneAlt)
        {
            var visible = await ReadVisibleCopySnapshotAsync(live, control, ct)
                .ConfigureAwait(false);
            if (visible is not null)
                snap = ForceCopyScreen(visible, "alt");
            else if (snap is { IsAlternateScreen: true })
                snap = ForceCopyScreen(snap, "alt");
            else
                snap = null;
        }
        else if (live.HasLivePane)
        {
            try
            {
                var cols = snap is { Cols: > 0 } ? snap.Cols : 80;
                var result = await control.CallAsync(
                        ProtocolMethods.PaneRead,
                        new JsonObject
                        {
                            ["pane_id"] = live.PaneId,
                            ["source"] = "recent",
                            ["lines"] = CopyModeBuffer.ResolveSeedRecentLines(cols),
                        },
                        ct)
                    .ConfigureAwait(false);
                if (result.ValueKind == JsonValueKind.Object
                    && result.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    recent = text.GetString();
                }
            }
            catch (ControlPlaneException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
                // Oversized NDJSON (or a dropped socket) must not tear attach.
                // Visible snapshot + live tail remain the seed.
            }
        }

        live.WithPaint(() =>
        {
            if (live.TryGetPaneFrame(live.PaneId, out var latest) && latest is not null
                && latest.IsAlternateScreen == paneAlt)
            {
                snap = latest;
            }
            else if (!paneAlt
                && live.LastComplete is { IsAlternateScreen: false } main)
            {
                snap = main;
            }

            var viewport = snap?.Rows ?? 24;
            if (live.Chrome is { } chrome && TryPaneContent(chrome, live.PaneId, out var box))
                viewport = box.Rows;

            var pending = live.TakeCopySeedPending();
            var deferred = live.CopyDeferredLive();
            live.Engine.Copy.Seed(snap, recent, viewport);
            if (snap is not null
                && !ReferenceEquals(snap, snapAtStart)
                && live.Engine.Copy.IsSeeded)
            {
                live.Engine.Copy.MergeSnapshot(snap);
            }

            live.AfterCopySeedBeforeReplay?.Invoke();
            if (!live.Engine.Copy.IsAltScreen)
            {
                live.Engine.Copy.AppendUnseenLive(deferred);
                live.Engine.Copy.AppendUnseenLive(pending);
            }
        });
    }

    internal static async Task RequestCopyRefreshSnapshotAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(tty);
        if (!live.TryBeginCopyRefresh())
        {
            live.MarkCopyRefreshPending();
            return;
        }

        try
        {
            do
            {
                live.ClearCopyRefreshPending();
                AssembledSnapshot? snap = null;
                if (live.CopyRefreshHandler is not null)
                    snap = await live.CopyRefreshHandler(ct).ConfigureAwait(false);
                else
                    snap = await ReadVisibleCopySnapshotAsync(live, control, ct).ConfigureAwait(false);

                if (snap is not null)
                {
                    live.WithPaint(() =>
                    {
                        if (live.Engine.PaintMode is AttachClientMode.Copy)
                            live.Engine.Copy.MergeSnapshot(snap);
                    });
                    WriteHostComposed(tty, live);
                }
            }
            while (live.ConsumeCopyRefreshPending());
        }
        finally
        {
            live.EndCopyRefresh();
            if (live.ConsumeCopyRefreshPending())
            {
                await RequestCopyRefreshSnapshotAsync(live, control, tty, ct)
                    .ConfigureAwait(false);
            }
        }

        await ReseedCopyIfNeededAsync(live, control, tty, controlGate: null, ct)
            .ConfigureAwait(false);
    }

    internal static async Task<AssembledSnapshot?> ReadVisibleCopySnapshotAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (!live.HasLivePane)
            return null;

        try
        {
            var result = await control.CallAsync(
                    ProtocolMethods.PaneRead,
                    new JsonObject
                    {
                        ["pane_id"] = live.PaneId,
                        ["source"] = "visible",
                    },
                    ct)
                .ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var visible = text.GetString();
            if (visible is null)
                return null;
            return SnapshotFromVisibleText(live, visible);
        }
        catch (ControlPlaneException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static AssembledSnapshot SnapshotFromVisibleText(AttachLiveState live, string visible)
    {
        ArgumentNullException.ThrowIfNull(live);
        var template = live.LastComplete;
        var cols = Math.Max(1, template?.Cols ?? 80);
        var paneId = string.IsNullOrWhiteSpace(live.PaneId) ? template?.PaneId ?? "p1" : live.PaneId;
        var screen = live.GetPaneAlt(paneId) ? "alt" : "main";
        var rowsText = visible.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var rows = Math.Max(template?.Rows ?? rowsText.Length, rowsText.Length);
        rows = Math.Max(1, rows);
        var cells = new List<IReadOnlyList<AssembledCell>>(rows);
        for (var r = 0; r < rows; r++)
        {
            var raw = r < rowsText.Length ? rowsText[r] : "";
            if (raw.Length > cols)
                raw = raw[..cols];
            var padded = raw.PadRight(cols);
            var line = new List<AssembledCell>(cols);
            foreach (var ch in padded)
                line.Add(new AssembledCell(ch.ToString(), 1, false, AssembledStyle.Default));
            cells.Add(line);
        }

        return new AssembledSnapshot(
            paneId,
            cols,
            rows,
            template?.Provider ?? "ghostty",
            screen,
            cells,
            template?.Snapshot ?? default,
            template?.Cursor ?? AssembledCursor.Default);
    }

    internal static async Task ReseedCopyIfNeededAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal tty,
        SemaphoreSlim? controlGate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(tty);
        if (live.Engine.PaintMode is not AttachClientMode.Copy)
            return;
        if (!live.Engine.Copy.ConsumeNeedsRecentReseed())
            return;

        if (controlGate is not null)
            await controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SeedCopyModeAsync(live, control, ct).ConfigureAwait(false);
        }
        finally
        {
            controlGate?.Release();
        }

        ApplyOverlayPaint(tty, live, restoreFrame: false);
    }

    internal static bool ShouldRequestCopySnapshot(AttachLiveState live, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Engine.Copy.IsAltScreen)
            return true;

        var wasAlt = false;
        ClientOverlayCarry? carry = null;
        if (!string.IsNullOrWhiteSpace(live.PaneId))
        {
            wasAlt = live.GetPaneAlt(live.PaneId);
            carry = live.GetOverlayCarry(live.PaneId);
        }

        var plan = ClientOverlay.PlanLivePaint(overlayActive: true, wasAlt, bytes, carry);
        if (!string.IsNullOrWhiteSpace(live.PaneId))
        {
            var wasPaneAlt = live.GetPaneAlt(live.PaneId);
            live.NotePaneAlt(live.PaneId, plan.NowAlt);
            if (wasPaneAlt != plan.NowAlt)
                SyncPaneChromeGeometry(live, tty: null);
        }
        ApplyCopyScreenTransition(live, plan.NowAlt, plan.LeftPaneAlt);
        return plan.NowAlt || plan.LeftPaneAlt;
    }

    private static bool TrackCopyLiveTransition(
        UnixRawTerminal tty,
        AttachLiveState live,
        string paneId,
        ReadOnlySpan<byte> bytes)
    {
        var wasAlt = !string.IsNullOrWhiteSpace(paneId) && live.GetPaneAlt(paneId);
        var plan = PlanPaneLivePaint(tty, live, paneId, bytes);
        ApplyCopyScreenTransition(live, plan.NowAlt, plan.LeftPaneAlt);
        return plan.NowAlt || plan.LeftPaneAlt || live.Engine.Copy.IsAltScreen || wasAlt && !plan.NowAlt;
    }

    private static void ApplyCopyScreenTransition(AttachLiveState live, bool nowAlt, bool leftAlt)
    {
        if (nowAlt)
            live.Engine.Copy.NoteLiveScreen(true);
        if (!leftAlt)
            return;

        live.Engine.Copy.NoteLiveScreen(false);
        if (live.LastComplete is { IsAlternateScreen: false } main)
            live.Engine.Copy.MergeSnapshot(main);
    }

    internal static bool ShouldSeedCopyOnEnter(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return live.Engine.PaintMode is not AttachClientMode.Copy || !live.Engine.Copy.IsSeeded;
    }

    internal static bool EntersCopyMode(IEnumerable<KeyEngineEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var ev in events)
        {
            if (ev.Kind is KeyEngineEventKind.EnterMode && ev.Mode is AttachClientMode.Copy)
                return true;
        }

        return false;
    }

    private static AssembledSnapshot ForceCopyScreen(AssembledSnapshot snap, string screen) =>
        snap with { ActiveScreen = screen };

    internal static void PaintCopyContent(UnixRawTerminal tty, AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        if (!live.Engine.Copy.IsSeeded)
            return;

        live.GetLiveCursor(live.Chrome?.FocusedPaneId ?? live.PaneId).ClearDrawn();
        WriteHostComposed(tty, live);
    }

    internal static void RestoreLastFrame(UnixRawTerminal tty, AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        _ = live.TryCopyRestore(live.PaneId, out var frame, out var tail);
        RestoreChromeFrames(tty, live, frame, tail, forceFullRedraw: true);
    }

    private static void RestoreChromeFrames(
        UnixRawTerminal tty,
        AttachLiveState live,
        AssembledSnapshot? frame,
        byte[] tail,
        bool forceFullRedraw = false)
    {
        SeedRestoreCursor(live, frame);
        WriteHostComposed(
            tty,
            live,
            generationFrame: frame,
            afterAccepted: () =>
            {
                live.AfterSnapshotBeforeTail?.Invoke();
                RemapRestoredTail(tty, live, tail);
            },
            recoverStale: true,
            requestRepaint: forceFullRedraw);
    }

    private static void RemapRestoredTail(
        UnixRawTerminal tty,
        AttachLiveState live,
        byte[] tail)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        if (tail.Length == 0)
            return;
        // Restore already composed the host frame in WriteHostComposed.
        // Do not write a deferred tail as pane-relative ANSI, chrome on
        // or chrome off. Wait for structured cells.
    }

    private static void SeedRestoreCursor(AttachLiveState live, AssembledSnapshot? frame)
    {
        var source = frame;
        if (source is null)
            live.TryGetPaneFrame(live.PaneId, out source);
        if (source is null)
            return;
        var cursor = source.Cursor ?? AssembledCursor.Default;
        live.SeedLiveCursor(
            source.PaneId,
            cursor.Col,
            cursor.Row,
            cursor.HasCursor && cursor.Visible);
    }

    internal static string FormatStatus(ControlPlaneException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return FormatStatus(ex.Message);
    }

    internal static string FormatStatus(InvalidOperationException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return FormatStatus(ex.Message);
    }

    internal static string FormatStatus(string? message)
    {
        var msg = string.IsNullOrWhiteSpace(message) ? "request failed" : message;
        return msg.Length <= 60 ? msg : msg[..60];
    }

    /// <summary>
    /// Clears the entry of one owner after its transient action ends. A
    /// pending command overlay restore keeps the entry.
    /// </summary>
    internal static void ClearTransientStatusError(AttachLiveState live, StatusOwner owner)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Dispatcher.HasPendingCommandOverlayRestore)
            return;
        live.Status.Clear(owner);
    }

    /// <summary>
    /// A key or a mouse event dismisses the error line that the person sees.
    /// It clears the entry of the owner of that error. Returns true when the
    /// visible entry changed, so the caller repaints. An older error from
    /// another owner can show after the clear.
    /// </summary>
    internal static bool DismissStatusErrorOnInput(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Status.TopError is not { } visible)
            return false;
        ClearTransientStatusError(live, visible.Owner);
        return live.Status.TopError != visible;
    }

    internal static async Task ApplyControlEventsAsync(
        IEnumerable<JsonElement> events,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct,
        CancellationTokenSource? linked = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);

        var previousToasts = live.Toasts;
        var hadNotify = false;
        var reloadApplied = false;
        var frozenPresentation = live.PendingActivation is not null || live.PresentationFrozen;
        List<JsonElement>? agentPatched = null;
        List<JsonElement>? structural = null;
        foreach (var ev in events)
        {
            if (ct.IsCancellationRequested)
                return;
            if (!TryGetEventType(ev, out var notifyType, out var notifyPayload))
            {
                NoteReconnectEvent(live, ev);
                if (ApplyNotifyEvent(live, ev, tty, linked))
                    hadNotify = true;
                continue;
            }

            TraceAttachEventReceived(live, notifyType, notifyPayload);
            if (AttachEndpointActivationPump.ShouldDropFrozenPresentationEvent(live, notifyType))
            {
                TraceAttachEventOutcome(
                    live, notifyType, notifyPayload, ProcessLogEvents.OutcomeDropped, "presentation_frozen");
                continue;
            }

            if (TryConsumeLeaseChanged(live, notifyType, notifyPayload))
                continue;

            NoteReconnectEvent(live, ev);
            if (string.Equals(notifyType, ProtocolEventTypes.PaneAgentStatusChanged, StringComparison.Ordinal))
            {
                // Patch every status in the batch. One frame after the loop shows them all.
                if (ApplyNotifyEvent(live, ev, tty, linked, paint: false))
                {
                    hadNotify = true;
                    (agentPatched ??= []).Add(ev);
                }
            }
            else
            {
                // A handler can apply a change and still return false (window title).
                if (ApplyNotifyEvent(live, ev, tty, linked))
                    hadNotify = true;
                if (IsLiveNotifyEvent(notifyType))
                    TraceAttachEventOutcome(live, notifyType, notifyPayload, ProcessLogEvents.OutcomeApplied);
            }

            if (IsAppliedConfigReloaded(ev))
                reloadApplied = true;
            if (IsStructuralPresentationEvent(notifyType))
                (structural ??= []).Add(ev);
        }

        if (live.InputDetachRequested || live.DetachRequested)
        {
            try { linked?.Cancel(); }
            catch (ObjectDisposedException)
            {
                // session already leaving
            }
        }

        var (cols, rows) = NotifyGridSize(live, tty);
        live.Dispatcher.LastNotificationPaneId = live.Notifications.LastTargetPaneId;
        live.Toasts = live.Notifications.Tick(cols, rows);
        if (reloadApplied)
        {
            if (ct.IsCancellationRequested)
                return;
            try
            {
                await ResizeVisiblePanesAsync(control, live, ct).ConfigureAwait(false);
            }
            catch (ControlPlaneException ex)
            {
                live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
            }
            catch (InvalidOperationException ex)
            {
                live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
            }

            if (live.PopupOpen)
            {
                await ResizeOpenPopupAsync(control, live, tty, ct).ConfigureAwait(false);
            }
        }

        if (ct.IsCancellationRequested)
            return;

        if (agentPatched is not null && tty is not null && live.ChromeEnabled && live.ChromeSeed is not null)
            SyncPaneChromeGeometry(live, tty);
        // The heartbeat drains this path, so an idle pane still sends a resize
        // that this batch marked, or one that waits for its lease backoff.
        if (live.PendingPaneChromeResize
            && !frozenPresentation
            && live.PendingActivation is null
            && !live.PresentationFrozen)
        {
            try
            {
                await FlushPaneChromeResizeAsync(control, live, ct).ConfigureAwait(false);
            }
            catch (ControlPlaneException ex)
            {
                // The flush keeps the request pending and retries after a backoff.
                live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
            }
        }
        await FlushSeenReportAsync(control, live, ct).ConfigureAwait(false);
        if (hadNotify || !SameToastContent(previousToasts, live.Toasts))
            PaintNotifyChrome(live, tty);
        if (agentPatched is not null)
        {
            // Earlier status events share the last event's frame.
            for (var i = 0; i < agentPatched.Count; i++)
            {
                if (!TryGetEventType(agentPatched[i], out var agentType, out var agentPayload))
                    continue;
                var outcome = i == agentPatched.Count - 1
                    ? AgentStatusOutcome(tty, live)
                    : ProcessLogEvents.OutcomeCoalesced;
                TraceAttachEventOutcome(live, agentType, agentPayload, outcome);
            }
        }

        foreach (var ev in events)
        {
            if (ct.IsCancellationRequested)
                return;
            if (!TryGetEventType(ev, out var type, out _))
                continue;
            if (AttachEndpointActivationPump.ShouldDropFrozenPresentationEvent(live, type))
                continue;
            if (!string.Equals(type, ProtocolEventTypes.PopupLifecycle, StringComparison.Ordinal))
                continue;

            await FinishPopupLifecycleAsync(control, live, ev, tty, ct).ConfigureAwait(false);
        }

        if (ct.IsCancellationRequested)
            return;

        var overlayRestored = !frozenPresentation
            && await TryRestoreCommandOverlayFocusAsync(events, live, control, tty, ct)
                .ConfigureAwait(false);
        if (structural is not null && !ct.IsCancellationRequested && !overlayRestored)
        {
            LastPresentedTarget(structural, out var presentedTabId, out var presentedPaneId);
            var tabBeforeShow = live.TabId;
            await RefreshChromeAsync(
                    control,
                    live,
                    tty,
                    ct,
                    followMuxFocus: true,
                    presentedTabId: presentedTabId,
                    presentedPaneId: presentedPaneId,
                    tabEmptiedByRefresh: ClientTabEmptiedByEvents(live, structural))
                .ConfigureAwait(false);
            await FocusShownTabAsync(control, live, tty, presentedTabId, tabBeforeShow, ct).ConfigureAwait(false);
        }

        if (structural is not null)
            TraceStructuralBatch(live, structural, tty, painted: !overlayRestored);

        if (overlayRestored)
            return;

        var session = live.EditScrollback;
        if (session is null)
            return;

        foreach (var ev in events)
        {
            if (ct.IsCancellationRequested)
                return;
            if (!EditScrollbackLauncher.IsEditorExit(ev, session.EditorPaneId))
                continue;

            await EditScrollbackLauncher.CompleteAsync(
                    live, control, alreadyClosed: false, ct)
                .ConfigureAwait(false);
            await SyncFocusAsync(control, live, ct, observePending: tty is null)
                .ConfigureAwait(false);
            if (tty is not null)
            {
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
                PaintChrome(tty, live);
            }
            else
            {
                await ConsumeHeldPendingObserveAsync(control, live, ct).ConfigureAwait(false);
            }

            return;
        }
    }

    internal static async Task<bool> TryRestoreCommandOverlayFocusAsync(
        IEnumerable<JsonElement> events,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);

        var overlayId = live.Dispatcher.CommandOverlayPaneId;
        if (string.IsNullOrWhiteSpace(overlayId))
            return false;

        LatchCommandOverlayGone(events, live, overlayId);
        if (!live.Dispatcher.HasPendingCommandOverlayRestore)
            return false;

        foreach (var next in OverlayRestoreCandidates(live, overlayId))
        {
            var focused = await TryFocusOverlayRestorePaneAsync(
                    next, live, control, ct, observePending: tty is null)
                .ConfigureAwait(false);
            if (focused is null)
                continue;
            if (!focused.Value)
                return false;

            return await CompleteCommandOverlayRestoreAsync(live, control, tty, ct)
                .ConfigureAwait(false);
        }

        var exported = await TryExportOverlayRestorePaneAsync(live, control, overlayId, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(exported))
            return false;

        var exportedFocus = await TryFocusOverlayRestorePaneAsync(
                exported, live, control, ct, observePending: tty is null)
            .ConfigureAwait(false);
        if (exportedFocus is not true)
            return false;

        return await CompleteCommandOverlayRestoreAsync(live, control, tty, ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> CompleteCommandOverlayRestoreAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        live.Status.Clear(StatusOwner.Overlay);
        live.Dispatcher.ClearCommandOverlay();
        if (tty is not null)
        {
            await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
            PaintChrome(tty, live);
        }
        else
        {
            await ConsumeHeldPendingObserveAsync(control, live, ct).ConfigureAwait(false);
        }

        return true;
    }

    private static async Task<string?> TryExportOverlayRestorePaneAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        string overlayId,
        CancellationToken ct)
    {
        var tabId = live.Dispatcher.CommandOverlayTabId;
        if (string.IsNullOrWhiteSpace(tabId))
            return null;

        JsonElement exported;
        try
        {
            exported = await control.CallAsync(
                    ProtocolMethods.LayoutExport,
                    new JsonObject { ["tab_id"] = tabId },
                    ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (exported.ValueKind != JsonValueKind.Object || LayoutContainsPane(exported, overlayId))
            return null;

        var focused = TryJsonString(exported, "focused_pane_id");
        if (string.IsNullOrWhiteSpace(focused)
            || string.Equals(focused, overlayId, StringComparison.Ordinal))
        {
            return null;
        }

        live.Dispatcher.NoteCommandOverlayGone(focused);
        return focused;
    }

    internal static bool IsCommandOverlayLiveEvent(string? type) =>
        string.Equals(type, ProtocolEventTypes.PaneLifecycle, StringComparison.Ordinal)
        || string.Equals(type, ProtocolEventTypes.LayoutUpdated, StringComparison.Ordinal);

    /// <summary>
    /// Trace a Control or Lifecycle event only when the sink takes its level
    /// (<see cref="AttachProcessLog.EventTraceLevel"/>). The check comes before
    /// the payload reads, so a Debug event costs no work at the default level.
    /// </summary>
    private static bool ShouldTraceAttachEvent(AttachLiveState live, string? type) =>
        !string.IsNullOrEmpty(type)
        && EventClassMap.TryFromWireType(type, out var cls)
        && cls is EventClass.Control or EventClass.Lifecycle
        && live.ProcessLog.IsEnabled(AttachProcessLog.EventTraceLevel(type));

    private static void TraceAttachEventReceived(AttachLiveState live, string? type, JsonElement payload)
    {
        if (type is null || !ShouldTraceAttachEvent(live, type))
            return;
        AttachProcessLog.EventReceived(
            live.ProcessLog,
            live.SessionName,
            live.AttachClientId,
            type,
            TryJsonString(payload, "pane_id"),
            TryJsonString(payload, "tab_id"));
    }

    /// <summary>
    /// A <c>pane.scroll_changed</c> that is older than the last applied
    /// scroll state of the pane is dropped, and the attach log says so.
    /// </summary>
    private static void TraceScrollChangedOutcome(
        AttachLiveState live,
        string? type,
        JsonElement payload,
        bool applied) =>
        TraceAttachEventOutcome(
            live,
            type,
            payload,
            applied ? ProcessLogEvents.OutcomeApplied : ProcessLogEvents.OutcomeDropped,
            applied ? null : "stale_or_invalid");

    private static void TraceAttachEventOutcome(
        AttachLiveState live,
        string? type,
        JsonElement payload,
        string outcome,
        string? reason = null)
    {
        if (type is null || !ShouldTraceAttachEvent(live, type))
            return;
        AttachProcessLog.EventOutcome(
            live.ProcessLog,
            live.SessionName,
            live.AttachClientId,
            type,
            TryJsonString(payload, "pane_id"),
            TryJsonString(payload, "tab_id"),
            outcome,
            reason);
    }

    private static string StructuralPaintOutcome(UnixRawTerminal? tty) =>
        tty is not null ? ProcessLogEvents.OutcomePainted : ProcessLogEvents.OutcomeRecomposed;

    private static string AgentStatusOutcome(UnixRawTerminal? tty, AttachLiveState live) =>
        tty is not null && live.ChromeEnabled
            ? ProcessLogEvents.OutcomePainted
            : live.ChromeEnabled
                ? ProcessLogEvents.OutcomeRecomposed
                : ProcessLogEvents.OutcomeApplied;

    /// <summary>
    /// Record each queued event, then apply the batch. The live reader uses
    /// the same apply step after <c>DrainPendingEvents</c>.
    /// </summary>
    internal static async Task ApplyQueuedStructuralEventsAsync(
        IReadOnlyList<JsonElement> events,
        IAttachCommandPort port,
        UnixRawTerminal? tty,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(live);
        foreach (var ev in events)
        {
            if (!TryGetEventType(ev, out var type, out var payload))
                continue;
            TraceAttachEventReceived(live, type, payload);
        }

        await ApplyPresentationBatchAsync(events, port, tty, live, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Apply events already queued on the reader. One snapshot and one paint
    /// cover the structural events. Does not wait for a later event.
    /// </summary>
    internal static async Task ApplyPresentationBatchAsync(
        IReadOnlyList<JsonElement> events,
        IAttachCommandPort port,
        UnixRawTerminal? tty,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(live);

        var structural = new List<JsonElement>();
        var overlayChanged = false;
        var sawChromeRefresh = false;
        foreach (var ev in events)
        {
            if (ct.IsCancellationRequested)
                return;
            if (!TryGetEventType(ev, out var type, out var payload))
                continue;

            if (TryConsumeLeaseChanged(live, type, payload))
                continue;

            if (string.Equals(type, ProtocolEventTypes.PopupLifecycle, StringComparison.Ordinal))
            {
                await FinishPopupLifecycleAsync(port, live, ev, tty, ct).ConfigureAwait(false);
                continue;
            }

            if (IsCommandOverlayLiveEvent(type)
                && !string.IsNullOrWhiteSpace(live.Dispatcher.CommandOverlayPaneId))
            {
                await TryRestoreCommandOverlayFocusAsync([ev], live, port, tty, ct)
                    .ConfigureAwait(false);
                TraceAttachEventOutcome(live, type, payload, ProcessLogEvents.OutcomeApplied);
                continue;
            }

            if (IsChromeRefreshEvent(type))
            {
                sawChromeRefresh = true;
                if (string.Equals(type, ProtocolEventTypes.PanePlacementChanged, StringComparison.Ordinal)
                    && await ReconcileOverlayPlacementEventAsync(port, live, payload, ct)
                        .ConfigureAwait(false))
                {
                    overlayChanged = true;
                }

                SyncOverlayGeometry(live);
                structural.Add(ev);
                continue;
            }

            if (type is ProtocolEventTypes.PaneLifecycle or ProtocolEventTypes.OccupantLifecycle)
            {
                NoteOccupantGeneration(live, payload);
                if (ApplyOverlayPaneExit(live, payload))
                {
                    await ReleaseOverlayLeasesAsync(port, live, ct).ConfigureAwait(false);
                    if (tty is not null)
                        PaintChrome(tty, live);
                }

                structural.Add(ev);
                continue;
            }

            if (string.Equals(type, ProtocolEventTypes.PaneScrollChanged, StringComparison.Ordinal))
            {
                var scrollApplied = TryApplyPaneScrollChanged(live, payload, tty);
                await FlushPaneChromeResizeAsync(port, live, ct).ConfigureAwait(false);
                TraceScrollChangedOutcome(live, type, payload, scrollApplied);
                continue;
            }

            if (IsLiveNotifyEvent(type))
            {
                if (string.Equals(type, ProtocolEventTypes.PaneAgentStatusChanged, StringComparison.Ordinal))
                    _ = ApplyNotifyEvent(live, ev, tty);
                else
                {
                    _ = ApplyNotifyEvent(live, ev, tty);
                    TraceAttachEventOutcome(live, type, payload, ProcessLogEvents.OutcomeApplied);
                }

                await FlushSeenReportAsync(port, live, ct).ConfigureAwait(false);
                continue;
            }

            if (!string.Equals(type, ProtocolEventTypes.TerminalRender, StringComparison.Ordinal))
            {
                TraceAttachEventOutcome(
                    live, type, payload, ProcessLogEvents.OutcomeDropped, "unhandled");
            }
        }

        if (structural.Count == 0 || ct.IsCancellationRequested)
            return;

        LastPresentedTarget(structural, out var presentedTabId, out var presentedPaneId);
        var tabBeforeShow = live.TabId;
        await RefreshChromeAsync(
                port,
                live,
                tty,
                ct,
                followMuxFocus: true,
                presentedTabId: presentedTabId,
                presentedPaneId: presentedPaneId,
                tabEmptiedByRefresh: ClientTabEmptiedByEvents(live, structural))
            .ConfigureAwait(false);
        await FocusShownTabAsync(port, live, tty, presentedTabId, tabBeforeShow, ct).ConfigureAwait(false);
        TraceStructuralBatch(live, structural, tty, painted: true);

        if (!sawChromeRefresh)
            return;
        if (overlayChanged && !live.Overlay.OwnsModal)
            await ReleaseOverlayLeasesAsync(port, live, ct).ConfigureAwait(false);
        else if (live.Overlay.OwnsModal
            && (overlayChanged || !live.Overlay.ResizeBeforeFull))
        {
            await PrepareOverlayAsync(port, live, ct).ConfigureAwait(false);
        }
    }

    private static async Task FinishPopupLifecycleAsync(
        IAttachCommandPort port,
        AttachLiveState live,
        JsonElement ev,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        var applied = TryConsumePopupLifecycle(live, ev, out var reason);
        if (applied)
        {
            if (live.PopupOpen)
                await ResizeOpenPopupAsync(port, live, tty, ct).ConfigureAwait(false);
            else if (tty is not null)
                PaintAfterPopupClosed(tty, live);
        }

        if (!TryGetEventType(ev, out var type, out var payload))
            return;
        TraceAttachEventOutcome(
            live,
            type,
            payload,
            applied ? ProcessLogEvents.OutcomeApplied : ProcessLogEvents.OutcomeDropped,
            reason);
    }

    private static bool TryConsumePopupLifecycle(
        AttachLiveState live,
        JsonElement ev,
        out string? reason)
    {
        if (IsStaleControlPopupOpened(live, ev) || IsStaleControlPopupClosed(live, ev))
        {
            reason = "stale";
            return false;
        }

        if (!ApplyPopupLifecycleEvent(live, ev))
        {
            reason = "rejected";
            return false;
        }

        reason = null;
        return true;
    }

    private static void LastPresentedTarget(
        IReadOnlyList<JsonElement> events,
        out string? tabId,
        out string? paneId)
    {
        tabId = null;
        paneId = null;
        foreach (var ev in events)
        {
            if (!TryGetEventType(ev, out var type, out var payload) || !IsTiledShow(type, payload))
                continue;
            var tab = TryJsonString(payload, "tab_id");
            if (string.IsNullOrWhiteSpace(tab))
                continue;
            tabId = tab;
            paneId = TryJsonString(payload, "focused_pane_id") ?? TryJsonString(payload, "pane_id");
        }
    }

    /// <summary>
    /// Only a pane shown in a tiled tab moves this client to that tab. A focus
    /// change from another client or from <c>hypa tab focus</c> updates the mux
    /// and leaves this client's view in place.
    /// </summary>
    /// <summary>
    /// A tiled show moved this client to the shown tab. Focus that tab on this
    /// connection too, as a tab click does, so the mux sends this client the
    /// render frames of the shown pane.
    /// </summary>
    private static async Task FocusShownTabAsync(
        IAttachCommandPort port,
        AttachLiveState live,
        UnixRawTerminal? tty,
        string? presentedTabId,
        string? tabBeforeShow,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(presentedTabId)
            || !string.Equals(live.TabId, presentedTabId, StringComparison.Ordinal)
            || string.Equals(tabBeforeShow, presentedTabId, StringComparison.Ordinal)
            || ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await ApplyTabFocusAsync(port, live, tty, presentedTabId, AttachSemanticSources.Mux, ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
        }
    }

    private static bool IsTiledShow(string? type, JsonElement payload) =>
        string.Equals(type, ProtocolEventTypes.PanePlacementChanged, StringComparison.Ordinal)
        && string.Equals(TryJsonString(payload, "to"), "tiled", StringComparison.Ordinal);

    private static void TraceStructuralBatch(
        AttachLiveState live,
        IReadOnlyList<JsonElement> structural,
        UnixRawTerminal? tty,
        bool painted)
    {
        var paintedOutcome = StructuralPaintOutcome(tty);
        for (var i = 0; i < structural.Count; i++)
        {
            if (!TryGetEventType(structural[i], out var type, out var payload))
                continue;
            var outcome = painted && i == structural.Count - 1
                ? paintedOutcome
                : ProcessLogEvents.OutcomeCoalesced;
            TraceAttachEventOutcome(live, type, payload, outcome);
        }
    }

    private static bool IsStructuralPresentationEvent(string? type) =>
        IsChromeRefreshEvent(type)
        || type is ProtocolEventTypes.PaneLifecycle or ProtocolEventTypes.OccupantLifecycle;

    internal static bool IsChromeRefreshEvent(string? type) =>
        string.Equals(type, ProtocolEventTypes.LayoutUpdated, StringComparison.Ordinal)
        || string.Equals(type, ProtocolEventTypes.PanePlacementChanged, StringComparison.Ordinal)
        || string.Equals(type, ProtocolEventTypes.WorkspaceLifecycle, StringComparison.Ordinal)
        || string.Equals(type, ProtocolEventTypes.TabLifecycle, StringComparison.Ordinal)
        || string.Equals(type, ProtocolEventTypes.ResourceChanged, StringComparison.Ordinal);

    /// <summary>
    /// Live attach consumes these on the render reader. They are not layout
    // / refreshes.
    /// </summary>
    internal static bool IsLiveNotifyEvent(string? type) =>
        string.Equals(type, ProtocolEventTypes.PaneAgentStatusChanged, StringComparison.Ordinal)
        || string.Equals(type, ProtocolEventTypes.PaneProgramStatusChanged, StringComparison.Ordinal)
        || string.Equals(type, ProtocolEventTypes.PaneProgramAttention, StringComparison.Ordinal)
        || string.Equals(type, ProtocolEventTypes.NotificationShown, StringComparison.Ordinal)
        || string.Equals(type, ProtocolEventTypes.ConfigReloaded, StringComparison.Ordinal)
        || string.Equals(type, ProtocolEventTypes.ClientWindowTitleChanged, StringComparison.Ordinal)
        || string.Equals(type, ProtocolEventTypes.PaneInputRejected, StringComparison.Ordinal);

    /// <summary>
    /// <c>lease.changed</c> is an audit of a claim or release. A release or
    /// expiry of a lease this attach tracks drops that id. A grant for
    /// another holder is not copied into the local lease fields.
    /// </summary>
    internal static bool TryConsumeLeaseChanged(
        AttachLiveState live,
        string? type,
        JsonElement payload)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!string.Equals(type, ProtocolEventTypes.LeaseChanged, StringComparison.Ordinal))
            return false;
        TraceAttachEventOutcome(
            live,
            type,
            payload,
            ProcessLogEvents.OutcomeApplied,
            NoteLeaseChanged(live, payload));
        return true;
    }

    internal static string NoteLeaseChanged(AttachLiveState live, JsonElement payload)
    {
        ArgumentNullException.ThrowIfNull(live);
        var leaseId = TryJsonString(payload, "lease_id");
        var state = TryJsonString(payload, "state");
        if (string.IsNullOrWhiteSpace(leaseId)
            || state is not (LeaseStates.Released or LeaseStates.Expired))
        {
            return "audit";
        }

        var released = false;
        if (string.Equals(live.InputLease, leaseId, StringComparison.Ordinal))
        {
            live.InputLease = "";
            released = true;
        }

        if (string.Equals(live.ResizeLease, leaseId, StringComparison.Ordinal))
        {
            live.ResizeLease = "";
            live.Dispatcher.ResizeLease = "";
            released = true;
        }

        lock (live.ChromeStateGate)
        {
            string? sibling = null;
            foreach (var pair in live.SiblingResizeLeases)
            {
                if (string.Equals(pair.Value, leaseId, StringComparison.Ordinal))
                {
                    sibling = pair.Key;
                    break;
                }
            }

            if (sibling is not null)
            {
                live.SiblingResizeLeases.Remove(sibling);
                released = true;
            }
        }

        if (live.OverlayLeases is { } overlay
            && (string.Equals(overlay.InputLease, leaseId, StringComparison.Ordinal)
                || string.Equals(overlay.ResizeLease, leaseId, StringComparison.Ordinal)))
        {
            // Drop only the released lease. The other lease of the pair can
            // still be granted to this client; keep tracking and renewing it.
            var inputGone = string.Equals(overlay.InputLease, leaseId, StringComparison.Ordinal);
            var remaining = new TargetPaneLeasePair
            {
                PaneId = overlay.PaneId,
                InputLease = inputGone ? "" : overlay.InputLease,
                ResizeLease = inputGone ? overlay.ResizeLease : "",
                InputNewlyGranted = !inputGone && overlay.InputNewlyGranted,
                ResizeNewlyGranted = inputGone && overlay.ResizeNewlyGranted,
            };
            live.OverlayLeases = string.IsNullOrEmpty(remaining.InputLease)
                && string.IsNullOrEmpty(remaining.ResizeLease)
                    ? null
                    : remaining;
            released = true;
        }

        if (released)
            live.Renew?.Untrack(leaseId);
        return released ? "released" : "audit";
    }

    private static void LatchCommandOverlayGone(
        IEnumerable<JsonElement> events,
        AttachLiveState live,
        string overlayId)
    {
        var overlayTabId = live.Dispatcher.CommandOverlayTabId;
        foreach (var ev in events)
        {
            if (!TryGetEventType(ev, out var type, out var payload))
                continue;

            if (string.Equals(type, ProtocolEventTypes.PaneLifecycle, StringComparison.Ordinal)
                && payload.ValueKind == JsonValueKind.Object)
            {
                var paneId = TryJsonString(payload, "pane_id");
                var state = TryJsonString(payload, "state") ?? TryJsonString(payload, "action");
                if (string.Equals(paneId, overlayId, StringComparison.Ordinal)
                    && string.Equals(state, PaneLifecycle.Closed, StringComparison.Ordinal))
                {
                    live.Dispatcher.NoteCommandOverlayGone();
                }
            }

            if (!string.Equals(type, ProtocolEventTypes.LayoutUpdated, StringComparison.Ordinal))
                continue;

            var layout = payload.ValueKind == JsonValueKind.String
                ? ParseJsonObject(payload.GetString())
                : payload;
            if (layout.ValueKind != JsonValueKind.Object)
                continue;
            // layout.updated is per-tab. Other tabs must not look like overlay exit.
            if (!IsOverlayTabLayout(layout, overlayTabId))
                continue;

            var layoutRestore = TryJsonString(layout, "focused_pane_id");
            if (!LayoutContainsPane(layout, overlayId))
                live.Dispatcher.NoteCommandOverlayGone(layoutRestore);
        }
    }

    private static bool IsOverlayTabLayout(JsonElement layout, string? overlayTabId)
    {
        if (string.IsNullOrWhiteSpace(overlayTabId))
            return false;
        return string.Equals(TryJsonString(layout, "tab_id"), overlayTabId, StringComparison.Ordinal);
    }

    private static IEnumerable<string> OverlayRestoreCandidates(AttachLiveState live, string overlayId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in new[]
                 {
                     live.Dispatcher.CommandOverlayRestorePaneId,
                     live.Dispatcher.CommandOverlayLayoutRestorePaneId,
                     live.Dispatcher.LastPaneId,
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate)
                || string.Equals(candidate, overlayId, StringComparison.Ordinal)
                || !seen.Add(candidate))
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static async Task<bool?> TryFocusOverlayRestorePaneAsync(
        string next,
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct,
        bool observePending = true)
    {
        var prevWorkspace = live.Dispatcher.WorkspaceId;
        var prevTab = live.Dispatcher.TabId;
        var prevPane = live.Dispatcher.PaneId;
        var prevLast = live.Dispatcher.LastPaneId;
        live.Dispatcher.FocusPane(next);
        try
        {
            await SyncFocusAsync(control, live, ct, observePending).ConfigureAwait(false);
        }
        catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.NotFound)
        {
            live.Dispatcher.RevertFocus(prevWorkspace, prevTab, prevPane, prevLast);
            return null;
        }
        catch (ControlPlaneException)
        {
            live.Dispatcher.RevertFocus(prevWorkspace, prevTab, prevPane, prevLast);
            return false;
        }

        if (!string.Equals(live.PaneId, next, StringComparison.Ordinal)
            || !string.Equals(live.Dispatcher.PaneId, next, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static JsonElement ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool LayoutContainsPane(JsonElement layout, string paneId)
    {
        if (layout.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(paneId))
            return false;
        if (layout.TryGetProperty("root", out var root))
            return NodeContainsPane(root, paneId);
        return NodeContainsPane(layout, paneId);
    }

    private static bool NodeContainsPane(JsonElement node, string paneId)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return false;
        if (string.Equals(TryJsonString(node, "pane_id"), paneId, StringComparison.Ordinal))
            return true;
        // layout.updated / layout.export trees are BSP (first/second), not children[].
        if (node.TryGetProperty("first", out var first) && NodeContainsPane(first, paneId))
            return true;
        if (node.TryGetProperty("second", out var second) && NodeContainsPane(second, paneId))
            return true;
        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var child in children.EnumerateArray())
        {
            if (NodeContainsPane(child, paneId))
                return true;
        }

        return false;
    }

    internal static void ShowClipboardToast(AttachLiveState live, string text)
    {
        var (cols, rows) = NotifyGridSize(live, tty: null);
        live.Toasts = live.Notifications.OnClipboardCopied(text, cols, rows);
        live.Dispatcher.LastNotificationPaneId = live.Notifications.LastTargetPaneId;
        PlaceToastsOnChrome(live, tty: null);
    }

    internal static bool ApplyPopupLifecycleEvent(AttachLiveState live, JsonElement ev)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!TryGetEventType(ev, out var type, out var payload))
            return false;
        if (!string.Equals(type, ProtocolEventTypes.PopupLifecycle, StringComparison.Ordinal))
            return false;
        var state = payload.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString()
            : null;
        if (string.Equals(state, "closed", StringComparison.Ordinal))
        {
            if (IsStaleControlPopupClosed(live, ev))
                return false;

            live.PopupClosedSeq = Math.Max(live.PopupClosedSeq, ReadEventSeq(ev));
            SetPopupOpen(live, open: false);
            SyncPopupChrome(live);
            return true;
        }

        if (string.Equals(state, "opened", StringComparison.Ordinal))
        {
            if (IsStaleControlPopupOpened(live, ev))
                return false;

            var cols = payload.TryGetProperty("cols", out var colsEl) && colsEl.TryGetInt32(out var c)
                ? c
                : live.PopupInnerCols;
            var rows = payload.TryGetProperty("rows", out var rowsEl) && rowsEl.TryGetInt32(out var r)
                ? r
                : live.PopupInnerRows;
            var outerCols = payload.TryGetProperty("outer_cols", out var ocEl) && ocEl.TryGetInt32(out var oc)
                ? oc
                : 0;
            var outerRows = payload.TryGetProperty("outer_rows", out var orEl) && orEl.TryGetInt32(out var orv)
                ? orv
                : 0;
            SetPopupOpen(
                live,
                open: true,
                cols,
                rows,
                TryReadPopupSize(payload, "width"),
                TryReadPopupSize(payload, "height"),
                outerCols,
                outerRows);
            live.PopupOpenedSeq = Math.Max(live.PopupOpenedSeq, ReadEventSeq(ev));
            // Closing settings changes integration load state. Use the input/paint lock.
            live.WithPaint(() => _ = live.Engine.ForceTerminalForPopup());
            live.Engine.PopupOpen = true;
            SyncPopupChrome(live);
            return true;
        }

        return false;
    }

    /// <param name="paint">
    /// False when the caller applies a batch and paints one frame after it.
    /// </param>
    internal static bool ApplyNotifyEvent(
        AttachLiveState live,
        JsonElement ev,
        UnixRawTerminal? tty,
        CancellationTokenSource? linked = null,
        bool paint = true)
    {
        if (ev.ValueKind != JsonValueKind.Object)
            return false;
        if (!ev.TryGetProperty("event", out var name) || name.GetString() != ProtocolEventTypes.RuntimeEvent)
            return false;
        if (!ev.TryGetProperty("params", out var parms) || parms.ValueKind != JsonValueKind.Object)
            return false;
        if (!parms.TryGetProperty("type", out var typeEl))
            return false;
        var type = typeEl.GetString();
        if (!parms.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return false;

        if (type == ProtocolEventTypes.PaneInputRejected)
        {
            ApplyPaneInputRejected(live, payload, linked);
            return true;
        }

        if (type is ProtocolEventTypes.PaneLifecycle or ProtocolEventTypes.OccupantLifecycle)
        {
            NoteOccupantGeneration(live, payload);
            return false;
        }

        if (type == ProtocolEventTypes.PaneScrollChanged)
        {
            TryApplyPaneScrollChanged(live, payload, tty);
            return false;
        }

        var (cols, rows) = NotifyGridSize(live, tty);
        if (type == ProtocolEventTypes.ClientWindowTitleChanged)
        {
            var overridden = payload.TryGetProperty("overridden", out var overEl)
                && overEl.ValueKind == JsonValueKind.True;
            live.WindowTitleOverride = overridden ? TryJsonString(payload, "title") : null;
            if (tty is not null)
                ApplyWindowTitle(tty, live);
            return false;
        }

        if (type == ProtocolEventTypes.PaneAgentStatusChanged)
        {
            var paneId = TryJsonString(payload, "pane_id") ?? "";
            if (!TryJsonInt32(payload, "occupant_generation", out var generation)
                || !live.TryAcceptOccupantGeneration(paneId, generation))
            {
                TraceAttachEventOutcome(
                    live, type, payload, ProcessLogEvents.OutcomeDropped, "occupant_generation");
                return false;
            }

            var tabId = TryJsonString(payload, "tab_id");
            var status = TryJsonString(payload, "agent_status") ?? "unknown";
            var agentSpecified = payload.TryGetProperty("agent", out var agentProp);
            string? agent = null;
            if (agentSpecified && agentProp.ValueKind == JsonValueKind.String)
                agent = agentProp.GetString();
            var message = TryJsonString(payload, "message");
            var seen = payload.TryGetProperty("seen", out var seenEl)
                && seenEl.ValueKind == JsonValueKind.True;
            var hadAgent = PaneRunsAgent(live, paneId);
            PatchSidebarAgentStatus(live, paneId, tabId, status, agent, agentSpecified);
            NotePredictionAgentChanged(tty, live, paneId, hadAgent);
            live.SeenReports.NotePane(paneId, tabId, status, seen, live.Notifications.FocusedTabId);
            live.InvalidateChrome();
            live.Toasts = live.Notifications.OnAgentStatusChanged(
                paneId, tabId, status, agent, message, cols, rows, seen,
                TryJsonString(payload, "source"));
            WritePendingOsc9(live, tty);
            if (paint && tty is not null && live.ChromeEnabled)
            {
                SyncPaneChromeGeometry(live, tty);
                PaintChrome(tty, live);
            }

            // A batch caller logs the outcome after its one frame.
            if (paint)
                TraceAttachEventOutcome(live, type, payload, AgentStatusOutcome(tty, live));
            return true;
        }

        if (type == ProtocolEventTypes.PaneProgramStatusChanged)
        {
            // The mux sends the finished views. The attach only stores and paints them.
            var paneId = TryJsonString(payload, "pane_id") ?? "";
            if (!TryJsonInt32(payload, "occupant_generation", out var generation)
                || !live.TryAcceptOccupantGeneration(paneId, generation))
            {
                TraceAttachEventOutcome(
                    live, type, payload, ProcessLogEvents.OutcomeDropped, "occupant_generation");
                return false;
            }

            var tabId = TryJsonString(payload, "tab_id");
            var paneView = payload.TryGetProperty("view", out var viewEl)
                ? ProgramStatusViewWire.TryReadPane(viewEl)
                : null;
            var tabView = payload.TryGetProperty("tab_view", out var tabViewEl)
                ? ProgramStatusViewWire.TryReadTab(tabViewEl)
                : null;
            PatchSidebarProgramStatus(live, paneId, paneView);
            PatchTabStatusBadge(live, tabId, tabView);
            // A done or error that the person cannot see yet is reported when its tab is in view.
            live.SeenReports.NotePane(
                paneId,
                tabId,
                paneView?.State is ProgramStatusState.Done or ProgramStatusState.Error ? paneView.State.ToWire() : null,
                paneView?.Seen ?? true,
                live.Notifications.FocusedTabId);
            // The summary is the view of the whole session that the mux sent.
            live.SessionStatus = payload.TryGetProperty("session_view", out var sessionEl)
                ? ProgramStatusViewWire.TryReadSession(sessionEl)
                : null;
            if (tty is not null)
                ApplyHostStatus(tty, live);
            live.InvalidateChrome();
            if (paint && tty is not null && live.ChromeEnabled)
            {
                SyncPaneChromeGeometry(live, tty);
                PaintChrome(tty, live);
            }

            return true;
        }

        if (type == ProtocolEventTypes.PaneProgramAttention)
        {
            // The mux decided that the pane needs the person and applied the rate limit.
            // The attach picks how to tell the person, and skips a pane in view.
            var paneId = TryJsonString(payload, "pane_id") ?? "";
            if (!TryJsonInt32(payload, "occupant_generation", out var generation)
                || !live.TryAcceptOccupantGeneration(paneId, generation))
            {
                TraceAttachEventOutcome(
                    live, type, payload, ProcessLogEvents.OutcomeDropped, "occupant_generation");
                return false;
            }

            if (ProgramStatusAttentionWire.TryRead(payload) is not { } item)
            {
                TraceAttachEventOutcome(live, type, payload, ProcessLogEvents.OutcomeDropped, "bad_payload");
                return false;
            }

            live.Toasts = live.Notifications.OnProgramAttention(item, cols, rows);
            WritePendingOsc9(live, tty);
            if (paint && tty is not null && live.ChromeEnabled)
            {
                SyncPaneChromeGeometry(live, tty);
                PaintChrome(tty, live);
            }

            return true;
        }

        if (type == ProtocolEventTypes.NotificationShown)
        {
            var title = TryJsonString(payload, "title") ?? "";
            var body = TryJsonString(payload, "body");
            var source = TryJsonString(payload, "source") ?? NotificationSources.Core;
            var sound = TryJsonString(payload, "sound") ?? NotificationSounds.None;
            var paneId = TryJsonString(payload, "pane_id");
            live.Toasts = live.Notifications.OnNotificationShown(
                title, body, source, sound, paneId, cols, rows);
            WritePendingOsc9(live, tty);
            return true;
        }

        if (type == ProtocolEventTypes.ConfigReloaded)
        {
            var status = TryJsonString(payload, "status");
            if (string.Equals(status, ConfigReloadStatuses.Applied, StringComparison.Ordinal))
            {
                if (live.SuppressNextConfigReloadedLoad)
                    live.SuppressNextConfigReloadedLoad = false;
                else
                    ApplyLiveAttachConfigFromDisk(live, tty);
            }

            return true;
        }

        return false;
    }

    private static void ApplyPaneInputRejected(
        AttachLiveState live,
        JsonElement payload,
        CancellationTokenSource? linked)
    {
        var paneId = TryJsonString(payload, "pane_id") ?? "";
        if (paneId.Length > 0
            && !string.Equals(paneId, live.PaneId, StringComparison.Ordinal))
        {
            return;
        }

        var leaseId = TryJsonString(payload, "lease_id") ?? "";
        if (leaseId.Length > 0
            && !string.Equals(leaseId, live.InputLease, StringComparison.Ordinal))
        {
            return;
        }

        _ = TryJsonInt32(payload, "code", out var code);
        var message = TryJsonString(payload, "message") ?? "input rejected";
        if (code is ProtocolErrorCodes.LeaseExpired
            or ProtocolErrorCodes.LeaseRequired
            or ProtocolErrorCodes.NotFound)
        {
            live.InputInvalidStateCount = 0;
            RequestInputDetach(live, FormatStatus(message), linked);
            return;
        }

        if (code == ProtocolErrorCodes.InvalidState)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - live.InputInvalidStateAt > AttachInputSender.TransientInvalidStateWindow)
                live.InputInvalidStateCount = 0;
            live.InputInvalidStateAt = now;
            live.InputInvalidStateCount++;
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(message));
            if (live.InputInvalidStateCount >= AttachInputSender.TransientInvalidStateLimit)
                RequestInputDetach(live, FormatStatus(message), linked);
            return;
        }

        RequestInputDetach(live, "input sender fault", linked);
    }

    private static void RequestInputDetach(
        AttachLiveState live,
        string status,
        CancellationTokenSource? linked)
    {
        live.Status.Set(StatusOwner.Transport, StatusSeverity.Error, status);
        live.InputDetachRequested = true;
        live.DetachRequested = true;
        try { linked?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // session already leaving
        }
    }

    private static bool IsAppliedConfigReloaded(JsonElement ev)
    {
        if (!TryGetEventType(ev, out var type, out var payload))
            return false;
        if (!string.Equals(type, ProtocolEventTypes.ConfigReloaded, StringComparison.Ordinal))
            return false;
        var status = TryJsonString(payload, "status");
        return string.Equals(status, ConfigReloadStatuses.Applied, StringComparison.Ordinal);
    }

    internal static void PatchSidebarProgramStatus(AttachLiveState live, string paneId, ProgramStatusView? view)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(paneId) || live.SidebarInput is not { } input)
            return;

        live.SidebarAgentRevision++;
        live.WithPaint(() =>
        {
            var panes = input.Panes.ToList();
            var index = panes.FindIndex(p => string.Equals(p.Id, paneId, StringComparison.Ordinal));
            if (index < 0)
                return;
            panes[index] = panes[index] with { ProgramStatus = view };
            live.SidebarInput = input with { Panes = panes };
            RecomposeLiveSidebar(live);
        });
    }

    internal static void PatchTabStatusBadge(AttachLiveState live, string? tabId, TabProgramStatusView? view)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(tabId) || live.TabHits.Count == 0)
            return;

        var badge = view is null ? "" : SidebarProgramStatusText.TabBadge(view, live.Ui.StatusIndicators);
        var tabs = live.TabHits.ToList();
        var index = tabs.FindIndex(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
        if (index < 0 || string.Equals(tabs[index].Badge, badge, StringComparison.Ordinal))
            return;
        tabs[index] = tabs[index] with { Badge = badge };
        live.TabHits = tabs;
        RebuildTabBarRight(live);
    }

    internal static void PatchSidebarAgentStatus(
        AttachLiveState live,
        string paneId,
        string? tabId,
        string status,
        string? agent,
        bool agentSpecified = false)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(paneId))
            return;

        live.SidebarAgentRevision++;
        live.WithPaint(() =>
        {
            var input = live.SidebarInput;
            var panes = input?.Panes is { Count: > 0 }
                ? input.Panes.ToList()
                : [];
            var found = false;
            for (var i = 0; i < panes.Count; i++)
            {
                if (!string.Equals(panes[i].Id, paneId, StringComparison.Ordinal))
                    continue;
                panes[i] = panes[i] with
                {
                    State = status,
                    Agent = agentSpecified ? agent : panes[i].Agent,
                    TabId = string.IsNullOrWhiteSpace(tabId) ? panes[i].TabId : tabId,
                };
                found = true;
                break;
            }

            if (!found)
            {
                panes.Add(new SidebarPaneItem
                {
                    Id = paneId,
                    TabId = tabId ?? live.TabId ?? "",
                    WorkspaceId = live.WorkspaceId ?? "",
                    Agent = agent,
                    State = status,
                });
            }

            if (input is null)
            {
                live.SidebarInput = new SidebarComposeInput
                {
                    Ui = live.Ui,
                    Panes = panes,
                    FocusedWorkspaceId = live.WorkspaceId,
                    FocusedTabId = live.TabId,
                    FocusedPaneId = live.PaneId,
                    Expanded = live.SidebarOpen,
                    RequestedWidth = live.SidebarRequestedWidth > 0
                        ? live.SidebarRequestedWidth
                        : live.Ui.SidebarWidth,
                    Git = live.GitStatus,
                    CollapsedSectionIds = live.CollapsedSectionIds,
                    CollapsedWorktreeGroups = live.CollapsedWorktreeGroups,
                    GlobalMenuAttentionBadgeVisible = live.GlobalMenuAttentionBadgeVisible,
                };
            }
            else
            {
                live.SidebarInput = input with { Panes = panes };
            }

            RecomposeLiveSidebar(live);
        });
    }

    internal static (int Cols, int Rows) NotifyGridSize(AttachLiveState live, UnixRawTerminal? tty)
    {
        if (tty is not null && tty.TryGetSize(out var tc, out var tr) && tc > 0 && tr > 0)
            return (tc, tr);
        if (live.Chrome is { Cols: > 0, Rows: > 0 } chrome)
            return (chrome.Cols, chrome.Rows);
        if (live.ChromeSeed is { Cols: > 0, Rows: > 0 } seed)
            return (seed.Cols, seed.Rows);
        return (80, 24);
    }

    internal static void PlaceToastsOnChrome(AttachLiveState live, UnixRawTerminal? tty)
    {
        _ = tty;
        if (!live.ChromeEnabled || live.Chrome is null)
            return;
        if (live.Chrome is { IsNarrow: true, MobileSwitcher.Open: true }
            && live.ChromeSeed is not null)
        {
            RecomputeChromeFromSeed(live, tty);
            return;
        }

        live.Chrome = live.Chrome with { Toasts = live.Toasts };
    }

    private static void PaintNotifyChrome(AttachLiveState live, UnixRawTerminal? tty)
    {
        PlaceToastsOnChrome(live, tty);
        if (tty is not null && live.ChromeEnabled)
            PaintChrome(tty, live);
    }

    private static bool SameToastContent(IReadOnlyList<ToastHit> left, IReadOnlyList<ToastHit> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].PaneId, right[i].PaneId, StringComparison.Ordinal)
                || !string.Equals(left[i].Text, right[i].Text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void WritePendingOsc9(AttachLiveState live, UnixRawTerminal? tty)
    {
        if (tty is null)
            return;
        var osc = live.Notifications.DrainOsc9();
        if (osc.Count == 0)
            return;
        for (var i = 0; i < osc.Count; i++)
            tty.WriteBytes(Encoding.UTF8.GetBytes(osc[i]));
    }

    internal static Task DrainOverlayEventsAsync(
        IEnumerable<JsonElement> pending,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct,
        CancellationTokenSource? linked = null) =>
        ApplyControlEventsAsync(pending, live, control, tty, ct, linked);

    /// <summary>
    /// <c>ServerMessage</c> on the client loop. The heartbeat takes the
    /// reliable lane. A presentation-sync, presentation-ready, or
    /// projection-snapshot line has no <c>params.type</c>, so overlay apply
    /// drops it. Forward that line into the same admit the render loop
    /// uses, labeled with this client's endpoint id and generation.
    /// <see cref="ControlPlaneClient.DrainPendingEvents"/> already removed
    /// the line, so the render loop does not admit it again.
    /// </summary>
    internal static async Task DrainOverlayEventsAsync(
        ControlPlaneClient control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct,
        CancellationTokenSource? linked = null)
    {
        var pending = control.DrainPendingEvents();
        if (live.PendingActivation is not null)
        {
            foreach (var ev in pending)
            {
                if (ct.IsCancellationRequested)
                    break;
                AttachEndpointActivationPump.TryForwardReadLoopActivationEvent(live, control, ev, tty);
            }
        }

        await ApplyControlEventsAsync(
                pending,
                live,
                new ControlPlaneAttachCommandPort(LiveControlClient(live, control) ?? control),
                tty,
                ct,
                linked)
            .ConfigureAwait(false);
    }

    internal static bool ShouldReportAttachClientMode(KeyEngineEvent ev, bool overlayCommit)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (overlayCommit)
            return true;
        if (ev.Kind is not (KeyEngineEventKind.EnterMode or KeyEngineEventKind.LeaveMode))
            return false;
        return ev.Mode is not AttachClientMode.Prefix;
    }

    internal static async Task ReportAttachClientModeAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        live.Engine.ExclusiveSurfaceOpen = ExclusiveClientSurfaceOpen(live);
        live.Dispatcher.ClientMode = AttachClientModePublication.PublishedToken(live);
        await PublishClientModeAsync(control, live, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(live.InputLease) || live.StatusError == "ui_busy")
            return;
        try
        {
            await control.CallAsync(
                    ProtocolMethods.RuntimeLeaseRenew,
                    new JsonObject
                    {
                        ["lease_id"] = live.InputLease,
                        ["ttl_ms"] = LeaseTtlMs,
                        ["client_mode"] = AttachClientModePublication.PublishedToken(live),
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException ex) when (IsUiBusyStatus(ex))
        {
            RevertRejectedHumanModal(live);
        }
    }

    private static async Task ConsumeHeldPendingObserveAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct)
    {
        Interlocked.Exchange(ref live.HoldPendingObserve, 0);
        try
        {
            var render = live.RenderPort ?? control;
            await ObservePendingAsync(render, live, ct).ConfigureAwait(false);
        }
        catch (ControlPlaneException ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
        }
        catch (InvalidOperationException ex)
        {
            live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
        }
    }

    internal static async Task ObservePendingAsync(
        IAttachCommandPort render,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(live);
        if (Volatile.Read(ref live.HoldPendingObserve) != 0)
            return;
        var pending = Interlocked.Exchange(ref live.PendingObservePaneId, null);
        if (string.IsNullOrWhiteSpace(pending) || string.IsNullOrWhiteSpace(live.RenderSub))
        {
            _ = StartChainReanchors(render, live);
            return;
        }

        // The observe of the pending pane posts a full frame of that pane.
        live.ChainReanchors.NoteCovered(pending, live.Time.GetUtcNow());
        try
        {
            string[] previous;
            lock (live.ChromeStateGate)
            {
                previous = [.. live.ObservedPaneIds];
                live.ObservedPaneIds.Clear();
                live.ObservedPaneIds.Add(pending);
            }

            try
            {
                await render.CallAsync(
                        ProtocolMethods.TerminalObserve,
                        new JsonObject
                        {
                            ["pane_id"] = pending,
                            ["subscription_id"] = live.RenderSub,
                            ["replace"] = true,
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                lock (live.ChromeStateGate)
                {
                    live.ObservedPaneIds.Clear();
                    foreach (var id in previous)
                        live.ObservedPaneIds.Add(id);
                }

                throw;
            }

            await ObserveVisiblePanesAsync(render, live, ct).ConfigureAwait(false);
            _ = StartChainReanchors(render, live);
        }
        catch (IOException)
        {
            Interlocked.CompareExchange(ref live.PendingObservePaneId, pending, null);
        }
        catch (ObjectDisposedException)
        {
            Interlocked.CompareExchange(ref live.PendingObservePaneId, pending, null);
        }
        catch (ControlPlaneException)
        {
            Interlocked.CompareExchange(ref live.PendingObservePaneId, pending, null);
        }
        catch (ControlPlaneClientTimeoutException)
        {
            Interlocked.CompareExchange(ref live.PendingObservePaneId, pending, null);
        }
    }

    /// <summary>
    /// Starts the queued reanchor requests of <see cref="AttachLiveState.ChainReanchors"/>
    /// and returns without a wait for their replies. Each request is
    /// <c>terminal.observe</c> with <c>replace</c> false for one pane: the mux
    /// posts a full frame of that pane, and later deltas chain on it. The
    /// full frame comes on the render stream, and the render loop applies it
    /// as any other body. With <c>replace</c> false, the mux does not move the
    /// subscription, and the client does not change its observed panes.
    /// <para>
    /// The render loop never waits for a reply, so a reanchor of one pane
    /// never delays the echo of another pane by a round trip. A pane has at
    /// most one request in flight. While the presentation is frozen or a
    /// focus change holds observes, the requests stay queued.
    /// </para>
    /// <para>
    /// A pane that is not visible, or a state with no render subscription,
    /// drops its request: a later observe sends a full frame. A pane that
    /// the mux does not find drops its request too. After any other failed
    /// request, the next dropped delta of the pane asks again after
    /// <see cref="PaneChainReanchorRequests.ResendAfter"/>.
    /// </para>
    /// The returned task ends when every started request ended. A link or
    /// mux error does not fault it. The render loop discards it.
    /// </summary>
    private static void StartChainReanchorsOffLoop(AttachLiveState live, ControlPlaneClient render)
    {
        if (!live.ChainReanchors.HasWork)
            return;
        _ = StartChainReanchors(new ControlPlaneAttachCommandPort(LiveControlClient(live, render) ?? render), live);
    }

    internal static Task StartChainReanchors(IAttachCommandPort render, AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(live);
        foreach (var retry in live.ChainReanchors.TakeRetryTimers())
            _ = RetryChainReanchorAfterWaitAsync(render, live, retry);
        if (!live.ChainReanchors.HasQueued
            || live.PresentationFrozen
            || Volatile.Read(ref live.HoldPendingObserve) != 0)
        {
            return Task.CompletedTask;
        }

        List<Task>? requests = null;
        foreach (var request in live.ChainReanchors.TakeQueued(live.Time.GetUtcNow()))
        {
            var subscription = live.RenderSub;
            if (string.IsNullOrWhiteSpace(subscription) || !IsVisiblePane(live, request.PaneId))
            {
                live.ChainReanchors.Finish(request, failed: false, live.Time.GetUtcNow(), out _);
                live.ChainReanchors.Clear(request.PaneId);
                continue;
            }

            (requests ??= []).Add(RequestChainReanchorAsync(render, live, request, subscription));
        }

        return requests is null ? Task.CompletedTask : Task.WhenAll(requests);
    }

    /// <summary>
    /// A failed request of a new break, or a second refused full frame in a
    /// row, asks again after <see cref="PaneChainReanchorRequests.ResendAfter"/>,
    /// also when no later delta comes to ask. The retry goes only when
    /// <paramref name="retry"/> is still the retry of its pane: a full frame
    /// that the host showed, a reset, or a later retry ends it.
    /// </summary>
    private static async Task RetryChainReanchorAfterWaitAsync(
        IAttachCommandPort render,
        AttachLiveState live,
        ChainReanchorTicket retry)
    {
        try
        {
            await Task.Delay(PaneChainReanchorRequests.ResendAfter, live.Time).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (live.ChainReanchors.TakeRetry(retry))
            await StartChainReanchors(render, live).ConfigureAwait(false);
    }

    private static async Task RequestChainReanchorAsync(
        IAttachCommandPort render,
        AttachLiveState live,
        ChainReanchorTicket request,
        string subscription)
    {
        var paneId = request.PaneId;
        AttachProcessLog.Observe(
            live.ProcessLog,
            live.SessionName,
            live.AttachClientId,
            paneId,
            ProcessLogEvents.OutcomeStarted,
            "reanchor",
            FrameDropsInRow(live, paneId));
        try
        {
            await render.CallAsync(
                    ProtocolMethods.TerminalObserve,
                    new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["subscription_id"] = subscription,
                        ["replace"] = false,
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            // The chain broke again after the full frame of this request:
            // ask again now. No later delta may come to ask. When the pane
            // already asked again at once, it asks after the resend time.
            // A reply of a request from before a reset changes nothing.
            var next = live.ChainReanchors.Finish(request, failed: false, live.Time.GetUtcNow(), out var retry);
            if (next == ChainReanchorFinish.Queued)
                _ = StartChainReanchors(render, live);
            else if (next == ChainReanchorFinish.RetryAfterWait)
                _ = RetryChainReanchorAfterWaitAsync(render, live, retry);
        }
        catch (Exception ex) when (ex is IOException
            or ObjectDisposedException
            or ControlPlaneException
            or ControlPlaneClientTimeoutException
            or InvalidOperationException
            or OperationCanceledException)
        {
            var next = live.ChainReanchors.Finish(request, failed: true, live.Time.GetUtcNow(), out var retry);
            // A stale ticket: a reset came after the send. A later request of
            // the pane can be in flight, and this failure does not touch it.
            if (next != ChainReanchorFinish.Stale
                && ex is ControlPlaneException { Code: ProtocolErrorCodes.NotFound })
            {
                live.ChainReanchors.Clear(paneId);
            }
            else if (next == ChainReanchorFinish.RetryAfterWait)
                _ = RetryChainReanchorAfterWaitAsync(render, live, retry);
            AttachProcessLog.Observe(
                live.ProcessLog,
                live.SessionName,
                live.AttachClientId,
                paneId,
                ProcessLogEvents.OutcomeError,
                "reanchor " + ex.GetType().Name);
        }
    }

    internal static async Task HandleRenderEventAsync(
        JsonElement ev,
        IAttachCommandPort render,
        SnapshotAssembler assembler,
        UnixRawTerminal? tty,
        AttachLiveState live,
        CancellationToken ct)
    {
        bool AbortIfSuperseded() => ct.IsCancellationRequested;

        try
        {
            if (AbortIfSuperseded())
                return;

            if (!TryGetEventType(ev, out var type, out var payload))
                return;
            TraceAttachEventReceived(live, type, payload);
            if (AttachEndpointActivationPump.ShouldDropFrozenPresentationEvent(live, type))
            {
                TraceAttachEventOutcome(
                    live, type, payload, ProcessLogEvents.OutcomeDropped, "presentation_frozen");
                return;
            }

            if (TryConsumeLeaseChanged(live, type, payload))
                return;

            if (BlocksPresentationInput(live)
                && !IsChromeRefreshEvent(type)
                && !string.Equals(type, ProtocolEventTypes.PopupLifecycle, StringComparison.Ordinal))
            {
                TraceAttachEventOutcome(
                    live, type, payload, ProcessLogEvents.OutcomeDropped, "presentation_blocked");
                return;
            }

            if (string.Equals(type, ProtocolEventTypes.PopupLifecycle, StringComparison.Ordinal))
            {
                if (AbortIfSuperseded())
                    return;
                await FinishPopupLifecycleAsync(render, live, ev, tty, ct).ConfigureAwait(false);
                return;
            }

            if (IsCommandOverlayLiveEvent(type)
                && !string.IsNullOrWhiteSpace(live.Dispatcher.CommandOverlayPaneId))
            {
                if (AbortIfSuperseded())
                    return;
                await TryRestoreCommandOverlayFocusAsync([ev], live, render, tty, ct)
                    .ConfigureAwait(false);
                TraceAttachEventOutcome(live, type, payload, ProcessLogEvents.OutcomeApplied);
                return;
            }

            if (IsStructuralPresentationEvent(type))
            {
                if (AbortIfSuperseded())
                    return;
                await ApplyPresentationBatchAsync([ev], render, tty, live, ct)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(type, ProtocolEventTypes.PaneScrollChanged, StringComparison.Ordinal))
            {
                if (AbortIfSuperseded())
                    return;
                var scrollApplied = TryApplyPaneScrollChanged(live, payload, tty);
                await FlushPaneChromeResizeAsync(render, live, ct).ConfigureAwait(false);
                TraceScrollChangedOutcome(live, type, payload, scrollApplied);
                return;
            }

            if (IsLiveNotifyEvent(type))
            {
                if (AbortIfSuperseded())
                    return;
                if (string.Equals(type, ProtocolEventTypes.PaneAgentStatusChanged, StringComparison.Ordinal))
                    _ = ApplyNotifyEvent(live, ev, tty);
                else
                {
                    _ = ApplyNotifyEvent(live, ev, tty);
                    TraceAttachEventOutcome(live, type, payload, ProcessLogEvents.OutcomeApplied);
                }

                await FlushSeenReportAsync(render, live, ct).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(type, ProtocolEventTypes.TerminalRender, StringComparison.Ordinal)
                || tty is null)
            {
                if (!string.Equals(type, ProtocolEventTypes.TerminalRender, StringComparison.Ordinal))
                {
                    TraceAttachEventOutcome(
                        live, type, payload, ProcessLogEvents.OutcomeDropped, "unhandled");
                }

                return;
            }

            if (live.PendingActivation is not null)
            {
                if (TryResolveRenderConnectionIdentity(live, sourceConnection: true, out var admitEndpoint, out var admitGeneration))
                {
                    TryCaptureActivationTerminalRender(
                        live,
                        admitEndpoint,
                        admitGeneration,
                        payload,
                        assembler);
                    _ = AttachEndpointActivationPump.TryAdmitTerminalRenderPayload(
                        live,
                        admitEndpoint,
                        admitGeneration,
                        payload,
                        tty);
                }

                return;
            }

            await ObservePendingAsync(render, live, ct).ConfigureAwait(false);
            if (AbortIfSuperseded())
                return;

            if (IsPopupRenderPayload(payload))
            {
                ApplyPopupRenderPayload(payload, assembler, tty, live);
                return;
            }

            if (!payload.TryGetProperty("pane_id", out var pid)
                || !IsVisiblePane(live, pid.GetString()))
            {
                NoteInvisibleRender(live, payload);
                return;
            }

            if (payload.TryGetProperty("kind", out var kind)
                && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == TerminalRenderBlitPayload.KindBlit)
            {
                live.BlitReanchorPending = true;
                return;
            }

            if (payload.TryGetProperty("kind", out kind)
                && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == TerminalRenderCellsPayload.KindCells)
            {
                var cells = DeserializePayload(
                    payload, ProtocolJsonContext.Default.TerminalRenderCellsPayload);
                if (cells is null)
                    return;
                if (AbortIfSuperseded())
                    return;
                if (TryWriteCells(tty, live, cells, reanchorBeforePaint: true))
                {
                    live.NoteSnapshotRender(cells.PaneId);
                    if (live.Chrome is not null)
                        live.InitialSnapshotPainted = true;
                }

                return;
            }

            if (payload.TryGetProperty("kind", out kind)
                && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == TerminalRenderSnapshotPayload.KindSnapshot)
            {
                var slice = DeserializePayload(
                    payload, ProtocolJsonContext.Default.TerminalRenderSnapshotPayload);
                if (slice is null)
                    return;
                if (AbortIfSuperseded())
                    return;
                // Paint the complete frame first. Note snapshot-only only after
                // SnapshotPainter has written cells. Chrome live bytes never
                // CUP-remap pane ANSI onto the host TTY.
                if (assembler.TryAdd(slice, out var frame) && frame is not null
                    && PaintAssembled(tty, frame, live))
                {
                    live.NoteSnapshotRender(slice.PaneId);
                    await AfterInitialSnapshotPaintAsync(render, live, tty, ct)
                        .ConfigureAwait(false);
                    await ReseedCopyIfNeededAsync(live, render, tty, controlGate: null, ct)
                        .ConfigureAwait(false);
                }
                return;
            }

            if (payload.TryGetProperty("data", out var dataEl)
                && dataEl.ValueKind == JsonValueKind.String
                && dataEl.GetString() is { Length: > 0 } b64)
            {
                try
                {
                    var paneKey = pid.GetString();
                    // Snapshot-capable panes never remap raw data onto the host TTY.
                    if (live.IsSnapshotCapable(paneKey))
                    {
                        if (!string.IsNullOrWhiteSpace(paneKey))
                            live.NotePaneMouseFromLive(paneKey, Convert.FromBase64String(b64));
                        return;
                    }

                    if (AbortIfSuperseded())
                        return;
                    ApplyLiveRender(
                        tty,
                        Convert.FromBase64String(b64),
                        live,
                        paneKey,
                        ReadRenderRoute(payload));
                }
                catch (FormatException)
                {
                }
            }
        }
        finally
        {
            if (live.PendingPaneChromeResize && !AbortIfSuperseded())
            {
                await FlushPaneChromeResizeAsync(render, live, ct).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<bool> ApplyEngineEventsAsync(
        UnixRawTerminal tty,
        AttachLiveState live,
        IEnumerable<KeyEngineEvent> events,
        IAttachCommandPort control,
        CancellationTokenSource linked,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(linked);

        if (EntersCopyMode(events))
            live.BeginCopySeedCapture();

        foreach (var ev in events)
        {
            if (ev.Kind == KeyEngineEventKind.HideOverlay)
            {
                await ApplyHideOverlayEventAsync(tty, live, control, ct)
                    .ConfigureAwait(false);
                continue;
            }

            if (ev.Kind == KeyEngineEventKind.Detach)
            {
                live.DetachRequested = true;
                linked.Cancel();
                return true;
            }

            if ((ev.Kind == KeyEngineEventKind.SendPopupBytes
                    || (ev.Kind == KeyEngineEventKind.SendPaneBytes
                        && string.Equals(ev.TargetId, "popup", StringComparison.Ordinal)))
                && ev.Bytes is { Length: > 0 } popupPayload)
            {
                try
                {
                    var keys = new JsonObject
                    {
                        ["encoding"] = "base64",
                        ["data"] = Convert.ToBase64String(popupPayload),
                    };
                    if (!string.IsNullOrWhiteSpace(live.InputLease))
                        keys["lease_id"] = live.InputLease;
                    await control.CallAsync(ProtocolMethods.PopupSendKeys, keys, ct)
                        .ConfigureAwait(false);
                }
                catch (ControlPlaneException ex)
                {
                    if (!ClosePopupIfNotOpen(live, ex))
                        live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                    PaintChrome(tty, live);
                }
                catch (InvalidOperationException ex)
                {
                    live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                    PaintChrome(tty, live);
                }

                continue;
            }

            if (ev.Kind == KeyEngineEventKind.SendPaneBytes
                && ev.Bytes is { Length: > 0 } payload)
            {
                if (OverlayDispatchReady(live, ev.TargetId))
                {
                    try
                    {
                        await control.CallAsync(
                                ProtocolMethods.PaneSendKeys,
                                OverlaySendKeysBody(live, payload, keyboard: !ev.Paste),
                                ct)
                            .ConfigureAwait(false);
                    }
                    catch (ControlPlaneException ex)
                    {
                        live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                        PaintChrome(tty, live);
                    }
                    catch (InvalidOperationException ex)
                    {
                        live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                        PaintChrome(tty, live);
                    }

                    continue;
                }

                if (!live.HasLivePane)
                    continue;

                // This path sends no seq, so its keys can never confirm.
                NoteUnsequencedPaneInput(tty, live);
                try
                {
                    await control.CallAsync(
                            ProtocolMethods.PaneSendKeys,
                            BuildPaneSendKeys(live.PaneId!, payload, live.InputLease, keyboard: !ev.Paste),
                            ct)
                        .ConfigureAwait(false);
                }
                catch (ControlPlaneException ex)
                {
                    live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                    PaintChrome(tty, live);
                }
                catch (InvalidOperationException ex)
                {
                    live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                    PaintChrome(tty, live);
                }

                continue;
            }

            if (ev.Kind == KeyEngineEventKind.Dispatch && ev.ToRequest() is { } request)
            {
                request = BindCloseRequestTarget(live, request);
                try
                {
                    await DispatchLayoutActionAsync(request, live, control, ct, tty).ConfigureAwait(false);
                    if (request.Action is not KeyActionId.ReloadConfig)
                        ClearTransientStatusError(live, StatusOwner.Command);
                    if (!IsEmptyRenameEnter(request))
                        await SyncFocusAsync(control, live, ct, observePending: false)
                            .ConfigureAwait(false);
                    await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
                }
                catch (ControlPlaneException ex)
                {
                    live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                    await RefreshChromeAfterCloseFaultAsync(request, live, control, tty, ct)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    live.Status.Set(StatusOwner.Command, StatusSeverity.Error, FormatStatus(ex));
                    await RefreshChromeAfterCloseFaultAsync(request, live, control, tty, ct)
                        .ConfigureAwait(false);
                }

                PaintChrome(tty, live);
                continue;
            }

            ApplyPendingModeEvent(live, ev);
            if (ev.Kind is KeyEngineEventKind.LeaveMode
                && ev.Mode is AttachClientMode.ConfirmMoveWork)
            {
                await CompletePendingMoveWorkAsync(live, control, tty, ct)
                    .ConfigureAwait(false);
            }

            if (ev.Kind is KeyEngineEventKind.LeaveMode
                && ev.Mode is AttachClientMode.TransferPicker)
            {
                await CompletePendingTransferPickAsync(live, control, tty, ct)
                    .ConfigureAwait(false);
            }

            if (ev.Kind is KeyEngineEventKind.EnterMode or KeyEngineEventKind.HelpFilter)
                await HandleSettingsOverlayAsync(ev, live, control, ct).ConfigureAwait(false);

            var settingsLeave = ev.Kind is KeyEngineEventKind.HelpFilter
                && await TryLeaveSettingsAfterPatchAsync(live, control, ct).ConfigureAwait(false);
            var onboardingDone = ev.Kind is KeyEngineEventKind.HelpFilter
                && await TryCompleteOnboardingAsync(live, control, ct).ConfigureAwait(false);
            var releaseNotesOpen = ev.Kind is KeyEngineEventKind.HelpFilter
                && TryOpenReleaseNotesFromSettings(live);
            var releaseNotesDismissed = ev.Kind is KeyEngineEventKind.HelpFilter
                && ev.Filter == ReleaseNotesOverlayModel.FilterDismiss
                && TryDismissReleaseNotes(live);
            var overlayCommit = settingsLeave || onboardingDone || releaseNotesOpen || releaseNotesDismissed;
            if (await ApplyMenuKeyEventAsync(ev, live, control, tty, ct).ConfigureAwait(false))
                continue;

            if (ev.Kind is KeyEngineEventKind.EnterMode
                or KeyEngineEventKind.LeaveMode
                or KeyEngineEventKind.HelpFilter
                or KeyEngineEventKind.PromptEdit
                or KeyEngineEventKind.CopyChanged
                or KeyEngineEventKind.CopyYank)
            {
                if (ev.Kind is KeyEngineEventKind.EnterMode
                    && ev.Mode is AttachClientMode.Copy
                    && ShouldSeedCopyOnEnter(live))
                    await SeedCopyModeAsync(live, control, ct).ConfigureAwait(false);

                if (ev.Kind is KeyEngineEventKind.LeaveMode && ev.Mode is AttachClientMode.Copy)
                    live.Engine.Copy.Reset();

                if (ev.Kind is KeyEngineEventKind.CopyYank && ev.Bytes is { Length: > 0 } osc)
                {
                    live.LastOsc52 = osc;
                    tty.WriteBytes(osc);
                    ShowClipboardToast(live, ev.PromptText ?? "copied");
                }

                if (ev.Kind is KeyEngineEventKind.EnterMode
                    && ev.Mode is AttachClientMode.Navigator
                        or AttachClientMode.WorkspacePicker
                        or AttachClientMode.TransferPicker)
                {
                    HydratePickerCatalog(live);
                }

                var restoreFrame = OverlayRestoreFrame(ev, settingsLeave, live);
                var noteSnapshot = OverlayLeaveNotesSnapshot(restoreFrame, live);
                if (ev.Kind is KeyEngineEventKind.EnterMode)
                    live.Engine.CommitPaintMode();
                if (ev.Kind is KeyEngineEventKind.EnterMode
                    or KeyEngineEventKind.LeaveMode
                    || overlayCommit)
                {
                    if (ShouldReportAttachClientMode(ev, overlayCommit))
                    {
                        await ReportAttachClientModeAsync(control, live, ct)
                            .ConfigureAwait(false);
                    }

                    await RefreshChromeForPaintModeAsync(control, live, tty, ct)
                        .ConfigureAwait(false);
                }

                ApplyOverlayPaint(tty, live, restoreFrame, noteSnapshot);
            }
        }

        return linked.IsCancellationRequested;
    }

    internal static async Task<bool> ApplyMenuKeyEventAsync(
        KeyEngineEvent ev,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        if (ev.Kind is KeyEngineEventKind.MenuMove && ev.Index is { } delta)
        {
            live.Mouse.Menu?.Move(delta);
            live.MouseMenu?.Move(delta);
            if (live.Engine.Mode is AttachClientMode.MobileSwitcher)
                ApplySwitcherSelectionDelta(live, delta, tty);

            if (tty is not null)
                PaintChrome(tty, live);
            return true;
        }

        if (ev.Kind is KeyEngineEventKind.MenuApply
            && live.Engine.Mode is AttachClientMode.MobileSwitcher
            && live.Chrome?.MobileSwitcher is { Open: true } switcher)
        {
            var row = SelectedSwitcherRow(switcher, live.SwitcherSelected);
            if (row is not null && row.Hit)
            {
                await ApplyChromeHitAsync(
                        ChromeHitTest.FromSwitcher(row),
                        live,
                        control,
                        tty,
                        ct)
                    .ConfigureAwait(false);
            }

            return true;
        }

        if (ev.Kind is KeyEngineEventKind.MenuApply
            && live.Engine.Mode is AttachClientMode.Navigate)
        {
            var selected = live.SidebarFrame?.Sections
                .FirstOrDefault(s => s.Id == SidebarTokenGrammar.SpacesId)
                ?.SelectedRowId
                ?? live.WorkspaceId;
            if (!string.IsNullOrWhiteSpace(selected))
            {
                await live.Dispatcher.FocusWorkspaceAsync(selected, ct).ConfigureAwait(false);
                await SyncFocusAsync(control, live, ct, observePending: false)
                    .ConfigureAwait(false);
                await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
            }

            return true;
        }

        if (ev.Kind is KeyEngineEventKind.MenuApply
            && (live.Mouse.Menu?.SelectedItem ?? live.MouseMenu?.SelectedItem) is { } applied)
        {
            var menu = live.Mouse.Menu ?? live.MouseMenu;
            await ApplyContextMenuItemAsync(
                    applied,
                    new MouseEngineResult(
                        MouseCommandKind.ApplyMenu,
                        Menu: menu,
                        MenuItem: applied,
                        PaneId: menu?.Kind is ContextMenuKind.Pane or ContextMenuKind.HiddenPane
                            ? menu.TargetId
                            : null,
                        TabId: menu?.Kind is ContextMenuKind.Tab ? menu.TargetId : null,
                        WorkspaceId: menu?.Kind is ContextMenuKind.Workspace ? menu.TargetId : null,
                        PlacementId: menu?.Kind is ContextMenuKind.Cube ? menu.TargetId : null),
                    live,
                    control,
                    tty,
                    ct)
                .ConfigureAwait(false);
            return true;
        }

        if (ev.Kind is KeyEngineEventKind.LeaveMode
            && ev.Mode is AttachClientMode.ContextMenu or AttachClientMode.GlobalMenu)
        {
            live.MouseMenu = null;
            live.Mouse.Reset();
        }

        return false;
    }

    internal static void ClearPendingCloseTargets(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.PendingClosePaneId = null;
        live.PendingCloseTabId = null;
        live.PendingCloseWorkspaceId = null;
        live.PendingCloseArmed = false;
        if (!live.Engine.HasConfirmAccept)
            live.Engine.ClearBoundCloseTargets();
    }

    internal static void ClearPendingRenameTargets(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.PendingRenamePaneId = null;
        live.PendingRenameTabId = null;
        live.PendingRenameWorkspaceId = null;
    }

    internal static void ArmPendingClose(
        AttachLiveState live,
        string? paneId,
        string? tabId,
        string? workspaceId = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.PendingClosePaneId = string.IsNullOrWhiteSpace(paneId) ? null : paneId;
        live.PendingCloseTabId = string.IsNullOrWhiteSpace(tabId) ? null : tabId;
        live.PendingCloseWorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId;
        live.PendingCloseArmed = live.PendingClosePaneId is not null
            || live.PendingCloseTabId is not null
            || live.PendingCloseWorkspaceId is not null;
        live.Engine.BindPendingCloseTarget(
            live.PendingClosePaneId,
            live.PendingCloseTabId,
            live.PendingCloseWorkspaceId);
    }

    internal static void ArmMenuFocusRestore(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.PendingMenuFocus is not null)
            return;
        live.PendingMenuFocus = new MenuFocusRestore(
            live.WorkspaceId,
            live.TabId,
            live.PaneId,
            live.LastPaneId);
    }

    internal static void ClearMenuFocusRestore(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.PendingMenuFocus = null;
    }

    internal static void RestoreMenuFocusIfArmed(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.PendingMenuFocus is not { } snap)
            return;
        // drops that workspace from the snapshot. Do not restore a closed
        // space after the operator accepts close.
        if (live.LastSnapshot is { } session
            && !string.IsNullOrWhiteSpace(snap.WorkspaceId)
            && !SnapshotHasWorkspace(session, snap.WorkspaceId))
        {
            live.PendingMenuFocus = null;
            return;
        }

        live.WorkspaceId = snap.WorkspaceId;
        live.TabId = snap.TabId;
        live.PaneId = snap.PaneId ?? live.PaneId;
        live.LastPaneId = snap.LastPaneId;
        live.Dispatcher.RevertFocus(snap.WorkspaceId, snap.TabId, snap.PaneId, snap.LastPaneId);
        live.PendingMenuFocus = null;
    }

    private static void RevertUncommittedDispatcherFocus(AttachLiveState live)
    {
        if (string.Equals(live.Dispatcher.PaneId, live.PaneId, StringComparison.Ordinal)
            && string.Equals(live.Dispatcher.TabId, live.TabId, StringComparison.Ordinal)
            && string.Equals(live.Dispatcher.WorkspaceId, live.WorkspaceId, StringComparison.Ordinal))
        {
            return;
        }

        live.Dispatcher.RevertFocus(live.WorkspaceId, live.TabId, live.PaneId, live.LastPaneId);
    }

    private static bool IsPromptOrConfirmMenuAction(KeyActionId action) =>
        action is KeyActionId.RenamePane
            or KeyActionId.RenameTab
            or KeyActionId.RenameWorkspace
            or KeyActionId.ClosePane
            or KeyActionId.CloseTab
            or KeyActionId.CloseWorkspace;

    private static void ApplyPendingModeEvent(AttachLiveState live, KeyEngineEvent ev)
    {
        if (ev.Kind is KeyEngineEventKind.LeaveMode && ev.Mode is AttachClientMode.ConfirmClose)
        {
            RestoreMenuFocusIfArmed(live);
            if (!live.Engine.HasConfirmAccept)
                ClearPendingCloseTargets(live);
        }
        else if (ev.Kind is KeyEngineEventKind.LeaveMode
            && ev.Mode is AttachClientMode.ConfirmMoveWork)
        {
            RestoreMenuFocusIfArmed(live);
            if (!live.Engine.HasConfirmMoveWorkAccept)
                live.PendingMoveWork = null;
        }
        else if (ev.Kind is KeyEngineEventKind.LeaveMode
            && ev.Mode is AttachClientMode.TransferPicker)
        {
            RestoreMenuFocusIfArmed(live);
            if (!live.Engine.HasTransferPick)
                live.PendingTransfer = null;
        }
        else if (ev.Kind is KeyEngineEventKind.EnterMode && ev.Mode is not AttachClientMode.ConfirmClose)
            ClearPendingCloseTargets(live);

        if (ev.Kind is KeyEngineEventKind.EnterMode)
        {
            switch (ev.Mode)
            {
                case AttachClientMode.ConfirmClose:
                    if (!live.PendingCloseArmed)
                    {
                        ArmPendingClose(
                            live,
                            live.Dispatcher.PaneId ?? live.PaneId,
                            live.Dispatcher.TabId ?? live.TabId,
                            live.Dispatcher.WorkspaceId ?? live.WorkspaceId);
                    }

                    break;
                case AttachClientMode.RenamePane:
                    live.PendingRenamePaneId ??= live.Dispatcher.PaneId ?? live.PaneId;
                    break;
                case AttachClientMode.RenameTab:
                    live.PendingRenameTabId ??= live.Dispatcher.TabId ?? live.TabId;
                    break;
                case AttachClientMode.RenameWorkspace:
                    live.PendingRenameWorkspaceId ??= live.Dispatcher.WorkspaceId ?? live.WorkspaceId;
                    break;
                case AttachClientMode.Navigator:
                    if (live.SidebarInput is { } input)
                    {
                        var catalog = SidebarLiveModel.Catalog(input, live.LinkedPlugins);
                        if (!live.Engine.Navigator.CatalogKeysEqual(catalog))
                            live.Engine.Navigator.SetCatalog(catalog);
                    }

                    break;
                case AttachClientMode.MobileSwitcher:
                    live.SwitcherScroll = 0;
                    live.SwitcherSelected = -1;
                    ClearPendingRenameTargets(live);
                    break;
                default:
                    ClearPendingRenameTargets(live);
                    break;
            }
        }
        else if (ev.Kind is KeyEngineEventKind.LeaveMode
            && ev.Mode is AttachClientMode.RenamePane
                or AttachClientMode.RenameTab
                or AttachClientMode.RenameWorkspace)
        {
            RestoreMenuFocusIfArmed(live);
            ClearPendingRenameTargets(live);
        }

        ReconcilePrefixAsciiInput(live);
    }

    internal static void ReconcilePrefixAsciiInput(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.PrefixAscii.Enabled =
            live.AttachConfig?.Experimental.SwitchAsciiInputSourceInPrefix == true;
        var wants = PrefixAsciiNeed.WantsAscii(live.Engine.Mode, live.Worktrees.Kind);
        live.PrefixAscii.ReconcileAndApply(wants, live.PrefixAsciiSource);
    }

    /// <summary>
    /// Restore the previous host input source on every attach exit.
    /// Idempotent. A missing live state is a no-op.
    /// </summary>
    internal static void RestorePrefixAsciiInput(AttachLiveState? live) =>
        live?.PrefixAsciiSource.Restore();

    /// <summary>
    /// The end of an attach. It restores the prefix input and the host
    /// terminal first, and then disposes the attach log. The log dispose
    /// drains the log queue for up to its drain timeout. Thus a stalled disk
    /// does not keep the terminal in raw mode after a detach.
    /// </summary>
    internal static void RestoreTerminalThenDrainLog(
        AttachLiveState? live,
        UnixRawTerminal? tty,
        IProcessLogSink attachLog)
    {
        try { RestorePrefixAsciiInput(live); }
        catch { /* ignore */ }

        try { tty?.Restore(); }
        catch { /* ignore */ }
        try { tty?.Dispose(); }
        catch { /* ignore */ }

        if (attachLog is IDisposable logDisposable)
        {
            try { logDisposable.Dispose(); }
            catch { /* ignore */ }
        }
    }

    private static bool IsEmptyRenameEnter(KeyActionRequest request) =>
        (request.Action is KeyActionId.RenamePane
            or KeyActionId.RenameTab
            or KeyActionId.RenameWorkspace)
        && string.IsNullOrEmpty(request.PromptText);

    private static bool IsFocusOnlyChromePlan(ChromeHitApplyResult plan) =>
        plan.Request is null
        && !plan.Detach
        && (!string.IsNullOrWhiteSpace(plan.FocusPaneId)
            || !string.IsNullOrWhiteSpace(plan.FocusWorkspaceId)
            || !string.IsNullOrWhiteSpace(plan.FocusTabId));

    private static bool IsRetargetingMouseCommand(MouseCommandKind kind) =>
        kind is MouseCommandKind.ApplyChromeHit
            or MouseCommandKind.SetSplitRatio
            or MouseCommandKind.MoveTab
            or MouseCommandKind.SetSidebarWidth
            or MouseCommandKind.SetSidebarSectionSplit
            or MouseCommandKind.ForwardSgr
            or MouseCommandKind.PreviewSplit
            or MouseCommandKind.PreviewSidebar
            or MouseCommandKind.PreviewSidebarSection;

    private static async Task<bool> PrepareMouseApplyAsync(
        MouseEngineResult result,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationTokenSource? linked,
        CancellationToken ct)
    {
        var mode = live.Engine.Mode;
        if (KeyEngine.HoldsPromptTarget(mode) && IsRetargetingMouseCommand(result.Kind))
        {
            if (result.Kind is MouseCommandKind.ApplyChromeHit && result.Hit is { } hit)
            {
                var plan = ChromeHitApply.Apply(
                    hit,
                    live.TabOverflowOffset,
                    live.Chrome?.TabBar.MaxOverflowOffset ?? 0);
                if (IsFocusOnlyChromePlan(plan))
                    return false;
            }
            else if (result.Kind is not MouseCommandKind.ApplyChromeHit)
            {
                return false;
            }
        }

        if (KeyEngine.IsDismissibleOverlay(mode)
            && mode is not AttachClientMode.Settings
            && IsRetargetingMouseCommand(result.Kind))
        {
            await DismissOverlayForMouseAsync(live, control, tty, linked, ct)
                .ConfigureAwait(false);
        }

        return true;
    }

    private static async Task DismissOverlayForMouseAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationTokenSource? linked,
        CancellationToken ct)
    {
        await ApplyModeEventsAsync(
                live.Engine.CancelInteractiveMode(),
                live,
                control,
                tty,
                linked,
                ct)
            .ConfigureAwait(false);
    }

    private static async Task ApplyModeEventsAsync(
        IReadOnlyList<KeyEngineEvent> events,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationTokenSource? linked,
        CancellationToken ct)
    {
        if (events.Count == 0)
            return;

        if (tty is not null)
        {
            if (linked is not null)
            {
                await ApplyEngineEventsAsync(tty, live, events, control, linked, ct)
                    .ConfigureAwait(false);
                return;
            }

            using var local = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await ApplyEngineEventsAsync(tty, live, events, control, local, ct)
                .ConfigureAwait(false);
            return;
        }

        foreach (var ev in events)
        {
            ApplyPendingModeEvent(live, ev);
            if (ev.Kind is KeyEngineEventKind.EnterMode or KeyEngineEventKind.HelpFilter)
                await HandleSettingsOverlayAsync(ev, live, control, ct).ConfigureAwait(false);
            var settingsLeave = ev.Kind is KeyEngineEventKind.HelpFilter
                && await TryLeaveSettingsAfterPatchAsync(live, control, ct).ConfigureAwait(false);
            var onboardingDone = ev.Kind is KeyEngineEventKind.HelpFilter
                && await TryCompleteOnboardingAsync(live, control, ct).ConfigureAwait(false);
            var releaseNotesOpen = ev.Kind is KeyEngineEventKind.HelpFilter
                && TryOpenReleaseNotesFromSettings(live);
            var releaseNotesDismissed = ev.Kind is KeyEngineEventKind.HelpFilter
                && ev.Filter == ReleaseNotesOverlayModel.FilterDismiss
                && TryDismissReleaseNotes(live);
            if (ev.Kind is KeyEngineEventKind.LeaveMode && ev.Mode is AttachClientMode.Copy)
                live.Engine.Copy.Reset();
            if (ev.Kind is KeyEngineEventKind.LeaveMode
                && ev.Mode is AttachClientMode.ConfirmMoveWork)
            {
                await CompletePendingMoveWorkAsync(live, control, tty, ct)
                    .ConfigureAwait(false);
            }

            if (ev.Kind is KeyEngineEventKind.LeaveMode
                && ev.Mode is AttachClientMode.TransferPicker)
            {
                await CompletePendingTransferPickAsync(live, control, tty, ct)
                    .ConfigureAwait(false);
            }

            if (ev.Kind is KeyEngineEventKind.LeaveMode
                || settingsLeave
                || onboardingDone
                || releaseNotesOpen
                || releaseNotesDismissed)
                live.Engine.CommitPaintMode();
            if (ShouldReportAttachClientMode(ev, settingsLeave || onboardingDone))
                await PublishClientModeAsync(control, live, ct).ConfigureAwait(false);
        }
    }

    internal static async Task ObserveSettingsIntegrationsAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (live.Engine.Mode is not AttachClientMode.Settings)
            return;
        if (live.Engine.Settings.ActivePage.Kind is not SettingsPageKind.Integrations)
            return;
        if (!AttachShellEndpointDispatch.SurfaceReady(live))
            return;

        if (live.WithPaint(() => IntegrationRefreshNeed(live)) is null)
            return;

        await RefreshSettingsIntegrationsAsync(live, control, ct, onlyIfNeeded: true)
            .ConfigureAwait(false);
        if (tty is not null)
            PaintChrome(tty, live);
    }

    /// <summary>
    /// True when <see cref="ObservePendingUnderGateAsync"/> has work to do.
    /// It reads the flags without the control gate. The render loop calls it
    /// for every event, so an event with no observe work does not wait for a
    /// gate holder such as the heartbeat or the lease renew.
    /// </summary>
    /// <remarks>
    /// A flag set after this check is served by the next render event. The
    /// heartbeat does not serve these flags. A writer that sets a flag while
    /// it holds the gate does not hand it to the event that is in progress:
    /// that event paints first, and the next event serves the flag.
    /// </remarks>
    internal static bool HasPendingObserveWork(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        // ObservePendingAsync clears a whitespace id, and an id with no
        // render subscription, under the gate. Keep that: any id counts.
        if (Volatile.Read(ref live.PendingObservePaneId) is not null
            && Volatile.Read(ref live.HoldPendingObserve) == 0)
        {
            return true;
        }

        if (live.PendingChromeRefresh)
            return true;

        return live.Engine.Mode is AttachClientMode.Settings
            && live.Engine.Settings.ActivePage.Kind is SettingsPageKind.Integrations;
    }

    internal static async Task ObservePendingUnderGateAsync(
        IAttachCommandPort render,
        AttachLiveState live,
        SemaphoreSlim controlGate,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        if (!HasPendingObserveWork(live))
            return;

        var gateStart = live.Latency.Now();
        await controlGate.WaitAsync(ct).ConfigureAwait(false);
        live.Latency.RecordHop(AttachLatencyHop.RenderGateWait, gateStart);
        try
        {
            await ObservePendingAsync(render, live, ct).ConfigureAwait(false);
            if (live.TakePendingChromeRefresh())
            {
                await RefreshChromeAsync(
                        LiveControlPort(live, render),
                        live,
                        tty,
                        ct)
                    .ConfigureAwait(false);
            }

            await ObserveSettingsIntegrationsAsync(
                    live,
                    LiveControlPort(live, render),
                    tty,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            controlGate.Release();
        }
    }

    internal static void BindCustomCommandContext(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.Dispatcher.ClientMode = AttachClientModePublication.PublishedToken(live);
        live.Dispatcher.PopupIsOpen = live.Engine.PopupOpen || live.PopupOpen;
        if (live.Chrome is { } chrome)
        {
            live.Dispatcher.AreaCols = Math.Max(1, chrome.Content.Cols);
            live.Dispatcher.AreaRows = Math.Max(1, chrome.Content.Rows);
        }

        live.Dispatcher.ActivePaneCwd = ReadPaneCwd(
            live.LastSnapshot,
            live.Dispatcher.PaneId ?? live.PaneId);
    }

    internal static string? ReadPaneCwd(JsonElement? snapshot, string? paneId)
    {
        if (snapshot is not { } snap || snap.ValueKind != JsonValueKind.Object)
            return null;
        if (string.IsNullOrWhiteSpace(paneId))
            return null;
        if (!snap.TryGetProperty("panes", out var panes) || panes.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var pane in panes.EnumerateArray())
        {
            if (pane.ValueKind != JsonValueKind.Object)
                continue;
            if (!pane.TryGetProperty("pane_id", out var idEl) || idEl.GetString() != paneId)
                continue;
            if (pane.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
            {
                var cwd = cwdEl.GetString();
                return string.IsNullOrWhiteSpace(cwd) ? null : cwd;
            }

            return null;
        }

        return null;
    }

    private static async Task RefreshChromeAfterCloseFaultAsync(
        KeyActionRequest request,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        if (request.Action is not (KeyActionId.CloseWorkspace or KeyActionId.CloseTab or KeyActionId.ClosePane))
            return;
        try
        {
            await SyncFocusAsync(control, live, ct, observePending: false).ConfigureAwait(false);
            await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    internal static async Task DispatchLayoutActionAsync(
        KeyActionRequest request,
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct,
        UnixRawTerminal? tty = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        BindCustomCommandContext(live);
        if (request.Action is KeyActionId.NewWorktree
            or KeyActionId.OpenWorktree
            or KeyActionId.RemoveWorktree)
        {
            await BeginWorktreeActionAsync(
                    request.Action,
                    FirstNonEmpty(request.TargetId, live.WorkspaceId),
                    live,
                    control,
                    tty,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        if (request.Action is KeyActionId.ClosePane)
        {
            var usePending = UsePendingCloseTarget(live);
            var pane = FirstNonEmpty(
                request.TargetId,
                usePending ? live.PendingClosePaneId : null,
                live.Dispatcher.PaneId,
                live.PaneId);
            ClearPendingCloseTargets(live);
            ClearMenuFocusRestore(live);
            if (string.IsNullOrWhiteSpace(pane))
            {
                ConsumeConfirmAccept(live);
                return;
            }

            if (TryConsumeCompletedEditScrollbackClose(live, pane))
                return;
            ConsumeConfirmAccept(live);
            var closingEditor = live.EditScrollback is { } overlay
                && string.Equals(pane, overlay.EditorPaneId, StringComparison.Ordinal);
            if (string.Equals(pane, live.Dispatcher.PaneId, StringComparison.Ordinal)
                || string.Equals(pane, live.PaneId, StringComparison.Ordinal))
            {
                await live.Dispatcher.HandleAsync(request, ct).ConfigureAwait(false);
            }
            else
            {
                await control.CallAsync(
                        ProtocolMethods.PaneClose,
                        new JsonObject { ["pane_id"] = pane },
                        ct)
                    .ConfigureAwait(false);
            }

            if (closingEditor)
            {
                await EditScrollbackLauncher.CompleteAsync(
                        live, control, alreadyClosed: true, ct)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (request.Action is KeyActionId.CloseTab)
        {
            var usePending = UsePendingCloseTarget(live);
            var tab = FirstNonEmpty(
                request.TargetId,
                usePending ? live.PendingCloseTabId : null,
                live.Dispatcher.TabId,
                live.TabId);
            ClearPendingCloseTargets(live);
            ClearMenuFocusRestore(live);
            if (string.IsNullOrWhiteSpace(tab))
            {
                ConsumeConfirmAccept(live);
                return;
            }

            ConsumeConfirmAccept(live);
            if (string.Equals(tab, live.Dispatcher.TabId, StringComparison.Ordinal)
                || string.Equals(tab, live.TabId, StringComparison.Ordinal))
            {
                await live.Dispatcher.HandleAsync(request, ct).ConfigureAwait(false);
                return;
            }

            await control.CallAsync(
                    ProtocolMethods.TabClose,
                    new JsonObject { ["tab_id"] = tab },
                    ct)
                .ConfigureAwait(false);
            return;
        }

        if (request.Action is KeyActionId.RenamePane && !string.IsNullOrEmpty(request.PromptText))
        {
            var pane = live.PendingRenamePaneId ?? live.Dispatcher.PaneId ?? live.PaneId;
            ClearPendingRenameTargets(live);
            ClearMenuFocusRestore(live);
            if (string.IsNullOrWhiteSpace(pane))
                return;
            await control.CallAsync(
                    ProtocolMethods.PaneRename,
                    new JsonObject
                    {
                        ["pane_id"] = pane,
                        ["label"] = request.PromptText,
                    },
                    ct)
                .ConfigureAwait(false);
            return;
        }

        if (request.Action is KeyActionId.RenameTab && !string.IsNullOrEmpty(request.PromptText))
        {
            var tab = live.PendingRenameTabId ?? live.Dispatcher.TabId ?? live.TabId;
            ClearPendingRenameTargets(live);
            ClearMenuFocusRestore(live);
            if (string.IsNullOrWhiteSpace(tab))
                return;
            await control.CallAsync(
                    ProtocolMethods.TabRename,
                    new JsonObject
                    {
                        ["tab_id"] = tab,
                        ["label"] = request.PromptText,
                    },
                    ct)
                .ConfigureAwait(false);
            return;
        }

        if (request.Action is KeyActionId.RenameWorkspace && !string.IsNullOrEmpty(request.PromptText))
        {
            var workspace = live.PendingRenameWorkspaceId
                ?? live.Dispatcher.WorkspaceId
                ?? live.WorkspaceId;
            ClearPendingRenameTargets(live);
            ClearMenuFocusRestore(live);
            if (string.IsNullOrWhiteSpace(workspace))
                return;
            await control.CallAsync(
                    ProtocolMethods.WorkspaceRename,
                    new JsonObject
                    {
                        ["workspace_id"] = workspace,
                        ["label"] = request.PromptText,
                    },
                    ct)
                .ConfigureAwait(false);
            return;
        }

        if (request.Action is KeyActionId.ToggleSidebar)
        {
            if (IsLiveNarrow(live))
                return;

            live.SidebarOpen = !live.SidebarOpen;
            live.SidebarCollapsed = !live.SidebarOpen;
            if (live.SidebarOpen)
            {
                live.SidebarRequestedWidth = SidebarHitModel.ClampWidth(
                    live.SidebarRequestedWidth > 0
                        ? live.SidebarRequestedWidth
                        : live.Ui.SidebarWidth,
                    live.Ui.SidebarMinWidth,
                    live.Ui.SidebarMaxWidth);
                live.SidebarWidth = live.SidebarRequestedWidth;
            }

            return;
        }

        if (request.Action is KeyActionId.Goto && !string.IsNullOrWhiteSpace(request.PromptText))
        {
            await ApplyGotoJumpAsync(request, live, control, ct).ConfigureAwait(false);
            return;
        }

        if (request.Action is KeyActionId.ReloadConfig)
        {
            await ReloadAttachConfigAsync(live, control, tty, ct).ConfigureAwait(false);
            return;
        }

        if (request.Action is KeyActionId.EditScrollback)
        {
            await EditScrollbackLauncher.LaunchAsync(live, control, ct)
                .ConfigureAwait(false);
            return;
        }

        if (request.Action is KeyActionId.AnnotateHandoff)
        {
            var selection = SelectionHandoffWriter.ExtractSelection(
                live.Engine.Copy,
                live.Mouse.Selection,
                request.PromptText);
            var written = SelectionHandoffFiles.WriteDefault(selection);
            if (!written.IsOk)
                live.Status.Set(StatusOwner.Command, StatusSeverity.Error, written.Error);
            return;
        }

        if (request.Action is KeyActionId.CloseWorkspace)
        {
            var usePending = UsePendingCloseTarget(live);
            var workspace = FirstNonEmpty(
                request.TargetId,
                usePending ? live.PendingCloseWorkspaceId : null,
                live.Dispatcher.WorkspaceId,
                live.WorkspaceId);
            ClearPendingCloseTargets(live);
            ClearMenuFocusRestore(live);
            if (string.IsNullOrWhiteSpace(workspace))
            {
                ConsumeConfirmAccept(live);
                return;
            }

            ConsumeConfirmAccept(live);
            ControlPlaneException? closeError = null;
            try
            {
                await live.Dispatcher.HandleAsync(
                        request with { TargetId = workspace },
                        ct)
                    .ConfigureAwait(false);
            }
            catch (ControlPlaneException ex)
            {
                closeError = ex;
            }

            // session snapshot after close_selected_workspace. Do not wait
            // for layout.export of a closed tab.
            await ApplySessionSnapshotToSidebarAsync(control, live, ct)
                .ConfigureAwait(false);
            if (closeError is not null)
                throw closeError;
            return;
        }

        await live.Dispatcher.HandleAsync(request, ct).ConfigureAwait(false);
    }

    private static bool UsePendingCloseTarget(AttachLiveState live) =>
        live.PendingCloseArmed
        && (live.Engine.Mode is AttachClientMode.ConfirmClose
            || live.Engine.HasConfirmAccept
            || !live.Ui.ConfirmClose);

    private static void ConsumeConfirmAccept(AttachLiveState live)
    {
        live.Engine.TakeConfirmAccept();
        live.Engine.ClearBoundCloseTargets();
    }

    internal static KeyActionRequest BindCloseRequestTarget(
        AttachLiveState live,
        KeyActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.TargetId))
            return request;
        var target = request.Action switch
        {
            KeyActionId.ClosePane => FirstNonEmpty(
                live.PendingClosePaneId,
                live.Dispatcher.PaneId,
                live.PaneId),
            KeyActionId.CloseTab => FirstNonEmpty(
                live.PendingCloseTabId,
                live.Dispatcher.TabId,
                live.TabId),
            KeyActionId.CloseWorkspace => FirstNonEmpty(
                live.PendingCloseWorkspaceId,
                live.Dispatcher.WorkspaceId,
                live.WorkspaceId),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(target) ? request : request with { TargetId = target };
    }

    private static bool TryConsumeCompletedEditScrollbackClose(AttachLiveState live, string pane)
    {
        if (!string.Equals(pane, live.CompletedEditScrollbackPaneId, StringComparison.Ordinal))
            return false;

        live.CompletedEditScrollbackPaneId = null;
        live.Engine.TakeConfirmAccept();
        live.Engine.ClearBoundCloseTargets();
        var restore = live.EditScrollbackRestorePaneId;
        live.EditScrollbackRestorePaneId = null;
        if (!string.IsNullOrWhiteSpace(restore))
            live.Dispatcher.FocusPane(restore);
        return true;
    }

    private static bool ShouldSerializeKeyFeed(AttachLiveState live) =>
        live.EditScrollback is not null
        || live.Engine.Mode is AttachClientMode.ConfirmClose
        || live.Engine.Mode is AttachClientMode.ConfirmMoveWork
        || live.Engine.Mode is AttachClientMode.TransferPicker;

    internal static async Task<(IReadOnlyList<KeyEngineEvent> Events, bool SerializeFeed)>
        FeedKeysAfterControlGateAsync(
            AttachLiveState live,
            List<byte> decoded,
            SemaphoreSlim controlGate,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentNullException.ThrowIfNull(controlGate);
        var serializeFeed = ShouldSerializeKeyFeed(live);
        if (serializeFeed)
        {
            var gateStart = live.Latency.Now();
            await controlGate.WaitAsync(ct).ConfigureAwait(false);
            live.Latency.RecordHop(AttachLatencyHop.StdinGateWait, gateStart);
        }
        try
        {
            IReadOnlyList<KeyEngineEvent> events = [];
            // The key engine lock only. A paint holds the paint lock while
            // it composes and encodes, and the host writer writes fd 1 off
            // both locks, so a slow host terminal does not delay this feed.
            live.WithKeyFeedEngine(() =>
            {
                live.SyncFocusedInputModes();
                events = live.Engine.Feed(CollectionsMarshal.AsSpan(decoded));
                events = RouteRecoverableCancelKeys(live, events, CollectionsMarshal.AsSpan(decoded));
                if (EntersCopyMode(events))
                    live.BeginCopySeedCapture();
            });
            return (events, serializeFeed);
        }
        catch
        {
            if (serializeFeed)
                controlGate.Release();
            throw;
        }
    }

    /// <summary>
    /// Merge adjacent <see cref="KeyEngineEventKind.SendPaneBytes"/> events
    /// for the same target. A paste decodes into one event per key; sending
    /// each alone costs a notification per byte. Nothing runs between
    /// adjacent byte events, so merging them preserves order and routing.
    /// </summary>
    internal static IReadOnlyList<KeyEngineEvent> CoalescePaneBytes(IReadOnlyList<KeyEngineEvent> events)
    {
        if (events.Count < 2)
            return events;

        List<KeyEngineEvent>? merged = null;
        var run = new List<byte>();
        for (var i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            if (ev.Kind != KeyEngineEventKind.SendPaneBytes || ev.Bytes is not { Length: > 0 } first)
            {
                merged?.Add(ev);
                continue;
            }

            // Paste bytes and typed bytes stay apart: the predictor treats
            // a paste as one unpredicted payload.
            var end = i + 1;
            while (end < events.Count
                   && events[end].Kind == KeyEngineEventKind.SendPaneBytes
                   && events[end].Bytes is { Length: > 0 }
                   && events[end].Paste == ev.Paste
                   && string.Equals(events[end].TargetId, ev.TargetId, StringComparison.Ordinal))
            {
                end++;
            }

            if (end == i + 1)
            {
                merged?.Add(ev);
                continue;
            }

            merged ??= [.. events.Take(i)];
            run.Clear();
            run.AddRange(first);
            // The run is paste only when every part is paste. Key bytes
            // must never go out marked as not keyboard input.
            var paste = ev.Paste;
            for (var j = i + 1; j < end; j++)
            {
                run.AddRange(events[j].Bytes!);
                paste &= events[j].Paste;
            }

            merged.Add(ev with { Bytes = [.. run], Paste = paste });
            i = end - 1;
        }

        return merged ?? events;
    }

    internal static async Task<bool> DispatchKeysUnderGateAsync(
        UnixRawTerminal tty,
        AttachLiveState live,
        List<byte> decoded,
        IAttachCommandPort control,
        SemaphoreSlim controlGate,
        CancellationTokenSource linked,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(linked);
        var (events, serializeFeed) = await FeedKeysAfterControlGateAsync(
                live, decoded, controlGate, ct)
            .ConfigureAwait(false);
        try
        {
            return await ApplyEngineEventsAsync(tty, live, events, control, linked, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            if (serializeFeed)
                controlGate.Release();
        }
    }

    internal static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    internal static async Task SyncFocusAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct,
        bool observePending = true)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        var nextPane = live.Dispatcher.PaneId;
        if (string.IsNullOrWhiteSpace(nextPane))
        {
            live.WorkspaceId = live.Dispatcher.WorkspaceId;
            live.TabId = live.Dispatcher.TabId;
            live.LastPaneId = live.Dispatcher.LastPaneId;
            live.InputLease = "";
            live.ResizeLease = "";
            live.Dispatcher.ResizeLease = "";
            live.Notifications.SetFocus(live.TabId, null);
            live.SeenReports.NoteTabInView(live.TabId);
            live.Status.Set(
                StatusOwner.Command,
                StatusSeverity.Error,
                string.IsNullOrWhiteSpace(live.WorkspaceId)
                    ? AttachClientViewCopy.EmptyTopology
                    : AttachClientViewCopy.EmptyTab);
            RememberAttachClientViewHint(live);
            await ClearLivePaneAsync(control, live, ct).ConfigureAwait(false);
            return;
        }

        if (nextPane == live.PaneId)
        {
            live.WorkspaceId = live.Dispatcher.WorkspaceId;
            live.TabId = live.Dispatcher.TabId;
            live.LastPaneId = live.Dispatcher.LastPaneId;
            live.Notifications.SetFocus(live.TabId, live.PaneId);
            live.SeenReports.NoteTabInView(live.TabId);
            await FlushSeenReportAsync(control, live, ct).ConfigureAwait(false);
            return;
        }

        var prevWorkspace = live.WorkspaceId;
        var prevTab = live.TabId;
        var prevPane = live.PaneId;
        var prevLast = live.LastPaneId;
        try
        {
            var focused = await control.CallAsync(
                    ProtocolMethods.PaneFocus,
                    new JsonObject { ["pane_id"] = nextPane },
                    ct)
                .ConfigureAwait(false);
            live.Dispatcher.ApplyPane(focused);
            if (string.IsNullOrWhiteSpace(TryJsonString(focused, "workspace_id"))
                || string.IsNullOrWhiteSpace(TryJsonString(focused, "tab_id")))
            {
                try
                {
                    var snap = await control.CallAsync(ProtocolMethods.SessionSnapshot, null, ct)
                        .ConfigureAwait(false);
                    live.Dispatcher.ApplyGraphForPane(snap, nextPane);
                }
                catch (ControlPlaneException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            live.WorkspaceId = live.Dispatcher.WorkspaceId;
            live.TabId = live.Dispatcher.TabId;
            live.PaneId = nextPane;
            live.LastPaneId = live.Dispatcher.LastPaneId;
            live.InputLease = "";
            live.ResizeLease = "";
            live.Dispatcher.ResizeLease = "";
            live.Notifications.SetFocus(live.TabId, live.PaneId);
            // pane.focus marked the focused pane. The other panes of the tab in view are reported now.
            live.SeenReports.Forget(nextPane);
            live.SeenReports.NoteTabInView(live.TabId);
            await FlushSeenReportAsync(control, live, ct).ConfigureAwait(false);
            live.DiscardLiveFrame();
            live.PendingObservePaneId = nextPane;

            try
            {
                if (!string.IsNullOrWhiteSpace(live.ControlSub))
                {
                    await control.CallAsync(
                            ProtocolMethods.TerminalObserve,
                            new JsonObject
                            {
                                ["pane_id"] = nextPane,
                                ["subscription_id"] = live.ControlSub,
                                ["replace"] = true,
                            },
                            ct)
                        .ConfigureAwait(false);
                }
            }
            catch (ControlPlaneException)
            {
                // control attachment is best-effort after a graph RPC
            }

            var render = live.RenderPort ?? control;
            if (observePending)
            {
                Interlocked.Exchange(ref live.HoldPendingObserve, 0);
                await ObservePendingAsync(render, live, ct).ConfigureAwait(false);
            }
            else
            {
                Interlocked.Exchange(ref live.HoldPendingObserve, 1);
            }

            live.WakeRender?.Invoke();
            if (live.Engine.Mode is AttachClientMode.Copy)
            {
                live.Engine.Copy.Reset();
                await SeedCopyModeAsync(live, control, ct).ConfigureAwait(false);
            }

            RememberAttachClientViewHint(live);
        }
        catch (InvalidOperationException ex)
        {
            FailFocusClaim(live, prevWorkspace, prevTab, prevPane, prevLast, ex.Message);
        }
        catch (ControlPlaneException)
        {
            live.Dispatcher.RevertFocus(prevWorkspace, prevTab, prevPane, prevLast);
            throw;
        }
    }

    internal static void RememberAttachClientViewHint(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(live.ConnectedPlacementId)
            || string.IsNullOrWhiteSpace(live.ConnectedBootId))
        {
            return;
        }

        live.ClientViewHints.Remember(
            live.ConnectedPlacementId,
            live.ConnectedBootId,
            live.WorkspaceId ?? live.Dispatcher.WorkspaceId,
            live.TabId ?? live.Dispatcher.TabId,
            live.PaneId ?? live.Dispatcher.PaneId);
    }

    private static void FailFocusClaim(
        AttachLiveState live,
        string? workspaceId,
        string? tabId,
        string paneId,
        string? lastPaneId,
        string? outcome)
    {
        live.Dispatcher.RevertFocus(workspaceId, tabId, paneId, lastPaneId);
        live.Status.Set(
            StatusOwner.Command,
            StatusSeverity.Error,
            FormatStatus(
                string.IsNullOrWhiteSpace(outcome)
                    ? "lease claim denied"
                    : "lease claim " + outcome));
    }

    private static async Task ReleaseLeaseQuietAsync(
        IAttachCommandPort control,
        string? leaseId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return;
        try
        {
            await control.CallAsync(
                    ProtocolMethods.RuntimeLeaseRelease,
                    new JsonObject { ["lease_id"] = leaseId },
                    ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
        }
    }

    private static async Task ClearLivePaneAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct)
    {
        var oldInput = live.InputLease;
        var oldResize = live.ResizeLease;
        live.Renew.Untrack(oldInput);
        live.Renew.Untrack(oldResize);
        live.PaneId = "";
        live.InputLease = "";
        live.ResizeLease = "";
        live.LastPaneId = null;
        live.DiscardLiveFrame();
        live.Dispatcher.ResizeLease = null;
        live.Dispatcher.RevertFocus(live.WorkspaceId, live.TabId, paneId: null, lastPaneId: null);
        live.PendingObservePaneId = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(oldInput))
            {
                await control.CallAsync(
                        ProtocolMethods.RuntimeLeaseRelease,
                        new JsonObject { ["lease_id"] = oldInput },
                        ct)
                    .ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(oldResize))
            {
                await control.CallAsync(
                        ProtocolMethods.RuntimeLeaseRelease,
                        new JsonObject { ["lease_id"] = oldResize },
                        ct)
                    .ConfigureAwait(false);
            }
        }
        catch (ControlPlaneException)
        {
        }
    }

    internal static string? ChooseFocusedWorkspaceId(JsonElement snap) =>
        TryFirstWorkspaceId(snap);

    internal static string? ChooseWorkspaceIdForPane(JsonElement snap, string? paneId) =>
        TryPaneString(snap, paneId, "workspace_id");

    internal static string? ChooseTabIdForPane(JsonElement snap, string? paneId) =>
        TryPaneString(snap, paneId, "tab_id");

    private static string? TryPaneString(JsonElement snap, string? paneId, string property)
    {
        if (string.IsNullOrWhiteSpace(paneId)
            || snap.ValueKind != JsonValueKind.Object
            || !snap.TryGetProperty("panes", out var panes)
            || panes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var pane in panes.EnumerateArray())
        {
            if (pane.ValueKind != JsonValueKind.Object)
                continue;
            if (!pane.TryGetProperty("pane_id", out var id) || id.GetString() != paneId)
                continue;
            if (pane.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text)
            {
                return text;
            }

            return null;
        }

        return null;
    }

    internal static bool ShouldDrivePane(AttachLiveState live, string paneId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        if (live.LastSnapshot is not { } snap)
            return true;
        if (!TryPaneAlive(snap, paneId, out var alive))
            return true;
        return alive;
    }

    internal static string? ChooseFocusedTabId(JsonElement snap)
    {
        if (snap.ValueKind != JsonValueKind.Object)
            return null;
        if (snap.TryGetProperty("focused_tab_id", out var ft)
            && ft.ValueKind == JsonValueKind.String
            && ft.GetString() is { Length: > 0 } tabId)
        {
            return tabId;
        }

        if (snap.TryGetProperty("tabs", out var tabs) && tabs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabs.EnumerateArray())
            {
                if (tab.ValueKind == JsonValueKind.Object
                    && tab.TryGetProperty("tab_id", out var id)
                    && id.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    internal static string? ChooseFocusedPaneId(JsonElement snap)
    {
        if (snap.ValueKind != JsonValueKind.Object)
            return null;

        if (snap.TryGetProperty("focused_tab_id", out var ft)
            && ft.ValueKind == JsonValueKind.String
            && ft.GetString() is { } tabId
            && snap.TryGetProperty("tabs", out var tabs)
            && tabs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabs.EnumerateArray())
            {
                if (tab.ValueKind != JsonValueKind.Object)
                    continue;
                if (!tab.TryGetProperty("tab_id", out var id) || id.GetString() != tabId)
                    continue;
                if (tab.TryGetProperty("focused_pane_id", out var fp)
                    && fp.ValueKind == JsonValueKind.String
                    && fp.GetString() is { Length: > 0 } pane
                    && IsAlivePane(snap, pane))
                {
                    return pane;
                }
            }
        }

        if (snap.TryGetProperty("panes", out var panes) && panes.ValueKind == JsonValueKind.Array)
        {
            foreach (var pane in panes.EnumerateArray())
            {
                if (pane.ValueKind == JsonValueKind.Object
                    && pane.TryGetProperty("pane_id", out var pid)
                    && pid.GetString() is { Length: > 0 } id
                    && IsAlivePane(pane))
                {
                    return id;
                }
            }
        }

        return null;
    }

    private static int CountAlivePanes(JsonElement snap)
    {
        if (snap.ValueKind != JsonValueKind.Object
            || !snap.TryGetProperty("panes", out var panes)
            || panes.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var n = 0;
        foreach (var pane in panes.EnumerateArray())
        {
            if (IsAlivePane(pane))
                n++;
        }

        return n;
    }

    private static bool IsAlivePane(JsonElement pane) =>
        pane.ValueKind == JsonValueKind.Object
        && pane.TryGetProperty("alive", out var alive)
        && alive.ValueKind == JsonValueKind.True;

    private static bool IsAlivePane(JsonElement snap, string paneId) =>
        TryPaneAlive(snap, paneId, out var alive) && alive;

    /// <summary>
    /// Returns false when the snapshot has no pane roster. An empty
    /// <c>session.snapshot</c> object is not evidence that a visible pane is dead.
    /// </summary>
    private static bool TryPaneAlive(JsonElement snap, string paneId, out bool alive)
    {
        alive = false;
        if (snap.ValueKind != JsonValueKind.Object
            || !snap.TryGetProperty("panes", out var panes)
            || panes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var pane in panes.EnumerateArray())
        {
            if (pane.ValueKind != JsonValueKind.Object)
                continue;
            if (!pane.TryGetProperty("pane_id", out var id) || id.GetString() != paneId)
                continue;
            if (pane.TryGetProperty("alive", out var flag) && flag.ValueKind == JsonValueKind.False)
            {
                alive = false;
                return true;
            }

            alive = true;
            return true;
        }

        alive = false;
        return true;
    }

    private static string? TryFirstPaneId(JsonElement snap)
    {
        if (snap.ValueKind != JsonValueKind.Object
            || !snap.TryGetProperty("panes", out var panes)
            || panes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var pane in panes.EnumerateArray())
        {
            if (pane.ValueKind == JsonValueKind.Object
                && pane.TryGetProperty("pane_id", out var pid)
                && pid.GetString() is { Length: > 0 } id)
            {
                return id;
            }
        }

        return null;
    }

    private static string? TryFirstWorkspaceId(JsonElement snap)
    {
        if (snap.ValueKind != JsonValueKind.Object)
            return null;
        if (snap.TryGetProperty("focused_workspace_id", out var fw)
            && fw.ValueKind == JsonValueKind.String
            && fw.GetString() is { Length: > 0 } focused)
        {
            return focused;
        }

        if (snap.TryGetProperty("workspaces", out var wss) && wss.ValueKind == JsonValueKind.Array)
        {
            foreach (var ws in wss.EnumerateArray())
            {
                if (ws.ValueKind == JsonValueKind.Object
                    && ws.TryGetProperty("workspace_id", out var id)
                    && id.GetString() is { Length: > 0 } wsId)
                {
                    return wsId;
                }
            }
        }

        return null;
    }

    internal readonly record struct StallReconnectResult(
        bool Ok,
        ControlPlaneClient? Render,
        ControlPlaneClient? Control,
        string? Detail);

    internal async Task<StallReconnectResult> ReconnectAfterStallAsync(
        MuxReadyInfo ready,
        ControlPlaneAttachCommandPort commandPort,
        AttachLiveState live,
        SnapshotAssembler assembler,
        UnixRawTerminal tty,
        SemaphoreSlim controlGate,
        LeaseRenewLoop renew,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ready);
        ArgumentNullException.ThrowIfNull(commandPort);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(assembler);
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(controlGate);
        ArgumentNullException.ThrowIfNull(renew);

        if (BlocksPresentationInput(live))
        {
            return new StallReconnectResult(
                false,
                null,
                null,
                live.StatusError ?? "Selected Placement owner is unavailable.");
        }

        if (RequiresSshReconnectPath(live)
            && string.IsNullOrWhiteSpace(live.ActiveMuxSocketPath))
        {
            return new StallReconnectResult(
                false,
                null,
                null,
                live.StatusError ?? FormatStallCopy(live));
        }

        var request = BuildReconnectRequest(live);
        if (request is null)
            return new StallReconnectResult(false, null, null, "reconnect failed");

        var offer = await HandshakeReconnectAsync(request, ct).ConfigureAwait(false);
        if (offer is null)
            return new StallReconnectResult(false, null, null, "reconnect failed");

        if (BlocksPresentationInput(live))
        {
            return new StallReconnectResult(
                false,
                null,
                null,
                live.StatusError ?? "Selected Placement owner is unavailable.");
        }

        string? reconnectSocket;
        if (RequiresSshReconnectPath(live))
        {
            reconnectSocket = live.ActiveMuxSocketPath;
            if (string.IsNullOrWhiteSpace(reconnectSocket))
            {
                return new StallReconnectResult(
                    false,
                    null,
                    null,
                    live.StatusError ?? FormatStallCopy(live));
            }
        }
        else
        {
            reconnectSocket = ready.SocketPath;
        }

        if (string.IsNullOrWhiteSpace(reconnectSocket))
            return new StallReconnectResult(false, null, null, "reconnect failed");

        ControlPlaneClient? client = null;
        try
        {
            client = _clientFactory(reconnectSocket);
            ApplyAttachReceivePolicy(live, client);
            await client.ConnectAsync(ct).ConfigureAwait(false);
            // A cancelled reconnect changes no shared state and paints nothing.
            // Each step that changes the live view checks the token first.
            ct.ThrowIfCancellationRequested();
            commandPort.ReplaceClient(client);
            // new stream+writer; registry.rs:147-168 insert() replaces the previous
            // transport. Keep LoggingAttachCommandPort on RenderPort so
            // LiveControlPort still logs chrome mouse. PaintChrome below still
            // composes one host-size cell frame, then encodes once.
            var loggingPort = new LoggingAttachCommandPort(commandPort, live);
            // The live fields describe the active endpoint. A new Local
            // connection changes them only while Local is the active endpoint.
            // Otherwise it changes LocalRoot, and the active cube stays as is.
            var localActive = IsLocalEndpointActive(live);
            if (localActive)
                live.WakeRender = client.WakeRead;
            live.ReconnectPort = _reconnect;

            var snap = await EnsurePaneAndSnapshotAsync(client, ct).ConfigureAwait(false);
            var paneId = ChooseFocusedPaneId(snap);
            if (string.IsNullOrWhiteSpace(paneId))
                paneId = live.PaneId;
            if (string.IsNullOrWhiteSpace(paneId))
            {
                live.Connections.Unbind(client);
                await client.DisposeAsync().ConfigureAwait(false);
                return new StallReconnectResult(false, null, null, "reconnect failed");
            }

            var inputLease = "";
            var resizeLease = "";
            ct.ThrowIfCancellationRequested();
            renew.Untrack((localActive ? live.InputLease : live.LocalRoot?.InputLease) ?? "");
            renew.Untrack((localActive ? live.ResizeLease : live.LocalRoot?.ResizeLease) ?? "");
            if (localActive)
            {
                live.PaneId = paneId;
                live.InputLease = inputLease;
                live.ResizeLease = resizeLease;
                live.Dispatcher.ResizeLease = resizeLease;
            }

            var snapshot = offer?.SnapshotPaint ?? true;
            var lastReceived = LastReconnectRequest?.LastReceived ?? _reconnect.LastReceived;
            ClearLocalOverlay(live, resetEventFence: true);
            // The mux can have restarted: the next scroll events can come
            // from a mux of another epoch.
            live.NoteScrollEventSubscriptionStarted();
            var subscribed = await SubscribeAsync(
                    client,
                    ["control", "lifecycle", "render"],
                    ct,
                    lastReceived,
                    snapshot,
                    refreshed =>
                    {
                        ct.ThrowIfCancellationRequested();
                        snap = refreshed;
                        ApplyRecoveredSessionSnapshot(live, tty, refreshed);
                        // Observe and resize below must target a pane that the
                        // refreshed snapshot still has.
                        paneId = live.PaneId;
                    })
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(subscribed.AttachClientId))
            {
                live.Connections.Unbind(client);
                await client.DisposeAsync().ConfigureAwait(false);
                return new StallReconnectResult(false, null, null, "reconnect failed");
            }

            var identity = new LocalConnectionIdentity
            {
                ControlSub = subscribed.SubscriptionId,
                RenderSub = subscribed.SubscriptionId,
                AttachClientId = subscribed.AttachClientId,
                PaneId = paneId,
                InputLease = inputLease,
                ResizeLease = resizeLease,
                Port = loggingPort,
            };
            ct.ThrowIfCancellationRequested();
            ApplyLocalConnectionIdentity(live, client, identity);
            if (localActive)
                await ReportAttachClientModeAsync(commandPort, live, ct).ConfigureAwait(false);
            // Hello
            // plus surface-on restore is_active_shell_client.
            try
            {
                await ActivateLocalAttachSurfaceAsync(client, live, tty, ct, identity)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException
                or ObjectDisposedException
                or ControlPlaneException
                or ControlPlaneClientTimeoutException
                or InvalidOperationException)
            {
                live.Connections.Unbind(client);
                await client.DisposeAsync().ConfigureAwait(false);
                return new StallReconnectResult(
                    false, null, null, ex.Message);
            }

            await ObserveAsync(client, paneId, subscribed.SubscriptionId, ct).ConfigureAwait(false);
            _ = client.DrainPendingEvents();
            live.OverlayPaneId = null;
            assembler.ConsumeReanchorRequest();
            live.ResetAppliedBlit();
            // The visible set of a cube belongs to the cube subscription. The
            // restore of Local publishes the Local visible set.
            if (localActive)
                await ObserveVisiblePanesAsync(commandPort, live, ct).ConfigureAwait(false);
            if (tty.TryGetSize(out var cols, out var rows) && cols > 0 && rows > 0)
            {
                await ResizeAsync(
                        client, controlGate, paneId, resizeLease, cols, rows, ct,
                        ignoreErrors: true)
                    .ConfigureAwait(false);
            }

            // The resize ignores its errors, a cancel too.
            ct.ThrowIfCancellationRequested();
            PaintChrome(tty, live);
            if (offer is not null)
            {
                var admitted = _reconnect.AdmitWithoutInputLease();
                if (!admitted.Ok)
                {
                    live.Connections.Unbind(client);
                    await client.DisposeAsync().ConfigureAwait(false);
                    return new StallReconnectResult(
                        false, null, null, admitted.Detail ?? "reconnect failed");
                }

                var complete = _reconnect.CompleteReplay(painted: true);
                if (!complete.Ok)
                {
                    live.Connections.Unbind(client);
                    await client.DisposeAsync().ConfigureAwait(false);
                    return new StallReconnectResult(
                        false, null, null, complete.Detail ?? "reconnect failed");
                }
            }

            ct.ThrowIfCancellationRequested();
            CalledServerStop = false;
            return new StallReconnectResult(true, client, client, null);
        }
        catch (Exception ex) when (ex is IOException
            or ObjectDisposedException
            or ControlPlaneException
            or ControlPlaneClientTimeoutException
            or InvalidOperationException)
        {
            if (client is not null)
            {
                try { live.Connections.Unbind(client); await client.DisposeAsync().ConfigureAwait(false); }
                catch { /* ignore */ }
            }

            return new StallReconnectResult(false, null, null, "reconnect failed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user left while the reconnect ran. Close the new client.
            if (client is not null)
            {
                try { live.Connections.Unbind(client); await client.DisposeAsync().ConfigureAwait(false); }
                catch { /* ignore */ }
            }

            throw;
        }
    }

    internal readonly record struct AttachSubscribeResult(string SubscriptionId, string? AttachClientId);

    /// <summary>
    /// Reads <c>subscription_id</c> and <c>attach_client_id</c> as distinct
    /// fields. Never copies the subscription id into the attach client id.
    /// </summary>
    internal static AttachSubscribeResult ReadSubscribeResult(JsonElement result)
    {
        if (!result.TryGetProperty("subscription_id", out var id)
            || id.GetString() is not { Length: > 0 } sub)
        {
            throw new InvalidOperationException("events.subscribe returned no subscription_id.");
        }

        string? attachClientId = null;
        if (result.TryGetProperty("attach_client_id", out var client)
            && client.ValueKind == JsonValueKind.String
            && client.GetString() is { Length: > 0 } parsed)
        {
            attachClientId = parsed;
        }

        return new AttachSubscribeResult(sub, attachClientId);
    }

    internal static Task<AttachSubscribeResult> SubscribeDestClientAsync(
        ControlPlaneClient client,
        CancellationToken ct) =>
        SubscribeAsync(client, ["control", "lifecycle", "render"], ct);

    /// <summary>
    /// Apply a snapshot taken after <c>cursor_expired</c> to the live view and paint it.
    /// </summary>
    internal static void ApplyRecoveredSessionSnapshot(
        AttachLiveState live,
        UnixRawTerminal? tty,
        JsonElement snap)
    {
        ArgumentNullException.ThrowIfNull(live);
        var paneId = ChooseFocusedPaneId(snap);
        if (string.IsNullOrWhiteSpace(paneId))
            paneId = live.PaneId;
        var workspaceId = ChooseWorkspaceIdForPane(snap, paneId) ?? ChooseFocusedWorkspaceId(snap);
        var tabId = ChooseTabIdForPane(snap, paneId) ?? ChooseFocusedTabId(snap);
        if (!string.IsNullOrWhiteSpace(workspaceId))
            live.WorkspaceId = workspaceId;
        if (!string.IsNullOrWhiteSpace(tabId))
            live.TabId = tabId;
        if (!string.IsNullOrWhiteSpace(paneId))
            live.PaneId = paneId;
        live.WindowTitleOverride = ReadWindowTitleOverride(snap);
        live.Dispatcher.RebindTarget(live.WorkspaceId, live.TabId, live.PaneId, live.ResizeLease);
        RebuildSidebar(live, snap, requestGit: false, hydratePicker: false);
        if (tty is not null && live.ChromeEnabled)
            PaintChrome(tty, live);
    }

    internal static async Task<AttachSubscribeResult> SubscribeAsync(
        ControlPlaneClient client,
        IReadOnlyList<string> types,
        CancellationToken ct,
        IReadOnlyList<ChannelCursor>? lastReceived = null,
        bool snapshotPaint = true,
        Action<JsonElement>? onSnapshot = null)
    {
        var parameters = BuildEventsSubscribeParams(types, lastReceived, snapshotPaint);
        try
        {
            var result = await client.CallAsync(ProtocolMethods.EventsSubscribe, parameters, ct)
                .ConfigureAwait(false);
            return ReadSubscribeResult(result);
        }
        catch (ControlPlaneException ex) when (ex.ErrorCode == ProtocolErrors.CursorExpired)
        {
            // Subscribe at the live edge first, then read the snapshot. Events that
            // arrive between the two apply on top of the newer snapshot, so no
            // change is lost and a further expiry cannot happen.
            var live = await client.CallAsync(
                    ProtocolMethods.EventsSubscribe,
                    BuildEventsSubscribeParams(types, lastReceived: null, snapshotPaint: true),
                    ct)
                .ConfigureAwait(false);
            var subscribed = ReadSubscribeResult(live);
            var snap = await client.CallAsync(ProtocolMethods.SessionSnapshot, ct: ct)
                .ConfigureAwait(false);
            onSnapshot?.Invoke(snap);
            return subscribed;
        }
    }

    private static Task<string> ClaimLeaseAsync(
        ControlPlaneClient client,
        string paneId,
        string scope,
        CancellationToken ct,
        bool takeover = false) =>
        ClaimLeaseAsync(new ControlPlaneAttachCommandPort(client), paneId, scope, ct, takeover);

    private static Task<LeaseClaimAttempt> TryClaimLeaseAsync(
        ControlPlaneClient client,
        string paneId,
        string scope,
        CancellationToken ct,
        bool takeover = false) =>
        TryClaimLeaseAsync(new ControlPlaneAttachCommandPort(client), paneId, scope, ct, takeover);

    private static async Task<string> ClaimLeaseAsync(
        IAttachCommandPort client,
        string paneId,
        string scope,
        CancellationToken ct,
        bool takeover = false)
    {
        var attempt = await TryClaimLeaseAsync(client, paneId, scope, ct, takeover).ConfigureAwait(false);
        if (!attempt.Granted || string.IsNullOrWhiteSpace(attempt.LeaseId))
        {
            throw new InvalidOperationException(
                $"runtime.lease.claim {scope} failed: {attempt.Outcome}");
        }

        return attempt.LeaseId;
    }

    private static async Task<LeaseClaimAttempt> TryClaimLeaseAsync(
        IAttachCommandPort client,
        string paneId,
        string scope,
        CancellationToken ct,
        bool takeover = false)
    {
        var result = await client.CallAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["scope"] = scope,
                    ["ttl_ms"] = LeaseTtlMs,
                    ["takeover"] = takeover,
                },
                ct)
            .ConfigureAwait(false);
        var outcome = result.TryGetProperty("outcome", out var o) && o.ValueKind == JsonValueKind.String
            ? o.GetString()
            : null;
        if (result.TryGetProperty("lease_id", out var id)
            && id.ValueKind == JsonValueKind.String
            && id.GetString() is { Length: > 0 } lease)
        {
            return new LeaseClaimAttempt(true, lease, outcome ?? "granted");
        }

        return new LeaseClaimAttempt(false, null, outcome ?? "unknown");
    }

    private readonly record struct LeaseClaimAttempt(bool Granted, string? LeaseId, string Outcome);

    private static Task<JsonElement> ObserveAsync(
        ControlPlaneClient client,
        string paneId,
        string subscriptionId,
        CancellationToken ct) =>
        client.CallAsync(
            ProtocolMethods.TerminalObserve,
            new JsonObject
            {
                ["pane_id"] = paneId,
                ["subscription_id"] = subscriptionId,
                ["replace"] = true,
            },
            ct);

    private static Task<JsonElement> ControlAsync(
        ControlPlaneClient client,
        string paneId,
        string leaseId,
        string subscriptionId,
        CancellationToken ct) =>
        client.CallAsync(
            ProtocolMethods.TerminalControl,
            new JsonObject
            {
                ["pane_id"] = paneId,
                ["lease_id"] = leaseId,
                ["subscription_id"] = subscriptionId,
                ["replace"] = true,
            },
            ct);

    private static bool TryGetEventType(JsonElement ev, out string? type, out JsonElement payload)
    {
        type = null;
        payload = default;
        if (ev.ValueKind != JsonValueKind.Object)
            return false;
        if (!ev.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
            return false;
        if (!p.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
            return false;
        type = t.GetString();
        if (p.TryGetProperty("payload", out var pay))
            payload = pay;
        return !string.IsNullOrEmpty(type);
    }

    private static long ReadEventSeq(JsonElement ev)
    {
        if (ev.ValueKind != JsonValueKind.Object
            || !ev.TryGetProperty("params", out var p)
            || p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty("seq", out var seq)
            || !seq.TryGetInt64(out var value)
            || value < 0)
        {
            return 0;
        }

        return value;
    }

    internal static bool IsStaleControlPopupOpened(AttachLiveState live, JsonElement ev)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!TryGetEventType(ev, out var type, out var payload)
            || !string.Equals(type, ProtocolEventTypes.PopupLifecycle, StringComparison.Ordinal))
        {
            return false;
        }

        var state = payload.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString()
            : null;
        if (!string.Equals(state, "opened", StringComparison.Ordinal))
            return false;

        var seq = ReadEventSeq(ev);
        if (live.PopupClosedSeq > 0 && seq <= live.PopupClosedSeq)
            return true;

        return !live.PopupOpen && live.PopupSawClosed && seq <= live.PopupClosedSeq;
    }

    internal static bool IsStaleControlPopupClosed(AttachLiveState live, JsonElement ev)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!TryGetEventType(ev, out var type, out var payload)
            || !string.Equals(type, ProtocolEventTypes.PopupLifecycle, StringComparison.Ordinal))
        {
            return false;
        }

        var state = payload.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString()
            : null;
        if (!string.Equals(state, "closed", StringComparison.Ordinal))
            return false;

        var seq = ReadEventSeq(ev);
        return seq > 0 && seq < live.PopupOpenedSeq;
    }
}

internal sealed class AttachTtyPrelude
{
    public required IProcessLogSink AttachLog { get; init; }

    public KeyBindingTable? Table { get; init; }

    public ThemeRuntime? Theme { get; init; }

    public int ExitCode { get; init; }
}

internal sealed record MenuFocusRestore(
    string? WorkspaceId,
    string? TabId,
    string? PaneId,
    string? LastPaneId);

internal struct HostThemeDecode
{
    public HostAppearance? Explicit;
    public HostAppearance? Inferred;
    public HostRgb? Foreground;
    public HostRgb? Background;
    public HostPalette Palette;
    public bool OscDropped;
    public int ColorReports;
    public bool HostStatusReply;

    /// <summary>
    /// True when the decode carries a value that <c>MergeHostTheme</c> or
    /// <c>ApplyHostThemeDecode</c> reads. <c>OscDropped</c>,
    /// <c>ColorReports</c> and <c>HostStatusReply</c> do not count.
    /// </summary>
    internal readonly bool HasThemeFields =>
        Explicit is not null
        || Inferred is not null
        || Foreground is not null
        || Background is not null
        || Palette.HasAnyColor();
}

internal readonly record struct HostThemeApplyResult(bool ChromeChanged, bool MuxThemeChanged);

internal enum SidebarWidthSource
{
    Config,
    Manual,
}

internal enum SidebarSectionSplitSource
{
    Config,
    Manual,
}

internal sealed record LastPaintedPane(
    AssembledSnapshot Frame,
    int OriginCol,
    int OriginRow,
    int ClipCols,
    int ClipRows);

internal readonly record struct DeclinedPaneResize(int Cols, int Rows, string Owner);

internal sealed class FrameDropNotice
{
    public string Reason { get; init; } = "";

    public int Suppressed { get; set; }

    /// <summary>The drops of this kind in a row: the logged one and the suppressed ones.</summary>
    public long Count => Suppressed + 1L;
}

internal sealed class AttachLiveState
{
    public AttachLiveState()
    {
        HostInput = new AttachHostInputFramer(() => _time.GetUtcNow());
    }

    /// <summary>
    /// The latency meter of the attach. <see cref="AttachSession"/> sets its
    /// own instance. A live state that a test builds gets a new meter.
    /// </summary>
    public AttachLatencyMeter Latency { get; init; } = new();

    /// <summary>
    /// The echo predictor of the focused pane. <see cref="AttachSession"/>
    /// sets one from <c>experimental.predictive_echo</c>. A live state that a
    /// test builds predicts nothing.
    /// </summary>
    internal EchoPredictor Prediction { get; init; } = EchoPredictor.Disabled();

    /// <summary>The predictions that the compose in progress stamps. Under the paint lock.</summary>
    internal PredictionView? ComposePrediction { get; set; }

    /// <summary>The pane rows that show predicted cells after the last accepted host paint.</summary>
    internal (string PaneId, int[] Rows)? PaintedPrediction { get; set; }

    internal object PredictionTimerGate { get; } = new();

    /// <summary>The one timer of the timed prediction rules. Created on first use.</summary>
    internal ITimer? PredictionTimer { get; set; }

    internal const int MaxDeferredLiveBytes = 65_536;

    internal const int MaxSessionAttachments = 16;

    internal const int ReservedAttachments = 2;

    internal const int MaxSiblingObserves = MaxSessionAttachments - ReservedAttachments;

    // Lock order: _paintGate, then _engineGate, then _liveGate, then the
    // TTY write lock. A thread that holds _engineGate without _paintGate
    // takes only _liveGate after it, so it never waits for a paint.
    private readonly object _liveGate = new();
    private readonly object _paintGate = new();

    // The key engine lock. It guards KeyEngine state. The key feed takes
    // only this lock. Every holder of WithPaint holds it too.
    private readonly object _engineGate = new();
    private readonly object _catalogReloadGate = new();
    private readonly Dictionary<string, PaneInputModes> _paneInputModes = new(StringComparer.Ordinal);
    private int _sidebarGitPaintPending;
    private int _catalogReloadPending;
    private int _commitChromeRefreshPending;
    private IReadOnlyList<SidebarCubeItem> _pendingCatalogItems = [];
    private SidebarCubeCatalogState _pendingCatalogState = SidebarCubeCatalogState.Ready;
    private string? _pendingCatalogRetiredPlacementId;
    private readonly List<byte> _deferredLive = [];
    private readonly List<byte> _copySeedPending = [];
    private readonly Dictionary<string, AssembledSnapshot> _paneFrames = new(StringComparer.Ordinal);

    // For each pane, a grid of the pane size that the stored frame does not
    // use. A body that rewrites every row unpacks into it, so the grid of
    // the frame before the batch stays as it was. See TakeSpareGrid.
    private readonly Dictionary<string, AssembledCell[]> _spareGrids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LastPaintedPane> _lastPainted = new(StringComparer.Ordinal);
    private string? _lastChromePaint;
    private string? _pendingChrome;
    private readonly ThreadLocal<List<LastPaintedPane>?> _composePainted = new();
    private readonly Dictionary<string, PaneLiveCursor> _liveCursors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClientOverlayCarry> _overlayCarries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _paneAlt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaneMouseObservation> _paneMouse = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _occupantGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Generation, int Occupant)> _appliedBlit = new(StringComparer.Ordinal);
    private readonly HashSet<string> _snapshotRenderPanes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Offset, int MaxOffset, int ViewportRows)> _paneScroll =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaneScrollRevision> _paneScrollRevisions = new(StringComparer.Ordinal);
    private readonly PaneScrollEpochs _scrollEpochs = new();
    private bool _processVtIsSnapshotCapable;
    private bool _copySeedCapturing;
    private int _copyRefreshBusy;
    private int _copyRefreshPending;

    internal Action? BeforePaintLock { get; set; }

    internal Action? AfterPaintEnter { get; set; }

    internal Action? AfterSnapshotBeforeTail { get; set; }

    internal Action? AfterCopySeedBeforeReplay { get; set; }

    internal Func<CancellationToken, Task<AssembledSnapshot?>>? CopyRefreshHandler { get; set; }

    internal IAttachReconnect? ReconnectPort { get; set; }

    internal PendingEndpointActivation? PendingActivation { get; set; }

    internal AttachEndpointCommands EndpointCommands { get; } = new();

    /// Counts <c>HandleEndpointDisconnect</c>. Lane retire must not increment it.
    internal int EndpointDisconnectCount;

    /// Lines <c>ReadRenderAsync</c> has taken from <c>ReadEventAsync</c>.
    internal int RenderLoopReads;

    internal void NoteRenderLoopRead() => Interlocked.Increment(ref RenderLoopReads);

    /// Times <c>ReadRenderAsync</c> has parked until the reader switches. Tests
    /// read this. While parked, the loop does no work until a switch.
    internal int ReaderParks;

    internal void NoteReaderPark() => Interlocked.Increment(ref ReaderParks);

    /// <summary>Times <c>RunTick</c> has started. Tests read this.</summary>
    internal int ActivationPumpTicks;

    internal object ActivationGate { get; } = new();

    internal SemaphoreSlim ActivationPumpGate { get; } = new(1, 1);

    /// Coalesced wake for the activation pump loop. A second release before
    /// the wait consumes the first leaves the count at one.
    internal SemaphoreSlim ActivationPumpWake { get; } = new(0, 1);

    internal void SignalActivationPump()
    {
        try
        {
            ActivationPumpWake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    internal ulong NextSurfaceSerial { get; set; } = 1;

    internal bool InputFrozen { get; set; }

    internal AttachEndpointHealthMonitor? HealthMonitor { get; set; }

    /// <summary>
    /// The Local connection. It is set when Local is the source of a handoff
    /// and when Local hellos again. A cube handoff never replaces it.
    /// Selecting Local restores it.
    /// </summary>
    internal AttachRetargetPresentationBackup? LocalRoot { get; set; }

    /// <summary>
    /// The source of the pending activation. It is the endpoint to restore
    /// when the handoff fails. It is set for one activation and cleared when
    /// that activation completes.
    /// </summary>
    internal AttachRetargetPresentationBackup? RollbackSource { get; set; }

    internal string? RequestedConnectPlacementId { get; set; }

    internal CubesConnectRetargetOutcome? PendingConnectOutcome { get; set; }

    internal SidebarCubeItem? PendingConnectCube { get; set; }

    internal IAttachCommandPort? PendingConnectControl { get; set; }

    internal UnixRawTerminal? PendingConnectTty { get; set; }

    internal CancellationToken PendingConnectCt { get; set; }

    internal JsonElement? PendingTargetSessionSnapshot { get; set; }

    internal ulong? LastSnapshotConnectionGeneration { get; set; }

    internal ulong? PendingTargetSessionSnapshotGeneration { get; set; }

    internal Queue<EndpointActivationRpcCompletion> ActivationCompletions { get; } = new();

    /// <summary>Dest connect-prep generation for stale-completion discard.</summary>
    internal ulong DestConnectGeneration;

    /// <summary>In-flight dest connect prep plus generation (null when idle).</summary>
    internal AttachDestConnectAttempt? DestConnectAttempt;

    /// <summary>Dest connect-prep completions drained by the attach pump.</summary>
    internal Queue<AttachDestConnectCompletion> DestConnectCompletions { get; } = new();

    internal List<string> ActivationAdmitLog { get; } = new();

    internal void NoteActivationAdmit(string endpointId, string eventName)
    {
        if (ActivationAdmitLog.Count >= 40)
            ActivationAdmitLog.RemoveAt(0);
        ActivationAdmitLog.Add($"{endpointId}:{eventName}");
    }

    /// <summary>
    /// Queued endpoint failures. <c>IdentityBound</c> is true when the failure
    /// named a bound client, so its identity is already the client's own.
    /// </summary>
    internal Queue<(string EndpointId, ulong Generation, string Error, bool IdentityBound)> EndpointFailures { get; } = new();

    internal bool TryInstallCoherentSurface(
        AttachSurfaceEvidence surface,
        EndpointActivationLease lease,
        out string error)
    {
        error = string.Empty;
        lock (ActivationGate)
        {
            if (!TryTakeActivationCapturedFrame(lease, surface, out var captured) || captured is null)
            {
                error = "endpoint activation completed without matching captured presentation cells";
                return false;
            }

            CoherentPaneSurface = surface;
            if (surface.FocusedPaneId is { Length: > 0 } focusedPane)
                PaneId = focusedPane;
            SetPaneFrame(captured);
            ClearActivationCapturedFramesCore();
            HostEncoder?.RequestRepaint();
            return true;
        }
    }

    internal void StoreActivationCapturedFrame(ActivationCapturedFrame capture)
    {
        lock (ActivationGate)
            StoreActivationCapturedFrameCore(capture);
    }

    internal void StoreActivationCapturedFrameCore(ActivationCapturedFrame capture) =>
        _activationCapturedFrames[capture.Binding.EndpointId] = capture;

    internal void ClearActivationCapturedFrames()
    {
        lock (ActivationGate)
            ClearActivationCapturedFramesCore();
    }

    internal void ClearActivationCapturedFramesCore()
    {
        _activationCapturedFrames.Clear();
        _unboundActivationRenders.Clear();
    }

    internal void HoldUnboundActivationRender(string endpointId, ulong generation, AssembledSnapshot frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (string.IsNullOrWhiteSpace(endpointId))
            return;
        _unboundActivationRenders[endpointId] = (generation, frame);
    }

    internal bool TryPeekUnboundActivationRender(
        string endpointId,
        ulong generation,
        out AssembledSnapshot? frame)
    {
        frame = null;
        if (!_unboundActivationRenders.TryGetValue(endpointId, out var held)
            || held.Generation != generation)
            return false;
        frame = held.Frame;
        return true;
    }

    internal void ClearUnboundActivationRender(string endpointId) =>
        _unboundActivationRenders.Remove(endpointId);

    /// Drops captured frames and unbound renders owned by this endpoint id.
    internal void RetireEndpointActivationGraphicsCore(string endpointId)
    {
        _activationCapturedFrames.Remove(endpointId);
        _unboundActivationRenders.Remove(endpointId);
    }

    internal bool HasActivationCapturedFrame(string endpointId)
    {
        lock (ActivationGate)
            return _activationCapturedFrames.ContainsKey(endpointId);
    }

    internal bool TryGetActivationCaptureBinding(
        string endpointId,
        out ActivationCaptureBinding? binding)
    {
        binding = null;
        lock (ActivationGate)
        {
            if (!_activationCapturedFrames.TryGetValue(endpointId, out var captured))
                return false;
            binding = captured.Binding;
            return true;
        }
    }

    internal bool HasMatchingActivationCapturedFrame(
        EndpointActivationLease lease,
        AttachSurfaceEvidence surface)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(surface);
        lock (ActivationGate)
        {
            if (!_activationCapturedFrames.TryGetValue(lease.EndpointId, out var captured))
                return false;
            var binding = captured.Binding;
            return binding.Generation == lease.ConnectionGeneration
                && string.Equals(binding.BootId, lease.BootId, StringComparison.Ordinal)
                && string.Equals(binding.BootId, surface.BootId, StringComparison.Ordinal)
                && binding.ProjectionRevision == surface.ProjectionRevision
                && binding.SurfaceRevision == surface.SurfaceRevision;
        }
    }

    private bool TryTakeActivationCapturedFrame(
        EndpointActivationLease lease,
        AttachSurfaceEvidence surface,
        out AssembledSnapshot? frame)
    {
        frame = null;
        if (!_activationCapturedFrames.TryGetValue(lease.EndpointId, out var captured))
            return false;

        var binding = captured.Binding;
        if (binding.Generation != lease.ConnectionGeneration
            || !string.Equals(binding.BootId, lease.BootId, StringComparison.Ordinal)
            || !string.Equals(binding.BootId, surface.BootId, StringComparison.Ordinal)
            || binding.ProjectionRevision != surface.ProjectionRevision
            || binding.SurfaceRevision != surface.SurfaceRevision)
            return false;

        _activationCapturedFrames.Remove(lease.EndpointId);
        frame = captured.Frame;
        return true;
    }

    internal AttachSurfaceEvidence? CoherentPaneSurface { get; private set; }

    internal bool InstalledSurfaceSatisfies(
        EndpointActivationLease lease,
        ulong minimumRevision,
        AttachGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (CoherentPaneSurface is not { } installed)
            return false;
        return string.Equals(installed.BootId, lease.BootId, StringComparison.Ordinal)
            && installed.ProjectionRevision >= minimumRevision
            && installed.Columns == geometry.Columns
            && installed.Rows == geometry.Rows;
    }

    internal SnapshotAssembler ActivationSnapshotAssembler { get; } = new();

    internal bool GetEndpointSurfaceActive(string endpointId) =>
        !_endpointSurfaceActive.TryGetValue(endpointId, out var active) || active;

    internal void SetEndpointSurfaceActive(string endpointId, bool active) =>
        _endpointSurfaceActive[endpointId] = active;

    /// <summary>
    /// The endpoint status is a projection of the endpoint connection state.
    /// Null until the endpoint has a connection entity that is not Connecting.
    /// </summary>
    internal ClientEndpointStatus? GetEndpointStatus(string endpointId)
    {
        if (!Connections.TryGetEntity(endpointId, out var connection))
            return null;
        // A commit that runs now reads Online. Its activation owns the outcome.
        if (connection.IsCommitting)
            return ClientEndpointStatus.Online;
        return ProjectEndpointStatus(connection.State);
    }

    internal static ClientEndpointStatus? ProjectEndpointStatus(EndpointConnectionState state) =>
        state switch
        {
            EndpointConnectionState.Live => ClientEndpointStatus.Online,
            EndpointConnectionState.Lost or EndpointConnectionState.Reconnecting => ClientEndpointStatus.Reconnecting,
            EndpointConnectionState.Failed => ClientEndpointStatus.Disabled,
            _ => null,
        };

    internal bool EndpointProjectionAvailable(string endpointId) =>
        GetEndpointStatus(endpointId) == ClientEndpointStatus.Online
        && EndpointHasSnapshot(endpointId);

    internal bool EndpointHasSnapshot(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return false;
        if (CubeSnapshots.ContainsKey(endpointId))
            return true;
        if (PendingConnectCube is { } cube
            && string.Equals(cube.Id, endpointId, StringComparison.Ordinal)
            && PendingTargetSessionSnapshot is { ValueKind: JsonValueKind.Object })
            return true;
        if (LastSnapshot is { ValueKind: JsonValueKind.Object }
            && (string.Equals(ConnectedPlacementId, endpointId, StringComparison.Ordinal)
                || (RollbackSource is { } backup
                    && string.Equals(backup.ConnectedPlacementId, endpointId, StringComparison.Ordinal))))
            return true;
        return false;
    }

    internal bool TryCopyEndpointSnapshot(string endpointId, out JsonElement snapshot)
    {
        snapshot = default;
        if (string.IsNullOrWhiteSpace(endpointId))
            return false;
        if (PendingConnectCube is { } cube
            && string.Equals(cube.Id, endpointId, StringComparison.Ordinal)
            && PendingTargetSessionSnapshot is { ValueKind: JsonValueKind.Object } dest)
        {
            snapshot = dest;
            return true;
        }

        if (CubeSnapshots.TryGetValue(endpointId, out var stored))
        {
            snapshot = stored;
            return true;
        }

        if (LastSnapshot is { ValueKind: JsonValueKind.Object } last
            && (string.Equals(ConnectedPlacementId, endpointId, StringComparison.Ordinal)
                || (RollbackSource is { } backup
                    && string.Equals(backup.ConnectedPlacementId, endpointId, StringComparison.Ordinal))))
        {
            snapshot = last;
            return true;
        }

        return false;
    }

    internal void SetPaneSurface(AttachSurfaceEvidence surface, EndpointActivationLease lease)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(lease);
        lock (ActivationGate)
        {
            CoherentPaneSurface = surface;
            if (surface.FocusedPaneId is { Length: > 0 } focusedPane)
                PaneId = focusedPane;
            if (TryTakeActivationCapturedFrame(lease, surface, out var captured) && captured is not null)
            {
                SetPaneFrame(captured);
                ClearActivationCapturedFramesCore();
            }

            HostEncoder?.RequestRepaint();
        }
    }

    internal byte[] TakePendingGraphicsCleanup()
    {
        if (_pendingGraphicsCleanup.Count == 0)
            return [];
        var bytes = _pendingGraphicsCleanup.ToArray();
        _pendingGraphicsCleanup.Clear();
        return bytes;
    }

    internal AttachEndpointTransportEnvelope TransportEnvelope { get; } = new();

    internal AttachEndpointTransportEnvelope SourceTransportEnvelope { get; } = new();

    private readonly Dictionary<string, bool> _endpointSurfaceActive = new(StringComparer.Ordinal);

    private readonly List<byte> _pendingGraphicsCleanup = [];

    private readonly Dictionary<string, ActivationCapturedFrame> _activationCapturedFrames =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, (ulong Generation, AssembledSnapshot Frame)> _unboundActivationRenders =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Takes the paint lock and then the key engine lock. The holder owns
    /// the host frame and the key engine state.
    /// </summary>
    internal void EnterPaint()
    {
        EnterFramePaint();
        Monitor.Enter(_engineGate);
    }

    /// <summary>
    /// Takes only the paint lock. The holder owns the host frame and the
    /// pane frames of a paint. It takes the key engine lock
    /// (<see cref="WithEngine(Action)"/>) for each read or change of key
    /// engine state, so the key feed does not wait for its compose, encode
    /// or write.
    /// </summary>
    internal void EnterFramePaint()
    {
        // The key feed holds the engine lock without the paint lock. A
        // paint lock taken there inverts the lock order.
        if (Monitor.IsEntered(_engineGate) && !Monitor.IsEntered(_paintGate))
            throw new InvalidOperationException("the paint lock is taken after the key engine lock.");
        if (Monitor.TryEnter(_paintGate))
        {
            // A free lock is a zero wait. It stays in the histogram, so the
            // percentiles describe every paint entry.
            Latency.RecordElapsed(AttachLatencyHop.PaintWait, 0);
            return;
        }

        // Only a caller that must wait counts. The free path stays one call.
        Interlocked.Increment(ref _paintWaits);
        var waitStart = Latency.Now();
        Monitor.Enter(_paintGate);
        Latency.RecordHop(AttachLatencyHop.PaintWait, waitStart);
    }

    private int _paintWaits;

    /// <summary>Test seam: calls that found the paint lock held and waited for it.</summary>
    internal int PaintWaitsForTests => Volatile.Read(ref _paintWaits);

    internal void ExitPaint()
    {
        Monitor.Exit(_engineGate);
        Monitor.Exit(_paintGate);
    }

    internal void ExitFramePaint() => Monitor.Exit(_paintGate);

    private int _settingsPaintPending;

    /// <summary>The last background integrations load. Tests await it.</summary>
    internal Task? SettingsIntegrationsLoad { get; set; }

    internal void MarkSettingsPaintPending() =>
        Interlocked.Exchange(ref _settingsPaintPending, 1);

    internal bool HasSettingsPaintPending =>
        Volatile.Read(ref _settingsPaintPending) != 0;

    internal bool TakeSettingsPaintPending() =>
        Interlocked.Exchange(ref _settingsPaintPending, 0) != 0;

    internal void MarkSidebarGitPaintPending() =>
        Interlocked.Exchange(ref _sidebarGitPaintPending, 1);

    internal bool HasSidebarGitPaintPending =>
        Volatile.Read(ref _sidebarGitPaintPending) != 0;

    internal bool TakeSidebarGitPaintPending() =>
        Interlocked.Exchange(ref _sidebarGitPaintPending, 0) != 0;

    internal bool HasCatalogReloadPending =>
        Volatile.Read(ref _catalogReloadPending) != 0;

    internal bool CommitChromeRefreshPending
    {
        get => Volatile.Read(ref _commitChromeRefreshPending) != 0;
        set => Interlocked.Exchange(ref _commitChromeRefreshPending, value ? 1 : 0);
    }

    internal bool TryPeekCatalogReload(
        out IReadOnlyList<SidebarCubeItem> items,
        out SidebarCubeCatalogState state,
        out string? retiredPlacementId)
    {
        if (Volatile.Read(ref _catalogReloadPending) == 0)
        {
            items = [];
            state = SidebarCubeCatalogState.Ready;
            retiredPlacementId = null;
            return false;
        }

        lock (_catalogReloadGate)
        {
            items = _pendingCatalogItems;
            state = _pendingCatalogState;
            retiredPlacementId = _pendingCatalogRetiredPlacementId;
        }

        return true;
    }

    internal void QueueCatalogReload(
        IReadOnlyList<SidebarCubeItem> items,
        SidebarCubeCatalogState state,
        string? retiredPlacementId)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_catalogReloadGate)
        {
            _pendingCatalogItems = items;
            _pendingCatalogState = state;
            _pendingCatalogRetiredPlacementId = retiredPlacementId;
        }

        Interlocked.Exchange(ref _catalogReloadPending, 1);
    }

    internal bool TakeCatalogReload(
        out IReadOnlyList<SidebarCubeItem> items,
        out SidebarCubeCatalogState state,
        out string? retiredPlacementId)
    {
        if (Interlocked.Exchange(ref _catalogReloadPending, 0) == 0)
        {
            items = [];
            state = SidebarCubeCatalogState.Ready;
            retiredPlacementId = null;
            return false;
        }

        lock (_catalogReloadGate)
        {
            items = _pendingCatalogItems;
            state = _pendingCatalogState;
            retiredPlacementId = _pendingCatalogRetiredPlacementId;
            _pendingCatalogRetiredPlacementId = null;
        }

        return true;
    }

    internal void WithPaint(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterPaint();
        try
        {
            action();
        }
        finally
        {
            ExitPaint();
        }
    }

    internal T WithPaint<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterPaint();
        try
        {
            return action();
        }
        finally
        {
            ExitPaint();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> under the paint lock only. See
    /// <see cref="EnterFramePaint"/>.
    /// </summary>
    internal T WithFramePaint<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterFramePaint();
        try
        {
            return action();
        }
        finally
        {
            ExitFramePaint();
        }
    }

    internal void WithFramePaint(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterFramePaint();
        try
        {
            action();
        }
        finally
        {
            ExitFramePaint();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> under the key engine lock only. The
    /// key feed uses it, and a frame paint uses it for each read or change
    /// of key engine state. Do not take the paint lock or the TTY write lock
    /// in <paramref name="action"/> without the paint lock held before.
    /// </summary>
    internal void WithEngine(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_engineGate)
            action();
    }

    internal T WithEngine<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_engineGate)
            return action();
    }

    /// <summary>
    /// The key feed: <see cref="WithEngine(Action)"/>, and the wait for the
    /// key engine lock goes into the <see cref="AttachLatencyHop.PaintWait"/>
    /// hop. That hop thus keeps the lock wait of the key path. The feed takes
    /// no paint lock, so <see cref="PaintWaitsForTests"/> does not count it.
    /// </summary>
    internal void WithKeyFeedEngine(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Monitor.TryEnter(_engineGate))
        {
            Latency.RecordElapsed(AttachLatencyHop.PaintWait, 0);
        }
        else
        {
            var waitStart = Latency.Now();
            Monitor.Enter(_engineGate);
            Latency.RecordHop(AttachLatencyHop.PaintWait, waitStart);
        }

        try
        {
            action();
        }
        finally
        {
            Monitor.Exit(_engineGate);
        }
    }

    public required KeyEngine Engine { get; init; }

    public required KeyBindingTable Table { get; set; }

    public required AttachCommandDispatcher Dispatcher { get; init; }

    /// <summary>
    /// Attach snapshot assembler. Occupant swap drops that pane's retained frame.
    /// </summary>
    internal SnapshotAssembler? Snapshots { get; set; }

    public LeaseRenewLoop Renew { get; set; } = null!;

    public string? SessionName { get; set; }

    public string? Hostname { get; set; }

    public string? WindowTitleOverride { get; set; }

    public string? TerminalTitle { get; set; }

    public string? WorkspaceId { get; set; }

    public string? TabId { get; set; }

    /// <summary>
    /// Live attach client identity from <c>events.subscribe</c>
    /// <c>attach_client_id</c>. Distinct from <see cref="ControlSub"/>.
    /// </summary>
    public string? AttachClientId { get; set; }

    /// <summary>
    /// Client id this attach sends in endpoint hellos, surface interest, and
    /// activation leases. Created once per attach and never reassigned.
    /// <see cref="AttachClientId"/> is a mux connection id (<c>conn_N</c>),
    /// unique only within one mux: a cube peer's attach often holds the same
    /// value as the host's local attach. The mux supersedes the older
    /// connection when two hellos share a client id, so reusing it froze the
    /// host's own view while a peer drove its panes.
    /// </summary>
    public string EndpointClientId { get; } = AttachSession.NewAttachClientId();

    /// <summary>
    /// Ingress that admitted the current tab RPC. <c>cli</c> or <c>mouse</c>.
    /// Cleared when that call returns.
    /// </summary>
    public string? UiActionSource { get; set; }

    public IProcessLogSink ProcessLog { get; set; } = NullProcessLogSink.Instance;

    public ClientOverlayState Overlay { get; } = new();

    internal SemaphoreSlim IntegrationListGate { get; } = new(1, 1);

    public TargetPaneLeasePair? OverlayLeases { get; set; }

    public string PaneId { get; set; } = "";

    public string InputLease { get; set; } = "";

    public string ResizeLease { get; set; } = "";

    public MuxReleaseCapability Release { get; init; } = MuxReleaseCapability.Product;

    public ISidebarCubeCatalogSource? CubeCatalog { get; set; }

    public IHostReachCatalog? HostReach { get; set; }

    public PlacementCatalogReloader? CatalogReloader { get; set; }

    public SshPlacementEndpointRegistry? SshEndpoints { get; set; }

    public ICubesConnectRetargeter? CubesConnect { get; set; }

    public IMoveWorkMenu? MoveWork { get; set; }

    internal PendingMoveWorkConfirm? PendingMoveWork { get; set; }

    internal PendingTransferPick? PendingTransfer { get; set; }

    public IMoveWorkDestExecutorFactory? DestExecutorFactory { get; set; }

    public string? ContinuityStoreDir { get; set; }

    public string? PlacementStoreDir { get; set; }

    public AttachClientConfig? AttachConfig { get; set; }

    /// <summary>
    // / Hidden-cursor reveal for CJK IME.
    /// </summary>
    internal CjkImeRevealFilter CjkIme { get; set; } = CjkImeRevealFilter.Disabled;

    /// <summary>
    // / Prefix ASCII input-source switch.
    /// <c>src/client/shell/input_source.rs:25-36</c>.
    /// </summary>
    internal PrefixAsciiInputCoordinator PrefixAscii { get; } = new();

    internal IPrefixAsciiInputSource PrefixAsciiSource { get; set; } =
        NoOpPrefixAsciiInputSource.Instance;

    public string? CubesConnectAction { get; set; }

    internal bool CubesConnectOutcomeRecorded { get; set; }

    public string? ConnectedPlacementId { get; set; }

    /// <summary>
    /// <c>active_endpoint_id</c>. Workspaces compose from this cube.
    /// </summary>
    public string? SelectedCubeId { get; set; }

    /// <summary>
    /// endpoint keeps <c>endpoint.snapshot</c>.
    /// </summary>
    public Dictionary<string, JsonElement> CubeSnapshots { get; } =
        new(StringComparer.Ordinal);

    internal bool RestoredCubeConnectAttempted { get; set; }

    public string? ConnectedBootId { get; set; }

    /// <summary>
    /// Boot from the live source hello. Reused so Connect does not
    /// hello the source again.
    /// </summary>
    public string? SourceEndpointBootId { get; set; }

    internal AttachClientViewHintStore ClientViewHints { get; } = new();

    public bool ConnectedSshPlacement { get; set; }

    public string? PlacementDisplayName { get; set; }

    public SidebarCubeKind? PlacementKind { get; set; }

    public bool RemoteDestination { get; set; }

    public bool RemoteAttach { get; set; }

    public string? PaintedMuxLabel { get; set; }

    public string? SourceMuxSocketPath { get; set; }

    public string? ActiveMuxSocketPath { get; set; }

    public bool PlacementOwnerUnavailable { get; set; }

    public bool PresentationFrozen { get; set; }

    /// <summary>
    /// the host-write freeze for one chrome frame.
    /// </summary>
    public bool FrozenChromePaintArmed { get; set; }

    /// <summary>
    // / Re-observe visible panes after source restore.
    /// <c>surface_interest.rs:33</c> requests a viewer repaint on surface-on.
    /// </summary>
    public bool PendingVisibleObserve { get; set; }

    internal bool TakePendingVisibleObserve()
    {
        if (!PendingVisibleObserve)
            return false;
        PendingVisibleObserve = false;
        return true;
    }

    /// <summary>
    /// layout.export + tab.list on the newly active endpoint.
    /// </summary>
    public bool PendingChromeRefresh { get; set; }

    internal bool TakePendingChromeRefresh()
    {
        if (!PendingChromeRefresh)
            return false;
        PendingChromeRefresh = false;
        return true;
    }

    /// <summary>
    /// </summary>
    public string? ActiveProjectionEndpointId { get; set; }

    /// <summary>
    /// The mux instance and the connection that the active projection shows.
    /// Null when the commit did not name them.
    /// </summary>
    internal EndpointMuxInstance? ActiveProjectionInstance { get; set; }

    public IMuxAliveProbe? SourceAlive { get; set; }

    internal AttachControlSlot? ControlSlot { get; set; }

    private ControlPlaneClient? _unavailableEndpointClient;

    /// <summary>
    /// A closed client. It stands in for the active cube when that cube has no
    /// live client, so that no request goes to the Local mux with cube identity.
    /// </summary>
    internal ControlPlaneClient UnavailableEndpointClient
    {
        get
        {
            if (_unavailableEndpointClient is { } existing)
                return existing;
            var closed = new ControlPlaneClient("/tmp/hypa-endpoint-unavailable.sock");
            closed.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return Interlocked.CompareExchange(ref _unavailableEndpointClient, closed, null) ?? closed;
        }
    }

    /// <summary>Connection identity of each endpoint client in this attach.</summary>
    internal AttachEndpointConnections Connections { get; } = new();

    /// <summary>
    /// The event subscription of the active endpoint. A new subscription
    /// can come from another mux process, so the scroll epoch of the last
    /// reply is no longer pinned (<see cref="NoteScrollEventSubscriptionStarted"/>).
    /// A restarted mux can give the same id again, so a reconnect also
    /// calls that method itself.
    /// </summary>
    public string ControlSub
    {
        get => _controlSub;
        set
        {
            if (!string.Equals(_controlSub, value, StringComparison.Ordinal))
                NoteScrollEventSubscriptionStarted();
            _controlSub = value;
        }
    }

    private string _controlSub = "";

    public string? RenderSub { get; set; }

    public IAttachCommandPort? RenderPort { get; set; }

    public Action? WakeRender { get; set; }

    private CancellationTokenSource _readerSwitch = new();

    internal CancellationToken ReaderSwitchToken => Volatile.Read(ref _readerSwitch).Token;

    // Hypa cancels
    /// the parked read. Callbacks run off the caller so <c>ActivationGate</c>
    /// stays free.
    internal void SignalReaderSwitch()
    {
        var next = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _readerSwitch, next);
        _ = old.CancelAsync();
    }

    public AttachInputSender? InputSender { get; set; }

    internal bool BlitReanchorPending { get; set; }

    /// <summary>
    /// One reanchor request for each pane whose cells generation chain
    /// broke. <see cref="AttachSession.ObservePendingAsync"/> sends them.
    /// </summary>
    internal PaneChainReanchorRequests ChainReanchors { get; } = new();

    internal HostFrame Host { get; } = new();

    internal HostBlitEncoder HostEncoder { get; } = new();

    /// <summary>
    /// What the host shows after the last accepted host paint. Null while
    /// <see cref="Host"/> can differ from the kept frame of
    /// <see cref="HostEncoder"/>: each compose clears it before it stamps,
    /// and only an accepted write sets it. Read and write it under the paint
    /// gate.
    /// </summary>
    internal HostPatchBaseline? PatchBaseline { get; set; }

    /// <summary>
    /// Panes whose scrollbar the host does not show yet. A scroll event that
    /// moves a thumb keeps the patch baseline and adds the pane here. The
    /// next patch paint stamps the gutter of each pane, and an accepted full
    /// compose paints all of them. Read and write it under the paint gate.
    /// </summary>
    internal HashSet<string> PendingGutterPanes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// False makes every cells paint a full compose. A test sets it to build
    /// the full-redraw reference of the patch path.
    /// </summary>
    internal bool PatchPaintEnabled { get; set; } = true;

    internal StartupSplashSurface? Splash { get; set; }

    /// <summary>Host stdin framer. It reads the clock of this state.</summary>
    internal AttachHostInputFramer HostInput { get; }

    /// <summary>Setting and detection for the status report to the host terminal.</summary>
    internal HostStatusReporter HostStatus { get; } = new();

    /// <summary>Which panes the attach tells the mux that the person has seen.</summary>
    internal SeenReportSelector SeenReports { get; } = new();

    /// <summary>Summary of the session that the attach shows. The mux computes it.</summary>
    internal SessionProgramStatusView? SessionStatus { get; set; }

    internal bool HostOverlayShown { get; set; }

    internal bool InputDetachRequested { get; set; }

    internal bool InputStallRequested { get; set; }

    internal int InputInvalidStateCount { get; set; }

    internal DateTimeOffset InputInvalidStateAt { get; set; }

    public string? LastPaneId { get; set; }

    public string? PendingObservePaneId;

    /// <summary>
    /// 1 while focus defers <c>terminal.observe</c> until current chrome
    // / exists.
    /// </summary>
    internal int HoldPendingObserve;

    internal int ObserveVisibleDepth;

    public string? PendingClosePaneId { get; set; }

    public string? PendingCloseTabId { get; set; }

    public string? PendingCloseWorkspaceId { get; set; }

    public bool PendingCloseArmed { get; set; }

    public string? CompletedEditScrollbackPaneId { get; set; }

    public string? EditScrollbackRestorePaneId { get; set; }

    public string? PendingRenamePaneId { get; set; }

    public string? PendingRenameTabId { get; set; }

    public string? PendingRenameWorkspaceId { get; set; }

    public MenuFocusRestore? PendingMenuFocus { get; set; }

    internal object ChromeStateGate { get; } = new();

    public HashSet<string> ObservedPaneIds { get; } = new(StringComparer.Ordinal);

    public DateTimeOffset? LastVisibleSetPublishUtc { get; set; }

    public IReadOnlyList<string>? LastPublishedVisiblePaneIds { get; set; }

    public Dictionary<string, string> SiblingResizeLeases { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, (int Cols, int Rows)> LastSentPaneSizes { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, DeclinedPaneResize> DeclinedPaneResizes { get; } = new(StringComparer.Ordinal);

    /// <summary>When this client last sent <c>pane.resize</c>, per pane.</summary>
    public Dictionary<string, DateTimeOffset> OwnPaneResizeSentAt { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> PaneGeometryOwners { get; } = new(StringComparer.Ordinal);

    internal object FrameLogGate { get; } = new();

    public Dictionary<string, FrameDropNotice> FrameDropNotices { get; } = new(StringComparer.Ordinal);

    /// <summary>Last admit reason logged per pane. A repeat is not logged again.</summary>
    public Dictionary<string, string> FrameAdmitNotices { get; } = new(StringComparer.Ordinal);

    public TabBarCommandCache TabBarCommands { get; set; } = new();

    public IReadOnlyList<TabBarTabSpec> TabHits { get; set; } = [];

    public AssembledSnapshot? LastComplete { get; private set; }

    /// <summary>
    /// The attach status. Each owner writes its own entry. Use
    /// <see cref="AttachStatusBoard.Set"/> and <see cref="AttachStatusBoard.Clear"/>.
    /// </summary>
    internal AttachStatusBoard Status { get; } = new();

    /// <summary>
    /// The error line that the chrome paints. A read-only view of the newest
    /// error entry of any owner.
    /// </summary>
    public string? StatusError => Status.TopError?.Text;

    /// <summary>
    /// A neutral one-line status. The mode bar shows it without the error chip.
    /// <see cref="StatusError"/> wins when both are set. A read-only view of
    /// the newest notice entry of any owner.
    /// </summary>
    public string? StatusNotice => Status.TopNotice?.Text;

    public AttachUiConfig Ui { get; set; } = AttachUiConfig.Default;

    public ThemeRuntime Theme { get; set; } = ThemeRuntime.Default;

    public HostTerminalTheme HostTheme { get; set; } = HostTerminalTheme.Empty;

    public LayoutChromeGeometry? Chrome { get; set; }

    internal ChromeComputeSeed? ChromeSeed { get; set; }

    public bool ChromeEnabled { get; set; }

    internal bool PendingPaneChromeResize { get; set; }

    /// <summary>1 while the render loop has a pane resize flush off the loop.</summary>
    internal int PaneChromeResizeOffLoop;

    /// <summary>The last pane resize flush that the render loop started off the loop.</summary>
    internal Task PaneChromeResizeOffLoopTask { get; set; } = Task.CompletedTask;

    /// <summary>
    /// The last focus redraw that the input loop started off the loop. It
    /// never faults. Tests wait for it.
    /// </summary>
    internal Task FocusRedraw { get; set; } = Task.CompletedTask;

    /// <summary>Runs one pane resize flush at a time across the render and control lanes.</summary>
    internal SemaphoreSlim PaneChromeResizeGate { get; } = new(1, 1);

    /// <summary>Earliest time to send a pane resize again after a lease refusal.</summary>
    internal DateTimeOffset PaneChromeResizeRetryAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Lease refusals in a row. It sets the retry backoff.</summary>
    internal int PaneChromeResizeRefusals { get; set; }

    public int TabOverflowOffset { get; set; }

    public bool RevealFocusedTab { get; set; } = true;

    public int? LastTabBarWidth { get; set; }

    public bool SidebarOpen { get; set; }

    public bool SidebarCollapsed { get; set; }

    public int SidebarWidth { get; set; }

    public int SidebarRequestedWidth { get; set; }

    public SidebarWidthSource SidebarWidthSource { get; set; }

    public float SidebarSectionSplit { get; set; } = SidebarTwoPaneLayoutPolicy.DefaultSplitRatio;

    public SidebarSectionSplitSource SidebarSectionSplitSource { get; set; }

    public DateTimeOffset? LastSidebarLiveRefresh { get; set; }

    public DateTimeOffset? SidebarRefreshAfter { get; set; }

    public int SidebarAgentRevision { get; set; }

    public IReadOnlyList<SidebarStubRow> SidebarRows { get; set; } = [];

    public IReadOnlyList<SidebarCubeItem> Cubes { get; set; } = [];

    public SidebarCubeCatalogState CubesState { get; set; } = SidebarCubeCatalogState.Ready;

    public SidebarFrame? SidebarFrame { get; set; }

    public int SidebarSpacesScroll { get; set; }

    public int SidebarAgentsScroll { get; set; }

    public Dictionary<string, int> SidebarResourceScrolls { get; } =
        new(StringComparer.Ordinal);

    public HashSet<string>? LinkedPluginIds { get; set; }

    public SidebarComposeInput? SidebarInput { get; set; }

    public IReadOnlyList<Hypa.AgentRuntime.Application.Plugins.InstalledPlugin> LinkedPlugins { get; set; } =
        [];

    public ISidebarGitStatus? GitStatus { get; set; }

    public string? UpdateAvailable { get; set; }

    /// <summary>
    /// <c>integration_updates_available</c>.
    /// </summary>
    public bool IntegrationUpdatesAvailable { get; set; }

    /// <summary>Evaluates the sidebar update row on each heartbeat. Null disables it.</summary>
    public IMuxUpdateNoticeSource? UpdateNotices { get; set; }

    /// <summary>Sidebar update row. Null hides it.</summary>
    public SidebarUpdateNotice? UpdateNotice { get; set; }

    /// <summary>The y/n prompt on screen confirms a mux restart, not Move Work.</summary>
    public bool PendingMuxRestart { get; set; }

    /// <summary>The user confirmed a restart. Attach detaches so its caller can restart.</summary>
    public bool MuxRestartRequested { get; set; }

    public bool GlobalMenuAttentionBadgeVisible =>
        SidebarTwoPaneLayoutPolicy.GlobalMenuAttentionBadgeVisible(
            UpdateAvailable is not null,
            IntegrationUpdatesAvailable);

    public IAttachConfigLoader? ConfigLoader { get; set; }

    public PackNotesService PackNotes { get; set; } = new();

    public IClientViewPreferencesStore? ClientViewPreferences { get; set; }

    /// <summary>
    /// Local applied RPC already loaded disk. Skip the next <c>config.reloaded</c> load.
    /// Control still resizes on the event.
    /// </summary>
    internal bool SuppressNextConfigReloadedLoad { get; set; }

    public IAttachConfigEnvironment? AttachEnvironment { get; set; }

    public IScrollbackHistoryFiles? ScrollbackFiles { get; set; }

    public EditScrollbackSession? EditScrollback { get; set; }

    public JsonElement? LastSnapshot { get; set; }

    public IReadOnlyList<PluginResourceDto> PluginResources { get; set; } = [];

    public HashSet<string>? CollapsedSectionIds { get; set; }

    public HashSet<string>? CollapsedWorktreeGroups { get; set; }

    public WorktreeDialogModel Worktrees { get; } = new();

    public CubePairingDialogModel CubesPairing { get; } = new();

    /// <summary>
    /// The session of the local attach. It is set once, at attach start. A cube
    /// connect changes <see cref="SessionName"/>, not this value.
    /// </summary>
    public string? HomeSessionName { get; init; }

    /// <summary>Test seam. Production talks to the home mux socket.</summary>
    internal IMuxCubeSharePort? MuxShare { get; set; }

    /// <summary>Test seam. Production mints invites in the pairing store.</summary>
    internal IShareInviteIssuer? ShareInvites { get; set; }

    internal MuxShareInvite? LastMuxShare { get; set; }

    public HashSet<string>? CollapsedTreeIds { get; set; }

    public string? NavigatedPaneId { get; set; }

    /// <summary>Local overlay owner pane. Graph placement stays hidden.</summary>
    public string? OverlayPaneId { get; set; }

    public int SwitcherScroll { get; set; }

    public int SwitcherSelected { get; set; }

    public IReadOnlyList<ToastHit> Toasts { get; set; } = [];

    public bool PopupOpen { get; set; }

    /// <summary>
    /// True after a local close. Control-socket <c>opened</c> with seq at or
    /// below <see cref="PopupClosedSeq"/> must not reopen the overlay.
    /// </summary>
    public bool PopupSawClosed { get; set; }

    /// <summary>
    /// Last applied <c>popup.lifecycle</c> opened seq. Local close fences at
    /// this seq when no closed event has arrived. A lagged closed with seq
    /// below this value must not dismiss the live overlay.
    /// </summary>
    public long PopupOpenedSeq { get; set; }

    /// <summary>
    /// Closed high-water. Survives reopen so a delayed opened at or below this
    /// seq cannot rewind a newer live overlay. Ignoring a lagged closed must
    /// not raise this to the live opened seq.
    /// </summary>
    public long PopupClosedSeq { get; set; }

    public int PopupInnerCols { get; set; }

    public int PopupInnerRows { get; set; }

    public int PopupOuterCols { get; set; }

    public int PopupOuterRows { get; set; }

    public bool PopupLockInnerGeometry { get; set; }

    public int PopupPaintContentCols { get; set; }

    public int PopupPaintContentRows { get; set; }

    public PopupSize? PopupRequestedWidth { get; set; }

    public PopupSize? PopupRequestedHeight { get; set; }

    public AssembledSnapshot? PopupSnapshot { get; set; }

    public bool PopupFillInner { get; set; }

    public Notification.NotificationDirector Notifications { get; set; } =
        new Notification.NotificationDirector(AttachUiConfig.Default);

    internal void NoteOccupantGeneration(string paneId, int generation)
    {
        if (string.IsNullOrWhiteSpace(paneId) || generation <= 0)
            return;
        var bumped = false;
        lock (_occupantGenerations)
        {
            if (_occupantGenerations.TryGetValue(paneId, out var current))
            {
                if (generation <= current)
                    return;
                bumped = true;
            }

            _occupantGenerations[paneId] = generation;
        }

        // Occupant replace drops the retained grid and encoder baseline.
        // Snapshot-capable panes keep one writer: do not reopen Remap.
        if (bumped)
        {
            WithPaint(() =>
            {
                Snapshots?.DropRetained(paneId);
                ClearLastPainted(paneId);
                ResetAppliedBlit(paneId);
                DropPaneFrame(paneId);
                HostEncoder.Invalidate();
            });
        }
    }

    internal (long Generation, int Occupant) LastAppliedBlit(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return (0, 0);
        lock (_appliedBlit)
            return _appliedBlit.TryGetValue(paneId, out var applied) ? applied : (0, 0);
    }

    internal void StoreAppliedBlit(string paneId, long generation, int occupant)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_appliedBlit)
            _appliedBlit[paneId] = (generation, occupant);
    }

    internal void ResetAppliedBlit(string? paneId = null)
    {
        lock (_appliedBlit)
        {
            if (string.IsNullOrWhiteSpace(paneId))
                _appliedBlit.Clear();
            else
                _appliedBlit.Remove(paneId);
        }
    }

    internal bool TryAcceptOccupantGeneration(string paneId, int generation)
    {
        if (string.IsNullOrWhiteSpace(paneId) || generation <= 0)
            return false;
        var bumped = false;
        lock (_occupantGenerations)
        {
            if (_occupantGenerations.TryGetValue(paneId, out var current))
            {
                if (generation < current)
                    return false;
                bumped = generation > current;
            }

            _occupantGenerations[paneId] = generation;
        }

        if (bumped)
        {
            WithPaint(() =>
            {
                Snapshots?.DropRetained(paneId);
                ClearLastPainted(paneId);
                ResetAppliedBlit(paneId);
                DropPaneFrame(paneId);
                HostEncoder.Invalidate();
            });
        }

        return true;
    }

    internal int OccupantGenerationOf(string? paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return 0;
        lock (_occupantGenerations)
            return _occupantGenerations.TryGetValue(paneId, out var generation) ? generation : 0;
    }

    public MouseEngine Mouse { get; set; } = new();

    public PaneHistoryView History { get; } = new();

    public Dictionary<string, string> PaneRightClick { get; } = new(StringComparer.Ordinal);

    public byte[]? LastOsc52 { get; set; }

    public bool DetachRequested { get; set; }

    public bool Reconnecting { get; set; }

    public ContextMenuModel? MouseMenu { get; set; }

    public bool InitialSnapshotPainted { get; set; }

    /// <summary>
    /// Mark <paramref name="paneId"/> as snapshot-capable on the attach paint path.
    /// Call after <c>kind=cells</c> or a complete <c>kind=snapshot</c> paints.
    /// Occupant bump keeps this marker. Popup close may still clear the popup target.
    /// </summary>
    public void NoteSnapshotRender(string? paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_liveGate)
            _snapshotRenderPanes.Add(paneId);
    }

    /// <summary>
    /// Drop the snapshot-only marker. Popup close may reopen Remap for a
    /// non-snapshot-capable popup until the next complete grid.
    /// </summary>
    public void ClearSnapshotRender(string? paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_liveGate)
            _snapshotRenderPanes.Remove(paneId);
    }

    /// <summary>
    /// True when this pane already painted from a cells or snapshot grid.
    /// </summary>
    public bool UsesSnapshotRender(string? paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        lock (_liveGate)
            return _snapshotRenderPanes.Contains(paneId);
    }

    /// <summary>
    /// Process VT from <c>runtime.health</c>. Ghostty production panes
    /// implement <c>IPaneVtSnapshot</c>. Fail closed to unknown when the
    /// provider is not Ghostty and health omits the snapshot token.
    /// </summary>
    internal void NoteProcessVtProvider(string? provider)
    {
        _processVtIsSnapshotCapable = IsSnapshotCapableVtProvider(provider);
    }

    /// <summary>
    /// Read process VT from a <c>runtime.health</c> result. Ghostty and an
    /// explicit <c>snapshot</c> capability are IPaneVtSnapshot panes before
    // / the first grid.
    /// </summary>
    internal void ApplyProcessVtHealth(JsonElement health)
    {
        if (health.ValueKind != JsonValueKind.Object)
            return;
        if (!health.TryGetProperty("vt", out var vt) || vt.ValueKind != JsonValueKind.Object)
            return;
        string? provider = null;
        if (vt.TryGetProperty("provider", out var providerEl) && providerEl.ValueKind == JsonValueKind.String)
            provider = providerEl.GetString();
        var snapshotCap = HasVtSnapshotCapability(vt);
        if (provider is null && !snapshotCap)
            return;
        _processVtIsSnapshotCapable = snapshotCap || IsSnapshotCapableVtProvider(provider);
    }

    /// <summary>
    /// One writer for Ghostty <c>IPaneVtSnapshot</c> / cells panes.
    /// Process health is the capability. A landed-snapshot marker is only
    /// the fallback when health is unknown.
    /// </summary>
    public bool IsSnapshotCapable(string? paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        if (_processVtIsSnapshotCapable)
            return true;
        return UsesSnapshotRender(paneId);
    }

    private static bool IsSnapshotCapableVtProvider(string? provider) =>
        string.Equals(provider, "ghostty", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, VtFloorDefaults.Provider, StringComparison.OrdinalIgnoreCase);

    private static bool HasVtSnapshotCapability(JsonElement vt)
    {
        if (!vt.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var cap in caps.EnumerateArray())
        {
            if (cap.ValueKind == JsonValueKind.String
                && string.Equals(cap.GetString(), VtFloorDefaults.SnapshotCapability, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// offset from the live bottom, max offset, viewport rows. Scalar only.
    /// </summary>
    internal void SetPaneScrollMetrics(string paneId, int offset, int maxOffset, int? viewportRows = null)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_liveGate)
            StorePaneScroll(paneId, offset, maxOffset, viewportRows);
    }

    /// <summary>
    /// A new event subscription started. It can come from a restarted mux,
    /// so the epoch of the last reply no longer names the mux of the next
    /// events. See <see cref="PaneScrollEpochs"/>.
    /// </summary>
    internal void NoteScrollEventSubscriptionStarted()
    {
        lock (_liveGate)
            _scrollEpochs.Unpin();
    }

    /// <summary>
    /// Forgets the scroll revisions and the mux epochs. The attach shows
    /// another mux connection, so the epochs of the earlier mux do not order
    /// the states of the next one.
    /// </summary>
    internal void ResetPaneScrollRevisions()
    {
        lock (_liveGate)
        {
            _paneScrollRevisions.Clear();
            _scrollEpochs.Clear();
        }
    }

    /// <summary>
    /// Store the scroll metrics of a pane. A <paramref name="revision"/> from
    /// the mux orders the <c>pane.scroll</c> replies and the
    /// <c>pane.scroll_changed</c> events: a value older than the last applied
    /// value of the same mux epoch is dropped, and the method returns false.
    /// An event of an epoch that is not the current one is dropped when
    /// <see cref="PaneScrollEpochs"/> says that it is an earlier mux. An
    /// event with no revision (a mux or a journal record of the old format)
    /// does not replace a state with a revision. The check and the store
    /// are one step under the live gate.
    /// </summary>
    internal bool TrySetPaneScrollMetrics(
        string paneId,
        int offset,
        int maxOffset,
        int? viewportRows,
        PaneScrollRevision? revision,
        bool fromReply = false)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        lock (_liveGate)
        {
            if (revision is { } next)
            {
                if (!_scrollEpochs.Admit(next.Epoch, fromReply))
                    return false;
                if (_paneScrollRevisions.TryGetValue(paneId, out var applied) && next.IsOlderThan(applied))
                    return false;
                _paneScrollRevisions[paneId] = next;
            }
            else if (fromReply)
            {
                // The mux that serves the attach sends no revision. The
                // states of an earlier mux with revisions are no baseline.
                _scrollEpochs.RetireCurrent();
                _paneScrollRevisions.Clear();
            }
            else if (_paneScrollRevisions.ContainsKey(paneId))
            {
                return false;
            }

            StorePaneScroll(paneId, offset, maxOffset, viewportRows);
            return true;
        }
    }

    private void StorePaneScroll(string paneId, int offset, int maxOffset, int? viewportRows)
    {
        maxOffset = Math.Max(0, maxOffset);
        offset = Math.Clamp(offset, 0, maxOffset);
        var rows = viewportRows.GetValueOrDefault();
        if (rows <= 0 && _paneScroll.TryGetValue(paneId, out var existing))
            rows = existing.ViewportRows;
        _paneScroll[paneId] = (offset, maxOffset, Math.Max(0, rows));
    }

    /// <summary>
    /// True when the mux that serves the attach now sent the scroll state
    /// of the pane with a revision. A mux that sends revisions also takes
    /// the <c>delta</c> of <c>pane.scroll</c>, so a read with a step of 0
    /// rows does not move the view. A revision that an earlier mux sent is
    /// not enough: after a new event subscription, the epoch of that
    /// revision must come again in a reply or in an event of the new stream
    /// (<see cref="PaneScrollEpochs.IsConfirmed"/>).
    /// </summary>
    internal bool ServingMuxSentPaneScrollRevision(string paneId)
    {
        lock (_liveGate)
        {
            return _paneScrollRevisions.TryGetValue(paneId, out var revision)
                && _scrollEpochs.IsConfirmed(revision.Epoch);
        }
    }

    /// <summary>
    /// Raise the stored scroll offset of a pane to <paramref name="origin"/>.
    /// A cells frame carries the origin of its capture. A frame can arrive
    /// after a newer <c>pane.scroll</c> reply, so it never lowers the offset.
    /// </summary>
    internal bool RaisePaneScrollOrigin(string paneId, int origin, int viewportRows)
    {
        if (string.IsNullOrWhiteSpace(paneId) || origin <= 0)
            return false;
        lock (_liveGate)
        {
            _paneScroll.TryGetValue(paneId, out var existing);
            if (existing.Offset >= origin)
                return false;
            var rows = viewportRows > 0 ? viewportRows : existing.ViewportRows;
            _paneScroll[paneId] = (origin, Math.Max(existing.MaxOffset, origin), Math.Max(0, rows));
            return true;
        }
    }

    internal bool TryGetPaneScrollMetrics(
        string? paneId,
        out int offset,
        out int maxOffset,
        out int viewportRows)
    {
        offset = 0;
        maxOffset = 0;
        viewportRows = 0;
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        lock (_liveGate)
        {
            if (!_paneScroll.TryGetValue(paneId, out var metrics))
                return false;
            offset = metrics.Offset;
            maxOffset = metrics.MaxOffset;
            viewportRows = metrics.ViewportRows;
            return true;
        }
    }

    /// <summary>
    /// server offset from the live bottom is greater than zero.
    /// </summary>
    internal bool PaneIsScrolledBack(string? paneId) =>
        TryGetPaneScrollMetrics(paneId, out var offset, out _, out _) && offset > 0;

    private TimeProvider _time = TimeProvider.System;

    public TimeProvider Time
    {
        get => _time;
        set
        {
            _time = value;
            Connections.Time = value;
        }
    }

    public bool HasLivePane => !string.IsNullOrWhiteSpace(PaneId);

    public bool HasDeferredLive
    {
        get
        {
            lock (_liveGate)
                return _deferredLive.Count > 0;
        }
    }

    public int DeferredLiveByteCount
    {
        get
        {
            lock (_liveGate)
                return _deferredLive.Count;
        }
    }

    public void DeferLive(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        lock (_liveGate)
        {
            AppendCapped(_deferredLive, bytes);
            if (_copySeedCapturing || HoldsCopySeedUnlocked())
                AppendCapped(_copySeedPending, bytes);
        }
    }

    public void DeferCopySeed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        lock (_liveGate)
        {
            if (_copySeedCapturing || HoldsCopySeedUnlocked())
                AppendCapped(_copySeedPending, bytes);
        }
    }

    public void BeginCopySeedCapture()
    {
        lock (_liveGate)
            _copySeedCapturing = true;
    }

    private bool HoldsCopySeedUnlocked() =>
        Engine.PaintMode is AttachClientMode.Copy && !Engine.Copy.IsSeeded;

    public byte[] TakeCopySeedPending()
    {
        lock (_liveGate)
        {
            _copySeedCapturing = false;
            var taken = _copySeedPending.Count == 0 ? [] : _copySeedPending.ToArray();
            _copySeedPending.Clear();
            return taken;
        }
    }

    public bool HasCopyRefreshPending => Volatile.Read(ref _copyRefreshPending) != 0;

    public void MarkCopyRefreshPending() => Volatile.Write(ref _copyRefreshPending, 1);

    public void ClearCopyRefreshPending() => Volatile.Write(ref _copyRefreshPending, 0);

    public bool ConsumeCopyRefreshPending() => Interlocked.Exchange(ref _copyRefreshPending, 0) != 0;

    public bool TryBeginCopyRefresh() => Interlocked.CompareExchange(ref _copyRefreshBusy, 1, 0) == 0;

    public void EndCopyRefresh() => Volatile.Write(ref _copyRefreshBusy, 0);

    public byte[] CopyDeferredLive()
    {
        lock (_liveGate)
            return CopyTailUnlocked();
    }

    public byte[] TakeDeferredLive()
    {
        lock (_liveGate)
        {
            var taken = CopyTailUnlocked();
            _deferredLive.Clear();
            return taken;
        }
    }

    public void ClearDeferredLive()
    {
        lock (_liveGate)
            _deferredLive.Clear();
    }

    internal AssembledSnapshot? GetLastPaintedFrame(string paneId)
    {
        lock (_liveGate)
            return _lastPainted.TryGetValue(paneId, out var last) ? last.Frame : null;
    }

    internal AssembledSnapshot? GetRestorePaintFrame(string paneId)
    {
        lock (_liveGate)
        {
            _paneFrames.TryGetValue(paneId, out var stored);
            _lastPainted.TryGetValue(paneId, out var last);
            if (stored is null)
                return last?.Frame;
            if (last is null)
                return stored;
            return last.Frame.Generation >= stored.Generation ? last.Frame : stored;
        }
    }

    internal bool HasNewerPainted(AssembledSnapshot frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_liveGate)
        {
            return _lastPainted.TryGetValue(frame.PaneId, out var last)
                && last.Frame.Generation > frame.Generation;
        }
    }

    internal bool ShouldSkipStoredBlit(
        string paneId,
        AssembledSnapshot stored,
        int originCol,
        int originRow,
        int clipCols,
        int clipRows)
    {
        ArgumentNullException.ThrowIfNull(stored);
        lock (_liveGate)
        {
            if (!_lastPainted.TryGetValue(paneId, out var last))
                return false;
            _ = originCol;
            _ = originRow;
            _ = clipCols;
            _ = clipRows;
            return stored.Generation < last.Frame.Generation;
        }
    }

    internal bool ChromeUnchanged(string painted)
    {
        lock (_liveGate)
            return _lastChromePaint is not null
                && string.Equals(_lastChromePaint, painted, StringComparison.Ordinal);
    }

    /// <summary>
    /// Drop the retained chrome frame so the next chrome paint cannot be
    /// suppressed by equality. Required whenever something else has written
    /// over the chrome region (full-screen overlays, onboarding, settings,
    /// mode bars, prompts), on resize, on full redraw, and on re-attach.
    /// </summary>
    internal void InvalidateChrome()
    {
        WithPaint(() =>
        {
            lock (_liveGate)
            {
                _pendingChrome = null;
                _lastChromePaint = null;
            }

            // Keep last_frame so the next encode can rewrite every cell without
            HostEncoder.RequestRepaint();
            Host.Invalidate();
        });
    }

    internal void QueueChrome(string painted)
    {
        lock (_liveGate)
            _pendingChrome = painted;
    }

    internal void CommitChrome()
    {
        lock (_liveGate)
        {
            if (_pendingChrome is null)
                return;
            _lastChromePaint = _pendingChrome;
            _pendingChrome = null;
        }
    }

    internal void AbortChrome()
    {
        lock (_liveGate)
            _pendingChrome = null;
    }

    internal bool NeedsFullRedraw(
        string paneId,
        AssembledSnapshot stored,
        int originCol,
        int originRow,
        int clipCols,
        int clipRows)
    {
        ArgumentNullException.ThrowIfNull(stored);
        lock (_liveGate)
        {
            if (!_lastPainted.TryGetValue(paneId, out var last))
                return true;
            return last.OriginCol != originCol
                || last.OriginRow != originRow
                || last.ClipCols != clipCols
                || last.ClipRows != clipRows
                || last.Frame.Cols != stored.Cols
                || last.Frame.Rows != stored.Rows
                || (stored.OccupantGeneration > 0
                    && last.Frame.OccupantGeneration != stored.OccupantGeneration);
        }
    }

    internal List<LastPaintedPane>? BeginComposePainted(List<LastPaintedPane> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var parent = _composePainted.IsValueCreated ? _composePainted.Value : null;
        _composePainted.Value = pending;
        return parent;
    }

    internal void EndComposePainted(List<LastPaintedPane>? parent) =>
        _composePainted.Value = parent;

    internal void MergeComposePainted(List<LastPaintedPane>? parent, List<LastPaintedPane> child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (parent is null)
            return;
        foreach (var pane in child)
            ReplacePainted(parent, pane);
    }

    internal void NotePendingPainted(
        AssembledSnapshot frame,
        int originCol,
        int originRow,
        int clipCols,
        int clipRows)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var pending = _composePainted.IsValueCreated ? _composePainted.Value : null;
        if (pending is null)
            return;
        ReplacePainted(pending, new LastPaintedPane(frame, originCol, originRow, clipCols, clipRows));
    }

    internal bool CanAcceptPainted(
        IReadOnlyList<LastPaintedPane> pending,
        AssembledSnapshot? generationFrame)
    {
        ArgumentNullException.ThrowIfNull(pending);
        lock (_liveGate)
            return !HasStalePaintedUnlocked(pending, generationFrame);
    }

    internal void PublishPainted(IReadOnlyList<LastPaintedPane> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        lock (_liveGate)
        {
            foreach (var pane in pending)
                _lastPainted[pane.Frame.PaneId] = pane;
        }
    }

    internal void ClearLastPainted(string? paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_liveGate)
            _lastPainted.Remove(paneId);
    }

    private bool HasStalePaintedUnlocked(
        IReadOnlyList<LastPaintedPane> pending,
        AssembledSnapshot? generationFrame)
    {
        if (generationFrame is not null
            && HasNewerStoredUnlocked(generationFrame.PaneId, generationFrame.Generation))
        {
            return true;
        }

        foreach (var pane in pending)
        {
            if (HasNewerStoredUnlocked(pane.Frame.PaneId, pane.Frame.Generation))
                return true;
        }

        return false;
    }

    private bool HasNewerStoredUnlocked(string paneId, long generation)
    {
        if (_lastPainted.TryGetValue(paneId, out var last)
            && last.Frame.Generation > generation)
        {
            return true;
        }

        return _paneFrames.TryGetValue(paneId, out var stored)
            && stored.Generation > generation;
    }

    private static void ReplacePainted(List<LastPaintedPane> pending, LastPaintedPane pane)
    {
        pending.RemoveAll(p => string.Equals(p.Frame.PaneId, pane.Frame.PaneId, StringComparison.Ordinal));
        pending.Add(pane);
    }

    public void SetCompleteFrame(AssembledSnapshot frame) => SetPaneFrame(frame);

    internal void DropStoredPaneFrames()
    {
        lock (_liveGate)
        {
            _paneFrames.Clear();
            _paneInputModes.Clear();
            _spareGrids.Clear();
            _lastPainted.Clear();
            LastComplete = null;
            _deferredLive.Clear();
        }

        ResetAppliedBlit();
        lock (ChromeStateGate)
            ObservedPaneIds.Clear();
    }

    /// <summary>
    /// Puts <paramref name="prior"/> back as the frame of
    /// <paramref name="paneId"/>, but only while the stored frame is still
    /// <paramref name="expected"/>. First it copies <paramref name="undo"/>
    /// back in reverse order, so the shared storage holds the cells of
    /// <paramref name="prior"/> again. A null <paramref name="prior"/> drops
    /// the frame. Returns false and changes nothing when another frame was
    /// stored after <paramref name="expected"/>: that frame is newer.
    /// </summary>
    internal bool TryRestorePaneFrame(
        string paneId,
        AssembledSnapshot expected,
        AssembledSnapshot? prior,
        IReadOnlyList<AttachSession.PaneCellsUndo>? undo)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        ArgumentNullException.ThrowIfNull(expected);
        lock (_liveGate)
        {
            if (!_paneFrames.TryGetValue(paneId, out var current) || !ReferenceEquals(current, expected))
                return false;
            if (undo is not null)
            {
                for (var i = undo.Count - 1; i >= 0; i--)
                    undo[i].Apply();
            }

            if (prior is null)
                DropPaneFrame(paneId);
            else
                SetPaneFrame(prior);
            return true;
        }
    }

    /// <summary>
    /// A grid of <paramref name="length"/> cells for a body of
    /// <paramref name="paneId"/> that rewrites every row: the spare grid of
    /// the pane when it has that length and is not
    /// <paramref name="inUse"/> or the grid of the stored frame, else a new
    /// grid. The pane has no spare grid after this call.
    /// </summary>
    internal AssembledCell[] TakeSpareGrid(string paneId, int length, AssembledCell[] inUse)
    {
        ArgumentNullException.ThrowIfNull(inUse);
        lock (_liveGate)
        {
            if (_spareGrids.Remove(paneId, out var spare)
                && spare.Length == length
                && !ReferenceEquals(spare, inUse)
                && !(_paneFrames.TryGetValue(paneId, out var stored) && ReferenceEquals(stored.Storage, spare)))
            {
                return spare;
            }
        }

        return new AssembledCell[length];
    }

    /// <summary>
    /// Keeps <paramref name="grid"/> as the spare grid of
    /// <paramref name="paneId"/>, but only while the stored frame and the
    /// last painted frame of the pane do not use it. A restore paint can
    /// stamp the last painted frame, so its cells must not change. The paint
    /// that shows the stored frame makes it the last painted frame, so a
    /// shown pane keeps its two grids.
    /// </summary>
    internal void KeepSpareGrid(string paneId, AssembledCell[] grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        lock (_liveGate)
        {
            if (!_paneFrames.TryGetValue(paneId, out var stored)
                || ReferenceEquals(stored.Storage, grid)
                || (_lastPainted.TryGetValue(paneId, out var painted) && ReferenceEquals(painted.Frame.Storage, grid)))
            {
                return;
            }

            _spareGrids[paneId] = grid;
        }
    }

    internal void DropPaneFrame(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_liveGate)
        {
            _paneFrames.Remove(paneId);
            _paneInputModes.Remove(paneId);
            _spareGrids.Remove(paneId);
            if (LastComplete is { } complete
                && string.Equals(complete.PaneId, paneId, StringComparison.Ordinal))
            {
                LastComplete = null;
                _deferredLive.Clear();
            }
        }
    }

    /// <summary>
    /// Copies the input modes of the focused pane, the popup and the overlay
    /// pane into the key engine. It reads the mode copies that
    /// <see cref="SetPaneFrame"/> keeps, under the live lock. The caller
    /// holds the key engine lock.
    /// </summary>
    internal void SyncFocusedInputModes()
    {
        var paneId = PaneId;
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_liveGate)
        {
            if (_paneInputModes.TryGetValue(paneId, out var modes))
            {
                Engine.ApplicationCursor = modes.ApplicationCursor;
                Engine.BracketedPaste = modes.BracketedPaste;
            }

            Engine.PopupBracketedPaste = PopupSnapshot?.BracketedPaste == true;
            Engine.OverlayBracketedPaste =
                Engine.OverlayPaneId is { } overlayId
                && _paneInputModes.TryGetValue(overlayId, out var overlay)
                && overlay.BracketedPaste;
        }
    }

    /// <summary>The input modes of one pane frame, kept for the key feed.</summary>
    private readonly record struct PaneInputModes(bool ApplicationCursor, bool BracketedPaste);

    public void SetPaneFrame(AssembledSnapshot frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_liveGate)
        {
            _paneFrames[frame.PaneId] = frame;
            // The key feed reads this copy. Do not write the key engine
            // here: a frame paint does not hold the key engine lock.
            _paneInputModes[frame.PaneId] = new PaneInputModes(frame.ApplicationCursor, frame.BracketedPaste);
            _paneAlt[frame.PaneId] = frame.IsAlternateScreen;
            string? mouseToken = frame.Mouse;
            if (mouseToken is null
                && !frame.IgnoreSnapshotMouse
                && PaneMouseMode.TryRead(frame.Snapshot, out var snapshotMouse))
            {
                mouseToken = snapshotMouse;
            }

            if (mouseToken is not null || frame.MouseEncoding is not null)
            {
                if (!_paneMouse.TryGetValue(frame.PaneId, out var obs))
                {
                    obs = new PaneMouseObservation();
                    _paneMouse[frame.PaneId] = obs;
                }

                if (mouseToken is not null)
                    obs.Mode = string.IsNullOrWhiteSpace(mouseToken) ? PaneMouseMode.None : mouseToken;
                if (frame.MouseEncoding is not null)
                    PaneMouseMode.TryApplyEncodingToken(obs, frame.MouseEncoding);
            }
            var cursor = frame.Cursor ?? AssembledCursor.Default;
            SeedLiveCursorUnlocked(
                frame.PaneId,
                cursor.Col,
                cursor.Row,
                cursor.HasCursor && cursor.Visible);
            if (string.IsNullOrWhiteSpace(PaneId)
                || string.Equals(frame.PaneId, PaneId, StringComparison.Ordinal))
            {
                LastComplete = frame;
                _deferredLive.Clear();
            }
        }
    }

    public PaneLiveCursor GetLiveCursor(string paneId)
    {
        lock (_liveGate)
        {
            if (!_liveCursors.TryGetValue(paneId, out var cursor))
            {
                cursor = new PaneLiveCursor();
                _liveCursors[paneId] = cursor;
            }

            return cursor;
        }
    }

    public ClientOverlayCarry GetOverlayCarry(string paneId)
    {
        lock (_liveGate)
        {
            if (!_overlayCarries.TryGetValue(paneId, out var carry))
            {
                carry = new ClientOverlayCarry();
                _overlayCarries[paneId] = carry;
            }

            return carry;
        }
    }

    public bool GetPaneAlt(string paneId)
    {
        lock (_liveGate)
            return _paneAlt.TryGetValue(paneId, out var alt) && alt;
    }

    public void NotePaneAlt(string paneId, bool entered)
    {
        lock (_liveGate)
            _paneAlt[paneId] = entered;
    }

    public void NotePaneMouseMode(string paneId, string mode)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        var token = string.IsNullOrWhiteSpace(mode) ? PaneMouseMode.None : mode;
        lock (_liveGate)
        {
            if (_paneMouse.TryGetValue(paneId, out var obs))
                obs.Mode = token;
            else
                _paneMouse[paneId] = new PaneMouseObservation { Mode = token };
        }
    }

    public bool TryGetPaneMouseMode(string paneId, out string mode)
    {
        lock (_liveGate)
        {
            if (_paneMouse.TryGetValue(paneId, out var obs))
            {
                mode = obs.Mode;
                return true;
            }
        }

        mode = PaneMouseMode.None;
        return false;
    }

    public MouseProtocolEncoding GetPaneMouseEncoding(string paneId)
    {
        lock (_liveGate)
        {
            if (_paneMouse.TryGetValue(paneId, out var obs))
                return obs.Encoding;
        }

        return MouseProtocolEncoding.Sgr;
    }

    public void NotePaneMouseFromLive(string paneId, ReadOnlySpan<byte> bytes)
    {
        if (string.IsNullOrWhiteSpace(paneId) || bytes.IsEmpty)
            return;
        lock (_liveGate)
        {
            if (!_paneMouse.TryGetValue(paneId, out var obs))
            {
                obs = new PaneMouseObservation();
                _paneMouse[paneId] = obs;
            }

            PaneMouseMode.ApplyLiveBytes(obs, bytes);
        }
    }

    public void SeedLiveCursor(string paneId, int col, int row, bool visible = false)
    {
        lock (_liveGate)
            SeedLiveCursorUnlocked(paneId, col, row, visible);
    }

    private void SeedLiveCursorUnlocked(string paneId, int col, int row, bool visible = false)
    {
        if (!_liveCursors.TryGetValue(paneId, out var cursor))
        {
            cursor = new PaneLiveCursor();
            _liveCursors[paneId] = cursor;
        }

        cursor.Seed(col, row, visible);
        if (_paneFrames.TryGetValue(paneId, out var frame) && frame is not null)
            cursor.SeedGlyphs(frame);
        else
            cursor.ClearGlyphs();
    }

    public bool TryGetPaneFrame(string paneId, out AssembledSnapshot? frame)
    {
        lock (_liveGate)
            return _paneFrames.TryGetValue(paneId, out frame);
    }

    public void DiscardLiveFrame()
    {
        lock (_liveGate)
        {
            LastComplete = null;
            _deferredLive.Clear();
        }
    }

    public bool TryCopyRestore(string paneId, out AssembledSnapshot? frame, out byte[] tail)
    {
        lock (_liveGate)
        {
            frame = LastComplete;
            if (frame is not null
                && (string.IsNullOrWhiteSpace(paneId)
                    || !string.Equals(frame.PaneId, paneId, StringComparison.Ordinal)))
            {
                frame = null;
            }

            tail = CopyTailUnlocked();
            return frame is not null || tail.Length > 0;
        }
    }

    private static void AppendCapped(List<byte> dest, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= MaxDeferredLiveBytes)
        {
            dest.Clear();
            dest.AddRange(bytes[(bytes.Length - MaxDeferredLiveBytes)..].ToArray());
            return;
        }

        var overflow = dest.Count + bytes.Length - MaxDeferredLiveBytes;
        if (overflow > 0)
            dest.RemoveRange(0, overflow);
        dest.AddRange(bytes.ToArray());
    }

    private byte[] CopyTailUnlocked() =>
        _deferredLive.Count == 0 ? [] : _deferredLive.ToArray();
}

internal sealed record ChromeComputeSeed(
    LayoutNodeDto? Root,
    bool Zoomed,
    string? ZoomedPaneId,
    string? FocusedPaneId,
    int Cols,
    int Rows);
