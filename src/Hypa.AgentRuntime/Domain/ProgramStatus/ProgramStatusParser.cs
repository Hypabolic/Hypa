using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace Hypa.AgentRuntime.Domain.ProgramStatus;

/// <summary>
/// Pure parser and validator for the pairs of an OSC 7501 report.
/// It checks every pair before it returns a report. A rejected report has no effect.
/// </summary>
public static class ProgramStatusParser
{
    public const int MaxSequenceBytes = 4096;
    public const int MaxKeyBytes = 16;
    public const int MaxMsgEncodedBytes = 2732;
    public const int MaxMsgDecodedBytes = 2048;
    public const int MaxTitleEncodedBytes = 256;
    public const int MaxTitleDecodedBytes = 192;
    public const int MaxAppChars = 32;

    // ESC ] 7501 ; takes 7 bytes. The shortest terminator (BEL) takes 1.
    private const int SequencePrefixBytes = 7;

    /// <param name="pairs">The bytes after <c>OSC 7501 ;</c> and before ST.</param>
    /// <param name="terminatorBytes">1 for BEL, 2 for ESC \.</param>
    public static Result<ProgramStatusReport, ProgramStatusRejection> Parse(
        ReadOnlySpan<byte> pairs,
        int terminatorBytes = 2)
    {
        if (SequencePrefixBytes + pairs.Length + terminatorBytes > MaxSequenceBytes)
            return Fail(ProgramStatusRejection.SequenceTooLong);

        string? state = null;
        string? kind = null;
        string? progress = null;
        string? app = null;
        string? title = null;
        string? msg = null;
        string? id = null;
        var idInvalid = false;

        while (true)
        {
            var colon = pairs.IndexOf((byte)':');
            var pair = colon < 0 ? pairs : pairs[..colon];
            pairs = colon < 0 ? default : pairs[(colon + 1)..];

            var rejection = ReadPair(
                pair,
                ref state,
                ref kind,
                ref progress,
                ref app,
                ref title,
                ref msg,
                ref id,
                ref idInvalid);
            if (rejection is { } failed)
                return Fail(failed);

            if (colon < 0)
                break;
        }

        if (idInvalid || (id is not null && !IsValidId(id)))
            return Fail(ProgramStatusRejection.InvalidId);
        if (state is null)
            return Fail(ProgramStatusRejection.MissingState);
        if (!ProgramStatusWire.TryParseState(state, out var parsedState))
            return Fail(ProgramStatusRejection.UnknownState);

        ProgramStatusKind? parsedKind = null;
        if (parsedState is ProgramStatusState.Blocked
            && kind is not null
            && ProgramStatusWire.TryParseKind(kind, out var k))
        {
            parsedKind = k;
        }

        int? parsedProgress = null;
        if (parsedState is ProgramStatusState.Working or ProgramStatusState.Blocked
            && progress is not null
            && IsDigits(progress)
            && progress.Length <= 3
            && int.Parse(progress, System.Globalization.CultureInfo.InvariantCulture) is var p
            && p <= 100)
        {
            parsedProgress = p;
        }

        return Result<ProgramStatusReport, ProgramStatusRejection>.Ok(new ProgramStatusReport
        {
            Id = id,
            State = parsedState,
            Kind = parsedKind,
            Progress = parsedProgress,
            App = app is not null && IsValidApp(app) ? app : null,
            Title = NullIfEmpty(title),
            Msg = NullIfEmpty(msg),
        });
    }

    private static ProgramStatusRejection? ReadPair(
        ReadOnlySpan<byte> pair,
        ref string? state,
        ref string? kind,
        ref string? progress,
        ref string? app,
        ref string? title,
        ref string? msg,
        ref string? id,
        ref bool idInvalid)
    {
        var eq = pair.IndexOf((byte)'=');
        if (eq < 0)
            return null;

        var key = Trim(pair[..eq]);
        if (key.Length > MaxKeyBytes)
            return ProgramStatusRejection.KeyTooLong;

        var value = Trim(pair[(eq + 1)..]);
        if (key.IsEmpty || !IsKey(key))
            return null;

        var keyText = Encoding.ASCII.GetString(key);

        // Check the limits of every msg and title pair, even when a later pair replaces it.
        if (keyText == "msg" && value.Length > MaxMsgEncodedBytes)
            return ProgramStatusRejection.MsgTooLong;
        if (keyText == "title" && value.Length > MaxTitleEncodedBytes)
            return ProgramStatusRejection.TitleTooLong;

        if (!IsValue(value))
        {
            // A broken id must not fall back to the root record.
            if (keyText == "id")
                idInvalid = true;
            return null;
        }

        var text = Encoding.ASCII.GetString(value);
        switch (keyText)
        {
            case "state":
                state = text;
                break;
            case "kind":
                kind = text;
                break;
            case "progress":
                progress = text;
                break;
            case "app":
                app = text;
                break;
            case "id":
                id = text;
                break;
            case "msg":
                var msgCheck = Decode(text, MaxMsgDecodedBytes, ProgramStatusRejection.MsgTooLong);
                if (!msgCheck.IsOk)
                    return msgCheck.Error;
                msg = msgCheck.Value;
                break;
            case "title":
                var titleCheck = Decode(text, MaxTitleDecodedBytes, ProgramStatusRejection.TitleTooLong);
                if (!titleCheck.IsOk)
                    return titleCheck.Error;
                title = titleCheck.Value;
                break;
        }

        return null;
    }

    // ReadPair already decoded msg and title. An empty text means no text.
    private static string? NullIfEmpty(string? decoded) =>
        string.IsNullOrEmpty(decoded) ? null : decoded;

    private static Result<string, ProgramStatusRejection> Decode(
        string encoded,
        int maxDecodedBytes,
        ProgramStatusRejection tooLong)
    {
        if (encoded.Length == 0)
            return Result<string, ProgramStatusRejection>.Ok("");

        var trimmed = encoded.TrimEnd('=');
        if (encoded.Length - trimmed.Length > 2 || trimmed.Length % 4 == 1)
            return Result<string, ProgramStatusRejection>.Fail(ProgramStatusRejection.InvalidBase64);

        var padded = trimmed.Length % 4 == 0 ? trimmed : trimmed + new string('=', 4 - trimmed.Length % 4);
        var buffer = new byte[padded.Length / 4 * 3];
        if (!Convert.TryFromBase64String(padded, buffer, out var written))
            return Result<string, ProgramStatusRejection>.Fail(ProgramStatusRejection.InvalidBase64);
        if (written > maxDecodedBytes)
            return Result<string, ProgramStatusRejection>.Fail(tooLong);

        var chars = new char[written];
        var status = Utf8.ToUtf16(buffer.AsSpan(0, written), chars, out _, out var charsWritten, false, true);
        if (status != OperationStatus.Done)
            return Result<string, ProgramStatusRejection>.Fail(ProgramStatusRejection.InvalidText);

        for (var i = 0; i < charsWritten; i++)
        {
            var c = chars[i];
            if (c <= 0x1f || (c >= 0x7f && c <= 0x9f))
                return Result<string, ProgramStatusRejection>.Fail(ProgramStatusRejection.ControlCharacter);
        }

        return Result<string, ProgramStatusRejection>.Ok(new string(chars, 0, charsWritten));
    }

    private static Result<ProgramStatusReport, ProgramStatusRejection> Fail(ProgramStatusRejection rejection) =>
        Result<ProgramStatusReport, ProgramStatusRejection>.Fail(rejection);

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> span)
    {
        while (!span.IsEmpty && span[0] is (byte)' ' or (byte)'\t')
            span = span[1..];
        while (!span.IsEmpty && span[^1] is (byte)' ' or (byte)'\t')
            span = span[..^1];
        return span;
    }

    private static bool IsKey(ReadOnlySpan<byte> key)
    {
        foreach (var b in key)
        {
            if (b is < (byte)'a' or > (byte)'z')
                return false;
        }

        return true;
    }

    private static bool IsValue(ReadOnlySpan<byte> value)
    {
        foreach (var b in value)
        {
            var ok = b is >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9'
                or (byte)'_' or (byte)'.' or (byte)',' or (byte)'+' or (byte)'/' or (byte)'=' or (byte)'-';
            if (!ok)
                return false;
        }

        return true;
    }

    private static bool IsDigits(string text)
    {
        if (text.Length == 0)
            return false;
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
                return false;
        }

        return true;
    }

    public const int MaxIdBytes = 128;
    public const int MaxIdLevels = 8;

    /// <summary>
    /// True when <paramref name="id"/> matches <c>segment ("/" segment)*</c>.
    /// A segment is 1 to 32 characters of <c>[A-Za-z0-9_.+-]</c>. The id has at most 128 bytes and 8 levels.
    /// </summary>
    public static bool IsValidId(string id)
    {
        if (id.Length is 0 or > MaxIdBytes)
            return false;
        var levels = 0;
        foreach (var segment in id.Split('/'))
        {
            levels++;
            if (!IsValidApp(segment))
                return false;
        }

        return levels <= MaxIdLevels;
    }

    /// <summary>True when <paramref name="text"/> is a valid <c>app</c> value or id segment.</summary>
    public static bool IsValidApp(string text)
    {
        if (text.Length is < 1 or > MaxAppChars)
            return false;
        foreach (var c in text)
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
                or '_' or '.' or '+' or '-';
            if (!ok)
                return false;
        }

        return true;
    }
}
