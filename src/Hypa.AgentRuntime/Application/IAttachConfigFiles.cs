namespace Hypa.AgentRuntime.Application;

/// <summary>File-system port for attach TOML. Tests inject a temp or in-memory store.</summary>
public interface IAttachConfigFiles
{
    bool FileExists(string path);

    string ReadAllText(string path);

    void WriteAllText(string path, string contents);

    void CopyFile(string source, string destination);
}
