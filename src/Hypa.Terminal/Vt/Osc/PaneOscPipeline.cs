using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.Terminal.Vt.Osc;

/// <summary>
/// The OSC handlers of one pane and the one tokenizer that feeds them.
/// A new OSC sequence is one handler type and one registration here.
/// </summary>
public sealed class PaneOscPipeline
{
    private readonly OscSequenceTokenizer _tokenizer;
    private readonly OscOutput _output = new();

    public PaneOscPipeline()
    {
        var registry = new OscHandlerRegistry.Builder();
        Agent.RegisterWith(registry);
        DefaultColors.RegisterWith(registry);
        ProgramStatus.RegisterWith(registry);
        ShellPrompt.RegisterWith(registry);
        ConEmuProgress.RegisterWith(registry);
        _tokenizer = new OscSequenceTokenizer(registry.Build());
    }

    public AgentOscStateTracker Agent { get; } = new();

    public DefaultColorOscTracker DefaultColors { get; } = new();

    public ProgramStatusOscScanner ProgramStatus { get; } = new();

    public ShellPromptOscHandler ShellPrompt { get; } = new();

    public ConEmuProgressOscHandler ConEmuProgress { get; } = new();

    /// <summary>
    /// True when the last chunk ended on the ESC of the ESC backslash
    /// terminator of an OSC 10 or OSC 11 query. The default colour handler
    /// sees that query only when the backslash arrives in the next chunk.
    /// </summary>
    public bool TryGetDefaultColorQueryAwaitingTerminator(
        out HostDefaultColorKind channel)
    {
        channel = default;
        if (!_tokenizer.TryGetStringAwaitingTerminator(out var number, out var body)
            || number is not (10 or 11))
        {
            return false;
        }

        var events = DefaultColorOscTracker.Parse(body);
        if (events.Count != 1 || events[0].Kind is not DefaultColorOscKind.Query)
            return false;
        channel = events[0].Channel;
        return true;
    }

    /// <summary>
    /// Read one chunk of child output and return the results in stream order.
    /// The list is valid until the next call.
    /// </summary>
    public IReadOnlyList<OscEffect> Observe(ReadOnlySpan<byte> bytes)
    {
        _output.Clear();
        _tokenizer.Observe(bytes, _output);
        return _output.Effects;
    }
}
