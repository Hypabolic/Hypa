namespace Hypa.Annotate.Application;

/// <summary>
/// Last non-empty stdout line for <c>hypa doctor</c>.
/// </summary>
public sealed record PluginDoctorStdout
{
    public required string Status { get; init; }

    public string? Value { get; init; }

    public string? Detail { get; init; }

    public string? Hint { get; init; }
}
