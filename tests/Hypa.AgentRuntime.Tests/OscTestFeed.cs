using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Osc;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Test helper. It feeds bytes to one OSC handler through a real <see cref="OscSequenceTokenizer"/>
/// that holds only that handler. The tokenizer keeps its split state between calls.
/// </summary>
internal sealed class OscTestFeed
{
    private readonly OscSequenceTokenizer _tokenizer;
    private readonly OscOutput _output = new();

    public OscTestFeed(Action<OscHandlerRegistry.Builder> register)
    {
        var builder = new OscHandlerRegistry.Builder();
        register(builder);
        _tokenizer = new OscSequenceTokenizer(builder.Build());
    }

    public static OscTestFeed For(AgentOscStateTracker tracker) => new(tracker.RegisterWith);

    public static OscTestFeed For(DefaultColorOscTracker tracker) => new(tracker.RegisterWith);

    public static OscTestFeed For(ProgramStatusOscScanner scanner) => new(scanner.RegisterWith);

    /// <summary>Feed one chunk and return its effects in stream order.</summary>
    public List<OscEffect> Observe(ReadOnlySpan<byte> bytes)
    {
        _output.Clear();
        _tokenizer.Observe(bytes, _output);
        var effects = new List<OscEffect>(_output.Effects);
        _output.Clear();
        return effects;
    }

    /// <summary>Feed one chunk and append its program status signals to <paramref name="signals"/>.</summary>
    public void Observe(ReadOnlySpan<byte> bytes, List<ProgramStatusSignal> signals)
    {
        foreach (var effect in Observe(bytes))
        {
            if (effect is ProgramStatusOscEffect status)
                signals.Add(status.Signal);
        }
    }

    /// <summary>Feed one chunk and return its default colour events.</summary>
    public List<DefaultColorOscEvent> ColorEvents(ReadOnlySpan<byte> bytes) =>
        Observe(bytes).OfType<DefaultColorOscEffect>().Select(e => e.Event).ToList();
}
