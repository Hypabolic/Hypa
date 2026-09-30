using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.Config;

public sealed class SystemAttachConfigFiles : IAttachConfigFiles
{
    public bool FileExists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, contents);
    }

    public void CopyFile(string source, string destination) => File.Copy(source, destination, overwrite: true);
}
