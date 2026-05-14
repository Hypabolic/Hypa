using Hypa.Runtime.Application.Ports;

namespace Hypa.Runtime.Application.Services;

public sealed class DoctorService(IEnumerable<IDoctorCheck> checks)
{
    public IReadOnlyList<DoctorCheckResult> Run() =>
        checks.Select(RunSafe).ToList();

    private static DoctorCheckResult RunSafe(IDoctorCheck check)
    {
        try
        {
            return check.Run();
        }
        catch (Exception ex)
        {
            return new DoctorCheckResult(check.Category, "check failed", DoctorStatus.Fail, ex.Message);
        }
    }
}
