using System.Security.Cryptography;
using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Reads a checkpoint manifest and verifies each artifact digest.
/// </summary>
[Collection("CheckpointTests")]
public class CheckpointFakeAtomicPullTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir;
    private readonly string _workspace;
    private readonly RuntimeStatePaths _paths;
    private readonly List<ControlPlaneService> _planes = [];

    public CheckpointFakeAtomicPullTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h10-pull-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_dir, "ws");
        Directory.CreateDirectory(_workspace);
        File.WriteAllText(Path.Combine(_workspace, "readme.md"), "# pull test");
        _paths = new RuntimeStatePaths { StateDirectory = Path.Combine(_dir, "state") };
        Directory.CreateDirectory(_paths.StateDirectory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var cp in _planes)
        {
            try { await cp.ShutdownAsync(CancellationToken.None); }
            catch { /* teardown */ }
        }

        _planes.Clear();
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
    }

    [Fact]
    public async Task Fake_atomic_pull_verifies_manifest_artifact_digests()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("pull1"));
        state.UpdateSession(s => s with
        {
            Name = "pull1",
            LifecycleState = SessionLifecycle.Ready,
            Binding = new AtomicBinding { ProjectRoot = _workspace },
        });

        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var cpStore = new FileCheckpointStore(_paths);
        var checkpoints = new DefaultCheckpointService(cpStore, new DefaultCheckpointArtifactBuilder());
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Create(new PaneIntelligencePipeline()),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            checkpoints: checkpoints);
        _planes.Add(cp);

        // Keep concrete type for dispose.

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        var exp = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);

        var manifestRel = exp.GetProperty("manifest_path").GetString()!;
        var manifestSha = exp.GetProperty("manifest_sha256").GetString()!;

        // --- Fake Atomic puller (contract only; no Atomic types) ---
        var stateRoot = _paths.StateDirectory;
        var manifestAbs = Path.Combine(stateRoot, manifestRel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(manifestAbs));

        var manifestBytes = await File.ReadAllBytesAsync(manifestAbs);
        var computedManifestSha = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        Assert.Equal(manifestSha, computedManifestSha);

        var manifest = JsonSerializer.Deserialize(
            manifestBytes, ProtocolJsonContext.Default.CheckpointManifestDto)!;
        Assert.Equal(checkpointId, manifest.CheckpointId);
        Assert.NotEmpty(manifest.Artifacts);

        var checkpointDir = Path.GetDirectoryName(manifestAbs)!;
        foreach (var art in manifest.Artifacts)
        {
            Assert.False(Path.IsPathRooted(art.Path), "artifact path must be relative");
            Assert.DoesNotContain("..", art.Path, StringComparison.Ordinal);
            var artAbs = Path.Combine(checkpointDir, art.Path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(artAbs), $"missing artifact {art.Path}");
            var artBytes = await File.ReadAllBytesAsync(artAbs);
            Assert.Equal(art.ByteCount, artBytes.LongLength);
            var artSha = Convert.ToHexString(SHA256.HashData(artBytes)).ToLowerInvariant();
            Assert.Equal(art.Sha256, artSha);
        }

        // Incomplete flag is a documented soft signal, not a hard fail for pull verification.
        _ = manifest.TransferIncomplete;

        await journal.DisposeAsync();
    }
}
