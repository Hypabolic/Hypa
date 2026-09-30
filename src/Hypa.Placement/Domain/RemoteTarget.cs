using System.Text;

namespace Hypa.Placement.Domain;

/// <summary>
/// OpenSSH destination text. Credentials stay in OpenSSH.
/// <c>src/client/endpoint/catalog.rs:45-68</c>.
/// </summary>
public static class RemoteTarget
{
    public const int MaxUtf8Bytes = 1024;

    public static bool TryValidate(string? target, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        if (target is null)
        {
            error = "missing value for --remote";
            return false;
        }

        if (target.Length == 0)
        {
            error = "missing value for --remote";
            return false;
        }

        if (target.StartsWith('-'))
        {
            error = "--remote target must not start with '-'";
            return false;
        }

        if (ContainsControl(target))
        {
            error = "SSH target must contain no control characters";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(target) > MaxUtf8Bytes)
        {
            error = $"SSH target must be at most {MaxUtf8Bytes} bytes";
            return false;
        }

        if (ContainsPasswordUserinfo(target))
        {
            error = "SSH target must not contain a password";
            return false;
        }

        normalized = target;
        return true;
    }

    public static bool ContainsPasswordUserinfo(string target)
    {
        var authority = target.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
            ? target["ssh://".Length..]
            : target;
        var at = authority.LastIndexOf('@');
        if (at <= 0)
            return false;
        var userinfo = authority[..at];
        return userinfo.Contains(':');
    }

    public static bool ContainsControl(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
                return true;
        }

        return false;
    }
}
