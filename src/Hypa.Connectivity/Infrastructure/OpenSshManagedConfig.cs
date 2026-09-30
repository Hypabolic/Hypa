namespace Hypa.Connectivity.Infrastructure;

/// <summary>
// Include user then system
/// config, then keepalive fallbacks. User keepalive wins (first-value-wins).
/// ControlMaster is a <c>-S</c> flag, not a file key.
/// </summary>
public sealed class OpenSshManagedConfig : IAsyncDisposable
{
    public const int ServerAliveIntervalSeconds = 15;
    public const int ServerAliveCountMax = 4;

    private int _disposed;

    public required string DirectoryPath { get; init; }

    public required string ConfigPath { get; init; }

    public string? ControlPath { get; init; }

    public static bool UsesControlSocket =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public static OpenSshManagedConfig Create(int attempt)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            $"hypa-ssh-{Environment.ProcessId}-{attempt}");
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var configPath = Path.Combine(dir, "config");
        var contents = BuildContents();
        File.WriteAllText(configPath, contents);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        string? controlPath = null;
        if (UsesControlSocket)
            controlPath = Path.Combine(dir, "control");

        return new OpenSshManagedConfig
        {
            DirectoryPath = dir,
            ConfigPath = configPath,
            ControlPath = controlPath,
        };
    }

    public static string BuildContents(
        string? userConfig = null,
        string? systemConfig = null)
    {
        userConfig ??= DefaultUserConfigPath();
        systemConfig ??= DefaultSystemConfigPath();

        var lines = new List<string>();
        if (File.Exists(userConfig))
            lines.Add("Include " + QuoteIncludePath(userConfig));
        if (File.Exists(systemConfig))
            lines.Add("Include " + QuoteIncludePath(systemConfig));
        lines.Add("Host *");
        lines.Add($"  ServerAliveInterval {ServerAliveIntervalSeconds}");
        lines.Add($"  ServerAliveCountMax {ServerAliveCountMax}");
        return string.Join('\n', lines) + "\n";
    }

    public static string QuoteIncludePath(string path)
    {
        var normalized = OperatingSystem.IsWindows()
            ? path.Replace('\\', '/')
            : path;
        if (normalized.Contains(' ') || normalized.Contains('"'))
            return "\"" + normalized.Replace("\"", "\\\"") + "\"";
        return normalized;
    }

    public static string DefaultUserConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = "/tmp";
        return Path.Combine(home, ".ssh", "config");
    }

    public static string DefaultSystemConfigPath() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "OpenSSH",
                "sshd_config")
            : "/etc/ssh/ssh_config";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
        catch
        {
            /* best effort */
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
