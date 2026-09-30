using System.Text.Json;

namespace Hypa.Cli.Attach.Mouse;

/// <summary>
/// Tracking token plus observed encoding flags. Snapshot writes the token only.
/// Live DECSET 1006/1005/1015/1016 set encoding. Unobserved encoding is SGR.
/// </summary>
internal sealed class PaneMouseObservation
{
    public string Mode { get; set; } = PaneMouseMode.None;

    public bool EncodingObserved { get; set; }

    public bool SgrPixels { get; set; }

    public bool Sgr { get; set; }

    public bool Urxvt { get; set; }

    public bool Utf8 { get; set; }

    public MouseProtocolEncoding Encoding
    {
        get
        {
            if (!EncodingObserved)
                return MouseProtocolEncoding.Sgr;
            if (SgrPixels)
                return MouseProtocolEncoding.SgrPixels;
            if (Sgr)
                return MouseProtocolEncoding.Sgr;
            if (Urxvt)
                return MouseProtocolEncoding.Urxvt;
            if (Utf8)
                return MouseProtocolEncoding.Utf8;
            return MouseProtocolEncoding.Default;
        }
    }
}

/// <summary>
/// Ghostty VT mouse tokens: <c>any</c>, <c>button</c>, <c>normal</c>, <c>tracking</c>.
/// Live DEC private modes 1003/1002/1000/9 update the same tokens.
/// </summary>
internal static class PaneMouseMode
{
    public const string None = "none";
    public const string Any = "any";
    public const string Button = "button";
    public const string Normal = "normal";
    public const string Tracking = "tracking";

    public const string EncodingSgr = "sgr";
    public const string EncodingUtf8 = "utf8";
    public const string EncodingUrxvt = "urxvt";
    public const string EncodingSgrPixels = "sgr_pixels";
    public const string EncodingDefault = "default";

    public static bool TryRead(JsonElement json, out string mode)
    {
        mode = None;
        if (json.ValueKind != JsonValueKind.Object)
            return false;
        if (!json.TryGetProperty("modes", out var modes) || modes.ValueKind != JsonValueKind.Object)
            return false;
        if (!modes.TryGetProperty("mouse", out var mouse) || mouse.ValueKind != JsonValueKind.String)
            return false;
        mode = mouse.GetString() ?? None;
        return true;
    }

    public static bool TryApplyEncodingToken(PaneMouseObservation obs, string? token)
    {
        ArgumentNullException.ThrowIfNull(obs);
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var sgrPixels = false;
        var sgr = false;
        var urxvt = false;
        var utf8 = false;
        if (token.Equals(EncodingSgrPixels, StringComparison.OrdinalIgnoreCase)
            || token.Equals("sgr-pixels", StringComparison.OrdinalIgnoreCase))
            sgrPixels = true;
        else if (token.Equals(EncodingSgr, StringComparison.OrdinalIgnoreCase))
            sgr = true;
        else if (token.Equals(EncodingUrxvt, StringComparison.OrdinalIgnoreCase))
            urxvt = true;
        else if (token.Equals(EncodingUtf8, StringComparison.OrdinalIgnoreCase))
            utf8 = true;
        else if (!token.Equals(EncodingDefault, StringComparison.OrdinalIgnoreCase)
            && !token.Equals("x10", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        obs.SgrPixels = sgrPixels;
        obs.Sgr = sgr;
        obs.Urxvt = urxvt;
        obs.Utf8 = utf8;
        obs.EncodingObserved = true;
        return true;
    }

    public static string ApplyLiveBytes(string? current, ReadOnlySpan<byte> bytes)
    {
        var obs = new PaneMouseObservation { Mode = current ?? None };
        ApplyLiveBytes(obs, bytes);
        return obs.Mode;
    }

    public static void ApplyLiveBytes(PaneMouseObservation obs, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(obs);
        if (bytes.IsEmpty)
            return;

        var any = false;
        var button = false;
        var normal = false;
        var tracking = false;
        FromToken(obs.Mode, ref any, ref button, ref normal, ref tracking);
        var sgrPixels = obs.SgrPixels;
        var sgr = obs.Sgr;
        var urxvt = obs.Urxvt;
        var utf8 = obs.Utf8;
        var encodingObserved = obs.EncodingObserved;
        var trackingChanged = false;
        var encodingChanged = false;
        var i = 0;
        Span<int> modes = stackalloc int[8];
        while (i < bytes.Length)
        {
            if (bytes[i] != 0x1b
                || i + 2 >= bytes.Length
                || bytes[i + 1] != (byte)'['
                || bytes[i + 2] != (byte)'?')
            {
                i++;
                continue;
            }

            i += 3;
            var count = 0;
            var value = 0;
            var hasValue = false;
            while (i < bytes.Length)
            {
                var b = bytes[i++];
                if (b is >= (byte)'0' and <= (byte)'9')
                {
                    hasValue = true;
                    if (value <= 1_000_000)
                        value = (value * 10) + (b - '0');
                    continue;
                }

                if (b == (byte)';')
                {
                    if (hasValue && count < modes.Length)
                        modes[count++] = value;
                    value = 0;
                    hasValue = false;
                    continue;
                }

                if (hasValue && count < modes.Length)
                    modes[count++] = value;
                if (b is (byte)'h' or (byte)'l')
                {
                    var set = b == (byte)'h';
                    for (var n = 0; n < count; n++)
                    {
                        if (ApplyTrackingMode(modes[n], set, ref any, ref button, ref normal, ref tracking))
                            trackingChanged = true;
                        if (ApplyEncodingMode(
                                modes[n],
                                set,
                                ref sgrPixels,
                                ref sgr,
                                ref urxvt,
                                ref utf8,
                                ref encodingObserved))
                        {
                            encodingChanged = true;
                        }
                    }
                }

                break;
            }
        }

        if (trackingChanged)
            obs.Mode = ToToken(any, button, normal, tracking);
        if (encodingChanged)
        {
            obs.SgrPixels = sgrPixels;
            obs.Sgr = sgr;
            obs.Urxvt = urxvt;
            obs.Utf8 = utf8;
            obs.EncodingObserved = encodingObserved;
        }
    }

    private static void FromToken(
        string? token,
        ref bool any,
        ref bool button,
        ref bool normal,
        ref bool tracking)
    {
        if (string.IsNullOrEmpty(token) || string.Equals(token, None, StringComparison.OrdinalIgnoreCase))
            return;
        if (string.Equals(token, Any, StringComparison.OrdinalIgnoreCase))
            any = true;
        else if (string.Equals(token, Button, StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "sgr", StringComparison.OrdinalIgnoreCase))
            button = true;
        else if (string.Equals(token, Normal, StringComparison.OrdinalIgnoreCase))
            normal = true;
        else if (string.Equals(token, Tracking, StringComparison.OrdinalIgnoreCase))
            tracking = true;
    }

    private static string ToToken(bool any, bool button, bool normal, bool tracking) =>
        any ? Any : button ? Button : normal ? Normal : tracking ? Tracking : None;

    private static bool ApplyTrackingMode(
        int mode,
        bool set,
        ref bool any,
        ref bool button,
        ref bool normal,
        ref bool tracking)
    {
        switch (mode)
        {
            case 1003:
                any = set;
                return true;
            case 1002:
                button = set;
                return true;
            case 1000:
                normal = set;
                return true;
            case 9:
                tracking = set;
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyEncodingMode(
        int mode,
        bool set,
        ref bool sgrPixels,
        ref bool sgr,
        ref bool urxvt,
        ref bool utf8,
        ref bool encodingObserved)
    {
        switch (mode)
        {
            case 1016:
                sgrPixels = set;
                encodingObserved = true;
                return true;
            case 1006:
                sgr = set;
                encodingObserved = true;
                return true;
            case 1015:
                urxvt = set;
                encodingObserved = true;
                return true;
            case 1005:
                utf8 = set;
                encodingObserved = true;
                return true;
            default:
                return false;
        }
    }
}
