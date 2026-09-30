using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Overlay;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class AttachStartLeaseTests
{
    [SkippableFact]
    public async Task Fresh_attach_chrome_does_not_keep_a_lease_error()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix sockets only.");

        var dir = Path.Combine("/tmp", "hypa-as-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var socketPath = Path.Combine(dir, "s.sock");
        var state = new AppState(SessionId.New("attach-start"));
        state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            state,
            new StubPaneFactory(),
            new StubIntel(),
            new StubDetector());
        var server = new UnixSocketServer(cp, socketPath);
        await server.StartAsync(CancellationToken.None);
        try
        {
            var created = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                Params(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = true,
                }),
                CancellationToken.None);
            var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString();
            var snap = await cp.DispatchAsync(
                ProtocolMethods.SessionSnapshot, parameters: null, CancellationToken.None);
            var tabId = snap.GetProperty("focused_tab_id").GetString();
            var workspaceId = snap.GetProperty("focused_workspace_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(paneId));
            Assert.False(string.IsNullOrWhiteSpace(tabId));

            await using var client = new ControlPlaneClient(socketPath, connectTimeout: TimeSpan.FromSeconds(2));
            await client.ConnectAsync();
            var port = new ControlPlaneAttachCommandPort(client);
            var live = Live(port, workspaceId, tabId, paneId!);
            live.ResizeLease = "";
            live.InputLease = "";
            live.Dispatcher.ResizeLease = "";

            await AttachSession.StartLocalAttachChromeAsync(
                client, port, live, tty: null, CancellationToken.None);

            Assert.DoesNotContain(
                "Controller lease required",
                live.StatusError ?? "",
                StringComparison.Ordinal);
            Assert.Null(live.StatusError);

            await using var outsider = new ControlPlaneClient(socketPath, connectTimeout: TimeSpan.FromSeconds(2));
            await outsider.ConnectAsync();
            var rejected = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                outsider.CallAsync(
                    ProtocolMethods.PaneResize,
                    new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["cols"] = 40,
                        ["rows"] = 12,
                    }));
            Assert.Equal("Controller lease required", rejected.Message);
        }
        finally
        {
            await server.DisposeAsync();
            await cp.ShutdownAsync(CancellationToken.None);
            try { Directory.Delete(dir, recursive: true); }
            catch { /* tmp */ }
        }
    }

    [Fact]
    public async Task Lease_changed_grant_is_applied_as_audit()
    {
        var port = new SilentPort();
        var live = Live(port, "w1", "t1", "p1");
        live.InputLease = "lease-in";
        live.ResizeLease = "lease-r";
        using var ev = LeaseEvent("other-lease", "p1", LeaseStates.Granted);

        await AttachSession.HandleRenderEventAsync(
            ev.RootElement, port, new SnapshotAssembler(), tty: null, live, CancellationToken.None);

        Assert.Equal("lease-in", live.InputLease);
        Assert.Equal("lease-r", live.ResizeLease);
        var sink = Assert.IsType<CapturingProcessLogSink>(live.ProcessLog);
        Assert.Contains(sink.Records, record =>
            record.Event == ProcessLogEvents.AttachEventOutcome
            && record.EventType == ProtocolEventTypes.LeaseChanged
            && record.Outcome == ProcessLogEvents.OutcomeApplied
            && record.Reason == "audit");
        Assert.DoesNotContain(sink.Records, record =>
            record.Event == ProcessLogEvents.AttachEventOutcome
            && record.Reason == "unhandled");
    }

    [Fact]
    public async Task Lease_changed_release_drops_a_tracked_lease()
    {
        var port = new SilentPort();
        var live = Live(port, "w1", "t1", "p1");
        live.InputLease = "lease-in";
        live.ResizeLease = "lease-r";
        live.Renew.Track("lease-in");
        live.SiblingResizeLeases["p2"] = "sib-lease";
        using var ev = LeaseEvent("lease-in", "p1", LeaseStates.Released);

        await AttachSession.HandleRenderEventAsync(
            ev.RootElement, port, new SnapshotAssembler(), tty: null, live, CancellationToken.None);

        Assert.Equal("", live.InputLease);
        Assert.Equal("lease-r", live.ResizeLease);
        Assert.False(live.Renew.IsTracked("lease-in"));
        Assert.Equal("sib-lease", live.SiblingResizeLeases["p2"]);
        var sink = Assert.IsType<CapturingProcessLogSink>(live.ProcessLog);
        Assert.Contains(sink.Records, record =>
            record.Event == ProcessLogEvents.AttachEventOutcome
            && record.EventType == ProtocolEventTypes.LeaseChanged
            && record.Outcome == ProcessLogEvents.OutcomeApplied
            && record.Reason == "released");
    }

    [Fact]
    public async Task Release_of_one_overlay_lease_keeps_the_other_tracked()
    {
        var port = new SilentPort();
        var live = Live(port, "w1", "t1", "p1");
        live.OverlayLeases = new TargetPaneLeasePair
        {
            PaneId = "p9",
            InputLease = "ov-in",
            ResizeLease = "ov-r",
            InputNewlyGranted = true,
            ResizeNewlyGranted = true,
        };
        live.Renew.Track("ov-in");
        live.Renew.Track("ov-r");
        using var ev = LeaseEvent("ov-in", "p9", LeaseStates.Released);

        await AttachSession.HandleRenderEventAsync(
            ev.RootElement, port, new SnapshotAssembler(), tty: null, live, CancellationToken.None);

        Assert.False(live.Renew.IsTracked("ov-in"));
        Assert.True(live.Renew.IsTracked("ov-r"));
        var kept = Assert.IsType<TargetPaneLeasePair>(live.OverlayLeases);
        Assert.Equal("", kept.InputLease);
        Assert.Equal("ov-r", kept.ResizeLease);
        Assert.True(kept.ResizeNewlyGranted);
    }

    private static JsonElement Params(JsonObject obj)
    {
        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static JsonDocument LeaseEvent(string leaseId, string paneId, string state) =>
        JsonDocument.Parse(new JsonObject
        {
            ["event"] = ProtocolEventTypes.RuntimeEvent,
            ["params"] = new JsonObject
            {
                ["type"] = ProtocolEventTypes.LeaseChanged,
                ["payload"] = new JsonObject
                {
                    ["lease_id"] = leaseId,
                    ["pane_id"] = paneId,
                    ["scope"] = LeaseScopes.Input,
                    ["state"] = state,
                    ["holder_id"] = "conn",
                    ["actor_id"] = "conn",
                    ["reason_hash"] = "",
                    ["policy_decision"] = state,
                },
            },
        }.ToJsonString());

    private static AttachLiveState Live(
        IAttachCommandPort port,
        string? workspaceId,
        string? tabId,
        string paneId)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(port, workspaceId, tabId, paneId, ""),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = workspaceId,
            TabId = tabId,
            PaneId = paneId,
            AttachClientId = "cli_attach",
            ChromeEnabled = true,
            SessionName = "attach-start",
            ProcessLog = new CapturingProcessLogSink(),
        };
    }

    private sealed class SilentPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse("{}");
            return Task.FromResult(doc.RootElement.Clone());
        }
    }

    private sealed class StubPaneFactory : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) => new StubPane(options.Id);
    }

    private sealed class StubPane(PaneId id) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; private set; }
        public int? Pid { get; private set; } = 275;
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct)
        {
            IsAlive = true;
            return Task.CompletedTask;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;

        public string ReadVisibleText() => "";

        public string ReadRecentText(int maxLines) => "";

        public string ReadRecentUnwrappedText(int maxLines) => "";

        public string ReadDetectionText() => "";

        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubIntel : IIntelligencePipeline
    {
        public void OnPaneOutput(PaneId paneId, ReadOnlySpan<byte> data)
        {
        }

        public void OnPaneInput(PaneId paneId, string text)
        {
        }

        public string CompressForAgent(PaneId paneId, string rawText, string? commandHint = null) => rawText;

        public void BindAtomic(PaneId paneId, AtomicBinding binding)
        {
        }

        public void RemovePane(PaneId paneId)
        {
        }
    }

    private sealed class StubDetector : IAgentDetector
    {
        public DetectionResult Detect(string snapshotText, string? processName = null) => new();
    }
}
