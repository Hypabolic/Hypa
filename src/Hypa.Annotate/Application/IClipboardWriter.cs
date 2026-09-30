using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
// / Native clipboard write port.
/// </summary>
public interface IClipboardWriter
{
    Result<bool, string> TryWrite(string text);
}
