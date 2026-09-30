namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>
/// Fenced skill block for an agent instruction file.
/// The marker shape matches <c>InjectFencedBlock</c> in Hypa.Infrastructure.
/// The marker also carries the release version so an older block is replaced.
/// </summary>
public static class IntegrationInstructionFence
{
    public const string MarkerName = "hypa-runtime";

    public static string Begin(string version) => "<!-- " + MarkerName + " " + version + " -->";

    public static string End(string version) => "<!-- /" + MarkerName + " " + version + " -->";

    public static string Block(string version, string body) =>
        Begin(version) + "\n" + body.Trim() + "\n" + End(version);

    public static string Insert(string existing, string version, string body)
    {
        var block = Block(version, body);
        if (TrySpan(existing, out var start, out var length))
        {
            if (existing.Substring(start, length) == block)
                return existing;
            return string.Concat(existing.AsSpan(0, start), block, existing.AsSpan(start + length));
        }

        if (existing.Length == 0)
            return block + "\n";
        if (existing.EndsWith("\n\n", StringComparison.Ordinal))
            return existing + block + "\n";
        if (existing.EndsWith('\n'))
            return existing + "\n" + block + "\n";
        return existing + "\n\n" + block + "\n";
    }

    /// <summary>Removes the Hypa block. Text outside the markers stays unchanged.</summary>
    public static string Remove(string existing)
    {
        if (!TrySpan(existing, out var start, out var length))
            return existing;
        return string.Concat(existing.AsSpan(0, start), existing.AsSpan(start + length));
    }

    public static bool Present(string existing) => TrySpan(existing, out _, out _);

    public static bool Current(string existing, string version, string body)
    {
        if (!TrySpan(existing, out var start, out var length))
            return false;
        return existing.Substring(start, length) == Block(version, body);
    }

    private static bool TrySpan(string content, out int start, out int length)
    {
        start = 0;
        length = 0;
        const string open = "<!-- " + MarkerName + " ";
        var index = content.IndexOf(open, StringComparison.Ordinal);
        if (index < 0)
            return false;
        var versionStart = index + open.Length;
        var versionEnd = content.IndexOf(" -->", versionStart, StringComparison.Ordinal);
        if (versionEnd < 0)
            return false;
        var version = content[versionStart..versionEnd];
        if (version.Length == 0 || version.Contains(' ') || version.Contains('\n'))
            return false;
        var endMarker = End(version);
        var end = content.IndexOf(endMarker, versionEnd, StringComparison.Ordinal);
        if (end < 0)
            return false;
        start = index;
        length = end + endMarker.Length - index;
        return true;
    }
}

/// <summary>Skill file path, instruction file path, or neither.</summary>
public readonly record struct RuntimeSkillLocation(string? SkillFile, string? InstructionFile)
{
    public bool IsNotApplicable => SkillFile is null && InstructionFile is null;
}
