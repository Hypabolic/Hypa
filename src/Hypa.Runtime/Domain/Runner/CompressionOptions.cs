namespace Hypa.Runtime.Domain.Runner;

public sealed record CompressionOptions
{
    public int MaxHeadLines { get; init; } = 80;
    public int MaxTailLines { get; init; } = 80;
    public int MaxTotalLines { get; init; } = 500;
    public bool TeeOnFailure { get; init; } = false;
    public bool TeeOnTruncation { get; init; } = false;
    public int SmallOutputThreshold { get; init; } = 50;

    public static CompressionOptions Default { get; } = new();
}
