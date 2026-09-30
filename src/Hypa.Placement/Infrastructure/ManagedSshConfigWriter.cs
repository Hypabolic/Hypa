namespace Hypa.Placement.Infrastructure;

/// <summary>
/// Private temporary SSH config. User keepalive wins.
/// then writes ServerAlive fallbacks. Linux and macOS also get a control socket.
/// Windows OpenSSH does not.
/// </summary>
public sealed record ManagedSshConfig : IDisposable
{
    public required string Directory { get; init; }
    public required string ConfigPath { get; init; }
    public string? ControlPath { get; init; }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class ManagedSshConfigWriter
{
    public const int ServerAliveInterval = 15;
    public const int ServerAliveCountMax = 4;
    public const string ControlSocketName = "ctl";

    public static ManagedSshConfig Write(
        string? userConfigPath = null,
        string? systemConfigPath = null,
        bool includeControlSocket = true)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "hypa-ssh-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config");
        string? controlPath = includeControlSocket
            ? Path.Combine(dir, ControlSocketName)
            : null;

        using var file = new FileStream(
            configPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
        using var writer = new StreamWriter(file);
        if (!string.IsNullOrEmpty(userConfigPath) && File.Exists(userConfigPath))
            writer.WriteLine("Include " + QuotePath(userConfigPath));
        if (!string.IsNullOrEmpty(systemConfigPath) && File.Exists(systemConfigPath))
            writer.WriteLine("Include " + QuotePath(systemConfigPath));
        writer.WriteLine("Host *");
        writer.WriteLine($"  ServerAliveInterval {ServerAliveInterval}");
        writer.WriteLine($"  ServerAliveCountMax {ServerAliveCountMax}");
        writer.Flush();

        return new ManagedSshConfig
        {
            Directory = dir,
            ConfigPath = configPath,
            ControlPath = controlPath,
        };
    }

    public static bool UsesControlSocket =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public static string DefaultUserConfigPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh",
            "config");

    internal static string QuotePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return "\"" + normalized.Replace("\"", "\\\"") + "\"";
    }
}
