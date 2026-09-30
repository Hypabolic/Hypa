namespace Hypa.Annotate.Application;

/// <summary>
// / Best-effort OSC 52 emit to a terminal.
/// <c>pane_clipboard.rs:52-59</c>. Redirected stdout is not a terminal.
/// </summary>
public sealed class ConsoleOsc52Emitter : IOsc52Emitter
{
    private readonly TextWriter _output;

    public ConsoleOsc52Emitter()
        : this(Console.Out)
    {
    }

    public ConsoleOsc52Emitter(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    public bool TryEmit(string sequence)
    {
        if (ReferenceEquals(_output, Console.Out) && Console.IsOutputRedirected)
            return false;

        try
        {
            _output.Write(sequence);
            _output.Flush();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
