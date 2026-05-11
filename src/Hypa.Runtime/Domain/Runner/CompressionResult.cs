using Hypa.Runtime.Domain.Parsers;

namespace Hypa.Runtime.Domain.Runner;

public sealed record CompressionResult
{
    public string Text { get; init; } = string.Empty;
    public int OriginalTokens { get; init; }
    public int CompressedTokens { get; init; }
    public string ReducerId { get; init; } = string.Empty;
    public IReadOnlyList<string> StagesApplied { get; init; } = [];
    public bool WasTruncated { get; init; }
    public ParseTier Tier { get; init; } = ParseTier.Passthrough;
    public string? FilterId { get; init; }

    public static CompressionResult From(
        string text,
        int originalTokens,
        int compressedTokens,
        string reducerId,
        IReadOnlyList<string> stagesApplied,
        bool wasTruncated) =>
        new()
        {
            Text = text,
            OriginalTokens = originalTokens,
            CompressedTokens = compressedTokens,
            ReducerId = reducerId,
            StagesApplied = stagesApplied,
            WasTruncated = wasTruncated,
        };

    public static CompressionResult Passthrough(string text, int tokens) =>
        new()
        {
            Text = text,
            OriginalTokens = tokens,
            CompressedTokens = tokens,
            ReducerId = "passthrough",
            StagesApplied = [],
            WasTruncated = false,
        };
}
