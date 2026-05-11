namespace Hypa.Runtime.Application.Ports;

public interface IFileSystem
{
    string GetCurrentDirectory();
    bool DirectoryExists(string path);
    IReadOnlyList<string> GetFiles(string directory, string searchPattern);
    byte[] ReadAllBytes(string path);
}
