using System.Collections.Concurrent;
using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hypa.AgentIntelligence;

/// <summary>
/// Intelligence middleware on pane I/O.
/// Ships: presentation compressor (ANSI strip, blank collapse, light dedupe), Atomic headers.
/// Does not own VT state; does not replace Hypa.Runtime IOutputCompressor.
/// </summary>
public sealed class PaneIntelligencePipeline : IIntelligencePipeline
{
    private readonly ConcurrentDictionary<string, AtomicBinding> _bindings = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private readonly IAgentPresentationCompressor _compressor;
    private readonly int _maxCompressedChars;

    public PaneIntelligencePipeline(
        ILogger? logger = null,
        int maxCompressedChars = 12_000,
        IAgentPresentationCompressor? compressor = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _maxCompressedChars = maxCompressedChars;
        _compressor = compressor ?? new DefaultAgentPresentationCompressor();
    }

    public void OnPaneOutput(PaneId paneId, ReadOnlySpan<byte> data)
    {
        // Observe only. Full reducers attach here later.
        _logger.LogTrace("Pane {PaneId} output {Bytes} bytes", paneId, data.Length);
    }

    public void OnPaneInput(PaneId paneId, string text)
    {
        // Observe only. Do not retain unredacted keystrokes.
        _logger.LogTrace("Pane {PaneId} input {Chars} chars", paneId, text.Length);
    }

    public string CompressForAgent(PaneId paneId, string rawText, string? commandHint = null)
    {
        if (string.IsNullOrEmpty(rawText))
            return string.Empty;

        var result = _compressor.Compress(new PresentationRequest
        {
            PaneId = paneId.Value,
            RawText = rawText,
            CommandHint = commandHint,
            MaxCompressedBytes = Math.Min(
                _maxCompressedChars * 4,
                DefaultAgentPresentationCompressor.DefaultMaxCompressedBytes),
            MaxLines = DefaultAgentPresentationCompressor.DefaultMaxLines,
        });
        var text = result.Text;

        if (_bindings.TryGetValue(paneId.Value, out var binding))
        {
            var header = new StringBuilder();
            header.AppendLine($"# hypa pane={paneId.Value}");
            if (binding.RunId is not null)
                header.AppendLine($"# atomic.run={binding.RunId}");
            if (binding.StepId is not null)
                header.AppendLine($"# atomic.step={binding.StepId}");
            if (binding.AgentSessionId is not null)
                header.AppendLine($"# atomic.agent_session={binding.AgentSessionId}");
            if (!string.IsNullOrEmpty(commandHint))
                header.AppendLine($"# command={commandHint}");
            header.AppendLine();
            text = header + text;
        }

        return text;
    }

    /// <summary>Presentation compressor used by this pipeline (test helper).</summary>
    public IAgentPresentationCompressor Compressor => _compressor;

    public void BindAtomic(PaneId paneId, AtomicBinding binding)
    {
        _bindings[paneId.Value] = binding;
        _logger.LogDebug(
            "Bound {PaneId} run={Run} step={Step} session={Session}",
            paneId, binding.RunId, binding.StepId, binding.AgentSessionId);
    }

    public void RemovePane(PaneId paneId)
    {
        _bindings.TryRemove(paneId.Value, out _);
    }

    public AtomicBinding? GetBinding(PaneId paneId) =>
        _bindings.TryGetValue(paneId.Value, out var b) ? b : null;

    /// <summary>Test helper: whether any state remains for a pane.</summary>
    public bool HasPaneState(PaneId paneId) =>
        _bindings.ContainsKey(paneId.Value);
}
