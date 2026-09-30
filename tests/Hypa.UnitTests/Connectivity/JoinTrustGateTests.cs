using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.UnitTests.Connectivity;

public sealed class JoinTrustGateTests
{
    [Fact]
    public async Task Established_join_stays_valid_after_capability_expiry()
    {
        var fixture = await PairAsync("estexp01", "plc_estexp", admit: true);
        var later = fixture.ExpiresAt.AddMinutes(5);

        Assert.False(await fixture.Pairing.IsJoinStillValidAsync(fixture.Bootstrap, later));
        Assert.True(await fixture.Pairing.IsEstablishedJoinStillValidAsync(fixture.Bootstrap, later));
    }

    [Fact]
    public async Task Admit_rejects_expired_capability()
    {
        var fixture = await PairAsync("admexpir", "plc_admexp", admit: false);
        var later = fixture.ExpiresAt.AddMinutes(1);

        var admitted = await fixture.Pairing.AdmitAsync(fixture.Bootstrap, later);

        Assert.False(admitted.Ok);
        Assert.Equal(ConnectivityReasons.JoinExpired, admitted.Reason);
        Assert.Equal("join capability expired", admitted.Detail);
    }

    [Fact]
    public async Task Unspent_capability_is_not_an_established_join()
    {
        var fixture = await PairAsync("unspent1", "plc_unspnt", admit: false);

        Assert.False(await fixture.Pairing.IsEstablishedJoinStillValidAsync(
            fixture.Bootstrap,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Established_join_fails_after_revoke()
    {
        var fixture = await PairAsync("revtrust", "plc_revtru", admit: true);

        var revoked = await fixture.Pairing.RevokeAsync(
            OperatorIdentity.LocalSelfHosted,
            fixture.DeviceId);
        Assert.True(revoked.Ok, revoked.Detail);

        Assert.False(await fixture.Pairing.IsEstablishedJoinStillValidAsync(
            fixture.Bootstrap,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Established_join_fails_when_capability_is_deleted()
    {
        var fixture = await PairAsync("delcap01", "plc_delcap", admit: true);
        await ClearCapabilitiesAsync(fixture.Store);

        Assert.False(await fixture.Pairing.IsEstablishedJoinStillValidAsync(
            fixture.Bootstrap,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Established_join_fails_on_operator_mismatch()
    {
        var fixture = await PairAsync("oprmiss1", "plc_oprmis", admit: true);
        Assert.True(OperatorIdentity.TryParse("opr_other1", out var other));
        var mismatched = fixture.Bootstrap with
        {
            Capability = fixture.Bootstrap.Capability with { OperatorId = other },
        };

        Assert.False(await fixture.Pairing.IsEstablishedJoinStillValidAsync(
            mismatched,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Established_join_fails_on_nonce_mismatch()
    {
        var fixture = await PairAsync("noncmis1", "plc_noncmi", admit: true);
        Assert.True(JoinNonce.TryParse("nonceoth", out var otherNonce));
        var mismatched = fixture.Bootstrap with
        {
            Nonce = otherNonce,
            Capability = fixture.Bootstrap.Capability with { Nonce = otherNonce },
        };

        Assert.False(await fixture.Pairing.IsEstablishedJoinStillValidAsync(
            mismatched,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Established_join_fails_when_device_trust_is_lost()
    {
        var fixture = await PairAsync("trustls1", "plc_trstls", admit: true);
        await MutateDeviceAsync(
            fixture.Store,
            fixture.DeviceId,
            device => device with { Status = DeviceTrustStatus.Pending });

        Assert.False(await fixture.Pairing.IsEstablishedJoinStillValidAsync(
            fixture.Bootstrap,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Established_join_fails_when_pairing_is_removed()
    {
        var fixture = await PairAsync("pairrm01", "plc_pairrm", admit: true);
        var loaded = await fixture.Store.LoadAsync();
        await fixture.Store.SaveAsync(loaded with
        {
            Devices = [],
            JoinCapabilities = [],
        });

        Assert.False(await fixture.Pairing.IsEstablishedJoinStillValidAsync(
            fixture.Bootstrap,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Established_join_fails_when_capability_spend_is_reversed()
    {
        var fixture = await PairAsync("spendrev", "plc_spndrv", admit: true);
        var loaded = await fixture.Store.LoadAsync();
        var capabilities = loaded.JoinCapabilities
            .Select(c => c with { Spent = false })
            .ToList();
        await fixture.Store.SaveAsync(loaded with { JoinCapabilities = capabilities });

        Assert.False(await fixture.Pairing.IsEstablishedJoinStillValidAsync(
            fixture.Bootstrap,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Local_self_hosted_established_join_ignores_expiry()
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var bootstrap = LocalClientBootstrap(now, now.AddMinutes(1));
        var gate = new LocalSelfHostedJoinTrustGate();
        var later = now.AddMinutes(6);

        var admitted = await gate.AdmitAsync(bootstrap, later);
        Assert.False(admitted.Ok);
        Assert.Equal(ConnectivityReasons.JoinExpired, admitted.Reason);
        Assert.False(await gate.IsJoinStillValidAsync(bootstrap, later));
        Assert.True(await gate.IsEstablishedJoinStillValidAsync(bootstrap, later));
    }

    [Fact]
    public async Task Local_self_hosted_established_join_fails_for_foreign_operator()
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var bootstrap = LocalClientBootstrap(now, now.AddMinutes(1));
        Assert.True(OperatorIdentity.TryParse("opr_other1", out var other));
        var mismatched = bootstrap with
        {
            Capability = bootstrap.Capability with { OperatorId = other },
        };
        var gate = new LocalSelfHostedJoinTrustGate();

        Assert.False(await gate.IsEstablishedJoinStillValidAsync(mismatched, now));
    }

    private static async Task<PairedFixture> PairAsync(string nonceValue, string placementValue, bool admit)
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-jtrust-" + Guid.NewGuid().ToString("N"));
        var store = new FileDevicePairingStore(temp);
        var keys = new FileDeviceKeyStore(temp);
        var pairing = new DevicePairingService(store, keys);
        var started = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        var approved = await pairing.ApproveAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value!.PairingCode,
            PairingApprover.SelfHostedConsole);
        Assert.True(approved.Ok, approved.Detail);
        Assert.True(JoinNonce.TryParse(nonceValue, out var nonce));
        Assert.True(PlacementId.TryParse(placementValue, out var placement));
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var issued = await pairing.IssueJoinCapabilityAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value.DeviceId,
            placement,
            JoinRole.Client,
            nonce,
            expires);
        Assert.True(issued.Ok, issued.Detail);
        var bootstrap = new JoinBootstrap
        {
            ProtocolVersion = ConnectivityProtocolVersion.V0,
            Role = JoinRole.Client,
            PlacementId = placement,
            Nonce = nonce,
            StreamClass = StreamClass.Control,
            Capability = issued.Value!,
            Audience = RendezvousAudiences.Rendezvous,
            TenantScope = RendezvousTenants.Local,
            EphPublicKey = JoinEphemeralKeyPair.CreatePublicKey(),
        };
        var signed = await JoinDeviceAuthenticator.StampAsync(bootstrap, keys);
        Assert.True(signed.Ok, signed.Detail);
        bootstrap = signed.Value!;
        if (admit)
        {
            var admitted = await pairing.AdmitAsync(bootstrap, DateTimeOffset.UtcNow);
            Assert.True(admitted.Ok, admitted.Detail);
        }

        return new PairedFixture(pairing, store, bootstrap, expires, started.Value.DeviceId);
    }

    private static async Task ClearCapabilitiesAsync(FileDevicePairingStore store)
    {
        var loaded = await store.LoadAsync();
        await store.SaveAsync(loaded with { JoinCapabilities = [] });
    }

    private static async Task MutateDeviceAsync(
        FileDevicePairingStore store,
        DeviceId deviceId,
        Func<DeviceRecord, DeviceRecord> mutate)
    {
        var loaded = await store.LoadAsync();
        var devices = loaded.Devices.ToList();
        var index = devices.FindIndex(d => d.Id.Value == deviceId.Value);
        Assert.True(index >= 0);
        devices[index] = mutate(devices[index]);
        await store.SaveAsync(loaded with { Devices = devices });
    }

    private static JoinBootstrap LocalClientBootstrap(DateTimeOffset now, DateTimeOffset expires)
    {
        Assert.True(JoinNonce.TryParse("local001", out var nonce));
        Assert.True(DeviceId.TryParse("dev_cli001", out var device));
        Assert.True(PlacementId.TryParse("plc_local1", out var placement));
        var issued = new DeveloperJoinCapabilityIssuer().Issue(
            placement,
            device,
            JoinRole.Client,
            nonce,
            expires,
            now);
        Assert.True(issued.Ok, issued.Detail);
        return new JoinBootstrap
        {
            ProtocolVersion = ConnectivityProtocolVersion.V0,
            Role = JoinRole.Client,
            PlacementId = placement,
            Nonce = nonce,
            StreamClass = StreamClass.Control,
            Capability = issued.Value!,
            Audience = RendezvousAudiences.Rendezvous,
            TenantScope = RendezvousTenants.Local,
            EphPublicKey = JoinEphemeralKeyPair.CreatePublicKey(),
        };
    }

    private sealed record PairedFixture(
        DevicePairingService Pairing,
        FileDevicePairingStore Store,
        JoinBootstrap Bootstrap,
        DateTimeOffset ExpiresAt,
        DeviceId DeviceId);
}
