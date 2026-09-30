using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.Cli.Commands;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class ConnectivityAcceptBindTests
{
    [SkippableFact]
    public async Task Loopback_bind_stays_on_that_address()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        using var cert = AcceptListenCertificate.CreateSelfSigned(IPAddress.Loopback);
        await using var accept = await StartAsync(IPAddress.Loopback, cert, quicCapable: true);
        Assert.Equal(IPAddress.Loopback, accept.TcpListenEndPoint.Address);
        Assert.False(accept.QuicListening);
        Assert.Null(accept.QuicBindAddress);
        Assert.Equal(QuicAcceptBindNotice.SpecificAddressDetail, accept.QuicStartupDetail);
        Assert.Empty(UdpLocalAddresses(accept.Port));
        Assert.True(await TcpConnectsAsync(IPAddress.Loopback, accept.Port));
        var other = await FindAddressReachedByWildcardAsync(IPAddress.Any, IPv4UnicastAddresses());
        Skip.If(other is null, "no second reachable address");
        Assert.False(await TcpConnectsAsync(other!, accept.Port));
        AssertListenDocument(accept, cert, specific: true);
    }

    [SkippableFact]
    public async Task Ipv6_loopback_bind_rejects_ipv4()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        using var cert = AcceptListenCertificate.CreateSelfSigned(IPAddress.IPv6Loopback);
        TcpTlsConnectivityAccept? accept;
        try
        {
            accept = await StartAsync(IPAddress.IPv6Loopback, cert, quicCapable: true);
        }
        catch (SocketException)
        {
            Skip.If(true, "IPv6 loopback is not available");
            return;
        }

        await using var started = accept;
        Assert.Equal(IPAddress.IPv6Loopback, started.TcpListenEndPoint.Address);
        Assert.False(started.QuicListening);
        Assert.Null(started.QuicBindAddress);
        Assert.Equal(QuicAcceptBindNotice.SpecificAddressDetail, started.QuicStartupDetail);
        Assert.Empty(UdpLocalAddresses(started.Port));
        Assert.True(await TcpConnectsAsync(IPAddress.IPv6Loopback, started.Port));
        Assert.False(await TcpConnectsAsync(IPAddress.Loopback, started.Port));
        AssertListenDocument(started, cert, specific: true);
    }

    [SkippableFact]
    public async Task Interface_address_bind_rejects_loopback()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var address = TryUnicastAddress();
        Skip.If(address is null, "no interface address");
        using var cert = AcceptListenCertificate.CreateSelfSigned(address!);
        TcpTlsConnectivityAccept? accept;
        try
        {
            accept = await StartAsync(
                address!,
                cert,
                quicCapable: true,
                trustGate: new LocalSelfHostedJoinTrustGate());
        }
        catch (SocketException)
        {
            Skip.If(true, "interface address did not bind");
            return;
        }

        await using var started = accept;
        Assert.Equal(address, started.TcpListenEndPoint.Address);
        Assert.False(started.QuicListening);
        Assert.Null(started.QuicBindAddress);
        Assert.Empty(UdpLocalAddresses(started.Port));
        Assert.True(await TcpConnectsAsync(address!, started.Port));
        Assert.False(await TcpConnectsAsync(IPAddress.Loopback, started.Port));
        AssertListenDocument(started, cert, specific: true);
    }

    [SkippableFact]
    [Trait("Category", "LiveQuic")]
    public async Task Ipv4_wildcard_accepts_on_more_than_one_address()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        Skip.IfNot(QuicTransportRuntime.IsUsable());
        using var cert = AcceptListenCertificate.CreateSelfSigned(IPAddress.Any);
        await using var accept = await StartAsync(
            IPAddress.Any,
            cert,
            quicCapable: false,
            trustGate: new LocalSelfHostedJoinTrustGate());
        await accept.WaitUntilReadyAsync();
        Assert.Equal(IPAddress.Any, accept.TcpListenEndPoint.Address);
        Assert.True(accept.QuicListening, accept.QuicStartupDetail);
        Assert.NotNull(accept.QuicBindAddress);
        Assert.True(ConnectivityAcceptBindRules.IsWildcard(accept.QuicBindAddress));
        Assert.Contains(UdpLocalAddresses(accept.Port), IsWildcardSocketAddress);
        var other = await FindAddressReachedByWildcardAsync(IPAddress.Any, IPv4UnicastAddresses());
        Skip.If(other is null, "no second reachable address");
        Assert.True(await TcpConnectsAsync(IPAddress.Loopback, accept.Port));
        Assert.True(await TcpConnectsAsync(other!, accept.Port));
        Assert.True(await QuicConnectsAsync(IPAddress.Loopback, accept.Port, cert));
        Assert.True(await QuicConnectsAsync(other!, accept.Port, cert));
        AssertListenDocument(accept, cert, specific: false);
    }

    [SkippableFact]
    [Trait("Category", "LiveQuic")]
    public async Task Ipv6_wildcard_accepts_loopback_families()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        Skip.IfNot(QuicTransportRuntime.IsUsable());
        using var cert = AcceptListenCertificate.CreateSelfSigned(IPAddress.IPv6Any);
        TcpTlsConnectivityAccept? accept;
        try
        {
            accept = await StartAsync(
                IPAddress.IPv6Any,
                cert,
                quicCapable: false,
                trustGate: new LocalSelfHostedJoinTrustGate());
        }
        catch (SocketException)
        {
            Skip.If(true, "IPv6 wildcard is not available");
            return;
        }

        await using var started = accept;
        await started.WaitUntilReadyAsync();
        Assert.True(ConnectivityAcceptBindRules.IsWildcard(started.TcpListenEndPoint.Address));
        Assert.True(started.QuicListening, started.QuicStartupDetail);
        Assert.NotNull(started.QuicBindAddress);
        Assert.True(ConnectivityAcceptBindRules.IsWildcard(started.QuicBindAddress));
        Assert.Contains(UdpLocalAddresses(started.Port), IsWildcardSocketAddress);
        Assert.True(await TcpConnectsAsync(IPAddress.IPv6Loopback, started.Port));
        Assert.True(await QuicConnectsAsync(IPAddress.IPv6Loopback, started.Port, cert));
        Assert.True(await QuicConnectsAsync(IPAddress.Loopback, started.Port, cert));
        var other = await FindAddressReachedByWildcardAsync(IPAddress.IPv6Any, IPv6UnicastAddresses());
        if (other is not null)
        {
            Assert.True(await TcpConnectsAsync(other, started.Port));
            Assert.True(await QuicConnectsAsync(other, started.Port, cert));
        }

        AssertListenDocument(started, cert, specific: false);
    }

    [SkippableFact]
    public async Task Command_specific_bind_writes_the_reason()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var dir = Path.Combine("/tmp", "hypa-abind-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
#pragma warning disable CA1416
        File.SetUnixFileMode(
            dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        var sock = Path.Combine(dir, "hypa.sock");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            var state = new AppState(SessionId.New("accept-bind"));
            var cp = new ControlPlaneService(
                state,
                new UnusedPaneFactory(),
                new NullIntelligence(),
                new NullDetector(),
                leases: new InMemoryLeaseRegistry(),
                attachments: new InMemoryAttachmentRegistry());
            await using var server = new UnixSocketServer(cp, sock);
            await server.StartAsync(CancellationToken.None);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var code = await ConnectivityCommand.RunAcceptAsync(
                "accept-bind",
                IPAddress.Loopback.ToString(),
                port: 0,
                certPath: null,
                json: true,
                new SupportedQuicProbe(),
                cts.Token,
                socketOverride: sock,
                output: stdout,
                error: stderr);
            Assert.True(code is 0 or 1, stderr.ToString());
            Assert.Contains(
                QuicAcceptBindNotice.SpecificAddressDetail,
                stderr.ToString(),
                StringComparison.Ordinal);
            var document = JsonSerializer.Deserialize(
                stdout.ToString().Trim(),
                ConnectivityJsonContext.Default.ConnectivityAcceptListenDocument);
            Assert.NotNull(document);
            Assert.Equal(IPAddress.Loopback.ToString(), document.Bind);
            Assert.False(document.QuicListening);
            Assert.Null(document.QuicBind);
            Assert.Equal(QuicAcceptBindNotice.SpecificAddressDetail, document.QuicDetail);
            Assert.DoesNotContain(sock, stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [SkippableFact]
    public async Task Command_unsupported_probe_writes_the_capability_reason()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var dir = Path.Combine("/tmp", "hypa-abind-off-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
#pragma warning disable CA1416
        File.SetUnixFileMode(
            dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        var sock = Path.Combine(dir, "hypa.sock");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            var state = new AppState(SessionId.New("accept-off"));
            var cp = new ControlPlaneService(
                state,
                new UnusedPaneFactory(),
                new NullIntelligence(),
                new NullDetector(),
                leases: new InMemoryLeaseRegistry(),
                attachments: new InMemoryAttachmentRegistry());
            await using var server = new UnixSocketServer(cp, sock);
            await server.StartAsync(CancellationToken.None);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var code = await ConnectivityCommand.RunAcceptAsync(
                "accept-off",
                IPAddress.Any.ToString(),
                port: 0,
                certPath: null,
                json: true,
                new UnsupportedQuicProbe(),
                cts.Token,
                socketOverride: sock,
                output: stdout,
                error: stderr,
                pairingStore: dir);
            Assert.True(code is 0 or 1, stderr.ToString());
            Assert.Contains(UnsupportedQuicProbe.DetailText, stderr.ToString(), StringComparison.Ordinal);
            var document = JsonSerializer.Deserialize(
                stdout.ToString().Trim(),
                ConnectivityJsonContext.Default.ConnectivityAcceptListenDocument);
            Assert.NotNull(document);
            Assert.False(document.QuicListening);
            Assert.Null(document.QuicBind);
            Assert.Equal(UnsupportedQuicProbe.DetailText, document.QuicDetail);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void AssertListenDocument(
        TcpTlsConnectivityAccept accept,
        X509Certificate2 certificate,
        bool specific)
    {
        var document = ConnectivityCommand.ToListenDocument(accept, certificate);
        Assert.Equal(accept.TcpListenEndPoint.Address.ToString(), document.Bind);
        if (specific)
        {
            Assert.Null(document.QuicBind);
            Assert.False(document.QuicListening);
            return;
        }

        Assert.NotNull(document.QuicBind);
        Assert.True(IPAddress.TryParse(document.QuicBind, out var quicAddress));
        Assert.True(ConnectivityAcceptBindRules.IsWildcard(quicAddress));
        var json = JsonSerializer.Serialize(
            document,
            ConnectivityJsonContext.Default.ConnectivityAcceptListenDocument);
        var parsed = JsonSerializer.Deserialize(
            json,
            ConnectivityJsonContext.Default.ConnectivityAcceptListenDocument);
        Assert.NotNull(parsed);
        Assert.True(IPAddress.TryParse(parsed.QuicBind, out var serializedBind));
        Assert.True(ConnectivityAcceptBindRules.IsWildcard(serializedBind));
        var text = ConnectivityCommand.FormatListen(document);
        Assert.Contains("quic_bind: " + document.QuicBind, text, StringComparison.Ordinal);
    }

    private static async Task<TcpTlsConnectivityAccept> StartAsync(
        IPAddress address,
        X509Certificate2 certificate,
        bool quicCapable,
        IJoinTrustGate? trustGate = null)
    {
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var accept = new TcpTlsConnectivityAccept(
            new ConnectivityAcceptBind
            {
                Host = address.ToString(),
                Port = 0,
                Tls = true,
                TlsServerName = "localhost",
            },
            new IdleUnixBridge(),
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Failure(ConnectivityReasons.Internal, "unused"),
            trustGate: trustGate,
            tlsCertificate: certificate,
            quicProbe: quicCapable ? new SupportedQuicProbe() : null);
        accept.Run();
        await accept.WaitUntilReadyAsync();
        return accept;
    }

    private static async Task<bool> TcpConnectsAsync(IPAddress address, int port)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(address, port, cts.Token);
            return client.Connected;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> QuicConnectsAsync(
        IPAddress address,
        int port,
        X509Certificate2 certificate)
    {
        var path = new QuicBytePath(certificate);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var opened = await path.OpenAsync(
                new BytePathRequest
                {
                    Provider = BytePathProviders.Quic,
                    Endpoint = new BytePathEndpoint
                    {
                        Host = address.ToString(),
                        Port = port,
                        Tls = true,
                        TlsServerName = "localhost",
                    },
                },
                cts.Token);
            if (!opened.Ok || opened.Value is null)
                return false;
            await opened.Value.Lifetime.DisposeAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<IPAddress?> FindAddressReachedByWildcardAsync(
        IPAddress wildcard,
        IEnumerable<IPAddress> candidates)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(wildcard, 0);
            listener.Start();
        }
        catch (SocketException)
        {
            return null;
        }

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using var client = await listener.AcceptTcpClientAsync();
                    }
                    catch (SocketException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                        return;
                    }
                }
            });
            foreach (var address in candidates)
            {
                if (await TcpConnectsAsync(address, port))
                    return address;
            }

            return null;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static IPAddress? TryUnicastAddress() =>
        IPv4UnicastAddresses().FirstOrDefault();

    private static IEnumerable<IPAddress> IPv4UnicastAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(address) || ConnectivityAcceptBindRules.IsWildcard(address))
                    continue;
                yield return address;
            }
        }
    }

    private static IEnumerable<IPAddress> IPv6UnicastAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetworkV6)
                    continue;
                if (IPAddress.IsLoopback(address)
                    || address.IsIPv6LinkLocal
                    || ConnectivityAcceptBindRules.IsWildcard(address))
                {
                    continue;
                }

                yield return address;
            }
        }
    }

    private static List<string> UdpLocalAddresses(int port)
    {
        if (OperatingSystem.IsLinux())
            return ReadProcUdp(port);
        return ReadNetstatUdp(port);
    }

    private static List<string> ReadNetstatUdp(int port)
    {
        var output = ReadProcess("netstat", "-an", "-p", "udp");
        var found = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !parts[0].StartsWith("udp", StringComparison.Ordinal))
                continue;
            var local = parts[3];
            var dot = local.LastIndexOf('.');
            if (dot <= 0)
                continue;
            if (!int.TryParse(local[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed != port)
            {
                continue;
            }

            found.Add(local[..dot]);
        }

        return found;
    }

    private static List<string> ReadProcUdp(int port)
    {
        var portHex = port.ToString("X4", CultureInfo.InvariantCulture);
        var found = new List<string>();
        foreach (var path in new[] { "/proc/net/udp", "/proc/net/udp6" })
        {
            if (!File.Exists(path))
                continue;
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;
                var local = parts[1];
                var colon = local.LastIndexOf(':');
                if (colon < 0)
                    continue;
                if (!local[(colon + 1)..].Equals(portHex, StringComparison.OrdinalIgnoreCase))
                    continue;
                found.Add(local[..colon]);
            }
        }

        return found;
    }

    private static string ReadProcess(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout;
    }

    private static bool IsWildcardSocketAddress(string address) =>
        address is "*" or "0.0.0.0" or "::"
        || (address.Length > 0 && address.All(static c => c == '0'));

    private sealed class IdleUnixBridge : ILocalUnixBridge
    {
        public bool ExposesUnixSocketPath => false;

        public ValueTask<ConnectivityOutcome<MuxBridgeBind>> BindMuxAsync(
            JoinBootstrap bootstrap,
            CancellationToken cancellationToken = default)
        {
            _ = bootstrap;
            _ = cancellationToken;
            return ValueTask.FromResult(ConnectivityOutcome<MuxBridgeBind>.Failure(
                ConnectivityReasons.Internal,
                "idle bridge"));
        }

        public Stream? TryTakeMuxStream(MuxBridgeReservation reservation)
        {
            _ = reservation;
            return null;
        }

        public void ReleaseMuxReservation(MuxBridgeReservation reservation) => _ = reservation;
    }

    private sealed class UnsupportedQuicProbe : IQuicTransportCapabilityProbe
    {
        public const string DetailText = "validated quic library is not available";

        public QuicTransportCapabilityReport Probe() =>
            new()
            {
                RuntimeIdentifier = "test",
                IsSupported = false,
                NativeLibraryFound = false,
                NativeLibraryLocation = MsQuicNativeLocations.Absent,
                QuicProvider = BytePathProviders.Quic,
                FallbackProvider = BytePathProviders.Tcp,
                ZeroRttEnabled = false,
                Reason = ConnectivityReasons.QuicUnsupported,
                Detail = DetailText,
            };
    }

    private sealed class SupportedQuicProbe : IQuicTransportCapabilityProbe
    {
        public QuicTransportCapabilityReport Probe() =>
            new()
            {
                RuntimeIdentifier = "test",
                IsSupported = true,
                NativeLibraryFound = true,
                NativeLibraryLocation = "test",
                QuicProvider = BytePathProviders.Quic,
                FallbackProvider = BytePathProviders.Tcp,
                ZeroRttEnabled = false,
            };
    }

    private sealed class UnusedPaneFactory : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) =>
            throw new InvalidOperationException("unused pane factory");
    }

    private sealed class NullIntelligence : IIntelligencePipeline
    {
        public void OnPaneOutput(PaneId paneId, ReadOnlySpan<byte> data)
        {
        }

        public void OnPaneInput(PaneId paneId, string text)
        {
        }

        public string CompressForAgent(PaneId paneId, string rawText, string? commandHint = null) =>
            rawText;

        public void BindAtomic(PaneId paneId, AtomicBinding binding)
        {
        }

        public void RemovePane(PaneId paneId)
        {
        }
    }

    private sealed class NullDetector : IAgentDetector
    {
        public DetectionResult Detect(string snapshotText, string? processName = null) => new();
    }
}
