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
    public const string NsswitchFile = "/etc/nsswitch.conf";

    /// <summary>SSSD answers NSS lookups through this socket only while it runs.</summary>
    public const string SssNssSocket = "/var/lib/sss/pipes/nss";

    /// <summary>systemd-nss dynamic users (DynamicUser=) take ids from this range.</summary>
    internal const uint SystemdDynamicFirst = 61184;
    internal const uint SystemdDynamicLast = 65519;

    public static bool IsPrivateTo(uint gid, uint ownerUid) =>
        OnlyLocalAccountSources(gid, TryReadLines(NsswitchFile), File.Exists(SssNssSocket))
        && IsPrivateTo(gid, ownerUid, TryReadLines(GroupFile), TryReadLines(PasswdFile));

    /// <summary>
    /// The files prove nothing when another NSS source can add accounts or
    /// memberships for this gid. Allow <c>files</c>, <c>systemd</c> outside
    /// its dynamic range, and <c>sss</c> only while SSSD is not running.
    /// Any other source (ldap, nis, compat, winbind, ...) fails closed.
    /// A missing nsswitch.conf or database line means glibc's default: files.
    /// </summary>
    public static bool OnlyLocalAccountSources(
        uint gid,
        IEnumerable<string>? nsswitchLines,
        bool sssRunning)
    {
        if (nsswitchLines is null)
            return true;

        foreach (var raw in nsswitchLines)
        {
            var line = raw;
            var comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment];
            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var database = line[..colon].Trim();
            if (database is not ("passwd" or "group"))
                continue;

            var sources = line[(colon + 1)..];
            while (sources.IndexOf('[') is var open and >= 0)
            {
                var close = sources.IndexOf(']', open);
                if (close < 0)
                    return false;
                sources = sources[..open] + " " + sources[(close + 1)..];
            }

            foreach (var source in sources.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (source)
                {
                    case "files":
                        break;
                    case "systemd" when gid is < SystemdDynamicFirst or > SystemdDynamicLast:
                        break;
                    case "sss" when !sssRunning:
                        break;
                    default:
                        return false;
                }
            }
        }

        // A database nsswitch.conf does not list uses glibc's default: files.
        return true;
    }

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
            if (IsCompatEntry(line))
                return false;
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
            if (IsCompatEntry(line))
                return false;
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

    /// <summary>NIS compat entries pull accounts from outside the file.</summary>
    private static bool IsCompatEntry(string line) =>
        line.StartsWith('+') || line.StartsWith('-');

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
