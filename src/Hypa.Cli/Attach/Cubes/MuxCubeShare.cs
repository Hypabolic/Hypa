using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach.Cubes;

/// <summary>The invite this attach minted for the mux listener it last saw.</summary>
internal sealed record MuxShareInvite(
    string Invite,
    int Port,
    string? CertificateSha256,
    IReadOnlyList<string> AdvertisedHosts,
    string? PairingStore = null);

/// <summary>Outcome of a <c>cube.share.*</c> call. Exactly one of the two is set.</summary>
internal sealed record MuxCubeShareCall(CubeShareStatusResult? Status, string? Error)
{
    public static MuxCubeShareCall Ok(CubeShareStatusResult status) => new(status, null);

    public static MuxCubeShareCall Fail(string error) => new(null, error);
}

/// <summary>
/// The home mux owns the share listener. Share talks to the home mux even
/// while the attach shows a remote cube.
/// </summary>
internal interface IMuxCubeSharePort
{
    Task<MuxCubeShareCall> StatusAsync(CancellationToken ct);

    Task<MuxCubeShareCall> StartAsync(string bind, int port, CancellationToken ct);

    Task<MuxCubeShareCall> StopAsync(CancellationToken ct);
}

internal sealed class SocketMuxCubeSharePort(string socketPath) : IMuxCubeSharePort
{
    internal const string OldMuxDetail = "this mux cannot share in the background. Restart the mux to share.";
    internal const string MuxDownDetail = "home mux is not running";

    /// <summary>Start waits for the listener to settle in the mux.</summary>
    private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(20);

    public static SocketMuxCubeSharePort ForSession(string session) =>
        new(UnixSocketServer.ResolveSocketPath(session, honorEnvironment: false));

    public Task<MuxCubeShareCall> StatusAsync(CancellationToken ct) =>
        CallAsync(ProtocolMethods.CubeShareStatus, null, null, ct);

    public Task<MuxCubeShareCall> StartAsync(string bind, int port, CancellationToken ct) =>
        CallAsync(
            ProtocolMethods.CubeShareStart,
            new JsonObject { ["bind"] = bind, ["port"] = port },
            StartBudget,
            ct);

    public Task<MuxCubeShareCall> StopAsync(CancellationToken ct) =>
        CallAsync(ProtocolMethods.CubeShareStop, null, null, ct);

    private async Task<MuxCubeShareCall> CallAsync(
        string method,
        JsonObject? parameters,
        TimeSpan? timeout,
        CancellationToken ct)
    {
        try
        {
            await using var client = new ControlPlaneClient(socketPath);
            await client.ConnectAsync(ct).ConfigureAwait(false);
            var result = await client.CallAsync(method, parameters, ct, timeout).ConfigureAwait(false);
            var status = result.Deserialize(ProtocolJsonContext.Default.CubeShareStatusResult);
            return status is null
                ? MuxCubeShareCall.Fail("mux share reply is invalid")
                : MuxCubeShareCall.Ok(status);
        }
        catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.MethodNotFound)
        {
            return MuxCubeShareCall.Fail(OldMuxDetail);
        }
        catch (ControlPlaneException ex)
        {
            return MuxCubeShareCall.Fail(ex.Message);
        }
        catch (ControlPlaneClientTimeoutException)
        {
            return MuxCubeShareCall.Fail("home mux did not answer");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return MuxCubeShareCall.Fail(MuxDownDetail);
        }
        catch (JsonException)
        {
            return MuxCubeShareCall.Fail("mux share reply is invalid");
        }
    }
}

/// <summary>Mints a host invite for the mux share listener.</summary>
internal interface IShareInviteIssuer
{
    Task<ConnectivityOutcome<string>> IssueAsync(
        string session,
        int port,
        string? certificateSha256,
        string? pairingStore,
        IReadOnlyList<string> advertiseHosts,
        CancellationToken ct);
}

/// <summary>
/// Pairing store invites. The fingerprint comes from the mux over its
/// uid-private socket, so no loopback probe of the port is needed.
/// </summary>
internal sealed class PairingShareInviteIssuer : IShareInviteIssuer
{
    public static PairingShareInviteIssuer Instance { get; } = new();

    public async Task<ConnectivityOutcome<string>> IssueAsync(
        string session,
        int port,
        string? certificateSha256,
        string? pairingStore,
        IReadOnlyList<string> advertiseHosts,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(certificateSha256))
        {
            return ConnectivityOutcome<string>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "share listener has no certificate");
        }

        if (advertiseHosts is not { Count: > 0 })
        {
            return ConnectivityOutcome<string>.Failure(
                ConnectivityReasons.InviteInvalid,
                "no reachable address");
        }

        DevicePairingService pairing;
        try
        {
            // The listener reports its store. The attach environment can differ
            // (HYPA_PAIRING_STORE, XDG_STATE_HOME), and an invite in another
            // store would never redeem.
            var directory = string.IsNullOrWhiteSpace(pairingStore)
                ? DevicePairingStatePaths.ResolveFromEnvironment()
                : Path.GetFullPath(pairingStore);
            Directory.CreateDirectory(directory);
            pairing = new DevicePairingService(
                new FileDevicePairingStore(directory),
                PlatformDeviceKeyStore.CreateOrFallback(
                    DevicePairingStatePaths.FallbackKeyDirectory(directory)));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return ConnectivityOutcome<string>.Failure(ConnectivityReasons.BootstrapInvalid, ex.Message);
        }

        var issued = await pairing.IssueHostInviteAsync(
                OperatorIdentity.LocalSelfHosted,
                new HostInviteIssueRequest
                {
                    Host = advertiseHosts[0],
                    Hosts = advertiseHosts,
                    Port = port,
                    CertificateSha256 = certificateSha256,
                    Session = session,
                },
                ct)
            .ConfigureAwait(false);
        return issued.Ok && issued.Value is not null
            ? ConnectivityOutcome<string>.Success(issued.Value.TransferValue)
            : ConnectivityOutcome<string>.Failure(
                issued.Reason ?? ConnectivityReasons.InviteInvalid,
                issued.Detail ?? "host invite was not issued");
    }
}
