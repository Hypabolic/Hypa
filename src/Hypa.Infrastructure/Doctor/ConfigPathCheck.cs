using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Doctor;

public sealed class ConfigPathCheck(IProjectRootDetector rootDetector) : IDoctorCheck
{
    public string Category => "Config";

    public DoctorCheckResult Run()
    {
        var userConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hypa", "config.json");
        var projectRoot = rootDetector.Detect(Directory.GetCurrentDirectory());
        var projectConfigPath = projectRoot is not null
            ? Path.Combine(projectRoot, ".hypa", "config.json")
            : null;

        var userExists = File.Exists(userConfigPath);
        var projectExists = projectConfigPath is not null && File.Exists(projectConfigPath);

        var detail = $"User:    {userConfigPath} ({(userExists ? "exists" : "not found")})" +
                     (projectConfigPath is not null
                         ? $"\nProject: {projectConfigPath} ({(projectExists ? "exists" : "not found")})"
                         : "\nProject: (no project root detected)");

        var status = userExists || projectExists ? DoctorStatus.Ok : DoctorStatus.Warn;

        return new DoctorCheckResult("Config Paths", userConfigPath, status, detail);
    }
}
