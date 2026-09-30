using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hypa.Terminal.Pty;

/// <summary>
/// AOT-safe AF_UNIX SCM_RIGHTS send/recv for a single file descriptor.
/// Platform layouts for msghdr/cmsghdr differ between Linux and macOS.
/// Sockets used with async I/O are often O_NONBLOCK; we clear that for the call.
/// Ownership receive path validates PTY master type and sets FD_CLOEXEC.
/// </summary>
internal static class UnixAncillaryFd
{
    /// <summary>
    /// Ownership-path SCM_RIGHTS send: require a PTY master, set FD_CLOEXEC, then send.
    /// Rejects pipes/files before transfer.
    /// </summary>
    public static void SendFd(int connectedSocket, int fdToSend)
    {
        if (fdToSend < 0)
            throw new ArgumentOutOfRangeException(nameof(fdToSend));
        if (!UnixPtyFdValidation.IsPtyMaster(fdToSend))
        {
            throw new IOException(
                $"SCM_RIGHTS send rejected: descriptor {fdToSend} is not a PTY master.");
        }

        // Ownership hygiene: ensure the descriptor we export cannot leak across exec.
        UnixPtyFdValidation.SetCloexec(fdToSend);
        SendFdUnchecked(connectedSocket, fdToSend);
    }

    /// <summary>
    /// Transport-only SCM_RIGHTS send without PTY validation.
    /// Used by tests to exercise receive-side rejection of non-PTY descriptors.
    /// Production ownership paths must call <see cref="SendFd"/>.
    /// </summary>
    internal static void SendFdUnchecked(int connectedSocket, int fdToSend)
    {
        if (fdToSend < 0)
            throw new ArgumentOutOfRangeException(nameof(fdToSend));

        // Still harden CLOEXEC on any FD that crosses the wire.
        UnixPtyFdValidation.SetCloexec(fdToSend);

        using var _ = TemporarilyBlocking(connectedSocket);
        if (OperatingSystem.IsMacOS())
            SendFdMac(connectedSocket, fdToSend);
        else if (OperatingSystem.IsLinux())
            SendFdLinux(connectedSocket, fdToSend);
        else
            throw new PlatformNotSupportedException("SCM_RIGHTS is Unix-only.");
    }

    /// <summary>
    /// Send an SCM_RIGHTS control message with no file-descriptor payload.
    /// Tests use this to prove empty rights are an error and do not close stdin.
    /// </summary>
    internal static void SendEmptyScmRights(int connectedSocket)
    {
        using var _ = TemporarilyBlocking(connectedSocket);
        if (OperatingSystem.IsMacOS())
            SendEmptyScmRightsMac(connectedSocket);
        else if (OperatingSystem.IsLinux())
            SendEmptyScmRightsLinux(connectedSocket);
        else
            throw new PlatformNotSupportedException("SCM_RIGHTS is Unix-only.");
    }

    /// <summary>
    /// Send an SCM_RIGHTS header with <c>cmsg_len</c> shorter than <c>CMSG_LEN(4)</c>.
    /// Linux only. Darwin panics in <c>uipc_syscalls.c</c> when
    /// <c>CMSG_ALIGN(cmsg_len) &gt; msg_controllen</c> — never call this on macOS.
    /// </summary>
    internal static void SendShortCmsgLen(int connectedSocket)
    {
        if (OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "XNU panics (cp_size > buflen) if sendmsg SCM_RIGHTS has unaligned/short msg_controllen. " +
                "Parse short cmsg in-process; do not sendmsg it on Darwin.");
        }

        using var _ = TemporarilyBlocking(connectedSocket);
        if (OperatingSystem.IsLinux())
            SendShortCmsgLenLinux(connectedSocket);
        else
            throw new PlatformNotSupportedException("SCM_RIGHTS is Unix-only.");
    }

    /// <summary>Raw SCM_RIGHTS receive (no type check). Prefer <see cref="RecvPtyMasterSafeHandle"/>.</summary>
    public static int RecvFd(int connectedSocket)
    {
        using var _ = TemporarilyBlocking(connectedSocket);
        if (OperatingSystem.IsMacOS())
            return RecvFdMac(connectedSocket);
        if (OperatingSystem.IsLinux())
            return RecvFdLinux(connectedSocket);
        throw new PlatformNotSupportedException("SCM_RIGHTS is Unix-only.");
    }

    /// <summary>
    /// Receive one SCM_RIGHTS FD, require it is a PTY master, set FD_CLOEXEC, wrap as SafeFileHandle.
    /// Non-PTY descriptors are closed and rejected.
    /// </summary>
    public static SafeFileHandle RecvPtyMasterSafeHandle(int connectedSocket)
    {
        var fd = RecvFd(connectedSocket);
        if (fd <= 0)
            throw new IOException("recvmsg SCM_RIGHTS returned invalid FD.");

        var owned = fd;
        try
        {
            UnixPtyFdValidation.SetCloexec(owned);
            if (!UnixPtyFdValidation.IsPtyMaster(owned))
            {
                throw new IOException(
                    $"SCM_RIGHTS descriptor {owned} is not a PTY master (rejected non-PTY adoption).");
            }

            var handle = new SafeFileHandle(new IntPtr(owned), ownsHandle: true);
            owned = -1; // SafeFileHandle owns it
            return handle;
        }
        finally
        {
            if (owned >= 0)
            {
                try { _ = close(owned); }
                catch { /* preserve primary */ }
            }
        }
    }

    /// <summary>
    /// Backward-compatible name used by handoff / FD-pass servers — always validates PTY master.
    /// </summary>
    public static SafeFileHandle RecvSafeHandle(int connectedSocket) =>
        RecvPtyMasterSafeHandle(connectedSocket);

    /// <summary>
    /// Clear O_NONBLOCK for the duration of sendmsg/recvmsg (async sockets are non-blocking).
    /// </summary>
    private static NonblockRestore TemporarilyBlocking(int fd)
    {
        var flags = Fcntl(fd, F_GETFL, 0);
        if (flags < 0)
            return new NonblockRestore(fd, -1);
        if ((flags & O_NONBLOCK) == 0)
            return new NonblockRestore(fd, -1);
        _ = Fcntl(fd, F_SETFL, flags & ~O_NONBLOCK);
        return new NonblockRestore(fd, flags);
    }

    private readonly struct NonblockRestore : IDisposable
    {
        private readonly int _fd;
        private readonly int _flags;

        public NonblockRestore(int fd, int flags)
        {
            _fd = fd;
            _flags = flags;
        }

        public void Dispose()
        {
            if (_flags >= 0)
                _ = Fcntl(_fd, F_SETFL, _flags);
        }
    }

    // macOS: O_NONBLOCK=0x0004; Linux: 0x800 (04000 octal) — pick per-OS.
    private static int O_NONBLOCK => OperatingSystem.IsMacOS() ? 0x0004 : 0x800;
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;

    private const int SOL_SOCKET_MAC = 0xffff;
    private const int SCM_RIGHTS = 1;
    private const int SOL_SOCKET_LINUX = 1;

    /*
     * Empirically working layout on macOS (LP64): cmsghdr is 12 bytes and the
     * kernel accepts FD data immediately after the header when cmsg_len=16.
     * Linux LP64 uses 16-byte cmsghdr with data at +16.
     */

    private static unsafe void SendFdMac(int sock, int fd)
    {
        byte dummy = 0;
        var iov = new Iovec { Base = &dummy, Len = (IntPtr)1 };

        const int cmsgSpace = 32;
        byte* cbuf = stackalloc byte[cmsgSpace];
        for (var i = 0; i < cmsgSpace; i++)
            cbuf[i] = 0;

        var cmsg = (CmsghdrMac*)cbuf;
        cmsg->Len = DarwinCmsgLenWithFd; // 12-byte hdr + 4-byte fd
        cmsg->Level = SOL_SOCKET_MAC;
        cmsg->Type = SCM_RIGHTS;
        *(int*)(cbuf + 12) = fd;

        EnsureDarwinSendmsgControlLen(cmsg->Len, DarwinCmsgLenWithFd);
        var msg = new MsghdrMac
        {
            Name = null,
            NameLen = 0,
            Iov = &iov,
            IovLen = 1,
            Control = cbuf,
            ControlLen = DarwinCmsgLenWithFd,
            Flags = 0,
        };

        var n = sendmsg(sock, &msg, 0);
        if (n < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"sendmsg(SCM_RIGHTS) failed errno={err}");
        }
    }

    private static unsafe void SendEmptyScmRightsMac(int sock)
    {
        byte dummy = 0;
        var iov = new Iovec { Base = &dummy, Len = (IntPtr)1 };

        const int cmsgSpace = 32;
        byte* cbuf = stackalloc byte[cmsgSpace];
        for (var i = 0; i < cmsgSpace; i++)
            cbuf[i] = 0;

        var cmsg = (CmsghdrMac*)cbuf;
        cmsg->Len = DarwinEmptyScmRightsCmsgLen; // header only — no fd payload
        cmsg->Level = SOL_SOCKET_MAC;
        cmsg->Type = SCM_RIGHTS;

        // XNU: CMSG_ALIGN(12)=16. msg_controllen MUST be >= 16 or the kernel
        // panics: cp_size > buflen @ uipc_syscalls.c (not EINVAL).
        EnsureDarwinSendmsgControlLen(cmsg->Len, DarwinEmptyScmRightsControlLen);
        var msg = new MsghdrMac
        {
            Name = null,
            NameLen = 0,
            Iov = &iov,
            IovLen = 1,
            Control = cbuf,
            ControlLen = DarwinEmptyScmRightsControlLen,
            Flags = 0,
        };

        var n = sendmsg(sock, &msg, 0);
        if (n < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"sendmsg(empty SCM_RIGHTS) failed errno={err}");
        }
    }

    /// <summary>
    /// Parse one SCM_RIGHTS fd from a received control buffer (macOS layout).
    /// Used by recvmsg and by tests so Darwin never sendmsg a short cmsg.
    /// </summary>
    internal static int ParseReceivedScmRightsFdMac(ReadOnlySpan<byte> control, uint controlLen)
    {
        const uint headerLen = 12;
        const uint cmsgLenFd = 16;
        if (controlLen < headerLen || control.Length < headerLen)
            throw new IOException(
                $"recvmsg missing control data (controllen={controlLen}); empty or short cmsg_len is an error.");

        var len = BinaryPrimitives.ReadUInt32LittleEndian(control);
        var level = BinaryPrimitives.ReadInt32LittleEndian(control.Slice(4));
        var type = BinaryPrimitives.ReadInt32LittleEndian(control.Slice(8));
        if (level != SOL_SOCKET_MAC || type != SCM_RIGHTS)
            throw new IOException($"recvmsg control is not SCM_RIGHTS (level={level} type={type}).");
        if (len < cmsgLenFd)
            throw new IOException($"recvmsg SCM_RIGHTS cmsg_len={len} shorter than CMSG_LEN(4)={cmsgLenFd}.");
        if (len > controlLen)
            throw new IOException($"recvmsg SCM_RIGHTS cmsg_len={len} exceeds controllen={controlLen}.");
        if (control.Length < 16)
            throw new IOException($"recvmsg SCM_RIGHTS cmsg_len={len} shorter than CMSG_LEN(4)={cmsgLenFd}.");

        var fd = BinaryPrimitives.ReadInt32LittleEndian(control.Slice(12));
        if (fd <= 0)
            throw new IOException($"recvmsg SCM_RIGHTS delivered invalid fd {fd}.");
        return fd;
    }

    private static unsafe int RecvFdMac(int sock)
    {
        byte dummy = 0;
        var iov = new Iovec { Base = &dummy, Len = (IntPtr)1 };
        const int cmsgSpace = 32;
        byte* cbuf = stackalloc byte[cmsgSpace];
        for (var i = 0; i < cmsgSpace; i++)
            cbuf[i] = 0;

        var msg = new MsghdrMac
        {
            Name = null,
            NameLen = 0,
            Iov = &iov,
            IovLen = 1,
            Control = cbuf,
            ControlLen = (uint)cmsgSpace,
            Flags = 0,
        };

        var n = recvmsg(sock, &msg, 0);
        if (n < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"recvmsg(SCM_RIGHTS) failed errno={err}");
        }

        return ParseReceivedScmRightsFdMac(new ReadOnlySpan<byte>(cbuf, cmsgSpace), msg.ControlLen);
    }

    internal const uint DarwinCmsgHdrLen = 12;
    internal const uint DarwinCmsgLenWithFd = 16;
    internal const uint DarwinEmptyScmRightsCmsgLen = 12;
    internal const uint DarwinEmptyScmRightsControlLen = 16;

    /// <summary>Darwin <c>CMSG_ALIGN</c> for 32-bit cmsg_len (XNU aligns to 4).</summary>
    internal static uint DarwinCmsgAlign(uint len) => (len + 3u) & ~3u;

    /// <summary>
    /// Fail closed before sendmsg when Darwin would panic:
    /// <c>msg_controllen &lt; CMSG_ALIGN(cmsg_len)</c>.
    /// </summary>
    internal static void EnsureDarwinSendmsgControlLen(uint cmsgLen, uint controlLen)
    {
        var aligned = DarwinCmsgAlign(cmsgLen);
        if (controlLen < aligned)
        {
            throw new PlatformNotSupportedException(
                "XNU panics (cp_size > buflen) if sendmsg SCM_RIGHTS has " +
                $"msg_controllen={controlLen} < CMSG_ALIGN(cmsg_len)={aligned}.");
        }
    }

    private static unsafe void SendFdLinux(int sock, int fd)
    {
        byte dummy = 0;
        var iov = new Iovec { Base = &dummy, Len = (IntPtr)1 };

        const int cmsgSpace = 32;
        byte* cbuf = stackalloc byte[cmsgSpace];
        for (var i = 0; i < cmsgSpace; i++)
            cbuf[i] = 0;

        var cmsg = (CmsghdrLinux*)cbuf;
        cmsg->Len = (UIntPtr)20; // CMSG_LEN(4) ≈ 16+4
        cmsg->Level = SOL_SOCKET_LINUX;
        cmsg->Type = SCM_RIGHTS;
        *(int*)(cbuf + 16) = fd;

        var msg = new MsghdrLinux
        {
            Name = null,
            NameLen = 0,
            Iov = &iov,
            IovLen = (UIntPtr)1,
            Control = cbuf,
            ControlLen = (UIntPtr)cmsgSpace,
            Flags = 0,
        };

        var n = sendmsg(sock, &msg, 0);
        if (n < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"sendmsg(SCM_RIGHTS) failed errno={err}");
        }
    }

    private static unsafe void SendEmptyScmRightsLinux(int sock)
    {
        byte dummy = 0;
        var iov = new Iovec { Base = &dummy, Len = (IntPtr)1 };

        const int cmsgSpace = 32;
        byte* cbuf = stackalloc byte[cmsgSpace];
        for (var i = 0; i < cmsgSpace; i++)
            cbuf[i] = 0;

        var cmsg = (CmsghdrLinux*)cbuf;
        cmsg->Len = (UIntPtr)16; // header only — no fd payload
        cmsg->Level = SOL_SOCKET_LINUX;
        cmsg->Type = SCM_RIGHTS;

        var msg = new MsghdrLinux
        {
            Name = null,
            NameLen = 0,
            Iov = &iov,
            IovLen = (UIntPtr)1,
            Control = cbuf,
            ControlLen = (UIntPtr)16,
            Flags = 0,
        };

        var n = sendmsg(sock, &msg, 0);
        if (n < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"sendmsg(empty SCM_RIGHTS) failed errno={err}");
        }
    }

    private static unsafe void SendShortCmsgLenLinux(int sock)
    {
        byte dummy = 0;
        var iov = new Iovec { Base = &dummy, Len = (IntPtr)1 };

        const int cmsgSpace = 32;
        byte* cbuf = stackalloc byte[cmsgSpace];
        for (var i = 0; i < cmsgSpace; i++)
            cbuf[i] = 0;

        var cmsg = (CmsghdrLinux*)cbuf;
        cmsg->Len = (UIntPtr)18; // header+2 — shorter than CMSG_LEN(4)=20
        cmsg->Level = SOL_SOCKET_LINUX;
        cmsg->Type = SCM_RIGHTS;

        var msg = new MsghdrLinux
        {
            Name = null,
            NameLen = 0,
            Iov = &iov,
            IovLen = (UIntPtr)1,
            Control = cbuf,
            ControlLen = (UIntPtr)18,
            Flags = 0,
        };

        var n = sendmsg(sock, &msg, 0);
        if (n < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"sendmsg(short SCM_RIGHTS) failed errno={err}");
        }
    }

    private static unsafe int RecvFdLinux(int sock)
    {
        byte dummy = 0;
        var iov = new Iovec { Base = &dummy, Len = (IntPtr)1 };
        const int cmsgSpace = 32;
        byte* cbuf = stackalloc byte[cmsgSpace];
        for (var i = 0; i < cmsgSpace; i++)
            cbuf[i] = 0;

        var msg = new MsghdrLinux
        {
            Name = null,
            NameLen = 0,
            Iov = &iov,
            IovLen = (UIntPtr)1,
            Control = cbuf,
            ControlLen = (UIntPtr)cmsgSpace,
            Flags = 0,
        };

        var n = recvmsg(sock, &msg, 0);
        if (n < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"recvmsg(SCM_RIGHTS) failed errno={err}");
        }

        return ParseReceivedScmRightsFdLinux(new ReadOnlySpan<byte>(cbuf, cmsgSpace), (ulong)msg.ControlLen);
    }

    /// <summary>
    /// Parse one SCM_RIGHTS fd from a received control buffer (Linux LP64 layout).
    /// </summary>
    internal static int ParseReceivedScmRightsFdLinux(ReadOnlySpan<byte> control, ulong controlLen)
    {
        const ulong headerLen = 16;
        const ulong cmsgLenFd = 20; // CMSG_LEN(sizeof(int)) on Linux LP64
        if (controlLen < headerLen || (ulong)control.Length < headerLen)
            throw new IOException(
                $"recvmsg missing control data (controllen={controlLen}); empty or short cmsg_len is an error.");

        var len = (ulong)(IntPtr.Size == 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(control)
            : BinaryPrimitives.ReadUInt32LittleEndian(control));
        var levelOff = IntPtr.Size;
        var typeOff = IntPtr.Size + 4;
        var fdOff = 16;
        var level = BinaryPrimitives.ReadInt32LittleEndian(control.Slice(levelOff));
        var type = BinaryPrimitives.ReadInt32LittleEndian(control.Slice(typeOff));
        if (level != SOL_SOCKET_LINUX || type != SCM_RIGHTS)
            throw new IOException($"recvmsg control is not SCM_RIGHTS (level={level} type={type}).");
        if (len < cmsgLenFd)
            throw new IOException($"recvmsg SCM_RIGHTS cmsg_len={len} shorter than CMSG_LEN(4)={cmsgLenFd}.");
        if (len > controlLen)
            throw new IOException($"recvmsg SCM_RIGHTS cmsg_len={len} exceeds controllen={controlLen}.");
        if ((ulong)control.Length < cmsgLenFd)
            throw new IOException($"recvmsg SCM_RIGHTS cmsg_len={len} shorter than CMSG_LEN(4)={cmsgLenFd}.");

        var fd = BinaryPrimitives.ReadInt32LittleEndian(control.Slice(fdOff));
        if (fd <= 0)
            throw new IOException($"recvmsg SCM_RIGHTS delivered invalid fd {fd}.");
        return fd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Iovec
    {
        public void* Base;
        public IntPtr Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MsghdrMac
    {
        public void* Name;
        public uint NameLen;
        public Iovec* Iov;
        public int IovLen;
        public void* Control;
        public uint ControlLen;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CmsghdrMac
    {
        public uint Len;
        public int Level;
        public int Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MsghdrLinux
    {
        public void* Name;
        public uint NameLen;
        public Iovec* Iov;
        public UIntPtr IovLen;
        public void* Control;
        public UIntPtr ControlLen;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CmsghdrLinux
    {
        public UIntPtr Len;
        public int Level;
        public int Type;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int sendmsg(int sockfd, void* msg, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int recvmsg(int sockfd, void* msg, int flags);

    // Darwin arm64: fcntl is variadic; Apple passes variadic args on the stack.
    // Six pads exhaust x2-x7 so the flags arg lands at [sp+0].
    // Same sequence as UnixRawTerminal.Fcntl.
    private static bool DarwinStackVariadic =>
        OperatingSystem.IsMacOS()
        && RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

    private static int Fcntl(int fd, int cmd, int arg) =>
        DarwinStackVariadic
            ? fcntl_darwin_variadic(fd, cmd, 0, 0, 0, 0, 0, 0, arg)
            : fcntl(fd, cmd, arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl_darwin_variadic(
        int fd,
        int cmd,
        nint pad2, nint pad3, nint pad4, nint pad5, nint pad6, nint pad7,
        nint arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
