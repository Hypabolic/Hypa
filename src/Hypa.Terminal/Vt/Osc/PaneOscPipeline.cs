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
