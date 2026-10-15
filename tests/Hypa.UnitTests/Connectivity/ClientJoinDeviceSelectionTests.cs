using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.UnitTests.Connectivity;

public sealed class ClientJoinDeviceSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hypa-joindev-" + Guid.NewGuid().ToString("N"));
    private readonly DevicePairingService _pairing;

    public ClientJoinDeviceSelectionTests()
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
    public async Task Candidates_list_trusted_devices_before_pending_ones_in_store_order()
    {
        var pendingFirst = await StartAsync();
        var trustedFirst = await PairAsync();
        var pendingSecond = await StartAsync();
        var trustedSecond = await PairAsync();

        var listed = await _pairing.ListJoinCandidatesAsync();

        Assert.True(listed.Ok, listed.Detail);
        Assert.Equal(
            [trustedFirst, trustedSecond, pendingFirst, pendingSecond],
            listed.Value!);
    }

    [Fact]
    public async Task Candidates_skip_revoked_devices_and_devices_with_no_key()
    {
        var keyless = await PairAsync();
        var revoked = await PairAsync();
        var kept = await StartAsync();
        var revokedOutcome = await _pairing.RevokeAsync(OperatorIdentity.LocalSelfHosted, revoked);
        Assert.True(revokedOutcome.Ok, revokedOutcome.Detail);
        var service = new DevicePairingService(
            new FileDevicePairingStore(_dir),
            new KeylessKeyStore(new FileDeviceKeyStore(_dir), keyless));

        var listed = await service.ListJoinCandidatesAsync();

        Assert.True(listed.Ok, listed.Detail);
        Assert.Equal([kept], listed.Value!);
    }

    [Fact]
    public async Task Candidates_are_empty_when_no_device_is_paired()
    {
        var listed = await _pairing.ListJoinCandidatesAsync();

        Assert.True(listed.Ok, listed.Detail);
        Assert.Empty(listed.Value!);
    }

    [Fact]
    public async Task A_pending_device_can_mint_because_the_host_decides_trust()
    {
        var pending = await StartAsync();

        var minted = await MintAsync(pending);

        Assert.True(minted.Ok, minted.Detail);
        Assert.Equal(pending, minted.Value!.DeviceId);
    }

    [Fact]
    public async Task Enrolled_device_is_used_even_when_two_devices_are_trusted()
    {
        var first = await PairAsync();
        await PairAsync();

        var minted = await MintAsync(first);

        Assert.True(minted.Ok, minted.Detail);
        Assert.Equal(first, minted.Value!.DeviceId);
    }

    [Fact]
    public async Task Enrolled_device_that_is_revoked_never_falls_back_to_another_device()
    {
        var enrolled = await PairAsync();
        await PairAsync();
        var revoked = await _pairing.RevokeAsync(OperatorIdentity.LocalSelfHosted, enrolled);
        Assert.True(revoked.Ok, revoked.Detail);

        var minted = await MintAsync(enrolled);

        Assert.False(minted.Ok);
        Assert.Contains(enrolled.Value, minted.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enrolled_device_that_is_unknown_never_falls_back_to_another_device()
    {
        await PairAsync();
        Assert.True(DeviceId.TryParse("dev_0000000000000000", out var unknown));

        var minted = await MintAsync(unknown);

        Assert.False(minted.Ok);
    }

    private ValueTask<ConnectivityOutcome<JoinCapability>> MintAsync(DeviceId enrolled)
    {
        Assert.True(PlacementId.TryParse("plc_joinselect01", out var placement));
        return _pairing.MintClientJoinCapabilityAsync(
            placement,
            JoinRole.Client,
            DateTimeOffset.UtcNow.AddMinutes(1),
            enrolled);
    }

    private async Task<DeviceId> StartAsync()
    {
        var started = await _pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        return started.Value!.DeviceId;
    }

    private async Task<DeviceId> PairAsync()
    {
        var started = await _pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        var approved = await _pairing.ApproveAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value!.PairingCode,
            PairingApprover.SelfHostedConsole);
        Assert.True(approved.Ok, approved.Detail);
        return started.Value.DeviceId;
    }

    private sealed class KeylessKeyStore(IDeviceKeyStore inner, DeviceId keyless) : IDeviceKeyStore
    {
        public bool IsPlatformStore => inner.IsPlatformStore;

        public string DirectoryPath => inner.DirectoryPath;

        public ValueTask StorePrivateKeyAsync(
            DeviceId deviceId,
            ReadOnlyMemory<byte> pkcs8,
            CancellationToken cancellationToken = default) =>
            inner.StorePrivateKeyAsync(deviceId, pkcs8, cancellationToken);

        public ValueTask<byte[]?> LoadPrivateKeyAsync(
            DeviceId deviceId,
            CancellationToken cancellationToken = default) =>
            deviceId == keyless
                ? new ValueTask<byte[]?>((byte[]?)null)
                : inner.LoadPrivateKeyAsync(deviceId, cancellationToken);
    }
}
