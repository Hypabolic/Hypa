using System.Globalization;
using System.Text;

namespace Hypa.AgentRuntime.Domain.AttachConfig;

public static class StrftimeClock
{
    public const string DefaultPattern = "%H:%M";

    public static bool IsValidFormat(string format) =>
        TryFormat(DateTime.UnixEpoch, format, requireSpecifier: true, out _);

    public static string Format(DateTime local, string? format)
    {
        if (string.IsNullOrEmpty(format)
            || !TryFormat(local, format, requireSpecifier: true, out var text))
        {
            return FormatDefault(local);
        }

        return text;
    }

    public static string FormatDefault(DateTime local) =>
        local.ToString("HH:mm", CultureInfo.InvariantCulture);

    internal static bool TryFormat(
        DateTime local,
        string format,
        bool requireSpecifier,
        out string text)
    {
        var sb = new StringBuilder(format.Length + 8);
        var sawSpecifier = false;
        for (var i = 0; i < format.Length; i++)
        {
            var ch = format[i];
            if (ch != '%')
            {
                sb.Append(ch);
                continue;
            }

            if (i + 1 >= format.Length)
            {
                text = "";
                return false;
            }

            var spec = format[++i];
            if (spec == '%')
            {
                sb.Append('%');
                continue;
            }

            if (!TrySpecifier(local, spec, out var piece))
            {
                text = "";
                return false;
            }

            sawSpecifier = true;
            sb.Append(piece);
        }

        if (requireSpecifier && !sawSpecifier)
        {
            text = "";
            return false;
        }

        text = sb.ToString();
        return true;
    }

    private static bool TrySpecifier(DateTime local, char spec, out string text)
    {
        text = spec switch
        {
            'H' => local.ToString("HH", CultureInfo.InvariantCulture),
            'I' => local.ToString("hh", CultureInfo.InvariantCulture),
            'M' => local.ToString("mm", CultureInfo.InvariantCulture),
            'S' => local.ToString("ss", CultureInfo.InvariantCulture),
            'Y' => local.ToString("yyyy", CultureInfo.InvariantCulture),
            'y' => local.ToString("yy", CultureInfo.InvariantCulture),
            'm' => local.ToString("MM", CultureInfo.InvariantCulture),
            'd' => local.ToString("dd", CultureInfo.InvariantCulture),
            'e' => $"{local.Day,2}",
            'p' => local.ToString("tt", CultureInfo.InvariantCulture),
            'P' => local.ToString("tt", CultureInfo.InvariantCulture).ToLowerInvariant(),
            'a' => local.ToString("ddd", CultureInfo.InvariantCulture),
            'A' => local.ToString("dddd", CultureInfo.InvariantCulture),
            'b' => local.ToString("MMM", CultureInfo.InvariantCulture),
            'B' => local.ToString("MMMM", CultureInfo.InvariantCulture),
            'F' => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            'T' => local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            'R' => local.ToString("HH:mm", CultureInfo.InvariantCulture),
            _ => "",
        };
        return text.Length > 0 || spec is 'P' or 'p';
    }
}
