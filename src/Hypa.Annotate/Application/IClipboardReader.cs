namespace Hypa.Annotate.Application;

/// <summary>
// / Read system clipboard text.
/// </summary>
public interface IClipboardReader
{
    /// <summary>
    /// Return clipboard text, or an empty string when no reader succeeds.
    /// </summary>
    string ReadText();
}
