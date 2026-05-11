using System.Runtime.InteropServices;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Doctor;

public sealed class OsCheck : IDoctorCheck
{
    public string Category => "System";

    public DoctorCheckResult Run() =>
        new("OS", RuntimeInformation.OSDescription, DoctorStatus.Ok);
}
