using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Vt.Ghostty;

namespace Hypa.Continuity.Tests.RemoteTyping;

/// <summary>
/// A live mux in this process: a <see cref="ControlPlaneService"/> on the
/// real system clock, with its <see cref="UnixSocketServer"/>, real pane
/// runtimes and Ghostty VT. The pane children are the scripted stand-ins of
/// <see cref="ScriptedPaneFactory"/>.
/// </summary>
internal sealed class RemoteTypingMux : IAsyncDisposable
{
    private readonly string _dir;
    private readonly UnixSocketServer _server;

    private RemoteTypingMux(
        string dir,
        ControlPlaneService controlPlane,
        UnixSocketServer server,
        string socketPath,
        ScriptedPaneFactory panes)
    {
        _dir = dir;
        Panes = panes;
        ControlPlane = controlPlane;
        _server = server;
        SocketPath = socketPath;
    }

    public ControlPlaneService ControlPlane { get; }

    public ScriptedPaneFactory Panes { get; }

    public string SocketPath { get; }

    public static async Task<RemoteTypingMux> StartAsync(string ghosttyLibrary)
    {
        // A short private path: a Unix socket path has a small length limit.
        var dir = Path.Combine("/tmp", "hypa-rtb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
#pragma warning disable CA1416
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        var socketPath = Path.Combine(dir, "hypa.sock");
        var paths = new RuntimeStatePaths { StateDirectory = dir };
        var migrated = await new SqliteRuntimeSchemaMigrator(paths).MigrateAsync();
        if (!migrated.IsOk)
            throw new InvalidOperationException("the runtime schema did not migrate");
        var state = new AppState(SessionId.New("remote-typing"));
        state.UpdateSession(s => s with { Name = "remote-typing", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(dir, label: "default");
        var store = new SqliteRuntimeSessionStore(paths);
        var saved = await store.SaveAsync(state.Snapshot());
        if (!saved.IsOk)
            throw new InvalidOperationException("the session did not save: " + saved.Error);
        // The live mux appends each event to a file journal. The subscribe of
        // the attach needs a journal. The bench uses the production journal
        // sync on each host (F_FULLFSYNC on macOS), as a real mux does.
        var journal = new FileRuntimeEventJournal(
            paths,
            new SqliteJournalManifestStore(paths),
            state.SessionId.Value);
        var recovered = await journal.RecoverAsync();
        if (!recovered.IsOk)
            throw new InvalidOperationException("the event journal did not recover: " + recovered.Error);
        var panes = new ScriptedPaneFactory();
        var factory = new PaneRuntimeFactory(
            ptyFactory: panes,
            vtEngineFactory: new VtEngineFactory(new VtProviderSelection { LibraryPathOverride = ghosttyLibrary }));
        var cp = new ControlPlaneService(
            state,
            factory,
            new NullIntelligence(),
            new NullDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            leases: new InMemoryLeaseRegistry(),
            attachments: new InMemoryAttachmentRegistry());
        var server = new UnixSocketServer(cp, socketPath);
        await server.StartAsync(CancellationToken.None);
        return new RemoteTypingMux(dir, cp, server, socketPath, panes);
    }

    /// <summary>
    /// Creates a pane that runs <paramref name="command"/> and returns its id.
    /// The first pane fills the tab. A later pane splits <paramref name="splitFrom"/>,
    /// so the two panes share one visible tab.
    /// </summary>
    public async Task<string> CreatePaneAsync(string command, string? splitFrom = null)
    {
        var created = splitFrom is null
            ? await Dispatch(ProtocolMethods.PaneCreate, new JsonObject { ["command"] = command, ["cwd"] = _dir })
            : await Dispatch(
                ProtocolMethods.PaneSplit,
                new JsonObject { ["pane_id"] = splitFrom, ["direction"] = "right", ["command"] = command, ["cwd"] = _dir });
        return created.GetProperty("pane_id").GetString()!;
    }

    public Task ClosePaneAsync(string paneId) =>
        Dispatch(ProtocolMethods.PaneClose, new JsonObject { ["pane_id"] = paneId });

    private Task<JsonElement> Dispatch(string method, JsonObject parameters) =>
        ControlPlane.DispatchAsync(
            method,
            JsonDocument.Parse(parameters.ToJsonString()).RootElement,
            CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await ControlPlane.ShutdownAsync(CancellationToken.None);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class NullIntelligence : Hypa.AgentRuntime.Application.IIntelligencePipeline
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

    private sealed class NullDetector : IAgentDetector
    {
        public DetectionResult Detect(string snapshotText, string? processName = null) => new();
    }
}
