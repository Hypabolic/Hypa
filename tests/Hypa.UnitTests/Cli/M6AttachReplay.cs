using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Config;
using Hypa.Cli.Attach.EditScrollback;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Notification;
using Hypa.Cli.Attach.Onboarding;
using Hypa.Cli.Attach.Plugins;
using Hypa.Cli.Attach.Settings;
using Hypa.Cli.Attach.Sidebar;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// Reconstructed TTY-byte replay. Feeds committed stdin through
/// <see cref="AttachSession.DecodeInput"/> then the attach apply path.
/// Capture kind is reconstructed_tty_script, not a live TTY recording.
/// </summary>
internal sealed class M6AttachReplay : IDisposable
{
    public const string CaptureKind = "reconstructed_tty_script";
    public const string InputMouse = "sgr_1006";
    public const string InputPrefix = "prefix_keys";
    public const string Hostname = "hypa";

    private readonly MemoryStream _capture = new();
    private readonly List<byte> _csi = new(16);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public M6AttachReplay(string suite)
    {
        Suite = suite;
        Cols = 80;
        Rows = 24;
        var files = new MemoryConfigFiles();
        var path = "/tmp/h45-" + suite + "/config.toml";
        var fixtureToml = Path.Combine(FixtureDir(suite), "config.toml");
        var toml = File.Exists(fixtureToml)
            ? File.ReadAllText(fixtureToml)
            : DefaultFixtureToml(onboarding: false);
        files.WriteAllText(path, toml);
        var env = new MapEnv(path);
        env["VISUAL"] = "true";
        var loader = new FileAttachConfigLoader(env, files);
        var loaded = loader.Load();
        if (!loaded.IsOk)
            throw new InvalidOperationException(loaded.Error.ToString());
        var attachConfig = loaded.Value;
        Ui = attachConfig.Ui;
        var keys = KeysConfigMapper.Map(attachConfig.Keys);
        Table = KeyBindingTable.CompileOrThrow(keys);
        Port = new M6RecordingPort(SlotsSuite(suite));
        Engine = new KeyEngine(Table, chrome: AttachChromePolicy.FromUi(Ui));
        Dispatcher = new AttachCommandDispatcher(Port, "w1", "t1", "p1", "lease-r")
        {
            Commands = keys.Commands,
        };
        Tty = new UnixRawTerminal(_capture, Cols, Rows);
        Linked = new CancellationTokenSource();
        ConfigFiles = files;
        ConfigPath = path;
        ConfigLoader = loader;
        var themeName = string.IsNullOrWhiteSpace(attachConfig.Theme.Name)
            ? "catppuccin"
            : attachConfig.Theme.Name;
        var sidebarOpen = AttachSession.SidebarOpenAtAttach(Ui);
        Live = new AttachLiveState
        {
            Engine = Engine,
            Table = Table,
            Ui = Ui,
            Dispatcher = Dispatcher,
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = "w1",
            TabId = "t1",
            PaneId = "p1",
            InputLease = "lease-in",
            ResizeLease = "lease-r",
            ChromeEnabled = true,
            SidebarOpen = sidebarOpen,
            SidebarCollapsed = !sidebarOpen,
            SidebarWidth = Ui.SidebarWidth,
            SidebarRows = DefaultSidebarRows(),
            ConfigLoader = loader,
            AttachEnvironment = env,
            ScrollbackFiles = new MemoryScrollback(),
            Theme = ThemeRuntime.FromConfig(new AttachThemeConfig
            {
                Name = themeName,
                AutoSwitch = attachConfig.Theme.AutoSwitch,
            }).Value,
            Notifications = new NotificationDirector(Ui),
        };
        Live.Mouse.Configure(MouseEngineOptions.FromUi(Ui, captureOverride: true));
        Live.Notifications.SetFocus("t1", "p1");
        Live.SetPaneFrame(MouseTestGeom.Frame("hello world", "p1"));
        Live.Chrome = ComputeChrome(Cols, Rows, split: false, zoomed: false);
        AttachSession.ApplyOnboardingAtStart(Live, attachConfig);
        SidebarOpenAtStart = Live.SidebarOpen;
    }

    public string Suite { get; }

    public int Cols { get; private set; }

    public int Rows { get; private set; }

    public AttachUiConfig Ui { get; }

    public KeyBindingTable Table { get; }

    public KeyEngine Engine { get; }

    public AttachCommandDispatcher Dispatcher { get; }

    public M6RecordingPort Port { get; }

    public AttachLiveState Live { get; }

    public UnixRawTerminal Tty { get; }

    public CancellationTokenSource Linked { get; }

    public MemoryConfigFiles ConfigFiles { get; }

    public string ConfigPath { get; }

    public FileAttachConfigLoader ConfigLoader { get; }

    public List<string> Checkpoints { get; } = [];

    public Func<string, Task>? OnCheckpoint { get; set; }

    public HashSet<string> Observed { get; } = new(StringComparer.Ordinal);

    public string? DetachSource { get; private set; }

    public bool SidebarOpenAtStart { get; }

    public bool SidebarUsed { get; private set; }

    public KeyActionId? LastIndexedAction { get; private set; }

    public bool SawSettings { get; private set; }

    public bool SawOnboarding { get; private set; }

    public bool PopupOpened { get; private set; }

    public bool PopupClosedFromApply { get; private set; }

    public static AttachClientConfig LoadSuiteConfig(string suite)
    {
        var fixtureToml = Path.Combine(FixtureDir(suite), "config.toml");
        var toml = File.Exists(fixtureToml)
            ? File.ReadAllText(fixtureToml)
            : DefaultFixtureToml(onboarding: false);
        var loaded = TomlAttachConfigBinder.Bind(toml);
        if (!loaded.IsOk)
            throw new InvalidOperationException(loaded.Error.ToString());
        return loaded.Value;
    }

    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypa.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    public static string FixtureDir(string suite) =>
        Path.Combine(
            FindRepoRoot(),
            "tests",
            "Hypa.AgentRuntime.Tests",
            "Fixtures",
            "m6",
            suite);

    public static M6GoldenMeta LoadMeta(string suite)
    {
        var path = Path.Combine(FixtureDir(suite), "meta.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, M6GoldenJsonContext.Default.M6GoldenMeta)
            ?? throw new InvalidOperationException("meta.json missing for " + suite);
    }

    public static M6GoldenExpected LoadExpected(string suite)
    {
        var path = Path.Combine(FixtureDir(suite), "expected.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, M6GoldenJsonContext.Default.M6GoldenExpected)
            ?? throw new InvalidOperationException("expected.json missing for " + suite);
    }

    public static IReadOnlyList<M6ScriptLine> LoadScript(string suite)
    {
        var path = Path.Combine(FixtureDir(suite), "session.script.ndjson");
        var lines = new List<M6ScriptLine>();
        foreach (var raw in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var line = JsonSerializer.Deserialize(raw, M6GoldenJsonContext.Default.M6ScriptLine)
                ?? throw new InvalidOperationException("bad script line in " + suite);
            lines.Add(line);
        }

        return lines;
    }

    public async Task ReplayAsync(IReadOnlyList<M6ScriptLine> script)
    {
        await HandshakeAsync().ConfigureAwait(false);
        foreach (var line in script)
        {
            switch (line.Kind)
            {
                case "checkpoint":
                    Checkpoints.Add(line.Name ?? "");
                    if (OnCheckpoint is not null)
                        await OnCheckpoint(line.Name ?? "").ConfigureAwait(false);
                    break;
                case "click":
                    await ClickSlotAsync(line.Name ?? "").ConfigureAwait(false);
                    ReseedFrame();
                    break;
                case "resize":
                    Cols = line.Cols ?? Cols;
                    Rows = line.Rows ?? Rows;
                    Tty.PinSize(Cols, Rows);
                    Engine.NarrowLayout = NarrowLayout.IsNarrow(Cols, Ui.MobileWidthThreshold);
                    Live.Chrome = ComputeChrome(Cols, Rows, Port.Split, Port.Zoomed);
                    await AttachSession.RefreshChromeAsync(Port, Live, Tty, CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case "event":
                    await ApplyScriptEventAsync(line).ConfigureAwait(false);
                    break;
                case "stdin":
                    await FeedStdinAsync(line.Data ?? "").ConfigureAwait(false);
                    ReseedFrame();
                    break;
                default:
                    throw new InvalidOperationException("unknown script kind " + line.Kind);
            }

            NoteObserved();
        }
    }

    public IReadOnlyList<string> RpcMethods =>
        Port.Calls.Select(c => c.Method).Distinct().ToArray();

    public void Dispose()
    {
        Linked.Dispose();
        Tty.Dispose();
        _capture.Dispose();
        _gate.Dispose();
    }

    private async Task FeedStdinAsync(string data)
    {
        var raw = Convert.FromBase64String(data);
        if (raw.IndexOf((byte)0x02) >= 0)
            AssertNoMouseCsi(raw);
        var decoded = AttachSession.DecodeInput(
            raw,
            _csi,
            out _,
            out _,
            out var mouse,
            out _,
            out _);
        decoded.AddRange(AttachSession.FlushPendingCsi(_csi));
        if (mouse.Count > 0)
        {
            AssertNoPrefixCommand(raw);
            foreach (var ev in mouse)
            {
                if (await DispatchDecodedMouseAsync(ev).ConfigureAwait(false))
                    return;
            }
        }

        if (decoded.Count == 0)
            return;

        if (AttachSession.ConsumesContextMenuKeys(Live))
        {
            _ = await AttachSession.HandleContextMenuKeysAsync(
                    Tty, Live, decoded, Port, _gate, Linked, CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        var events = Engine.Feed(CollectionsMarshalAsSpan(decoded));
        foreach (var ev in events)
        {
            if (ev.Kind is KeyEngineEventKind.Dispatch
                && ev.Action is KeyActionId.IndexedTabs
                    or KeyActionId.IndexedWorkspaces
                    or KeyActionId.IndexedAgents)
            {
                LastIndexedAction = ev.Action;
            }
        }

        _ = await AttachSession.ApplyEngineEventsAsync(
                Tty, Live, events, Port, Linked, CancellationToken.None)
            .ConfigureAwait(false);
        NoteKeyDetach();
        NoteSidebarFromApply();
    }

    private async Task<bool> DispatchDecodedMouseAsync(MouseEvent ev)
    {
        if (Engine.Mode is AttachClientMode.Onboarding
            && ev.Button is MouseButton.Left
            && ev.Action is MouseAction.Release)
        {
            await AttachSession.ApplyOnboardingPointerAsync(
                    Live, ev.Col, ev.Row, Port, Tty, CancellationToken.None)
                .ConfigureAwait(false);
            return Live.DetachRequested;
        }

        if (Engine.Mode is AttachClientMode.Settings
            && ev.Button is MouseButton.Left
            && ev.Action is MouseAction.Release)
        {
            await AttachSession.ApplySettingsPointerAsync(
                    Live, ev.Col, ev.Row, Port, Tty, CancellationToken.None)
                .ConfigureAwait(false);
            return Live.DetachRequested;
        }

        var ctx = AttachSession.MouseFeedContextFor(Live);
        var beforeMenu = Live.Mouse.Menu?.Kind;
        foreach (var result in Live.Mouse.Feed(ev, ctx))
        {
            NoteMouseSidebar(result);
            NoteMouseDetach(result, beforeMenu);
            if (await AttachSession.ApplyMouseResultAsync(
                    result, Live, Port, Tty, Linked, CancellationToken.None)
                .ConfigureAwait(false))
            {
                NoteMouseDetach(result, beforeMenu);
                return true;
            }
        }

        return Live.DetachRequested;
    }

    private async Task ApplyScriptEventAsync(M6ScriptLine line)
    {
        var type = line.Type ?? ProtocolEventTypes.PaneAgentStatusChanged;
        JsonObject json;
        if (string.Equals(type, ProtocolEventTypes.PopupLifecycle, StringComparison.Ordinal))
        {
            var payload = new JsonObject
            {
                ["state"] = line.State ?? "closed",
            };
            if (line.Cols is { } cols)
                payload["cols"] = cols;
            if (line.Rows is { } rows)
                payload["rows"] = rows;
            var parms = new JsonObject
            {
                ["type"] = ProtocolEventTypes.PopupLifecycle,
                ["payload"] = payload,
            };
            if (line.Seq is { } seq)
                parms["seq"] = seq;
            json = new JsonObject
            {
                ["event"] = ProtocolEventTypes.RuntimeEvent,
                ["params"] = parms,
            };
        }
        else if (string.Equals(type, ProtocolEventTypes.PaneAgentStatusChanged, StringComparison.Ordinal))
        {
            json = new JsonObject
            {
                ["event"] = ProtocolEventTypes.RuntimeEvent,
                ["params"] = new JsonObject
                {
                    ["type"] = ProtocolEventTypes.PaneAgentStatusChanged,
                    ["payload"] = new JsonObject
                    {
                        ["pane_id"] = line.PaneId ?? "p_bg",
                        ["tab_id"] = line.TabId ?? "t_bg",
                        ["occupant_generation"] = line.OccupantGeneration ?? 1,
                        ["agent_status"] = line.AgentStatus ?? "blocked",
                        ["agent"] = "claude",
                        ["message"] = "wait",
                        ["seen"] = line.Seen ?? false,
                    },
                },
            };
        }
        else
        {
            return;
        }

        using var doc = JsonDocument.Parse(json.ToJsonString());
        var beforePopup = Live.PopupOpen;
        await AttachSession.ApplyControlEventsAsync(
                [doc.RootElement.Clone()],
                Live,
                Port,
                Tty,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (string.Equals(type, ProtocolEventTypes.PopupLifecycle, StringComparison.Ordinal))
        {
            if (Live.PopupOpen)
                PopupOpened = true;
            if (beforePopup && !Live.PopupOpen)
                PopupClosedFromApply = true;
            if (string.Equals(line.State, "closed", StringComparison.Ordinal) && Live.PopupSawClosed)
                PopupClosedFromApply = true;
        }
    }

    private void NoteMouseSidebar(MouseEngineResult result)
    {
        if (result.Hit?.Kind is ChromeHitKind.SidebarWorkspace
            or ChromeHitKind.SidebarAgent
            or ChromeHitKind.SidebarNew
            or ChromeHitKind.SidebarMenu)
        {
            SidebarUsed = true;
        }

        if (result.Kind is MouseCommandKind.OpenMenu
            && result.Menu?.Kind is ContextMenuKind.Global)
        {
            SidebarUsed = true;
        }
    }

    private void NoteMouseDetach(MouseEngineResult result, ContextMenuKind? before)
    {
        if (result.Kind is MouseCommandKind.Detach
            || (result.Kind is MouseCommandKind.ApplyMenu
                && result.MenuItem?.Id == ContextMenuModel.Detach))
        {
            DetachSource = before is ContextMenuKind.Global || result.Menu?.Kind is ContextMenuKind.Global
                ? "menu"
                : "mouse";
        }

        if (Live.DetachRequested && DetachSource is null)
            DetachSource = "menu";
    }

    private void NoteKeyDetach()
    {
        if (Live.DetachRequested && DetachSource is null)
            DetachSource = "prefix+q";
    }

    private void ReseedFrame()
    {
        var id = string.IsNullOrWhiteSpace(Live.PaneId) ? "p1" : Live.PaneId;
        Live.SetPaneFrame(MouseTestGeom.Frame("hello world hello world", id, cols: 40));
        if (!string.Equals(id, "p2", StringComparison.Ordinal))
            Live.SetPaneFrame(MouseTestGeom.Frame("hello world hello world", "p2", cols: 40));
    }

    private async Task HandshakeAsync()
    {
        var snap = await Port.CallAsync(ProtocolMethods.SessionSnapshot, null, CancellationToken.None)
            .ConfigureAwait(false);
        _ = snap;
        var input = await Port.CallAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                new JsonObject
                {
                    ["pane_id"] = Live.PaneId,
                    ["scope"] = "input",
                    ["ttl_ms"] = 30_000,
                    ["takeover"] = true,
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        var resize = await Port.CallAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                new JsonObject
                {
                    ["pane_id"] = Live.PaneId,
                    ["scope"] = "resize",
                    ["ttl_ms"] = 30_000,
                    ["takeover"] = true,
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        Live.InputLease = ReadString(input, "lease_id") ?? Live.InputLease;
        Live.ResizeLease = ReadString(resize, "lease_id") ?? Live.ResizeLease;
        if (!string.IsNullOrWhiteSpace(Live.InputLease))
            Live.Renew.Track(Live.InputLease);
        if (!string.IsNullOrWhiteSpace(Live.ResizeLease))
            Live.Renew.Track(Live.ResizeLease);

        var controlSub = await Port.CallAsync(
                ProtocolMethods.EventsSubscribe,
                SubscribeParams("control", "lifecycle"),
                CancellationToken.None)
            .ConfigureAwait(false);
        Live.ControlSub = ReadString(controlSub, "subscription_id") ?? "";
        await Port.CallAsync(
                ProtocolMethods.TerminalControl,
                new JsonObject
                {
                    ["pane_id"] = Live.PaneId,
                    ["lease_id"] = Live.InputLease,
                    ["subscription_id"] = Live.ControlSub,
                    ["replace"] = true,
                },
                CancellationToken.None)
            .ConfigureAwait(false);

        var renderSub = await Port.CallAsync(
                ProtocolMethods.EventsSubscribe,
                SubscribeParams("render", "lifecycle"),
                CancellationToken.None)
            .ConfigureAwait(false);
        Live.RenderSub = ReadString(renderSub, "subscription_id");
        await AttachSession.RefreshChromeAsync(Port, Live, Tty, CancellationToken.None)
            .ConfigureAwait(false);
        var before = _capture.Length;
        AttachSession.PaintChrome(Tty, Live);
        var painted = _capture.Length > before;
        await Port.CallAsync(
                ProtocolMethods.TerminalObserve,
                new JsonObject
                {
                    ["pane_id"] = Live.PaneId,
                    ["subscription_id"] = Live.RenderSub,
                    ["replace"] = true,
                },
                CancellationToken.None)
            .ConfigureAwait(false);

        var sawSnap = Port.Calls.Any(c => c.Method == ProtocolMethods.SessionSnapshot);
        var sawObserve = Port.Calls.Any(c => c.Method == ProtocolMethods.TerminalObserve);
        var sawControl = Port.Calls.Any(c => c.Method == ProtocolMethods.TerminalControl);
        if (sawSnap && sawObserve && sawControl && painted)
            Observed.Add("attach");
        await SeedSlotsToastAsync().ConfigureAwait(false);
    }

    private async Task SeedSlotsToastAsync()
    {
        if (!SlotsSuite(Suite))
            return;
        Live.Toasts = Live.Notifications.OnNotificationShown(
            "slot toast",
            "waiting",
            "plugin:slots",
            NotificationSounds.Request,
            Live.PaneId,
            Cols,
            Rows);
        await AttachSession.RefreshChromeAsync(Port, Live, Tty, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task ClickSlotAsync(string target)
    {
        if (!SlotsSuite(Suite) || !SuiteStartsWithMouse(Suite))
            throw new InvalidOperationException("click lines are only valid on mouse-slots");
        switch (target)
        {
            case "sidebar-item":
                await ClickHitAsync(FindCollectionItem()).ConfigureAwait(false);
                SidebarUsed = true;
                break;
            case "sidebar-menu":
                await ClickHitAsync(FindGlobalMenuAnchor()).ConfigureAwait(false);
                break;
            case "menu-settings":
                await ClickMenuItemAsync(GlobalMenuModel.Settings).ConfigureAwait(false);
                break;
            case "settings-close":
                await ClickHitAsync(FindSettingsClose()).ConfigureAwait(false);
                break;
            case "menu-detach":
                await ClickMenuItemAsync(ContextMenuModel.Detach).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("unknown click target " + target);
        }
    }

    private async Task ClickMenuItemAsync(string itemId)
    {
        var menu = Live.Mouse.Menu ?? Live.MouseMenu
            ?? throw new InvalidOperationException("global menu is not open");
        var index = -1;
        for (var i = 0; i < menu.Items.Count; i++)
        {
            if (string.Equals(menu.Items[i].Id, itemId, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            throw new InvalidOperationException("menu item not found: " + itemId);
        await ClickHitAsync((menu.Rect.Col, menu.Rect.Row + index)).ConfigureAwait(false);
    }

    private (int Col, int Row) FindCollectionItem()
    {
        var geo = AttachSession.ChromeForHitTest(Live)
            ?? throw new InvalidOperationException("chrome missing for collection click");
        foreach (var hit in geo.SidebarRows)
        {
            if (hit.Kind is not SidebarStubKind.CollectionItem)
                continue;
            return (hit.Rect.Col, hit.Rect.Row);
        }

        throw new InvalidOperationException("slots collection row is not hittable");
    }

    private (int Col, int Row) FindGlobalMenuAnchor()
    {
        var geo = AttachSession.ChromeForHitTest(Live)
            ?? throw new InvalidOperationException("chrome missing for menu click");
        if (!Live.SidebarOpen)
            throw new InvalidOperationException("sidebar is closed");
        foreach (var hit in geo.SidebarRows)
        {
            if (hit.Kind is not SidebarStubKind.Menu)
                continue;
            return (hit.Rect.Col, hit.Rect.Row);
        }

        throw new InvalidOperationException("spaces menu hit is missing");
    }

    private (int Col, int Row) FindSettingsClose()
    {
        if (Engine.Mode is not AttachClientMode.Settings)
            throw new InvalidOperationException("settings overlay is not open");
        Live.Engine.Settings.Layout ??= SettingsPainter.Measure(Live.Engine.Settings, Cols, Rows);
        var close = Live.Engine.Settings.Layout.Close;
        if (close.Cols <= 0 || close.Rows <= 0)
            throw new InvalidOperationException("settings close hit is missing");
        return (close.Col, close.Row);
    }

    private async Task ClickHitAsync((int Col, int Row) cell)
    {
        var press = new MouseEvent(MouseButton.Left, MouseAction.Press, cell.Col, cell.Row);
        var release = new MouseEvent(MouseButton.Left, MouseAction.Release, cell.Col, cell.Row);
        var pressBytes = MouseDecoder.EncodeSgr(press, cell.Col + 1, cell.Row + 1);
        var releaseBytes = MouseDecoder.EncodeSgr(release, cell.Col + 1, cell.Row + 1);
        await FeedStdinAsync(Convert.ToBase64String(Concat(pressBytes, releaseBytes)))
            .ConfigureAwait(false);
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var raw = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, raw, 0, first.Length);
        Buffer.BlockCopy(second, 0, raw, first.Length, second.Length);
        return raw;
    }

    private void NoteSidebarFromApply()
    {
        if (SuiteStartsWithMouse(Suite))
            return;
        if (Live.SidebarOpen == SidebarOpenAtStart)
            return;
        if (Live.Chrome is { Sidebar: not null } chrome
            && chrome.SidebarOpen == Live.SidebarOpen)
        {
            SidebarUsed = true;
        }
    }

    private LayoutChromeGeometry ComputeChrome(int cols, int rows, bool split, bool zoomed)
    {
        LayoutNodeDto root = split
            ? MouseTestGeom.RightSplit()
            : new LayoutNodeDto { Type = "pane", PaneId = "p1", Label = "main" };
        var tabs = Port.CreatedTab
            ? new (string Id, string Label, bool Active)[] { ("t1", "one", true), ("t2", "two", false) }
            : [("t1", "one", true)];
        return LayoutChromeGeometry.Compute(
            cols,
            rows,
            root,
            zoomed,
            zoomed ? "p1" : null,
            Live.PaneId,
            Ui,
            tabs.Length,
            Engine.PaintMode,
            tabs,
            hostname: Hostname,
            sidebarOpen: Live.SidebarOpen,
            sidebarWidth: Ui.SidebarWidth,
            sidebarRows: Live.SidebarRows.Count > 0 ? Live.SidebarRows : DefaultSidebarRows(),
            toasts: Live.Toasts,
            switcherOpen: Engine.NarrowLayout || Engine.Mode is AttachClientMode.MobileSwitcher,
            sidebarFrame: Live.SidebarFrame,
            spacesScroll: Live.SidebarSpacesScroll,
            agentsScroll: Live.SidebarAgentsScroll);
    }

    private void NoteObserved()
    {
        if (Engine.Mode is AttachClientMode.Onboarding || Engine.PaintMode is AttachClientMode.Onboarding)
            SawOnboarding = true;
        if (Engine.Mode is AttachClientMode.Settings || Engine.PaintMode is AttachClientMode.Settings)
            SawSettings = true;
        if (Port.Calls.Any(c => c.Method == ProtocolMethods.TabCreate))
            Observed.Add("tab.create");
        if (Port.Calls.Any(c => c.Method == ProtocolMethods.PaneSplit))
            Observed.Add("pane.split");
        if (Port.Calls.Any(c => c.Method == ProtocolMethods.PaneFocus))
            Observed.Add("pane.focus");
        if (Port.Calls.Any(c => c.Method == ProtocolMethods.PaneZoom))
            Observed.Add("pane.zoom");
        if (Live.LastOsc52 is { Length: > 0 }
            && Encoding.ASCII.GetString(Live.LastOsc52).StartsWith("\u001b]52;c;", StringComparison.Ordinal))
        {
            Observed.Add("copy");
        }

        NoteSidebarFromApply();
        if (SidebarUsed)
            Observed.Add("sidebar");
        if (Live.DetachRequested)
            Observed.Add("detach");
        if (SawOnboarding
            && ConfigFiles.ReadAllText(ConfigPath).Contains("onboarding = false", StringComparison.Ordinal))
        {
            Observed.Add("onboarding");
        }

        if (SawSettings)
            Observed.Add("settings");
        if (!string.Equals(Live.Theme.Name, "catppuccin", StringComparison.Ordinal))
            Observed.Add("themes");
        if (Live.Toasts.Any(t => string.Equals(t.PaneId, "p_bg", StringComparison.Ordinal))
            || string.Equals(Live.Notifications.LastTargetPaneId, "p_bg", StringComparison.Ordinal)
            || Port.Calls.Any(c =>
                c.Method == ProtocolMethods.PaneFocus
                && c.Params?["pane_id"]?.GetValue<string>() == "p_bg"))
        {
            Observed.Add("notify");
        }

        if (Engine.NarrowLayout && Live.Chrome is { IsNarrow: true })
            Observed.Add("narrow");
        if (Port.Calls.Any(c => c.Method == ProtocolMethods.PopupOpen))
            Observed.Add("custom-command");
        if (PopupClosedFromApply || (Live.PopupSawClosed && Port.Calls.Any(c => c.Method == ProtocolMethods.PopupOpen)))
            Observed.Add("popup");
        if (Port.Calls.Any(c => c.Method == ProtocolMethods.PaneRead))
            Observed.Add("edit-scrollback");
        if (LastIndexedAction is KeyActionId.IndexedTabs
            or KeyActionId.IndexedWorkspaces
            or KeyActionId.IndexedAgents)
        {
            Observed.Add("indexed");
        }
    }

    private static bool SuiteStartsWithMouse(string suite) =>
        suite.StartsWith("mouse", StringComparison.Ordinal);

    private static bool SlotsSuite(string suite) =>
        string.Equals(suite, "mouse-slots", StringComparison.Ordinal)
        || string.Equals(suite, "prefix-slots", StringComparison.Ordinal);

    private static JsonObject SubscribeParams(params string[] types)
    {
        var nodes = new JsonNode?[types.Length];
        for (var i = 0; i < types.Length; i++)
            nodes[i] = JsonValue.Create(types[i]);
        return new JsonObject
        {
            ["types"] = new JsonArray(nodes),
            ["live"] = true,
            ["from_seq"] = 0,
        };
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static string DefaultFixtureToml(bool onboarding) =>
        onboarding
            ? """
              onboarding = true
              [theme]
              name = "catppuccin"
              auto_switch = true
              """
            : """
              onboarding = false
              [theme]
              name = "catppuccin"
              auto_switch = true
              """;

    private static SidebarStubRow[] DefaultSidebarRows() =>
    [
        new(SidebarStubKind.Workspace, "w1", "space", 0, Selected: true),
        new(SidebarStubKind.New, "new", "new", 1),
        new(SidebarStubKind.Menu, "menu", "menu", 2),
    ];

    private static ReadOnlySpan<byte> CollectionsMarshalAsSpan(List<byte> decoded) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(decoded);

    private static void AssertNoPrefixCommand(byte[] raw)
    {
        foreach (var b in raw)
        {
            if (b == 0x02)
                throw new InvalidOperationException("mouse-only script contains prefix 0x02");
        }
    }

    private static void AssertNoMouseCsi(byte[] raw)
    {
        var text = Encoding.ASCII.GetString(raw);
        if (text.Contains("\u001b[<", StringComparison.Ordinal)
            || text.Contains("\u001b[M", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("prefix-only script contains mouse CSI");
        }
    }

    internal sealed class M6RecordingPort : IAttachCommandPort
    {
        private readonly string _pluginListJson;
        private readonly string _snapshotJson;

        public M6RecordingPort(bool slotsFixture = false)
        {
            if (slotsFixture)
            {
                _pluginListJson = SlotsPluginListJson();
                _snapshotJson = SlotsSnapshotJson();
            }
            else
            {
                _pluginListJson = "{}";
                _snapshotJson = SnapshotJson();
            }
        }

        public List<(string Method, JsonObject? Params)> Calls { get; } = [];

        public bool CreatedTab { get; private set; }

        public bool Split { get; private set; }

        public bool Zoomed { get; private set; }

        public string FocusedPane { get; private set; } = "p1";

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add((method, parameters));
            if (method == ProtocolMethods.TabCreate)
                CreatedTab = true;
            if (method == ProtocolMethods.PaneSplit)
                Split = true;
            if (method == ProtocolMethods.PaneZoom)
            {
                var mode = parameters?["mode"]?.GetValue<string>();
                Zoomed = mode is null or "toggle" or "on";
                if (mode == "off")
                    Zoomed = false;
            }

            if (method == ProtocolMethods.PaneFocus)
                FocusedPane = parameters?["pane_id"]?.GetValue<string>() ?? FocusedPane;

            return Task.FromResult(Response(method, parameters));
        }

        private JsonElement Response(string method, JsonObject? parameters)
        {
            return method switch
            {
                ProtocolMethods.TabCreate => Parse(
                    """{"tab_id":"t2","workspace_id":"w1","focused_pane_id":"p1"}"""),
                ProtocolMethods.PaneSplit => Parse(
                    """{"pane_id":"p2"}"""),
                ProtocolMethods.RuntimeLeaseClaim => Parse(
                    "{\"lease_id\":\"lease-"
                    + (parameters?["scope"]?.GetValue<string>() ?? "next")
                    + "\",\"outcome\":\"granted\"}"),
                ProtocolMethods.EventsSubscribe => Parse(
                    "{\"subscription_id\":\"sub-" + Calls.Count + "\"}"),
                ProtocolMethods.TerminalObserve => Parse(
                    """{"attachment_id":"att-observe"}"""),
                ProtocolMethods.TerminalControl => Parse(
                    """{"attachment_id":"att-control"}"""),
                ProtocolMethods.LayoutExport => Parse(LayoutJson()),
                ProtocolMethods.TabList => Parse(TabListJson()),
                ProtocolMethods.SessionSnapshot => Parse(_snapshotJson),
                ProtocolMethods.PluginList => Parse(_pluginListJson),
                ProtocolMethods.PluginConfigGet => Parse(
                    """{"plugin_id":"slots","values":{"theme":"dark"}}"""),
                ProtocolMethods.PaneRead => Parse(
                    """{"text":"hello world"}"""),
                ProtocolMethods.PopupOpen => Parse(
                    """{"ok":true}"""),
                ProtocolMethods.PopupClose => Parse(
                    """{"ok":true}"""),
                ProtocolMethods.WorkspaceCreate => Parse(
                    """{"workspace_id":"w9","focused_tab_id":"t9","focused_pane_id":"p9"}"""),
                ProtocolMethods.WorkspaceList => Parse(
                    """[{"workspace_id":"w1","label":"space"}]"""),
                _ => Parse("{}"),
            };
        }

        private string LayoutJson()
        {
            var zoomed = Zoomed ? "true" : "false";
            if (Split)
            {
                return "{\"workspace_id\":\"w1\",\"tab_id\":\"t1\",\"zoomed\":" + zoomed
                    + ",\"focused_pane_id\":\"" + FocusedPane
                    + "\",\"root\":{\"type\":\"split\",\"direction\":\"right\",\"ratio\":0.5,"
                    + "\"first\":{\"type\":\"pane\",\"pane_id\":\"p1\"},"
                    + "\"second\":{\"type\":\"pane\",\"pane_id\":\"p2\"}}}";
            }

            return "{\"workspace_id\":\"w1\",\"tab_id\":\"t1\",\"zoomed\":" + zoomed
                + ",\"focused_pane_id\":\"" + FocusedPane
                + "\",\"root\":{\"type\":\"pane\",\"pane_id\":\"p1\"}}";
        }

        private string TabListJson() =>
            CreatedTab
                ? """[{"tab_id":"t1","label":"one"},{"tab_id":"t2","label":"two"}]"""
                : """[{"tab_id":"t1","label":"one"}]""";

        private static string SnapshotJson() =>
            """
            {
              "focused_workspace_id": "w1",
              "focused_tab_id": "t1",
              "workspaces": [
                {"workspace_id": "w1", "label": "space", "cwd": "/tmp/a", "focused_tab_id": "t1"}
              ],
              "tabs": [
                {"tab_id": "t1", "workspace_id": "w1", "label": "one", "focused_pane_id": "p1", "ordinal": 0}
              ],
              "panes": [
                {"pane_id": "p1", "tab_id": "t1", "workspace_id": "w1", "label": "main", "state": "idle"}
              ]
            }
            """;

        private static string SlotsSnapshotJson() =>
            """
            {
              "focused_workspace_id": "w1",
              "focused_tab_id": "t1",
              "workspaces": [
                {"workspace_id": "w1", "label": "space", "cwd": "/tmp/a", "focused_tab_id": "t1"}
              ],
              "tabs": [
                {"tab_id": "t1", "workspace_id": "w1", "label": "one", "focused_pane_id": "p1", "ordinal": 0}
              ],
              "panes": [
                {"pane_id": "p1", "tab_id": "t1", "workspace_id": "w1", "label": "main", "state": "idle"}
              ],
              "resources": [
                {
                  "owner_id": "plugin:slots",
                  "resource_id": "plugin:slots/queue",
                  "schema": "hypa.projection.collection.v1",
                  "revision": 1,
                  "freshness": "ready",
                  "value": {
                    "summary": "3 need attention",
                    "items": [
                      {
                        "id": "work-42",
                        "label": "Plan retry",
                        "status": "blocked",
                        "attention": 2,
                        "tokens": { "title": "Plan retry", "age": "12m" },
                        "action": "open"
                      }
                    ]
                  }
                }
              ]
            }
            """;

        private static string SlotsPluginListJson()
        {
            var parsed = new PluginManifestParser().Parse(OfficialPluginAssets.SlotsManifest);
            if (!parsed.IsOk)
                throw new InvalidOperationException(parsed.Error.Message);
            var plugin = InstalledPluginWireMapper.FromManifest(
                parsed.Value,
                "slots/hypa-plugin.toml");
            var json = JsonSerializer.Serialize(
                new PluginListResult { Plugins = [InstalledPluginWireMapper.ToDto(plugin)] },
                ProtocolJsonContext.Default.PluginListResult);
            return json;
        }

        private static JsonElement Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
    }

    internal sealed class MemoryConfigFiles : IAttachConfigFiles
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public bool FileExists(string path) => _files.ContainsKey(path);

        public string ReadAllText(string path) =>
            _files.TryGetValue(path, out var text) ? text : "";

        public void WriteAllText(string path, string contents) =>
            _files[path] = contents ?? "";

        public void CopyFile(string source, string destination) =>
            _files[destination] = _files[source];
    }

    private sealed class MapEnv(string path) : IAttachConfigEnvironment
    {
        private readonly Dictionary<string, string> _vars = new(StringComparer.Ordinal)
        {
            [FileAttachConfigLoader.ConfigPathVariable] = path,
        };

        public string this[string key]
        {
            set => _vars[key] = value;
        }

        public string? GetVariable(string name) =>
            _vars.TryGetValue(name, out var value) ? value : null;

        public string UserHome => "/tmp/h45-home";

        public string? AppData => null;

        public bool IsWindows => false;

        public bool IsMacOs => true;
    }

    private sealed class MemoryScrollback : IScrollbackHistoryFiles
    {
        public string LastPath { get; private set; } = "";

        public string WriteUnique(string text)
        {
            LastPath = "/tmp/hypa-h45-scrollback.txt";
            return LastPath;
        }

        public void TryDelete(string path) => LastPath = path ?? LastPath;
    }
}
