using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

public sealed record DetectionResult
{
    public AgentStatus Status { get; init; } = AgentStatus.Unknown;
    public string? AgentKind { get; init; }
    public string? Message { get; init; }
    public double Confidence { get; init; }

    /// <summary>
    /// Overlay matched. Control plane must keep the previous <see cref="AgentStatus"/>.
    /// </summary>
    public bool SkipStateUpdate { get; init; }

    /// <summary>Bundled, remote, or local override.</summary>
    public string? ManifestSourceKind { get; init; }

    /// <summary>Human-readable source label (bundled, remote path, or override path).</summary>
    public string? ManifestSource { get; init; }

    public string? ManifestVersion { get; init; }
    public string? MatchedRuleId { get; init; }
    public string? Warning { get; init; }
    public string? FallbackReason { get; init; }

    /// <summary>
    /// Latch <c>foreground_shell_exit_reported</c> only after this result
    /// is applied to the pane.
    /// </summary>
    public bool ProcessExited { get; init; }
}

/// <summary>
/// Pure detection: reads a snapshot, returns a result. Never mutates VT/parser state.
/// </summary>
public interface IAgentDetector
{
    DetectionResult Detect(string snapshotText, string? processName = null);

    /// <summary>
    /// Default ignores OSC so test doubles keep the two-argument Detect.
    /// </summary>
    DetectionResult Detect(
        string snapshotText,
        string? processName,
        string oscTitle,
        string oscProgress) =>
        Detect(snapshotText, processName);
}
