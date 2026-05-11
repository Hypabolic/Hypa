namespace Hypa.Runtime.Domain.Parsers.Canonical;

public sealed record LintResult
{
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public IReadOnlyList<LintDiagnostic> Diagnostics { get; init; } = [];
}
