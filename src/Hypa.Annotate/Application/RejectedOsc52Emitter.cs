namespace Hypa.Annotate.Application;

/// <summary>
// / OSC 52 that never lands.
/// native clipboard only for the global copy-archive command. Plugin stdout
/// is a captured pipe, not a terminal.
/// </summary>
public sealed class RejectedOsc52Emitter : IOsc52Emitter
{
    public static RejectedOsc52Emitter Instance { get; } = new();

    public bool TryEmit(string sequence) => false;
}
