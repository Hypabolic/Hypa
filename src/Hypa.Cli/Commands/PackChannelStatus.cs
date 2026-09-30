namespace Hypa.Cli.Commands;

/// <summary>
/// Report the installed pack channel. F1 packs omit <c>hypa.channel</c>.
/// F2 packs write token <c>f2</c>. This is not an updater.
/// </summary>
internal static class PackChannelStatus
{
    public const string MarkerFileName = "hypa.channel";
    public const string F1 = "f1";
    public const string F2 = "f2";

    public static string Report()
    {
        var marker = ReadMarker();
        return string.Equals(marker, F2, StringComparison.Ordinal) ? F2 : F1;
    }

    internal static string? ReadMarker()
    {
        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir))
            return null;

        var path = Path.Combine(Path.GetFullPath(baseDir), MarkerFileName);
        if (!File.Exists(path))
            return null;

        return File.ReadAllText(path).Trim();
    }
}
