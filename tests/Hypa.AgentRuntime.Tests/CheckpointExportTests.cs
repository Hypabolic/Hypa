using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

[Collection("CheckpointTests")]
public class CheckpointExportTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir;
    private readonly string _workspace;
    private readonly RuntimeStatePaths _paths;
    private readonly List<ControlPlaneService> _planes = [];
    private readonly TestWorkspaceWalkHooks _walkHooks = new();
    private readonly TestCheckpointExportHooks _exportHooks = new();
    private readonly TestGitProbeHooks _gitHooks = new();

    public CheckpointExportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h10-exp-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(_workspace);
        File.WriteAllText(Path.Combine(_workspace, "hello.txt"), "hello checkpoint");
        Directory.CreateDirectory(Path.Combine(_workspace, "node_modules"));
        File.WriteAllText(Path.Combine(_workspace, "node_modules", "skip.js"), "skip");
        _paths = new RuntimeStatePaths { StateDirectory = Path.Combine(_dir, "state") };
        Directory.CreateDirectory(_paths.StateDirectory);
        ResetTestHooks();
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
        ResetTestHooks();
        SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
    }

    private async Task<(ControlPlaneService Cp, AppState State, FileRuntimeEventJournal Journal, FileCheckpointStore Store)>
        CreatePlaneAsync(string sessionName, IGitWorkspaceProbe? git = null)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New(sessionName));
        state.UpdateSession(s => s with
        {
            Name = sessionName,
            LifecycleState = SessionLifecycle.Ready,
            Binding = new AtomicBinding { ProjectRoot = _workspace, RunId = "run_exp" },
        });

        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var cpStore = new FileCheckpointStore(_paths);
        var builder = new DefaultCheckpointArtifactBuilder(
            git ?? new StubGitWorkspaceProbe(),
            _exportHooks,
            _walkHooks);
        var checkpoints = new DefaultCheckpointService(cpStore, builder);

        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Create(new PaneIntelligencePipeline()),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            redactor: new DefaultEventPayloadRedactor(),
            checkpoints: checkpoints);
        _planes.Add(cp);

        return (cp, state, journal, cpStore);
    }

    [Fact]
    public async Task Export_writes_manifest_with_path_sha256_and_byte_count()
    {
        var (cp, _, journal, _) = await CreatePlaneAsync("exp1");

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true,"include_scrollback":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;
        var barrier = prep.GetProperty("barrier_seq").GetInt64();

        var exp = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);

        Assert.Equal("exported", exp.GetProperty("state").GetString());
        Assert.Equal(barrier, exp.GetProperty("barrier_seq").GetInt64());
        var manifestPath = exp.GetProperty("manifest_path").GetString()!;
        Assert.StartsWith("checkpoints/", manifestPath, StringComparison.Ordinal);
        Assert.EndsWith("/manifest.json", manifestPath, StringComparison.Ordinal);
        Assert.Equal(64, exp.GetProperty("manifest_sha256").GetString()!.Length);
        Assert.True(exp.GetProperty("byte_count").GetInt64() > 0);

        var abs = Path.Combine(_paths.StateDirectory, manifestPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(abs));
        var bytes = await File.ReadAllBytesAsync(abs);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(exp.GetProperty("manifest_sha256").GetString(), sha);

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Assert.Equal(checkpointId, root.GetProperty("checkpoint_id").GetString());
        Assert.Equal(barrier, root.GetProperty("barrier_seq").GetInt64());
        Assert.True(root.GetProperty("artifacts").GetArrayLength() >= 1);

        // Excluded node_modules must not appear as artifacts.
        foreach (var art in root.GetProperty("artifacts").EnumerateArray())
        {
            var p = art.GetProperty("path").GetString()!;
            Assert.DoesNotContain("node_modules", p, StringComparison.Ordinal);
        }

        // Lifecycle exported event.
        var range = await journal.ReadRangeAsync(0, classes: null, budget: 50, CancellationToken.None);
        Assert.True(range.IsOk);
        Assert.Contains(range.Value, r =>
            r.Type == ProtocolEventTypes.CheckpointLifecycle &&
            r.PayloadJson.Contains("exported", StringComparison.Ordinal));

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Export_omits_planted_env_and_id_rsa_sets_transfer_incomplete_and_warning()
    {
        const string envSecret = "H19_ENV_SECRET_BYTES=do-not-export";
        const string keySecret = "H19_ID_RSA_SECRET_BYTES_do_not_export";
        await File.WriteAllTextAsync(Path.Combine(_workspace, ".env"), envSecret);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "id_rsa"), keySecret);

        var (cp, _, journal, _) = await CreatePlaneAsync("exp-secret-omit");
        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true,"include_scrollback":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        var exp = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);

        Assert.Equal("exported", exp.GetProperty("state").GetString());
        Assert.True(exp.GetProperty("transfer_incomplete").GetBoolean());

        var abs = Path.Combine(
            _paths.StateDirectory,
            exp.GetProperty("manifest_path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(abs));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("transfer_incomplete").GetBoolean());
        var warnings = root.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
        Assert.Contains(warnings, w => w.Contains("omit secret file .env", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("omit secret file id_rsa", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains(envSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains(keySecret, StringComparison.Ordinal));

        foreach (var art in root.GetProperty("artifacts").EnumerateArray())
        {
            var p = art.GetProperty("path").GetString()!;
            Assert.DoesNotContain(".env", p, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("id_rsa", p, StringComparison.OrdinalIgnoreCase);
        }

        var excludes = root.GetProperty("excludes").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(".env", excludes);
        Assert.Contains("id_rsa", excludes);
        Assert.Contains("*.pem", excludes);

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Export_secret_bytes_are_absent_from_checkpoint_tree_and_manifest()
    {
        const string envSecret = "H19_ENV_TREE_SECRET=never-copy-me";
        const string keySecret = "H19_RSA_TREE_SECRET_never_copy_me";
        await File.WriteAllTextAsync(Path.Combine(_workspace, ".env"), envSecret);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "id_rsa"), keySecret);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "notes.pem"), "H19_PEM_TREE_SECRET");
        Directory.CreateDirectory(Path.Combine(_workspace, ".ssh"));
        await File.WriteAllTextAsync(Path.Combine(_workspace, ".ssh", "config"), "H19_SSH_TREE_SECRET");

        var (cp, _, journal, store) = await CreatePlaneAsync("exp-secret-absent");
        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true,"include_scrollback":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        var exp = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);
        Assert.True(exp.GetProperty("transfer_incomplete").GetBoolean());

        var ckptDir = store.GetCheckpointDirectory(checkpointId);
        Assert.False(File.Exists(Path.Combine(ckptDir, "artifacts", "workspace", ".env")));
        Assert.False(File.Exists(Path.Combine(ckptDir, "artifacts", "workspace", "id_rsa")));
        Assert.False(File.Exists(Path.Combine(ckptDir, "artifacts", "workspace", "notes.pem")));
        Assert.False(Directory.Exists(Path.Combine(ckptDir, "artifacts", "workspace", ".ssh")));

        foreach (var file in Directory.EnumerateFiles(ckptDir, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(envSecret, text);
            Assert.DoesNotContain(keySecret, text);
            Assert.DoesNotContain("H19_PEM_TREE_SECRET", text);
            Assert.DoesNotContain("H19_SSH_TREE_SECRET", text);
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Export_denied_inode_swapped_onto_allowed_name_is_omitted()
    {
        // After deny harvest, replace already-listed safe.txt with the .env inode.
        // Export must omit that entry, keep secret bytes out of the tree, and mark incomplete.
        const string secret = "H19_SWAP_SECRET_BYTES_do_not_export";
        await File.WriteAllTextAsync(Path.Combine(_workspace, ".env"), secret);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "safe.txt"), "allowed-before-swap");

        _exportHooks.AfterSecretDenyCheckHandler = dir =>
        {
            var env = Path.Combine(dir, ".env");
            var safe = Path.Combine(dir, "safe.txt");
            if (!File.Exists(env) || !File.Exists(safe))
                return;
            File.Delete(safe);
            File.Move(env, safe);
        };

        try
        {
            var (cp, _, journal, store) = await CreatePlaneAsync("exp-name-swap");
            var prep = await cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointPrepare,
                JsonDocument.Parse("""{"include_workspace_files":true,"include_scrollback":false}""").RootElement,
                CancellationToken.None);
            var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

            var exp = await cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointExport,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
                CancellationToken.None);

            Assert.Equal("exported", exp.GetProperty("state").GetString());
            Assert.True(exp.GetProperty("transfer_incomplete").GetBoolean());

            var abs = Path.Combine(
                _paths.StateDirectory,
                exp.GetProperty("manifest_path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(abs));
            var root = doc.RootElement;
            Assert.True(root.GetProperty("transfer_incomplete").GetBoolean());

            var warnings = root.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
            Assert.Contains(warnings, w =>
                w.Contains("safe.txt", StringComparison.Ordinal)
                && (w.Contains("omit secret file", StringComparison.Ordinal)
                    || w.Contains("omit replaced file", StringComparison.Ordinal)));
            Assert.DoesNotContain(warnings, w => w.Contains(secret, StringComparison.Ordinal));

            var ckptDir = store.GetCheckpointDirectory(checkpointId);
            Assert.False(File.Exists(Path.Combine(ckptDir, "artifacts", "workspace", "safe.txt")));
            Assert.False(File.Exists(Path.Combine(ckptDir, "artifacts", "workspace", ".env")));

            foreach (var art in root.GetProperty("artifacts").EnumerateArray())
            {
                var p = art.GetProperty("path").GetString()!;
                Assert.False(
                    p.EndsWith("/safe.txt", StringComparison.Ordinal)
                    || p.EndsWith("/.env", StringComparison.Ordinal),
                    p);
            }

            foreach (var file in Directory.EnumerateFiles(ckptDir, "*", SearchOption.AllDirectories))
            {
                var text = await File.ReadAllTextAsync(file);
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }

            await journal.DisposeAsync();
        }
        finally
        {
            _exportHooks.AfterSecretDenyCheckHandler = null;
        }
    }

    [Fact]
    public async Task Export_still_includes_non_secret_workspace_file()
    {
        const string envSecret = "H19_KEEP_ENV_SECRET=no";
        await File.WriteAllTextAsync(Path.Combine(_workspace, ".env"), envSecret);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "id_rsa"), "H19_KEEP_RSA_SECRET");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "keep.txt"), "keep-workspace-body");

        var (cp, _, journal, store) = await CreatePlaneAsync("exp-keep-plain");
        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true,"include_scrollback":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        var exp = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);

        var keep = Path.Combine(
            store.GetCheckpointDirectory(checkpointId), "artifacts", "workspace", "keep.txt");
        var hello = Path.Combine(
            store.GetCheckpointDirectory(checkpointId), "artifacts", "workspace", "hello.txt");
        Assert.True(File.Exists(keep));
        Assert.Equal("keep-workspace-body", await File.ReadAllTextAsync(keep));
        Assert.True(File.Exists(hello));
        Assert.Equal("hello checkpoint", await File.ReadAllTextAsync(hello));
        Assert.False(File.Exists(Path.Combine(
            store.GetCheckpointDirectory(checkpointId), "artifacts", "workspace", ".env")));

        var abs = Path.Combine(
            _paths.StateDirectory,
            exp.GetProperty("manifest_path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(abs));
        var paths = doc.RootElement.GetProperty("artifacts").EnumerateArray()
            .Select(a => a.GetProperty("path").GetString()!)
            .ToList();
        Assert.Contains(paths, p => p.EndsWith("/keep.txt", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("/hello.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains(".env", StringComparison.OrdinalIgnoreCase));

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Export_conflicts_when_session_fingerprint_changes()
    {
        var (cp, state, journal, _) = await CreatePlaneAsync("exp-conflict");

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        // Simulate concurrent graph write that bypassed freeze (test-only path).
        state.UpdateSession(s => s with
        {
            PlacementGeneration = s.PlacementGeneration + 1,
            Binding = (s.Binding ?? new AtomicBinding()) with { RunId = "mutated_run" },
        });

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointExport,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.CheckpointConflict, ex.Code);

        // Conflict must auto-unfreeze so the session is not permanently immutable.
        Assert.Equal(SessionLifecycle.Ready, state.Snapshot().LifecycleState);

        // Mutations work again after conflict recovery.
        var ws = await cp.DispatchAsync(
            "workspace.create",
            JsonDocument.Parse("""{"cwd":"/tmp","create_pane":false}""").RootElement,
            CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(ws.GetProperty("workspace_id").GetString()));

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Export_after_abort_and_mutate_remains_exported_never_conflict()
    {
        // prepare → export → abort → mutate graph → re-export same id must stay exported.
        // Must never clobber Exported → Conflict (stable Atomic pull handle).
        var (cp, state, journal, store) = await CreatePlaneAsync("exp-idemp");

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true,"include_scrollback":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        var exp1 = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);
        Assert.Equal(CheckpointStates.Exported, exp1.GetProperty("state").GetString());
        var manifestPath = exp1.GetProperty("manifest_path").GetString()!;
        var manifestSha = exp1.GetProperty("manifest_sha256").GetString()!;
        var byteCount = exp1.GetProperty("byte_count").GetInt64();

        // Abort after successful export: unfreezes only; durable + RPC state stay exported.
        var abort = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointAbort,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}","reason":"release-after-export"}""").RootElement,
            CancellationToken.None);
        Assert.Equal(CheckpointStates.Exported, abort.GetProperty("state").GetString());
        Assert.Equal(SessionLifecycle.Ready, abort.GetProperty("session_state").GetString());
        Assert.Equal(SessionLifecycle.Ready, state.Snapshot().LifecycleState);

        // Must not emit checkpoint.lifecycle aborted for a non-demoting release.
        var events = await journal.ReadRangeAsync(0, classes: null, budget: 500);
        Assert.True(events.IsOk);
        Assert.DoesNotContain(
            events.Value,
            e => string.Equals(e.Type, ProtocolEventTypes.CheckpointLifecycle, StringComparison.Ordinal)
                 && e.PayloadJson.Contains("\"state\":\"aborted\"", StringComparison.Ordinal)
                 && e.PayloadJson.Contains(checkpointId, StringComparison.Ordinal));
        Assert.Contains(
            events.Value,
            e => string.Equals(e.Type, ProtocolEventTypes.SessionLifecycle, StringComparison.Ordinal)
                 && e.PayloadJson.Contains("\"state\":\"ready\"", StringComparison.Ordinal));

        // Graph mutation after unfreeze.
        state.UpdateSession(s => s with
        {
            PlacementGeneration = s.PlacementGeneration + 7,
            Binding = (s.Binding ?? new AtomicBinding()) with { RunId = "post_export_mut" },
        });
        Assert.NotEqual(
            (await store.GetAsync(checkpointId)).Value!.SessionFingerprint,
            SessionGraphFingerprint.Compute(state.Snapshot()));

        // Re-export same id: idempotent exported handle, never checkpoint_conflict.
        var exp2 = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);
        Assert.Equal(CheckpointStates.Exported, exp2.GetProperty("state").GetString());
        Assert.Equal(manifestPath, exp2.GetProperty("manifest_path").GetString());
        Assert.Equal(manifestSha, exp2.GetProperty("manifest_sha256").GetString());
        Assert.Equal(byteCount, exp2.GetProperty("byte_count").GetInt64());

        var durable = await store.GetAsync(checkpointId);
        Assert.True(durable.IsOk);
        Assert.Equal(CheckpointStates.Exported, durable.Value!.State);
        Assert.NotEqual(CheckpointStates.Conflict, durable.Value.State);

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Export_unknown_checkpoint_is_not_found()
    {
        var (cp, _, journal, _) = await CreatePlaneAsync("exp-nf");

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointExport,
                JsonDocument.Parse("""{"checkpoint_id":"cp_missing"}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.NotFound, ex.Code);

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Export_transfer_incomplete_when_file_over_budget()
    {
        // Build with a tiny max file budget via direct service call.
        var store = new InMemoryCheckpointStore(_paths.StateDirectory);
        var builder = new BudgetedArtifactBuilder(maxFileBytes: 4);
        var service = new DefaultCheckpointService(store, builder);

        var session = new SessionState
        {
            Id = SessionId.New("budget"),
            Name = "budget",
            Binding = new AtomicBinding { ProjectRoot = _workspace },
        };
        var record = MakePrepared("cp_budget", session, _workspace);
        Assert.True((await service.SavePreparedAsync(record)).IsOk);

        var material = await service.ExportAsync(record, session);
        Assert.True(material.IsOk, material.IsOk ? null : material.Error.Message);
        Assert.True(material.Value.TransferIncomplete);
    }

    [Fact]
    public void Path_containment_helpers_reject_escape_and_dotdot()
    {
        var root = Path.GetFullPath(Path.Combine(_dir, "contain-root"));
        Directory.CreateDirectory(root);
        var inside = Path.GetFullPath(Path.Combine(root, "a", "b.txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllText(inside, "x");

        Assert.True(DefaultCheckpointArtifactBuilder.IsPathInsideRoot(root, root));
        Assert.True(DefaultCheckpointArtifactBuilder.IsPathInsideRoot(root, inside));
        Assert.False(DefaultCheckpointArtifactBuilder.IsPathInsideRoot(
            root, Path.GetFullPath(Path.Combine(root, "..", "escape.txt"))));

        Assert.True(DefaultCheckpointArtifactBuilder.IsUnsafeRelativePath(".."));
        Assert.True(DefaultCheckpointArtifactBuilder.IsUnsafeRelativePath("../secret"));
        Assert.True(DefaultCheckpointArtifactBuilder.IsUnsafeRelativePath("a/../../b"));
        Assert.True(DefaultCheckpointArtifactBuilder.IsUnsafeRelativePath("/abs"));
        Assert.False(DefaultCheckpointArtifactBuilder.IsUnsafeRelativePath("src/main.cs"));
        Assert.False(DefaultCheckpointArtifactBuilder.IsUnsafeRelativePath("nested/file.txt"));
    }

    [Fact]
    public void NoFollow_open_reads_regular_file_and_rejects_symlink()
    {
        var regular = Path.Combine(_dir, "nofollow-regular.txt");
        File.WriteAllText(regular, "safe-content");

        var status = NoFollowFileReader.TryReadAllBytes(regular, out var bytes, out var detail);
        Assert.Equal(NoFollowFileReader.OpenStatus.Ok, status);
        Assert.Equal("safe-content", System.Text.Encoding.UTF8.GetString(bytes));

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var outside = Path.Combine(_dir, "nofollow-secret.txt");
        File.WriteAllText(outside, "SECRET_OUTSIDE");
        var link = Path.Combine(_dir, "nofollow-link.txt");
        File.CreateSymbolicLink(link, outside);

        var linkStatus = NoFollowFileReader.TryReadAllBytes(link, out var linkBytes, out var linkDetail);
        Assert.Equal(NoFollowFileReader.OpenStatus.SymlinkOrReparse, linkStatus);
        Assert.Empty(linkBytes);
        Assert.DoesNotContain("SECRET_OUTSIDE", linkDetail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workspace_walk_skips_file_symlink_to_outside_path()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var project = Path.Combine(_dir, "sym-project");
        var outside = Path.Combine(_dir, "outside-secret");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "ok.txt"), "inside");
        File.WriteAllText(outside, "SECRET_OUTSIDE");
        var linkPath = Path.Combine(project, "leak.txt");
        File.CreateSymbolicLink(linkPath, outside);

        var store = new InMemoryCheckpointStore(_paths.StateDirectory);
        var service = new DefaultCheckpointService(store, new DefaultCheckpointArtifactBuilder(exportHooks: _exportHooks, walkHooks: _walkHooks));
        var session = new SessionState
        {
            Id = SessionId.New("sym-file"),
            Name = "sym-file",
            Binding = new AtomicBinding { ProjectRoot = project },
        };
        var record = MakePrepared("cp_sym_file", session, project);
        Assert.True((await service.SavePreparedAsync(record)).IsOk);

        var material = await service.ExportAsync(record, session);
        Assert.True(material.IsOk, material.IsOk ? null : material.Error.Message);
        Assert.True(material.Value.TransferIncomplete);

        // Outside content must not appear under the checkpoint tree.
        var wsDir = Path.Combine(store.GetCheckpointDirectory(record.CheckpointId), "artifacts", "workspace");
        Assert.True(Directory.Exists(wsDir));
        Assert.True(File.Exists(Path.Combine(wsDir, "ok.txt")));
        Assert.False(File.Exists(Path.Combine(wsDir, "leak.txt")));
        foreach (var f in Directory.EnumerateFiles(wsDir, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(f);
            Assert.DoesNotContain("SECRET_OUTSIDE", text, StringComparison.Ordinal);
        }

        Assert.Contains(material.Value.Record.Warnings, w =>
            w.Contains("symlink", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Workspace_walk_skips_directory_symlink_escape()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var project = Path.Combine(_dir, "sym-dir-project");
        var outsideDir = Path.Combine(_dir, "outside-dir");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(project, "local.txt"), "local");
        File.WriteAllText(Path.Combine(outsideDir, "escaped.txt"), "ESCAPED_CONTENT");
        var linkDir = Path.Combine(project, "escape_link");
        Directory.CreateSymbolicLink(linkDir, outsideDir);

        var store = new InMemoryCheckpointStore(_paths.StateDirectory);
        var service = new DefaultCheckpointService(store, new DefaultCheckpointArtifactBuilder(exportHooks: _exportHooks, walkHooks: _walkHooks));
        var session = new SessionState
        {
            Id = SessionId.New("sym-dir"),
            Name = "sym-dir",
            Binding = new AtomicBinding { ProjectRoot = project },
        };
        var record = MakePrepared("cp_sym_dir", session, project);
        Assert.True((await service.SavePreparedAsync(record)).IsOk);

        var material = await service.ExportAsync(record, session);
        Assert.True(material.IsOk, material.IsOk ? null : material.Error.Message);
        Assert.True(material.Value.TransferIncomplete);

        var wsDir = Path.Combine(store.GetCheckpointDirectory(record.CheckpointId), "artifacts", "workspace");
        Assert.True(File.Exists(Path.Combine(wsDir, "local.txt")));
        Assert.False(Directory.Exists(Path.Combine(wsDir, "escape_link")));
        Assert.False(File.Exists(Path.Combine(wsDir, "escape_link", "escaped.txt")));
        foreach (var f in Directory.EnumerateFiles(wsDir, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(f);
            Assert.DoesNotContain("ESCAPED_CONTENT", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Openat_child_rejects_directory_swapped_to_symlink()
    {
        // Intermediate-symlink TOCTOU unit: after parent dir fd is open, replace a real
        // child directory with a symlink to a host path; openat(O_NOFOLLOW|O_DIRECTORY)
        // must fail and never surface outside bytes.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var project = Path.Combine(_dir, "toctou-openat");
        var outside = Path.Combine(_dir, "toctou-outside");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(project, "victim"));
        File.WriteAllText(Path.Combine(project, "victim", "inside.txt"), "inside-only");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET_HOST_BYTES");

        Assert.True(NoFollowWorkspaceWalker.TryOpenWalkRoot(
            project, out var rootFd, out var openDetail), openDetail);
        using (rootFd)
        {
            // Swap real directory → symlink to outside while parent fd stays open.
            Directory.Delete(Path.Combine(project, "victim"), recursive: true);
            Directory.CreateSymbolicLink(Path.Combine(project, "victim"), outside);

            var kind = NoFollowWorkspaceWalker.ClassifyEntry(rootFd, "victim", out var classDetail);
            Assert.Equal(NoFollowWorkspaceWalker.EntryKind.SymlinkOrReparse, kind);

            Assert.False(NoFollowWorkspaceWalker.TryOpenSubdirectory(
                rootFd, "victim", out var child, out var subDetail), subDetail);
            child.Dispose();

            // openat file read of a name that only exists outside must not succeed via follow.
            var status = NoFollowWorkspaceWalker.TryReadFile(
                rootFd, "secret.txt", out var bytes, out _);
            Assert.NotEqual(NoFollowFileReader.OpenStatus.Ok, status);
            Assert.DoesNotContain(
                "SECRET_HOST_BYTES",
                System.Text.Encoding.UTF8.GetString(bytes),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Openat_child_reads_regular_file()
    {
        // fd-relative O_NOFOLLOW read of a regular file must succeed. Intel macOS
        // fstat() (32-bit inode) must not mis-read st_ino as S_IFDIR.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var project = Path.Combine(_dir, "openat-regular");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "plain.txt"), "plain-body");

        Assert.True(NoFollowWorkspaceWalker.TryOpenWalkRoot(
            project, out var rootFd, out var openDetail), openDetail);
        using (rootFd)
        {
            var kind = NoFollowWorkspaceWalker.ClassifyEntry(rootFd, "plain.txt", out var classDetail);
            Assert.Equal(NoFollowWorkspaceWalker.EntryKind.RegularFile, kind);

            var status = NoFollowWorkspaceWalker.TryReadFile(
                rootFd, "plain.txt", out var bytes, out var readDetail);
            Assert.Equal(NoFollowFileReader.OpenStatus.Ok, status);
            Assert.Equal("plain-body", System.Text.Encoding.UTF8.GetString(bytes));
            Assert.True(string.IsNullOrEmpty(readDetail), readDetail);
            Assert.Null(classDetail);
        }
    }

    [Fact]
    public void Classify_then_name_swap_read_rejects_identity_change()
    {
        var project = Path.Combine(_dir, "ident-swap");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "safe.txt"), "allowed-body");
        File.WriteAllText(Path.Combine(project, ".env"), "SECRET_IDENT_BYTES");

        Assert.True(NoFollowWorkspaceWalker.TryOpenWalkRoot(
            project, out var rootFd, out var openDetail), openDetail);
        using (rootFd)
        {
            var kind = NoFollowWorkspaceWalker.ClassifyEntry(
                rootFd, "safe.txt", out var classifiedId, out var classDetail);
            Assert.Equal(NoFollowWorkspaceWalker.EntryKind.RegularFile, kind);
            Assert.NotEqual(default, classifiedId);
            Assert.True(string.IsNullOrEmpty(classDetail), classDetail);

            File.Delete(Path.Combine(project, "safe.txt"));
            File.Move(Path.Combine(project, ".env"), Path.Combine(project, "safe.txt"));

            var status = NoFollowWorkspaceWalker.TryReadFile(
                rootFd,
                "safe.txt",
                long.MaxValue,
                CancellationToken.None,
                classifiedId,
                out var bytes,
                out _,
                out var readId,
                out var readDetail);
            Assert.Equal(NoFollowFileReader.OpenStatus.IdentityMismatch, status);
            Assert.Contains("inode changed", readDetail, StringComparison.Ordinal);
            Assert.NotEqual(classifiedId, readId);
            Assert.DoesNotContain(
                "SECRET_IDENT_BYTES",
                System.Text.Encoding.UTF8.GetString(bytes),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Workspace_walk_directory_symlink_swap_mid_walk_never_exfils()
    {
        // Full export path: a real subdir is present at discovery time; a concurrent actor
        // replaces it with a symlink to outside before/during walk. Outside bytes must not
        // appear under artifacts/workspace; transfer_incomplete must be set.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var project = Path.Combine(_dir, "toctou-export-project");
        var outside = Path.Combine(_dir, "toctou-export-outside");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(project, "nested", "deep"));
        // Many files so the walk has a longer window for the swap race.
        for (var i = 0; i < 40; i++)
            File.WriteAllText(Path.Combine(project, $"pad-{i:D2}.txt"), "pad");
        File.WriteAllText(Path.Combine(project, "nested", "deep", "local.txt"), "local-ok");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "escaped.txt"), "ESCAPED_TOCTOU_CONTENT");

        var store = new InMemoryCheckpointStore(_paths.StateDirectory);
        var service = new DefaultCheckpointService(store, new DefaultCheckpointArtifactBuilder(exportHooks: _exportHooks, walkHooks: _walkHooks));
        var session = new SessionState
        {
            Id = SessionId.New("toctou-export"),
            Name = "toctou-export",
            Binding = new AtomicBinding { ProjectRoot = project },
        };
        var record = MakePrepared("cp_toctou_export", session, project);
        Assert.True((await service.SavePreparedAsync(record)).IsOk);

        var swap = Task.Run(async () =>
        {
            // Hammer swap: delete nested and point at outside for the duration of export.
            for (var i = 0; i < 200; i++)
            {
                try
                {
                    var nested = Path.Combine(project, "nested");
                    if (Directory.Exists(nested) && !IsSymlink(nested))
                    {
                        Directory.Delete(nested, recursive: true);
                        Directory.CreateSymbolicLink(nested, outside);
                    }
                    else if (IsSymlink(nested))
                    {
                        // leave symlink; re-create real dir next iteration sometimes
                        if (i % 3 == 0)
                        {
                            File.Delete(nested);
                            Directory.CreateDirectory(Path.Combine(nested, "deep"));
                            File.WriteAllText(
                                Path.Combine(nested, "deep", "local.txt"), "local-ok");
                        }
                    }
                }
                catch
                {
                    // concurrent with walk — best-effort
                }

                await Task.Delay(1);
            }
        });

        var material = await service.ExportAsync(record, session);
        await swap;

        Assert.True(material.IsOk, material.IsOk ? null : material.Error.Message);

        var wsDir = Path.Combine(
            store.GetCheckpointDirectory(record.CheckpointId), "artifacts", "workspace");
        if (Directory.Exists(wsDir))
        {
            foreach (var f in Directory.EnumerateFiles(wsDir, "*", SearchOption.AllDirectories))
            {
                var text = await File.ReadAllTextAsync(f);
                Assert.DoesNotContain("ESCAPED_TOCTOU_CONTENT", text, StringComparison.Ordinal);
            }
        }

        // If the swap was observed, walk marks incomplete; if not, still no outside bytes.
        // At least one pad file should usually copy when race loses to export finishing first.
        Assert.True(
            material.Value.TransferIncomplete
            || Directory.EnumerateFiles(wsDir, "pad-*.txt", SearchOption.TopDirectoryOnly).Any(),
            "export must either complete pad files or report incomplete");
    }

    [Fact]
    public void Walk_root_classify_then_swap_to_symlink_never_follows()
    {
        // Real project_root classified as a directory, then swapped to an outside symlink
        // before open: must not follow (old O_NOFOLLOW-fail fallback TOCTOU).
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var project = Path.Combine(_dir, "root-swap-unit");
        var outside = Path.Combine(_dir, "root-swap-unit-outside");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "inside.txt"), "inside-only");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET_ROOT_SWAP");

        Assert.True(NoFollowWorkspaceWalker.TryClassifyWalkRoot(
            project, out var isSymlink, out var classifyDetail), classifyDetail);
        Assert.False(isSymlink);

        Directory.Delete(project, recursive: true);
        Directory.CreateSymbolicLink(project, outside);

        Assert.False(NoFollowWorkspaceWalker.TryOpenWalkRootAfterClassify(
            project, isSymlinkAtClassify: false, out var handle, out var openDetail), openDetail);
        handle.Dispose();
    }

    [Fact]
    public async Task Export_walk_root_swap_to_outside_symlink_mid_open_never_exfils()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var project = Path.Combine(_dir, "root-swap-export");
        var outside = Path.Combine(_dir, "root-swap-export-outside");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "ok.txt"), "inside-ok");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET_WALK_ROOT");
        Assert.True(await InitGitRepoAsync(outside, "outside-secret"));
        var outsideHead = await ReadGitHeadAsync(outside);
        Assert.False(string.IsNullOrWhiteSpace(outsideHead));

        var store = new InMemoryCheckpointStore(_paths.StateDirectory);
        var service = new DefaultCheckpointService(
            store, new DefaultCheckpointArtifactBuilder(new ProcessGitWorkspaceProbe(hooks: _gitHooks), _exportHooks, _walkHooks));
        var session = new SessionState
        {
            Id = SessionId.New("root-swap"),
            Name = "root-swap",
            Binding = new AtomicBinding { ProjectRoot = project },
        };
        var record = MakePrepared("cp_root_swap", session, project);
        Assert.True((await service.SavePreparedAsync(record)).IsOk);

        _walkHooks.AfterWalkRootLstatHandler = path =>
        {
            if (!string.Equals(path, Path.GetFullPath(project), StringComparison.Ordinal)
                && !string.Equals(path, project, StringComparison.Ordinal))
                return;

            try
            {
                if (Directory.Exists(project) && !IsSymlink(project))
                {
                    Directory.Delete(project, recursive: true);
                    Directory.CreateSymbolicLink(project, outside);
                }
            }
            catch
            {
                // best-effort swap
            }
        };

        try
        {
            var material = await service.ExportAsync(record, session);
            Assert.True(material.IsOk, material.IsOk ? null : material.Error.Message);
            Assert.True(material.Value.TransferIncomplete);

            var wsDir = Path.Combine(
                store.GetCheckpointDirectory(record.CheckpointId), "artifacts", "workspace");
            if (Directory.Exists(wsDir))
            {
                foreach (var f in Directory.EnumerateFiles(wsDir, "*", SearchOption.AllDirectories))
                {
                    var text = await File.ReadAllTextAsync(f);
                    Assert.DoesNotContain("SECRET_WALK_ROOT", text, StringComparison.Ordinal);
                }
            }

            Assert.Contains(material.Value.Record.Warnings, w =>
                w.Contains("project_root", StringComparison.OrdinalIgnoreCase)
                || w.Contains("symlink", StringComparison.OrdinalIgnoreCase)
                || w.Contains("open", StringComparison.OrdinalIgnoreCase));

            var gitMeta = Path.Combine(
                store.GetCheckpointDirectory(record.CheckpointId), "artifacts", "git_meta.json");
            if (File.Exists(gitMeta))
            {
                using var gitDoc = JsonDocument.Parse(await File.ReadAllTextAsync(gitMeta));
                var head = gitDoc.RootElement.TryGetProperty("head", out var h)
                    ? h.GetString()
                    : null;
                Assert.NotEqual(outsideHead, head);
                Assert.DoesNotContain("SECRET_WALK_ROOT", await File.ReadAllTextAsync(gitMeta));
            }
        }
        finally
        {
            _walkHooks.AfterWalkRootLstatHandler = null;
        }
    }

    [Fact]
    public async Task Export_operator_symlink_project_root_resolves_once()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var real = Path.Combine(_dir, "op-root-real");
        var link = Path.Combine(_dir, "op-root-link");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "from-target.txt"), "operator-ok");
        Directory.CreateSymbolicLink(link, real);

        var store = new InMemoryCheckpointStore(_paths.StateDirectory);
        var service = new DefaultCheckpointService(store, new DefaultCheckpointArtifactBuilder(exportHooks: _exportHooks, walkHooks: _walkHooks));
        var session = new SessionState
        {
            Id = SessionId.New("op-root"),
            Name = "op-root",
            Binding = new AtomicBinding { ProjectRoot = link },
        };
        var record = MakePrepared("cp_op_root", session, link);
        Assert.True((await service.SavePreparedAsync(record)).IsOk);

        var material = await service.ExportAsync(record, session);
        Assert.True(material.IsOk, material.IsOk ? null : material.Error.Message);

        var copied = Path.Combine(
            store.GetCheckpointDirectory(record.CheckpointId),
            "artifacts", "workspace", "from-target.txt");
        Assert.True(File.Exists(copied));
        Assert.Equal("operator-ok", await File.ReadAllTextAsync(copied));
    }

    [Fact(Timeout = 15000)]
    public async Task Export_fifo_in_workspace_is_incomplete_and_abort_unfreezes()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var project = Path.Combine(_dir, "fifo-project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "ok.txt"), "ok-body");
        var fifoPath = Path.Combine(project, "block.fifo");
        Assert.Equal(0, MkFifo(fifoPath, 0x1B6)); // 0666

        // Production git probe (or a complete stub) so transfer_incomplete cannot
        // come from StubGitWorkspaceProbe. Completeness must be the FIFO skip.
        var (cp, state, journal, _) = await CreatePlaneAsync(
            "fifo-exp", new CompleteGitWorkspaceProbe());
        state.UpdateSession(s => s with
        {
            Binding = (s.Binding ?? new AtomicBinding()) with { ProjectRoot = project },
        });

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true,"include_scrollback":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;
        Assert.Equal(SessionLifecycle.FrozenReadOnly, state.Snapshot().LifecycleState);

        var exp = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);
        Assert.Equal("exported", exp.GetProperty("state").GetString());
        Assert.True(exp.GetProperty("transfer_incomplete").GetBoolean());

        var abs = Path.Combine(
            _paths.StateDirectory,
            exp.GetProperty("manifest_path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(abs)))
        {
            foreach (var art in doc.RootElement.GetProperty("artifacts").EnumerateArray())
            {
                var p = art.GetProperty("path").GetString()!;
                Assert.DoesNotContain("block.fifo", p, StringComparison.Ordinal);
            }

            Assert.True(doc.RootElement.TryGetProperty("warnings", out var warnEl)
                && warnEl.ValueKind == JsonValueKind.Array);
            var warnings = warnEl.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .ToList();
            Assert.Contains(warnings, w =>
                w.Contains("special file", StringComparison.OrdinalIgnoreCase)
                && w.Contains("block.fifo", StringComparison.Ordinal));
        }

        var abort = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointAbort,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}","reason":"after-fifo"}""").RootElement,
            CancellationToken.None);
        Assert.Equal(SessionLifecycle.Ready, abort.GetProperty("session_state").GetString());
        Assert.Equal(SessionLifecycle.Ready, state.Snapshot().LifecycleState);

        await journal.DisposeAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task Abort_during_export_unfreezes_without_waiting_for_walk()
    {
        var project = Path.Combine(_dir, "abort-during");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "ok.txt"), "ok");

        var (cp, state, journal, store) = await CreatePlaneAsync("abort-io");
        state.UpdateSession(s => s with
        {
            Binding = (s.Binding ?? new AtomicBinding()) with { ProjectRoot = project },
        });

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _exportHooks.ExportIoDelay = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        try
        {
            var exportTask = cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointExport,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
                CancellationToken.None);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var abort = await cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointAbort,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}","reason":"concurrent"}""").RootElement,
                CancellationToken.None);

            Assert.Equal(CheckpointStates.Aborted, abort.GetProperty("state").GetString());
            Assert.Equal(SessionLifecycle.Ready, abort.GetProperty("session_state").GetString());
            Assert.Equal(SessionLifecycle.Ready, state.Snapshot().LifecycleState);

            var durable = await store.GetAsync(checkpointId);
            Assert.True(durable.IsOk);
            Assert.NotNull(durable.Value);
            Assert.Equal(CheckpointStates.Aborted, durable.Value!.State);

            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() => exportTask);
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
            Assert.Contains("cancel", ex.Message, StringComparison.OrdinalIgnoreCase);

            var durableAfter = await store.GetAsync(checkpointId);
            Assert.True(durableAfter.IsOk);
            Assert.Equal(CheckpointStates.Aborted, durableAfter.Value!.State);
            Assert.Equal(SessionLifecycle.Ready, state.Snapshot().LifecycleState);

            var range = await journal.ReadRangeAsync(0, classes: null, budget: 50, CancellationToken.None);
            Assert.True(range.IsOk);
            Assert.Contains(range.Value, r =>
                r.Type == ProtocolEventTypes.CheckpointLifecycle &&
                r.PayloadJson.Contains("aborted", StringComparison.Ordinal));
        }
        finally
        {
            _exportHooks.ExportIoDelay = null;
        }

        await journal.DisposeAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task Abort_after_export_finishes_first_keeps_exported()
    {
        var project = Path.Combine(_dir, "abort-after-win");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "ok.txt"), "ok");

        var (cp, state, journal, store) = await CreatePlaneAsync("abort-win");
        state.UpdateSession(s => s with
        {
            Binding = (s.Binding ?? new AtomicBinding()) with { ProjectRoot = project },
        });

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _exportHooks.ExportIoDelay = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };

        try
        {
            var exportTask = cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointExport,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
                CancellationToken.None);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();

            var exp = await exportTask;
            Assert.Equal(CheckpointStates.Exported, exp.GetProperty("state").GetString());

            var abort = await cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointAbort,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}","reason":"after-export"}""").RootElement,
                CancellationToken.None);
            Assert.Equal(CheckpointStates.Exported, abort.GetProperty("state").GetString());
            Assert.Equal(SessionLifecycle.Ready, abort.GetProperty("session_state").GetString());
            Assert.Equal(SessionLifecycle.Ready, state.Snapshot().LifecycleState);

            var durable = await store.GetAsync(checkpointId);
            Assert.True(durable.IsOk);
            Assert.Equal(CheckpointStates.Exported, durable.Value!.State);

            var range = await journal.ReadRangeAsync(0, classes: null, budget: 50, CancellationToken.None);
            Assert.True(range.IsOk);
            Assert.DoesNotContain(range.Value, r =>
                r.Type == ProtocolEventTypes.CheckpointLifecycle &&
                r.PayloadJson.Contains("\"state\":\"aborted\"", StringComparison.Ordinal));
        }
        finally
        {
            _exportHooks.ExportIoDelay = null;
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Save_aborted_cannot_be_overwritten_to_exported()
    {
        var store = new FileCheckpointStore(_paths);
        var prepared = new CheckpointRecord
        {
            CheckpointId = "cp_cas_abort",
            SessionId = "sess",
            State = CheckpointStates.Prepared,
            BarrierSeq = 0,
            NextSeqAtPrepare = 1,
            SessionFingerprint = "fp",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        Assert.True((await store.SaveAsync(prepared)).IsOk);

        var aborted = prepared with { State = CheckpointStates.Aborted };
        Assert.True((await store.SaveAsync(aborted)).IsOk);

        var exported = prepared with { State = CheckpointStates.Exported };
        var save = await store.SaveAsync(exported);
        Assert.False(save.IsOk);
        Assert.Equal(RuntimePersistenceError.ConflictError, save.Error.Code);

        var loaded = await store.GetAsync(prepared.CheckpointId);
        Assert.True(loaded.IsOk);
        Assert.Equal(CheckpointStates.Aborted, loaded.Value!.State);
    }

    [Fact]
    public async Task Export_list_failure_marks_transfer_incomplete()
    {
        var project = Path.Combine(_dir, "list-fail");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "ok.txt"), "ok");

        var store = new InMemoryCheckpointStore(_paths.StateDirectory);
        var service = new DefaultCheckpointService(store, new DefaultCheckpointArtifactBuilder(exportHooks: _exportHooks, walkHooks: _walkHooks));
        var session = new SessionState
        {
            Id = SessionId.New("list-fail"),
            Name = "list-fail",
            Binding = new AtomicBinding { ProjectRoot = project },
        };
        var record = MakePrepared("cp_list_fail", session, project);
        Assert.True((await service.SavePreparedAsync(record)).IsOk);

        _walkHooks.ThrowOnList = true;
        try
        {
            var material = await service.ExportAsync(record, session);
            Assert.True(material.IsOk, material.IsOk ? null : material.Error.Message);
            Assert.True(material.Value.TransferIncomplete);
            Assert.Contains(material.Value.Record.Warnings, w =>
                w.Contains("enumerate", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _walkHooks.ThrowOnList = false;
        }
    }

    [Fact]
    public void ListEntryNames_invalid_fd_throws()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        // Must not own fd 0: IntPtr.Zero is stdin. Dispose/GC of that handle
        // closed testhost stdin and aborted aggregate Checkpoint runs (EBADF /
        // ResumeThread) on later Process.Start.
        using var invalid = new Microsoft.Win32.SafeHandles.SafeFileHandle(
            new IntPtr(-1), ownsHandle: true);
        Assert.Throws<IOException>(() =>
            NoFollowWorkspaceWalker.ListEntryNames(invalid, _dir));
    }

    [Fact]
    public async Task Invalid_directory_handle_dispose_does_not_break_process_start()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        for (var i = 0; i < 8; i++)
        {
            Assert.False(NoFollowWorkspaceWalker.TryOpenDirectory(
                Path.Combine(_dir, "missing-fd-" + i), out var handle, out _));
            handle.Dispose();
        }

        using var invalid = new Microsoft.Win32.SafeHandles.SafeFileHandle(
            new IntPtr(-1), ownsHandle: true);
        Assert.Throws<IOException>(() =>
            NoFollowWorkspaceWalker.ListEntryNames(invalid, _dir));
        invalid.Dispose();

        var repo = Path.Combine(_dir, "fd-safe-git");
        Directory.CreateDirectory(repo);
        Assert.True(await RunGitAsync(repo, "init", "-b", "main"));
    }

    [Fact(Timeout = 20000)]
    public async Task Export_after_prepare_root_swap_to_outside_git_never_follows()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var project = Path.Combine(_dir, "prep-swap");
        var outside = Path.Combine(_dir, "prep-swap-outside");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "inside.txt"), "inside-ok");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET_PREPARE_SWAP");
        Assert.True(await InitGitRepoAsync(outside, "outside-prepare-swap"));
        var outsideHead = await ReadGitHeadAsync(outside);
        Assert.False(string.IsNullOrWhiteSpace(outsideHead));

        var (cp, state, journal, store) = await CreatePlaneAsync(
            "prep-swap", new ProcessGitWorkspaceProbe(hooks: _gitHooks));
        state.UpdateSession(s => s with
        {
            Binding = (s.Binding ?? new AtomicBinding()) with { ProjectRoot = project },
        });

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        var pinned = await store.GetAsync(checkpointId);
        Assert.True(pinned.IsOk);
        Assert.NotNull(pinned.Value!.ProjectRootDevice);
        Assert.NotNull(pinned.Value.ProjectRootInode);
        Assert.False(pinned.Value.ProjectRootWasSymlink);

        Directory.Delete(project, recursive: true);
        Directory.CreateSymbolicLink(project, outside);

        var exp = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);
        Assert.Equal(CheckpointStates.Exported, exp.GetProperty("state").GetString());
        Assert.True(exp.GetProperty("transfer_incomplete").GetBoolean());

        var wsDir = Path.Combine(store.GetCheckpointDirectory(checkpointId), "artifacts", "workspace");
        if (Directory.Exists(wsDir))
        {
            foreach (var f in Directory.EnumerateFiles(wsDir, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain("SECRET_PREPARE_SWAP", await File.ReadAllTextAsync(f));
        }

        var gitMeta = Path.Combine(store.GetCheckpointDirectory(checkpointId), "artifacts", "git_meta.json");
        if (File.Exists(gitMeta))
        {
            using var gitDoc = JsonDocument.Parse(await File.ReadAllTextAsync(gitMeta));
            var head = gitDoc.RootElement.TryGetProperty("head", out var h) ? h.GetString() : null;
            Assert.NotEqual(outsideHead, head);
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public void Classify_fifo_is_other_not_regular()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var project = Path.Combine(_dir, "fifo-classify");
        Directory.CreateDirectory(project);
        var fifo = Path.Combine(project, "x.fifo");
        Assert.Equal(0, MkFifo(fifo, 0x1B6));

        Assert.True(NoFollowWorkspaceWalker.TryOpenWalkRoot(project, out var root, out var detail), detail);
        using (root)
        {
            var kind = NoFollowWorkspaceWalker.ClassifyEntry(root, "x.fifo", out _);
            Assert.Equal(NoFollowWorkspaceWalker.EntryKind.OtherOrUnavailable, kind);

            var status = NoFollowWorkspaceWalker.TryReadFile(
                root, "x.fifo", 1024, CancellationToken.None, out var bytes, out _, out _);
            Assert.Equal(NoFollowFileReader.OpenStatus.NotRegular, status);
            Assert.Empty(bytes);
        }
    }

    [Fact]
    public async Task Export_skips_file_over_production_max_without_loading_it()
    {
        var project = Path.Combine(_dir, "huge-project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "hello.txt"), "hello");
        var hugePath = Path.Combine(project, "huge.bin");
        using (var fs = new FileStream(hugePath, FileMode.Create, FileAccess.Write))
            fs.SetLength(CheckpointBudgets.MaxFileBytes + 4096);

        var store = new InMemoryCheckpointStore(_paths.StateDirectory);
        var service = new DefaultCheckpointService(store, new DefaultCheckpointArtifactBuilder(exportHooks: _exportHooks, walkHooks: _walkHooks));
        var session = new SessionState
        {
            Id = SessionId.New("huge"),
            Name = "huge",
            Binding = new AtomicBinding { ProjectRoot = project },
        };
        var record = MakePrepared("cp_huge", session, project);
        Assert.True((await service.SavePreparedAsync(record)).IsOk);

        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var material = await service.ExportAsync(record, session);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(material.IsOk, material.IsOk ? null : material.Error.Message);
        Assert.True(material.Value.TransferIncomplete);
        Assert.Contains(material.Value.Record.Warnings, w =>
            w.Contains("oversize", StringComparison.OrdinalIgnoreCase)
            && w.Contains("huge.bin", StringComparison.Ordinal));

        var wsDir = Path.Combine(store.GetCheckpointDirectory(record.CheckpointId), "artifacts", "workspace");
        Assert.True(File.Exists(Path.Combine(wsDir, "hello.txt")));
        Assert.False(File.Exists(Path.Combine(wsDir, "huge.bin")));
        Assert.True(
            allocated < CheckpointBudgets.MaxFileBytes / 2,
            "export allocated " + allocated + " bytes; must not materialize the 100 MiB+ file");
    }

    [Fact]
    public async Task Prepare_missing_project_root_does_not_follow_later_symlink()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var missing = Path.Combine(_dir, "missing-at-prepare");
        var outside = Path.Combine(_dir, "missing-outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET_MISSING_ROOT");

        var (cp, state, journal, store) = await CreatePlaneAsync("missing-root");
        state.UpdateSession(s => s with
        {
            Binding = (s.Binding ?? new AtomicBinding()) with { ProjectRoot = missing },
        });

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        var prepared = await store.GetAsync(checkpointId);
        Assert.True(prepared.IsOk);
        Assert.False(prepared.Value!.IncludeWorkspaceFiles);
        Assert.True(prepared.Value.TransferIncomplete);
        Assert.False(prepared.Value.HasProjectRootPin);

        Directory.CreateSymbolicLink(missing, outside);

        var exp = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);
        Assert.Equal(CheckpointStates.Exported, exp.GetProperty("state").GetString());

        var wsDir = Path.Combine(store.GetCheckpointDirectory(checkpointId), "artifacts", "workspace");
        Assert.False(Directory.Exists(wsDir) &&
                     Directory.EnumerateFiles(wsDir, "*", SearchOption.AllDirectories).Any());

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Prepare_pin_failure_after_classify_does_not_record_operator_symlink()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var project = Path.Combine(_dir, "pin-fail-real");
        var outside = Path.Combine(_dir, "pin-fail-outside");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "inside.txt"), "inside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET_PIN_FAIL");

        var (cp, state, journal, store) = await CreatePlaneAsync("pin-fail");
        state.UpdateSession(s => s with
        {
            Binding = (s.Binding ?? new AtomicBinding()) with { ProjectRoot = project },
        });

        _walkHooks.AfterPinClassifyHandler = path =>
        {
            if (!string.Equals(path, Path.GetFullPath(project), StringComparison.Ordinal)
                && !string.Equals(path, project, StringComparison.Ordinal))
                return;
            try
            {
                if (Directory.Exists(project) && !IsSymlink(project))
                {
                    Directory.Delete(project, recursive: true);
                    Directory.CreateSymbolicLink(project, outside);
                }
            }
            catch
            {
                // best-effort
            }
        };

        try
        {
            var prep = await cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointPrepare,
                JsonDocument.Parse("""{"include_workspace_files":true}""").RootElement,
                CancellationToken.None);
            var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;
            var prepared = await store.GetAsync(checkpointId);
            Assert.True(prepared.IsOk);
            Assert.False(prepared.Value!.IncludeWorkspaceFiles);
            Assert.True(prepared.Value.TransferIncomplete);
            Assert.False(prepared.Value.HasProjectRootPin);
            Assert.NotEqual(true, prepared.Value.ProjectRootWasSymlink);

            var exp = await cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointExport,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
                CancellationToken.None);
            Assert.Equal(CheckpointStates.Exported, exp.GetProperty("state").GetString());

            var wsDir = Path.Combine(store.GetCheckpointDirectory(checkpointId), "artifacts", "workspace");
            if (Directory.Exists(wsDir))
            {
                foreach (var f in Directory.EnumerateFiles(wsDir, "*", SearchOption.AllDirectories))
                    Assert.DoesNotContain("SECRET_PIN_FAIL", await File.ReadAllTextAsync(f));
            }
        }
        finally
        {
            _walkHooks.AfterPinClassifyHandler = null;
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Unpinned_prepared_row_does_not_follow_preexisting_symlink()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var real = Path.Combine(_dir, "unpinned-real");
        var link = Path.Combine(_dir, "unpinned-link");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "secret.txt"), "SECRET_UNPINNED");
        Directory.CreateSymbolicLink(link, real);

        var store = new InMemoryCheckpointStore(_paths.StateDirectory);
        var service = new DefaultCheckpointService(
            store, new DefaultCheckpointArtifactBuilder(new ProcessGitWorkspaceProbe(hooks: _gitHooks), _exportHooks, _walkHooks));
        var session = new SessionState
        {
            Id = SessionId.New("unpinned"),
            Name = "unpinned",
            Binding = new AtomicBinding { ProjectRoot = link },
        };
        var record = new CheckpointRecord
        {
            CheckpointId = "cp_unpinned",
            SessionId = session.Id.Value,
            State = CheckpointStates.Prepared,
            BarrierSeq = 0,
            NextSeqAtPrepare = 1,
            SessionFingerprint = SessionGraphFingerprint.Compute(session),
            IncludeWorkspaceFiles = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        Assert.False(record.HasProjectRootPin);
        Assert.True((await service.SavePreparedAsync(record)).IsOk);

        var material = await service.ExportAsync(record, session);
        Assert.True(material.IsOk, material.IsOk ? null : material.Error.Message);
        Assert.True(material.Value.TransferIncomplete);

        var wsDir = Path.Combine(store.GetCheckpointDirectory(record.CheckpointId), "artifacts", "workspace");
        Assert.False(File.Exists(Path.Combine(wsDir, "secret.txt")));
        if (Directory.Exists(wsDir))
        {
            foreach (var f in Directory.EnumerateFiles(wsDir, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain("SECRET_UNPINNED", await File.ReadAllTextAsync(f));
        }

        Assert.False(DefaultCheckpointArtifactBuilder.CanTrustPathForGit(link, record, null));
    }

    [Fact]
    public async Task Prepare_without_workspace_files_still_pins_for_git()
    {
        var project = Path.Combine(_dir, "git-only-pin");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "a.txt"), "a");
        Assert.True(await InitGitRepoAsync(project, "git-only"));
        var head = await ReadGitHeadAsync(project);

        var (cp, state, journal, store) = await CreatePlaneAsync(
            "git-only-pin", new ProcessGitWorkspaceProbe(hooks: _gitHooks));
        state.UpdateSession(s => s with
        {
            Binding = (s.Binding ?? new AtomicBinding()) with { ProjectRoot = project },
        });

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;
        var prepared = await store.GetAsync(checkpointId);
        Assert.True(prepared.IsOk);
        Assert.False(prepared.Value!.IncludeWorkspaceFiles);
        Assert.True(prepared.Value.HasProjectRootPin);

        var exp = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);
        Assert.Equal(CheckpointStates.Exported, exp.GetProperty("state").GetString());

        var gitMeta = Path.Combine(store.GetCheckpointDirectory(checkpointId), "artifacts", "git_meta.json");
        Assert.True(File.Exists(gitMeta));
        using var gitDoc = JsonDocument.Parse(await File.ReadAllTextAsync(gitMeta));
        Assert.Equal(head, gitDoc.RootElement.GetProperty("head").GetString());

        await journal.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Export_git_probe_after_walk_swap_never_follows_outside_head()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var project = Path.Combine(_dir, "git-mid-swap");
        var outside = Path.Combine(_dir, "git-mid-swap-outside");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "inside.txt"), "inside");
        Assert.True(await InitGitRepoAsync(project, "inside-repo"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET_GIT_SWAP");
        Assert.True(await InitGitRepoAsync(outside, "outside-repo"));
        var outsideHead = await ReadGitHeadAsync(outside);
        var insideHead = await ReadGitHeadAsync(project);
        Assert.False(string.IsNullOrWhiteSpace(outsideHead));
        Assert.False(string.IsNullOrWhiteSpace(insideHead));
        Assert.NotEqual(insideHead, outsideHead);

        var (cp, state, journal, store) = await CreatePlaneAsync(
            "git-mid-swap", new ProcessGitWorkspaceProbe(hooks: _gitHooks));
        state.UpdateSession(s => s with
        {
            Binding = (s.Binding ?? new AtomicBinding()) with { ProjectRoot = project },
        });

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        _exportHooks.BeforeGitProbeHandler = () =>
            SwapProjectRootToOutside(project, outside, displace: false);

        try
        {
            var exp = await cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointExport,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
                CancellationToken.None);
            Assert.Equal(CheckpointStates.Exported, exp.GetProperty("state").GetString());
            Assert.True(exp.GetProperty("transfer_incomplete").GetBoolean());

            var gitMeta = Path.Combine(store.GetCheckpointDirectory(checkpointId), "artifacts", "git_meta.json");
            var gitMetaExists = File.Exists(gitMeta);
            string? head = null;
            if (gitMetaExists)
            {
                using var gitDoc = JsonDocument.Parse(await File.ReadAllTextAsync(gitMeta));
                head = gitDoc.RootElement.TryGetProperty("head", out var h) && h.ValueKind == JsonValueKind.String
                    ? h.GetString()
                    : null;
                Assert.NotEqual(outsideHead, head);
            }

            Assert.True(
                !gitMetaExists ^ string.Equals(head, insideHead, StringComparison.Ordinal),
                "git_meta must be absent or record the inside HEAD; never a silent complete miss");
        }
        finally
        {
            _exportHooks.BeforeGitProbeHandler = null;
        }

        await journal.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Export_git_probe_process_start_swap_never_stores_outside_head()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var project = Path.Combine(_dir, "git-spawn-swap");
        var outside = Path.Combine(_dir, "git-spawn-swap-outside");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "inside.txt"), "inside");
        Assert.True(await InitGitRepoAsync(project, "inside-repo"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET_GIT_SPAWN_SWAP");
        Assert.True(await InitGitRepoAsync(outside, "outside-repo"));
        var outsideHead = await ReadGitHeadAsync(outside);
        var insideHead = await ReadGitHeadAsync(project);
        Assert.False(string.IsNullOrWhiteSpace(outsideHead));
        Assert.False(string.IsNullOrWhiteSpace(insideHead));
        Assert.NotEqual(insideHead, outsideHead);

        var (cp, state, journal, store) = await CreatePlaneAsync(
            "git-spawn-swap", new ProcessGitWorkspaceProbe(hooks: _gitHooks));
        state.UpdateSession(s => s with
        {
            Binding = (s.Binding ?? new AtomicBinding()) with { ProjectRoot = project },
        });

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        // After path publish / last cwd trust, replace the name with an outside
        // repo. chdir of a vnode path would follow; /dev/fd/N must not.
        _gitHooks.BeforeProcessStartHandler = _ =>
            SwapProjectRootToOutside(project, outside, displace: true);
        _exportHooks.AfterGitProbePathPublishedHandler = path =>
        {
            Assert.True(
                NoFollowWorkspaceWalker.IsKernelFdDirectoryPath(path),
                "git cwd must be the pinned directory fd, not a followable vnode path");
        };

        try
        {
            var exp = await cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointExport,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
                CancellationToken.None);
            Assert.Equal(CheckpointStates.Exported, exp.GetProperty("state").GetString());

            var gitMeta = Path.Combine(store.GetCheckpointDirectory(checkpointId), "artifacts", "git_meta.json");
            Assert.True(File.Exists(gitMeta));
            using var gitDoc = JsonDocument.Parse(await File.ReadAllTextAsync(gitMeta));
            var head = gitDoc.RootElement.TryGetProperty("head", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString()
                : null;
            Assert.NotEqual(outsideHead, head);
            Assert.Equal(insideHead, head);
            Assert.False(gitDoc.RootElement.GetProperty("incomplete").GetBoolean());
        }
        finally
        {
            _gitHooks.BeforeProcessStartHandler = null;
            _exportHooks.AfterGitProbePathPublishedHandler = null;
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public void GitProbeEnvironment_drops_repo_overrides_and_forces_noninteractive()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/u",
            ["GIT_DIR"] = "/tmp/outside/.git",
            ["GIT_WORK_TREE"] = "/tmp/outside",
            ["GIT_OBJECT_DIRECTORY"] = "/tmp/outside/.git/objects",
            ["GIT_INDEX_FILE"] = "/tmp/outside/.git/index",
            ["GIT_COMMON_DIR"] = "/tmp/outside/.git",
            ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = "/tmp/other/.git/objects",
            ["GIT_TERMINAL_PROMPT"] = "1",
            ["GIT_OPTIONAL_LOCKS"] = "1",
        };

        GitProbeEnvironment.ApplyTo(env);

        foreach (var name in GitProbeEnvironment.RepoOverrideNames)
            Assert.False(env.ContainsKey(name), name + " must be dropped");
        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("0", env["GIT_OPTIONAL_LOCKS"]);
        Assert.Equal("/usr/bin", env["PATH"]);
        Assert.Equal("/home/u", env["HOME"]);

        var pairs = GitProbeEnvironment.BuildParentPairs(new Dictionary<string, string>
        {
            ["PATH"] = "/bin",
            ["GIT_DIR"] = "/tmp/outside/.git",
            ["git_work_tree"] = "/tmp/outside",
            ["GIT_TERMINAL_PROMPT"] = "1",
        });
        Assert.Contains("PATH=/bin", pairs);
        Assert.Contains("GIT_TERMINAL_PROMPT=0", pairs);
        Assert.Contains("GIT_OPTIONAL_LOCKS=0", pairs);
        Assert.DoesNotContain(pairs, p =>
            p.StartsWith("GIT_DIR=", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("GIT_WORK_TREE=", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("git_work_tree=", StringComparison.OrdinalIgnoreCase)
            || p.Equals("GIT_TERMINAL_PROMPT=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessGitWorkspaceProbe_ignores_parent_GIT_DIR_override()
    {
        var inside = Path.Combine(_dir, "probe-git-dir-inside");
        var outside = Path.Combine(_dir, "probe-git-dir-outside");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "inside.txt"), "inside");
        Assert.True(await InitGitRepoAsync(inside, "inside-repo"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET_GIT_DIR");
        Assert.True(await InitGitRepoAsync(outside, "outside-repo"));
        var insideHead = await ReadGitHeadAsync(inside);
        var outsideHead = await ReadGitHeadAsync(outside);
        Assert.False(string.IsNullOrWhiteSpace(insideHead));
        Assert.False(string.IsNullOrWhiteSpace(outsideHead));
        Assert.NotEqual(insideHead, outsideHead);
        Assert.True(NoFollowWorkspaceWalker.TryPinWalkRoot(
            inside, out var dev, out var ino, out _, out _));

        var previous = PushGitRepoOverrides(outside);
        try
        {
            var probe = new ProcessGitWorkspaceProbe(hooks: _gitHooks);
            var result = await probe.ProbeAsync(inside, expectedDevice: dev, expectedInode: ino);
            Assert.True(result.IsOk, result.IsOk ? null : result.Error.Message);
            Assert.NotEqual(outsideHead, result.Value.Head);
            Assert.True(
                string.Equals(result.Value.Head, insideHead, StringComparison.Ordinal)
                || (result.Value.Incomplete && result.Value.Head is null),
                "GIT_DIR must not report the outside HEAD; got " + (result.Value.Head ?? "<null>"));
        }
        finally
        {
            PopGitRepoOverrides(previous);
        }
    }

    [Fact(Timeout = 20000)]
    public async Task Export_pinned_clean_repo_ignores_parent_GIT_DIR()
    {
        var project = Path.Combine(_dir, "export-git-dir");
        var outside = Path.Combine(_dir, "export-git-dir-outside");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "app.txt"), "inside-body");
        Assert.True(await InitGitRepoAsync(project, "inside-export"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET_EXPORT_GIT_DIR");
        Assert.True(await InitGitRepoAsync(outside, "outside-export"));
        var insideHead = await ReadGitHeadAsync(project);
        var outsideHead = await ReadGitHeadAsync(outside);
        Assert.False(string.IsNullOrWhiteSpace(insideHead));
        Assert.False(string.IsNullOrWhiteSpace(outsideHead));
        Assert.NotEqual(insideHead, outsideHead);

        var (cp, state, journal, store) = await CreatePlaneAsync(
            "export-git-dir", new ProcessGitWorkspaceProbe(hooks: _gitHooks));
        state.UpdateSession(s => s with
        {
            Binding = (s.Binding ?? new AtomicBinding()) with { ProjectRoot = project },
        });

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true,"include_scrollback":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;
        var prepared = await store.GetAsync(checkpointId);
        Assert.True(prepared.IsOk);
        Assert.True(prepared.Value!.HasProjectRootPin);

        var previous = PushGitRepoOverrides(outside);
        try
        {
            var exp = await cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointExport,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
                CancellationToken.None);
            Assert.Equal(CheckpointStates.Exported, exp.GetProperty("state").GetString());

            var gitMeta = Path.Combine(
                store.GetCheckpointDirectory(checkpointId), "artifacts", "git_meta.json");
            Assert.True(File.Exists(gitMeta));
            using var gitDoc = JsonDocument.Parse(await File.ReadAllTextAsync(gitMeta));
            var head = gitDoc.RootElement.TryGetProperty("head", out var h)
                && h.ValueKind == JsonValueKind.String
                ? h.GetString()
                : null;
            var incomplete = gitDoc.RootElement.GetProperty("incomplete").GetBoolean();
            Assert.NotEqual(outsideHead, head);
            Assert.True(
                string.Equals(head, insideHead, StringComparison.Ordinal)
                || (incomplete && head is null),
                "GIT_DIR must not ship the outside HEAD; got " + (head ?? "<null>"));
        }
        finally
        {
            PopGitRepoOverrides(previous);
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task ProcessGitWorkspaceProbe_vnode_swap_before_start_discards_outside_head()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var inside = Path.Combine(_dir, "probe-vnode-inside");
        var outside = Path.Combine(_dir, "probe-vnode-outside");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "inside.txt"), "inside");
        Assert.True(await InitGitRepoAsync(inside, "inside-repo"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET_VNODE");
        Assert.True(await InitGitRepoAsync(outside, "outside-repo"));
        var outsideHead = await ReadGitHeadAsync(outside);
        Assert.False(string.IsNullOrWhiteSpace(outsideHead));
        Assert.True(NoFollowWorkspaceWalker.TryPinWalkRoot(
            inside, out var dev, out var ino, out _, out _));

        _gitHooks.BeforeProcessStartHandler = _ =>
            SwapProjectRootToOutside(inside, outside, displace: true);

        try
        {
            var probe = new ProcessGitWorkspaceProbe(hooks: _gitHooks);
            var result = await probe.ProbeAsync(inside, expectedDevice: dev, expectedInode: ino);
            Assert.True(result.IsOk);
            Assert.True(result.Value.Incomplete);
            Assert.NotEqual(outsideHead, result.Value.Head);
            Assert.Null(result.Value.Head);
        }
        finally
        {
            _gitHooks.BeforeProcessStartHandler = null;
        }
    }

    [Fact]
    public async Task Sqlite_fallback_without_sidecar_still_returns_pin()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var session = new SessionState
        {
            Id = SessionId.New("pin-sql"),
            Name = "pin-sql",
            Binding = new AtomicBinding { ProjectRoot = _workspace },
        };
        var sessionStore = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await sessionStore.SaveAsync(session)).IsOk);

        var store = new FileCheckpointStore(_paths);
        var record = MakePrepared("cp_pin_sql", session, _workspace);
        Assert.True(record.HasProjectRootPin);
        Assert.True((await store.SaveAsync(record)).IsOk);

        var sidecar = Path.Combine(store.GetCheckpointDirectory(record.CheckpointId), "record.json");
        Assert.True(File.Exists(sidecar));
        File.Delete(sidecar);

        var loaded = await store.GetAsync(record.CheckpointId);
        Assert.True(loaded.IsOk);
        Assert.NotNull(loaded.Value);
        Assert.True(loaded.Value!.HasProjectRootPin);
        Assert.Equal(record.ProjectRootDevice, loaded.Value.ProjectRootDevice);
        Assert.Equal(record.ProjectRootInode, loaded.Value.ProjectRootInode);
        Assert.Equal(record.ProjectRootWasSymlink, loaded.Value.ProjectRootWasSymlink);
    }

    [Fact]
    public async Task Abort_without_id_demotes_prepared_row()
    {
        var (cp, state, journal, store) = await CreatePlaneAsync("abort-no-id");

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        var abort = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointAbort,
            JsonDocument.Parse("""{}""").RootElement,
            CancellationToken.None);
        Assert.Equal(CheckpointStates.Aborted, abort.GetProperty("state").GetString());
        Assert.Equal(SessionLifecycle.Ready, state.Snapshot().LifecycleState);

        var loaded = await store.GetAsync(checkpointId);
        Assert.True(loaded.IsOk);
        Assert.Equal(CheckpointStates.Aborted, loaded.Value!.State);

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointExport,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Export_of_prepared_requires_frozen_session()
    {
        var (cp, state, journal, store) = await CreatePlaneAsync("export-unfrozen");

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointExport,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);

        var durable = await store.GetAsync(checkpointId);
        Assert.True(durable.IsOk);
        Assert.Equal(CheckpointStates.Prepared, durable.Value!.State);

        await journal.DisposeAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task Export_fingerprint_recheck_before_commit_conflicts()
    {
        var (cp, state, journal, store) = await CreatePlaneAsync("fp-recheck");

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        _exportHooks.ExportIoDelay = _ =>
        {
            state.UpdateSession(s => s with
            {
                PlacementGeneration = s.PlacementGeneration + 3,
                Binding = (s.Binding ?? new AtomicBinding()) with { RunId = "mid_export_mut" },
            });
            return Task.CompletedTask;
        };

        try
        {
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.RuntimeCheckpointExport,
                    JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.CheckpointConflict, ex.Code);
            Assert.Equal(SessionLifecycle.Ready, state.Snapshot().LifecycleState);

            var durable = await store.GetAsync(checkpointId);
            Assert.True(durable.IsOk);
            Assert.Equal(CheckpointStates.Conflict, durable.Value!.State);
        }
        finally
        {
            _exportHooks.ExportIoDelay = null;
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task ProcessGitWorkspaceProbe_symlink_root_does_not_follow()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var real = Path.Combine(_dir, "probe-real");
        var link = Path.Combine(_dir, "probe-link");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(link, real);

        var probe = new ProcessGitWorkspaceProbe(hooks: _gitHooks);
        var result = await probe.ProbeAsync(link);
        Assert.True(result.IsOk);
        Assert.True(result.Value.Incomplete);
        Assert.Contains("symlink", result.Value.Warning ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Value.Head);
    }

    private static CheckpointRecord MakePrepared(
        string id, SessionState session, string projectRoot, bool includeWorkspace = true)
    {
        ulong? device = null;
        ulong? inode = null;
        bool? was = null;
        if (NoFollowWorkspaceWalker.TryPinWalkRoot(
                projectRoot, out var d, out var i, out var w, out _))
        {
            device = d;
            inode = i;
            was = w;
        }

        return new CheckpointRecord
        {
            CheckpointId = id,
            SessionId = session.Id.Value,
            State = CheckpointStates.Prepared,
            BarrierSeq = 0,
            NextSeqAtPrepare = 1,
            SessionFingerprint = SessionGraphFingerprint.Compute(session),
            IncludeWorkspaceFiles = includeWorkspace,
            CreatedAt = DateTimeOffset.UtcNow,
            ProjectRootDevice = device,
            ProjectRootInode = inode,
            ProjectRootWasSymlink = was,
        };
    }

    private static async Task<bool> InitGitRepoAsync(string cwd, string commitMessage)
    {
        if (!await RunGitAsync(cwd, "init", "-b", "main"))
            return false;
        if (!await RunGitAsync(cwd, "config", "user.email", "test@example.com"))
            return false;
        if (!await RunGitAsync(cwd, "config", "user.name", "Test"))
            return false;
        if (!await RunGitAsync(cwd, "add", "."))
            return false;
        return await RunGitAsync(cwd, "commit", "-m", commitMessage);
    }

    private static async Task<string?> ReadGitHeadAsync(string cwd)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        proc.StartInfo.ArgumentList.Add("rev-parse");
        proc.StartInfo.ArgumentList.Add("HEAD");
        GitProbeEnvironment.ApplyTo(proc.StartInfo.Environment);
        if (!proc.Start())
            return null;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0 ? stdout.Trim() : null;
    }

    private static async Task<bool> RunGitAsync(string cwd, params string[] args)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            proc.StartInfo.ArgumentList.Add(a);
        GitProbeEnvironment.ApplyTo(proc.StartInfo.Environment);
        if (!proc.Start())
            return false;
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0;
    }

    private static Dictionary<string, string?> PushGitRepoOverrides(string outsideWorkTree)
    {
        var gitDir = Path.Combine(outsideWorkTree, ".git");
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_DIR"] = gitDir,
            ["GIT_WORK_TREE"] = outsideWorkTree,
            ["GIT_OBJECT_DIRECTORY"] = Path.Combine(gitDir, "objects"),
            ["GIT_INDEX_FILE"] = Path.Combine(gitDir, "index"),
            ["GIT_COMMON_DIR"] = gitDir,
            ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = Path.Combine(gitDir, "objects"),
        };

        var previous = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var kv in values)
        {
            previous[kv.Key] = Environment.GetEnvironmentVariable(kv.Key);
            Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }

        return previous;
    }

    private static void PopGitRepoOverrides(Dictionary<string, string?> previous)
    {
        foreach (var kv in previous)
            Environment.SetEnvironmentVariable(kv.Key, kv.Value);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string pathname, int mode);

    private static bool IsSymlink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Replace <paramref name="projectRoot"/> with a symlink to <paramref name="outside"/>.
    /// When <paramref name="displace"/> is true, rename the pinned directory so a held
    /// fd still names the original inode. Otherwise delete the tree (open-time refuse).
    /// </summary>
    private void ResetTestHooks()
    {
        _exportHooks.Reset();
        _walkHooks.Reset();
        _gitHooks.Reset();
    }

    private static void SwapProjectRootToOutside(string projectRoot, string outside, bool displace)
    {
        try
        {
            if (!Directory.Exists(projectRoot) || IsSymlink(projectRoot))
                return;

            if (displace)
            {
                var displaced = projectRoot + ".pinned";
                if (Directory.Exists(displaced))
                    Directory.Delete(displaced, recursive: true);
                Directory.Move(projectRoot, displaced);
            }
            else
            {
                Directory.Delete(projectRoot, recursive: true);
            }

            Directory.CreateSymbolicLink(projectRoot, outside);
        }
        catch
        {
            // best-effort adversarial swap
        }
    }

    /// <summary>Test builder that enforces a tiny per-file budget.</summary>
    private sealed class BudgetedArtifactBuilder : ICheckpointArtifactBuilder
    {
        private readonly long _maxFileBytes;
        private readonly DefaultCheckpointArtifactBuilder _inner = new();

        public BudgetedArtifactBuilder(long maxFileBytes) => _maxFileBytes = maxFileBytes;

        public Task<RuntimeResult<CheckpointArtifactBuildResult>> BuildAsync(
            CheckpointArtifactBuildRequest request, CancellationToken ct = default) =>
            _inner.BuildAsync(request with
            {
                MaxFileBytes = _maxFileBytes,
                MaxWorkspaceBytes = CheckpointBudgets.MaxWorkspaceBytes,
            }, ct);

        public bool TryPinProjectRoot(
            string? projectRoot,
            out ulong device,
            out ulong inode,
            out bool wasSymlink) =>
            _inner.TryPinProjectRoot(projectRoot, out device, out inode, out wasSymlink);
    }
}
