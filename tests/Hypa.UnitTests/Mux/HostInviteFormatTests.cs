using System.Text.Json;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.UnitTests.Mux;

public sealed class HostInviteFormatTests
{
    [Fact]
    public void A_named_IPv6_address_is_refused_when_the_listener_serves_IPv4_only()
    {
        var ok = HostInviteReach.TrySelectAdvertised(
            ["192.0.2.10"], "2001:db8::10", bindAddress: null, port: 7443, out var hosts, out var error);

        Assert.False(ok);
        Assert.Empty(hosts);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void A_named_IPv6_address_is_kept_when_the_listener_binds_to_the_IPv6_wildcard()
    {
        var ok = HostInviteReach.TrySelectAdvertised(
            ["192.0.2.10"], "2001:db8::10", bindAddress: "::", port: 7443, out var hosts, out var error);

        Assert.True(ok, error);
        Assert.Equal(["2001:db8::10"], hosts);
    }

    [Fact]
    public void An_automatic_list_never_holds_more_than_the_host_limit()
    {
        var interfaces = Enumerable.Range(1, HostInviteReach.MaxHosts + 8)
            .Select(n => "192.0.2." + n)
            .ToList();

        var ok = HostInviteReach.TrySelectAdvertised(
            interfaces, namedAddress: null, bindAddress: null, port: 7443, out var hosts, out var error);

        Assert.True(ok, error);
        Assert.Equal(HostInviteReach.MaxHosts, hosts.Count);
    }

    [Fact]
    public async Task Issue_rejects_wildcard_advertise_host()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-inv-" + Guid.NewGuid().ToString("N"));
        var pairing = new DevicePairingService(
            new FileDevicePairingStore(temp),
            new FileDeviceKeyStore(temp));
        var issued = await pairing.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = "0.0.0.0",
                Port = 7443,
                CertificateSha256 = new string('a', 64),
            });
        Assert.False(issued.Ok);
        Assert.Equal(ConnectivityReasons.InviteInvalid, issued.Reason);
    }

    [Fact]
    public async Task Issue_round_trips_transfer_and_omits_pairing_prefix()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-inv-" + Guid.NewGuid().ToString("N"));
        var pairing = new DevicePairingService(
            new FileDevicePairingStore(temp),
            new FileDeviceKeyStore(temp));
        var issued = await pairing.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = "192.168.1.10",
                Port = 7443,
                CertificateSha256 = new string('a', 64),
                Session = "agents",
                Label = "Edge",
            });
        Assert.True(issued.Ok, issued.Detail);
        Assert.StartsWith(DevicePairingPaths.InvitePrefix, issued.Value!.TransferValue, StringComparison.Ordinal);
        Assert.DoesNotContain(DevicePairingPaths.QrPrefix, issued.Value.TransferValue, StringComparison.Ordinal);
        var decoded = HostInviteCodec.Decode(issued.Value.TransferValue);
        Assert.True(decoded.Ok, decoded.Detail);
        Assert.Equal("192.168.1.10", decoded.Value!.Host);
        Assert.Equal(7443, decoded.Value.Port);
        Assert.Equal(issued.Value.Secret, decoded.Value.Secret);
        Assert.Equal("agents", decoded.Value.Session);
        Assert.Equal(["192.168.1.10"], decoded.Value.Hosts);
    }

    [Fact]
    public async Task Issue_without_a_named_address_carries_each_usable_address()
    {
        var interfaces = new[]
        {
            "127.0.0.1",
            "::1",
            "0.0.0.0",
            "::",
            "[::]",
            "169.254.10.1",
            "fe80::1",
            "fe80::2%eth0",
            "224.0.0.1",
            "ff02::1",
            "::ffff:203.0.113.8",
            "192.168.1.10",
            "203.0.113.8",
            "10.1.2.3",
            "2001:db8::5",
            "192.168.1.10",
        };
        var ipv4Only = HostInviteAdvertisement.Resolve(interfaces, namedAddress: null, bindAddress: "0.0.0.0", port: 7443);
        Assert.True(ipv4Only.Ok, ipv4Only.Detail);
        Assert.Equal(new[] { "192.168.1.10", "203.0.113.8", "10.1.2.3" }, ipv4Only.Value);

        // Only a bind to :: is dual stack, so only then does the invite carry IPv6.
        var selected = HostInviteAdvertisement.Resolve(interfaces, namedAddress: null, bindAddress: "::", port: 7443);
        Assert.True(selected.Ok, selected.Detail);
        var expected = new[] { "192.168.1.10", "203.0.113.8", "10.1.2.3", "2001:db8::5" };
        Assert.Equal(expected, selected.Value);
        var issued = await IssueAsync(selected.Value!);
        var decoded = HostInviteCodec.Decode(issued.TransferValue);
        Assert.True(decoded.Ok, decoded.Detail);
        Assert.Equal(expected, decoded.Value!.Hosts);
        Assert.Equal(expected[0], decoded.Value.Host);
        foreach (var host in decoded.Value.Hosts)
        {
            Assert.False(HostInviteReach.IsWildcard(host));
            Assert.False(HostInviteReach.IsLoopbackHost(host));
            Assert.False(HostInviteReach.IsLinkLocalHost(host));
        }

        Assert.True(HostInviteFormat.TryUnwrap(issued.TransferValue, out var bytes, out _));
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(expected[0], document.RootElement.GetProperty("host").GetString());
        var listed = document.RootElement.GetProperty("hosts").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(expected, listed);
    }

    [Fact]
    public async Task Issue_with_a_named_address_carries_only_that_address()
    {
        var interfaces = new[] { "192.168.1.10", "203.0.113.8", "127.0.0.1" };
        var selected = HostInviteAdvertisement.Resolve(
            interfaces,
            namedAddress: "10.9.9.9",
            bindAddress: "0.0.0.0",
            port: 7443);
        Assert.True(selected.Ok, selected.Detail);
        Assert.Equal(["10.9.9.9"], selected.Value);
        var issued = await IssueAsync(selected.Value!);
        var decoded = HostInviteCodec.Decode(issued.TransferValue);
        Assert.True(decoded.Ok, decoded.Detail);
        Assert.Equal(["10.9.9.9"], decoded.Value!.Hosts);
        Assert.Equal("10.9.9.9", decoded.Value.Host);
    }

    [Fact]
    public async Task Issue_with_a_specific_bind_carries_only_that_address()
    {
        var interfaces = new[] { "192.168.1.10", "203.0.113.8" };
        var selected = HostInviteAdvertisement.Resolve(
            interfaces,
            namedAddress: null,
            bindAddress: "10.8.8.8",
            port: 7443);
        Assert.True(selected.Ok, selected.Detail);
        Assert.Equal(["10.8.8.8"], selected.Value);
        var issued = await IssueAsync(selected.Value!);
        var decoded = HostInviteCodec.Decode(issued.TransferValue);
        Assert.True(decoded.Ok, decoded.Detail);
        Assert.Equal(["10.8.8.8"], decoded.Value!.Hosts);
    }

    [Fact]
    public void Pairing_offer_prefix_is_not_a_host_invite()
    {
        var decoded = HostInviteCodec.Decode(DevicePairingPaths.QrPrefix + "ABCD2345.1.ab.cd");
        Assert.False(decoded.Ok);
        Assert.Equal(ConnectivityReasons.InviteInvalid, decoded.Reason);
    }

    [Fact]
    public async Task Decode_accepts_wrapped_body_and_missing_prefix()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-inv-" + Guid.NewGuid().ToString("N"));
        var pairing = new DevicePairingService(
            new FileDevicePairingStore(temp),
            new FileDeviceKeyStore(temp));
        var issued = await pairing.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = "127.0.0.1",
                Port = 7443,
                CertificateSha256 = new string('a', 64),
                Session = "default",
            });
        Assert.True(issued.Ok, issued.Detail);
        var transfer = issued.Value!.TransferValue;
        var body = transfer[DevicePairingPaths.InvitePrefix.Length..];
        var wrapped = string.Join('\n', Chunk(transfer, 24));
        var fromWrap = HostInviteCodec.Decode(wrapped);
        Assert.True(fromWrap.Ok, fromWrap.Detail);
        Assert.StartsWith(DevicePairingPaths.InvitePrefix, fromWrap.Value!.TransferValue, StringComparison.Ordinal);
        var fromBody = HostInviteCodec.Decode(body);
        Assert.True(fromBody.Ok, fromBody.Detail);
        Assert.Equal("127.0.0.1", fromBody.Value!.Host);
        Assert.Equal("default", fromBody.Value.Session);
    }

    private static IEnumerable<string> Chunk(string value, int size)
    {
        for (var i = 0; i < value.Length; i += size)
            yield return value.Substring(i, Math.Min(size, value.Length - i));
    }

    [Fact]
    public async Task Schema_one_store_loads_without_host_invites()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-inv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var path = Path.Combine(temp, "pairing.json");
        await File.WriteAllTextAsync(
            path,
            """
            {"schema":1,"operator_id":"opr_local","devices":[],"pairing_challenges":[],"join_capabilities":[]}
            """);
        var store = new FileDevicePairingStore(temp);
        var loaded = await store.LoadAsync();
        Assert.Empty(loaded.HostInvites);
        await store.SaveAsync(loaded with
        {
            HostInvites =
            [
                new HostInviteRecord
                {
                    Id = ParseInvite(),
                    SecretSha256 = new string('b', 64),
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                    Consumed = false,
                },
            ],
        });
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"schema\":2", json, StringComparison.Ordinal);
        Assert.Contains("host_invites", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_process_redeem_consumes_secret_and_trusts_device()
    {
        var hostDir = Path.Combine(Path.GetTempPath(), "hypa-invh-" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(Path.GetTempPath(), "hypa-invc-" + Guid.NewGuid().ToString("N"));
        var host = new DevicePairingService(
            new FileDevicePairingStore(hostDir),
            new FileDeviceKeyStore(hostDir));
        var client = new DevicePairingService(
            new FileDevicePairingStore(clientDir),
            new FileDeviceKeyStore(clientDir));
        var issued = await host.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = "192.168.1.10",
                Port = 7443,
                CertificateSha256 = new string('a', 64),
            });
        Assert.True(issued.Ok, issued.Detail);
        var local = await client.EnsureLocalDeviceAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(local.Ok, local.Detail);
        var pkcs8 = await client.KeyStore.LoadPrivateKeyAsync(local.Value!.Id);
        Assert.NotNull(pkcs8);
        var stamped = HostInviteAuthenticator.Stamp(
            new HostInviteRedeemRequest
            {
                InviteId = issued.Value!.InviteId,
                Secret = issued.Value.Secret,
                DeviceId = local.Value.Id,
                DevicePublicKeySpkiBase64 = local.Value.PublicKeySpkiBase64,
                IssuedAt = DateTimeOffset.UtcNow,
                Signature = "",
            },
            pkcs8);
        Assert.True(stamped.Ok, stamped.Detail);
        var redeemed = await host.RedeemAsync(stamped.Value!, DateTimeOffset.UtcNow);
        Assert.True(redeemed.Ok, redeemed.Detail);
        Assert.Equal(DeviceTrustStatus.Trusted, redeemed.Value!.Status);
        Assert.Equal(PairingApproverKind.InviteRedeem, redeemed.Value.ApprovedBy);

        var replay = await host.RedeemAsync(stamped.Value!, DateTimeOffset.UtcNow);
        Assert.True(replay.Ok, replay.Detail);

        var replaySame = await client.EnsureLocalDeviceAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(replaySame.Ok, replaySame.Detail);
        Assert.Equal(local.Value.Id, replaySame.Value!.Id);
        var otherDir = Path.Combine(Path.GetTempPath(), "hypa-invo-" + Guid.NewGuid().ToString("N"));
        var other = new DevicePairingService(
            new FileDevicePairingStore(otherDir),
            new FileDeviceKeyStore(otherDir));
        var otherDevice = await other.EnsureLocalDeviceAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(otherDevice.Ok, otherDevice.Detail);
        var otherKey = await other.KeyStore.LoadPrivateKeyAsync(otherDevice.Value!.Id);
        Assert.NotNull(otherKey);
        var otherStamp = HostInviteAuthenticator.Stamp(
            new HostInviteRedeemRequest
            {
                InviteId = issued.Value.InviteId,
                Secret = issued.Value.Secret,
                DeviceId = otherDevice.Value.Id,
                DevicePublicKeySpkiBase64 = otherDevice.Value.PublicKeySpkiBase64,
                IssuedAt = DateTimeOffset.UtcNow,
                Signature = "",
            },
            otherKey);
        Assert.True(otherStamp.Ok, otherStamp.Detail);
        var second = await host.RedeemAsync(otherStamp.Value!, DateTimeOffset.UtcNow);
        Assert.False(second.Ok);
        Assert.Equal(ConnectivityReasons.InviteConsumed, second.Reason);
    }

    [Fact]
    public async Task Eight_failed_signatures_lock_invite()
    {
        var hostDir = Path.Combine(Path.GetTempPath(), "hypa-invs-" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(Path.GetTempPath(), "hypa-invsc-" + Guid.NewGuid().ToString("N"));
        var host = new DevicePairingService(
            new FileDevicePairingStore(hostDir),
            new FileDeviceKeyStore(hostDir));
        var client = new DevicePairingService(
            new FileDevicePairingStore(clientDir),
            new FileDeviceKeyStore(clientDir));
        var issued = await IssueLoopbackInviteAsync(host);
        var stamped = await StampRedeemAsync(client, issued);
        for (var i = 0; i < HostInviteLimits.MaxFailedAttempts; i++)
        {
            var forged = stamped with { Signature = ForgeSignature(stamped.Signature) };
            var failed = await host.RedeemAsync(forged, DateTimeOffset.UtcNow);
            Assert.False(failed.Ok);
            Assert.Equal(
                i + 1 >= HostInviteLimits.MaxFailedAttempts
                    ? ConnectivityReasons.InviteRateLimited
                    : ConnectivityReasons.Unauthorized,
                failed.Reason);
        }

        var locked = await host.RedeemAsync(stamped, DateTimeOffset.UtcNow);
        Assert.False(locked.Ok);
        Assert.Equal(ConnectivityReasons.InviteRateLimited, locked.Reason);
    }

    [Fact]
    public async Task Eight_failed_documents_lock_invite()
    {
        var hostDir = Path.Combine(Path.GetTempPath(), "hypa-invd-" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(Path.GetTempPath(), "hypa-invdc-" + Guid.NewGuid().ToString("N"));
        var host = new DevicePairingService(
            new FileDevicePairingStore(hostDir),
            new FileDeviceKeyStore(hostDir));
        var client = new DevicePairingService(
            new FileDevicePairingStore(clientDir),
            new FileDeviceKeyStore(clientDir));
        var issued = await IssueLoopbackInviteAsync(host);
        for (var i = 0; i < HostInviteLimits.MaxFailedAttempts; i++)
        {
            var failed = await host.RecordFailedRedeemAttemptAsync(issued.InviteId);
            Assert.False(failed.Ok);
            Assert.Equal(
                i + 1 >= HostInviteLimits.MaxFailedAttempts
                    ? ConnectivityReasons.InviteRateLimited
                    : ConnectivityReasons.InviteInvalid,
                failed.Reason);
        }

        var stamped = await StampRedeemAsync(client, issued);
        var locked = await host.RedeemAsync(stamped, DateTimeOffset.UtcNow);
        Assert.False(locked.Ok);
        Assert.Equal(ConnectivityReasons.InviteRateLimited, locked.Reason);
    }

    [Fact]
    public async Task Expired_invite_fails_closed()
    {
        var clock = new FrozenClock(DateTimeOffset.Parse("2026-09-13T00:00:00Z"));
        var temp = Path.Combine(Path.GetTempPath(), "hypa-invx-" + Guid.NewGuid().ToString("N"));
        var host = new DevicePairingService(
            new FileDevicePairingStore(temp),
            new FileDeviceKeyStore(temp),
            clock);
        var issued = await host.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = "192.168.1.10",
                Port = 7443,
                CertificateSha256 = new string('a', 64),
            });
        Assert.True(issued.Ok, issued.Detail);
        clock.UtcNow = clock.UtcNow.AddMinutes(11);
        var clientDir = Path.Combine(Path.GetTempPath(), "hypa-invxc-" + Guid.NewGuid().ToString("N"));
        var client = new DevicePairingService(
            new FileDevicePairingStore(clientDir),
            new FileDeviceKeyStore(clientDir));
        var local = await client.EnsureLocalDeviceAsync(OperatorIdentity.LocalSelfHosted);
        var pkcs8 = await client.KeyStore.LoadPrivateKeyAsync(local.Value!.Id);
        var stamped = HostInviteAuthenticator.Stamp(
            new HostInviteRedeemRequest
            {
                InviteId = issued.Value!.InviteId,
                Secret = issued.Value.Secret,
                DeviceId = local.Value.Id,
                DevicePublicKeySpkiBase64 = local.Value.PublicKeySpkiBase64,
                IssuedAt = clock.UtcNow,
                Signature = "",
            },
            pkcs8!);
        var redeemed = await host.RedeemAsync(stamped.Value!, clock.UtcNow);
        Assert.False(redeemed.Ok);
        Assert.Equal(ConnectivityReasons.InviteExpired, redeemed.Reason);
    }

    [Fact]
    public void Advertised_list_stops_at_the_host_limit_and_a_longer_invite_is_refused()
    {
        var interfaces = Enumerable.Range(1, HostInviteReach.MaxHosts + 8)
            .Select(i => "192.168.0." + i)
            .ToList();
        var selected = HostInviteAdvertisement.Resolve(interfaces, namedAddress: null, bindAddress: "0.0.0.0", port: 7443);
        Assert.True(selected.Ok, selected.Detail);
        Assert.Equal(HostInviteReach.MaxHosts, selected.Value!.Count);

        var tooMany = Enumerable.Range(1, HostInviteReach.MaxHosts + 1)
            .Select(i => "192.168.1." + i)
            .ToList();
        var encoded = HostInviteCodec.Encode(new HostInvite
        {
            InviteId = ParseInvite(),
            Secret = "s",
            Host = tooMany[0],
            Hosts = tooMany,
            Port = 7443,
            CertificateSha256 = new string('a', 64),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Session = "default",
            TransferValue = "",
        });
        var decoded = HostInviteCodec.Decode(encoded);
        Assert.False(decoded.Ok);
    }

    [Fact]
    public async Task An_explicit_host_list_over_the_limit_is_refused_at_issue()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-inv-" + Guid.NewGuid().ToString("N"));
        var pairing = new DevicePairingService(
            new FileDevicePairingStore(temp),
            new FileDeviceKeyStore(temp));
        var hosts = Enumerable.Range(1, HostInviteReach.MaxHosts + 1).Select(i => "192.168.2." + i).ToList();

        var issued = await pairing.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = hosts[0],
                Hosts = hosts,
                Port = 7443,
                CertificateSha256 = new string('a', 64),
                Session = "default",
            });

        Assert.False(issued.Ok);
    }

    [Fact]
    public void One_address_may_take_longer_than_a_quic_dial_and_the_tcp_fallback()
    {
        Assert.True(
            OutboundHostInviteRedeem.AddressAttemptTimeout
            > QuicTransportPolicy.DialTimeout + QuicTransportPolicy.DialTimeout);
        Assert.True(OutboundHostInviteRedeem.TotalAttemptTimeout > OutboundHostInviteRedeem.AddressAttemptTimeout);
    }

    private static async Task<HostInvite> IssueAsync(IReadOnlyList<string> hosts)
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-inv-" + Guid.NewGuid().ToString("N"));
        var pairing = new DevicePairingService(
            new FileDevicePairingStore(temp),
            new FileDeviceKeyStore(temp));
        var issued = await pairing.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = hosts[0],
                Hosts = hosts,
                Port = 7443,
                CertificateSha256 = new string('a', 64),
                Session = "default",
            });
        Assert.True(issued.Ok, issued.Detail);
        return issued.Value!;
    }

    private static async Task<HostInvite> IssueLoopbackInviteAsync(DevicePairingService host)
    {
        var issued = await host.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = "192.168.1.10",
                Port = 7443,
                CertificateSha256 = new string('a', 64),
            });
        Assert.True(issued.Ok, issued.Detail);
        return issued.Value!;
    }

    private static async Task<HostInviteRedeemRequest> StampRedeemAsync(
        DevicePairingService client,
        HostInvite invite)
    {
        var local = await client.EnsureLocalDeviceAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(local.Ok, local.Detail);
        var pkcs8 = await client.KeyStore.LoadPrivateKeyAsync(local.Value!.Id);
        Assert.NotNull(pkcs8);
        var stamped = HostInviteAuthenticator.Stamp(
            new HostInviteRedeemRequest
            {
                InviteId = invite.InviteId,
                Secret = invite.Secret,
                DeviceId = local.Value.Id,
                DevicePublicKeySpkiBase64 = local.Value.PublicKeySpkiBase64,
                IssuedAt = DateTimeOffset.UtcNow,
                Signature = "",
            },
            pkcs8);
        Assert.True(stamped.Ok, stamped.Detail);
        return stamped.Value!;
    }

    private static string ForgeSignature(string signature)
    {
        var chars = signature.ToCharArray();
        chars[0] = chars[0] == 'A' ? 'B' : 'A';
        return new string(chars);
    }

    private static InviteId ParseInvite()
    {
        Assert.True(InviteId.TryParse("inv_abcd1234ef567890", out var id));
        return id;
    }

    private sealed class FrozenClock : TimeProvider
    {
        public FrozenClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
