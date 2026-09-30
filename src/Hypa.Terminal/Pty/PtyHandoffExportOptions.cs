namespace Hypa.Terminal.Pty;

/// <summary>Options for <see cref="IPtyProcessControl.ExportHandoffAsync"/>.</summary>
public sealed record PtyHandoffExportOptions
{
    public string RuntimeSessionId { get; init; } = string.Empty;
    public string PaneId { get; init; } = string.Empty;
    public int Generation { get; init; }
    /// <summary>16-byte nonce; null generates a cryptographically random nonce.</summary>
    public byte[]? Nonce { get; init; }
    public ushort Cols { get; init; } = 80;
    public ushort Rows { get; init; } = 24;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Options for <see cref="PtyHostProcess.AdoptHandoffAsync"/>.</summary>
public sealed record PtyHandoffAdoptOptions
{
    public required byte[] Nonce { get; init; }
    public int Generation { get; init; }
    /// <summary>
    /// When set, <see cref="PtyHostProcess.ImportHandoffAsync"/> rejects Hello with
    /// <see cref="PtyHandoffStatus.GenerationMismatch"/> if the peer generation differs.
    /// </summary>
    public int? ExpectedGeneration { get; init; }
    public int ChildPid { get; init; }
    public string TargetRuntimeId { get; init; } = string.Empty;
    public string? HelperPath { get; init; }
    public ushort Cols { get; init; } = 80;
    public ushort Rows { get; init; } = 24;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
    public PtyHostOptions? HostOptions { get; init; }
}
