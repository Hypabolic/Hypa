namespace Hypa.Runtime.Domain.Filters;

public sealed record TrustRecord
{
    public string ProjectRoot { get; init; } = string.Empty;
    public string FilterFilePath { get; init; } = string.Empty;
    public string FileHash { get; init; } = string.Empty;
    public DateTimeOffset GrantedAt { get; init; }
}
