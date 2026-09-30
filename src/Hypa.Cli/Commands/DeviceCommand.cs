using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;

namespace Hypa.Cli.Commands;

/// <summary>
/// self-hosted device pairing. This is not a hosted account and not a team identity system.
/// </summary>
public sealed class DeviceCommand
{
    public Command Build()
    {
        var cmd = new Command("device", "Pair and revoke devices. Sign-in is not device trust.");
        cmd.Add(BuildPair());
        cmd.Add(BuildApprove());
        cmd.Add(BuildList());
        cmd.Add(BuildRevoke());
        return cmd;
    }

    private static Command BuildPair()
    {
        var storeOpt = StoreOption();
        var cmd = new Command("pair", "Create a device key and an out-of-band pairing code.");
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var pairing = CreatePairing(parseResult.GetValue(storeOpt), out var error);
            if (pairing is null)
                return WriteFail(error);

            var started = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted, ct);
            if (!started.Ok || started.Value is null)
                return WriteFail(started.WithoutValue());

            var offer = started.Value;
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new DevicePairingOfferDocument
                {
                    Ok = true,
                    OperatorId = offer.OperatorId.Value,
                    DeviceId = offer.DeviceId.Value,
                    PairingCode = offer.PairingCode,
                    QrValue = offer.QrValue,
                    ExpiresAt = offer.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    PublicKeyFingerprint = offer.PublicKeyFingerprint,
                    PublicKeySpkiBase64 = offer.PublicKeySpkiBase64,
                    PairingStore = pairing.PairingStorePath,
                    KeyStore = pairing.KeyStore.DirectoryPath,
                    PlatformKeyStore = pairing.KeyStore.IsPlatformStore,
                },
                ConnectivityJsonContext.Default.DevicePairingOfferDocument));
            return 0;
        });
        return cmd;
    }

    private static Command BuildApprove()
    {
        var storeOpt = StoreOption();
        var codeOpt = new Option<string>("--code")
        {
            Required = true,
            Description = "Pairing code, QR value, or pair JSON from the requesting device.",
        };
        var fromOpt = new Option<string>("--from") { DefaultValueFactory = _ => "self-hosted-console" };
        var approverOpt = new Option<string?>("--approver-device-id");
        var cmd = new Command("approve", "Approve a pairing code, QR value, or pair JSON from the self-hosted console or a trusted device.");
        cmd.Add(storeOpt);
        cmd.Add(codeOpt);
        cmd.Add(fromOpt);
        cmd.Add(approverOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var pairing = CreatePairing(parseResult.GetValue(storeOpt), out var error);
            if (pairing is null)
                return WriteFail(error);

            if (!PairingApproverKindRules.TryParse(parseResult.GetValue(fromOpt), out var kind))
                return WriteFail(ConnectivityReasons.Unauthorized, "approver must be self-hosted-console or trusted-device");

            PairingApprover approver;
            if (kind == PairingApproverKind.SelfHostedConsole)
            {
                approver = PairingApprover.SelfHostedConsole;
            }
            else
            {
                if (!DeviceId.TryParse(parseResult.GetValue(approverOpt), out var approverId))
                    return WriteFail(ConnectivityReasons.Unauthorized, "approver device id is invalid");
                approver = PairingApprover.TrustedDevice(approverId);
            }

            var approved = await pairing.ApproveAsync(
                OperatorIdentity.LocalSelfHosted,
                parseResult.GetValue(codeOpt),
                approver,
                ct);
            if (!approved.Ok || approved.Value is null)
                return WriteFail(approved.WithoutValue());

            Console.Out.WriteLine(JsonSerializer.Serialize(
                new DeviceMutationDocument
                {
                    Ok = true,
                    DeviceId = approved.Value.Id.Value,
                    Status = approved.Value.Status,
                },
                ConnectivityJsonContext.Default.DeviceMutationDocument));
            return 0;
        });
        return cmd;
    }

    private static Command BuildList()
    {
        var storeOpt = StoreOption();
        var cmd = new Command("list", "List paired devices for the local operator.");
        cmd.Add(storeOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var pairing = CreatePairing(parseResult.GetValue(storeOpt), out var error);
            if (pairing is null)
                return WriteFail(error);

            var listed = await pairing.ListAsync(OperatorIdentity.LocalSelfHosted, ct);
            if (!listed.Ok || listed.Value is null)
                return WriteFail(listed.WithoutValue());

            Console.Out.WriteLine(JsonSerializer.Serialize(
                new DeviceListDocument
                {
                    Ok = true,
                    OperatorId = OperatorIdentity.LocalSelfHostedValue,
                    Devices = listed.Value.Select(d => new DeviceListRowDocument
                    {
                        DeviceId = d.Id.Value,
                        Status = d.Status,
                        CreatedAt = d.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                        ApprovedAt = d.ApprovedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                        ApprovedBy = d.ApprovedBy,
                    }).ToList(),
                },
                ConnectivityJsonContext.Default.DeviceListDocument));
            return 0;
        });
        return cmd;
    }

    private static Command BuildRevoke()
    {
        var storeOpt = StoreOption();
        var deviceOpt = new Option<string>("--device-id") { Required = true };
        var cmd = new Command("revoke", "Revoke a device and close its active joins.");
        cmd.Add(storeOpt);
        cmd.Add(deviceOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var pairing = CreatePairing(parseResult.GetValue(storeOpt), out var error);
            if (pairing is null)
                return WriteFail(error);

            if (!DeviceId.TryParse(parseResult.GetValue(deviceOpt), out var deviceId))
                return WriteFail(ConnectivityReasons.Unauthorized, "device id is invalid");

            var revoked = await pairing.RevokeAsync(OperatorIdentity.LocalSelfHosted, deviceId, ct);
            if (!revoked.Ok)
                return WriteFail(revoked);

            Console.Out.WriteLine(JsonSerializer.Serialize(
                new DeviceMutationDocument
                {
                    Ok = true,
                    DeviceId = deviceId.Value,
                    Status = DeviceTrustStatus.Revoked,
                },
                ConnectivityJsonContext.Default.DeviceMutationDocument));
            return 0;
        });
        return cmd;
    }

    private static Option<string?> StoreOption() =>
        new("--store") { Description = "Pairing store directory. File name is pairing.json." };

    private static DevicePairingService? CreatePairing(string? storeDir, out ConnectivityOutcome error)
    {
        string directory;
        try
        {
            directory = string.IsNullOrWhiteSpace(storeDir)
                ? DevicePairingStatePaths.ResolveFromEnvironment()
                : Path.GetFullPath(storeDir);
        }
        catch (InvalidDataException ex)
        {
            error = ConnectivityOutcome.Failure(ConnectivityReasons.Unauthorized, ex.Message);
            return null;
        }

        Directory.CreateDirectory(directory);
        var keys = string.IsNullOrWhiteSpace(storeDir)
            ? PlatformDeviceKeyStore.CreateOrFallback(DevicePairingStatePaths.FallbackKeyDirectory(directory))
            : new FileDeviceKeyStore(DevicePairingStatePaths.FallbackKeyDirectory(directory));
        error = ConnectivityOutcome.Success();
        return new DevicePairingService(new FileDevicePairingStore(directory), keys);
    }

    private static int WriteFail(ConnectivityOutcome outcome) =>
        WriteFail(outcome.Reason ?? ConnectivityReasons.Unauthorized, outcome.Detail ?? "pairing denied");

    private static int WriteFail(string reason, string detail)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(
            new ConnectivityFailureDocument
            {
                Ok = false,
                Reason = reason,
                Detail = detail,
            },
            ConnectivityJsonContext.Default.ConnectivityFailureDocument));
        return 2;
    }
}
