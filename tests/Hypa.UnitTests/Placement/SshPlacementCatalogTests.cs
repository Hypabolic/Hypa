using System.Text.Json;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;
using Xunit;

namespace Hypa.UnitTests.Placement;

public sealed class SshPlacementCatalogTests
{
    private const string MuxAgents = "mux_agents";

    [Fact]
    public async Task Add_invalid_target_fails_before_preparation()
    {
        var dir = CreateStore();
        var catalog = CreateCatalog(dir, new StubPreparation());
        var outcome = await catalog.AddAsync(new SshPlacementAddRequest
        {
            Owner = Owner(),
            Label = "Build",
            Target = "user:pass@host",
            Session = "agents",
        });
        Assert.False(outcome.Ok);
        Assert.Equal(PlacementReasons.ProfileInvalid, outcome.Reason);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task Add_failed_preparation_persists_no_row()
    {
        var dir = CreateStore();
        var catalog = CreateCatalog(
            dir,
            new StubPreparation
            {
                Outcome = PlacementOutcome<MuxIdentity>.Failure(
                    PlacementReasons.PreparationFailed,
                    "Remote preparation failed for Build. Placement was not saved."),
            });
        var outcome = await catalog.AddAsync(new SshPlacementAddRequest
        {
            Owner = Owner(),
            Label = "Build",
            Target = "user@dev",
            Session = "agents",
        });
        Assert.False(outcome.Ok);
        Assert.Equal(PlacementReasons.PreparationFailed, outcome.Reason);
        var store = new FilePlacementDirectoryStore(dir);
        var snapshot = await store.LoadAsync();
        Assert.Empty(snapshot.Placements);
    }

    [Fact]
    public async Task Add_success_writes_schema_two_with_ssh_field()
    {
        var dir = CreateStore();
        var catalog = CreateCatalog(dir, StubPreparation.Ready(MuxAgents));
        var outcome = await catalog.AddAsync(new SshPlacementAddRequest
        {
            Owner = Owner(),
            Label = "Build",
            Target = "user@dev",
            Session = "agents",
        });
        Assert.True(outcome.Ok);
        Assert.NotNull(outcome.Value?.Ssh);
        Assert.True(outcome.Value.Ssh.Enabled);
        var json = await File.ReadAllTextAsync(Path.Combine(dir, "directory.json"));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal("agents", document.RootElement.GetProperty("placements")[0].GetProperty("ssh").GetProperty("session").GetString());
    }

    [Fact]
    public async Task Duplicate_labels_keep_distinct_ids()
    {
        var dir = CreateStore();
        var catalog = CreateCatalog(dir, StubPreparation.Ready(MuxAgents));
        var first = await catalog.AddAsync(Request("Build", "user@dev", "agents"));
        var second = await catalog.AddAsync(Request("Build", "user@qa", "agents"));
        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.NotEqual(first.Value!.Id, second.Value!.Id);
    }

    [Fact]
    public async Task Rename_updates_label_and_profile_atomically()
    {
        var dir = CreateStore();
        var catalog = CreateCatalog(dir, StubPreparation.Ready(MuxAgents));
        var added = await catalog.AddAsync(Request("Build", "user@dev", "agents"));
        var renamed = await catalog.RenameAsync(Owner(), added.Value!.Id, "Renamed");
        Assert.True(renamed.Ok);
        Assert.Equal("Renamed", renamed.Value!.DisplayName);
        Assert.Equal("Renamed", renamed.Value.Ssh!.Label);
    }

    [Fact]
    public async Task Disable_keeps_row_and_sets_enabled_false()
    {
        var dir = CreateStore();
        var catalog = CreateCatalog(dir, StubPreparation.Ready(MuxAgents));
        var added = await catalog.AddAsync(Request("Build", "user@dev", "agents"));
        var disabled = await catalog.DisableAsync(Owner(), added.Value!.Id);
        Assert.True(disabled.Ok);
        Assert.False(disabled.Value!.Ssh!.Enabled);
    }

    [Fact]
    public async Task Remove_deletes_row_and_leaves_no_ssh_profile()
    {
        var dir = CreateStore();
        var catalog = CreateCatalog(dir, StubPreparation.Ready(MuxAgents));
        var added = await catalog.AddAsync(Request("Build", "user@dev", "agents"));
        var removed = await catalog.RemoveAsync(Owner(), added.Value!.Id);
        Assert.True(removed.Ok);
        var store = new FilePlacementDirectoryStore(dir);
        var snapshot = await store.LoadAsync();
        Assert.Empty(snapshot.Placements);
    }

    [Fact]
    public async Task Enable_on_non_ssh_row_returns_provider_mismatch()
    {
        var dir = CreateStore();
        var store = new FilePlacementDirectoryStore(dir);
        var directory = new PlacementDirectoryService(store);
        var registered = await directory.RegisterAsync(new PlacementRegistration
        {
            Owner = Owner(),
            DisplayName = "Peer",
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = MuxIdentity.Parse("mux_peer001"),
        });
        var catalog = CreateCatalog(dir, StubPreparation.Ready(MuxAgents));
        var enabled = await catalog.EnableAsync(Owner(), registered.Value!.Id);
        Assert.False(enabled.Ok);
        Assert.Equal(PlacementReasons.ProviderMismatch, enabled.Reason);
    }

    [Fact]
    public async Task Schema_one_record_loads_without_ssh()
    {
        var dir = CreateStore();
        var json = """
            {"schema":1,"placements":[{"placement_id":"plc_local01","owner_id":"local:test","display_name":"Local","kind":"local","mux_identity":"mux_local01","reachability":"local","last_seen":"2026-09-08T00:00:00Z"}],"placement_grants":[],"work_grants":[]}
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "directory.json"), json);
        var store = new FilePlacementDirectoryStore(dir);
        var snapshot = await store.LoadAsync();
        Assert.Single(snapshot.Placements);
        Assert.Null(snapshot.Placements[0].Ssh);
    }

    [Fact]
    public async Task Profile_mismatch_fails_closed_on_load()
    {
        var dir = CreateStore();
        var json = """
            {"schema":2,"placements":[{"placement_id":"plc_peer001","owner_id":"local:test","display_name":"Build","kind":"peer","mux_identity":"mux_agents","reachability":"unreachable","last_seen":"2026-09-08T00:00:00Z","ssh":{"id":"plc_other1","label":"Build","target":"user@dev","session":"agents","enabled":true}}],"placement_grants":[],"work_grants":[]}
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "directory.json"), json);
        var store = new FilePlacementDirectoryStore(dir);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync().AsTask());
    }

    [Fact]
    public async Task Failed_enable_keeps_row_disabled()
    {
        var dir = CreateStore();
        var catalog = CreateCatalog(
            dir,
            new SequencePreparation(
                StubPreparation.Ready(MuxAgents),
                new StubPreparation
                {
                    Outcome = PlacementOutcome<MuxIdentity>.Failure(
                        RemoteMuxReasons.ApprovalRequired,
                        "OpenSSH approval required for user@dev. Run ssh 'user@dev' in a terminal, then retry."),
                }));
        var added = await catalog.AddAsync(Request("Build", "user@dev", "agents"));
        await catalog.DisableAsync(Owner(), added.Value!.Id);
        var enabled = await catalog.EnableAsync(Owner(), added.Value.Id);
        Assert.False(enabled.Ok);
        Assert.Equal(RemoteMuxReasons.ApprovalRequired, enabled.Reason);
        var store = new FilePlacementDirectoryStore(dir);
        var snapshot = await store.LoadAsync();
        var row = Assert.Single(snapshot.Placements);
        Assert.False(row.Ssh!.Enabled);
        Assert.Equal(SshPlacementAttentionStates.ApprovalRequired, row.Ssh.Attention);
    }

    [Fact]
    public async Task Removing_last_ssh_profile_keeps_schema_two()
    {
        var dir = CreateStore();
        var catalog = CreateCatalog(dir, StubPreparation.Ready(MuxAgents));
        var added = await catalog.AddAsync(Request("Build", "user@dev", "agents"));
        var removed = await catalog.RemoveAsync(Owner(), added.Value!.Id);
        Assert.True(removed.Ok);
        var json = await File.ReadAllTextAsync(Path.Combine(dir, "directory.json"));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("schema").GetInt32());
    }

    [Fact]
    public async Task Enable_store_failure_returns_store_failed_detail()
    {
        var dir = CreateStore();
        var inner = new FilePlacementDirectoryStore(dir);
        var store = new ThrowOnSecondMutateStore(inner);
        var catalog = new SshPlacementCatalogService(store, StubPreparation.Ready(MuxAgents));
        var added = await catalog.AddAsync(Request("Build", "user@dev", "agents"));
        await catalog.DisableAsync(Owner(), added.Value!.Id);
        var enabled = await catalog.EnableAsync(Owner(), added.Value.Id);
        Assert.False(enabled.Ok);
        Assert.Equal(PlacementReasons.StoreFailed, enabled.Reason);
        Assert.Equal(
            "Remote session is ready. Placement remains disabled. Retry enable.",
            enabled.Detail);
        var snapshot = await inner.LoadAsync();
        Assert.False(snapshot.Placements[0].Ssh!.Enabled);
    }

    [Fact]
    public async Task Stale_failed_enable_does_not_disable_newer_successful_enable()
    {
        var dir = CreateStore();
        var inner = new FilePlacementDirectoryStore(dir);
        var store = new RecordingMutateStore(inner);
        var catalog = new SshPlacementCatalogService(
            store,
            new StaleEnableRacePreparation(inner, Owner()));
        var added = await catalog.AddAsync(Request("Build", "user@dev", "agents"));
        await catalog.DisableAsync(Owner(), added.Value!.Id);
        var enabled = await catalog.EnableAsync(Owner(), added.Value.Id);
        Assert.False(enabled.Ok);
        var snapshot = await inner.LoadAsync();
        var row = Assert.Single(snapshot.Placements);
        Assert.True(row.Ssh!.Enabled);
        Assert.Null(row.Ssh.Attention);
    }

    [Fact]
    public async Task Catalog_reload_detects_rename_without_adopting_selection()
    {
        var dir = CreateStore();
        var catalog = CreateCatalog(dir, StubPreparation.Ready(MuxAgents));
        var added = await catalog.AddAsync(Request("Build", "user@dev", "agents"));
        var source = new DirectorySidebarCatalogSource(dir, Owner());
        var registry = new Hypa.Cli.Mux.SshPlacementEndpointRegistry();
        var reloader = new Hypa.Cli.Mux.PlacementCatalogReloader(source, Owner(), registry);
        IReadOnlyList<Hypa.AgentRuntime.Application.Sidebar.SidebarCubeItem>? seen = null;
        reloader.Changed += (items, _) => seen = items;
        reloader.Start();
        await Task.Delay(1200);
        await catalog.RenameAsync(Owner(), added.Value!.Id, "Renamed");
        await Task.Delay(1200);
        await reloader.DisposeAsync();
        Assert.NotNull(seen);
        Assert.Contains(seen!, item => item.Name == "Renamed");
    }

    private static SshPlacementAddRequest Request(string label, string target, string session) =>
        new()
        {
            Owner = Owner(),
            Label = label,
            Target = target,
            Session = session,
        };

    private static DirectoryIdentity Owner() => DirectoryIdentity.Parse("local:test");

    private static string CreateStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-plc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ISshPlacementCatalog CreateCatalog(string dir, ISshPlacementPreparation preparation) =>
        new SshPlacementCatalogService(new FilePlacementDirectoryStore(dir), preparation);

    private sealed class StubPreparation : ISshPlacementPreparation
    {
        public PlacementOutcome<MuxIdentity> Outcome { get; init; } =
            PlacementOutcome<MuxIdentity>.Success(MuxIdentity.Parse(MuxAgents));

        public static StubPreparation Ready(string mux) =>
            new() { Outcome = PlacementOutcome<MuxIdentity>.Success(MuxIdentity.Parse(mux)) };

        public ValueTask<PlacementOutcome<MuxIdentity>> PrepareAsync(
            PeerProfile profile,
            CancellationToken cancellationToken = default) =>
            new(Outcome);
    }

    private sealed class SequencePreparation(params ISshPlacementPreparation[] steps) : ISshPlacementPreparation
    {
        private int _index;

        public async ValueTask<PlacementOutcome<MuxIdentity>> PrepareAsync(
            PeerProfile profile,
            CancellationToken cancellationToken = default)
        {
            var step = steps[Math.Min(_index, steps.Length - 1)];
            _index++;
            return await step.PrepareAsync(profile, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class RecordingMutateStore(IPlacementDirectoryStore inner) : IPlacementDirectoryStore
    {
        public ValueTask<PlacementDirectorySnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            inner.LoadAsync(cancellationToken);

        public ValueTask SaveAsync(PlacementDirectorySnapshot snapshot, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(snapshot, cancellationToken);

        public ValueTask<T> MutateAsync<T>(
            Func<PlacementDirectorySnapshot, (PlacementDirectorySnapshot Next, T Result)> mutator,
            CancellationToken cancellationToken = default) =>
            inner.MutateAsync(mutator, cancellationToken);

        public PlacementDirectoryChangeStamp ReadChangeStamp() => inner.ReadChangeStamp();
    }

    private sealed class StaleEnableRacePreparation(
        IPlacementDirectoryStore store,
        DirectoryIdentity owner) : ISshPlacementPreparation
    {
        private int _calls;

        public async ValueTask<PlacementOutcome<MuxIdentity>> PrepareAsync(
            PeerProfile profile,
            CancellationToken cancellationToken = default)
        {
            if (_calls++ == 0)
                return PlacementOutcome<MuxIdentity>.Success(MuxIdentity.Parse(MuxAgents));

            await store.MutateAsync(
                    snapshot =>
                    {
                        var placements = snapshot.Placements.ToList();
                        var index = placements.FindIndex(row => row.Ssh is not null && row.Owner == owner);
                        if (index < 0)
                            return (snapshot, false);

                        var current = placements[index];
                        placements[index] = current with
                        {
                            Ssh = current.Ssh! with { Enabled = true, Attention = null },
                        };
                        return (snapshot with { Placements = placements }, true);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return PlacementOutcome<MuxIdentity>.Failure(
                RemoteMuxReasons.ApprovalRequired,
                "stale enable failed");
        }
    }

    private sealed class ThrowOnSecondMutateStore(IPlacementDirectoryStore inner) : IPlacementDirectoryStore
    {
        private int _mutations;

        public ValueTask<PlacementDirectorySnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            inner.LoadAsync(cancellationToken);

        public ValueTask SaveAsync(PlacementDirectorySnapshot snapshot, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(snapshot, cancellationToken);

        public PlacementDirectoryChangeStamp ReadChangeStamp() => inner.ReadChangeStamp();

        public ValueTask<T> MutateAsync<T>(
            Func<PlacementDirectorySnapshot, (PlacementDirectorySnapshot Next, T Result)> mutator,
            CancellationToken cancellationToken = default)
        {
            _mutations++;
            if (_mutations >= 3)
                throw new IOException("store write failed");
            return inner.MutateAsync(mutator, cancellationToken);
        }
    }

    private sealed class DirectorySidebarCatalogSource : Hypa.Cli.Attach.Sidebar.ISidebarCubeCatalogSource
    {
        private readonly string _dir;
        private readonly DirectoryIdentity _actor;

        public DirectorySidebarCatalogSource(string dir, DirectoryIdentity actor)
        {
            _dir = dir;
            _actor = actor;
        }

        public async ValueTask<Hypa.Cli.Attach.Sidebar.SidebarCubeCatalogLoad> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            var directory = new PlacementDirectoryService(new FilePlacementDirectoryStore(_dir));
            return await Hypa.Cli.Attach.Sidebar.SidebarCubeCatalog.LoadAsync(directory, _actor, cancellationToken);
        }
    }
}
