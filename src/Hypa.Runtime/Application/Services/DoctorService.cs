using Hypa.Runtime.Application.Ports;

namespace Hypa.Runtime.Application.Services;

public sealed class DoctorService(IEnumerable<IDoctorCheck> checks)
{
    public IReadOnlyList<DoctorCheckResult> Run() =>
        checks.Select(c => c.Run()).ToList();
}
