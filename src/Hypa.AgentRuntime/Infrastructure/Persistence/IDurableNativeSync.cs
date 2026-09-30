using Microsoft.Win32.SafeHandles;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// OS durability primitive. <see cref="DurableFileSync"/> calls file then directory.
/// Tests own a recorder or a phase-failure implementation; production has no
/// process-global mutable hook.
/// </summary>
internal interface IDurableNativeSync
{
    /// <summary>Named primitive: F_FULLFSYNC, fsync, or FlushFileBuffers.</summary>
    string FileSyncKind { get; }

    void SyncHandle(SafeFileHandle handle);

    void SyncDirectory(string directoryPath);
}
