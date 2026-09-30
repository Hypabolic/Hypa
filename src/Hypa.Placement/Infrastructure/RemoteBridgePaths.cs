namespace Hypa.Placement.Infrastructure;

/// <summary>
// / Atomic private bridge directory.
/// uses pid and hash in the socket name under temp, not a shared ancestor.
/// </summary>
internal sealed class RemoteBridgeDirectory : IDisposable
{
    private RemoteBridgeDirectory(string directoryPath)
    {
        DirectoryPath = directoryPath;
        SocketPath = Path.Combine(directoryPath, "hypa.sock");
    }

    internal string DirectoryPath { get; }

    internal string SocketPath { get; }

    internal static RemoteBridgeDirectory Create()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "SSH bridge directory requires a Unix local client.");
        }

        var created = Directory.CreateTempSubdirectory("hypa-ssh-bridge-");
        Hypa.ControlPlane.Unix.UnixPrivatePathGuard.EnsureBridgeDirectory(created.FullName);
        return new RemoteBridgeDirectory(created.FullName);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
