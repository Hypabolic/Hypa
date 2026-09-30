using System.Runtime.InteropServices;
using Hypa.AgentRuntime.Application.Plugins;

namespace Hypa.AgentRuntime.Infrastructure.Plugins;

public sealed class SystemPluginFiles : IPluginFiles
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    public void CopyFile(string source, string destination)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.Copy(source, destination, overwrite: true);
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var mode = File.GetUnixFileMode(destination);
            File.SetUnixFileMode(
                destination,
                mode
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool DeleteFile(string path)
    {
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    public bool DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return false;
        Directory.Delete(path, recursive: true);
        return true;
    }

    public string GetFullPath(string path) => Path.GetFullPath(path);
}
