using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Pty;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A live <see cref="ControlPlaneService"/> with one pane and attach
/// connections that offer or do not offer
/// <see cref="AttachEndpointProtocol.EchoModeCapability"/>. The pane runs on
/// the real hypa-pty-host helper, which reports the termios ECHO flag, or on
/// process-io, which reports no mode.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class EchoModeLiveTests
{
    private readonly string? _lib = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public async Task Cells_body_with_the_output_after_stty_echo_off_has_echo_false()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var helper = GhosttyTestRequire.RequirePtyHostHelper(PtyHostSupervisor.ResolveHelperPath());
        await using var session = await OpenAsync(
            HelperFactory(helper),
            "printf BEFORE-MARK; read x; stty -echo; printf AFTER-MARK; read y",
            ("echo-client", true));
        var client = session.Clients[0];

        var before = await session.WaitForBodyAsync(client, 0, "BEFORE-MARK");
        Assert.True(EchoOf(before), "the body before stty -echo has echo=true: " + before.GetRawText());

        var mark = client.Sink.Count;
        await session.TypeAsync(client, "x\n");
        var after = await session.WaitForBodyAsync(client, mark, "AFTER-MARK");
        Assert.False(EchoOf(after), "the body with the output after stty -echo has echo=false: " + after.GetRawText());
        // stty -echo keeps canonical mode: a password prompt, not a line editor.
        Assert.True(after.GetProperty("canonical").GetBoolean(), after.GetRawText());
    }

    [SkippableFact]
    public async Task Silent_stty_echo_off_posts_a_body_with_echo_false_at_the_next_key()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var helper = GhosttyTestRequire.RequirePtyHostHelper(PtyHostSupervisor.ResolveHelperPath());
        var dir = Path.Combine(Path.GetTempPath(), "hypa-echo-silent-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        var start = Path.Combine(dir, "start");
        var done = Path.Combine(dir, "done");
        try
        {
            // The child waits for a file, not for a key: no output and no
            // input may come between the change and the key below.
            await using var session = await OpenAsync(
                HelperFactory(helper),
                $"printf READY-MARK; while [ ! -e '{start}' ]; do sleep 0.02; done; "
                + $"stty -echo; : > '{done}'; read x; read y",
                ("echo-client", true));
            var client = session.Clients[0];
            var ready = await session.WaitForBodyAsync(client, 0, "READY-MARK");
            Assert.True(EchoOf(ready));

            await File.WriteAllTextAsync(start, "");
            await Poll.UntilAsync(() => File.Exists(done), "the child turned echo off");
            var mark = client.Sink.Count;

            // The key is not echoed, and the child prints nothing. The helper
            // finds the mode at the input write. The mux paints the change.
            // The test does not flush a paint here: the mux must schedule it.
            await session.TypeAsync(client, "s");
            var body = await session.WaitForBodyAsync(
                client,
                mark,
                body => body.TryGetProperty("echo", out var echo) && echo.ValueKind == JsonValueKind.False,
                "a body with echo=false",
                flush: false);
            Assert.DoesNotContain("s", RowText(body), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Peer_without_echo_mode_gets_no_echo_field()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var helper = GhosttyTestRequire.RequirePtyHostHelper(PtyHostSupervisor.ResolveHelperPath());
        await using var session = await OpenAsync(
            HelperFactory(helper),
            "printf FIRST-MARK; read x; stty -echo; printf SECOND-MARK; read y",
            ("echo-client", true),
            ("plain-client", false));
        var (echoClient, plain) = (session.Clients[0], session.Clients[1]);
        Assert.True(session.Cp.EchoModeOn(echoClient.Sink.ConnectionId));
        Assert.False(session.Cp.EchoModeOn(plain.Sink.ConnectionId));
        Assert.Contains(AttachEndpointProtocol.EchoModeCapability, echoClient.Welcome.Capabilities);

        var first = await session.WaitForBodyAsync(echoClient, 0, "FIRST-MARK");
        Assert.True(EchoOf(first));
        await session.WaitForBodyAsync(plain, 0, "FIRST-MARK");
        await session.TypeAsync(echoClient, "x\n");
        var second = await session.WaitForBodyAsync(echoClient, 0, "SECOND-MARK");
        Assert.False(EchoOf(second));
        await session.WaitForBodyAsync(plain, 0, "SECOND-MARK");

        // The same frames went to both peers. Only the peer that negotiated
        // the capability has the field.
        Assert.All(session.CellsOf(plain, 0), body => Assert.False(body.TryGetProperty("echo", out _), body.GetRawText()));
    }

    [SkippableFact]
    public async Task Pty_without_a_mode_source_gives_unknown_mode_and_no_echo_field()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync(
            TestPaneFactories.ProcessIo(),
            "printf OLD-MARK; read x",
            ("echo-client", true));
        var client = session.Clients[0];
        Assert.True(session.Cp.EchoModeOn(client.Sink.ConnectionId));

        await session.WaitForBodyAsync(client, 0, "OLD-MARK");
        Assert.Null(session.Runtime.TtyMode);
        Assert.All(session.CellsOf(client, 0), body => Assert.False(body.TryGetProperty("echo", out _), body.GetRawText()));
    }

    private static IPtyProcessFactory HelperFactory(string helper) =>
        new PtyProcessFactory(new PtyProviderOptions
        {
            Provider = PtyProviderKind.HypaPtyHost,
            HelperPath = helper,
        });

    private static bool EchoOf(JsonElement body)
    {
        Assert.True(body.TryGetProperty("echo", out var echo), "no echo field: " + body.GetRawText());
        return echo.GetBoolean();
    }

    private static string RowText(JsonElement body)
    {
        if (!body.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return "";
        var text = new StringBuilder();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String)
                text.Append(t.GetString());
        }

        return text.ToString();
    }

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement;

    private static AttachEndpointHello Hello(string clientId, bool offerEcho) =>
        new()
        {
            EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
            ProtocolMajor = (uint)ProtocolVersion.Major,
            ProtocolMinor = (uint)ProtocolVersion.Minor,
            ClientId = clientId,
            Geometry = new AttachGeometry
            {
                Columns = 80,
                Rows = 24,
                CellWidthPx = 8,
                CellHeightPx = 16,
                GeometryRevision = 1,
            },
            SurfaceActive = false,
            SnapshotCodecs = [AttachEndpointProtocol.SnapshotCodec],
            SurfaceCodecs = [AttachEndpointProtocol.SurfaceCodec],
            InputCodecs = [AttachEndpointProtocol.InputCodec],
            RequiredCapabilities = AttachEndpointProtocol.RequiredCapabilities.ToArray(),
            OptionalCapabilities = offerEcho ? [AttachEndpointProtocol.EchoModeCapability] : null,
        };

    private async Task<Session> OpenAsync(
        IPtyProcessFactory ptyFactory,
        string script,
        params (string ClientId, bool OfferEcho)[] clients)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-echo-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new RuntimeStatePaths { StateDirectory = dir };
        var migrator = new SqliteRuntimeSchemaMigrator(paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("echo-mode"));
        state.UpdateSession(s => s with { Name = "echo-mode", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(dir, label: "default");
        var store = new SqliteRuntimeSessionStore(paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(paths);
        var journal = new FileRuntimeEventJournal(paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var factory = new PaneRuntimeFactory(
            new PaneIntelligencePipeline(),
            ptyFactory: ptyFactory,
            vtEngineFactory: new VtEngineFactory(new VtProviderSelection
            {
                LibraryPathOverride = _lib,
            }));
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub());
        var session = new Session(dir, paths, cp);
        try
        {
            var create = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                Json(new JsonObject
                {
                    ["command"] = "/bin/sh",
                    ["args"] = new JsonArray("-c", script),
                    ["cwd"] = dir,
                }),
                CancellationToken.None);
            session.PaneId = create.GetProperty("pane_id").GetString()!;
            session.Runtime = Assert.IsType<PaneRuntime>(cp.PeekRuntime(session.PaneId));

            foreach (var (clientId, offerEcho) in clients)
            {
                var sink = new LaneSink(clientId);
                var welcome = await cp.HandleAttachHelloAsync(Hello(clientId, offerEcho), sink, CancellationToken.None);
                var client = new Client(clientId, sink)
                {
                    Welcome = welcome.Deserialize(ProtocolJsonContext.Default.AttachEndpointWelcome)!,
                };
                session.Clients.Add(client);
                await cp.HandleAttachSurfaceInterestAsync(
                    new AttachSurfaceInterestRequest
                    {
                        RequestId = "on",
                        ClientId = clientId,
                        Active = true,
                        GeometryRevision = 1,
                    },
                    sink,
                    CancellationToken.None);
                await session.ObserveAsync(client);
            }

            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private sealed record Client(string ClientId, LaneSink Sink)
    {
        public AttachEndpointWelcome Welcome { get; init; } = null!;

        public string? Lease { get; set; }
    }

    private sealed class Session(string dir, RuntimeStatePaths paths, ControlPlaneService cp) : IAsyncDisposable
    {
        public ControlPlaneService Cp { get; } = cp;

        public List<Client> Clients { get; } = [];

        public string PaneId { get; set; } = "";

        public PaneRuntime Runtime { get; set; } = null!;

        public Task<JsonElement> WaitForBodyAsync(Client client, int mark, string text) =>
            WaitForBodyAsync(
                client,
                mark,
                body => RowText(body).Contains(text, StringComparison.Ordinal),
                "a body with " + text);

        /// <summary>Paints the pane until a cells body after the mark matches. Returns the first match.</summary>
        public async Task<JsonElement> WaitForBodyAsync(
            Client client,
            int mark,
            Func<JsonElement, bool> match,
            string what,
            bool flush = true)
        {
            JsonElement found = default;
            await Poll.UntilAsync(
                async () =>
                {
                    foreach (var body in CellsOf(client, mark))
                    {
                        if (match(body))
                        {
                            found = body;
                            return true;
                        }
                    }

                    if (flush)
                    {
                        await Poll.GuardAsync(Cp.FlushCoalescedPaintForTestsAsync(PaneId), "paint flush");
                        await Cp.WaitOutputDeliveredForTestsAsync(PaneId);
                    }

                    return false;
                },
                what + " for " + client.ClientId);
            return found;
        }

        public async Task TypeAsync(Client client, string text)
        {
            if (client.Lease is null)
            {
                var claimed = await Cp.DispatchAsync(
                    ProtocolMethods.RuntimeLeaseClaim,
                    Json(new JsonObject
                    {
                        ["pane_id"] = PaneId,
                        ["scope"] = "input",
                        ["ttl_ms"] = 60_000,
                        ["takeover"] = false,
                    }),
                    client.Sink,
                    CancellationToken.None);
                Assert.Equal("granted", claimed.GetProperty("outcome").GetString());
                client.Lease = claimed.GetProperty("lease_id").GetString()!;
            }

            await Cp.DispatchAsync(
                ProtocolMethods.PaneSendKeys,
                Json(new JsonObject
                {
                    ["pane_id"] = PaneId,
                    ["lease_id"] = client.Lease,
                    ["encoding"] = "utf8",
                    ["data"] = text,
                }),
                client.Sink,
                CancellationToken.None);
        }

        public List<JsonElement> CellsOf(Client client, int lineMark)
        {
            var list = new List<JsonElement>();
            foreach (var line in client.Sink.Lines.Skip(lineMark))
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("params", out var parms)
                    || !parms.TryGetProperty("type", out var type)
                    || type.GetString() != ProtocolEventTypes.TerminalRender
                    || !parms.TryGetProperty("payload", out var payload)
                    || payload.ValueKind != JsonValueKind.Object
                    || !payload.TryGetProperty("kind", out var kind)
                    || kind.GetString() != TerminalRenderCellsPayload.KindCells
                    || !payload.TryGetProperty("pane_id", out var pane)
                    || pane.GetString() != PaneId
                    || (payload.TryGetProperty("ack_only", out var ackOnly) && ackOnly.GetBoolean()))
                {
                    continue;
                }

                list.Add(payload.Clone());
            }

            return list;
        }

        public async Task ObserveAsync(Client client)
        {
            var sub = await Cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                Json(new JsonObject
                {
                    ["from_seq"] = 0,
                    ["types"] = new JsonArray("output", "render"),
                    ["live"] = true,
                }),
                client.Sink,
                CancellationToken.None);
            var subId = sub.GetProperty("subscription_id").GetString()!;
            await Cp.CompleteEventsSubscribeAsync(subId, [], client.Sink, CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalVisibleSet,
                Json(new JsonObject
                {
                    ["subscription_id"] = subId,
                    ["pane_ids"] = new JsonArray(PaneId),
                }),
                client.Sink,
                CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                Json(new JsonObject
                {
                    ["pane_id"] = PaneId,
                    ["subscription_id"] = subId,
                }),
                client.Sink,
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Cp.ShutdownAsync(CancellationToken.None);
            SqliteTestCleanup.ReleaseAndDelete(dir, paths.DatabasePath);
        }
    }

    private sealed class LaneSink(string id) : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public string ConnectionId { get; } = id;

        public bool UsesWriterLanes => true;

        public int MaxLineBytes => 1_048_576;

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                    return [.. _lines];
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                    return _lines.Count;
            }
        }

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            Add(jsonLine);
            return Task.CompletedTask;
        }

        public WriterLaneResult TryEnqueueRender(string jsonLine) => Add(jsonLine);

        public WriterLaneResult TryEnqueueRender(byte[] utf8Json) => Add(Encoding.UTF8.GetString(utf8Json));

        public WriterLaneResult EnqueueOrderedRender(string jsonLine) => Add(jsonLine);

        public WriterLaneResult EnqueueOrderedRender(byte[] utf8Json) => Add(Encoding.UTF8.GetString(utf8Json));

        private WriterLaneResult Add(string json)
        {
            lock (_gate)
                _lines.Add(json);
            return WriterLaneResult.Ok;
        }
    }
}
