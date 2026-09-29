namespace Hypa.Runtime.Application;

public static class CurrentDirectory
{
    /// <summary>
    /// Returns the process working directory, or <c>null</c> when it is unavailable
    /// (for example, when the directory was deleted after the process entered it).
    /// </summary>
    public static string? TryGet()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch (IOException)
        {
            return null;
        }
    }
}
