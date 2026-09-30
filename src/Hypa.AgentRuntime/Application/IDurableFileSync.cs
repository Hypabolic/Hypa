using Microsoft.Win32.SafeHandles;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Two-phase durable sync: file first, then the parent directory.
/// Tests inject a native-backed recorder or a phase-failure double.
/// </summary>
public interface IDurableFileSync
{
    /// <summary>
    /// Fsync <paramref name="fileHandle"/>, then fsync the parent directory of
    /// <paramref name="filePath"/>.
    /// </summary>
    void SyncFileThenDirectory(SafeFileHandle fileHandle, string filePath);
}
