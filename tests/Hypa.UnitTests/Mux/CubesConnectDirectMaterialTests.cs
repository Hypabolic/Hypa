using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Placement.Domain;
using PlacementId = Hypa.Placement.Domain.PlacementId;
using Xunit;

namespace Hypa.UnitTests.Mux;

public sealed class CubesConnectDirectMaterialTests
{
    [Fact]
    public async Task Environment_source_builds_direct_endpoint_from_catalog_target()
    {
        var secretBase64 = Convert.ToBase64String(new byte[JoinSecret.SizeBytes]);
        Assert.True(JoinSecret.TryParse(secretBase64, out _));
        var source = new EnvironmentCubesConnectDirectMaterialSource(
            getenv: key => key switch
            {
                EnvironmentCubesConnectDirectMaterialSource.SecretVariable => secretBase64,
                EnvironmentCubesConnectDirectMaterialSource.NonceVariable => "nonce001",
                EnvironmentCubesConnectDirectMaterialSource.DeviceVariable => "dev_cli001",
                _ => null,
            });
        var placement = new PlacementRecord
        {
            Id = PlacementId.Parse("plc_quic01"),
            Owner = DirectoryIdentity.Parse("local:test"),
            DisplayName = "Edge",
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = MuxIdentity.Parse("mux_agents"),
            Reachability = PlacementReachability.Reachable,
            LastSeen = DateTimeOffset.UtcNow,
            Quic = new QuicPlacementProfile
            {
                Id = "plc_quic01",
                Label = "Edge",
                Target = "192.168.1.10:8443",
                Session = "agents",
                Enabled = true,
                CertificateSha256 = new string('a', 64),
            },
        };

        var material = await source.GetAsync(placement);

        Assert.NotNull(material);
        Assert.Equal("192.168.1.10", material!.Endpoint.Host);
        Assert.Equal(8443, material.Endpoint.Port);
        Assert.True(material.Endpoint.Tls);
        Assert.Equal("192.168.1.10", material.Endpoint.TlsServerName);
        Assert.Equal("plc_quic01", material.Bootstrap.PlacementId.Value);
        Assert.Equal(new string('a', 64), material.CertificateSha256);
    }

    [Fact]
    public async Task Pairing_source_fails_closed_when_device_is_unpaired()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-pair-" + Guid.NewGuid().ToString("N"));
        var pairing = new DevicePairingService(
            new FileDevicePairingStore(temp),
            new FileDeviceKeyStore(temp));
        var source = new PairingCubesConnectDirectMaterialSource(pairing);
        var material = await source.GetAsync(EnabledPlacement());
        Assert.Null(material);
    }

    [Fact]
    public async Task Pairing_source_mints_with_enrolled_device_id()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-pair-" + Guid.NewGuid().ToString("N"));
        var pairing = new DevicePairingService(
            new FileDevicePairingStore(temp),
            new FileDeviceKeyStore(temp));
        var first = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(first.Ok, first.Detail);
        var second = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(second.Ok, second.Detail);
        Assert.NotEqual(first.Value!.DeviceId, second.Value!.DeviceId);
        var source = new PairingCubesConnectDirectMaterialSource(pairing);
        var placement = EnabledPlacement();
        placement = placement with
        {
            Quic = placement.Quic! with { EnrolledDeviceId = second.Value.DeviceId.Value },
        };
        var material = await source.GetAsync(placement);
        Assert.NotNull(material);
        Assert.Equal(second.Value.DeviceId, material!.Bootstrap.Capability.DeviceId);
    }

    [Fact]
    public async Task Pairing_source_issues_capability_without_lab_secret()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-pair-" + Guid.NewGuid().ToString("N"));
        var pairing = new DevicePairingService(
            new FileDevicePairingStore(temp),
            new FileDeviceKeyStore(temp));
        var started = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        var source = new PairingCubesConnectDirectMaterialSource(pairing);
        var material = await source.GetAsync(EnabledPlacement());
        Assert.NotNull(material);
        Assert.True(material!.Bootstrap.Capability.Secret.IsEmpty);
        Assert.Equal(started.Value!.DeviceId, material.Bootstrap.Capability.DeviceId);
        Assert.Equal(new string('a', 64), material.CertificateSha256);
        Assert.NotNull(material.DeviceKeys);
    }

    [Fact]
    public async Task Pairing_source_fails_closed_without_certificate_pin()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-pair-" + Guid.NewGuid().ToString("N"));
        var pairing = new DevicePairingService(
            new FileDevicePairingStore(temp),
            new FileDeviceKeyStore(temp));
        var started = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        var source = new PairingCubesConnectDirectMaterialSource(pairing);
        var placement = EnabledPlacement();
        placement = placement with
        {
            Quic = placement.Quic! with { CertificateSha256 = null },
        };
        var material = await source.GetAsync(placement);
        Assert.Null(material);
    }

    [Fact]
    public async Task Product_direct_join_without_pin_does_not_open_untrusted_path()
    {
        Assert.True(JoinNonce.TryParse("nonce001", out var nonce));
        Assert.True(DeviceId.TryParse("dev_cli001", out var deviceId));
        Assert.True(Hypa.Connectivity.Domain.PlacementId.TryParse("plc_quic01", out var joinPlacement));
        var material = new CubesConnectDirectMaterial
        {
            Endpoint = new BytePathEndpoint
            {
                Host = "192.168.1.10",
                Port = 443,
                Tls = true,
                TlsServerName = "192.168.1.10",
            },
            Bootstrap = new JoinBootstrap
            {
                ProtocolVersion = ConnectivityProtocolVersion.V0,
                Role = JoinRole.Client,
                PlacementId = joinPlacement,
                Nonce = nonce,
                StreamClass = StreamClass.Control,
                Capability = new JoinCapability
                {
                    OperatorId = OperatorIdentity.LocalSelfHosted,
                    PlacementId = joinPlacement,
                    DeviceId = deviceId,
                    Role = JoinRole.Client,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
                    Nonce = nonce,
                },
                Audience = RendezvousAudiences.Rendezvous,
                TenantScope = RendezvousTenants.Local,
            },
        };

        var session = await CubesConnectDirectOutboundJoin.ConnectAsync(material);
        Assert.Null(session);
    }

    [Fact]
    public async Task Environment_source_returns_null_when_only_lab_flag_is_set()
    {
        var source = new EnvironmentCubesConnectDirectMaterialSource(
            getenv: key => key == "HYPA_LAB" ? "1" : null);
        var material = await source.GetAsync(EnabledPlacement());
        Assert.Null(material);
    }

    [Fact]
    public async Task Environment_source_does_not_fill_a_missing_secret_from_the_lab_flag()
    {
        var source = new EnvironmentCubesConnectDirectMaterialSource(
            getenv: key => key switch
            {
                "HYPA_LAB" => "1",
                EnvironmentCubesConnectDirectMaterialSource.NonceVariable => "nonce001",
                EnvironmentCubesConnectDirectMaterialSource.DeviceVariable => "dev_cli001",
                _ => null,
            });
        var material = await source.GetAsync(EnabledPlacement());
        Assert.Null(material);
    }

    [Fact]
    public async Task Environment_source_returns_null_for_disabled_quic_row()
    {
        var source = new EnvironmentCubesConnectDirectMaterialSource(getenv: _ => null);
        var placement = new PlacementRecord
        {
            Id = PlacementId.Parse("plc_quic01"),
            Owner = DirectoryIdentity.Parse("local:test"),
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
                Enabled = false,
            },
        };

        var material = await source.GetAsync(placement);
        Assert.Null(material);
    }

    private static PlacementRecord EnabledPlacement() =>
        new()
        {
            Id = PlacementId.Parse("plc_quic01"),
            Owner = DirectoryIdentity.Parse("local:test"),
            DisplayName = "Edge",
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = MuxIdentity.Parse("mux_agents"),
            Reachability = PlacementReachability.Reachable,
            LastSeen = DateTimeOffset.UtcNow,
            Quic = new QuicPlacementProfile
            {
                Id = "plc_quic01",
                Label = "Edge",
                Target = "192.168.1.10:8443",
                Session = "agents",
                Enabled = true,
                CertificateSha256 = new string('a', 64),
            },
        };
}
