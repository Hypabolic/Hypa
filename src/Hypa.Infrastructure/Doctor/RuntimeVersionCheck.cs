using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Doctor;

public sealed class RuntimeVersionCheck : IDoctorCheck
{
    public string Category => "Runtime";

    public DoctorCheckResult Run() =>
        new(".NET Runtime", Environment.Version.ToString(), DoctorStatus.Ok);
}
