namespace Hypa.Runtime.Application.Ports;

public interface IDoctorCheck
{
    string Category { get; }
    DoctorCheckResult Run();
}

public sealed record DoctorCheckResult(
    string Label,
    string Value,
    DoctorStatus Status,
    string? Detail = null,
    string? Hint = null);

public enum DoctorStatus { Ok, Warn, Fail }
