namespace Hypa.AgentRuntime.Application;

/// <summary>Input to <see cref="IAgentPresentationCompressor"/>.</summary>
public sealed record PresentationRequest
{
    public required string PaneId { get; init; }
    public required string RawText { get; init; }
    public string? CommandHint { get; init; }
    public int MaxLines { get; init; } = 200;
    public int MaxCompressedBytes { get; init; } = 32 * 1024;
    public int RawCapBytes { get; init; } = 64 * 1024;
    public int TimeoutMs { get; init; } = 250;
}

/// <summary>Result of agent presentation compression.</summary>
public sealed record PresentationResult
{
    public required string Text { get; init; }
    public bool Truncated { get; init; }
    public string CompressorId { get; init; } = "default";
    public long ElapsedMs { get; init; }
    public bool UsedFallback { get; init; }
}
