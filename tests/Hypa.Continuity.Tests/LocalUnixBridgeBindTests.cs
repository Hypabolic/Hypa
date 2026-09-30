using System.Diagnostics;
using System.Net.Sockets;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class LocalUnixBridgeBindTests
{
    [SkippableFact]
    public async Task Accepted_bind_median_stays_under_one_hundred_milliseconds()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBoot, _) = JoinTestPairs.SelfHosted("plc_ubind1", "ubind001");
        await BindAndReleaseAsync(muxBoot);

        var samples = new double[8];
        for (var i = 0; i < samples.Length; i++)
        {
            await using var mux = SocketMux.Start(SocketMuxMode.Hold);
            await using var bridge = new LocalUnixBridge(mux.SocketPath, new ReadyJoin(muxBoot));
            var watch = Stopwatch.StartNew();
            var bound = await bridge.BindMuxAsync(muxBoot);
            watch.Stop();
            Assert.True(bound.Ok, bound.Detail);
            samples[i] = watch.Elapsed.TotalMilliseconds;
            bridge.ReleaseMuxReservation(bound.Value!.Reservation);
        }

        var median = Median(samples);
        Assert.True(median < 100, $"median bind took {median:F1} ms");
    }

    [SkippableFact]
    public async Task Close_during_join_returns_unauthorized()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBoot, _) = JoinTestPairs.SelfHosted("plc_ubind5", "ubind005");
        var joinStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var mux = SocketMux.StartCloseAfterJoin(joinStarted.Task, peerClosed);
        await using var bridge = new LocalUnixBridge(
            mux.SocketPath,
            new GateJoin(muxBoot, joinStarted, peerClosed.Task));
        var bound = await bridge.BindMuxAsync(muxBoot);
        Assert.False(bound.Ok);
        Assert.Equal(ConnectivityReasons.Unauthorized, bound.Reason);
        Assert.Equal("unix peer rejected", bound.Detail);
    }

    private static async Task BindAndReleaseAsync(JoinBootstrap muxBoot)
    {
        await using var mux = SocketMux.Start(SocketMuxMode.Hold);
        await using var bridge = new LocalUnixBridge(mux.SocketPath, new ReadyJoin(muxBoot));
        var bound = await bridge.BindMuxAsync(muxBoot);
        Assert.True(bound.Ok, bound.Detail);
        bridge.ReleaseMuxReservation(bound.Value!.Reservation);
    }

    private static double Median(double[] samples)
    {
        var ordered = samples.ToArray();
        Array.Sort(ordered);
        var mid = ordered.Length / 2;
        if (ordered.Length % 2 == 1)
            return ordered[mid];

        return (ordered[mid - 1] + ordered[mid]) / 2;
    }

    [SkippableFact]
    public async Task Accepted_bind_still_reads_mux_bytes()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBoot, _) = JoinTestPairs.SelfHosted("plc_ubind2", "ubind002");
        await using var mux = SocketMux.Start(SocketMuxMode.Hold);
        await using var bridge = new LocalUnixBridge(mux.SocketPath, new ReadyJoin(muxBoot));
        var bound = await bridge.BindMuxAsync(muxBoot);
        Assert.True(bound.Ok, bound.Detail);
        var held = await mux.WaitHeldAsync();
        Assert.Equal(1, await held.SendAsync(new byte[] { 0x41 }));
        await using var stream = bridge.TryTakeMuxStream(bound.Value!.Reservation);
        Assert.NotNull(stream);
        var buffer = new byte[1];
        var read = await stream!.ReadAsync(buffer);
        Assert.Equal(1, read);
        Assert.Equal(0x41, buffer[0]);
    }

    [SkippableFact]
    public async Task Closed_unix_peer_returns_unauthorized()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBoot, _) = JoinTestPairs.SelfHosted("plc_ubind3", "ubind003");
        for (var i = 0; i < 30; i++)
        {
            await using var mux = SocketMux.Start(SocketMuxMode.Close);
            await using var bridge = new LocalUnixBridge(mux.SocketPath, new ReadyJoin(muxBoot));
            var bound = await bridge.BindMuxAsync(muxBoot);
            Assert.False(bound.Ok);
            Assert.Equal(ConnectivityReasons.Unauthorized, bound.Reason);
            Assert.Equal("unix peer rejected", bound.Detail);
        }
    }

    [SkippableFact]
    public async Task Joining_client_sees_unauthorized_when_unix_peer_closes()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_ubind4", "ubind004");
        await using var mux = SocketMux.Start(SocketMuxMode.Close);
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        await using var bridge = new LocalUnixBridge(mux.SocketPath, new AcceptPathJoin(coordinator));
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            bridge,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot));
        var outcome = await OutboundFramedSession.ConnectDirectAsync(accept.DialEndpoint, clientBoot);
        Assert.False(outcome.Ok);
        Assert.Equal(ConnectivityReasons.Unauthorized, outcome.Reason);
        Assert.Equal("unix peer rejected", outcome.Detail);
    }

    private enum SocketMuxMode
    {
        Hold,
        Close,
    }

    private sealed class SocketMux : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<Socket> _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Socket> _open = [];
        private Task _loop = Task.CompletedTask;

        private SocketMux(Socket listener, string socketPath)
        {
            _listener = listener;
            SocketPath = socketPath;
        }

        public string SocketPath { get; }

        public static SocketMux Start(SocketMuxMode mode)
        {
            var mux = Create();
            mux._loop = Task.Run(() => mux.LoopAsync(mode));
            return mux;
        }

        public static SocketMux StartCloseAfterJoin(Task joinStarted, TaskCompletionSource peerClosed)
        {
            var mux = Create();
            mux._loop = Task.Run(() => mux.CloseAfterJoinAsync(joinStarted, peerClosed));
            return mux;
        }

        private static SocketMux Create()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hypa-ubind-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
#pragma warning disable CA1416
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var socketPath = Path.Combine(dir, "mux.sock");
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(
                socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
            listener.Listen(8);
            return new SocketMux(listener, socketPath);
        }

        public Task<Socket> WaitHeldAsync() => _held.Task;

        private async Task LoopAsync(SocketMuxMode mode)
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    break;
                }

                if (mode == SocketMuxMode.Close)
                {
                    client.Dispose();
                    continue;
                }

                lock (_open)
                    _open.Add(client);
                _held.TrySetResult(client);
            }
        }

        private async Task CloseAfterJoinAsync(Task joinStarted, TaskCompletionSource peerClosed)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                peerClosed.TrySetResult();
                return;
            }

            try
            {
                await joinStarted.WaitAsync(_cts.Token).ConfigureAwait(false);
                client.Shutdown(SocketShutdown.Both);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
            finally
            {
                client.Dispose();
                peerClosed.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }

            _listener.Dispose();
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException)
            {
            }

            lock (_open)
            {
                foreach (var socket in _open)
                    socket.Dispose();
                _open.Clear();
            }

            _cts.Dispose();
            try
            {
                if (File.Exists(SocketPath))
                    File.Delete(SocketPath);
                var dir = Path.GetDirectoryName(SocketPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class GateJoin(JoinBootstrap mux, TaskCompletionSource started, Task peerClosed) : IRendezvousJoin
    {
        public async ValueTask<ConnectivityOutcome<JoinBinding>> JoinAsync(
            JoinBootstrap bootstrap,
            CancellationToken cancellationToken = default)
        {
            _ = bootstrap;
            started.TrySetResult();
            await peerClosed.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return ConnectivityOutcome<JoinBinding>.Success(new JoinBinding
            {
                PlacementId = mux.PlacementId,
                StreamClass = mux.StreamClass,
                Role = JoinRole.Mux,
                PeerEphPublicKey = mux.EphPublicKey,
                PeerEphPublicMac = mux.EphPublicMac,
            });
        }
    }

    private sealed class ReadyJoin(JoinBootstrap mux) : IRendezvousJoin
    {
        public ValueTask<ConnectivityOutcome<JoinBinding>> JoinAsync(
            JoinBootstrap bootstrap,
            CancellationToken cancellationToken = default)
        {
            _ = bootstrap;
            _ = cancellationToken;
            return ValueTask.FromResult(ConnectivityOutcome<JoinBinding>.Success(new JoinBinding
            {
                PlacementId = mux.PlacementId,
                StreamClass = mux.StreamClass,
                Role = JoinRole.Mux,
                PeerEphPublicKey = mux.EphPublicKey,
                PeerEphPublicMac = mux.EphPublicMac,
            }));
        }
    }
}
