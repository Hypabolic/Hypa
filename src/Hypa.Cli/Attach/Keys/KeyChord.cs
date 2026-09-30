using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Runtime.Domain.Common;

namespace Hypa.Cli.Attach.Keys;

/// <summary>One parsed key chord. Tokens: <c>ctrl</c>, <c>alt</c>, <c>shift</c>, <c>prefix</c>, named keys, <c>1..9</c>.</summary>
public sealed record KeyChord(bool Prefix, bool Ctrl, bool Alt, bool Shift, string Key)
{
    public static KeyChord Parse(string spec)
    {
        var result = TryParse(spec);
        if (!result.IsOk)
            throw new FormatException(result.Error);
        return result.Value;
    }

    public static Result<KeyChord, string> TryParse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return Result<KeyChord, string>.Fail("empty_chord");

        var trimmed = spec.Trim();
        if (trimmed.Equals("unset", StringComparison.OrdinalIgnoreCase))
            return Result<KeyChord, string>.Fail("unset");
        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            return Result<KeyChord, string>.Fail(
                AttachKeySpec.IsCompleteDigitRange(trimmed) ? "range_not_a_chord" : "invalid_range");
        }

        var prefix = false;
        var ctrl = false;
        var alt = false;
        var shift = false;
        string? key = null;

        foreach (var raw in SplitParts(trimmed))
        {
            var part = raw.Trim();
            if (part.Length == 0)
                return Result<KeyChord, string>.Fail("invalid_chord");

            var lower = part.ToLowerInvariant();
            switch (lower)
            {
                case "prefix":
                    prefix = true;
                    continue;
                case "ctrl" or "control":
                    ctrl = true;
                    continue;
                case "alt" or "option" or "meta":
                    alt = true;
                    continue;
                case "shift":
                    shift = true;
                    continue;
            }

            if (key is not null)
                return Result<KeyChord, string>.Fail("multiple_keys");
            key = NormalizeKey(part);
        }

        if (key is null)
            return Result<KeyChord, string>.Fail("missing_key");

        return Result<KeyChord, string>.Ok(new KeyChord(prefix, ctrl, alt, shift, key));
    }

    public KeyChord WithoutPrefix() => Prefix ? this with { Prefix = false } : this;

    public bool IsEscape =>
        !Prefix && !Ctrl && !Alt && !Shift && Key is "esc";

    public bool IsEnter =>
        !Prefix && !Ctrl && !Alt && !Shift && Key is "enter";

    public bool IsTab =>
        !Prefix && !Ctrl && !Alt && !Shift && Key is "tab";

    public bool IsShiftTab =>
        !Prefix && !Ctrl && !Alt && Shift && Key is "tab";

    public bool IsUnmodifiedDigit =>
        !Prefix && !Ctrl && !Alt && !Shift && Key.Length == 1 && Key[0] is >= '1' and <= '9';

    public bool IsArrow => Key is "up" or "down" or "left" or "right";

    public bool IsLeftOrRightArrow =>
        !Prefix && !Ctrl && !Alt && !Shift && Key is "left" or "right";

    public bool IsPrintable =>
        !Ctrl && !Alt && !Prefix && Key.Length > 0 && Key is not (
            "esc" or "enter" or "tab" or "backspace" or "up" or "down" or "left" or "right"
            or "space");

    public string Format()
    {
        var parts = new List<string>(5);
        if (Prefix)
            parts.Add("prefix");
        if (Ctrl)
            parts.Add("ctrl");
        if (Alt)
            parts.Add("alt");
        if (Shift)
            parts.Add("shift");
        parts.Add(Key);
        return string.Join('+', parts);
    }

    public override string ToString() => Format();

    public static string NormalizeKey(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var lower = token.ToLowerInvariant();
        return lower switch
        {
            "esc" or "escape" => "esc",
            "enter" or "return" => "enter",
            "backspace" or "bs" => "backspace",
            "space" => "space",
            "tab" => "tab",
            "up" => "up",
            "down" => "down",
            "left" => "left",
            "right" => "right",
            "minus" or "-" => "minus",
            "plus" or "+" => "plus",
            "comma" or "," => "comma",
            "period" or "." or "dot" => "period",
            "slash" or "/" => "slash",
            "question" => "?",
            "[" or "left_bracket" => "[",
            "]" or "right_bracket" => "]",
            _ when token.Length == 1 => char.IsAsciiLetter(token[0])
                ? char.ToLowerInvariant(token[0]).ToString()
                : token,
            _ => lower,
        };
    }

    internal static IEnumerable<string> SplitParts(string spec)
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

/// <summary>One string or a list of strings. Empty / <c>unset</c> means no chord.</summary>
public sealed record BindingSpec
{
    public static BindingSpec Unset { get; } = new([]);

    public IReadOnlyList<string> Specs { get; }

    public bool IsUnset => Specs.Count == 0;

    public string? Primary => Specs.Count == 0 ? null : Specs[0];

    private BindingSpec(IReadOnlyList<string> specs) => Specs = specs;

    public static BindingSpec From(string spec) => Parse(spec);

    public static BindingSpec From(IReadOnlyList<string> specs)
    {
        if (specs is null || specs.Count == 0)
            return Unset;
        if (specs is string[] arr)
            return From(arr);

        var copy = new string[specs.Count];
        for (var i = 0; i < specs.Count; i++)
            copy[i] = specs[i];
        return From(copy);
    }

    public static BindingSpec From(params string[] specs)
    {
        if (specs is null || specs.Length == 0)
            return Unset;
        var list = new List<string>(specs.Length);
        foreach (var spec in specs)
        {
            var parsed = Parse(spec);
            if (!parsed.IsUnset)
                list.AddRange(parsed.Specs);
        }

        return list.Count == 0 ? Unset : new BindingSpec(list);
    }

    public static BindingSpec Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Unset;
        var trimmed = raw.Trim();
        if (trimmed.Equals("unset", StringComparison.OrdinalIgnoreCase))
            return Unset;
        return new BindingSpec([trimmed]);
    }

    public bool TryExpand(out IReadOnlyList<string> expanded, out string? error)
    {
        expanded = [];
        error = null;
        if (IsUnset)
            return true;

        var list = new List<string>();
        foreach (var spec in Specs)
        {
            if (!AttachKeySpec.TryExpand(spec, out var part, out error))
            {
                expanded = [];
                return false;
            }

            list.AddRange(part);
        }

        expanded = list;
        return true;
    }

    public IReadOnlyList<string> Expand() =>
        TryExpand(out var expanded, out _) ? expanded : [];

    public static implicit operator BindingSpec(string spec) => Parse(spec);

    public override string ToString() => IsUnset ? "unset" : string.Join(", ", Specs);
}
