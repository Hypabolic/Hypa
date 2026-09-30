using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Ghostty is the only pane VT. Product source must not construct Basic.
/// Missing native fails closed.
/// </summary>
public class GhosttyOnlyVtTests
{

    [Fact]
    public void ParseProvider_rejects_basic_and_f1()
    {
        var basic = Assert.Throws<ArgumentException>(() => VtProviderSelection.ParseProvider("basic"));
        Assert.Contains("rejected", basic.Message, StringComparison.OrdinalIgnoreCase);
        var f1 = Assert.Throws<ArgumentException>(() => VtProviderSelection.ParseProvider("f1"));
        Assert.Contains("rejected", f1.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Factory_creates_one_engine_for_each_pane()
    {
        var counting = new CountingVtEngineFactory();
        var factory = TestPaneFactories.Create(vtEngineFactory: counting);
        await using var runtime = (PaneRuntime)factory.Create(new PaneSpawnOptions
        {
            Id = PaneId.New(),
            Cwd = Path.GetTempPath(),
            Command = "/bin/true",
            Cols = 20,
            Rows = 6,
        });
        Assert.Equal(1, counting.Created);
        Assert.Same(counting.Last, runtime.VtEngine);
    }

    [Fact]
    public void PaneRuntime_null_engine_throws()
    {
        var options = new PaneSpawnOptions
        {
            Id = PaneId.New(),
            Cwd = Path.GetTempPath(),
            Command = "/bin/true",
            Cols = 20,
            Rows = 6,
        };
        IVtEngine? missing = null;
        Assert.Throws<ArgumentNullException>(() => new PaneRuntime(options, missing!));
        Func<string, IReadOnlyList<string>, Hypa.Terminal.Pty.IPtyProcess> spawn =
            static (_, _) => throw new InvalidOperationException("no spawn");
        Assert.Throws<ArgumentNullException>(() => new PaneRuntime(options, spawn, missing!));
    }

    [Fact]
    public void PaneRuntimeFactory_omitted_factory_is_environment_ghostty_not_basic()
    {
        var factory = new PaneRuntimeFactory(ptyFactory: TestPaneFactories.ProcessIo());
        Assert.Equal(VtProviderKind.Ghostty, factory.VtEngineFactory.Selection.Provider);
        Assert.True(factory.VtEngineFactory.Selection.RequireGhostty);
        Assert.Equal("ghostty", factory.VtEngineFactory.ProviderWireName);
        Assert.IsType<VtEngineFactory>(factory.VtEngineFactory);
    }

    [SkippableFact]
    public void Factory_create_returns_ghostty_for_default_selection()
    {
        var lib = GhosttyTestRequire.TryResolveNativeLibraryPath();
        GhosttyTestRequire.RequireNativeLibrary(lib);
        var factory = new VtEngineFactory(new VtProviderSelection
        {
            LibraryPathOverride = lib,
        });
        using var engine = factory.Create(20, 6);
        Assert.IsType<GhosttyVtEngine>(engine);
        Assert.Equal("ghostty", factory.ProviderWireName);
        Assert.Equal("ghostty", engine.CaptureSnapshot().Provider);
    }

    [Fact]
    public void Factory_leftover_basic_enum_still_creates_ghostty_or_throws()
    {
        var lib = GhosttyTestRequire.TryResolveNativeLibraryPath();
        var factory = new VtEngineFactory(new VtProviderSelection
        {
            Provider = VtProviderKind.Basic,
            RequireGhostty = false,
            LibraryPathOverride = lib ?? Path.Combine(Path.GetTempPath(), "hypa-missing-ghostty-enum", "nope.so"),
        });
        Assert.Equal("ghostty", factory.ProviderWireName);
        if (lib is null || !File.Exists(lib))
        {
            Assert.ThrowsAny<Exception>(() => factory.Create(20, 6));
            return;
        }

        using var engine = factory.Create(20, 6);
        Assert.IsType<GhosttyVtEngine>(engine);
    }

    [Fact]
    public void Missing_library_create_throws_not_basic()
    {
        if (OperatingSystem.IsWindows())
            return;

        var missing = Path.Combine(
            Path.GetTempPath(),
            "hypa-missing-ghostty-only-" + Guid.NewGuid().ToString("N"),
            "nope.so");
        var factory = new VtEngineFactory(new VtProviderSelection
        {
            Provider = VtProviderKind.Ghostty,
            RequireGhostty = true,
            LibraryPathOverride = missing,
        });
        Assert.ThrowsAny<Exception>(() => factory.Create(20, 6));
    }

    private sealed class CountingVtEngineFactory : IVtEngineFactory
    {
        public int Created { get; private set; }

        public IVtEngine? Last { get; private set; }

        public VtProviderSelection Selection { get; } = new()
        {
            Provider = VtProviderKind.Ghostty,
            RequireGhostty = true,
        };

        public string ProviderWireName => "ghostty";

        public IVtEngine Create(int cols, int rows) => Create(cols, rows, 10_000);

        public IVtEngine Create(int cols, int rows, int maxScrollback)
        {
            Created++;
            Last = new StubVtEngine(cols, rows);
            return Last;
        }
    }

}
