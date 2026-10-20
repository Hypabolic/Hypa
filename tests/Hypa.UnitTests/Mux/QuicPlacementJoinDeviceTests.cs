using System.Diagnostics;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.Cubes.Connections;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;
using PlacementId = Hypa.Placement.Domain.PlacementId;
using Xunit;

namespace Hypa.UnitTests.Mux;

/// <summary>
/// The join of a QUIC placement that has no enrolled device. The resolver tries the
/// paired devices in turn and moves on only when the host denies the join.
/// </summary>
public sealed class QuicPlacementJoinDeviceTests : IDisposable
{
    private static readonly DirectoryIdentity Owner = DirectoryIdentity.Parse("local:test");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hypa-joinplc-" + Guid.NewGuid().ToString("N"));
    private readonly DevicePairingService _pairing;
    private readonly FilePlacementDirectoryStore _store;
    private readonly QuicPlacementCatalogService _catalog;

    public QuicPlacementJoinDeviceTests()
    {
        _pairing = new DevicePairingService(
            new FileDevicePairingStore(Path.Combine(_dir, "pairing")),
            new FileDeviceKeyStore(Path.Combine(_dir, "pairing")));
        _store = new FilePlacementDirectoryStore(Path.Combine(_dir, "placements"));
        _catalog = new QuicPlacementCatalogService(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task First_candidate_denied_second_accepted_connects_with_the_second_and_saves_it()
    {
        var first = await PairAsync();
        var second = await PairAsync();
        var placement = await AddPlacementAsync(enrolled: null);
        var host = new Host(denied: [first]);

        var result = await Resolver(host).ResolveAsync(Destination(placement));

        Assert.IsType<ConnectivityAttachEndpoint>(result.EndpointOrNull());
        Assert.Equal([first, second], host.Attempts);
        Assert.Equal(second.Value, (await LoadAsync(placement)).Quic!.EnrolledDeviceId);
    }

    [Fact]
    public async Task Later_joins_use_only_the_saved_device()
    {
        var first = await PairAsync();
        var second = await PairAsync();
        var placement = await AddPlacementAsync(enrolled: null);
        var firstJoin = new Host(denied: [first]);
        Assert.IsType<ConnectivityAttachEndpoint>(
            (await Resolver(firstJoin).ResolveAsync(Destination(placement))).EndpointOrNull());

        var laterJoin = new Host(denied: []);
        var result = await Resolver(laterJoin).ResolveAsync(Destination(placement));

        Assert.IsType<ConnectivityAttachEndpoint>(result.EndpointOrNull());
        Assert.Equal([second], laterJoin.Attempts);
    }

    [Fact]
    public async Task Trusted_device_is_tried_before_a_pending_one()
    {
        var pending = await StartAsync();
        var trusted = await PairAsync();
        var placement = await AddPlacementAsync(enrolled: null);
        var host = new Host(denied: [trusted, pending]);

        var result = await Resolver(host).ResolveAsync(Destination(placement));

        Assert.False(result.IsOk);
        Assert.Equal([trusted, pending], host.Attempts);
    }

    [Fact]
    public async Task Pending_device_is_used_when_the_host_trusts_it_and_is_then_saved()
    {
        var pending = await StartAsync();
        var trusted = await PairAsync();
        var placement = await AddPlacementAsync(enrolled: null);
        var host = new Host(denied: [trusted]);

        var result = await Resolver(host).ResolveAsync(Destination(placement));

        Assert.IsType<ConnectivityAttachEndpoint>(result.EndpointOrNull());
        Assert.Equal(pending.Value, (await LoadAsync(placement)).Quic!.EnrolledDeviceId);
    }

    [Fact]
    public async Task Every_candidate_denied_fails_with_a_message_that_names_the_device_option()
    {
        var first = await PairAsync();
        var second = await StartAsync();
        var placement = await AddPlacementAsync(enrolled: null);
        var host = new Host(denied: [first, second]);

        var result = await Resolver(host).ResolveAsync(Destination(placement));

        Assert.False(result.IsOk);
        Assert.Equal(ConnectivityReasons.JoinDenied, result.Error.Reason);
        Assert.Contains("add-quic --device", result.Error.Detail, StringComparison.Ordinal);
        Assert.Contains("add-quic --device", AttachEndpointUserCopy.ConnectFailed("Remote", result.Error), StringComparison.Ordinal);
        Assert.Equal([first, second], host.Attempts);
        Assert.Null((await LoadAsync(placement)).Quic!.EnrolledDeviceId);
    }

    [Fact]
    public async Task No_paired_device_fails_with_a_message_that_names_the_device_option()
    {
        var placement = await AddPlacementAsync(enrolled: null);
        var host = new Host(denied: []);

        var result = await Resolver(host).ResolveAsync(Destination(placement));

        Assert.False(result.IsOk);
        Assert.Contains("add-quic --device", result.Error.Detail, StringComparison.Ordinal);
        Assert.Contains("add-quic --device", AttachEndpointUserCopy.ConnectFailed("Remote", result.Error), StringComparison.Ordinal);
        Assert.Empty(host.Attempts);
    }

    [Theory]
    [InlineData(ConnectivityReasons.PeerUnavailable)]
    [InlineData(ConnectivityReasons.Unauthorized)]
    [InlineData(CubesConnectErrorReasons.DialFailed)]
    public async Task A_failure_that_is_not_a_denial_stops_at_the_first_candidate(string reason)
    {
        var first = await PairAsync();
        await PairAsync();
        var placement = await AddPlacementAsync(enrolled: null);
        var host = new Host(denied: [], failure: new Dictionary<DeviceId, string> { [first] = reason });

        var result = await Resolver(host).ResolveAsync(Destination(placement));

        Assert.False(result.IsOk);
        Assert.Equal(reason, result.Error.Reason);
        Assert.Equal([first], host.Attempts);
        Assert.Null((await LoadAsync(placement)).Quic!.EnrolledDeviceId);
    }

    [Fact]
    public async Task An_enrolled_device_is_the_only_candidate_and_is_never_replaced()
    {
        var other = await PairAsync();
        var enrolled = await PairAsync();
        var placement = await AddPlacementAsync(enrolled: enrolled);
        var denied = new Host(denied: [enrolled]);

        var refused = await Resolver(denied).ResolveAsync(Destination(placement));

        Assert.False(refused.IsOk);
        Assert.Equal(ConnectivityReasons.JoinDenied, refused.Error.Reason);
        Assert.Equal([enrolled], denied.Attempts);

        var accepted = new Host(denied: []);
        Assert.IsType<ConnectivityAttachEndpoint>(
            (await Resolver(accepted).ResolveAsync(Destination(placement))).EndpointOrNull());
        Assert.Equal([enrolled], accepted.Attempts);
        Assert.NotEqual(other, enrolled);
        Assert.Equal(enrolled.Value, (await LoadAsync(placement)).Quic!.EnrolledDeviceId);
    }

    [Fact]
    public async Task A_failed_save_keeps_the_connection()
    {
        var first = await PairAsync();
        var placement = await AddPlacementAsync(enrolled: null);
        var host = new Host(denied: []);
        var resolver = new DirectoryCubesConnectEndpointResolver(
            new StoreDirectory(_store, failEnroll: true),
            directMaterial: new PairingCubesConnectDirectMaterialSource(_pairing),
            directConnect: host.ConnectAsync);

        var result = await resolver.ResolveAsync(Destination(placement));

        Assert.IsType<ConnectivityAttachEndpoint>(result.EndpointOrNull());
        Assert.Equal([first], host.Attempts);
        Assert.Null((await LoadAsync(placement)).Quic!.EnrolledDeviceId);
    }

    [Fact]
    public async Task A_malformed_enrolled_value_fails_closed_and_tries_no_device()
    {
        await PairAsync();
        await PairAsync();
        var placement = await AddPlacementWithValueAsync("not-a-device-id");
        var host = new Host(denied: []);

        var result = await Resolver(host).ResolveAsync(Destination(placement));

        Assert.False(result.IsOk);
        Assert.Contains("add-quic --device", result.Error.Detail, StringComparison.Ordinal);
        Assert.Empty(host.Attempts);
        Assert.Equal("not-a-device-id", (await LoadAsync(placement)).Quic!.EnrolledDeviceId);
    }

    [Fact]
    public async Task Each_capability_is_made_just_before_its_own_dial()
    {
        var clock = new AdvancingClock(DateTimeOffset.UtcNow);
        var keys = Path.Combine(_dir, "pairing");
        var pairing = new DevicePairingService(
            new FileDevicePairingStore(keys),
            new FileDeviceKeyStore(keys),
            clock);
        var first = await PairAsync(pairing);
        await PairAsync(pairing);
        var placement = await AddPlacementAsync(enrolled: null);
        var lifeLeftAtDial = new List<TimeSpan>();
        var dials = 0;
        var resolver = new DirectoryCubesConnectEndpointResolver(
            new StoreDirectory(_store),
            directMaterial: new PairingCubesConnectDirectMaterialSource(pairing, clock),
            directConnect: (material, _) =>
            {
                lifeLeftAtDial.Add(material.Bootstrap.Capability.ExpiresAt - clock.GetUtcNow());
                if (dials++ == 0)
                {
                    // The host takes two minutes to deny the first device.
                    clock.Advance(TimeSpan.FromMinutes(2));
                    return Task.FromResult(
                        Hypa.Runtime.Domain.Common.Result<IFramedSession, ConnectError>.Fail(
                            new ConnectError(ConnectivityReasons.JoinDenied, "join denied")));
                }

                return Task.FromResult(
                    Hypa.Runtime.Domain.Common.Result<IFramedSession, ConnectError>.Ok(
                        ChannelFramedSession.Pair("plc_join").Client));
            });

        var result = await resolver.ResolveAsync(Destination(placement));

        Assert.IsType<ConnectivityAttachEndpoint>(result.EndpointOrNull());
        Assert.Equal(2, lifeLeftAtDial.Count);
        Assert.All(lifeLeftAtDial, left => Assert.True(left > TimeSpan.FromSeconds(30), left.ToString()));
        Assert.NotEqual(first.Value, (await LoadAsync(placement)).Quic!.EnrolledDeviceId);
    }

    [Fact]
    public async Task The_device_is_saved_in_the_store_that_loaded_the_placement()
    {
        var first = await PairAsync();
        var otherDir = Path.Combine(_dir, "placements-b");
        var storeB = new FilePlacementDirectoryStore(otherDir);
        var catalogB = new QuicPlacementCatalogService(storeB);
        var addedB = await catalogB.AddAsync(new QuicPlacementAddRequest
        {
            Owner = Owner,
            Label = "Remote",
            Target = "192.168.1.10:8443",
            Session = "default",
            CertificateSha256 = new string('a', 64),
        });
        Assert.True(addedB.Ok, addedB.Detail);
        // Store A is the default store of the resolver. It holds a row with the same id.
        var storeA = _store;
        await storeA.SaveAsync((await storeA.LoadAsync()) with { Placements = [addedB.Value!] });
        var host = new Host(denied: []);
        var resolver = new DirectoryCubesConnectEndpointResolver(
                new StoreDirectory(storeA),
                directMaterial: new PairingCubesConnectDirectMaterialSource(_pairing),
                directConnect: host.ConnectAsync)
            .WithDirectory(new StoreDirectory(storeB));

        var result = await resolver.ResolveAsync(Destination(addedB.Value!));

        Assert.IsType<ConnectivityAttachEndpoint>(result.EndpointOrNull());
        Assert.Equal(first.Value, Assert.Single((await storeB.LoadAsync()).Placements).Quic!.EnrolledDeviceId);
        Assert.Null(Assert.Single((await storeA.LoadAsync()).Placements).Quic!.EnrolledDeviceId);
    }

    [Fact]
    public async Task A_first_connect_that_enrolls_its_device_passes_the_check_after_the_snapshot()
    {
        var device = await PairAsync();
        var placement = await AddPlacementAsync(enrolled: null);
        var host = new Host(denied: []);
        var stages = StageClock(placement);

        var result = await ((ICubesConnectStageResolver)Resolver(host))
            .ResolveAsync(Destination(placement), stages, CancellationToken.None);

        Assert.IsType<ConnectivityAttachEndpoint>(result.EndpointOrNull());
        Assert.Equal(device.Value, (await LoadAsync(placement)).Quic!.EnrolledDeviceId);
        Assert.True(await AttachSession.AuthorizePlacementConnectAsync(
            placement.Id.Value,
            CancellationToken.None,
            stages,
            openDirectory: () => throw new InvalidOperationException("the remembered directory must be used")));
    }

    [Fact]
    public async Task A_change_to_a_device_that_the_join_did_not_use_still_fails_the_check()
    {
        var joined = await PairAsync();
        var other = await PairAsync();
        var placement = await AddPlacementAsync(enrolled: null);
        var host = new Host(denied: []);
        var stages = StageClock(placement);

        var result = await ((ICubesConnectStageResolver)Resolver(host))
            .ResolveAsync(Destination(placement), stages, CancellationToken.None);
        Assert.IsType<ConnectivityAttachEndpoint>(result.EndpointOrNull());
        Assert.Equal(joined.Value, (await LoadAsync(placement)).Quic!.EnrolledDeviceId);

        var snapshot = await _store.LoadAsync();
        await _store.SaveAsync(snapshot with
        {
            Placements = snapshot.Placements
                .Select(p => p.Id == placement.Id ? p with { Quic = p.Quic! with { EnrolledDeviceId = other.Value } } : p)
                .ToList(),
        });

        Assert.False(await AttachSession.AuthorizePlacementConnectAsync(
            placement.Id.Value,
            CancellationToken.None,
            stages,
            openDirectory: () => throw new InvalidOperationException("the remembered directory must be used")));
    }

    [Fact]
    public async Task A_target_change_after_the_enrollment_still_fails_the_check()
    {
        await PairAsync();
        var placement = await AddPlacementAsync(enrolled: null);
        var host = new Host(denied: []);
        var stages = StageClock(placement);

        Assert.IsType<ConnectivityAttachEndpoint>(
            (await ((ICubesConnectStageResolver)Resolver(host))
                .ResolveAsync(Destination(placement), stages, CancellationToken.None)).EndpointOrNull());

        var snapshot = await _store.LoadAsync();
        await _store.SaveAsync(snapshot with
        {
            Placements = snapshot.Placements
                .Select(p => p.Id == placement.Id ? p with { Quic = p.Quic! with { Target = "192.168.1.99:8443" } } : p)
                .ToList(),
        });

        Assert.False(await AttachSession.AuthorizePlacementConnectAsync(
            placement.Id.Value,
            CancellationToken.None,
            stages,
            openDirectory: () => throw new InvalidOperationException("the remembered directory must be used")));
    }

    [Fact]
    public void An_enrollment_is_accepted_only_for_the_device_that_the_join_used()
    {
        var original = QuicRow(enrolled: null);
        Assert.True(DeviceId.TryParse("dev_joined01", out var joined));
        Assert.True(DeviceId.TryParse("dev_other001", out var other));

        Assert.True(ConnectPlacementRecheck.SameEnabledProfile(
            original, QuicRow(joined.Value), joinedDevice: joined));
        Assert.False(ConnectPlacementRecheck.SameEnabledProfile(
            original, QuicRow(other.Value), joinedDevice: joined));
        Assert.False(ConnectPlacementRecheck.SameEnabledProfile(
            original, QuicRow(joined.Value), joinedDevice: null));
        Assert.False(ConnectPlacementRecheck.SameEnabledProfile(
            QuicRow(other.Value), QuicRow(joined.Value), joinedDevice: joined));
        Assert.True(ConnectPlacementRecheck.SameEnabledProfile(
            original, original, joinedDevice: joined));

        // The joined device does not excuse a change to another dial field.
        var enrolled = QuicRow(joined.Value);
        Assert.False(ConnectPlacementRecheck.SameEnabledProfile(
            original, enrolled with { Quic = enrolled.Quic! with { Session = "other" } }, joinedDevice: joined));
        Assert.False(ConnectPlacementRecheck.SameEnabledProfile(
            original, enrolled with { Quic = enrolled.Quic! with { CertificateSha256 = new string('b', 64) } }, joinedDevice: joined));
        Assert.False(ConnectPlacementRecheck.SameEnabledProfile(
            original, enrolled with { Quic = enrolled.Quic! with { Enabled = false } }, joinedDevice: joined));
        Assert.False(ConnectPlacementRecheck.SameEnabledProfile(
            original, enrolled with { Quic = enrolled.Quic! with { Target = "192.168.1.99:8443" } }, joinedDevice: joined));
    }

    private static PlacementRecord QuicRow(string? enrolled) =>
        new()
        {
            Id = PlacementId.Parse("plc_quicrow1"),
            Owner = Owner,
            Kind = PlacementDirectoryKind.Peer,
            DisplayName = "Remote",
            MuxIdentity = MuxIdentity.Parse("mux_quicrow1"),
            Reachability = PlacementReachability.Reachable,
            LastSeen = DateTimeOffset.UnixEpoch,
            Quic = new QuicPlacementProfile
            {
                Id = "plc_quicrow1",
                Label = "Remote",
                Enabled = true,
                Target = "192.168.1.10:8443",
                Session = "default",
                CertificateSha256 = new string('a', 64),
                EnrolledDeviceId = enrolled,
            },
        };

    private static CubesConnectStageClock StageClock(PlacementRecord placement) =>
        new(
            Stopwatch.StartNew(),
            NullProcessLogSink.Instance,
            placement.Id.Value,
            sessionId: null,
            attachClientId: null);

    private DirectoryCubesConnectEndpointResolver Resolver(Host host) =>
        new(
            new StoreDirectory(_store),
            directMaterial: new PairingCubesConnectDirectMaterialSource(_pairing),
            directConnect: host.ConnectAsync);

    private static SidebarCubeItem Destination(PlacementRecord placement) =>
        new()
        {
            Id = placement.Id.Value,
            Name = "Remote",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
            ProviderSuffix = "QUIC · Reachable",
            ConnectEnabled = true,
        };

    private Task<PlacementRecord> AddPlacementAsync(DeviceId? enrolled) =>
        AddPlacementWithValueAsync(enrolled?.Value);

    private async Task<PlacementRecord> AddPlacementWithValueAsync(string? enrolled)
    {
        var added = await _catalog.AddAsync(new QuicPlacementAddRequest
        {
            Owner = Owner,
            Label = "Remote",
            Target = "192.168.1.10:8443",
            Session = "default",
            CertificateSha256 = new string('a', 64),
            EnrolledDeviceId = enrolled,
        });
        Assert.True(added.Ok, added.Detail);
        return added.Value!;
    }

    private async Task<PlacementRecord> LoadAsync(PlacementRecord placement)
    {
        var snapshot = await _store.LoadAsync();
        return snapshot.Placements.Single(p => p.Id == placement.Id);
    }

    private async Task<DeviceId> StartAsync()
    {
        var started = await _pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        return started.Value!.DeviceId;
    }

    private Task<DeviceId> PairAsync() => PairAsync(_pairing);

    private static async Task<DeviceId> PairAsync(DevicePairingService pairing)
    {
        var started = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        var approved = await pairing.ApproveAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value!.PairingCode,
            PairingApprover.SelfHostedConsole);
        Assert.True(approved.Ok, approved.Detail);
        return started.Value.DeviceId;
    }

    private sealed class AdvancingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>The host. It denies named devices and fails others with a given reason.</summary>
    private sealed class Host(
        IReadOnlyCollection<DeviceId> denied,
        IReadOnlyDictionary<DeviceId, string>? failure = null)
    {
        public List<DeviceId> Attempts { get; } = [];

        public Task<Hypa.Runtime.Domain.Common.Result<IFramedSession, ConnectError>> ConnectAsync(
            CubesConnectDirectMaterial material,
            CancellationToken cancellationToken)
        {
            var device = material.Bootstrap.Capability.DeviceId;
            Attempts.Add(device);
            if (failure is not null && failure.TryGetValue(device, out var reason))
            {
                return Task.FromResult(
                    Hypa.Runtime.Domain.Common.Result<IFramedSession, ConnectError>.Fail(new ConnectError(reason)));
            }

            if (denied.Contains(device))
            {
                return Task.FromResult(
                    Hypa.Runtime.Domain.Common.Result<IFramedSession, ConnectError>.Fail(
                        new ConnectError(ConnectivityReasons.JoinDenied, "join denied")));
            }

            return Task.FromResult(
                Hypa.Runtime.Domain.Common.Result<IFramedSession, ConnectError>.Ok(
                    ChannelFramedSession.Pair("plc_join").Client));
        }
    }

    /// <summary>Reads rows from the store on each call, so a saved device shows at once.</summary>
    private sealed class StoreDirectory(IPlacementDirectoryStore store, bool failEnroll = false)
        : IPlacementDirectory, IQuicDeviceEnrollment
    {
        public ValueTask<PlacementOutcome<PlacementRecord>> EnrollQuicDeviceAsync(
            DirectoryIdentity owner,
            PlacementId placementId,
            string deviceId,
            CancellationToken cancellationToken = default) =>
            failEnroll
                ? throw new IOException("directory.json is read only")
                : new QuicPlacementCatalogService(store).EnrollDeviceAsync(owner, placementId, deviceId, cancellationToken);

        public async ValueTask<PlacementOutcome<PlacementRecord>> GetAsync(
            PlacementId placementId,
            CancellationToken cancellationToken = default)
        {
            var snapshot = await store.LoadAsync(cancellationToken);
            var row = snapshot.Placements.FirstOrDefault(p => p.Id == placementId);
            return row is null
                ? PlacementOutcome<PlacementRecord>.Failure(PlacementReasons.PlacementNotFound, "not found")
                : PlacementOutcome<PlacementRecord>.Success(row);
        }

        public ValueTask<PlacementOutcome<PlacementRecord>> GetForConnectAsync(
            DirectoryIdentity requester,
            PlacementId placementId,
            CancellationToken cancellationToken = default) =>
            GetAsync(placementId, cancellationToken);

        public ValueTask<PlacementOutcome<PlacementRecord>> RegisterAsync(
            PlacementRegistration request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome<PlacementRecord>> SetReachabilityAsync(
            DirectoryIdentity actor,
            PlacementId placementId,
            PlacementReachability reachability,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome<PlacementRecord>> SetActiveWorkAsync(
            DirectoryIdentity actor,
            PlacementId placementId,
            string? workId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome> GrantListAccessAsync(
            DirectoryIdentity actor,
            PlacementId placementId,
            DirectoryIdentity grantee,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome> GrantWorkAccessAsync(
            DirectoryIdentity actor,
            DirectoryIdentity identity,
            string workId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome<PlacementRecord>> RemoveOwnedAsync(
            DirectoryIdentity owner,
            PlacementId placementId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome<IReadOnlyList<PlacementRow>>> ListAsync(
            DirectoryIdentity requester,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
