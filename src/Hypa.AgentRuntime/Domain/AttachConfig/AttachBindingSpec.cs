namespace Hypa.AgentRuntime.Domain.AttachConfig;

/// <summary>One or more raw chord specs. Comma is a key name, not a delimiter.</summary>
public sealed record AttachBindingSpec : IEquatable<AttachBindingSpec>
{
    public static AttachBindingSpec Unset { get; } = new(["unset"]);

    public IReadOnlyList<string> Specs { get; }

    private AttachBindingSpec(IReadOnlyList<string> specs) => Specs = specs;

    public bool IsUnset =>
        Specs.Count == 0
        || (Specs.Count == 1 && Specs[0].Equals("unset", StringComparison.OrdinalIgnoreCase));

    public string Primary => Specs.Count == 0 ? "unset" : Specs[0];

    public static AttachBindingSpec From(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return Unset;
        var trimmed = spec.Trim();
        return trimmed.Equals("unset", StringComparison.OrdinalIgnoreCase)
            ? Unset
            : new([trimmed]);
    }

    public static AttachBindingSpec From(IReadOnlyList<string> specs)
    {
        if (specs is null || specs.Count == 0)
            return Unset;

        var list = new List<string>(specs.Count);
        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec))
                continue;
            var trimmed = spec.Trim();
            if (trimmed.Equals("unset", StringComparison.OrdinalIgnoreCase))
                continue;
            list.Add(trimmed);
        }

        return list.Count == 0 ? Unset : new(list);
    }

    public static implicit operator AttachBindingSpec(string spec) => From(spec);

    public bool Equals(AttachBindingSpec? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;
        if (Specs.Count != other.Specs.Count)
            return false;
        for (var i = 0; i < Specs.Count; i++)
        {
            if (!string.Equals(Specs[i], other.Specs[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var spec in Specs)
            hash.Add(spec, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public override string ToString() => IsUnset ? "unset" : string.Join(" | ", Specs);
}
