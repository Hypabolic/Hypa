using System.Net;
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

public sealed class ConnectivityAcceptCommandTests
{
    [Fact]
    public void Connectivity_command_includes_accept()
    {
        var command = new ConnectivityCommand(new QuicTransportCapabilityProbe()).Build();
        Assert.Contains(command.Subcommands, item => item.Name == "accept");
    }

    [Fact]
    public void Mux_serve_has_no_public_listen_flag()
    {
        var serve = new MuxCommand().Build().Subcommands.Single(item => item.Name == "serve");
        Assert.DoesNotContain(
            serve.Options,
            option => option.Name.Contains("listen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Non_literal_bind_fails_before_listen()
    {
        var parsed = ConnectivityCommand.TryCreateBind("localhost", ConnectivityCommand.DefaultAcceptPort);
        Assert.False(parsed.Ok);
        Assert.Contains("literal ip", parsed.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_certificate_file_fails_closed()
    {
        var loaded = AcceptListenCertificate.LoadPfx(Path.Combine(Path.GetTempPath(), "missing-accept.pfx"));
        Assert.False(loaded.Ok);
        Assert.Contains("missing", loaded.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Paired_json_round_trips_event()
    {
        var line = JsonSerializer.Serialize(
            new ConnectivityAcceptPairedDocument
            {
                Ok = true,
                Event = ConnectivityAcceptPairedDocument.EventName,
                DeviceId = "dev_peer01",
            },
            ConnectivityJsonContext.Default.ConnectivityAcceptPairedDocument);
        Assert.True(ConnectivityAcceptPairedDocument.TryRead(line, out var document));
        Assert.Equal("dev_peer01", document.DeviceId);
        Assert.Equal("paired: dev_peer01", ConnectivityCommand.FormatPaired("dev_peer01"));
        Assert.False(ConnectivityAcceptPairedDocument.TryRead("""{"ok":true,"bind":"127.0.0.1"}""", out _));
    }

    [Fact]
    public void Listen_text_omits_unix_socket_path()
    {
        using var cert = AcceptListenCertificate.CreateSelfSigned(IPAddress.Loopback);
        var document = new ConnectivityAcceptListenDocument
        {
            Ok = true,
            Bind = "127.0.0.1",
            Port = 7443,
            Tls = true,
            QuicListening = false,
            CertificateSha256 = AcceptListenCertificate.Sha256Fingerprint(cert),
        };
        var text = ConnectivityCommand.FormatListen(document);
        Assert.DoesNotContain("hypa.sock", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".sock", text, StringComparison.Ordinal);
        Assert.Contains("port: 7443", text, StringComparison.Ordinal);
        Assert.Contains("certificate_sha256:", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Accept_helper_starts_for_a_live_mux_session()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX socket fixture");
        var dir = Path.Combine("/tmp", "hypa-acli-" + Guid.NewGuid().ToString("N")[..8]);
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
            var state = new AppState(SessionId.New("accept-cli"));
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
                "accept-cli",
                IPAddress.Loopback.ToString(),
                port: 0,
                certPath: null,
                json: false,
                new DisabledQuicProbe(),
                cts.Token,
                socketOverride: sock,
                output: stdout,
                error: stderr);

            Assert.True(code is 0 or 1, stderr.ToString());
            var printed = stdout.ToString();
            Assert.Contains("port:", printed, StringComparison.Ordinal);
            Assert.Contains("quic_listening:", printed, StringComparison.Ordinal);
            Assert.Contains("certificate_sha256:", printed, StringComparison.Ordinal);
            Assert.DoesNotContain(sock, printed, StringComparison.Ordinal);
            Assert.DoesNotContain("hypa.sock", printed, StringComparison.Ordinal);
            Assert.DoesNotContain(sock, stderr.ToString(), StringComparison.Ordinal);
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
    public async Task Second_accept_reuses_stable_certificate()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX socket fixture");
        var dir = Path.Combine("/tmp", "hypa-acli-reuse-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
#pragma warning disable CA1416
        File.SetUnixFileMode(
            dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        var sock = Path.Combine(dir, "hypa.sock");
        try
        {
            var state = new AppState(SessionId.New("accept-reuse"));
            var cp = new ControlPlaneService(
                state,
                new UnusedPaneFactory(),
                new NullIntelligence(),
                new NullDetector(),
                leases: new InMemoryLeaseRegistry(),
                attachments: new InMemoryAttachmentRegistry());
            await using var server = new UnixSocketServer(cp, sock);
            await server.StartAsync(CancellationToken.None);

            var first = await RunAcceptUntilListenAsync(sock);
            var second = await RunAcceptUntilListenAsync(sock);
            Assert.True(File.Exists(Path.Combine(dir, "accept.pfx")));
            Assert.False(string.IsNullOrWhiteSpace(first));
            Assert.Equal(first, second);
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
    public async Task Occupied_port_fails_without_unhandled_exception()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX socket fixture");
        var dir = Path.Combine("/tmp", "hypa-acli-busy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
#pragma warning disable CA1416
        File.SetUnixFileMode(
            dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        var sock = Path.Combine(dir, "hypa.sock");
        var occupied = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            var state = new AppState(SessionId.New("accept-busy"));
            var cp = new ControlPlaneService(
                state,
                new UnusedPaneFactory(),
                new NullIntelligence(),
                new NullDetector(),
                leases: new InMemoryLeaseRegistry(),
                attachments: new InMemoryAttachmentRegistry());
            await using var server = new UnixSocketServer(cp, sock);
            await server.StartAsync(CancellationToken.None);

            var code = await ConnectivityCommand.RunAcceptAsync(
                "accept-busy",
                IPAddress.Loopback.ToString(),
                port,
                certPath: null,
                json: true,
                new DisabledQuicProbe(),
                CancellationToken.None,
                socketOverride: sock,
                output: stdout,
                error: stderr);

            Assert.Equal(2, code);
            Assert.Contains($"port {port} is already in use", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            occupied.Stop();
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

    [Fact]
    public async Task Missing_mux_session_fails_without_socket_path()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await ConnectivityCommand.RunAcceptAsync(
            "missing-accept-session",
            IPAddress.Loopback.ToString(),
            ConnectivityCommand.DefaultAcceptPort,
            certPath: null,
            json: false,
            new DisabledQuicProbe(),
            CancellationToken.None,
            output: stdout,
            error: stderr);
        Assert.Equal(2, code);
        Assert.Contains("not listening", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hypa.sock", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hypa.sock", stdout.ToString(), StringComparison.Ordinal);
    }

    private static async Task<string?> RunAcceptUntilListenAsync(string sock)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var code = await ConnectivityCommand.RunAcceptAsync(
            "accept-reuse",
            IPAddress.Loopback.ToString(),
            port: 0,
            certPath: null,
            json: true,
            new DisabledQuicProbe(),
            cts.Token,
            socketOverride: sock,
            output: stdout,
            error: stderr);
        Assert.True(code is 0 or 1, stderr.ToString());
        var document = JsonSerializer.Deserialize(
            stdout.ToString().Trim(),
            ConnectivityJsonContext.Default.ConnectivityAcceptListenDocument);
        Assert.NotNull(document);
        Assert.True(document.Ok);
        return document.CertificateSha256;
    }

    private sealed class DisabledQuicProbe : IQuicTransportCapabilityProbe
    {
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
                Detail = "test probe disables quic",
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
