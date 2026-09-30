namespace Hypa.ControlPlane.Unix;

/// <summary>
/// Strategy: enforce a private parent directory (0700) and socket inode (0600)
/// before listen. Failures must abort server start.
/// </summary>
public interface IUnixSocketModeGuard
{
    /// <summary>
    /// Create the immediate parent if missing. Set and verify exact mode 0700.
    /// Refuse an existing parent that is writable by group or others
    /// (0775, 0777, /tmp 1777). Do not chmod that parent.
    /// </summary>
    void EnsurePrivateDirectory(string directory);

    /// <summary>
    /// After Bind and before Listen: set and verify exact socket mode 0600.
    /// </summary>
    void HardenSocket(string socketPath);
}
