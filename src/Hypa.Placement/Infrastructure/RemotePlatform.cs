namespace Hypa.Placement.Infrastructure;

/// <summary>
/// Remote host OS from <c>uname -s</c>.
/// A Windows remote host is refused.
/// </summary>
public sealed record RemotePlatform(string Os, string Arch)
{
    public bool IsUnix =>
        string.Equals(Os, "linux", StringComparison.Ordinal)
        || string.Equals(Os, "macos", StringComparison.Ordinal);

    public static bool TryParse(string? unameS, string? unameM, out RemotePlatform platform)
    {
        platform = null!;
        var os = (unameS ?? "").Trim() switch
        {
            "Linux" => "linux",
            "Darwin" => "macos",
            _ => null,
        };
        if (os is null)
            return false;

        var arch = (unameM ?? "").Trim() switch
        {
            "x86_64" or "amd64" => "x86_64",
            "aarch64" or "arm64" => "aarch64",
            _ => null,
        };
        if (arch is null)
            return false;

        platform = new RemotePlatform(os, arch);
        return true;
    }

    public static bool LooksLikeWindows(string? unameS)
    {
        var os = (unameS ?? "").Trim();
        return os.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)
            || os.Equals("MINGW64_NT", StringComparison.OrdinalIgnoreCase)
            || os.Contains("CYGWIN", StringComparison.OrdinalIgnoreCase)
            || os.Contains("MSYS", StringComparison.OrdinalIgnoreCase);
    }
}
