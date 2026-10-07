using System.CommandLine;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.ControlPlane;

namespace Hypa.Cli.Commands;

/// <summary>Connectivity capability probes and the accept helper. This is not mux listen.</summary>
public sealed class ConnectivityCommand(IQuicTransportCapabilityProbe quicProbe)
{
    internal const string DefaultAcceptBind = "0.0.0.0";
    internal const int DefaultAcceptPort = 7443;

    public Command Build()
    {
        var cmd = new Command("connectivity", "Connectivity capability probes and accept helper.");
        cmd.Add(BuildQuicCapability());
        cmd.Add(BuildAccept());
        return cmd;
    }

    private Command BuildQuicCapability()
    {
        var jsonOpt = new Option<bool>("--json") { DefaultValueFactory = _ => false };
        var cmd = new Command(
            "quic-capability",
            "Report System.Net.Quic support for this RID. QUIC is not the product default.");
        cmd.Add(jsonOpt);
        cmd.SetAction(async (parseResult, _) =>
        {
            var report = quicProbe.Probe();
            var document = ToDocument(report);
            if (parseResult.GetValue(jsonOpt))
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
                    document,
                    ConnectivityJsonContext.Default.QuicTransportCapabilityDocument));
            }
            else
            {
                WriteHuman(document);
            }
        });
        return cmd;
    }

    private Command BuildAccept()
    {
        var sessionOpt = new Option<string>("--session")
        {
            Required = true,
            Description = "Mux session name. The helper dials the uid-private socket.",
        };
        var bindOpt = new Option<string>("--bind") { DefaultValueFactory = _ => DefaultAcceptBind };
        var portOpt = new Option<int>("--port") { DefaultValueFactory = _ => DefaultAcceptPort };
        var certOpt = new Option<string?>("--cert")
        {
            Description = "PFX certificate. When omitted, the helper writes a local certificate.",
        };
        var pairingStoreOpt = new Option<string?>("--pairing-store")
        {
            Description = "Pairing store directory. File name is pairing.json.",
        };
        var jsonOpt = new Option<bool>("--json") { DefaultValueFactory = _ => false };
        var advertiseOpt = new Option<string[]>("--advertise-host")
        {
            Description = "Address the invite carries. Repeat the option for another address. One address limits the invite to that address.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        // The mux passes its own socket and holds stdin open for the life of the share.
        var socketOpt = new Option<string?>("--socket") { Hidden = true };
        var stdinEofOpt = new Option<bool>("--exit-on-stdin-eof") { Hidden = true };
        var cmd = new Command(
            "accept",
            "Admit direct joins beside a live mux. The mux does not bind a public port.");
        cmd.Add(sessionOpt);
        cmd.Add(bindOpt);
        cmd.Add(portOpt);
        cmd.Add(certOpt);
        cmd.Add(pairingStoreOpt);
        cmd.Add(advertiseOpt);
        cmd.Add(jsonOpt);
        cmd.Add(socketOpt);
        cmd.Add(stdinEofOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (parseResult.GetValue(stdinEofOpt))
                _ = CancelOnStdinEofAsync(Console.OpenStandardInput(), stop);
            return await RunAcceptAsync(
                    parseResult.GetValue(sessionOpt) ?? "",
                    parseResult.GetValue(bindOpt) ?? DefaultAcceptBind,
                    parseResult.GetValue(portOpt),
                    parseResult.GetValue(certOpt),
                    parseResult.GetValue(jsonOpt),
                    quicProbe,
                    stop.Token,
                    socketOverride: parseResult.GetValue(socketOpt),
                    output: Console.Out,
                    error: Console.Error,
                    pairingStore: parseResult.GetValue(pairingStoreOpt),
                    advertiseHosts: parseResult.GetValue(advertiseOpt))
                .ConfigureAwait(false);
        });
        return cmd;
    }

    /// <summary>
    /// The owning mux holds the write end. EOF means the mux exited, however it
    /// exited, so the listener stops instead of serving a dead session.
    /// </summary>
    internal static async Task CancelOnStdinEofAsync(Stream stdin, CancellationTokenSource stop)
    {
        var buffer = new byte[256];
        try
        {
            while (await stdin.ReadAsync(buffer, stop.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            await stdin.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal static async Task<int> RunAcceptAsync(
        string session,
        string bindHost,
        int port,
        string? certPath,
        bool json,
        IQuicTransportCapabilityProbe quicProbe,
        CancellationToken cancellationToken,
        string? socketOverride = null,
        TextWriter? output = null,
        TextWriter? error = null,
        string? pairingStore = null,
        IReadOnlyList<string>? advertiseHosts = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            await error.WriteLineAsync("connectivity accept is Unix-only.")
                .ConfigureAwait(false);
            return 2;
        }

        if (string.IsNullOrWhiteSpace(session))
        {
            await error.WriteLineAsync("session name is required.")
                .ConfigureAwait(false);
            return 2;
        }

        var parsedBind = TryCreateBind(bindHost, port);
        if (!parsedBind.Ok || parsedBind.Value is null)
        {
            await error.WriteLineAsync(parsedBind.Detail ?? "accept bind is invalid")
                .ConfigureAwait(false);
            return 2;
        }

        string socketPath;
        try
        {
            socketPath = string.IsNullOrWhiteSpace(socketOverride)
                ? UnixSocketServer.ResolveSocketPath(session, honorEnvironment: false)
                : Path.GetFullPath(socketOverride);
        }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        if (!File.Exists(socketPath))
        {
            await error.WriteLineAsync("mux session is not listening.")
                .ConfigureAwait(false);
            return 2;
        }

        if (!ConnectivityAcceptBindRules.TryResolveBindAddress(
                parsedBind.Value,
                out var address,
                out var bindDetail))
        {
            await error.WriteLineAsync(bindDetail).ConfigureAwait(false);
            return 2;
        }

        X509Certificate2 certificate;
        if (!string.IsNullOrWhiteSpace(certPath))
        {
            var loaded = AcceptListenCertificate.LoadPfx(certPath);
            if (!loaded.Ok || loaded.Value is null)
            {
                await error.WriteLineAsync(loaded.Detail ?? "certificate file is invalid")
                    .ConfigureAwait(false);
                return 2;
            }

            certificate = loaded.Value;
        }
        else
        {
            var sessionDir = Path.GetDirectoryName(socketPath);
            var stablePath = string.IsNullOrEmpty(sessionDir)
                ? null
                : Path.Combine(sessionDir, "accept.pfx");
            if (!string.IsNullOrEmpty(stablePath) && File.Exists(stablePath))
            {
                var loaded = AcceptListenCertificate.LoadPfx(stablePath);
                if (!loaded.Ok || loaded.Value is null)
                {
                    await error.WriteLineAsync(loaded.Detail ?? "certificate file is invalid")
                        .ConfigureAwait(false);
                    return 2;
                }

                certificate = loaded.Value;
            }
            else
            {
                var pfx = AcceptListenCertificate.CreateSelfSignedPfx(address);
                certificate = X509CertificateLoader.LoadPkcs12(
                    pfx,
                    AcceptListenCertificate.DefaultPfxPassword,
                    X509KeyStorageFlags.Exportable);
                if (!string.IsNullOrEmpty(stablePath))
                {
                    try
                    {
                        AcceptListenCertificate.WritePfx(stablePath, pfx);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        string? resolvedPairingStore = null;
        DevicePairingService? pairing;
        try
        {
            pairing = CreateAcceptPairing(pairingStore, out var pairingError, out resolvedPairingStore);
            if (pairing is null && !ConnectivityAcceptBindRules.IsLoopbackOnly(address))
            {
                await error.WriteLineAsync(pairingError ?? "pairing store is required")
                    .ConfigureAwait(false);
                return 2;
            }
        }
        catch (InvalidDataException ex)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        IJoinTrustGate? trustGate = pairing;
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new LocalUnixBridge(socketPath, new AcceptPathJoin(coordinator));
        var outputGate = new object();
        void WriteOutputLine(string line)
        {
            lock (outputGate)
            {
                output.WriteLine(line);
                output.Flush();
            }
        }

        TcpTlsConnectivityAccept accept;
        try
        {
            accept = new TcpTlsConnectivityAccept(
                parsedBind.Value,
                bridge,
                coordinator,
                AcceptMuxBootstrap.FromClient,
                trustGate,
                tlsCertificate: certificate,
                quicProbe: quicProbe,
                onInviteRedeemed: device => WritePaired(WriteOutputLine, json, device));
        }
        catch (ArgumentException ex)
        {
            certificate.Dispose();
            await bridge.DisposeAsync().ConfigureAwait(false);
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }
        catch (SocketException ex)
        {
            certificate.Dispose();
            await bridge.DisposeAsync().ConfigureAwait(false);
            var message = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"port {port} is already in use"
                : ex.Message;
            await error.WriteLineAsync(message).ConfigureAwait(false);
            return 2;
        }

        pairing?.AttachJoinCloser(accept);

        await using (accept)
        using (certificate)
        {
            accept.Run();
            try
            {
                await accept.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 1;
            }

            string? invite = null;
            if (advertiseHosts is { Count: > 0 })
            {
                if (pairing is null)
                {
                    await error.WriteLineAsync("pairing store is required for a host invite")
                        .ConfigureAwait(false);
                    return 2;
                }

                var issued = await pairing.IssueHostInviteAsync(
                        OperatorIdentity.LocalSelfHosted,
                        new HostInviteIssueRequest
                        {
                            Host = advertiseHosts[0],
                            Hosts = advertiseHosts,
                            Port = accept.Port,
                            CertificateSha256 = AcceptListenCertificate.Sha256Fingerprint(certificate),
                            Session = session,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!issued.Ok || issued.Value is null)
                {
                    await error.WriteLineAsync(issued.Detail ?? "host invite was not issued")
                        .ConfigureAwait(false);
                    return 2;
                }

                invite = issued.Value.TransferValue;
            }

            if (!accept.QuicListening && !string.IsNullOrWhiteSpace(accept.QuicStartupDetail))
                await error.WriteLineAsync(accept.QuicStartupDetail).ConfigureAwait(false);

            var document = ToListenDocument(accept, certificate, invite, resolvedPairingStore);
            if (json)
            {
                WriteOutputLine(JsonSerializer.Serialize(
                    document,
                    ConnectivityJsonContext.Default.ConnectivityAcceptListenDocument));
            }
            else
            {
                WriteOutputLine(FormatListen(document));
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return 0;
    }

    internal static QuicTransportCapabilityDocument ToDocument(QuicTransportCapabilityReport report)
    {
        var ok = report.IsSupported && report.NativeLibraryFound;
        return new QuicTransportCapabilityDocument
        {
            Ok = ok,
            RuntimeIdentifier = report.RuntimeIdentifier,
            IsSupported = report.IsSupported,
            NativeLibraryFound = report.NativeLibraryFound,
            NativeLibraryLocation = report.NativeLibraryLocation,
            QuicProvider = report.QuicProvider,
            FallbackProvider = report.FallbackProvider,
            ZeroRttEnabled = report.ZeroRttEnabled,
            Reason = report.Reason,
            Detail = report.Detail,
        };
    }

    internal static ConnectivityOutcome<ConnectivityAcceptBind> TryCreateBind(string host, int port)
    {
        var bind = new ConnectivityAcceptBind
        {
            Host = host,
            Port = port,
            Tls = true,
            TlsServerName = "localhost",
        };
        if (!ConnectivityAcceptBindRules.TryResolveBindAddress(bind, out _, out var detail))
        {
            return ConnectivityOutcome<ConnectivityAcceptBind>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                detail);
        }

        return ConnectivityOutcome<ConnectivityAcceptBind>.Success(bind);
    }

    internal static ConnectivityAcceptListenDocument ToListenDocument(
        TcpTlsConnectivityAccept accept,
        X509Certificate2 certificate,
        string? invite = null,
        string? pairingStore = null) =>
        new()
        {
            Ok = true,
            Bind = accept.TcpListenEndPoint.Address.ToString(),
            QuicBind = accept.QuicBindAddress?.ToString(),
            Port = accept.Port,
            Tls = accept.UsesTls,
            QuicListening = accept.QuicListening,
            QuicDetail = accept.QuicStartupDetail,
            CertificateSha256 = AcceptListenCertificate.Sha256Fingerprint(certificate),
            Invite = invite,
            PairingStore = pairingStore,
        };

    internal static void WritePaired(Action<string> writeLine, bool json, DeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(writeLine);
        ArgumentNullException.ThrowIfNull(device);
        if (json)
        {
            writeLine(JsonSerializer.Serialize(
                new ConnectivityAcceptPairedDocument
                {
                    Ok = true,
                    Event = ConnectivityAcceptPairedDocument.EventName,
                    DeviceId = device.Id.Value,
                },
                ConnectivityJsonContext.Default.ConnectivityAcceptPairedDocument));
            return;
        }

        writeLine(FormatPaired(device.Id.Value));
    }

    internal static string FormatPaired(string deviceId) =>
        "paired: " + deviceId;

    internal static string FormatListen(ConnectivityAcceptListenDocument document)
    {
        var lines = new[]
        {
            $"bind: {document.Bind}",
            $"port: {document.Port}",
            $"tls: {document.Tls}",
            $"quic_listening: {document.QuicListening}",
            $"certificate_sha256: {document.CertificateSha256}",
        };
        if (!string.IsNullOrWhiteSpace(document.QuicBind))
            lines = [.. lines, $"quic_bind: {document.QuicBind}"];
        if (!string.IsNullOrWhiteSpace(document.QuicDetail))
            lines = [.. lines, $"quic_detail: {document.QuicDetail}"];
        if (!string.IsNullOrWhiteSpace(document.Invite))
            lines = [.. lines, $"invite: {document.Invite}"];

        return string.Join('\n', lines);
    }

    private static DevicePairingService? CreateAcceptPairing(
        string? storeDir,
        out string? error,
        out string? resolvedDirectory)
    {
        error = null;
        resolvedDirectory = null;
        string directory;
        try
        {
            directory = string.IsNullOrWhiteSpace(storeDir)
                ? DevicePairingStatePaths.ResolveFromEnvironment()
                : Path.GetFullPath(storeDir);
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
            return null;
        }

        Directory.CreateDirectory(directory);
        resolvedDirectory = directory;
        var keys = string.IsNullOrWhiteSpace(storeDir)
            ? PlatformDeviceKeyStore.CreateOrFallback(DevicePairingStatePaths.FallbackKeyDirectory(directory))
            : new FileDeviceKeyStore(DevicePairingStatePaths.FallbackKeyDirectory(directory));
        return new DevicePairingService(new FileDevicePairingStore(directory), keys);
    }

    private static void WriteHuman(QuicTransportCapabilityDocument document)
    {
        Console.WriteLine($"runtime_identifier: {document.RuntimeIdentifier}");
        Console.WriteLine($"is_supported: {document.IsSupported}");
        Console.WriteLine($"native_library_found: {document.NativeLibraryFound}");
        Console.WriteLine($"native_library_location: {document.NativeLibraryLocation}");
        Console.WriteLine($"quic_provider: {document.QuicProvider}");
        Console.WriteLine($"fallback_provider: {document.FallbackProvider}");
        Console.WriteLine($"zero_rtt_enabled: {document.ZeroRttEnabled}");
        if (document.Reason is not null)
            Console.WriteLine($"reason: {document.Reason}");
        if (document.Detail is not null)
            Console.WriteLine($"detail: {document.Detail}");
    }
}
