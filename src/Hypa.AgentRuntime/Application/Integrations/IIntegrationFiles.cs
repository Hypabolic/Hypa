namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>File-system port for official integration install and uninstall.</summary>
public interface IIntegrationFiles
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    string ReadAllText(string path);

    void WriteAllText(string path, string contents);

    void CreateDirectory(string path);

    bool DeleteFile(string path);

    bool DeleteDirectory(string path);

    IReadOnlyList<string> ListDirectories(string path);

    IReadOnlyList<string> ListFiles(string path);

    void SetUnixExecutable(string path);
}
