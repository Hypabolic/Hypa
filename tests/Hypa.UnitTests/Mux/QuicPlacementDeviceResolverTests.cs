using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Placement.Domain;
using Xunit;

namespace Hypa.UnitTests.Mux;

public sealed class QuicPlacementDeviceResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hypa-qdev-" + Guid.NewGuid().ToString("N"));
    private readonly DevicePairingService _pairing;

    public QuicPlacementDeviceResolverTests()
    {
        _pairing = new DevicePairingService(
            new FileDevicePairingStore(_dir),
            new FileDeviceKeyStore(_dir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task No_option_names_no_device_and_does_not_read_the_pairing_store()
    {
        var resolved = await new QuicPlacementDeviceResolver(_pairing).ResolveAsync(null);

        Assert.True(resolved.Ok, resolved.Detail);
        Assert.Null(resolved.Value);
    }

    [Fact]
    public async Task Named_device_is_accepted_when_two_devices_are_trusted()
    {
        var first = await PairAsync();
        await PairAsync();

        var resolved = await new QuicPlacementDeviceResolver(_pairing).ResolveAsync(first.Value);

        Assert.True(resolved.Ok, resolved.Detail);
        Assert.Equal(first.Value, resolved.Value);
    }

    [Fact]
    public async Task Unknown_named_device_fails()
    {
        await PairAsync();

        var resolved = await new QuicPlacementDeviceResolver(_pairing)
            .ResolveAsync("dev_0000000000000000");

        Assert.False(resolved.Ok);
        Assert.Contains(QuicPlacementDeviceResolver.DeviceOption, resolved.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoked_named_device_fails()
    {
        var device = await PairAsync();
        Assert.True(DeviceId.TryParse(device.Value, out var id));
        var revoked = await _pairing.RevokeAsync(OperatorIdentity.LocalSelfHosted, id);
        Assert.True(revoked.Ok, revoked.Detail);

        var resolved = await new QuicPlacementDeviceResolver(_pairing).ResolveAsync(device.Value);

        Assert.False(resolved.Ok);
    }

    private async Task<PlacementOutcome<string>> StartAsync()
    {
        var started = await _pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        return PlacementOutcome<string>.Success(started.Value!.DeviceId.Value);
    }

    private async Task<PlacementOutcome<string>> PairAsync()
    {
        var started = await _pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        var approved = await _pairing.ApproveAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value!.PairingCode,
            PairingApprover.SelfHostedConsole);
        Assert.True(approved.Ok, approved.Detail);
        return PlacementOutcome<string>.Success(started.Value.DeviceId.Value);
    }
}
