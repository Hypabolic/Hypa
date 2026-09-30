using Hypa.Cli.Mux;
using Hypa.Connectivity.Domain;
using Hypa.Placement.Domain;
using PlacementId = Hypa.Placement.Domain.PlacementId;
using Xunit;

namespace Hypa.UnitTests.Mux;

public sealed class CubesConnectJoinMaterialTests
{
    [Fact]
    public async Task Environment_source_returns_null_when_only_lab_flag_is_set()
    {
        var source = new EnvironmentCubesConnectJoinMaterialSource(
            getenv: key => key == "HYPA_LAB" ? "1" : null);
        var material = await source.GetAsync(Placement());
        Assert.Null(material);
    }

    [Fact]
    public async Task Environment_source_does_not_fill_a_missing_secret_from_the_lab_flag()
    {
        var source = new EnvironmentCubesConnectJoinMaterialSource(
            getenv: key => key switch
            {
                "HYPA_LAB" => "1",
                EnvironmentCubesConnectJoinMaterialSource.UrlVariable => "ws://127.0.0.1:7422",
                EnvironmentCubesConnectJoinMaterialSource.NonceVariable => "nonce001",
                EnvironmentCubesConnectJoinMaterialSource.DeviceVariable => "dev_cli001",
                _ => null,
            });
        var material = await source.GetAsync(Placement());
        Assert.Null(material);
    }

    [Fact]
    public async Task Environment_source_builds_join_material_from_explicit_values()
    {
        var secretBase64 = Convert.ToBase64String(new byte[JoinSecret.SizeBytes]);
        Assert.True(JoinSecret.TryParse(secretBase64, out _));
        var source = new EnvironmentCubesConnectJoinMaterialSource(
            getenv: key => key switch
            {
                EnvironmentCubesConnectJoinMaterialSource.UrlVariable => "ws://127.0.0.1:7422",
                EnvironmentCubesConnectJoinMaterialSource.SecretVariable => secretBase64,
                EnvironmentCubesConnectJoinMaterialSource.NonceVariable => "nonce001",
                EnvironmentCubesConnectJoinMaterialSource.DeviceVariable => "dev_cli001",
                _ => null,
            });

        var material = await source.GetAsync(Placement());

        Assert.NotNull(material);
        Assert.Equal("ws://127.0.0.1:7422", material!.Url.Value);
        Assert.Equal("plc_join01", material.Bootstrap.PlacementId.Value);
        Assert.Equal("nonce001", material.Bootstrap.Nonce.Value);
        Assert.Equal("dev_cli001", material.Bootstrap.Capability.DeviceId.Value);
    }

    private static PlacementRecord Placement() =>
        new()
        {
            Id = PlacementId.Parse("plc_join01"),
            Owner = DirectoryIdentity.Parse("local:test"),
            DisplayName = "Lab",
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = MuxIdentity.Parse("mux_agents"),
            Reachability = PlacementReachability.Reachable,
            LastSeen = DateTimeOffset.UtcNow,
        };
}
