namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// User private group check. Linuxbrew and other umask 002 installs leave
/// directories group-writable. That grants nothing to another user when the
/// group holds only the owner. Fails closed on any entry it cannot read.
/// </summary>
internal static class UnixPrivateGroup
{
    public const string GroupFile = "/etc/group";
    public const string PasswdFile = "/etc/passwd";

    public static bool IsPrivateTo(uint gid, uint ownerUid) =>
        IsPrivateTo(gid, ownerUid, TryReadLines(GroupFile), TryReadLines(PasswdFile));

    /// <summary>
    /// True when <paramref name="gid"/> is the owner's primary group, no other
    /// account has it as a primary group, and it lists no other member.
    /// </summary>
    public static bool IsPrivateTo(
        uint gid,
        uint ownerUid,
        IEnumerable<string>? groupLines,
        IEnumerable<string>? passwdLines)
    {
        if (groupLines is null || passwdLines is null)
            return false;

        string? ownerName = null;
        foreach (var line in passwdLines)
        {
            var parts = line.Split(':');
            if (parts.Length < 7)
                continue;
            if (!uint.TryParse(parts[2], out var uid) || !uint.TryParse(parts[3], out var primary))
                continue;

            if (uid == ownerUid)
            {
                if (ownerName is not null || primary != gid)
                    return false;
                ownerName = parts[0];
            }
            else if (primary == gid)
            {
                return false;
            }
        }

        if (string.IsNullOrEmpty(ownerName))
            return false;

        var found = false;
        foreach (var line in groupLines)
        {
            var parts = line.Split(':');
            if (parts.Length < 4)
                continue;
            if (!uint.TryParse(parts[2], out var entryGid) || entryGid != gid)
                continue;
            if (found)
                return false;
            found = true;

            foreach (var member in parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.Equals(member, ownerName, StringComparison.Ordinal))
                    return false;
            }
        }

        return found;
    }

    private static string[]? TryReadLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
