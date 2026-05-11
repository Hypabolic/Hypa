namespace Hypa.Runtime.Domain.Parsers.Canonical;

public sealed record BuildResult
{
    public bool Succeeded { get; init; }
    public IReadOnlyList<BuildDiagnostic> Errors { get; init; } = [];
    public IReadOnlyList<BuildDiagnostic> Warnings { get; init; } = [];
    public string ElapsedTime { get; init; } = string.Empty;
}
