using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
// / Pane clipboard write rules.
/// </summary>
public static class PaneClipboard
{
    /// <summary>
    /// Base64 payload size that terminals commonly refuse beyond.
    /// </summary>
    public const int Osc52CommonPayloadLimitBytes = 74_994;

    public static string Osc52ClipboardSequence(string text) =>
        Osc52Clipboard.EncodePayload(text);

    public static bool ExceedsCommonOsc52Limit(string text)
    {
        var payload = Osc52ClipboardSequence(text);
        var base64Length = payload.Length - "\u001b]52;c;".Length - 1;
        return base64Length > Osc52CommonPayloadLimitBytes;
    }

    /// <summary>
    /// Native write first, then OSC 52. Either destination is success.
    /// successful copy. A native miss must not throw or block OSC 52.
    /// </summary>
    public static Result<bool, string> Write(
        string text,
        Func<string, Result<bool, string>> writeClipboard,
        Func<string, bool> emit)
    {
        var native = writeClipboard(text);
        var emitted = emit(Osc52ClipboardSequence(text));
        if (native.IsOk || emitted)
            return Result<bool, string>.Ok(true);

        return Result<bool, string>.Fail(native.Error);
    }

    public static Result<bool, string> Write(
        string text,
        IClipboardWriter clipboardWriter,
        IOsc52Emitter osc52Emitter) =>
        Write(text, clipboardWriter.TryWrite, osc52Emitter.TryEmit);
}
