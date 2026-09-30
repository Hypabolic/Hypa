using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Worktrees;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class PaneLinkActivateTests : IDisposable
{
    private readonly string _root;
    private readonly MemoryPluginFiles _files;
    private readonly RecordingLauncher _launcher;
    private readonly FocusedSessionContextSource _context = new();
    private readonly PluginHostService _host;
    private readonly HyperlinkPaneFactory _panes = new();

    public PaneLinkActivateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-pane-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _files = new MemoryPluginFiles();
        var paths = new SystemPluginPathRoots(_root);
        _launcher = new RecordingLauncher();
        _host = new PluginHostService(
            _files,
            new FixedClock(),
            paths,
            new PluginManifestParser(),
            new FilePluginRegistry(_files, paths),
            _launcher,
            _context);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Activate_resolves_url_from_live_cell_and_starts_action()
    {
        var dir = WritePlugin("example.links", LinkManifest());
        Assert.True(_host.Link(dir, true).IsOk);
        var (cp, paneId) = await CreateSessionAsync();
        try
        {
            _panes.Get(paneId)!.Url = "https://github.com/org/repo/issues/1";
            var result = await cp.DispatchAsync(
                ProtocolMethods.PaneLinkActivate,
                JsonDocument.Parse(
                    $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"viewport_row":1,"col":2}""")
                    .RootElement,
                CancellationToken.None);
            Assert.True(result.GetProperty("handled").GetBoolean());
            Assert.Equal("https://github.com/org/repo/issues/1", result.GetProperty("url").GetString());
            var start = Assert.Single(_launcher.Starts);
            Assert.Equal("https://github.com/org/repo/issues/1", start.Environment[PluginEnv.ClickedUrl]);
            Assert.Equal("github-issue", start.Environment[PluginEnv.LinkHandlerId]);
            Assert.Contains("\"invocation_source\":\"link_click\"", start.Environment[PluginEnv.ContextJson], StringComparison.Ordinal);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Link_context_includes_clicked_pane_workspace_worktree()
    {
        var dir = WritePlugin("example.wt-click", LinkManifest());
        Assert.True(_host.Link(dir, true).IsOk);
        var app = new AppState(SessionId.New("pane-link-wt"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            _panes,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            plugins: _host);
        try
        {
            var created = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(
                    $$"""{"cwd":{{JsonSerializer.Serialize(Path.GetTempPath())}},"command":"/bin/true","create_pane":true}""")
                    .RootElement,
                CancellationToken.None);
            var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(paneId));
            var pane = app.GetPane(new PaneId(paneId!));
            Assert.NotNull(pane);
            app.SetWorktreeMembership(pane.WorkspaceId, new WorktreeSpaceMembership
            {
                Key = "key-click",
                Label = "repo-click",
                RepoRoot = "/repo/click",
                CheckoutPath = "/repo/click/wt",
                IsLinkedWorktree = true,
            });
            _panes.Get(paneId)!.Url = "https://github.com/org/repo/issues/1";
            var result = await cp.DispatchAsync(
                ProtocolMethods.PaneLinkActivate,
                JsonDocument.Parse(
                    $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"viewport_row":1,"col":2}""")
                    .RootElement,
                CancellationToken.None);
            Assert.True(result.GetProperty("handled").GetBoolean());
            using var doc = JsonDocument.Parse(_launcher.Starts[0].Environment[PluginEnv.ContextJson]);
            var nested = doc.RootElement.GetProperty("worktree");
            Assert.Equal("key-click", nested.GetProperty("repo_key").GetString());
            Assert.Equal("repo-click", nested.GetProperty("repo_name").GetString());
            Assert.Equal("/repo/click", nested.GetProperty("repo_root").GetString());
            Assert.Equal("/repo/click/wt", nested.GetProperty("checkout_path").GetString());
            Assert.True(nested.GetProperty("is_linked_worktree").GetBoolean());
            Assert.False(doc.RootElement.TryGetProperty("selected_text", out _));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stale_generation_starts_no_process()
    {
        var dir = WritePlugin("example.stale-gen", LinkManifest());
        Assert.True(_host.Link(dir, true).IsOk);
        var (cp, paneId) = await CreateSessionAsync();
        try
        {
            _panes.Get(paneId)!.Url = "https://github.com/org/repo/issues/1";
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneLinkActivate,
                    JsonDocument.Parse(
                        $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"viewport_row":1,"col":2,"generation":5}""")
                        .RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
            Assert.Empty(_launcher.Starts);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stale_viewport_starts_no_process()
    {
        var dir = WritePlugin("example.stale-view", LinkManifest());
        Assert.True(_host.Link(dir, true).IsOk);
        var (cp, paneId) = await CreateSessionAsync();
        try
        {
            _panes.Get(paneId)!.Url = "https://github.com/org/repo/issues/1";
            _panes.Get(paneId)!.Offset = 3;
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneLinkActivate,
                    JsonDocument.Parse(
                        $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"viewport_row":1,"col":2,"offset_from_bottom":0}""")
                        .RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
            Assert.Empty(_launcher.Starts);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stale_feed_generation_starts_no_process()
    {
        var dir = WritePlugin("example.stale-feed", LinkManifest());
        Assert.True(_host.Link(dir, true).IsOk);
        var (cp, paneId) = await CreateSessionAsync();
        try
        {
            var runtime = _panes.Get(paneId)!;
            runtime.Url = "https://github.com/org/repo/issues/1";
            runtime.OnCapture = () => runtime.BumpFeedGeneration();
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneLinkActivate,
                    JsonDocument.Parse(
                        $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"viewport_row":1,"col":2}""")
                        .RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
            Assert.Empty(_launcher.Starts);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Occupant_change_during_activation_starts_no_process()
    {
        var dir = WritePlugin("example.stale-occupant", LinkManifest());
        Assert.True(_host.Link(dir, true).IsOk);
        var (cp, paneId) = await CreateSessionAsync();
        try
        {
            var runtime = _panes.Get(paneId)!;
            runtime.Url = "https://github.com/org/repo/issues/1";
            runtime.OnCapture = () => cp.ForceOccupantGenerationForTests(paneId, 99);
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneLinkActivate,
                    JsonDocument.Parse(
                        $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"viewport_row":1,"col":2}""")
                        .RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
            Assert.Empty(_launcher.Starts);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Link_context_uses_clicked_pane_not_focus_session()
    {
        var dir = WritePlugin("example.link-focus-a", LinkManifest());
        Assert.True(_host.Link(dir, true).IsOk);
        var (cp, paneA, paneB) = await CreateSplitSessionAsync();
        try
        {
            await ReportSessionAsync(cp, paneA, "sess-focus-a");
            _panes.Get(paneB)!.Url = "https://github.com/org/repo/issues/1";
            await FocusPaneAsync(cp, paneA);
            _context.FocusedPaneId = paneA;
            _context.Session = new NativeAgentSessionRef
            {
                Kind = NativeAgentSessionRef.KindId,
                Value = "sess-focus-a",
                Source = "plugin:claude",
                Agent = "claude",
            };

            await cp.DispatchAsync(
                ProtocolMethods.PaneLinkActivate,
                JsonDocument.Parse(
                    $$"""{"pane_id":{{JsonSerializer.Serialize(paneB)}},"viewport_row":1,"col":2}""")
                    .RootElement,
                CancellationToken.None);

            var start = Assert.Single(_launcher.Starts);
            using var doc = JsonDocument.Parse(start.Environment[PluginEnv.ContextJson]);
            var context = doc.RootElement;
            Assert.Equal(paneB, context.GetProperty("focused_pane_id").GetString());
            Assert.False(context.TryGetProperty("agent_session", out _));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Link_context_carries_clicked_pane_session()
    {
        var dir = WritePlugin("example.link-focus-b", LinkManifest());
        Assert.True(_host.Link(dir, true).IsOk);
        var (cp, paneA, paneB) = await CreateSplitSessionAsync();
        try
        {
            await ReportSessionAsync(cp, paneB, "sess-click-b");
            _panes.Get(paneB)!.Url = "https://github.com/org/repo/issues/1";
            await FocusPaneAsync(cp, paneA);
            _context.FocusedPaneId = paneA;
            _context.Session = new NativeAgentSessionRef
            {
                Kind = NativeAgentSessionRef.KindId,
                Value = "sess-focus-a",
                Source = "plugin:claude",
                Agent = "claude",
            };

            await cp.DispatchAsync(
                ProtocolMethods.PaneLinkActivate,
                JsonDocument.Parse(
                    $$"""{"pane_id":{{JsonSerializer.Serialize(paneB)}},"viewport_row":1,"col":2}""")
                    .RootElement,
                CancellationToken.None);

            var start = Assert.Single(_launcher.Starts);
            using var doc = JsonDocument.Parse(start.Environment[PluginEnv.ContextJson]);
            var context = doc.RootElement;
            Assert.Equal(paneB, context.GetProperty("focused_pane_id").GetString());
            var session = context.GetProperty("agent_session");
            Assert.Equal("id", session.GetProperty("kind").GetString());
            Assert.Equal("sess-click-b", session.GetProperty("value").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Link_activate_records_delivery_target_for_send_text()
    {
        var dir = WritePlugin("example.link-send", LinkManifest() + """

            [grants]
            request = ["pane.send_text:self"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var (cp, paneId) = await CreateSessionAsync();
        try
        {
            _panes.Get(paneId)!.Url = "https://github.com/org/repo/issues/1";
            await cp.DispatchAsync(
                ProtocolMethods.PaneLinkActivate,
                JsonDocument.Parse(
                    $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"viewport_row":1,"col":2}""")
                    .RootElement,
                CancellationToken.None);
            Assert.True(_host.CanDeliverText("example.link-send", paneId));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Plugin_token_cannot_call_pane_link_activate()
    {
        var dir = WritePlugin("example.token-link", LinkManifest() + """

            [grants]
            request = ["action.invoke:self"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.token-link");
        var (cp, paneId) = await CreateSessionAsync();
        try
        {
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneLinkActivate,
                    JsonDocument.Parse(
                        $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"viewport_row":1,"col":2}""")
                        .RootElement,
                    connection: null,
                    grantToken: token,
                    pluginConnection: true,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.CapabilityInvalid, ex.Code);
            Assert.Empty(_launcher.Starts);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private async Task<(ControlPlaneService Service, string PaneId)> CreateSessionAsync()
    {
        var (cp, paneA, _) = await CreateSplitSessionAsync();
        return (cp, paneA);
    }

    private async Task<(ControlPlaneService Service, string PaneA, string PaneB)> CreateSplitSessionAsync()
    {
        var app = new AppState(SessionId.New("pane-link"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            _panes,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            plugins: _host);
        var created = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse(
                $$"""{"cwd":{{JsonSerializer.Serialize(Path.GetTempPath())}},"command":"/bin/true","create_pane":true}""")
                .RootElement,
            CancellationToken.None);
        var paneA = created.GetProperty("pane").GetProperty("pane_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(paneA));
        var split = await cp.DispatchAsync(
            ProtocolMethods.PaneSplit,
            JsonDocument.Parse(
                $$"""{"pane_id":{{JsonSerializer.Serialize(paneA)}},"direction":"right","command":"/bin/true"}""")
                .RootElement,
            CancellationToken.None);
        var paneB = split.GetProperty("pane_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(paneB));
        return (cp, paneA!, paneB!);
    }

    private static async Task FocusPaneAsync(ControlPlaneService cp, string paneId) =>
        await cp.DispatchAsync(
            ProtocolMethods.PaneFocus,
            JsonDocument.Parse($$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}}}""").RootElement,
            CancellationToken.None);

    private static async Task ReportSessionAsync(ControlPlaneService cp, string paneId, string sessionId)
    {
        await cp.DispatchAsync(
            ProtocolMethods.PaneReportAgent,
            JsonDocument.Parse(
                $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"source":"plugin:claude","agent":"claude","state":"working","seq":1}""")
                .RootElement,
            CancellationToken.None);
        await cp.DispatchAsync(
            ProtocolMethods.PaneReportAgentSession,
            JsonDocument.Parse(
                $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"source":"plugin:claude","agent":"claude","agent_session_id":{{JsonSerializer.Serialize(sessionId)}},"session_start_source":"startup","seq":2}""")
                .RootElement,
            CancellationToken.None);
    }

    private string WritePlugin(string id, string extra)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, PluginHostService.ManifestFileName);
        _files.WriteAllText(path, $"""
            id = "{id}"
            name = "Test {id}"
            version = "0.1.0"
            min_hypa_version = "0.1.0"
            platforms = ["linux", "macos", "windows"]
            {extra}
            """);
        return dir;
    }

    private static string LinkManifest() =>
        """

        [[actions]]
        id = "open"
        title = "Open"
        command = ["/bin/echo", "ok"]

        [[link_handlers]]
        id = "github-issue"
        title = "Open GitHub issue"
        pattern = "^https://github\\.com/"
        action = "open"
        """;

    private sealed class FocusedSessionContextSource : IPluginContextSource
    {
        public string? FocusedPaneId { get; set; }

        public NativeAgentSessionRef? Session { get; set; }

        public PluginInvocationContext Current(string correlationId) =>
            new()
            {
                CorrelationId = correlationId,
                InvocationSource = "api",
                FocusedPaneId = FocusedPaneId,
                AgentSession = Session,
                SelectedText = null,
            };

        public PluginInvocationContext ForEvent(string hookName, string eventJson, string correlationId)
        {
            _ = eventJson;
            return Current(correlationId) with { InvocationSource = hookName };
        }
    }

    private sealed class FixedClock : IPluginClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UnixEpoch.AddDays(1);
    }

    private sealed class RecordingLauncher : IPluginProcessLauncher
    {
        public List<Launch> Starts { get; } = [];

        public bool TryStart(
            string program,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            int outputCapBytes,
            Action<PluginProcessExit> onExit,
            out string? error)
        {
            error = null;
            Starts.Add(new Launch(program, arguments.ToArray(), workingDirectory, new Dictionary<string, string>(environment)));
            onExit(new PluginProcessExit(0, "", "", null));
            return true;
        }

        public sealed record Launch(
            string Program,
            IReadOnlyList<string> Arguments,
            string Cwd,
            IReadOnlyDictionary<string, string> Environment);
    }

    private sealed class MemoryPluginFiles : IPluginFiles
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);

        public bool FileExists(string path) => _files.ContainsKey(Path.GetFullPath(path));

        public bool DirectoryExists(string path) => _dirs.Contains(Path.GetFullPath(path));

        public string ReadAllText(string path) => _files[Path.GetFullPath(path)];

        public void WriteAllText(string path, string contents)
        {
            var full = Path.GetFullPath(path);
            _files[full] = contents;
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
                _dirs.Add(parent);
        }

        public void CreateDirectory(string path) => _dirs.Add(Path.GetFullPath(path));

        public bool DeleteFile(string path) => _files.Remove(Path.GetFullPath(path));

        public bool DeleteDirectory(string path)
        {
            var full = Path.GetFullPath(path);
            var prefix = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var removed = _dirs.Remove(full);
            var nestedDirs = _dirs.Where(d => d.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            foreach (var dir in nestedDirs)
            {
                _dirs.Remove(dir);
                removed = true;
            }

            var nestedFiles = _files.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal) || key == full)
                .ToArray();
            foreach (var file in nestedFiles)
            {
                _files.Remove(file);
                removed = true;
            }

            return removed;
        }

        public string GetFullPath(string path) => Path.GetFullPath(path);
    }

    private sealed class HyperlinkPaneFactory : IPaneRuntimeFactory
    {
        private readonly Dictionary<string, HyperlinkPaneRuntime> _runtimes = new(StringComparer.Ordinal);

        public HyperlinkPaneRuntime? Get(string paneId) =>
            _runtimes.TryGetValue(paneId, out var runtime) ? runtime : null;

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            var runtime = new HyperlinkPaneRuntime(options.Id);
            _runtimes[options.Id.Value] = runtime;
            return runtime;
        }
    }

    private sealed class HyperlinkPaneRuntime(PaneId id) : IPaneRuntime, IPaneVtSnapshot
    {
        private long _feedGeneration = 1;

        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; }
        public int? Pid { get; } = 42_193;
        public string? Url { get; set; }
        public int Offset { get; set; }
        public int MaxOffset { get; set; }
        public Action? OnCapture { get; set; }
        public long FeedGeneration => Volatile.Read(ref _feedGeneration);

        public void BumpFeedGeneration() => Interlocked.Increment(ref _feedGeneration);

        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
#pragma warning disable CS0067
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct)
        {
            IsAlive = true;
            return Task.CompletedTask;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            OutputReceived?.Invoke(this, data);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => "";
        public string ReadDetectionText() => "";

        public bool TryCaptureSnapshotJson(out string json, out long feedGeneration)
        {
            json = "{}";
            feedGeneration = FeedGeneration;
            return true;
        }

        public bool TryCaptureAttachFrame(out VtFrame? frame, out long feedGeneration)
        {
            feedGeneration = FeedGeneration;
            OnCapture?.Invoke();
            var link = string.IsNullOrEmpty(Url)
                ? VtCellView.Blank
                : new VtCellView("x", 1, false, null, null, false, false, false, false, false, false, false, Hyperlink: Url);
            var blank = VtCellView.Blank;
            var rows = new[]
            {
                new[] { blank, blank, blank },
                new[] { blank, blank, link },
            };
            frame = new VtFrame(
                Id.Value,
                3,
                2,
                rows,
                new VtFrameCursor(0, 0, true, 0),
                new VtFrameModes(false, true, false, false, "", false),
                0,
                0,
                0);
            return true;
        }

        public bool TryCaptureLivePaintFrame(
            out VtFrame? frame,
            out long feedGeneration,
            out PaneVtPaintKind kind)
        {
            kind = PaneVtPaintKind.Full;
            return TryCaptureAttachFrame(out frame, out feedGeneration);
        }

        public bool TryGetScrollMetrics(out int offset, out int maxOffset)
        {
            offset = Offset;
            maxOffset = MaxOffset;
            return true;
        }

        public bool TryGetScrollOrigin(out int offset)
        {
            offset = Offset;
            return true;
        }

        public bool TrySetScrollOrigin(int offset)
        {
            Offset = offset;
            return true;
        }

        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }
}
