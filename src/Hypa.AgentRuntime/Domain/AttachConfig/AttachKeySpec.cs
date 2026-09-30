namespace Hypa.AgentRuntime.Domain.AttachConfig;

/// <summary>Chord grammar aligned with Cli <c>KeyChord</c>. Domain has no Cli dependency.</summary>
public static class AttachKeySpec
{
    public static string NormalizeIndexed(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "unset";

        var trimmed = raw.Trim();
        if (trimmed.Equals("unset", StringComparison.OrdinalIgnoreCase))
            return "unset";
        if (trimmed.Contains("1..9", StringComparison.Ordinal))
            return trimmed;
        if (LooksLikeModifierOnly(trimmed))
            return trimmed.TrimEnd('+') + "+1..9";
        return trimmed;
    }

    public static bool TryValidateBinding(AttachBindingSpec spec, bool prefixSlot, out string? error)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.IsUnset)
        {
            error = prefixSlot ? "required" : null;
            return !prefixSlot;
        }

        foreach (var chord in spec.Specs)
        {
            if (!TryValidateBinding(chord, prefixSlot, out error))
                return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateBinding(string spec, bool prefixSlot, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(spec) || spec.Equals("unset", StringComparison.OrdinalIgnoreCase))
        {
            if (prefixSlot)
            {
                error = "required";
                return false;
            }

            return true;
        }

        if (!TryExpand(spec, out var expanded, out error))
            return false;

        foreach (var chord in expanded)
        {
            if (!TryParseChord(chord, out var hasPrefix, out error))
                return false;
            if (prefixSlot && hasPrefix)
            {
                error = "keys.prefix cannot include the prefix modifier";
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<string> Expand(string spec) =>
        TryExpand(spec, out var expanded, out _) ? expanded : [];

    public static bool TryExpand(string spec, out IReadOnlyList<string> expanded, out string? error)
    {
        expanded = [];
        error = null;
        if (string.IsNullOrWhiteSpace(spec) || spec.Equals("unset", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!TryFindDigitRange(spec, out var head, out var tail, out var found, out error))
            return false;

        if (!found)
        {
            expanded = [spec];
            return true;
        }

        var list = new List<string>(9);
        for (var i = 1; i <= 9; i++)
            list.Add(head + i + tail);
        expanded = list;
        return true;
    }

    public static bool IsCompleteDigitRange(string spec) =>
        TryFindDigitRange(spec, out _, out _, out var found, out var error) && error is null && found;

    public static bool TryParseChord(string spec, out bool hasPrefix, out string? error)
    {
        hasPrefix = false;
        error = null;
        if (string.IsNullOrWhiteSpace(spec))
        {
            error = "empty_chord";
            return false;
        }

        var trimmed = spec.Trim();
        if (trimmed.Equals("unset", StringComparison.OrdinalIgnoreCase))
        {
            error = "unset";
            return false;
        }

        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            error = IsCompleteDigitRange(trimmed) ? "range_not_a_chord" : "invalid_range";
            return false;
        }

        string? key = null;
        foreach (var raw in SplitParts(trimmed))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                error = "invalid_chord";
                return false;
            }

            var lower = part.ToLowerInvariant();
            switch (lower)
            {
                case "prefix":
                    hasPrefix = true;
                    continue;
                case "ctrl" or "control" or "alt" or "option" or "meta" or "shift":
                    continue;
            }

            if (key is not null)
            {
                error = "multiple_keys";
                return false;
            }

            key = part;
        }

        if (key is null)
        {
            error = "missing_key";
            return false;
        }

        return true;
    }

    public static bool LooksLikeModifierOnly(string spec)
    {
        foreach (var raw in SplitParts(spec))
        {
            var lower = raw.Trim().ToLowerInvariant();
            if (lower.Length == 0)
                return false;
            if (lower is not ("prefix" or "ctrl" or "control" or "alt" or "option" or "meta" or "shift"))
                return false;
        }

        return true;
    }

    private static bool TryFindDigitRange(
        string spec,
        out string head,
        out string tail,
        out bool found,
        out string? error)
    {
        head = "";
        tail = "";
        found = false;
        error = null;

        var start = 0;
        for (var i = 0; i <= spec.Length; i++)
        {
            if (i < spec.Length && spec[i] != '+')
                continue;

            var token = spec.AsSpan(start, i - start).Trim();
            if (token.Equals("1..9", StringComparison.Ordinal))
            {
                if (found)
                {
                    error = "invalid_range";
                    return false;
                }

                found = true;
                head = spec[..start];
                tail = i < spec.Length ? spec[i..] : "";
                if (head.Length > 0 && !head.EndsWith('+'))
                    head += "+";
            }
            else if (token.Contains("..", StringComparison.Ordinal))
            {
                error = "invalid_range";
                return false;
            }

            start = i + 1;
        }

        return true;
    }

    private static IEnumerable<string> SplitParts(string spec)
    {
        var start = 0;
        for (var i = 0; i < spec.Length; i++)
        {
            if (spec[i] != '+')
                continue;
            yield return spec[start..i];
            start = i + 1;
        }

        yield return spec[start..];
    }
}
