namespace Hypa.Runtime.Domain.Filters;

public sealed record FilterSavingsEstimate
{
    public string FilterId { get; init; } = string.Empty;
    public string AppliesTo { get; init; } = string.Empty;
    public int OriginalTokens { get; init; }
    public int CompressedTokens { get; init; }
    public int SavedTokens => Math.Max(0, OriginalTokens - CompressedTokens);
    public int SavedPercent => OriginalTokens == 0
        ? 0
        : (int)Math.Round((1.0 - (double)CompressedTokens / OriginalTokens) * 100);
    public int OriginalBytes { get; init; }
    public int CompressedBytes { get; init; }
    public string SampleKind { get; init; } = "synthetic";
}
