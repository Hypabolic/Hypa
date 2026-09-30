namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// SQLite <c>journal_segments</c> row (design Appendix A). Content lives in the HYJR file.
/// </summary>
public sealed record JournalSegmentManifest
{
    public required string SegmentId { get; init; }
    public required string SessionId { get; init; }
    /// <summary>Path relative to the runtime state root, e.g. <c>journal/seg_0_abc.hyjr</c>.</summary>
    public required string RelativePath { get; init; }
    public required long FirstSeq { get; init; }
    public long? LastSeq { get; init; }
    public int RecordCount { get; init; }
    public long ByteCount { get; init; }
    /// <summary>Lower-hex SHA-256 of the complete closed file (including footer); null while open.</summary>
    public string? ChecksumSha256 { get; init; }
    public bool Closed { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
}
