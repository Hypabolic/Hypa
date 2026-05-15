using Hypa.Infrastructure.Storage;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Storage;

public sealed class SqliteProjectRegistryTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
    private readonly HypaDataOptions _options;
    private readonly SqliteSchemaInitializer _schema;
    private readonly SqliteProjectRegistry _registry;

    public SqliteProjectRegistryTests()
    {
        _options = new HypaDataOptions { DataDirectory = _dataDir };
        _schema = new SqliteSchemaInitializer(_options);
        _registry = new SqliteProjectRegistry(_options, _schema);
    }

    public async Task InitializeAsync() => await _schema.InitAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    [Fact]
    public async Task RegisterAsync_Persists()
    {
        await _registry.RegisterAsync("/repo/a", "claude");

        var all = await _registry.GetAllAsync();

        Assert.Single(all);
        Assert.Equal("/repo/a", all[0].RootPath);
        Assert.Equal("claude", all[0].AgentKey);
    }

    [Fact]
    public async Task RegisterAsync_SameRootAndAgent_Upserts()
    {
        await _registry.RegisterAsync("/repo/a", "claude");
        await _registry.RegisterAsync("/repo/a", "claude");

        var all = await _registry.GetAllAsync();

        Assert.Single(all);
    }

    [Fact]
    public async Task RegisterAsync_DifferentAgents_SameRoot_BothPersist()
    {
        await _registry.RegisterAsync("/repo/a", "claude");
        await _registry.RegisterAsync("/repo/a", "codex");

        var all = await _registry.GetAllAsync();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetByAgentAsync_ReturnsOnlyMatchingAgent()
    {
        await _registry.RegisterAsync("/repo/a", "claude");
        await _registry.RegisterAsync("/repo/b", "codex");
        await _registry.RegisterAsync("/repo/c", "claude");

        var claudeRegs = await _registry.GetByAgentAsync("claude");

        Assert.Equal(2, claudeRegs.Count);
        Assert.All(claudeRegs, r => Assert.Equal("claude", r.AgentKey));
    }

    [Fact]
    public async Task GetByAgentAsync_UnknownAgent_ReturnsEmpty()
    {
        await _registry.RegisterAsync("/repo/a", "claude");

        var results = await _registry.GetByAgentAsync("codex");

        Assert.Empty(results);
    }

    [Fact]
    public async Task UnregisterAsync_RemovesEntry()
    {
        await _registry.RegisterAsync("/repo/a", "claude");
        await _registry.UnregisterAsync("/repo/a", "claude");

        var all = await _registry.GetAllAsync();

        Assert.Empty(all);
    }

    [Fact]
    public async Task UnregisterAsync_OnlyRemovesMatchingAgentEntry()
    {
        await _registry.RegisterAsync("/repo/a", "claude");
        await _registry.RegisterAsync("/repo/a", "codex");

        await _registry.UnregisterAsync("/repo/a", "claude");

        var all = await _registry.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("codex", all[0].AgentKey);
    }

    [Fact]
    public async Task UnregisterAsync_NonExistentEntry_DoesNotThrow()
    {
        await _registry.UnregisterAsync("/nonexistent", "claude");
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllRegistrations()
    {
        await _registry.RegisterAsync("/repo/a", "claude");
        await _registry.RegisterAsync("/repo/b", "codex");

        var all = await _registry.GetAllAsync();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task RegisterAsync_InstalledAt_IsRoundTripped()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _registry.RegisterAsync("/repo/a", "claude");
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        var all = await _registry.GetAllAsync();

        Assert.InRange(all[0].InstalledAt, before, after);
    }
}
