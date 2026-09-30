using System.CommandLine;
using System.Text.Json;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;

namespace Hypa.Cli.Commands;

/// <summary>
/// AOT-safe outbound join client. This is not attach and not mux Connect.
/// </summary>
public sealed class RendezvousCommand
{
    public Command Build()
    {
        var cmd = new Command("rendezvous", "Dial a rendezvous. Join is not attach.");
        cmd.Add(BuildJoin());
        return cmd;
    }

    private static Command BuildJoin()
    {
        var urlOpt = new Option<string>("--url") { Required = true };
        var roleOpt = new Option<string>("--role") { Required = true };
        var placementOpt = new Option<string>("--placement-id") { Required = true };
        var nonceOpt = new Option<string>("--nonce") { Required = true };
        var streamOpt = new Option<string>("--stream-class") { DefaultValueFactory = _ => "control" };
        var audienceOpt = new Option<string>("--audience") { DefaultValueFactory = _ => RendezvousAudiences.Rendezvous };
        var tenantOpt = new Option<string>("--tenant") { DefaultValueFactory = _ => RendezvousTenants.Local };
        var deviceOpt = new Option<string>("--device-id") { Required = true };
        var secretOpt = new Option<string>("--join-secret");
        var ttlOpt = new Option<int>("--capability-ttl-seconds") { DefaultValueFactory = _ => 60 };

        var cmd = new Command("join", "Dial out and join when bootstrap fields match. This is not attach.");
        cmd.Add(urlOpt);
        cmd.Add(roleOpt);
        cmd.Add(placementOpt);
        cmd.Add(nonceOpt);
        cmd.Add(streamOpt);
        cmd.Add(audienceOpt);
        cmd.Add(tenantOpt);
        cmd.Add(deviceOpt);
        cmd.Add(secretOpt);
        cmd.Add(ttlOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var urlRaw = parseResult.GetValue(urlOpt);
            var parsedUrl = RendezvousUrl.ParseOutcome(urlRaw);
            if (!parsedUrl.Ok)
                return WriteFail(parsedUrl.WithoutValue());
            var url = parsedUrl.Value;

            if (!JoinRoleRules.TryParse(parseResult.GetValue(roleOpt), out var role))
                return WriteFail(ConnectivityReasons.BootstrapInvalid, "role must be mux or client");

            if (!JoinNonce.TryParse(parseResult.GetValue(nonceOpt), out var nonce))
                return WriteFail(ConnectivityReasons.BootstrapInvalid, "join nonce is invalid");

            if (!StreamClassRules.TryParse(parseResult.GetValue(streamOpt), out var streamClass))
                return WriteFail(ConnectivityReasons.BootstrapInvalid, "stream class must be control or binary");

            if (!DeviceId.TryParse(parseResult.GetValue(deviceOpt), out var deviceId))
                return WriteFail(ConnectivityReasons.BootstrapInvalid, "device id is invalid");

            var ttl = parseResult.GetValue(ttlOpt);
            if (ttl < 1 || ttl > JoinCapabilityLifetime.MaxSeconds)
            {
                return WriteFail(
                    ConnectivityReasons.BootstrapInvalid,
                    "capability ttl must be between 1 and 300 seconds");
            }

            if (!JoinSecret.TryParse(parseResult.GetValue(secretOpt), out var joinSecret))
            {
                return WriteFail(
                    ConnectivityReasons.ApplicationEncryptionRequired,
                    "join secret is required");
            }

            if (!PlacementId.TryParse(parseResult.GetValue(placementOpt), out var placementId))
                return WriteFail(ConnectivityReasons.BootstrapInvalid, "placement id is invalid");

            var now = DateTimeOffset.UtcNow;
            var issuer = new DeveloperJoinCapabilityIssuer();
            var issued = issuer.Issue(placementId, deviceId, role, nonce, now.AddSeconds(ttl), now, joinSecret);
            if (!issued.Ok || issued.Value is null)
                return WriteFail(issued.WithoutValue());

            using var keys = JoinEphemeralKeyPair.Create();
            var bootstrap = new JoinBootstrap
            {
                ProtocolVersion = ConnectivityProtocolVersion.V0,
                Role = role,
                PlacementId = placementId,
                Nonce = nonce,
                StreamClass = streamClass,
                Capability = issued.Value,
                Audience = parseResult.GetValue(audienceOpt) ?? RendezvousAudiences.Rendezvous,
                TenantScope = parseResult.GetValue(tenantOpt) ?? RendezvousTenants.Local,
                EphPublicKey = keys.PublicKey,
            };
            var stamped = JoinEphAuthenticator.Stamp(bootstrap, keys);
            if (!stamped.Ok || stamped.Value is null)
                return WriteFail(stamped.WithoutValue());

            var validated = JoinBootstrapRules.Validate(stamped.Value);
            if (!validated.Ok)
                return WriteFail(validated.WithoutValue());

            var connected = await OutboundFramedSession.ConnectAsync(url, stamped.Value, cancellationToken: ct);
            if (!connected.Ok || connected.Value is null)
                return WriteFail(connected.WithoutValue());

            await using var session = connected.Value;
            var peerKey = JoinEphPublicKeys.Require(session.Binding.PeerEphPublicKey);
            if (!session.ApplicationEncryptionEnabled || !peerKey.Ok)
            {
                return WriteFail(
                    ConnectivityReasons.ApplicationEncryptionRequired,
                    peerKey.Detail ?? "peer ephemeral public key is required");
            }

            Console.Out.WriteLine(JsonSerializer.Serialize(
                new JoinResultDocument
                {
                    Ok = true,
                    PlacementId = session.Binding.PlacementId.Value,
                    StreamClass = session.Binding.StreamClass,
                    Role = session.Binding.Role,
                    PeerEphPublicKey = session.Binding.PeerEphPublicKey,
                },
                ConnectivityJsonContext.Default.JoinResultDocument));
            return 0;
        });
        return cmd;
    }

    private static int WriteFail(string reason, string detail) =>
        WriteFail(ConnectivityOutcome.Failure(reason, detail));

    private static int WriteFail(ConnectivityOutcome outcome)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(
            new JoinResultDocument
            {
                Ok = false,
                Reason = outcome.Reason ?? ConnectivityReasons.JoinDenied,
                Detail = outcome.Detail ?? "join denied",
                Stage = outcome.Stage,
                AttemptId = outcome.AttemptId,
                Retryable = outcome.Retryable,
            },
            ConnectivityJsonContext.Default.JoinResultDocument));
        return 2;
    }
}
