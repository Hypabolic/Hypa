using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Mandatory middleware on every pane I/O path. Compression + evidence + Atomic binding.
/// Milestone 4 is partial: heuristics and Atomic headers ship now; full
/// IOutputCompressor / EvidenceLedger adapters are a follow-up.
/// </summary>
public interface IIntelligencePipeline
{
    /// <summary>Observe raw PTY bytes (before or after VT feed).</summary>
    void OnPaneOutput(PaneId paneId, ReadOnlySpan<byte> data);

    /// <summary>Observe client/agent input into a pane.</summary>
    void OnPaneInput(PaneId paneId, string text);

    /// <summary>Produce agent-facing compressed text from a pane snapshot.</summary>
    string CompressForAgent(PaneId paneId, string rawText, string? commandHint = null);

    /// <summary>Bind or update Atomic metadata for a pane.</summary>
    void BindAtomic(PaneId paneId, AtomicBinding binding);

    /// <summary>Drop all per-pane intelligence state (close / failed start / shutdown).</summary>
    void RemovePane(PaneId paneId);
}
