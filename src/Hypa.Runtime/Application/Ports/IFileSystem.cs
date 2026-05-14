namespace Hypa.Runtime.Application.Ports;

public interface IFileSystem
{
    string GetCurrentDirectory();
    bool DirectoryExists(string path);
    IReadOnlyList<string> GetFiles(string directory, string searchPattern, bool recursive = false);
    byte[] ReadAllBytes(string path);
}
