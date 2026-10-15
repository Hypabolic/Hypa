using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Hypa.ControlPlane.Unix;
using Hypa.AgentRuntime.Tests.Support;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// uid-private socket modes, peer credentials, line cap, connection cap.
/// </summary>
public class UnixSocketPerimeterTests
{
    [SkippableFact]
    public async Task Start_sets_0700_parent_and_0600_socket()
    {
        RequireUnixPerimeter();

        var (dir, sock) = NewPrivateSocketPath("h18m");
        try
        {
            await using var server = new UnixSocketServer(CreateControlPlane(), sock);
            await server.StartAsync(CancellationToken.None);

            var dirWant = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            var sockWant = UnixFileMode.UserRead | UnixFileMode.UserWrite;
#pragma warning disable CA1416
            var dirMode = File.GetUnixFileMode(dir);
            var sockMode = File.GetUnixFileMode(sock);
#pragma warning restore CA1416
            Assert.Equal(0, (int)(dirMode & ~dirWant));
            Assert.Equal(dirWant, dirMode & dirWant);
            Assert.Equal(0, (int)(sockMode & ~sockWant));
            Assert.Equal(sockWant, sockMode & sockWant);
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    [SkippableFact]
    public async Task Start_fails_when_mode_guard_throws()
    {
        RequireUnixPerimeter();

        var (dir, sock) = NewPrivateSocketPath("h18f");
        try
        {
            var options = new UnixSocketServerOptions
            {
                ModeGuard = new ThrowingModeGuard(),
            };
            await using var server = new UnixSocketServer(CreateControlPlane(), sock, options);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => server.StartAsync(CancellationToken.None));
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    [SkippableFact]
    public async Task Start_refuses_world_writable_parent()
    {
        RequireUnixPerimeter();

        string parent;
        string sock;
        var fabricated = false;
        var tmp = "/tmp";
#pragma warning disable CA1416
        var tmpIsStickyWorld =
            Directory.Exists(tmp)
            && (File.GetUnixFileMode(tmp) & UnixFileMode.OtherWrite) != 0
            && (File.GetUnixFileMode(tmp) & UnixFileMode.StickyBit) != 0;
#pragma warning restore CA1416

        if (tmpIsStickyWorld)
        {
            parent = tmp;
            sock = Path.Combine(tmp, "h18-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        }
        else
        {
            fabricated = true;
            parent = Path.Combine(Path.GetTempPath(), "h18-sticky-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(parent);
#pragma warning disable CA1416
            File.SetUnixFileMode(
                parent,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute
                | UnixFileMode.StickyBit);
#pragma warning restore CA1416
            sock = Path.Combine(parent, "s.sock");
        }

        try
        {
            await using var server = new UnixSocketServer(CreateControlPlane(), sock);
            var ex = await Record.ExceptionAsync(() => server.StartAsync(CancellationToken.None));
            Assert.NotNull(ex);
            Assert.Contains("private directory", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (File.Exists(sock))
                    File.Delete(sock);
            }
            catch
            {
                // ignore
            }

            if (fabricated)
                TryDeleteTree(parent);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(NonStickyWritableParentModes))]
    public async Task Start_refuses_non_sticky_writable_parent(UnixFileMode parentMode, string label)
    {
        RequireUnixPerimeter();
        await AssertRefusesWritableParentWithoutMutatingAsync(parentMode, label);
    }

    [SkippableTheory]
    [MemberData(nameof(NonStickyWritableParentModes))]
    public void Default_owner_guard_refuses_non_sticky_writable_parent_without_chmod(
        UnixFileMode parentMode,
        string label)
    {
        RequireUnixPerimeter();

        var parent = Path.Combine(
            Path.GetTempPath(),
            "h18-" + label + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(parent);
#pragma warning disable CA1416
        File.SetUnixFileMode(parent, parentMode);
        var before = File.GetUnixFileMode(parent);
#pragma warning restore CA1416
        Assert.Equal(parentMode, before & parentMode);
        Assert.Equal(0, (int)(before & UnixFileMode.StickyBit));

        try
        {
            var guard = new UnixSocketOwnerGuard();
            var ex = Record.Exception(() => guard.EnsurePrivateDirectory(parent));
            Assert.NotNull(ex);
            Assert.IsType<UnauthorizedAccessException>(ex);
            Assert.Contains("private directory", ex.Message, StringComparison.OrdinalIgnoreCase);
#pragma warning disable CA1416
            var after = File.GetUnixFileMode(parent);
#pragma warning restore CA1416
            Assert.Equal(before, after);
            Assert.NotEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                after);
        }
        finally
        {
            TryDeleteTree(parent);
        }
    }

    public static TheoryData<UnixFileMode, string> NonStickyWritableParentModes()
    {
        var userRwx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var groupRwx = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;
        var otherRx = UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        return new TheoryData<UnixFileMode, string>
        {
            { userRwx | groupRwx | otherRx, "0775" },
            { userRwx | groupRwx | otherRx | UnixFileMode.OtherWrite, "0777" },
        };
    }

    [SkippableFact]
    public async Task Same_uid_connect_is_accepted()
    {
        RequireUnixPerimeter();

        var (dir, sock) = NewPrivateSocketPath("h18s");
        try
        {
            var cp = CreateControlPlane();
            await using var server = new UnixSocketServer(cp, sock);
            await server.StartAsync(CancellationToken.None);

            await using var client = new ControlPlaneClient(sock);
            await client.ConnectAsync();
            var result = await client.CallAsync("ping");
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal(1, result.GetProperty("protocol").GetInt32());
            await cp.ShutdownAsync(CancellationToken.None);
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    [SkippableFact]
    public async Task Foreign_uid_is_rejected()
    {
        RequireUnixPerimeter();

        // Live second-account fixture (`sudo -u nobody`) is optional and not
        // required on CI. This test injects IUnixPeerAuthenticator so the
        // accept-path reject is exercised without root.
        var (dir, sock) = NewPrivateSocketPath("h18x");
        try
        {
            var recording = new RecordingControlPlaneService();
            var options = new UnixSocketServerOptions
            {
                PeerAuthenticator = new RejectingPeerAuthenticator(),
            };
            await using var server = new UnixSocketServer(recording, sock, options);
            await server.StartAsync(CancellationToken.None);

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            var n = 0;
            var buf = new byte[64];
            using var receiveTimeout = new CancellationTokenSource(Poll.HangGuard);
            try
            {
                n = await client.ReceiveAsync(buf.AsMemory(), SocketFlags.None, receiveTimeout.Token);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                n = 0;
            }

            await Poll.UntilAsync(() => server.LiveConnectionCount == 0, "the rejected connection to close");

            Assert.Equal(0, n);
            Assert.Equal(0, recording.DispatchCount);
            Assert.Equal(0, server.LiveConnectionCount);
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    [Fact]
    public void Peer_policy_rejects_mismatched_uid()
    {
        Assert.True(EuidPeerAuthenticator.IsSameUid(501, 501));
        Assert.False(EuidPeerAuthenticator.IsSameUid(501, 0));
        Assert.False(EuidPeerAuthenticator.IsSameUid(0, 1));
        var ex = Assert.Throws<UnauthorizedAccessException>(
            () => EuidPeerAuthenticator.ThrowIfForeignUid(1, 2));
        Assert.Contains("uid-private", ex.Message, StringComparison.Ordinal);
        EuidPeerAuthenticator.ThrowIfForeignUid(42, 42);
    }

    [SkippableFact]
    public async Task Oversize_line_writes_parse_error_and_closes()
    {
        RequireUnixPerimeter();

        var (dir, sock) = NewPrivateSocketPath("h18o");
        try
        {
            var recording = new RecordingControlPlaneService();
            var options = new UnixSocketServerOptions
            {
                MaxLineBytes = 32,
            };
            await using var server = new UnixSocketServer(recording, sock, options);
            await server.StartAsync(CancellationToken.None);

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            var payload = Encoding.UTF8.GetBytes(new string('x', 64) + "\n");
            Assert.True(payload.Length > 32);
            await client.SendAsync(payload, SocketFlags.None);

            var line = await ReadLineAsync(client, Poll.HangGuard);
            Assert.False(string.IsNullOrEmpty(line));
            using var doc = JsonDocument.Parse(line);
            var error = doc.RootElement.GetProperty("error");
            Assert.Equal(ProtocolErrorCodes.ParseError, error.GetProperty("code").GetInt32());
            Assert.Contains("NDJSON line exceeds max size", error.GetProperty("message").GetString());

            // The server closes after the parse error: the next read ends at EOF.
            await ReadToEofAsync(client);
            Assert.Equal(0, recording.DispatchCount);
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    [SkippableFact]
    public async Task Connection_flood_is_rejected_and_bounded()
    {
        RequireUnixPerimeter();

        var (dir, sock) = NewPrivateSocketPath("h18c");
        var held = new List<Socket>();
        try
        {
            var options = new UnixSocketServerOptions
            {
                MaxConcurrentConnections = 2,
                ListenBacklog = 8,
            };
            await using var server = new UnixSocketServer(CreateControlPlane(), sock, options);
            await server.StartAsync(CancellationToken.None);

            for (var i = 0; i < 2; i++)
            {
                var c = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await c.ConnectAsync(new UnixDomainSocketEndPoint(sock));
                held.Add(c);
            }

            await Poll.UntilAsync(() => server.LiveConnectionCount == 2, "both connections to be live");
            Assert.True(server.InFlightCount <= 2);
            Assert.True(server.AcceptedSocketCount <= 2);

            using var extra = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await extra.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            var reject = await ReadLineAsync(extra, Poll.HangGuard);
            Assert.False(string.IsNullOrEmpty(reject));
            using var doc = JsonDocument.Parse(reject);
            Assert.Equal(
                ProtocolErrorCodes.InvalidRequest,
                doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());

            // The server writes the reject line only after it refused the slot, so the
            // counts below already include the decision.
            Assert.True(server.LiveConnectionCount <= 2);
            Assert.Equal(2, server.LiveConnectionCount);
            Assert.True(server.InFlightCount <= 2);
            Assert.True(server.PeakInFlightCount <= 2);
            Assert.True(server.PeakAcceptedSocketCount <= 3);
        }
        finally
        {
            foreach (var s in held)
            {
                try { s.Dispose(); }
                catch
                {
                    // ignore
                }
            }

            TryDeleteTree(dir);
        }
    }

    [SkippableFact]
    public async Task Connection_flood_bounds_handlers_and_process_fds()
    {
        RequireUnixPerimeter();

        const int max = 2;
        const int backlog = 8;
        const int flood = 40;
        var (dir, sock) = NewPrivateSocketPath("h18fd");
        var held = new List<Socket>();
        var extras = new ConcurrentBag<Socket>();
        try
        {
            var options = new UnixSocketServerOptions
            {
                MaxConcurrentConnections = max,
                ListenBacklog = backlog,
            };
            await using var server = new UnixSocketServer(CreateControlPlane(), sock, options);
            await server.StartAsync(CancellationToken.None);

            for (var i = 0; i < max; i++)
            {
                var c = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await c.ConnectAsync(new UnixDomainSocketEndPoint(sock));
                held.Add(c);
            }

            await Poll.UntilAsync(() => server.LiveConnectionCount == max, "the held connections to be live");
            Assert.True(server.InFlightCount <= max, "Held handlers must stay at the cap.");
            Assert.True(server.AcceptedSocketCount <= max);

            // The first excess connect gets the reject line. The accept loop then waits
            // for a free slot, so the rest of the flood queues in the backlog.
            var first = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            extras.Add(first);
            await first.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            var rejectLine = await ReadLineAsync(first, Poll.HangGuard);
            Assert.False(string.IsNullOrEmpty(rejectLine), "The excess connect must get a reject line.");
            using (var rejectDoc = JsonDocument.Parse(rejectLine))
            {
                Assert.Equal(
                    ProtocolErrorCodes.InvalidRequest,
                    rejectDoc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }

            var fdAtCap = CountOpenFds();

            var connectTasks = new Task[flood];
            for (var i = 0; i < flood; i++)
            {
                connectTasks[i] = TryConnectExtraAsync(
                    sock, extras, TimeSpan.FromMilliseconds(250));
            }

            await Task.WhenAll(connectTasks);

            Assert.True(server.LiveConnectionCount <= max);
            Assert.True(server.InFlightCount <= max);
            Assert.True(
                server.PeakInFlightCount <= max,
                "Queued handlers must stay at the cap. Peak=" + server.PeakInFlightCount);
            Assert.True(
                server.PeakAcceptedSocketCount <= max + 1,
                "Accepted FDs must stay O(max). Peak=" + server.PeakAcceptedSocketCount);
            Assert.True(server.AcceptedSocketCount <= max + 1);

            var extraList = extras.ToList();
            Assert.True(
                extraList.Count <= backlog + 4,
                "Connected extras must stay O(listen backlog). Count=" + extraList.Count);

            var fds = CountOpenFds();
            const int slack = 16;
            var bound = fdAtCap + backlog + slack;
            Assert.True(
                fds <= bound,
                $"Process FD count {fds} exceeded O(max + listen backlog) bound {bound} " +
                $"(fdAtCap={fdAtCap}, extras={extraList.Count}, backlog={backlog}).");

        }
        finally
        {
            foreach (var s in held)
            {
                try { s.Dispose(); }
                catch
                {
                    // ignore
                }
            }

            foreach (var s in extras)
            {
                try { s.Dispose(); }
                catch
                {
                    // ignore
                }
            }

            TryDeleteTree(dir);
        }
    }

    [SkippableFact]
    public void Default_owner_guard_does_not_swallow_harden_failure()
    {
        RequireUnixPerimeter();

        var guard = new UnixSocketOwnerGuard();
        var missing = Path.Combine(
            Path.GetTempPath(),
            "h18-missing-" + Guid.NewGuid().ToString("N")[..8],
            "no.sock");

        var ex = Record.Exception(() => guard.HardenSocket(missing));
        Assert.NotNull(ex);
        Assert.IsAssignableFrom<IOException>(ex);
    }

    [SkippableFact]
    public void Default_owner_guard_does_not_swallow_parent_mode_failure()
    {
        RequireUnixPerimeter();

        var fileAsParent = Path.GetTempFileName();
        try
        {
            var nested = Path.Combine(fileAsParent, "nested-socket-dir");
            var guard = new UnixSocketOwnerGuard();
            var ex = Record.Exception(() => guard.EnsurePrivateDirectory(nested));
            Assert.NotNull(ex);
        }
        finally
        {
            try { File.Delete(fileAsParent); }
            catch
            {
                // ignore
            }
        }
    }

    private static async Task AssertRefusesWritableParentWithoutMutatingAsync(
        UnixFileMode parentMode,
        string label)
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            "h18-" + label + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(parent);
#pragma warning disable CA1416
        File.SetUnixFileMode(parent, parentMode);
        var before = File.GetUnixFileMode(parent);
#pragma warning restore CA1416
        Assert.Equal(parentMode, before & parentMode);
        Assert.Equal(0, (int)(before & UnixFileMode.StickyBit));
        var sock = Path.Combine(parent, "s.sock");

        try
        {
            await using var server = new UnixSocketServer(CreateControlPlane(), sock);
            var ex = await Record.ExceptionAsync(() => server.StartAsync(CancellationToken.None));
            Assert.NotNull(ex);
            Assert.Contains("private directory", ex.Message, StringComparison.OrdinalIgnoreCase);
#pragma warning disable CA1416
            var after = File.GetUnixFileMode(parent);
#pragma warning restore CA1416
            Assert.Equal(before, after);
            Assert.Equal(0, (int)(after & UnixFileMode.StickyBit));
            Assert.NotEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                after);
        }
        finally
        {
            try
            {
                if (File.Exists(sock))
                    File.Delete(sock);
            }
            catch
            {
                // ignore
            }

            TryDeleteTree(parent);
        }
    }

    private static void RequireUnixPerimeter() =>
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "H-18 Unix socket perimeter; explicit skip (not a silent pass).");

    private static ControlPlaneService CreateControlPlane()
    {
        var state = new AppState(SessionId.New("h18"));
        var intel = new PaneIntelligencePipeline();
        return new ControlPlaneService(
            state,
            TestPaneFactories.Create(intel),
            intel,
            new HeuristicAgentDetector());
    }

    private static (string Dir, string Sock) NewPrivateSocketPath(string prefix)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine("/tmp", prefix + "-" + id);
        Directory.CreateDirectory(dir);
#pragma warning disable CA1416
        File.SetUnixFileMode(
            dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        return (dir, Path.Combine(dir, "s.sock"));
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    private static async Task TryConnectExtraAsync(
        string sock,
        ConcurrentBag<Socket> extras,
        TimeSpan timeout)
    {
        var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await client.ConnectAsync(new UnixDomainSocketEndPoint(sock), cts.Token)
                .ConfigureAwait(false);
            extras.Add(client);
        }
        catch
        {
            try { client.Dispose(); }
            catch
            {
                // ignore
            }
        }
    }

    private static int CountOpenFds()
    {
        if (OperatingSystem.IsLinux() && Directory.Exists("/proc/self/fd"))
            return Directory.GetFileSystemEntries("/proc/self/fd").Length;
        if (Directory.Exists("/dev/fd"))
            return Directory.GetFileSystemEntries("/dev/fd").Length;
        throw new InvalidOperationException("Cannot count process file descriptors.");
    }

    private static async Task ReadToEofAsync(Socket socket)
    {
        using var cts = new CancellationTokenSource(Poll.HangGuard);
        var buffer = new byte[512];
        try
        {
            while (await socket.ReceiveAsync(buffer.AsMemory(), cts.Token).ConfigureAwait(false) != 0)
            {
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
        }
    }

    private static async Task<string?> ReadLineAsync(Socket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[512];
        var filled = 0;
        try
        {
            while (filled < buffer.Length)
            {
                var n = await socket.ReceiveAsync(buffer.AsMemory(filled), cts.Token).ConfigureAwait(false);
                if (n == 0)
                    break;
                filled += n;
                var nl = Array.IndexOf(buffer, (byte)'\n', 0, filled);
                if (nl >= 0)
                    return Encoding.UTF8.GetString(buffer, 0, nl).TrimEnd('\r');
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }

        return filled == 0 ? null : Encoding.UTF8.GetString(buffer, 0, filled).TrimEnd('\r', '\n');
    }

    private sealed class ThrowingModeGuard : IUnixSocketModeGuard
    {
        public void EnsurePrivateDirectory(string directory) =>
            throw new InvalidOperationException("mode guard failed (test)");

        public void HardenSocket(string socketPath) =>
            throw new InvalidOperationException("mode guard failed (test)");
    }

    private sealed class RejectingPeerAuthenticator : IUnixPeerAuthenticator
    {
        public void Authenticate(Socket connected) =>
            throw new UnauthorizedAccessException("foreign uid (test)");
    }

    private sealed class RecordingControlPlaneService : IControlPlaneService
    {
        private int _dispatchCount;

        public int DispatchCount => Volatile.Read(ref _dispatchCount);

        public Task<JsonElement> DispatchAsync(
            string method,
            JsonElement? parameters,
            IClientConnection? connection,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _dispatchCount);
            return Task.FromResult(JsonDocument.Parse("""{"ok":true}""").RootElement);
        }

        public Task<RuntimeResult<IReadOnlyList<RuntimeEventRecord>>> PrepareEventsSubscribeAsync(
            string subscriptionId,
            CancellationToken ct) =>
            Task.FromResult(
                RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Ok(
                    Array.Empty<RuntimeEventRecord>()));

        public Task CompleteEventsSubscribeAsync(
            string subscriptionId,
            IReadOnlyList<RuntimeEventRecord> replayRecords,
            IClientConnection connection,
            CancellationToken ct) =>
            Task.CompletedTask;

        public void OnClientDisconnected(IClientConnection connection)
        {
        }

        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
