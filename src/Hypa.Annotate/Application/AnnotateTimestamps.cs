namespace Hypa.Annotate.Application;

internal static class AnnotateTimestamps
{
    public static string NowIso() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK");
}
