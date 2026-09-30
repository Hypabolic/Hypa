namespace Hypa.Runtime.Application.Ports;

/// <summary>
/// Discovers extra doctor checks at run time.
/// </summary>
public interface IDoctorCheckSource
{
    IEnumerable<IDoctorCheck> GetChecks();
}
