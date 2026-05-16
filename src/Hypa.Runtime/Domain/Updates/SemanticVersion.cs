namespace Hypa.Runtime.Domain.Updates;

public readonly struct SemanticVersion : IComparable<SemanticVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }

    private SemanticVersion(int major, int minor, int patch, string? preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public static bool TryParse(string? input, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var s = input.TrimStart('v');

        // Strip build metadata
        var plusIdx = s.IndexOf('+');
        if (plusIdx >= 0)
            s = s[..plusIdx];

        string? preRelease = null;
        var dashIdx = s.IndexOf('-');
        if (dashIdx >= 0)
        {
            preRelease = s[(dashIdx + 1)..];
            s = s[..dashIdx];
        }

        var parts = s.Split('.');
        if (parts.Length < 2 || parts.Length > 3)
            return false;

        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor))
            return false;

        var patch = 0;
        if (parts.Length == 3 && !int.TryParse(parts[2], out patch))
            return false;

        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // No pre-release beats pre-release (stable > prerelease)
        if (PreRelease is null && other.PreRelease is not null) return 1;
        if (PreRelease is not null && other.PreRelease is null) return -1;
        if (PreRelease is not null && other.PreRelease is not null)
            return string.Compare(PreRelease, other.PreRelease, StringComparison.Ordinal);

        return 0;
    }

    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
    public static bool operator ==(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) == 0;
    public static bool operator !=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) != 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && CompareTo(other) == 0;
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);
    public override string ToString() =>
        PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
