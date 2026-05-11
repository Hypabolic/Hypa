using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Doctor;

public sealed class ProjectRootCheck(IProjectRootDetector rootDetector) : IDoctorCheck
{
    public string Category => "Project";

    public DoctorCheckResult Run()
    {
        var root = rootDetector.Detect(Directory.GetCurrentDirectory());
        return root is not null
            ? new DoctorCheckResult("Project Root", root, DoctorStatus.Ok)
            : new DoctorCheckResult("Project Root", "not detected", DoctorStatus.Warn,
                "No .git, .hypa, *.sln, *.slnx, or *.csproj found in any parent directory.");
    }
}
