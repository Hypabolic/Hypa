namespace Hypa.Runtime.Application.Ports;

public interface IFileSystem
{
    string GetCurrentDirectory();
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IReadOnlyList<string> GetFiles(string directory, string searchPattern, bool recursive = false);
    byte[] ReadAllBytes(string path);
    string ReadAllText(string path);
}
