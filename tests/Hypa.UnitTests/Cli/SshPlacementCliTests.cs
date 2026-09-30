using System.Text.Json;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class SshPlacementCliTests
{
    [Fact]
    public void Ssh_mutation_document_success_shape_uses_null_reason()
    {
        var json = JsonSerializer.Serialize(
            new PlacementSshMutationDocument { Ok = true, PlacementId = "plc_test01" },
            PlacementJsonContext.Default.PlacementSshMutationDocument);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("plc_test01", document.RootElement.GetProperty("placement_id").GetString());
        Assert.False(document.RootElement.TryGetProperty("reason", out var reason) && reason.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public void Ssh_mutation_document_failure_shape_uses_null_placement_id()
    {
        var json = JsonSerializer.Serialize(
            new PlacementSshMutationDocument
            {
                Ok = false,
                Reason = PlacementReasons.PreparationFailed,
                Detail = "Remote preparation failed for Build. Placement was not saved.",
            },
            PlacementJsonContext.Default.PlacementSshMutationDocument);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(PlacementReasons.PreparationFailed, document.RootElement.GetProperty("reason").GetString());
        Assert.False(document.RootElement.TryGetProperty("placement_id", out var id) && id.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Preparation_service_does_not_retry_incompatible_failures()
    {
        var remote = new CountingRemoteMux([
            RemoteMuxOutcome.Failure(RemoteMuxReasons.Incompatible, "Remote mux is not reachable through OpenSSH."),
            RemoteMuxOutcome.Success(new RemoteMuxPath
            {
                LocalSocketPath = "/tmp/unused.sock",
                Session = "agents",
                Target = "user@dev",
                Generation = 1,
            }),
        ]);
        var preparation = new SshPlacementPreparationService(
            remote,
            new StubIdentityReader(),
            TimeProvider.System);
        var profile = PeerProfile.TryCreate(
            "plc_test01",
            "Build",
            "user@dev",
            "agents",
            enabled: true,
            PeerProviders.Ssh,
            out var peer,
            out _) && peer is not null
            ? peer
            : throw new InvalidOperationException("profile");
        var outcome = await preparation.PrepareAsync(profile);
        Assert.False(outcome.Ok);
        Assert.Equal(1, remote.Attempts);
    }

    [Fact]
    public async Task Open_ssh_adapter_cancels_each_ssh_attempt_after_five_seconds()
    {
        var adapter = new OpenSshRemoteMuxAdapter(
            new SlowOpenSshProcess(),
            new RemoteConnectionFence(),
            unixClient: true);
        var profile = PeerProfile.TryCreate(
            "plc_test01",
            "Build",
            "user@dev",
            "agents",
            enabled: true,
            PeerProviders.Ssh,
            out var peer,
            out _) && peer is not null
            ? peer
            : throw new InvalidOperationException("profile");
        var started = Environment.TickCount64;
        await Assert.ThrowsAnyAsync<Exception>(() =>
            adapter.OpenAsync(
                    RemoteMuxOpenRequest.FromProfile(peer) with { Interactive = false })
                .AsTask());
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
        Assert.InRange(elapsed.TotalSeconds, 4, 7);
    }

    [Fact]
    public void Attention_states_map_to_cubes_suffixes_and_connect_gating()
    {
        var approval = SshPlacementPresentation.ResolveAttention(
            PlacementReachability.Unreachable,
            new SshPlacementProfile
            {
                Id = "plc_test01",
                Label = "Build",
                Target = "user@dev",
                Session = "agents",
                Enabled = false,
                Attention = SshPlacementAttentionStates.ApprovalRequired,
            });
        Assert.Equal(SshPlacementAttention.ApprovalRequired, approval);
        Assert.False(SshPlacementPresentation.ConnectEnabled(approval));
        Assert.Contains(
            OpenSshArgumentBuilder.Quote("user@dev"),
            SshPlacementPresentation.ConnectDetail(approval, new SshPlacementProfile
            {
                Id = "plc_test01",
                Label = "Build",
                Target = "user@dev",
                Session = "agents",
                Enabled = false,
            }));

        var preparation = SshPlacementPresentation.ResolveAttention(
            PlacementReachability.Unreachable,
            new SshPlacementProfile
            {
                Id = "plc_test01",
                Label = "Build",
                Target = "user@dev",
                Session = "agents",
                Enabled = false,
                Attention = SshPlacementAttentionStates.PreparationRequired,
            });
        Assert.Equal(SshPlacementAttention.PreparationRequired, preparation);
        Assert.Equal(
            "Remote preparation failed for Build. Placement remains disabled.",
            SshPlacementPresentation.ConnectDetail(preparation, new SshPlacementProfile
            {
                Id = "plc_test01",
                Label = "Build",
                Target = "user@dev",
                Session = "agents",
                Enabled = false,
            }));
    }

    [Fact]
    public async Task List_exposes_ssh_only_to_owner()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-plc-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var owner = DirectoryIdentity.Parse("local:owner");
            var grantee = DirectoryIdentity.Parse("local:guest");
            var store = new FilePlacementDirectoryStore(dir);
            var service = new PlacementDirectoryService(store, TimeProvider.System);
            await store.SaveAsync(new PlacementDirectorySnapshot
            {
                Placements =
                [
                    new PlacementRecord
                    {
                        Id = PlacementId.Parse("plc_ssh001"),
                        Owner = owner,
                        DisplayName = "Build",
                        Kind = PlacementDirectoryKind.Peer,
                        MuxIdentity = MuxIdentity.Parse("mux_agents"),
                        Reachability = PlacementReachability.Unreachable,
                        LastSeen = DateTimeOffset.UtcNow,
                        Ssh = new SshPlacementProfile
                        {
                            Id = "plc_ssh001",
                            Label = "Build",
                            Target = "user@dev",
                            Session = "agents",
                            Enabled = true,
                        },
                    },
                ],
                PlacementGrants =
                [
                    new PlacementAccessGrant
                    {
                        PlacementId = PlacementId.Parse("plc_ssh001"),
                        Grantee = grantee,
                    },
                ],
            });

            var ownerList = await service.ListAsync(owner);
            var guestList = await service.ListAsync(grantee);
            Assert.NotNull(ownerList.Value![0].Ssh);
            Assert.Null(guestList.Value![0].Ssh);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class CountingRemoteMux(IReadOnlyList<RemoteMuxOutcome> outcomes) : IRemoteMuxPath
    {
        private int _index;
        public int Attempts { get; private set; }

        public ValueTask<RemoteMuxOutcome> OpenAsync(
            RemoteMuxOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            var outcome = outcomes[Math.Min(_index, outcomes.Count - 1)];
            _index++;
            return new(outcome);
        }
    }

    private sealed class SlowOpenSshProcess : IOpenSshProcess
    {
        public async Task<OpenSshProcessResult> RunAsync(
            OpenSshProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return new OpenSshProcessResult { ExitCode = 0, Stdout = "", Stderr = "" };
        }
    }

    private sealed class StubIdentityReader : IRemoteMuxIdentityReader
    {
        public ValueTask<PlacementOutcome<MuxIdentity>> ReadAsync(
            string localSocketPath,
            string session,
            CancellationToken cancellationToken = default) =>
            new(PlacementOutcome<MuxIdentity>.Success(MuxIdentity.Parse("mux_agents")));
    }

}
