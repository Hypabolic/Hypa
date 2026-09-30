using Hypa.AgentRuntime.Application.Integrations;

namespace Hypa.AgentRuntime.Infrastructure.Integrations;

public sealed class SystemIntegrationEnvironment : IIntegrationEnvironment
{
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string? UserHome
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
                return home.Trim();
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(profile) ? null : profile;
        }
    }

    public string? PathVariable => Environment.GetEnvironmentVariable("PATH");

    public bool FileIsExecutable(string path)
    {
        if (!File.Exists(path))
            return false;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return true;
#pragma warning disable CA1416
        var mode = File.GetUnixFileMode(path);
        return mode.HasFlag(UnixFileMode.UserExecute)
            || mode.HasFlag(UnixFileMode.GroupExecute)
            || mode.HasFlag(UnixFileMode.OtherExecute);
#pragma warning restore CA1416
    }
}
