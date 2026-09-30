using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// PaneRuntime must deterministically dispose IVtEngine (native Ghostty terminals).
/// </summary>
[Collection("GhosttyPtyTests")]
public class PaneRuntimeVtDisposeTests
{
    private static readonly string? LibPath = ResolveNativeLibraryPath();

    [Fact]
    public async Task DisposeAsync_calls_IVtEngine_Dispose()
    {
        var spy = new DisposeCountingVtEngine(20, 6);
        await using (var runtime = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "/bin/true",
                Args = [],
                Cols = 20,
                Rows = 6,
            },
            spawnProcess: static (_, _) => throw new InvalidOperationException("not started"),
            vt: spy))
        {
            Assert.Equal(0, spy.DisposeCount);
        }

        Assert.Equal(1, spy.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_is_idempotent_for_vt_dispose()
    {
        var spy = new DisposeCountingVtEngine(20, 6);
        var runtime = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "/bin/true",
                Args = [],
                Cols = 20,
                Rows = 6,
            },
            spawnProcess: static (_, _) => throw new InvalidOperationException("not started"),
            vt: spy);

        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
        Assert.Equal(1, spy.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_releases_store_after_the_engine()
    {
        var order = new List<string>();
        var vt = new OrderRecordingVtEngine(order);
        var store = new OrderRecordingHistoryStore(order);
        var runtime = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "/bin/true",
                Args = [],
                Cols = 20,
                Rows = 6,
            },
            spawnProcess: static (_, _) => throw new InvalidOperationException("not started"),
            vt: vt,
            historyStore: store);
        await runtime.DisposeAsync();
        Assert.Equal(["vt", "store"], order);
        await runtime.DisposeAsync();
        Assert.Equal(["vt", "store"], order);
        Assert.Equal(1, vt.DisposeCount);
        Assert.Equal(1, store.DisposeCount);
    }

    [SkippableFact]
    public async Task DisposeAsync_frees_Ghostty_native_terminal()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);

        var vt = new GhosttyVtEngine(20, 6, maxScrollback: 100, libraryPathOverride: LibPath);
        await using (var runtime = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "/bin/true",
                Args = [],
                Cols = 20,
                Rows = 6,
            },
            spawnProcess: static (_, _) => throw new InvalidOperationException("not started"),
            vt: vt))
        {
            // Engine is live while pane owns it.
            var snap = runtime.CaptureVtSnapshot();
            Assert.Equal(GhosttyVtEngine.Provider, snap.Provider);
        }

        // After pane dispose, native terminal must be freed.
        Assert.ThrowsAny<ObjectDisposedException>(() => vt.CaptureSnapshot());
        Assert.ThrowsAny<ObjectDisposedException>(() => vt.Feed("x"u8));
    }

    [SkippableFact]
    public async Task Factory_open_close_many_Ghostty_panes_disposes_each_engine()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);

        var factory = TestPaneFactories.Create(
            vtEngineFactory: new VtEngineFactory(new VtProviderSelection
            {
                Provider = VtProviderKind.Ghostty,
                RequireGhostty = true,
                LibraryPathOverride = LibPath,
            }));

        // Pane churn must not leak native terminals until finalization.
        for (var i = 0; i < 32; i++)
        {
            var runtime = factory.Create(new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "/bin/true",
                Args = [],
                Cols = 40,
                Rows = 12,
            });
            Assert.IsType<PaneRuntime>(runtime);
            await runtime.DisposeAsync();
        }
    }

    /// <summary>Spy engine: counts Dispose while forwarding to Basic.</summary>
    private sealed class OrderRecordingVtEngine : IVtEngine
    {
        private readonly List<string> _order;
        private readonly StubVtEngine _inner;
        public int DisposeCount { get; private set; }

        public OrderRecordingVtEngine(List<string> order)
        {
            _order = order;
            _inner = new StubVtEngine(20, 6);
        }

        public int Cols => _inner.Cols;
        public int Rows => _inner.Rows;
        public int CursorCol => _inner.CursorCol;
        public int CursorRow => _inner.CursorRow;
        public void Resize(int cols, int rows) => _inner.Resize(cols, rows);
        public void Feed(ReadOnlySpan<byte> data) => _inner.Feed(data);
        public void Feed(ReadOnlySpan<char> text) => _inner.Feed(text);
        public int TakePendingBellCount() => _inner.TakePendingBellCount();
        public string GetVisibleText(bool trimTrailingWhitespace = true) =>
            _inner.GetVisibleText(trimTrailingWhitespace);
        public string GetRecentText(int maxLines, bool trimTrailingWhitespace = true) =>
            _inner.GetRecentText(maxLines, trimTrailingWhitespace);
        public string GetRecentUnwrappedText(int maxLines, bool trimTrailingWhitespace = true) =>
            _inner.GetRecentUnwrappedText(maxLines, trimTrailingWhitespace);
        public VtStructuredSnapshot CaptureSnapshot() => _inner.CaptureSnapshot();
        public void Reset() => _inner.Reset();
        public void Dispose()
        {
            if (DisposeCount > 0)
                return;
            DisposeCount++;
            _order.Add("vt");
            _inner.Dispose();
        }
    }

    private sealed class OrderRecordingHistoryStore : IPaneHistoryStore
    {
        private readonly List<string> _order;
        public int DisposeCount { get; private set; }

        public OrderRecordingHistoryStore(List<string> order) => _order = order;

        public int RowCount => 0;
        public long CompressedByteCount => 0;
        public long FirstRowIndex => 0;
        public long NextRowIndex => 0;
        public int Cols => 0;
        public void AppendRows(IReadOnlyList<VtFrameCell[]> rows, int cols, VtFrameTables tables) { }
        public void SealOpenBlock() { }
        public PaneHistoryResult<PaneHistoryRead> TryReadRows(long startIndex, int count) =>
            PaneHistoryResult<PaneHistoryRead>.Ok(new PaneHistoryRead([]));
        public void Dispose()
        {
            if (DisposeCount > 0)
                return;
            DisposeCount++;
            _order.Add("store");
        }
    }

    private sealed class DisposeCountingVtEngine : IVtEngine
    {
        private readonly StubVtEngine _inner;
        private int _disposeCount;

        public DisposeCountingVtEngine(int cols, int rows) =>
            _inner = new StubVtEngine(cols, rows);

        public int DisposeCount => _disposeCount;

        public int Cols => _inner.Cols;
        public int Rows => _inner.Rows;
        public int CursorCol => _inner.CursorCol;
        public int CursorRow => _inner.CursorRow;

        public void Resize(int cols, int rows) => _inner.Resize(cols, rows);
        public void Feed(ReadOnlySpan<byte> data) => _inner.Feed(data);
        public void Feed(ReadOnlySpan<char> text) => _inner.Feed(text);
        public int TakePendingBellCount() => _inner.TakePendingBellCount();
        public string GetVisibleText(bool trimTrailingWhitespace = true) =>
            _inner.GetVisibleText(trimTrailingWhitespace);
        public string GetRecentText(int maxLines, bool trimTrailingWhitespace = true) =>
            _inner.GetRecentText(maxLines, trimTrailingWhitespace);
        public string GetRecentUnwrappedText(int maxLines, bool trimTrailingWhitespace = true) =>
            _inner.GetRecentUnwrappedText(maxLines, trimTrailingWhitespace);
        public VtStructuredSnapshot CaptureSnapshot() => _inner.CaptureSnapshot();
        public void Reset() => _inner.Reset();

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            _inner.Dispose();
        }
    }

    private static string? ResolveNativeLibraryPath() =>
        GhosttyTestRequire.TryResolveNativeLibraryPath();
}
