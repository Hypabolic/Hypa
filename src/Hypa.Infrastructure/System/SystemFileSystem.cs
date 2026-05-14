using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.System;

public sealed class SystemFileSystem : IFileSystem
{
    public string GetCurrentDirectory() => Directory.GetCurrentDirectory();

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> GetFiles(string directory, string searchPattern, bool recursive = false) =>
        Directory.GetFiles(directory, searchPattern,
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
}
