namespace Hypa.Runtime.Application.Services;

/// <summary>
/// Noise-exclusion rules for the <see cref="IndexProfile.CodeGraph"/> export profile.
/// Config-data languages emit one "key" symbol per line and are not code; generated,
/// vendored, lockfile, and minified files are not authored source. Neither belongs in
/// the code-graph symbol/edge stream. Markdown is never subject to these rules — the
/// doc consumer needs section emission regardless of profile.
/// </summary>
internal static class CodeGraphNoisePolicy
{
    // .json/.yaml/.yml/.toml are indexed for structural/config visibility, but their
    // "key" symbols (one per data key) are not code symbols. Excluding the language
    // entirely also drops generated dumps like openapi/*.json for free.
    private static readonly HashSet<string> NoiseLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "yaml", "toml",
    };

    // Lockfile basenames. Most already fail CodeLanguageRegistry's extension map
    // (no language for "yarn.lock"), but package-lock.json/pnpm-lock.yaml only lose
    // their "key" symbols via NoiseLanguages above — this list is the explicit,
    // future-proof denylist the design forum asked for.
    private static readonly HashSet<string> LockfileBasenames = new(StringComparer.Ordinal)
    {
        "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock",
        "Cargo.lock", "Gemfile.lock", "poetry.lock", "Pipfile.lock", "composer.lock",
        "mix.lock", "go.sum", "packages.lock.json", "flake.lock",
    };

    // Directory segments that mark generated or vendored output. Checked as whole
    // path segments (not substrings) to avoid false positives like "mycoverage/".
    private static readonly HashSet<string> DenylistedDirectorySegments = new(StringComparer.Ordinal)
    {
        ".next", ".nuxt", ".svelte-kit", "coverage",
    };

    public static bool IsNoiseLanguage(string language) => NoiseLanguages.Contains(language);

    public static bool IsDenylistedPath(string relativePath)
    {
        var fileName = relativePath[(relativePath.LastIndexOf('/') + 1)..];

        if (LockfileBasenames.Contains(fileName))
            return true;

        // Path.GetExtension(".../foo.d.ts") is ".ts" — generated declaration files
        // masquerade as ordinary TypeScript and need an explicit suffix check.
        if (fileName.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase))
            return true;

        // Minified bundles and source maps: foo.min.js, foo.min.css, foo.js.map.
        if (fileName.Contains(".min.", StringComparison.OrdinalIgnoreCase))
            return true;
        if (fileName.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
            return true;

        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (DenylistedDirectorySegments.Contains(segments[i]))
                return true;

            // .vitepress/cache/** — vendored dependency bundles (e.g. cytoscape.js)
            // checked in under a build-tool cache dir, sailing under the size gate.
            if (segments[i] == ".vitepress" && i + 1 < segments.Length && segments[i + 1] == "cache")
                return true;
        }

        return false;
    }

    /// <summary>
    /// Heuristic for vendored/bundled content that evades the path denylist (e.g. a
    /// minified bundle checked in without "min" in its name, or living outside a
    /// known cache directory). Flags files with very long average lines, or with
    /// both very low whitespace density AND at least one pathologically long line.
    /// Dense hand-authored source (long generic signatures, compact interfaces) can
    /// sit near the whitespace floor without ever having a line of thousands of
    /// characters — the max-line gate is what keeps those files in.
    /// </summary>
    public static bool LooksMinified(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        var lineCount = 1;
        var maxLineLength = 0;
        var currentLineLength = 0;
        foreach (var ch in content)
        {
            if (ch == '\n')
            {
                if (currentLineLength > maxLineLength)
                    maxLineLength = currentLineLength;
                currentLineLength = 0;
                lineCount++;
            }
            else
            {
                currentLineLength++;
            }
        }

        if (currentLineLength > maxLineLength)
            maxLineLength = currentLineLength;

        var meanLineLength = (double)content.Length / lineCount;
        if (meanLineLength > 500)
            return true;

        // Whitespace-ratio check needs enough content to be meaningful; short files
        // with naturally low whitespace (e.g. a single import line) would otherwise
        // false-positive. Dense hand-authored source can also sit under 8% whitespace
        // (long C# generic signatures) while keeping bounded line lengths — require
        // a genuinely long line so only minified/bundled output trips this branch.
        if (content.Length < 2000)
            return false;

        var whitespaceCount = 0;
        foreach (var ch in content)
        {
            if (char.IsWhiteSpace(ch))
                whitespaceCount++;
        }

        var whitespaceRatio = (double)whitespaceCount / content.Length;
        return whitespaceRatio < 0.08 && maxLineLength > 500;
    }
}
