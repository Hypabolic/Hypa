using System.Globalization;
using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.ProgramStatus;

namespace Hypa.Cli.Attach;

/// <summary>
/// Pure encoder for the program status report that the attach sends to its host terminal
/// (OSC 7501). It only formats. The mux decides the summary.
/// </summary>
public static class HostStatusReport
{
    /// <summary>The <c>app</c> value of the report. The host terminal runs Hypa.</summary>
    public const string App = "hypa";

    /// <summary>Feature detection query. The host replies with the same body.</summary>
    public const string Query = "\u001b]7501;?\u001b\\";

    /// <summary>Removes every record of the host terminal.</summary>
    public const string Clear = "\u001b]7501;state=clear\u001b\\";

    /// <summary>The spec limit for a decoded <c>msg</c>.</summary>
    public const int MaxDecodedMsgBytes = 2048;

    /// <summary>The report for a summary. A null summary gives <see cref="Clear"/>.</summary>
    public static string Encode(SessionProgramStatusView? summary)
    {
        if (summary is null)
            return Clear;

        var body = new StringBuilder("state=", 96);
        body.Append(summary.State.ToWire());
        if (summary.State is ProgramStatusState.Blocked && summary.Kind is { } kind)
            body.Append(":kind=").Append(kind.ToWire());
        if (summary.State is ProgramStatusState.Working && summary.Progress is { } progress)
            body.Append(":progress=").Append(Math.Clamp(progress, 0, 100).ToString(CultureInfo.InvariantCulture));
        body.Append(":app=").Append(App);
        if (EncodeMsg(summary.Msg) is { Length: > 0 } msg)
            body.Append(":msg=").Append(msg);

        return "\u001b]7501;" + body + "\u001b\\";
    }

    /// <summary>
    /// True when <paramref name="sequence"/> (OSC through the terminator) is the host reply
    /// to the detection query. Text after <c>?</c> is ignored, as the spec says.
    /// </summary>
    public static bool IsDetectionReply(ReadOnlySpan<byte> sequence)
    {
        ReadOnlySpan<byte> prefix = "\u001b]7501;?"u8;
        if (sequence.Length < prefix.Length + 1 || !sequence.StartsWith(prefix))
            return false;

        var rest = sequence[prefix.Length..];
        return rest[0] is 0x07 or 0x1b or (byte)';' or (byte)':';
    }

    // Base64 of UTF-8. The text has no control character, and fits the decoded limit.
    private static string? EncodeMsg(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var clean = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsControl(rune))
                clean.Append(rune.ToString());
        }

        var bytes = Encoding.UTF8.GetBytes(clean.ToString());
        if (bytes.Length > MaxDecodedMsgBytes)
        {
            var length = MaxDecodedMsgBytes;
            while (length > 0 && (bytes[length] & 0xC0) == 0x80)
                length--;
            bytes = bytes[..length];
        }

        return bytes.Length == 0 ? null : Convert.ToBase64String(bytes);
    }
}

/// <summary>
/// The setting and the detection for one attach. It decides if the attach may report,
/// and what to report. It holds no I/O: the host writer sends the result.
/// </summary>
public sealed class HostStatusReporter
{
    private bool _querySent;
    private bool _hostAnswered;

    public bool HostAnswered => _hostAnswered;

    /// <summary>
    /// The detection query, once for each attach, and only in <see cref="HostStatusMode.Auto"/>.
    /// Returns null when no query is due.
    /// </summary>
    public string? TakeQuery(HostStatusMode mode)
    {
        if (mode is not HostStatusMode.Auto || _querySent)
            return null;

        _querySent = true;
        return HostStatusReport.Query;
    }

    /// <summary>The host answered the detection query.</summary>
    public void NoteHostReply() => _hostAnswered = true;

    /// <summary>
    /// The sequence to send for <paramref name="summary"/>. A null summary gives <see cref="HostStatusReport.Clear"/>.
    /// When the attach may not report (<c>off</c>, or <c>auto</c> with no host reply), the result is
    /// <see cref="HostStatusReport.Clear"/> too. A setting that changes to <c>off</c> at run time
    /// then removes the report that is outstanding. The host writer drops a clear when no report is outstanding.
    /// </summary>
    public string Sequence(HostStatusMode mode, SessionProgramStatusView? summary) =>
        mode switch
        {
            HostStatusMode.On => HostStatusReport.Encode(summary),
            HostStatusMode.Auto when _hostAnswered => HostStatusReport.Encode(summary),
            _ => HostStatusReport.Clear,
        };
}
