using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.System;

public sealed class SystemFileSystem : IFileSystem
{
    public string GetCurrentDirectory() => Directory.GetCurrentDirectory();

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> GetFiles(string directory, string searchPattern) =>
        Directory.GetFiles(directory, searchPattern);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
}
