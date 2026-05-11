using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Doctor;

public sealed class RewriteRegistryCheck(IEnumerable<ICommandRewriteStrategy> strategies) : IDoctorCheck
{
    public string Category => "Rewrite";

    public DoctorCheckResult Run()
    {
        var count = strategies.Count();
        return count > 0
            ? new DoctorCheckResult("Registry", $"{count} strategies", DoctorStatus.Ok)
            : new DoctorCheckResult("Registry", "no strategies registered", DoctorStatus.Fail);
    }
}
