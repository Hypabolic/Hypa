using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.Config;

public sealed class SystemAttachConfigEnvironment : IAttachConfigEnvironment
{
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string UserHome
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
                return home;
            return Environment.GetEnvironmentVariable("HOME") ?? "";
        }
    }

    public string? AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public bool IsWindows => OperatingSystem.IsWindows();

    public bool IsMacOs => OperatingSystem.IsMacOS();
}
