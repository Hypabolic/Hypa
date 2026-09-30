using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Hypa.Terminal.Pty;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Unit tests for handoff port framing, SCM_RIGHTS loopback, PTY validation, CLOEXEC, and private UDS.
/// </summary>
public class PtyHandoffPortTests
{
    [Fact]
    public void Handoff_message_codec_round_trips()
    {
        var nonce = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

        var hello = PtyHandoffMessage.CreateHello("sess-1", "pane-a", 3, nonce);
        var (t1, b1) = UnixPtyHandoffPort.EncodeMessage(hello);
        var d1 = UnixPtyHandoffPort.DecodeMessage(t1, b1);
        Assert.Equal(PtyHandoffMessageKind.Hello, d1.Kind);
        Assert.Equal("sess-1", d1.RuntimeSessionId);
        Assert.Equal("pane-a", d1.PaneId);
        Assert.Equal(3, d1.Generation);
        Assert.Equal(nonce, d1.Nonce);

        var prepare = PtyHandoffMessage.CreatePrepare(nonce, "target-rt");
        var (t2, b2) = UnixPtyHandoffPort.EncodeMessage(prepare);
        var d2 = UnixPtyHandoffPort.DecodeMessage(t2, b2);
        Assert.Equal(PtyHandoffMessageKind.Prepare, d2.Kind);
        Assert.Equal("target-rt", d2.TargetRuntimeId);
        Assert.Equal(nonce, d2.Nonce);

        var meta = PtyHandoffMessage.CreateHandleMeta(nonce, 3, 4242, 100, 40);
        var (t3, b3) = UnixPtyHandoffPort.EncodeMessage(meta);
        var d3 = UnixPtyHandoffPort.DecodeMessage(t3, b3);
        Assert.Equal(PtyHandoffMessageKind.HandleMeta, d3.Kind);
        Assert.Equal(4242, d3.ChildPid);
        Assert.Equal((ushort)100, d3.Cols);
        Assert.Equal((ushort)40, d3.Rows);

        var commit = PtyHandoffMessage.CreateCommit(nonce);
        var (t4, b4) = UnixPtyHandoffPort.EncodeMessage(commit);
        var d4 = UnixPtyHandoffPort.DecodeMessage(t4, b4);
        Assert.Equal(PtyHandoffMessageKind.Commit, d4.Kind);
        Assert.Equal(nonce, d4.Nonce);

        var abort = PtyHandoffMessage.CreateAbort((int)PtyHandoffStatus.Failed, "import failed");
        var (t5, b5) = UnixPtyHandoffPort.EncodeMessage(abort);
        var d5 = UnixPtyHandoffPort.DecodeMessage(t5, b5);
        Assert.Equal(PtyHandoffMessageKind.Abort, d5.Kind);
        Assert.Equal((int)PtyHandoffStatus.Failed, d5.AbortReason);
        Assert.Equal("import failed", d5.AbortMessage);
    }

    [Fact]
    public void Nonce_generation_mismatch_is_detectable_on_decoded_messages()
    {
        var nonceA = new byte[16];
        var nonceB = new byte[16];
        nonceA[0] = 1;
        nonceB[0] = 2;

        var hello = PtyHandoffMessage.CreateHello("s", "p", 1, nonceA);
        var prepare = PtyHandoffMessage.CreatePrepare(nonceB, "t");
        Assert.False(hello.Nonce.AsSpan().SequenceEqual(prepare.Nonce));

        var metaWrongGen = PtyHandoffMessage.CreateHandleMeta(nonceA, 99, 1);
        Assert.NotEqual(hello.Generation, metaWrongGen.Generation);
    }

    [Fact]
    public async Task Unix_loopback_SCM_RIGHTS_send_recv_raw_pipe_fd()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Transport-only: raw RecvFd still moves arbitrary FDs. Ownership path rejects non-PTY.
        if (!PipeCreate(out var readFd, out var writeFd))
            Assert.Fail("pipe() failed");

        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var (dir, path) = UnixPrivateSocketPath.Create("hypa-scm");
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                UnixPrivateSocketPath.HardenSocketFile(path);
                listener.Listen(1);

                var clientTask = Task.Run(async () =>
                {
                    using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
                    var fd = client.SafeHandle.DangerousGetHandle().ToInt32();
                    // Transport-only: unchecked send moves arbitrary FDs (recv still raw).
                    UnixAncillaryFd.SendFdUnchecked(fd, readFd);
                });

                using var server = await listener.AcceptAsync();
                UnixPrivateSocketPath.ValidatePeerIsSelf(server);
                var serverFd = server.SafeHandle.DangerousGetHandle().ToInt32();
                var receivedFd = UnixAncillaryFd.RecvFd(serverFd);
                await clientTask;

                Assert.True(receivedFd >= 0);
                try
                {
                    var payload = "scm-rights-ok"u8.ToArray();
                    var w = write(writeFd, payload, payload.Length);
                    Assert.True(w > 0);

                    var buf = new byte[64];
                    var r = read(receivedFd, buf, buf.Length);
                    Assert.True(r > 0);
                    Assert.Equal("scm-rights-ok", Encoding.UTF8.GetString(buf, 0, r));
                }
                finally
                {
                    close(receivedFd);
                }
            }
            finally
            {
                UnixPrivateSocketPath.TryUnlink(path);
                UnixPrivateSocketPath.TryDeleteDirectory(dir);
            }
        }
        finally
        {
            close(readFd);
            close(writeFd);
        }
    }

    [Fact]
    public async Task SCM_RIGHTS_RecvPtyMaster_rejects_pipe_fd()
    {
        if (OperatingSystem.IsWindows())
            return;

        if (!PipeCreate(out var readFd, out var writeFd))
            Assert.Fail("pipe() failed");

        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var (dir, path) = UnixPrivateSocketPath.Create("hypa-scm-reject");
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                UnixPrivateSocketPath.HardenSocketFile(path);
                listener.Listen(1);

                var clientTask = Task.Run(async () =>
                {
                    using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
                    // Bypass ownership-path SendFd: prove receive rejects non-PTY.
                    UnixAncillaryFd.SendFdUnchecked(
                        client.SafeHandle.DangerousGetHandle().ToInt32(), readFd);
                });

                using var server = await listener.AcceptAsync();
                var serverFd = server.SafeHandle.DangerousGetHandle().ToInt32();
                var ex = Assert.ThrowsAny<IOException>(() =>
                    UnixAncillaryFd.RecvPtyMasterSafeHandle(serverFd));
                Assert.Contains("not a PTY master", ex.Message, StringComparison.OrdinalIgnoreCase);
                await clientTask;
            }
            finally
            {
                UnixPrivateSocketPath.TryUnlink(path);
                UnixPrivateSocketPath.TryDeleteDirectory(dir);
            }
        }
        finally
        {
            close(readFd);
            close(writeFd);
        }
    }

    [Fact]
    public async Task SCM_RIGHTS_RecvPtyMaster_accepts_pty_and_sets_cloexec()
    {
        if (OperatingSystem.IsWindows())
            return;

        var master = OpenPtyMaster();
        Assert.True(master >= 0, "posix_openpt failed");
        try
        {
            Assert.True(UnixPtyFdValidation.IsPtyMaster(master));

            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var (dir, path) = UnixPrivateSocketPath.Create("hypa-scm-pty");
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                UnixPrivateSocketPath.HardenSocketFile(path);
                listener.Listen(1);

                var clientTask = Task.Run(async () =>
                {
                    using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
                    UnixAncillaryFd.SendFd(client.SafeHandle.DangerousGetHandle().ToInt32(), master);
                });

                using var server = await listener.AcceptAsync();
                using var received = UnixAncillaryFd.RecvPtyMasterSafeHandle(
                    server.SafeHandle.DangerousGetHandle().ToInt32());
                await clientTask;

                Assert.False(received.IsInvalid);
                var rfd = received.DangerousGetHandle().ToInt32();
                Assert.True(UnixPtyFdValidation.IsPtyMaster(rfd));
                Assert.True(UnixPtyFdValidation.HasCloexec(rfd));
            }
            finally
            {
                UnixPrivateSocketPath.TryUnlink(path);
                UnixPrivateSocketPath.TryDeleteDirectory(dir);
            }
        }
        finally
        {
            close(master);
        }
    }

    [Fact]
    public async Task Private_socket_directory_is_0700_and_socket_is_0600()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Production path: Listen creates private dir + hardens socket to 0600.
        await using var listener = UnixPtyHandoffPort.Listen();
        var path = listener.SocketPath!;
        Assert.False(string.IsNullOrEmpty(path));
        Assert.True(path.Length <= 104, "sockaddr_un budget: " + path);

        var dir = Path.GetDirectoryName(path)!;
        var dirMode = File.GetUnixFileMode(dir);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            dirMode);

        Assert.True(File.Exists(path));
        var sockMode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, sockMode);

        // Peer path also validates credentials (same euid).
        await using var client = await UnixPtyHandoffPort.ConnectAsync(path);
        await listener.AcceptAsync();
        await listener.SendAsync(PtyHandoffMessage.CreateAbort((int)PtyHandoffStatus.Failed, "mode-ok"));
        var abort = await client.ReceiveAsync();
        Assert.Equal((int)PtyHandoffStatus.Failed, abort.AbortReason);
    }

    [Fact]
    public void IsPtyMaster_false_for_pipe_true_for_openpt()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.True(PipeCreate(out var r, out var w));
        try
        {
            Assert.False(UnixPtyFdValidation.IsPtyMaster(r));
            Assert.False(UnixPtyFdValidation.IsPtyMaster(w));
        }
        finally
        {
            close(r);
            close(w);
        }

        var master = OpenPtyMaster();
        Assert.True(master >= 0);
        try
        {
            Assert.True(UnixPtyFdValidation.IsPtyMaster(master));
            UnixPtyFdValidation.SetCloexec(master);
            Assert.True(UnixPtyFdValidation.HasCloexec(master));
        }
        finally
        {
            close(master);
        }
    }

    [Fact]
    public async Task Handoff_port_hello_prepare_commit_without_fd()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var listener = UnixPtyHandoffPort.Listen();
        var path = listener.SocketPath!;
        Assert.False(string.IsNullOrEmpty(path));
        // Private dir: socket must not sit directly in global temp root.
        Assert.NotEqual(
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Path.GetDirectoryName(path)!).TrimEnd(Path.DirectorySeparatorChar));

        var nonce = Enumerable.Range(0, 16).Select(i => (byte)(i + 10)).ToArray();

        var exporter = Task.Run(async () =>
        {
            await listener.AcceptAsync();
            await listener.SendAsync(PtyHandoffMessage.CreateHello("rt", "pane", 7, nonce));
            var prep = await listener.ReceiveAsync();
            Assert.Equal(PtyHandoffMessageKind.Prepare, prep.Kind);
            Assert.Equal(nonce, prep.Nonce);
            await listener.SendAsync(PtyHandoffMessage.CreateAbort(0, "skip-meta")); // not full path
        });

        await using var client = await UnixPtyHandoffPort.ConnectAsync(path);
        var hello = await client.ReceiveAsync();
        Assert.Equal(PtyHandoffMessageKind.Hello, hello.Kind);
        Assert.Equal(7, hello.Generation);
        await client.SendAsync(PtyHandoffMessage.CreatePrepare(hello.Nonce, "new-rt"));
        var abort = await client.ReceiveAsync();
        Assert.Equal(PtyHandoffMessageKind.Abort, abort.Kind);

        await exporter;
    }

    [Fact]
    public void SCM_RIGHTS_SendFd_rejects_pipe_fd()
    {
        if (OperatingSystem.IsWindows())
            return;

        if (!PipeCreate(out var readFd, out var writeFd))
            Assert.Fail("pipe() failed");

        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var (dir, path) = UnixPrivateSocketPath.Create("hypa-scm-send-reject");
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                UnixPrivateSocketPath.HardenSocketFile(path);
                listener.Listen(1);

                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                client.Connect(new UnixDomainSocketEndPoint(path));
                using var server = listener.Accept();

                var sockFd = client.SafeHandle.DangerousGetHandle().ToInt32();
                var ex = Assert.ThrowsAny<IOException>(() => UnixAncillaryFd.SendFd(sockFd, readFd));
                Assert.Contains("not a PTY master", ex.Message, StringComparison.OrdinalIgnoreCase);

                // Ownership path must not transfer the pipe even if peer waits.
                // (No SCM_RIGHTS message should arrive — peer would block on recv.)
            }
            finally
            {
                UnixPrivateSocketPath.TryUnlink(path);
                UnixPrivateSocketPath.TryDeleteDirectory(dir);
            }
        }
        finally
        {
            close(readFd);
            close(writeFd);
        }
    }

    [Fact]
    public async Task Handoff_port_HandleMeta_rejects_non_pty_fd_on_send()
    {
        if (OperatingSystem.IsWindows())
            return;

        if (!PipeCreate(out var readFd, out var writeFd))
            Assert.Fail("pipe() failed");

        try
        {
            await using var listener = UnixPtyHandoffPort.Listen();
            var path = listener.SocketPath!;
            var nonce = new byte[16];
            nonce[0] = 0xAB;

            await using var client = await UnixPtyHandoffPort.ConnectAsync(path);
            await listener.AcceptAsync();

            // Ownership SendHandleAsync must reject non-PTY before wire transfer.
            using var handle = new PtyHandoffHandle(
                new SafeFileHandle(new IntPtr(readFd), ownsHandle: false));
            var ex = await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                await listener.SendHandleAsync(
                    PtyHandoffMessage.CreateHandleMeta(nonce, 1, 1, 80, 24),
                    handle);
            });
            Assert.Contains("not a PTY master", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            close(readFd);
            close(writeFd);
        }
    }

    [Fact]
    public async Task ConnectAsync_rejects_wrong_mode_socket()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Build a socket with group-readable mode (0640) — connect path must reject.
        var (dir, path) = UnixPrivateSocketPath.Create("hypa-bad-mode");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            // Intentionally wrong: group read bit set.
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            listener.Listen(1);

            var ex = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(async () =>
            {
                await using var _ = await UnixPtyHandoffPort.ConnectAsync(path);
            });
            Assert.Contains("0600", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            listener.Dispose();
            UnixPrivateSocketPath.TryUnlink(path);
            UnixPrivateSocketPath.TryDeleteDirectory(dir);
        }
    }

    [Theory]
    [InlineData(UnixFileMode.None)] // 0000 — owner-mode deviation (no bits)
    [InlineData(UnixFileMode.UserRead)] // 0400 — owner read only
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)] // 0700
    public async Task ConnectAsync_rejects_non_exact_0600_socket_modes(UnixFileMode badSocketMode)
    {
        if (OperatingSystem.IsWindows())
            return;

        // Exact 0600 required: reject owner-mode deviations that lack group/other bits.
        var (dir, path) = UnixPrivateSocketPath.Create("hypa-exact-sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, badSocketMode);
            listener.Listen(1);

            // Parent remains 0700 from Create; only socket mode is wrong.
            var dirMode = File.GetUnixFileMode(dir);
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                dirMode);

            var ex = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(async () =>
            {
                await using var _ = await UnixPtyHandoffPort.ConnectAsync(path);
            });
            Assert.Contains("0600", ex.Message, StringComparison.Ordinal);

            // Direct validator must fail closed the same way.
            var direct = Assert.ThrowsAny<UnauthorizedAccessException>(
                () => UnixPrivateSocketPath.ValidateConnectPath(path));
            Assert.Contains("0600", direct.Message, StringComparison.Ordinal);
        }
        finally
        {
            // Restore enough mode for cleanup unlink.
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* ignore */ }
            listener.Dispose();
            UnixPrivateSocketPath.TryUnlink(path);
            UnixPrivateSocketPath.TryDeleteDirectory(dir);
        }
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupExecute)] // 0711 — no g/o write, still not 0700
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute)] // 0755
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserExecute)] // 0500 — missing write
    public async Task ConnectAsync_rejects_non_exact_0700_parent_modes(UnixFileMode badParentMode)
    {
        if (OperatingSystem.IsWindows())
            return;

        // Isolated private parent so we do not disturb the shared hp{pid} dir.
        var isolated = Path.Combine(
            Path.GetTempPath(),
            "hypa-parent-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(isolated);
        File.SetUnixFileMode(
            isolated,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(isolated, "s.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(1);

            // Socket is exact 0600; parent intentionally not exact 0700.
            File.SetUnixFileMode(isolated, badParentMode);

            var ex = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(async () =>
            {
                await using var _ = await UnixPtyHandoffPort.ConnectAsync(path);
            });
            Assert.Contains("0700", ex.Message, StringComparison.Ordinal);

            var direct = Assert.ThrowsAny<UnauthorizedAccessException>(
                () => UnixPrivateSocketPath.ValidateConnectPath(path));
            Assert.Contains("0700", direct.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                File.SetUnixFileMode(
                    isolated,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { /* ignore */ }
            listener.Dispose();
            UnixPrivateSocketPath.TryUnlink(path);
            try { Directory.Delete(isolated, recursive: false); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ValidateConnectPath_accepts_exact_0600_socket_and_0700_parent()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (dir, path) = UnixPrivateSocketPath.Create("hypa-exact-ok");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            UnixPrivateSocketPath.HardenSocketFile(path);
            listener.Listen(1);

            // Positive path: exact private modes must pass (no throw).
            UnixPrivateSocketPath.ValidateConnectPath(path);
        }
        finally
        {
            listener.Dispose();
            UnixPrivateSocketPath.TryUnlink(path);
            UnixPrivateSocketPath.TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task ConnectAsync_rejects_non_socket_path()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (dir, _) = UnixPrivateSocketPath.Create("hypa-not-sock");
        var filePath = Path.Combine(dir, "notasocket");
        try
        {
            await File.WriteAllTextAsync(filePath, "nope");
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var ex = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(async () =>
            {
                await using var _ = await UnixPtyHandoffPort.ConnectAsync(filePath);
            });
            Assert.Contains("socket", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(filePath); } catch { /* ignore */ }
            UnixPrivateSocketPath.TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteFrameAsync_completes_full_frame_under_partial_sends()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Round-trip a large Abort body; SendAsync loop must deliver every byte.
        await using var listener = UnixPtyHandoffPort.Listen();
        var path = listener.SocketPath!;
        var big = new string('x', 64 * 1024);

        var exporter = Task.Run(async () =>
        {
            await listener.AcceptAsync();
            await listener.SendAsync(
                PtyHandoffMessage.CreateAbort((int)PtyHandoffStatus.Failed, big));
        });

        await using var client = await UnixPtyHandoffPort.ConnectAsync(path);
        var abort = await client.ReceiveAsync();
        Assert.Equal(PtyHandoffMessageKind.Abort, abort.Kind);
        Assert.Equal((int)PtyHandoffStatus.Failed, abort.AbortReason);
        Assert.Equal(big, abort.AbortMessage);
        await exporter;
    }

    [Fact]
    public async Task Empty_SCM_RIGHTS_is_error_and_does_not_close_stdin()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var (dir, path) = UnixPrivateSocketPath.Create("hypa-scm-empty");
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            UnixPrivateSocketPath.HardenSocketFile(path);
            listener.Listen(1);

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var connectTask = client.ConnectAsync(new UnixDomainSocketEndPoint(path));
            using var server = await listener.AcceptAsync();
            await connectTask;

            var clientFd = client.SafeHandle.DangerousGetHandle().ToInt32();
            var serverFd = server.SafeHandle.DangerousGetHandle().ToInt32();
            var sendOk = true;
            try
            {
                UnixAncillaryFd.SendEmptyScmRights(clientFd);
            }
            catch (System.ComponentModel.Win32Exception sendEx)
                when (sendEx.NativeErrorCode == 22 && OperatingSystem.IsMacOS())
            {
                sendOk = false;
            }

            if (sendOk)
            {
                var ex = Assert.ThrowsAny<IOException>(() => UnixAncillaryFd.RecvFd(serverFd));
                Assert.True(
                    ex.Message.Contains("cmsg_len", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("control", StringComparison.OrdinalIgnoreCase),
                    "empty SCM_RIGHTS must be an error: " + ex.Message);
            }
            else
            {
                // Send EINVAL'd — prove empty header parse rejects without sendmsg.
                Span<byte> buf = stackalloc byte[16];
                buf.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(buf, 12);
                BinaryPrimitives.WriteInt32LittleEndian(buf[4..], 0xffff);
                BinaryPrimitives.WriteInt32LittleEndian(buf[8..], 1);
                var copy = buf.ToArray();
                var ex = Assert.Throws<IOException>(() =>
                    UnixAncillaryFd.ParseReceivedScmRightsFdMac(copy, 12));
                Assert.Contains("cmsg_len", ex.Message, StringComparison.OrdinalIgnoreCase);
            }

            Assert.True(fcntl(0, F_GETFD, 0) >= 0, "empty SCM_RIGHTS must not close stdin");
        }
        finally
        {
            UnixPrivateSocketPath.TryUnlink(path);
            UnixPrivateSocketPath.TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void SendShortCmsgLen_throws_on_darwin()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var ex = Assert.Throws<PlatformNotSupportedException>(() =>
            UnixAncillaryFd.SendShortCmsgLen(0));
        Assert.Contains("XNU", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sendmsg", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Darwin_scm_rights_control_lengths_are_16()
    {
        Assert.Equal(16u, UnixAncillaryFd.DarwinCmsgLenWithFd);
        Assert.Equal(16u, UnixAncillaryFd.DarwinEmptyScmRightsControlLen);
    }

    [Fact]
    public void Valid_length_scm_rights_fd_zero_is_rejected_and_stdin_stays_open()
    {
        if (OperatingSystem.IsWindows())
            return;

        if (OperatingSystem.IsMacOS())
        {
            Span<byte> buf = stackalloc byte[16];
            buf.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 16);
            BinaryPrimitives.WriteInt32LittleEndian(buf[4..], 0xffff);
            BinaryPrimitives.WriteInt32LittleEndian(buf[8..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(buf[12..], 0);
            var copy = buf.ToArray();
            var ex = Assert.Throws<IOException>(() =>
                UnixAncillaryFd.ParseReceivedScmRightsFdMac(copy, 16));
            Assert.Contains("invalid fd", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var buf = new byte[20];
            BinaryPrimitives.WriteUInt64LittleEndian(buf, 20);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12), 1);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16), 0);
            var ex = Assert.Throws<IOException>(() =>
                UnixAncillaryFd.ParseReceivedScmRightsFdLinux(buf, 20));
            Assert.Contains("invalid fd", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(fcntl(0, F_GETFD, 0) >= 0, "fd 0 reject must not close stdin");
    }

    [Fact]
    public void Short_cmsg_len_parse_rejects_without_sendmsg()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // Darwin panics in uipc_syscalls.c if sendmsg SCM_RIGHTS has
        // CMSG_ALIGN(cmsg_len) > msg_controllen. Prove recv validation in-process.
        Span<byte> buf = stackalloc byte[16];
        buf.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(buf, 14); // short cmsg_len
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..], 0xffff); // SOL_SOCKET
        BinaryPrimitives.WriteInt32LittleEndian(buf[8..], 1); // SCM_RIGHTS
        var copy = buf.ToArray();
        var ex = Assert.Throws<IOException>(() =>
            UnixAncillaryFd.ParseReceivedScmRightsFdMac(copy, 14));
        Assert.Contains("cmsg_len", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Short_cmsg_len_is_error()
    {
        if (OperatingSystem.IsWindows())
            return;
        // XNU: sendmsg with controllen 14 and cmsg_len 14 panics the machine.
        if (OperatingSystem.IsMacOS())
            return;

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var (dir, path) = UnixPrivateSocketPath.Create("hypa-scm-short");
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            UnixPrivateSocketPath.HardenSocketFile(path);
            listener.Listen(1);

            var clientTask = Task.Run(async () =>
            {
                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
                UnixAncillaryFd.SendShortCmsgLen(client.SafeHandle.DangerousGetHandle().ToInt32());
            });

            using var server = await listener.AcceptAsync();
            var serverFd = server.SafeHandle.DangerousGetHandle().ToInt32();
            var ex = Assert.ThrowsAny<IOException>(() => UnixAncillaryFd.RecvFd(serverFd));
            Assert.Contains("cmsg_len", ex.Message, StringComparison.OrdinalIgnoreCase);
            await clientTask;

            Assert.True(fcntl(0, F_GETFD, 0) >= 0, "short cmsg_len must not close stdin");
        }
        finally
        {
            UnixPrivateSocketPath.TryUnlink(path);
            UnixPrivateSocketPath.TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void Native_fdpass_selftest_rejects_non_pty_and_accepts_pty()
    {
        if (OperatingSystem.IsWindows())
            return;

        if (!TryResolvePtyHelper(out var helper))
        {
            Assert.Fail(
                "hypa-pty-host is required on Unix for --selftest-fdpass. " +
                "Build with scripts/build-hypa-pty-host.sh or set HYPA_PTY_HOST. " +
                "For a local skip set HYPA_ALLOW_MISSING_PTY_HOST=1 (not for CI).");
        }

        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = helper,
            ArgumentList = { "--selftest-fdpass" },
            // Do not redirect stdin: a stale helper without --selftest would block on Hello.
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(proc);
        Assert.True(proc.WaitForExit(15_000), "native fdpass selftest timed out for " + helper);
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(
            proc.ExitCode == 0,
            $"--selftest-fdpass failed exit={proc.ExitCode} helper={helper} stderr={stderr}");
    }

    private static bool TryResolvePtyHelper(out string helper)
    {
        helper = "";
        var rid = OperatingSystem.IsMacOS()
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
            : (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64");

        foreach (var root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(root);
            for (var i = 0; i < 8 && dir is not null; i++)
            {
                var candidate = Path.Combine(dir.FullName, "native", "runtimes", rid, "native", "hypa-pty-host");
                if (File.Exists(candidate))
                {
                    helper = candidate;
                    return true;
                }

                dir = dir.Parent;
            }
        }

        var resolved = PtyHostSupervisor.ResolveHelperPath();
        if (resolved is not null && File.Exists(resolved))
        {
            helper = resolved;
            return true;
        }

        if (IsTruthy(Environment.GetEnvironmentVariable("HYPA_ALLOW_MISSING_PTY_HOST")))
            return false;

        return false;
    }

    private static bool IsTruthy(string? value) =>
        value is "1" or "true" or "TRUE" or "yes" or "YES";

    private static bool PipeCreate(out int readFd, out int writeFd)
    {
        var fds = new int[2];
        if (pipe(fds) != 0)
        {
            readFd = writeFd = -1;
            return false;
        }

        readFd = fds[0];
        writeFd = fds[1];
        return true;
    }

    private static int OpenPtyMaster()
    {
        // O_RDWR=2, O_NOCTTY=0x10000 on macOS? Use 0 for flags portability via posix_openpt(O_RDWR).
        const int O_RDWR = 2;
        const int O_NOCTTY_LINUX = 0x100;
        const int O_NOCTTY_MAC = 0x20000;
        var flags = O_RDWR | (OperatingSystem.IsMacOS() ? O_NOCTTY_MAC : O_NOCTTY_LINUX);
        var fd = posix_openpt(flags);
        if (fd < 0)
            return -1;
        if (grantpt(fd) != 0 || unlockpt(fd) != 0)
        {
            close(fd);
            return -1;
        }

        return fd;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe(int[] pipefd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int fd);

    private const int F_GETFD = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);
}
