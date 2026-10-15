using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Terminal.Vt.Osc;

namespace Hypa.Terminal.Vt;

/// <summary>One default colour event in the output of a chunk. The event holds the end offset in the chunk.</summary>
public sealed record DefaultColorOscEffect(DefaultColorOscEvent Event) : OscEffect;

/// <summary>
/// Child OSC 10/11/110/111 handler.
/// <c>src/pane/osc.rs</c> <c>DefaultColorEventTracker</c> for those four
/// commands only. Skip OSC 4 and OSC 12.
/// </summary>
public sealed class DefaultColorOscTracker : IOscHandler
{
    private const int MaxBody = 1024;

    // ESC pairs stay in the body. BEL after ESC is body text.
    public static OscStringPolicy Policy { get; } =
        new(MaxBody, OscEscapeInBody.Keep, CancelAborts: false);

    /// <summary>Register this handler for OSC 10, 11, 110, and 111.</summary>
    public void RegisterWith(OscHandlerRegistry.Builder registry)
    {
        registry.Add(10, Policy, this).Add(11, Policy, this).Add(110, Policy, this).Add(111, Policy, this);
    }

    public void Handle(in OscSequence sequence, OscOutput output)
    {
        foreach (var ev in Parse(sequence.Body))
            output.Add(new DefaultColorOscEffect(ev with { EndOffset = sequence.EndOffset }));
    }

    internal static IReadOnlyList<DefaultColorOscEvent> Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
            return [];

        if (IsExact(body, "10;?"u8))
            return [DefaultColorOscEvent.Query(HostDefaultColorKind.Foreground)];
        if (IsExact(body, "11;?"u8))
            return [DefaultColorOscEvent.Query(HostDefaultColorKind.Background)];
        if (IsExact(body, "110"u8) || IsExact(body, "110;"u8))
            return [DefaultColorOscEvent.Reset(HostDefaultColorKind.Foreground)];
        if (IsExact(body, "111"u8) || IsExact(body, "111;"u8))
            return [DefaultColorOscEvent.Reset(HostDefaultColorKind.Background)];

        // commands 10 and 11 only. Skip OSC 12. Split on ';', drop empty
        // values before offset, skip '?', map start+offset onto fg/bg.
        return ParseSetEvents(body);
    }

    private static IReadOnlyList<DefaultColorOscEvent> ParseSetEvents(ReadOnlySpan<byte> body)
    {
        var split = body.IndexOf((byte)';');
        if (split != 2)
            return [];

        int start;
        if (body[0] == (byte)'1' && body[1] == (byte)'0')
            start = 10;
        else if (body[0] == (byte)'1' && body[1] == (byte)'1')
            start = 11;
        else
            return [];

        var events = new List<DefaultColorOscEvent>();
        var offset = 0;
        var i = split + 1;
        while (i <= body.Length)
        {
            var end = i;
            while (end < body.Length && body[end] != (byte)';')
                end++;
            var len = end - i;
            if (len > 0)
            {
                var isQuery = len == 1 && body[i] == (byte)'?';
                if (!isQuery)
                {
                    var command = start + offset;
                    if (command is 10 or 11)
                    {
                        HostRgb? color = null;
                        if (TryParseRgbSlice(body, i, len, out var parsed))
                            color = parsed;
                        events.Add(DefaultColorOscEvent.Set(
                            command == 10
                                ? HostDefaultColorKind.Foreground
                                : HostDefaultColorKind.Background,
                            color));
                    }
                }

                offset++;
            }

            if (end >= body.Length)
                break;
            i = end + 1;
        }

        return events;
    }

    private static bool TryParseRgbSlice(
        ReadOnlySpan<byte> body,
        int start,
        int length,
        out HostRgb color)
    {
        var chars = new char[length];
        for (var n = 0; n < length; n++)
            chars[n] = (char)body[start + n];
        return HostThemeParser.TryParseRgbColor(new string(chars), out color);
    }

    private static bool IsExact(ReadOnlySpan<byte> body, ReadOnlySpan<byte> expected)
    {
        if (body.Length != expected.Length)
            return false;
        for (var i = 0; i < expected.Length; i++)
        {
            if (body[i] != expected[i])
                return false;
        }

        return true;
    }

}

public readonly record struct DefaultColorOscEvent(
    DefaultColorOscKind Kind,
    HostDefaultColorKind Channel,
    HostRgb? Color,
    int EndOffset = 0)
{
    public static DefaultColorOscEvent Query(HostDefaultColorKind channel) =>
        new(DefaultColorOscKind.Query, channel, null);

    public static DefaultColorOscEvent Set(HostDefaultColorKind channel, HostRgb? color) =>
        new(DefaultColorOscKind.Set, channel, color);

    public static DefaultColorOscEvent Reset(HostDefaultColorKind channel) =>
        new(DefaultColorOscKind.Reset, channel, null);
}

public enum DefaultColorOscKind
{
    Query = 0,
    Set = 1,
    Reset = 2,
}
