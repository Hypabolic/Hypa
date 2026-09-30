namespace Hypa.Annotate.Application;

/// <summary>
// / OSC 52 emit port.
/// </summary>
public interface IOsc52Emitter
{
    bool TryEmit(string sequence);
}
