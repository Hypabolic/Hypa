namespace Hypa.Runtime.Domain.Parsers.Canonical;

public sealed record TestRunResult
{
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public int Total { get; init; }
    public string Duration { get; init; } = string.Empty;
    public IReadOnlyList<FailingTest> FailingTests { get; init; } = [];
}
