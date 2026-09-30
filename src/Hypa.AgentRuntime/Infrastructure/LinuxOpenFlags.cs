using System.Runtime.InteropServices;

namespace Hypa.AgentRuntime.Infrastructure;

/// <summary>
/// Linux <c>open</c> flag bits that differ by CPU. x86 and x86-64 use their own
/// values. arm, arm64, and the other generic ABIs use different bits, and the
/// x86 value for <c>O_DIRECTORY</c> is <c>O_DIRECT</c> there while the x86
/// value for <c>O_NOFOLLOW</c> is <c>O_LARGEFILE</c>. A wrong value means the
/// open follows symlinks or fails with EINVAL.
/// </summary>
internal static class LinuxOpenFlags
{
    private static bool UsesX86Bits =>
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;

    public static int NoFollow => UsesX86Bits ? 0x20000 : 0x8000;

    public static int Directory => UsesX86Bits ? 0x10000 : 0x4000;
}
