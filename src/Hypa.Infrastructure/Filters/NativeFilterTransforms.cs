using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hypa.Infrastructure.Filters;

internal static class NativeFilterTransforms
{
    public static string Apply(string? id, string text) =>
        id switch
        {
            "git.status" => GitStatus(text),
            "git.log" => GitLog(text),
            "git.diff" => GitDiff(text),
            "docker.build" => DockerBuild(text),
            "docker.logs" => DedupeTimestampedLogs(text, 30, 15),
            "kubectl.logs" => DedupeTimestampedLogs(text, 30, 20),
            "kubectl.describe" => KubectlDescribe(text),
            "typecheck.python" => PythonTypecheck(text),
            "playwright" => Playwright(text),
            "test.go" => GoTest(text),
            "test.rspec" => Rspec(text),
            "test.mocha" => Mocha(text),
            "cmake" => Cmake(text),
            "ninja" => Ninja(text),
            "aws" => Aws(text),
            _ => text,
        };

    private static string GitStatus(string text)
    {
        var branch = string.Empty;
        var ahead = 0;
        var staged = new List<string>();
        var unstaged = new List<string>();
        var untracked = new List<string>();
        var section = string.Empty;

        foreach (var line in Lines(text))
        {
            var branchMatch = Regex.Match(line, @"^On branch (.+)$");
            if (branchMatch.Success)
                branch = branchMatch.Groups[1].Value;

            var aheadMatch = Regex.Match(line, @"ahead of '.+' by (\d+) commit");
            if (aheadMatch.Success)
                int.TryParse(aheadMatch.Groups[1].Value, out ahead);

            if (line.Contains("Changes to be committed", StringComparison.Ordinal))
                section = "staged";
            else if (line.Contains("Changes not staged", StringComparison.Ordinal))
                section = "unstaged";
            else if (line.Contains("Untracked files", StringComparison.Ordinal))
                section = "untracked";

            var trimmed = line.Trim();
            if (trimmed.StartsWith("new file:", StringComparison.Ordinal))
                AddChange(staged, section, "staged", "+", trimmed["new file:".Length..]);
            else if (trimmed.StartsWith("modified:", StringComparison.Ordinal))
            {
                AddChange(staged, section, "staged", "~", trimmed["modified:".Length..]);
                AddChange(unstaged, section, "unstaged", "~", trimmed["modified:".Length..]);
            }
            else if (trimmed.StartsWith("deleted:", StringComparison.Ordinal))
                AddChange(staged, section, "staged", "-", trimmed["deleted:".Length..]);
            else if (trimmed.StartsWith("renamed:", StringComparison.Ordinal))
                AddChange(staged, section, "staged", ">", trimmed["renamed:".Length..]);
            else if (section == "untracked" && IsUntrackedFileLine(trimmed))
                untracked.Add(trimmed);
        }

        if (branch.Length == 0 && staged.Count == 0 && unstaged.Count == 0 && untracked.Count == 0)
            return CompactLines(text.Trim(), 10);

        var parts = new List<string> { branch.Length > 0 ? branch : "?" };
        if (ahead > 0)
            parts[0] += $" ahead {ahead}";
        if (staged.Count > 0)
            parts.Add("staged: " + string.Join(' ', staged));
        if (unstaged.Count > 0)
            parts.Add("unstaged: " + string.Join(' ', unstaged));
        if (untracked.Count > 0)
            parts.Add("untracked: " + string.Join(' ', untracked));
        if (text.Contains("nothing to commit", StringComparison.Ordinal) && parts.Count == 1)
            parts.Add("clean");
        return string.Join('\n', parts);
    }

    private static void AddChange(List<string> target, string section, string expected, string prefix, string file)
    {
        if (section == expected)
            target.Add(prefix + file.Trim());
    }

    private static bool IsUntrackedFileLine(string trimmed) =>
        trimmed.Length > 0 &&
        !trimmed.StartsWith('(') &&
        !trimmed.StartsWith("Untracked", StringComparison.Ordinal) &&
        !trimmed.StartsWith("nothing added", StringComparison.Ordinal);

    private static string GitLog(string text)
    {
        var lines = Lines(text).ToArray();
        if (lines.Length == 0)
            return string.Empty;
        if (!lines[0].StartsWith("commit ", StringComparison.Ordinal))
            return lines.Length <= 100
                ? string.Join('\n', lines)
                : string.Join('\n', lines.Take(100)) + $"\n... ({lines.Length - 100} more commits, use git log --max-count=N to see all)";

        var entries = new List<string>();
        var inDiff = false;
        var additions = 0;
        var deletions = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("commit ", StringComparison.Ordinal))
            {
                entries.Add(trimmed.Length >= 14 ? trimmed[7..14] : trimmed[7..]);
                inDiff = false;
                continue;
            }
            if (trimmed.StartsWith("Author:", StringComparison.Ordinal) ||
                trimmed.StartsWith("Date:", StringComparison.Ordinal) ||
                trimmed.StartsWith("Merge:", StringComparison.Ordinal) ||
                trimmed.Length == 0)
                continue;
            if (trimmed.StartsWith("diff --git", StringComparison.Ordinal) || trimmed.StartsWith("--- a/", StringComparison.Ordinal))
            {
                inDiff = true;
                continue;
            }
            if (inDiff || IsDiffStatLine(trimmed))
            {
                if (trimmed.StartsWith('+') && !trimmed.StartsWith("+++", StringComparison.Ordinal)) additions++;
                if (trimmed.StartsWith('-') && !trimmed.StartsWith("---", StringComparison.Ordinal)) deletions++;
                continue;
            }
            if (entries.Count > 0 && !entries[^1].Contains(' ', StringComparison.Ordinal))
                entries[^1] += " " + trimmed;
        }

        if (entries.Count == 0)
            return text;
        var output = entries.Count <= 100
            ? string.Join('\n', entries)
            : string.Join('\n', entries.Take(100)) + $"\n... ({entries.Count - 100} more commits, use git log --max-count=N to see all)";
        if (additions > 0 || deletions > 0)
            output += $"\n[{entries.Count} commits, +{additions}/-{deletions} total]";
        return output;
    }

    private static bool IsDiffStatLine(string line) =>
        line.Contains(" | ", StringComparison.Ordinal) && (line.EndsWith('+') || line.EndsWith('-'));

    private static string GitDiff(string text)
    {
        var result = new List<string>();
        var contextRun = 0;
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("diff --git", StringComparison.Ordinal) || line.StartsWith("@@", StringComparison.Ordinal))
            {
                contextRun = 0;
                result.Add(line);
            }
            else if (line.StartsWith("index ", StringComparison.Ordinal))
            {
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                result.Add(line);
            }
            else if (line.StartsWith('+') || line.StartsWith('-'))
            {
                contextRun = 0;
                result.Add(line);
            }
            else if (++contextRun <= 3)
            {
                result.Add(line);
            }
        }

        return result.Count == 0 ? text : string.Join('\n', result);
    }

    private static string DockerBuild(string text)
    {
        var steps = 0;
        var lastStep = string.Empty;
        var errors = new List<string>();
        foreach (var line in Lines(text))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Step ", StringComparison.Ordinal) ||
                (trimmed.StartsWith('#') && trimmed.Contains('[', StringComparison.Ordinal)))
            {
                steps++;
                lastStep = trimmed;
            }
            if (trimmed.Contains("ERROR", StringComparison.Ordinal) ||
                trimmed.Contains("error:", StringComparison.OrdinalIgnoreCase))
                errors.Add(trimmed);
        }

        if (errors.Count > 0)
            return $"{steps} steps, {errors.Count} errors:\n" + string.Join('\n', errors.Take(20));
        return steps > 0 ? $"{steps} steps, last: {lastStep}" : "built";
    }

    private static string DedupeTimestampedLogs(string text, int maxLines, int tailLines)
    {
        var originalLines = Lines(text).ToArray();
        if (originalLines.Length <= 10)
            return text;

        var deduped = new List<(string Line, int Count)>();
        foreach (var line in originalLines)
        {
            var normalized = Regex.Replace(line, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\S*\s*", "");
            var trimmed = normalized.Trim();
            if (trimmed.Length == 0)
                continue;
            if (deduped.Count > 0 && deduped[^1].Line == trimmed)
                deduped[^1] = (trimmed, deduped[^1].Count + 1);
            else
                deduped.Add((trimmed, 1));
        }

        var rendered = deduped
            .Select(item => item.Count > 1 ? $"{item.Line} (x{item.Count})" : item.Line)
            .ToArray();
        if (rendered.Length <= maxLines)
            return string.Join('\n', rendered);
        return $"... ({originalLines.Length} lines total)\n" + string.Join('\n', rendered.TakeLast(tailLines));
    }

    private static string KubectlDescribe(string text)
    {
        var lines = Lines(text).ToArray();
        if (lines.Length <= 20)
            return text;

        var sections = new List<string>();
        var name = string.Empty;
        var current = string.Empty;
        var currentLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("Name:", StringComparison.Ordinal) || line.StartsWith("Namespace:", StringComparison.Ordinal))
            {
                name = name.Length == 0 ? line.Trim() : name + "\n" + line.Trim();
                continue;
            }
            if (!line.StartsWith(' ') && !line.StartsWith('\t') && line.EndsWith(':') && !line.Contains("  ", StringComparison.Ordinal))
            {
                FlushSection(sections, current, currentLines);
                current = line.TrimEnd(':');
                currentLines.Clear();
            }
            else if (current.Length > 0)
            {
                currentLines.Add(line);
            }
        }
        FlushSection(sections, current, currentLines);
        return string.Join('\n', new[] { name }.Where(s => s.Length > 0).Concat(sections));
    }

    private static void FlushSection(List<string> sections, string current, List<string> lines)
    {
        if (current.Length == 0)
            return;
        if (current == "Events" && lines.Count > 5)
            sections.Add($"Events (last 5 of {lines.Count}):\n" + string.Join('\n', lines.TakeLast(5)));
        else if (lines.Count <= 5)
            sections.Add(current + "\n" + string.Join('\n', lines));
        else
            sections.Add($"{current} ({lines.Count} lines)");
    }

    private static string PythonTypecheck(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return "ok";
        if (trimmed.Contains("no issues found", StringComparison.OrdinalIgnoreCase))
            return "clean";

        var diagnostics = new List<(string File, string Line, string Severity, string Message, string Code)>();
        var byCode = new Dictionary<string, int>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in Lines(trimmed))
        {
            var match = Regex.Match(line, @"^(.+?):(\d+):\s+(error|warning|note):\s+(.+?)(?:\s+\[(.+)\])?$", RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;
            var file = match.Groups[1].Value;
            var code = match.Groups[5].Success ? match.Groups[5].Value : "?";
            files.Add(file);
            byCode[code] = byCode.GetValueOrDefault(code) + 1;
            diagnostics.Add((file, match.Groups[2].Value, match.Groups[3].Value.ToLowerInvariant(), match.Groups[4].Value, code));
        }

        if (diagnostics.Count == 0)
            return CompactLines(trimmed, 8);

        var errorCount = diagnostics.Count(d => d.Severity == "error");
        var warningCount = diagnostics.Count(d => d.Severity == "warning");
        var parts = new List<string> { $"{diagnostics.Count} issues in {files.Count} files ({errorCount} errors, {warningCount} warnings)" };
        foreach (var item in byCode.OrderByDescending(kv => kv.Value).Take(6))
            parts.Add($"  [{item.Key}]: {item.Value}");
        parts.Add("Top errors:");
        parts.AddRange(diagnostics.Take(5).Select(d => $"  {ShortFile(d.File)}:{d.Line} [{d.Code}] {d.Message}"));
        return string.Join('\n', parts);
    }

    private static string Playwright(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return "ok";
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var failedNames = new List<string>();
        var artifacts = new List<string>();
        var duration = string.Empty;

        foreach (var line in Lines(trimmed))
        {
            var l = line.Trim().ToLowerInvariant();
            passed = ExtractBeforeKeyword(l, "passed") ?? passed;
            failed = ExtractBeforeKeyword(l, "failed") ?? failed;
            skipped = ExtractBeforeKeyword(l, "skipped") ?? skipped;
            var failedMatch = Regex.Match(line, @"^\s+\d+\)\s+(.+)$");
            if (failedMatch.Success)
                failedNames.Add(failedMatch.Groups[1].Value.Trim());
            if (l.Contains("finished in", StringComparison.Ordinal) || l.Contains("duration", StringComparison.Ordinal))
                duration = line.Trim();
            if (Regex.IsMatch(line, @"\.(png|webm|zip|trace)$", RegexOptions.IgnoreCase))
                artifacts.Add(line.Trim());
        }

        var total = passed + failed + skipped;
        if (total == 0)
            return CompactLines(trimmed, 10);
        var parts = new List<string> { $"{total} tests: {passed} passed, {failed} failed, {skipped} skipped" };
        if (failedNames.Count > 0)
            parts.AddRange(new[] { "failed:" }.Concat(failedNames.Take(10).Select(name => $"  {name}")));
        if (artifacts.Count > 0)
            parts.AddRange(new[] { "artifacts:" }.Concat(artifacts.Take(10).Select(path => $"  {path}")));
        if (duration.Length > 0)
            parts.Add(duration);
        return string.Join('\n', parts);
    }

    private static int? ExtractBeforeKeyword(string line, string keyword)
    {
        var pos = line.IndexOf(keyword, StringComparison.Ordinal);
        if (pos < 0)
            return null;
        return int.TryParse(line[..pos].Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), out var value)
            ? value
            : null;
    }

    private static string GoTest(string text)
    {
        var passed = 0;
        var failed = 0;
        var failures = new List<string>();
        var packages = new List<string>();
        foreach (var line in Lines(text))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("--- PASS:", StringComparison.Ordinal))
                passed++;
            else if (trimmed.StartsWith("--- FAIL:", StringComparison.Ordinal))
            {
                failed++;
                failures.Add(trimmed["--- FAIL:".Length..].Trim());
            }
            else if (trimmed.StartsWith("ok ", StringComparison.Ordinal) || trimmed.StartsWith("FAIL\t", StringComparison.Ordinal))
                packages.Add(trimmed);
        }
        if (passed == 0 && failed == 0)
            return text;
        var parts = new List<string> { failed > 0 ? $"go test: {passed} passed, {failed} failed" : $"go test: {passed} passed" };
        parts.AddRange(packages.Select(pkg => $"  {pkg}"));
        parts.AddRange(failures.Take(5).Select(f => $"  FAIL: {f}"));
        return string.Join('\n', parts);
    }

    private static string Rspec(string text)
    {
        foreach (var line in Lines(text).Reverse())
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("example", StringComparison.Ordinal) && trimmed.Contains("failure", StringComparison.Ordinal))
                return "rspec: " + trimmed;
        }
        return text;
    }

    private static string Mocha(string text)
    {
        var passing = 0;
        var failing = 0;
        var duration = string.Empty;
        var failures = new List<string>();
        var inFailures = false;
        foreach (var line in Lines(text))
        {
            var trimmed = line.Trim();
            var passingMatch = Regex.Match(trimmed, @"^(\d+)\s+passing(?:\s+\((.+)\))?");
            if (passingMatch.Success)
            {
                int.TryParse(passingMatch.Groups[1].Value, out passing);
                duration = passingMatch.Groups[2].Value;
            }
            var failingMatch = Regex.Match(trimmed, @"^(\d+)\s+failing");
            if (failingMatch.Success)
            {
                int.TryParse(failingMatch.Groups[1].Value, out failing);
                inFailures = true;
            }
            if (inFailures && Regex.IsMatch(trimmed, @"^\d+\)"))
                failures.Add(Regex.Replace(trimmed, @"^\d+\)\s*", ""));
        }
        if (passing == 0 && failing == 0)
            return text;
        var result = $"mocha: {passing} passed" + (failing > 0 ? $", {failing} failed" : "");
        if (duration.Length > 0)
            result += $" ({duration})";
        if (failures.Count > 0)
            result += "\n" + string.Join('\n', failures.Take(10).Select(f => $"  FAIL: {f}"));
        return result;
    }

    private static string Cmake(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return "ok";
        var errors = Lines(trimmed).Where(l => l.Contains("CMake Error", StringComparison.Ordinal) || l.TrimStart().StartsWith("ERROR:", StringComparison.Ordinal)).ToArray();
        var warnings = Lines(trimmed).Count(l => l.Contains("CMake Warning", StringComparison.Ordinal) || l.TrimStart().StartsWith("WARNING:", StringComparison.Ordinal));
        if (errors.Length > 0)
            return $"{errors.Length} errors:\n" + string.Join('\n', errors.Take(10).Select(e => "  " + e.Trim()));
        if (trimmed.Contains("Configuring done", StringComparison.Ordinal) || trimmed.Contains("Build files have been written", StringComparison.Ordinal))
            return warnings > 0 ? $"CMake configured ok ({warnings} warnings)" : "CMake configured ok";
        return CompactLines(trimmed, 10);
    }

    private static string Ninja(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return "ninja: ok";
        var current = 0;
        var total = 0;
        var errors = new List<string>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(trimmed))
        {
            var progress = Regex.Match(line.Trim(), @"^\[(\d+)/(\d+)\]\s+");
            if (progress.Success)
            {
                int.TryParse(progress.Groups[1].Value, out current);
                int.TryParse(progress.Groups[2].Value, out total);
                continue;
            }
            var lower = line.ToLowerInvariant();
            if (lower.Contains("error:", StringComparison.Ordinal) || lower.Contains("fatal error", StringComparison.Ordinal) || lower.Contains("ninja: error", StringComparison.Ordinal))
                errors.Add(line.Trim());
            else if (lower.Contains("warning:", StringComparison.Ordinal))
                warnings.Add(Regex.Replace(line.Trim(), @"[^\s:]+:\d+:\d+:\s*", ""));
        }
        if (errors.Count > 0)
            return $"ninja: FAILED ({errors.Count} errors, {warnings.Count} unique warnings, {current}/{total} steps)\n" + string.Join('\n', errors.Take(10).Select(e => $"  {e}"));
        var result = $"ninja: ok ({current}/{total} steps)";
        if (warnings.Count > 0)
            result += $"\n{warnings.Count} unique warnings:\n" + string.Join('\n', warnings.Take(10).Select(w => $"  {w}"));
        return result;
    }

    private static string Aws(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return "ok";
        if (TryJsonSummary(trimmed, out var json))
            return json;
        return CompactLines(trimmed, 15);
    }

    private static bool TryJsonSummary(string text, out string summary)
    {
        summary = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            var parts = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject().Take(20))
            {
                parts.Add(prop.Value.ValueKind switch
                {
                    JsonValueKind.Array => $"{prop.Name}: [{prop.Value.GetArrayLength()} items]",
                    JsonValueKind.Object => $"{prop.Name}: {{...}}",
                    JsonValueKind.String => $"{prop.Name}: \"{prop.Value.GetString()}\"",
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => $"{prop.Name}: {prop.Value}",
                    _ => prop.Name,
                });
            }
            summary = "JSON: {" + string.Join(", ", parts) + "}";
            return parts.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ShortFile(string file) =>
        file.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? file;

    private static string CompactLines(string text, int max)
    {
        var lines = Lines(text).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        return lines.Length <= max
            ? string.Join('\n', lines)
            : string.Join('\n', lines.Take(max)) + $"\n... ({lines.Length - max} more lines)";
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n');
}
