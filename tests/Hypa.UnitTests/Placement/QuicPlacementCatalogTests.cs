using System.Text.Json;
using Hypa.Cli.Attach.Sidebar;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;
using Xunit;

namespace Hypa.UnitTests.Placement;

public sealed class QuicPlacementCatalogTests
{
    [Fact]
    public void Change_stamp_follows_file_bytes_without_a_parse()
    {
        var dir = CreateStore();
        try
        {
            var store = new FilePlacementDirectoryStore(dir);
            Assert.False(store.ReadChangeStamp().Exists);

            File.WriteAllText(store.FilePath, "a");
            var first = store.ReadChangeStamp();
            Assert.True(first.Exists);
            Assert.Equal(1, first.Length);

            File.WriteAllText(store.FilePath, "abcd");
            var second = store.ReadChangeStamp();
            Assert.Equal(4, second.Length);
            Assert.NotEqual(first, second);
            Assert.Equal(second, store.ReadChangeStamp());

            // Same length and the same write time: only the bytes differ.
            var writeTime = File.GetLastWriteTimeUtc(store.FilePath);
            File.WriteAllText(store.FilePath, "wxyz");
            File.SetLastWriteTimeUtc(store.FilePath, writeTime);
            var third = store.ReadChangeStamp();
            Assert.Equal(4, third.Length);
            Assert.NotEqual(second, third);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Add_invalid_target_fails_before_persist()
    {
        var dir = CreateStore();
        var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
        var outcome = await catalog.AddAsync(new QuicPlacementAddRequest
        {
            Owner = Owner(),
            Label = "Edge",
            Target = "user@host",
            Session = "agents",
        });
        Assert.False(outcome.Ok);
        Assert.Equal(PlacementReasons.ProfileInvalid, outcome.Reason);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task Add_success_writes_schema_two_with_quic_field()
    {
        var dir = CreateStore();
        var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
        var outcome = await catalog.AddAsync(Request("Edge", "192.168.1.10", "agents"));
        Assert.True(outcome.Ok);
        Assert.NotNull(outcome.Value?.Quic);
        Assert.True(outcome.Value.Quic.Enabled);
        var json = await File.ReadAllTextAsync(Path.Combine(dir, "directory.json"));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(
            "192.168.1.10",
            document.RootElement.GetProperty("placements")[0].GetProperty("quic").GetProperty("target").GetString());
    }

    [Fact]
    public async Task Disable_keeps_row_and_sets_enabled_false()
    {
        var dir = CreateStore();
        var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
        var added = await catalog.AddAsync(Request("Edge", "192.168.1.10", "agents"));
        var disabled = await catalog.DisableAsync(Owner(), added.Value!.Id);
        Assert.True(disabled.Ok);
        Assert.False(disabled.Value!.Quic!.Enabled);
    }

    [Fact]
    public async Task Enable_on_non_quic_row_returns_provider_mismatch()
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
        var catalog = new QuicPlacementCatalogService(store);
        var enabled = await catalog.EnableAsync(Owner(), registered.Value!.Id);
        Assert.False(enabled.Ok);
        Assert.Equal(PlacementReasons.ProviderMismatch, enabled.Reason);
    }

    [Fact]
    public async Task Schema_one_local_and_rendezvous_rows_load_without_quic()
    {
        var dir = CreateStore();
        var json = """
            {"schema":1,"revision":0,"placements":[{"placement_id":"plc_local01","owner_id":"local:test","display_name":"Local","kind":"local","mux_identity":"mux_local01","reachability":"local","last_seen":"2026-09-08T00:00:00Z"},{"placement_id":"plc_peer001","owner_id":"local:test","display_name":"Peer","kind":"peer","mux_identity":"mux_peer001","reachability":"reachable","last_seen":"2026-09-08T00:00:00Z"}],"placement_grants":[],"work_grants":[]}
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "directory.json"), json);
        var store = new FilePlacementDirectoryStore(dir);
        var snapshot = await store.LoadAsync();
        Assert.Equal(2, snapshot.Placements.Count);
        Assert.All(snapshot.Placements, row => Assert.Null(row.Quic));
    }

    [Fact]
    public async Task Dual_provider_row_fails_closed_on_load()
    {
        var dir = CreateStore();
        var json = """
            {"schema":2,"revision":0,"placements":[{"placement_id":"plc_peer001","owner_id":"local:test","display_name":"Edge","kind":"peer","mux_identity":"mux_agents","reachability":"unreachable","last_seen":"2026-09-08T00:00:00Z","ssh":{"id":"plc_peer001","label":"Edge","target":"user@dev","session":"agents","enabled":true},"quic":{"id":"plc_peer001","label":"Edge","target":"192.168.1.10","session":"agents","enabled":true}}],"placement_grants":[],"work_grants":[]}
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "directory.json"), json);
        var store = new FilePlacementDirectoryStore(dir);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync().AsTask());
    }

    [Fact]
    public async Task List_exposes_quic_only_to_owner()
    {
        var dir = CreateStore();
        var owner = DirectoryIdentity.Parse("local:owner");
        var grantee = DirectoryIdentity.Parse("local:guest");
        var store = new FilePlacementDirectoryStore(dir);
        var service = new PlacementDirectoryService(store, TimeProvider.System);
        await store.SaveAsync(new PlacementDirectorySnapshot
        {
            SchemaVersion = 2,
            Placements =
            [
                new PlacementRecord
                {
                    Id = PlacementId.Parse("plc_quic001"),
                    Owner = owner,
                    DisplayName = "Edge",
                    Kind = PlacementDirectoryKind.Peer,
                    MuxIdentity = MuxIdentity.Parse("mux_agents"),
                    Reachability = PlacementReachability.Unreachable,
                    LastSeen = DateTimeOffset.UtcNow,
                    Quic = new QuicPlacementProfile
                    {
                        Id = "plc_quic001",
                        Label = "Edge",
                        Target = "192.168.1.10",
                        Session = "agents",
                        Enabled = true,
                    },
                },
            ],
            PlacementGrants =
            [
                new PlacementAccessGrant
                {
                    PlacementId = PlacementId.Parse("plc_quic001"),
                    Grantee = grantee,
                },
            ],
        });

        var ownerList = await service.ListAsync(owner);
        var guestList = await service.ListAsync(grantee);
        Assert.NotNull(ownerList.Value![0].Quic);
        Assert.Null(guestList.Value![0].Quic);
    }

    [Fact]
    public async Task Add_hyphen_session_maps_opaque_mux_identity_and_keeps_session_name()
    {
        var dir = CreateStore();
        var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
        var outcome = await catalog.AddAsync(Request("Lab", "192.168.1.10", "lab-mux"));
        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal("lab-mux", outcome.Value!.Quic!.Session);
        Assert.StartsWith("mux_", outcome.Value.MuxIdentity.Value, StringComparison.Ordinal);
        Assert.DoesNotContain('-', outcome.Value.MuxIdentity.Value);
        Assert.True(MuxIdentity.TryFromSessionName("lab-mux", out var expected));
        Assert.Equal(expected, outcome.Value.MuxIdentity);
    }

    [Fact]
    public async Task Add_underscore_session_still_uses_direct_mux_identity()
    {
        var dir = CreateStore();
        var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
        var outcome = await catalog.AddAsync(Request("Edge", "192.168.1.10", "agents"));
        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal("mux_agents", outcome.Value!.MuxIdentity.Value);
        Assert.Equal("agents", outcome.Value.Quic!.Session);
    }

    [Fact]
    public async Task Add_stores_certificate_fingerprint_without_private_key()
    {
        var fingerprint = new string('a', 64);
        var dir = CreateStore();
        var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
        var outcome = await catalog.AddAsync(Request("Edge", "192.168.1.10", "agents") with
        {
            CertificateSha256 = fingerprint,
        });
        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal(fingerprint, outcome.Value!.Quic!.CertificateSha256);
        var json = await File.ReadAllTextAsync(Path.Combine(dir, "directory.json"));
        using var document = JsonDocument.Parse(json);
        var quic = document.RootElement.GetProperty("placements")[0].GetProperty("quic");
        Assert.Equal(fingerprint, quic.GetProperty("certificate_sha256").GetString());
        Assert.False(quic.TryGetProperty("private_key", out _));
        Assert.DoesNotContain("BEGIN", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_invalid_fingerprint_fails_before_persist()
    {
        var dir = CreateStore();
        var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
        var outcome = await catalog.AddAsync(Request("Edge", "192.168.1.10", "agents") with
        {
            CertificateSha256 = "not-a-fingerprint",
        });
        Assert.False(outcome.Ok);
        Assert.Equal(PlacementReasons.ProfileInvalid, outcome.Reason);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task Same_target_and_session_refreshes_the_row_and_keeps_id()
    {
        var dir = CreateStore();
        var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
        var first = await catalog.AddAsync(Request("Edge", "192.168.1.10", "agents") with
        {
            EnrolledDeviceId = "dev_one",
        });
        var second = await catalog.AddAsync(Request("Lab", "192.168.1.10", "agents") with
        {
            EnrolledDeviceId = "dev_two",
            CertificateSha256 = new string('b', 64),
        });
        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(first.Value!.Id, second.Value!.Id);
        Assert.Equal("Lab", second.Value.DisplayName);
        Assert.Equal("192.168.1.10", second.Value.Quic!.Target);
        Assert.Equal("dev_two", second.Value.Quic.EnrolledDeviceId);
        Assert.Equal(new string('b', 64), second.Value.Quic.CertificateSha256);
        var snapshot = await new FilePlacementDirectoryStore(dir).LoadAsync();
        Assert.Single(snapshot.Placements);
    }

    [Fact]
    public async Task Refresh_without_a_device_keeps_the_enrolled_device_and_pin()
    {
        var dir = CreateStore();
        var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
        var first = await catalog.AddAsync(Request("Edge", "192.168.1.10", "agents") with
        {
            EnrolledDeviceId = "dev_one",
            CertificateSha256 = new string('a', 64),
        });
        var second = await catalog.AddAsync(Request("Edge renamed", "192.168.1.10", "agents"));
        Assert.True(second.Ok, second.Detail);
        Assert.Equal(first.Value!.Id, second.Value!.Id);
        Assert.Equal("Edge renamed", second.Value.DisplayName);
        Assert.Equal("dev_one", second.Value.Quic!.EnrolledDeviceId);
        Assert.Equal(new string('a', 64), second.Value.Quic.CertificateSha256);
        Assert.Equal(PlacementReachability.Reachable, second.Value.Reachability);
    }

    [Fact]
    public async Task Different_target_with_the_same_session_adds_a_second_placement()
    {
        var dir = CreateStore();
        var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
        var first = await catalog.AddAsync(Request("Remote", "hc-remote:7443", "default") with
        {
            CertificateSha256 = new string('a', 64),
            EnrolledDeviceId = "dev_one",
        });
        var second = await catalog.AddAsync(Request("Lab", "hypa-remote:7443", "default") with
        {
            CertificateSha256 = new string('b', 64),
            EnrolledDeviceId = "dev_two",
        });
        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.NotEqual(first.Value!.Id, second.Value!.Id);

        var snapshot = await new FilePlacementDirectoryStore(dir).LoadAsync();
        Assert.Equal(2, snapshot.Placements.Count);
        var remote = snapshot.Placements.Single(p => p.Id == first.Value.Id);
        Assert.Equal("Remote", remote.DisplayName);
        Assert.Equal("hc-remote:7443", remote.Quic!.Target);
        Assert.Equal(new string('a', 64), remote.Quic.CertificateSha256);
        Assert.Equal("dev_one", remote.Quic.EnrolledDeviceId);
        var lab = snapshot.Placements.Single(p => p.Id == second.Value.Id);
        Assert.Equal("hypa-remote:7443", lab.Quic!.Target);
        Assert.Equal("dev_two", lab.Quic.EnrolledDeviceId);
    }

    [Fact]
    public async Task Same_target_with_a_different_session_adds_a_second_placement()
    {
        var dir = CreateStore();
        var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
        var first = await catalog.AddAsync(Request("One", "hc-remote:7443", "default"));
        var second = await catalog.AddAsync(Request("Two", "hc-remote:7443", "other"));
        Assert.NotEqual(first.Value!.Id, second.Value!.Id);
        var snapshot = await new FilePlacementDirectoryStore(dir).LoadAsync();
        Assert.Equal(2, snapshot.Placements.Count);
    }

    [Fact]
    public async Task Concurrent_add_of_the_same_destination_keeps_one_row()
    {
        var dir = CreateStore();
        var store = new FilePlacementDirectoryStore(dir);
        var catalog = new QuicPlacementCatalogService(store);
        var results = await Task.WhenAll(
            catalog.AddAsync(Request("Edge", "192.168.1.10", "agents")).AsTask(),
            catalog.AddAsync(Request("Other", "192.168.1.10", "agents")).AsTask());
        Assert.All(results, outcome => Assert.True(outcome.Ok));
        var snapshot = await store.LoadAsync();
        Assert.Single(snapshot.Placements);
    }

    [Fact]
    public async Task Concurrent_add_of_two_targets_with_one_session_keeps_two_rows()
    {
        var dir = CreateStore();
        var store = new FilePlacementDirectoryStore(dir);
        var catalog = new QuicPlacementCatalogService(store);
        var results = await Task.WhenAll(
            catalog.AddAsync(Request("Edge", "192.168.1.10", "agents")).AsTask(),
            catalog.AddAsync(Request("Other", "192.168.1.11", "agents")).AsTask());
        Assert.All(results, outcome => Assert.True(outcome.Ok));
        var snapshot = await store.LoadAsync();
        Assert.Equal(2, snapshot.Placements.Count);
    }

    [Fact]
    public async Task Register_with_a_shared_mux_identity_leaves_a_quic_row_alone()
    {
        var dir = CreateStore();
        var store = new FilePlacementDirectoryStore(dir);
        var catalog = new QuicPlacementCatalogService(store);
        var added = await catalog.AddAsync(Request("Remote", "hc-remote:7443", "default"));
        var directory = new PlacementDirectoryService(store);
        var registered = await directory.RegisterAsync(new PlacementRegistration
        {
            Owner = Owner(),
            DisplayName = "Local",
            Kind = PlacementDirectoryKind.Local,
            MuxIdentity = added.Value!.MuxIdentity,
        });
        Assert.True(registered.Ok, registered.Detail);
        Assert.NotEqual(added.Value.Id, registered.Value!.Id);
        var snapshot = await store.LoadAsync();
        Assert.Equal(2, snapshot.Placements.Count);
        Assert.Equal("Remote", snapshot.Placements.Single(p => p.Id == added.Value.Id).DisplayName);
    }

    [Fact]
    public async Task Old_directory_with_rows_that_share_a_mux_identity_loads_all_rows()
    {
        var dir = CreateStore();
        var json = """
            {"schema":2,"revision":3,"placements":[{"placement_id":"plc_one","owner_id":"tester","display_name":"Remote","kind":"peer","mux_identity":"mux_default","reachability":"reachable","last_seen":"2026-10-12T10:38:06Z","quic":{"id":"plc_one","label":"Remote","target":"hc-remote:7443","session":"default","enabled":true}},{"placement_id":"plc_two","owner_id":"tester","display_name":"Lab","kind":"peer","mux_identity":"mux_default","reachability":"reachable","last_seen":"2026-10-12T10:38:06Z","quic":{"id":"plc_two","label":"Lab","target":"hypa-remote:7443","session":"default","enabled":true}}],"placement_grants":[],"work_grants":[]}
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "directory.json"), json);
        var snapshot = await new FilePlacementDirectoryStore(dir).LoadAsync();
        Assert.Equal(2, snapshot.Placements.Count);
    }

    [Fact]
    public async Task Remove_owned_drops_local_row_without_quic()
    {
        var dir = CreateStore();
        var store = new FilePlacementDirectoryStore(dir);
        var directory = new PlacementDirectoryService(store);
        var registered = await directory.RegisterAsync(new PlacementRegistration
        {
            Owner = Owner(),
            DisplayName = "Arm Mac",
            Kind = PlacementDirectoryKind.Local,
            MuxIdentity = MuxIdentity.Parse("mux_armmac"),
        });
        Assert.True(registered.Ok);
        var removed = await directory.RemoveOwnedAsync(Owner(), registered.Value!.Id);
        Assert.True(removed.Ok);
        var snapshot = await store.LoadAsync();
        Assert.Empty(snapshot.Placements);
    }

    [Fact]
    public async Task Remove_clears_grants_so_reused_id_does_not_reach_former_grantee()
    {
        var dir = CreateStore();
        var owner = Owner();
        var grantee = DirectoryIdentity.Parse("local:guest");
        var store = new FilePlacementDirectoryStore(dir);
        var catalog = new QuicPlacementCatalogService(store);
        var directory = new PlacementDirectoryService(store);
        var added = await catalog.AddAsync(Request("Edge", "192.168.1.10", "agents"));
        Assert.True(added.Ok);
        var placementId = added.Value!.Id;
        var granted = await directory.GrantListAccessAsync(owner, placementId, grantee);
        Assert.True(granted.Ok);
        var guestBefore = await directory.ListAsync(grantee);
        Assert.Single(guestBefore.Value!);

        var removed = await catalog.RemoveAsync(owner, placementId);
        Assert.True(removed.Ok);
        var snapshot = await store.LoadAsync();
        Assert.DoesNotContain(snapshot.PlacementGrants, g => g.PlacementId == placementId);

        var reregistered = await directory.RegisterAsync(new PlacementRegistration
        {
            Owner = owner,
            DisplayName = "Replacement",
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = MuxIdentity.Parse("mux_replacement"),
            Id = placementId,
        });
        Assert.True(reregistered.Ok);

        var guestAfter = await directory.ListAsync(grantee);
        Assert.Empty(guestAfter.Value!);
        var connect = await directory.GetForConnectAsync(grantee, placementId);
        Assert.False(connect.Ok);
        Assert.Equal(PlacementReasons.Unauthorized, connect.Reason);
    }

    [Fact]
    public async Task Schema_one_reachability_mutation_keeps_schema_one_without_provider_fields()
    {
        var dir = CreateStore();
        var json = """
            {"schema":1,"revision":0,"placements":[{"placement_id":"plc_peer001","owner_id":"local:test","display_name":"Peer","kind":"peer","mux_identity":"mux_peer001","reachability":"unreachable","last_seen":"2026-09-08T00:00:00Z"}],"placement_grants":[],"work_grants":[]}
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "directory.json"), json);
        var store = new FilePlacementDirectoryStore(dir);
        var directory = new PlacementDirectoryService(store);
        var updated = await directory.SetReachabilityAsync(
            Owner(),
            PlacementId.Parse("plc_peer001"),
            PlacementReachability.Reachable);
        Assert.True(updated.Ok);
        var saved = await File.ReadAllTextAsync(Path.Combine(dir, "directory.json"));
        using var document = JsonDocument.Parse(saved);
        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        var row = document.RootElement.GetProperty("placements")[0];
        Assert.False(row.TryGetProperty("ssh", out _));
        Assert.False(row.TryGetProperty("quic", out _));
    }

    [Fact]
    public async Task Add_with_enrollment_is_reachable()
    {
        var dir = CreateStore();
        var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
        var enrolled = await catalog.AddAsync(Request("Lab", "127.0.0.1:7443", "default") with
        {
            EnrolledDeviceId = "dev_docker",
        });
        Assert.True(enrolled.Ok, enrolled.Detail);
        Assert.Equal(PlacementReachability.Reachable, enrolled.Value!.Reachability);

        var listed = SidebarCubeCatalog.FromRows(
        [
            new PlacementRow
            {
                Id = enrolled.Value.Id,
                DisplayName = enrolled.Value.DisplayName,
                Kind = enrolled.Value.Kind,
                MuxIdentity = enrolled.Value.MuxIdentity,
                Reachability = enrolled.Value.Reachability,
                LastSeen = enrolled.Value.LastSeen,
                Quic = enrolled.Value.Quic,
            },
        ]);
        var item = Assert.Single(listed);
        Assert.Equal("QUIC · Reachable", item.ProviderSuffix);
        Assert.True(item.ConnectEnabled);
    }

    [Fact]
    public async Task Add_without_enrollment_stays_unreachable()
    {
        var dir = CreateStore();
        var catalog = new QuicPlacementCatalogService(new FilePlacementDirectoryStore(dir));
        var outcome = await catalog.AddAsync(Request("Edge", "192.168.1.10", "agents"));
        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal(PlacementReachability.Unreachable, outcome.Value!.Reachability);
    }

    [Fact]
    public void Sidebar_row_shows_provider_without_raw_target()
    {
        var items = SidebarCubeCatalog.FromRows(
        [
            new PlacementRow
            {
                Id = PlacementId.Parse("plc_quic01"),
                DisplayName = "Edge",
                Kind = PlacementDirectoryKind.Peer,
                MuxIdentity = MuxIdentity.Parse("mux_agents"),
                Reachability = PlacementReachability.Reachable,
                LastSeen = DateTimeOffset.UtcNow,
                Quic = new QuicPlacementProfile
                {
                    Id = "plc_quic01",
                    Label = "Edge",
                    Target = "192.168.1.10",
                    Session = "agents",
                    Enabled = true,
                },
            },
        ]);

        var item = Assert.Single(items);
        Assert.Equal("QUIC · Reachable", item.ProviderSuffix);
        Assert.DoesNotContain("192.168.1.10", item.Name, StringComparison.Ordinal);
        Assert.Null(item.ConnectDetail);
    }

    [Theory]
    [InlineData("edge.example", "edge.example:443", 1)]
    [InlineData("edge.example:443", "EDGE.example", 1)]
    [InlineData("edge.example", "edge.example:8443", 2)]
    [InlineData("::1", "[::1]:443", 1)]
    [InlineData("[::1]:8443", "::1", 2)]
    [InlineData("[0:0:0:0:0:0:0:1]:443", "::1", 1)]
    public async Task Add_compares_targets_by_host_and_effective_port(string first, string second, int rows)
    {
        var dir = CreateStore();
        var store = new FilePlacementDirectoryStore(dir);
        var catalog = new QuicPlacementCatalogService(store);
        Assert.True((await catalog.AddAsync(Request("One", first, "agents"))).Ok);
        Assert.True((await catalog.AddAsync(Request("Two", second, "agents"))).Ok);

        var stored = (await store.LoadAsync()).Placements;

        Assert.Equal(rows, stored.Count);
        if (rows == 1)
            Assert.Equal(second, stored[0].Quic!.Target);
        else
            Assert.Equal(first, stored[0].Quic!.Target);
    }

    [Fact]
    public async Task Enroll_device_saves_the_device_on_a_row_that_has_none()
    {
        var dir = CreateStore();
        var store = new FilePlacementDirectoryStore(dir);
        var catalog = new QuicPlacementCatalogService(store);
        var added = await catalog.AddAsync(Request("Edge", "192.168.1.10:8443", "agents"));
        Assert.True(added.Ok, added.Detail);

        var enrolled = await catalog.EnrollDeviceAsync(Owner(), added.Value!.Id, "dev_aaaaaaaaaaaaaaaa");

        Assert.True(enrolled.Ok, enrolled.Detail);
        var row = Assert.Single((await store.LoadAsync()).Placements);
        Assert.Equal("dev_aaaaaaaaaaaaaaaa", row.Quic!.EnrolledDeviceId);
    }

    [Fact]
    public async Task Enroll_device_never_replaces_an_enrolled_device()
    {
        var dir = CreateStore();
        var store = new FilePlacementDirectoryStore(dir);
        var catalog = new QuicPlacementCatalogService(store);
        var added = await catalog.AddAsync(Request("Edge", "192.168.1.10:8443", "agents") with
        {
            EnrolledDeviceId = "dev_aaaaaaaaaaaaaaaa",
        });
        Assert.True(added.Ok, added.Detail);

        var enrolled = await catalog.EnrollDeviceAsync(Owner(), added.Value!.Id, "dev_bbbbbbbbbbbbbbbb");

        Assert.True(enrolled.Ok, enrolled.Detail);
        var row = Assert.Single((await store.LoadAsync()).Placements);
        Assert.Equal("dev_aaaaaaaaaaaaaaaa", row.Quic!.EnrolledDeviceId);
    }

    private static QuicPlacementAddRequest Request(string label, string target, string session) =>
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
        var dir = Path.Combine(Path.GetTempPath(), "hypa-quic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
