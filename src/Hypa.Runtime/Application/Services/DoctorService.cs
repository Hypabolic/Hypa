using Hypa.Runtime.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Hypa.Runtime.Application.Services;

public sealed class DoctorService
{
    private readonly IEnumerable<IDoctorCheck> _checks;
    private readonly IEnumerable<IDoctorCheckSource> _sources;

    public DoctorService(IEnumerable<IDoctorCheck> checks)
        : this(checks, [])
    {
    }

    [ActivatorUtilitiesConstructor]
    public DoctorService(IEnumerable<IDoctorCheck> checks, IEnumerable<IDoctorCheckSource> sources)
    {
        _checks = checks;
        _sources = sources;
    }

    public IReadOnlyList<DoctorCheckResult> Run()
    {
        var all = new List<IDoctorCheck>();
        all.AddRange(_checks);
        foreach (var source in _sources)
            all.AddRange(source.GetChecks());
        return all.Select(RunSafe).ToList();
    }

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
