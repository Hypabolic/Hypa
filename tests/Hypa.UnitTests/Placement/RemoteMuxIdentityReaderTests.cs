using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime;
using Hypa.ControlPlane;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Xunit;

namespace Hypa.UnitTests.Placement;

public sealed class RemoteMuxIdentityReaderTests
{
    [Fact]
    public async Task Missing_socket_returns_placement_outcome()
    {
        var reader = new ControlPlaneRemoteMuxIdentityReader();
        var missing = Path.Combine(Path.GetTempPath(), "hypa-missing-" + Guid.NewGuid().ToString("N"), "hypa.sock");
        var outcome = await reader.ReadAsync(missing, "agents");
        Assert.False(outcome.Ok);
        Assert.Equal(PlacementReasons.PreparationFailed, outcome.Reason);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Detail));
    }

    [SkippableFact]
    public async Task Reader_connects_then_reads_health_identity()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX socket fixture");
        var (dir, sock) = NewPrivateSocketPath();
        try
        {
            await using var mux = await LiveControlPlane.StartAsync(sock, "agents");
            var reader = new ControlPlaneRemoteMuxIdentityReader();
            var outcome = await reader.ReadAsync(sock, "agents");
            Assert.True(outcome.Ok, outcome.Detail);
            Assert.Equal("mux_agents", outcome.Value.Value);
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    [Fact]
    public void Hyphen_session_maps_without_raw_hyphen_suffix()
    {
        Assert.True(RemoteMuxIdentityFormat.TryParse("lab-mux", "lab-mux", out var identity));
        Assert.StartsWith("mux_", identity.Value, StringComparison.Ordinal);
        Assert.DoesNotContain('-', identity.Value);
        Assert.True(MuxIdentity.TryFromSessionName("lab-mux", out var expected));
        Assert.Equal(expected, identity);
    }

    private static (string Dir, string Sock) NewPrivateSocketPath()
    {
        var dir = Path.Combine("/tmp", "hypa-id-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
#pragma warning disable CA1416
        File.SetUnixFileMode(
            dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        return (dir, Path.Combine(dir, "hypa.sock"));
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class LiveControlPlane : IAsyncDisposable
    {
        private readonly UnixSocketServer _server;

        private LiveControlPlane(ControlPlaneService controlPlane, UnixSocketServer server, string socketPath)
        {
            ControlPlane = controlPlane;
            _server = server;
            SocketPath = socketPath;
        }

        public ControlPlaneService ControlPlane { get; }

        public string SocketPath { get; }

        public static async Task<LiveControlPlane> StartAsync(string socketPath, string session)
        {
            var state = new AppState(SessionId.New(session));
            var cp = new ControlPlaneService(
                state,
                new UnusedPaneFactory(),
                new NullIntelligence(),
                new NullDetector(),
                leases: new InMemoryLeaseRegistry(),
                attachments: new InMemoryAttachmentRegistry());
            var server = new UnixSocketServer(cp, socketPath);
            await server.StartAsync(CancellationToken.None).ConfigureAwait(false);
            return new LiveControlPlane(cp, server, socketPath);
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            await ControlPlane.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class UnusedPaneFactory : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) =>
            throw new InvalidOperationException("unused pane factory");
    }

    private sealed class NullIntelligence : IIntelligencePipeline
    {
        public void OnPaneOutput(PaneId paneId, ReadOnlySpan<byte> data)
        {
        }

        public void OnPaneInput(PaneId paneId, string text)
        {
        }

        public string CompressForAgent(PaneId paneId, string rawText, string? commandHint = null) =>
            rawText;

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
