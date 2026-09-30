using Hypa.AgentRuntime.Application.Integrations;

namespace Hypa.AgentRuntime.Infrastructure.Integrations;

public sealed class SystemIntegrationFiles : IIntegrationFiles
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, contents);
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

    public IReadOnlyList<string> ListDirectories(string path)
    {
        if (!Directory.Exists(path))
            return [];
        return Directory.GetDirectories(path);
    }

    public IReadOnlyList<string> ListFiles(string path)
    {
        if (!Directory.Exists(path))
            return [];
        return Directory.GetFiles(path);
    }

    public void SetUnixExecutable(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
#pragma warning disable CA1416
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(
            path,
            mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
    }
}
