namespace Hypa.Runtime.Domain.Parsers.Canonical;

public sealed record DependencyState
{
    public string PackageManager { get; init; } = string.Empty;
    public int TotalPackages { get; init; }
    public IReadOnlyList<ConflictEntry> Conflicts { get; init; } = [];
}
