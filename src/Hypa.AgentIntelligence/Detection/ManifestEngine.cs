using System.Text.RegularExpressions;
using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentIntelligence.Detection;

/// <summary>
/// Quote that file before changing match or region behaviour.
/// </summary>
internal static class ManifestEngine
{
    public const uint EngineVersion = 3;
    private const uint TopNonEmptyLinesEngineVersion = 3;
    private const int MaxRulesPerManifest = 128;
    private const int MaxGateDepth = 8;
    private const int MaxTotalGates = 512;
    private const int MaxMatchersPerGate = 32;
    private const int MaxTotalMatchers = 1024;
    private const int MaxMatcherChars = 512;
    private const int MaxTopRegionLineCount = 65535;

    public static CompiledManifest Compile(
        AgentManifestDocument manifest,
        ManifestOrigin origin,
        string? warning,
        string? cachedRemoteVersion,
        bool localOverrideShadowingRemote)
    {
        Validate(manifest);
        var compiled = new CompiledRule[manifest.Rules.Count];
        for (var i = 0; i < manifest.Rules.Count; i++)
        {
            var rule = manifest.Rules[i];
            compiled[i] = new CompiledRule(rule, CompileGate(rule.Gate));
        }

        return new CompiledManifest(
            manifest,
            compiled,
            origin,
            warning,
            cachedRemoteVersion,
            localOverrideShadowingRemote);
    }

    public static ManifestMatch Evaluate(CompiledManifest loaded, string screen) =>
        Evaluate(loaded, ManifestDetectionInput.FromScreen(screen));

    public static ManifestMatch Evaluate(CompiledManifest loaded, ManifestDetectionInput input)
    {
        var normalized = (input.Screen ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var regions = new ManifestDetectionInput(
            normalized,
            input.OscTitle ?? "",
            input.OscProgress ?? "");
        ManifestRuleDocument? matched = null;
        foreach (var compiled in loaded.Rules)
        {
            var regionText = Region(regions, compiled.Rule.Region);
            if (!GateMatches(compiled.Gate, regionText))
                continue;
            if (matched is not null && matched.Priority >= compiled.Rule.Priority)
                continue;
            matched = compiled.Rule;
        }

        if (matched is null)
        {
            return new ManifestMatch(
                AgentStatus.Idle,
                false,
                null,
                ScreenAgentCatalog.DefaultKnownAgentIdleFallback,
                loaded.Origin,
                loaded.Manifest.Version,
                loaded.CachedRemoteVersion,
                loaded.LocalOverrideShadowingRemote,
                loaded.Warning);
        }

        var state = matched.State ?? AgentStatus.Unknown;
        return new ManifestMatch(
            state,
            matched.SkipStateUpdate,
            matched.Id,
            matched.SkipStateUpdate ? "matched_rule:" + matched.Id : null,
            loaded.Origin,
            loaded.Manifest.Version,
            loaded.CachedRemoteVersion,
            loaded.LocalOverrideShadowingRemote,
            loaded.Warning);
    }

    public static void Validate(AgentManifestDocument manifest)
    {
        if (manifest.Rules.Count == 0)
            throw new InvalidOperationException("manifest must contain at least one rule");
        if (manifest.Rules.Count > MaxRulesPerManifest)
            throw new InvalidOperationException($"manifest contains {manifest.Rules.Count} rules, max is {MaxRulesPerManifest}");

        var complexity = new Complexity();
        foreach (var rule in manifest.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
                throw new InvalidOperationException("manifest rule id must not be empty");
            if (rule.SkipStateUpdate)
            {
                if (rule.State != AgentStatus.Unknown)
                    throw new InvalidOperationException($"rule {rule.Id} uses skip_state_update without state = \"unknown\"");
                if (rule.VisibleIdle || rule.VisibleBlocker || rule.VisibleWorking)
                    throw new InvalidOperationException($"rule {rule.Id} uses skip_state_update with visible state evidence");
            }

            ValidateRegionName(rule.Region);
            if (rule.Region.Trim().StartsWith("top_non_empty_lines(", StringComparison.Ordinal)
                && manifest.MinEngineVersion is uint min
                && min < TopNonEmptyLinesEngineVersion)
            {
                throw new InvalidOperationException(
                    $"rule {rule.Id} uses top_non_empty_lines but min_engine_version is below {TopNonEmptyLinesEngineVersion}");
            }

            ValidateGate(rule.Gate, "rule", 0, complexity);
        }
    }

    private static void ValidateGate(ManifestGateDocument gate, string context, int depth, Complexity complexity)
    {
        if (depth > MaxGateDepth)
            throw new InvalidOperationException($"{context} exceeds max gate depth {MaxGateDepth}");
        complexity.TotalGates++;
        if (complexity.TotalGates > MaxTotalGates)
            throw new InvalidOperationException($"manifest exceeds max gate count {MaxTotalGates}");
        ValidateMatcherLimits(gate, context, complexity);
        if (!HasPositiveMatcher(gate))
            throw new InvalidOperationException($"{context} must contain a positive matcher");
        ValidateRegexPatterns(gate.Regex, context, "regex");
        ValidateRegexPatterns(gate.LineRegex, context, "line_regex");
        foreach (var nested in gate.All)
            ValidateGate(nested, "all gate", depth + 1, complexity);
        foreach (var nested in gate.Any)
            ValidateGate(nested, "any gate", depth + 1, complexity);
        foreach (var nested in gate.Not)
        {
            if (!HasAnyMatcher(nested))
                throw new InvalidOperationException($"{context} contains an empty not gate");
            ValidateNotGate(nested, depth + 1, complexity);
        }
    }

    private static void ValidateNotGate(ManifestGateDocument gate, int depth, Complexity complexity)
    {
        if (depth > MaxGateDepth)
            throw new InvalidOperationException($"not gate exceeds max gate depth {MaxGateDepth}");
        complexity.TotalGates++;
        if (complexity.TotalGates > MaxTotalGates)
            throw new InvalidOperationException($"manifest exceeds max gate count {MaxTotalGates}");
        ValidateMatcherLimits(gate, "not gate", complexity);
        if (!HasAnyMatcher(gate))
            throw new InvalidOperationException("not gate must contain a matcher");
        ValidateRegexPatterns(gate.Regex, "not gate", "regex");
        ValidateRegexPatterns(gate.LineRegex, "not gate", "line_regex");
        foreach (var nested in gate.All)
            ValidateGate(nested, "not all gate", depth + 1, complexity);
        foreach (var nested in gate.Any)
            ValidateGate(nested, "not any gate", depth + 1, complexity);
        foreach (var nested in gate.Not)
            ValidateNotGate(nested, depth + 1, complexity);
    }

    private static void ValidateMatcherLimits(ManifestGateDocument gate, string context, Complexity complexity)
    {
        var matcherCount = gate.Contains.Count + gate.Regex.Count + gate.LineRegex.Count;
        if (matcherCount > MaxMatchersPerGate)
            throw new InvalidOperationException($"{context} has {matcherCount} direct matchers, max is {MaxMatchersPerGate}");
        complexity.TotalMatchers += matcherCount;
        if (complexity.TotalMatchers > MaxTotalMatchers)
            throw new InvalidOperationException($"manifest exceeds max matcher count {MaxTotalMatchers}");
        foreach (var value in gate.Contains.Concat(gate.Regex).Concat(gate.LineRegex))
        {
            if (value.EnumerateRunes().Count() > MaxMatcherChars)
                throw new InvalidOperationException($"{context} matcher exceeds max length {MaxMatcherChars}");
        }
    }

    private static void ValidateRegexPatterns(IReadOnlyList<string> patterns, string context, string field)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                _ = new Regex(ManifestRegex.TranslateForDotNet(pattern), RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"{context} contains invalid {field} pattern {pattern}: {ex.Message}");
            }
        }
    }

    private static bool HasPositiveMatcher(ManifestGateDocument gate) =>
        gate.Contains.Count > 0
        || gate.Regex.Count > 0
        || gate.LineRegex.Count > 0
        || gate.All.Count > 0
        || gate.Any.Count > 0;

    private static bool HasAnyMatcher(ManifestGateDocument gate) =>
        HasPositiveMatcher(gate) || gate.Not.Count > 0;

    private static void ValidateRegionName(string spec)
    {
        var trimmed = spec.Trim();
        switch (trimmed)
        {
            case "whole_recent":
            case "after_last_prompt_marker":
            case "before_current_prompt_marker":
            case "whole_recent_without_current_prompt_marker":
            case "current_prompt_block_marker":
            case "after_current_prompt_block_marker":
            case "prompt_box_body":
            case "above_prompt_box":
            case "last_non_empty_above_prompt_box":
            case "after_last_horizontal_rule":
            case "osc_title":
            case "osc_progress":
                return;
        }

        if (RegionCount(trimmed, "bottom_lines") is not null
            || RegionCount(trimmed, "bottom_non_empty_lines") is not null
            || TopRegionCount(trimmed) is not null)
        {
            return;
        }

        throw new InvalidOperationException($"rule uses invalid region: {trimmed}");
    }

    private static CompiledGate CompileGate(ManifestGateDocument gate)
    {
        var regex = new Regex[gate.Regex.Count];
        for (var i = 0; i < gate.Regex.Count; i++)
            regex[i] = new Regex(ManifestRegex.TranslateForDotNet(gate.Regex[i]), RegexOptions.CultureInvariant);
        var lineRegex = new Regex[gate.LineRegex.Count];
        for (var i = 0; i < gate.LineRegex.Count; i++)
            lineRegex[i] = new Regex(ManifestRegex.TranslateForDotNet(gate.LineRegex[i]), RegexOptions.CultureInvariant);

        var contains = new string[gate.Contains.Count];
        for (var i = 0; i < gate.Contains.Count; i++)
            contains[i] = gate.Contains[i].ToLowerInvariant();

        var all = new CompiledGate[gate.All.Count];
        for (var i = 0; i < gate.All.Count; i++)
            all[i] = CompileGate(gate.All[i]);
        var any = new CompiledGate[gate.Any.Count];
        for (var i = 0; i < gate.Any.Count; i++)
            any[i] = CompileGate(gate.Any[i]);
        var notGate = new CompiledGate[gate.Not.Count];
        for (var i = 0; i < gate.Not.Count; i++)
            notGate[i] = CompileGate(gate.Not[i]);

        return new CompiledGate(all, any, notGate, contains, regex, lineRegex);
    }

    private static bool GateMatches(CompiledGate gate, string text)
    {
        var lower = text.ToLowerInvariant();
        return GateMatches(gate, text, lower);
    }

    private static bool GateMatches(CompiledGate gate, string text, string lowerText)
    {
        foreach (var needle in gate.ContainsLower)
        {
            if (!lowerText.Contains(needle, StringComparison.Ordinal))
                return false;
        }

        foreach (var regex in gate.Regex)
        {
            if (!regex.IsMatch(text))
                return false;
        }

        foreach (var regex in gate.LineRegex)
        {
            var hit = false;
            foreach (var line in SplitLines(text))
            {
                if (regex.IsMatch(line))
                {
                    hit = true;
                    break;
                }
            }

            if (!hit)
                return false;
        }

        foreach (var nested in gate.All)
        {
            if (!GateMatches(nested, text, lowerText))
                return false;
        }

        if (gate.Any.Count > 0)
        {
            var anyHit = false;
            foreach (var nested in gate.Any)
            {
                if (GateMatches(nested, text, lowerText))
                {
                    anyHit = true;
                    break;
                }
            }

            if (!anyHit)
                return false;
        }

        foreach (var nested in gate.Not)
        {
            if (GateMatches(nested, text, lowerText))
                return false;
        }

        return true;
    }

    internal static string Region(string content, string spec) =>
        Region(ManifestDetectionInput.FromScreen(content), spec);

    /// <summary>
    // OSC regions read dedicated
    /// fields. Other regions read screen content.
    /// </summary>
    internal static string Region(ManifestDetectionInput input, string spec)
    {
        var trimmed = spec.Trim();
        if (trimmed is "osc_title")
            return input.OscTitle ?? "";
        if (trimmed is "osc_progress")
            return input.OscProgress ?? "";

        var content = input.Screen ?? "";
        return trimmed switch
        {
            "whole_recent" => content,
            "after_last_prompt_marker" => AfterLastPromptMarker(content),
            "before_current_prompt_marker" => BeforeCurrentPromptMarker(content),
            "whole_recent_without_current_prompt_marker" =>
                CurrentCodexPromptIndex(SplitLines(content)) is not null ? "" : content,
            "current_prompt_block_marker" => CurrentPromptBlockMarker(content) ?? "",
            "after_current_prompt_block_marker" => AfterCurrentPromptBlockMarker(content) ?? "",
            "prompt_box_body" => PromptBoxBody(content) ?? "",
            "above_prompt_box" => AbovePromptBox(content),
            "last_non_empty_above_prompt_box" => LastNonEmptyLine(AbovePromptBox(content)),
            "after_last_horizontal_rule" => AfterLastHorizontalRule(content),
            _ => NamedCountRegion(content, trimmed),
        };
    }

    private static string NamedCountRegion(string content, string trimmed)
    {
        if (RegionCount(trimmed, "bottom_lines") is int bottom)
            return BottomLines(content, bottom);
        if (RegionCount(trimmed, "bottom_non_empty_lines") is int bottomNe)
            return BottomNonEmptyLines(content, bottomNe);
        if (TopRegionCount(trimmed) is int top)
            return TopNonEmptyLines(content, top);
        return "";
    }

    private static int? RegionCount(string spec, string name)
    {
        if (!spec.StartsWith(name, StringComparison.Ordinal))
            return null;
        var rest = spec[name.Length..];
        if (rest.Length < 3 || rest[0] != '(' || rest[^1] != ')')
            return null;
        return int.TryParse(rest[1..^1], out var count) ? count : null;
    }

    private static int? TopRegionCount(string spec)
    {
        const string name = "top_non_empty_lines";
        if (!spec.StartsWith(name, StringComparison.Ordinal))
            return null;
        var rest = spec[name.Length..];
        if (rest.Length < 3 || rest[0] != '(' || rest[^1] != ')')
            return null;
        var count = rest[1..^1];
        if (count.StartsWith('0') || count.Length == 0)
            return null;
        foreach (var ch in count)
        {
            if (!char.IsAsciiDigit(ch))
                return null;
        }

        if (!int.TryParse(count, out var n) || n > MaxTopRegionLineCount)
            return null;
        return n;
    }

    private static string BottomLines(string content, int count)
    {
        var lines = SplitLines(content);
        var start = Math.Max(0, lines.Count - count);
        return SliceFromLine(content, lines, start);
    }

    private static string BottomNonEmptyLines(string content, int count)
    {
        var lines = SplitLines(content);
        var remaining = count;
        var startIndex = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Trim().Length == 0)
                continue;
            startIndex = i;
            remaining--;
            if (remaining == 0)
                break;
        }

        return startIndex < 0 ? "" : SliceFromLine(content, lines, startIndex);
    }

    private static string TopNonEmptyLines(string content, int count)
    {
        var lines = SplitLines(content);
        var taken = 0;
        var endIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Length == 0)
                continue;
            endIndex = i;
            taken++;
            if (taken == count)
                break;
        }

        if (endIndex < 0)
            return "";
        var end = LineStartOffset(content, lines, endIndex + 1);
        return content[..Math.Min(end, content.Length)];
    }

    private static string AfterLastPromptMarker(string content)
    {
        var lines = SplitLines(content);
        var index = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (CodexPromptLine(lines[i]))
            {
                index = i;
                break;
            }
        }

        return index < 0 ? content : SliceFromLine(content, lines, index + 1);
    }

    private static string BeforeCurrentPromptMarker(string content)
    {
        var lines = SplitLines(content);
        var index = CurrentCodexPromptIndex(lines);
        if (index is null)
            return content;
        var offset = LineStartOffset(content, lines, index.Value);
        return content[..Math.Min(offset, content.Length)];
    }

    private static string? CurrentPromptBlockMarker(string content)
    {
        var lines = SplitLines(content);
        var promptIndex = CurrentCodexPromptIndex(lines);
        if (promptIndex is null)
            return null;
        for (var i = promptIndex.Value - 1; i >= 0; i--)
        {
            if (CodexBlockMarkerLine(lines[i]))
                return lines[i];
        }

        return null;
    }

    private static string? AfterCurrentPromptBlockMarker(string content)
    {
        var lines = SplitLines(content);
        var promptIndex = CurrentCodexPromptIndex(lines);
        if (promptIndex is null)
            return null;
        var blockIndex = -1;
        for (var i = promptIndex.Value - 1; i >= 0; i--)
        {
            if (CodexBlockMarkerLine(lines[i]))
            {
                blockIndex = i;
                break;
            }
        }

        return blockIndex < 0 ? null : SliceFromLine(content, lines, blockIndex);
    }

    private static int? CurrentCodexPromptIndex(List<string> lines)
    {
        var promptIndex = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (CodexPromptLine(lines[i]))
            {
                promptIndex = i;
                break;
            }
        }

        if (promptIndex < 0)
            return null;
        for (var i = promptIndex + 1; i < lines.Count; i++)
        {
            if (CodexBlockMarkerLine(lines[i]))
                return null;
        }

        return promptIndex;
    }

    private static bool CodexPromptLine(string line) =>
        line == "›" || line.StartsWith("› ", StringComparison.Ordinal);

    private static bool CodexBlockMarkerLine(string line) =>
        line.StartsWith('•') || line.StartsWith('■') || line.StartsWith('✗') || line.StartsWith('✓');

    private static string? PromptBoxBody(string content)
    {
        var lines = SplitLines(content);
        var top = PromptBoxTopBorderIndex(lines);
        if (top is null)
            return null;
        var start = LineStartOffset(content, lines, top.Value + 1);
        var endIndex = lines.Count;
        for (var i = top.Value + 1; i < lines.Count; i++)
        {
            if (IsHorizontalRule(lines[i]))
            {
                endIndex = i;
                break;
            }
        }

        var end = LineStartOffset(content, lines, endIndex);
        var lo = Math.Min(start, content.Length);
        var hi = Math.Min(end, content.Length);
        return lo <= hi ? content[lo..hi] : "";
    }

    private static string AbovePromptBox(string content)
    {
        var lines = SplitLines(content);
        var top = PromptBoxTopBorderIndex(lines);
        if (top is null)
            return content;
        var end = LineStartOffset(content, lines, top.Value);
        return content[..Math.Min(end, content.Length)];
    }

    private static string AfterLastHorizontalRule(string content)
    {
        var lastRuleEnd = 0;
        var offset = 0;
        foreach (var line in SplitLines(content))
        {
            var next = offset + line.Length + 1;
            if (IsHorizontalRule(line))
                lastRuleEnd = Math.Min(next, content.Length);
            offset = next;
        }

        return lastRuleEnd <= content.Length ? content[lastRuleEnd..] : "";
    }

    private static string LastNonEmptyLine(string content)
    {
        var lines = SplitLines(content);
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Trim().Length > 0)
                return lines[i];
        }

        return "";
    }

    private static int? PromptBoxTopBorderIndex(List<string> lines)
    {
        var borderCount = 0;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (!IsHorizontalRule(lines[i]))
                continue;
            borderCount++;
            if (borderCount == 2)
                return i;
        }

        return null;
    }

    private static bool IsHorizontalRule(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return false;
        var ruleChars = 0;
        foreach (var ch in trimmed)
        {
            if (ch != '─')
                break;
            ruleChars++;
        }

        if (ruleChars == 0)
            return false;
        var suffix = trimmed[ruleChars..].TrimStart();
        return suffix.Length == 0 || ruleChars >= 3;
    }

    private static string SliceFromLine(string content, List<string> lines, int index) =>
        content[Math.Min(LineStartOffset(content, lines, index), content.Length)..];

    private static int LineStartOffset(string content, List<string> lines, int index)
    {
        var offset = 0;
        var limit = Math.Min(index, lines.Count);
        for (var i = 0; i < limit; i++)
            offset += lines[i].Length + 1;
        return Math.Min(offset, content.Length);
    }

    internal static List<string> SplitLines(string content)
    {
        var lines = new List<string>();
        var i = 0;
        while (i < content.Length)
        {
            var start = i;
            while (i < content.Length && content[i] != '\n')
                i++;
            var end = i;
            if (end > start && content[end - 1] == '\r')
                end--;
            lines.Add(content[start..end]);
            if (i < content.Length)
                i++;
        }

        return lines;
    }

    private sealed class Complexity
    {
        public int TotalGates;
        public int TotalMatchers;
    }
}
