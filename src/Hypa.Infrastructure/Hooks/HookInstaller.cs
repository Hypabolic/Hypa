using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Infrastructure.Hooks;

public sealed class HookInstaller : IHookInstaller
{
    public async Task<InstallReport> InstallAsync(
        InstallPlan plan,
        string harnessKey,
        bool dryRun,
        CancellationToken ct = default)
    {
        var entries = new List<InstallEntry>();

        foreach (var op in plan.Operations)
        {
            var entry = op switch
            {
                InstallOperation.PatchJsonHook patch => await ExecutePatchJsonHookAsync(patch, dryRun, ct),
                InstallOperation.PatchTomlKey toml => await ExecutePatchTomlKeyAsync(toml, dryRun, ct),
                InstallOperation.EnsureCodexHooksFeature codexHooks => await ExecuteEnsureCodexHooksFeatureAsync(codexHooks, dryRun, ct),
                InstallOperation.WriteFile write => await ExecuteWriteFileAsync(write, dryRun, ct),
                InstallOperation.InjectLine inject => await ExecuteInjectLineAsync(inject, dryRun, ct),
                InstallOperation.PatchJsonObject pjo => await ExecutePatchJsonObjectAsync(pjo, dryRun, ct),
                InstallOperation.InjectFencedBlock fence => await ExecuteInjectFencedBlockAsync(fence, dryRun, ct),
                InstallOperation.PatchTomlSection pts => await ExecutePatchTomlSectionAsync(pts, dryRun, ct),
                InstallOperation.NotSupported ns => new InstallEntry(
                    "Install", InstallStatus.Skipped, ns.Message),
                _ => new InstallEntry("Unknown operation", InstallStatus.Error, "Unrecognised operation type"),
            };
            entries.Add(entry);
        }

        return new InstallReport(harnessKey, entries);
    }

    private static async Task<InstallEntry> ExecutePatchJsonHookAsync(
        InstallOperation.PatchJsonHook op,
        bool dryRun,
        CancellationToken ct)
    {
        var description = $"Hook in {op.FilePath}";
        try
        {
            var dir = Path.GetDirectoryName(op.FilePath);
            if (dir is { Length: > 0 } && !Directory.Exists(dir))
            {
                if (!dryRun) Directory.CreateDirectory(dir);
            }

            JsonNode root;
            if (File.Exists(op.FilePath))
            {
                var existing = await File.ReadAllTextAsync(op.FilePath, ct);
                try
                {
                    root = JsonNode.Parse(existing) ?? new JsonObject();
                }
                catch (JsonException ex)
                {
                    return new InstallEntry(description, InstallStatus.Error, $"Invalid JSON: {ex.Message}");
                }
            }
            else
            {
                root = new JsonObject();
            }

            var hookNode = JsonNode.Parse(op.HookJson);
            if (hookNode is null)
                return new InstallEntry(description, InstallStatus.Error, "Invalid hook JSON in install plan");

            if (IsHookAlreadyInstalled(root, op.HookEventName, op.HookJson))
                return new InstallEntry(description, InstallStatus.AlreadyPresent);

            AddHookToJson(root, op.HookEventName, hookNode);

            if (!dryRun)
                await WriteAtomicAsync(op.FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);

            return new InstallEntry(description, InstallStatus.Installed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InstallEntry(description, InstallStatus.Error, ex.Message);
        }
    }

    private static async Task<InstallEntry> ExecutePatchTomlKeyAsync(
        InstallOperation.PatchTomlKey op,
        bool dryRun,
        CancellationToken ct)
    {
        var description = $"Config {op.Section}.{op.Key} in {op.FilePath}";
        try
        {
            var dir = Path.GetDirectoryName(op.FilePath);
            if (dir is { Length: > 0 } && !Directory.Exists(dir))
            {
                if (!dryRun) Directory.CreateDirectory(dir);
            }

            var lines = File.Exists(op.FilePath)
                ? (await File.ReadAllLinesAsync(op.FilePath, ct)).ToList()
                : [];

            if (IsTomlKeyPresent(lines, op.Section, op.Key, op.Value))
                return new InstallEntry(description, InstallStatus.AlreadyPresent);

            var patched = PatchTomlSection(lines, op.Section, op.Key, op.Value);

            if (!dryRun)
                await WriteAtomicAsync(op.FilePath, string.Join(Environment.NewLine, patched) + Environment.NewLine, ct);

            return new InstallEntry(description, InstallStatus.Installed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InstallEntry(description, InstallStatus.Error, ex.Message);
        }
    }

    private static async Task<InstallEntry> ExecutePatchTomlSectionAsync(
        InstallOperation.PatchTomlSection op,
        bool dryRun,
        CancellationToken ct)
    {
        var description = $"Config section [{op.SectionPath}] in {op.FilePath}";
        try
        {
            var dir = Path.GetDirectoryName(op.FilePath);
            if (dir is { Length: > 0 } && !Directory.Exists(dir))
            {
                if (!dryRun) Directory.CreateDirectory(dir);
            }

            var lines = File.Exists(op.FilePath)
                ? (await File.ReadAllLinesAsync(op.FilePath, ct)).ToList()
                : [];

            var (patched, status) = PatchTomlSectionContent(lines, op.SectionPath, op.Content);
            if (status == InstallStatus.AlreadyPresent)
                return new InstallEntry(description, InstallStatus.AlreadyPresent);

            if (!dryRun)
                await WriteAtomicAsync(op.FilePath, string.Join(Environment.NewLine, patched) + Environment.NewLine, ct);

            return new InstallEntry(description, InstallStatus.Installed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InstallEntry(description, InstallStatus.Error, ex.Message);
        }
    }

    private static (List<string> Lines, InstallStatus Status) PatchTomlSectionContent(
        List<string> lines, string sectionPath, string content)
    {
        var result = new List<string>(lines);
        var header = $"[{sectionPath}]";
        var contentLines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        var sectionIdx = result.FindIndex(l =>
            TomlSectionHelper.TryParseHeaderPath(l, out var p) && p == sectionPath);
        if (sectionIdx >= 0)
        {
            var nextIdx = TomlSectionHelper.FindNextNonDescendantSection(result, sectionIdx + 1, sectionPath);
            var endIdx = nextIdx < 0 ? result.Count : nextIdx;

            var existingBody = string.Join("\n", result
                .Skip(sectionIdx + 1)
                .Take(endIdx - sectionIdx - 1)
                .Select(l => l.TrimEnd('\r')))
                .TrimEnd();

            var normalizedContent = string.Join("\n",
                content.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')));
            if (existingBody == normalizedContent)
                return (result, InstallStatus.AlreadyPresent);

            result.RemoveRange(sectionIdx + 1, endIdx - sectionIdx - 1);
            for (var i = contentLines.Length - 1; i >= 0; i--)
                result.Insert(sectionIdx + 1, contentLines[i]);
        }
        else
        {
            if (result.Count > 0 && result[^1].Trim().Length > 0)
                result.Add(string.Empty);
            result.Add(header);
            result.AddRange(contentLines);
        }

        return (result, InstallStatus.Installed);
    }

    private static async Task<InstallEntry> ExecuteEnsureCodexHooksFeatureAsync(
        InstallOperation.EnsureCodexHooksFeature op,
        bool dryRun,
        CancellationToken ct)
    {
        var description = $"Config features.hooks in {op.FilePath}";
        try
        {
            var dir = Path.GetDirectoryName(op.FilePath);
            if (dir is { Length: > 0 } && !Directory.Exists(dir))
            {
                if (!dryRun) Directory.CreateDirectory(dir);
            }

            var lines = File.Exists(op.FilePath)
                ? (await File.ReadAllLinesAsync(op.FilePath, ct)).ToList()
                : [];

            var (patched, changed) = EnsureCodexHooksFeature(lines);
            if (!changed)
                return new InstallEntry(description, InstallStatus.AlreadyPresent);

            if (!dryRun)
                await WriteAtomicAsync(op.FilePath, string.Join(Environment.NewLine, patched) + Environment.NewLine, ct);

            return new InstallEntry(description, InstallStatus.Installed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InstallEntry(description, InstallStatus.Error, ex.Message);
        }
    }

    private static async Task<InstallEntry> ExecuteWriteFileAsync(
        InstallOperation.WriteFile op,
        bool dryRun,
        CancellationToken ct)
    {
        var description = $"File {op.FilePath}";
        try
        {
            if (File.Exists(op.FilePath))
            {
                var current = await File.ReadAllTextAsync(op.FilePath, ct);
                if (current == op.Content)
                    return new InstallEntry(description, InstallStatus.AlreadyPresent);
            }

            var dir = Path.GetDirectoryName(op.FilePath);
            if (dir is { Length: > 0 } && !Directory.Exists(dir))
            {
                if (!dryRun) Directory.CreateDirectory(dir);
            }

            if (!dryRun)
                await WriteAtomicAsync(op.FilePath, op.Content, ct);

            return new InstallEntry(description, InstallStatus.Installed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InstallEntry(description, InstallStatus.Error, ex.Message);
        }
    }

    private static async Task<InstallEntry> ExecuteInjectLineAsync(
        InstallOperation.InjectLine op,
        bool dryRun,
        CancellationToken ct)
    {
        var description = $"Line in {op.FilePath}";
        try
        {
            if (File.Exists(op.FilePath))
            {
                var content = await File.ReadAllTextAsync(op.FilePath, ct);
                if (ContainsLine(content, op.Line))
                    return new InstallEntry(description, InstallStatus.AlreadyPresent);

                if (!dryRun)
                    await File.AppendAllTextAsync(op.FilePath, Environment.NewLine + op.Line + Environment.NewLine, ct);

                return new InstallEntry(description, InstallStatus.Installed);
            }

            if (!op.CreateIfMissing)
                return new InstallEntry(description, InstallStatus.Skipped, "File not found");

            if (!dryRun)
                await File.WriteAllTextAsync(op.FilePath, op.Line + Environment.NewLine, ct);

            return new InstallEntry(description, InstallStatus.Installed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InstallEntry(description, InstallStatus.Error, ex.Message);
        }
    }

    private static async Task<InstallEntry> ExecutePatchJsonObjectAsync(
        InstallOperation.PatchJsonObject op,
        bool dryRun,
        CancellationToken ct)
    {
        var description = $"{op.TopLevelKey}.{op.ObjectKey} in {op.FilePath}";
        try
        {
            var dir = Path.GetDirectoryName(op.FilePath);
            if (dir is { Length: > 0 } && !Directory.Exists(dir))
            {
                if (!dryRun) Directory.CreateDirectory(dir);
            }

            JsonNode root;
            if (File.Exists(op.FilePath))
            {
                var existing = await File.ReadAllTextAsync(op.FilePath, ct);
                try { root = JsonNode.Parse(existing) ?? new JsonObject(); }
                catch (JsonException ex) { return new InstallEntry(description, InstallStatus.Error, $"Invalid JSON: {ex.Message}"); }
            }
            else
            {
                root = new JsonObject();
            }

            var objectNode = JsonNode.Parse(op.ObjectJson);
            if (objectNode is null)
                return new InstallEntry(description, InstallStatus.Error, "Invalid object JSON in install plan");

            var topLevel = root[op.TopLevelKey];
            if (topLevel is JsonObject topObj && topObj[op.ObjectKey] is not null)
            {
                if (JsonNodesEqual(topObj[op.ObjectKey]!, objectNode))
                    return new InstallEntry(description, InstallStatus.AlreadyPresent);
            }

            if (root[op.TopLevelKey] is not JsonObject)
                root[op.TopLevelKey] = new JsonObject();

            ((JsonObject)root[op.TopLevelKey]!)[op.ObjectKey] = objectNode.DeepClone();

            if (!dryRun)
                await WriteAtomicAsync(op.FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);

            return new InstallEntry(description, InstallStatus.Installed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InstallEntry(description, InstallStatus.Error, ex.Message);
        }
    }

    private static bool IsHookAlreadyInstalled(JsonNode root, string eventName, string hookJson)
    {
        var hooks = root["hooks"];
        if (hooks is null) return false;

        var eventHooks = hooks[eventName];
        if (eventHooks is not JsonArray arr) return false;

        JsonNode? targetHook;
        try { targetHook = JsonNode.Parse(hookJson); }
        catch (JsonException) { return false; }

        if (targetHook is null) return false;

        foreach (var item in arr)
        {
            if (item is not null && JsonNodesEqual(item, targetHook))
                return true;
        }

        return false;
    }

    private static bool JsonNodesEqual(JsonNode a, JsonNode b)
    {
        if (a is JsonObject objA && b is JsonObject objB)
        {
            if (objA.Count != objB.Count) return false;
            foreach (var (key, valA) in objA)
            {
                if (!objB.TryGetPropertyValue(key, out var valB)) return false;
                if (valA is null && valB is null) continue;
                if (valA is null || valB is null) return false;
                if (!JsonNodesEqual(valA, valB)) return false;
            }
            return true;
        }

        if (a is JsonArray arrA && b is JsonArray arrB)
        {
            if (arrA.Count != arrB.Count) return false;
            for (var i = 0; i < arrA.Count; i++)
            {
                var elemA = arrA[i];
                var elemB = arrB[i];
                if (elemA is null && elemB is null) continue;
                if (elemA is null || elemB is null) return false;
                if (!JsonNodesEqual(elemA, elemB)) return false;
            }
            return true;
        }

        if (a is JsonValue && b is JsonValue)
            return a.ToJsonString() == b.ToJsonString();

        return false;
    }

    private static void AddHookToJson(JsonNode root, string eventName, JsonNode hookNode)
    {
        var hooks = root["hooks"];
        if (hooks is null)
        {
            var hooksObj = new JsonObject();
            root["hooks"] = hooksObj;
            hooks = hooksObj;
        }

        var eventHooks = hooks[eventName];
        if (eventHooks is not JsonArray arr)
        {
            arr = [];
            hooks[eventName] = arr;
        }

        arr.Add(hookNode.DeepClone());
    }

    private static bool IsTomlKeyPresent(List<string> lines, string section, string key, string value)
    {
        var inSection = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == $"[{section}]") { inSection = true; continue; }
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) { inSection = false; continue; }
            if (inSection && trimmed == $"{key} = {value}") return true;
        }
        return false;
    }

    private static List<string> PatchTomlSection(List<string> lines, string section, string key, string value)
    {
        var result = new List<string>(lines);
        var sectionHeader = $"[{section}]";
        var keyLine = $"{key} = {value}";
        var keyAssignment = $"{key} =";

        var sectionIndex = result.FindIndex(l => l.Trim() == sectionHeader);
        if (sectionIndex < 0)
        {
            if (result.Count > 0 && result[^1].Trim().Length > 0)
                result.Add(string.Empty);
            result.Add(sectionHeader);
            result.Add(keyLine);
        }
        else
        {
            var i = sectionIndex + 1;
            while (i < result.Count && !result[i].Trim().StartsWith('['))
            {
                if (result[i].Trim().StartsWith(keyAssignment))
                {
                    result[i] = keyLine;
                    return result;
                }
                i++;
            }
            result.Insert(sectionIndex + 1, keyLine);
        }

        return result;
    }

    private static (List<string> Lines, bool Changed) EnsureCodexHooksFeature(List<string> lines)
    {
        var result = new List<string>(lines);
        var layout = InspectCodexHooksFeatureLayout(result);
        var changed = false;

        if (layout.FeaturesHooksIndex is int hooksIndex)
        {
            var replacement = RewriteCodexHooksLine(result[hooksIndex]);
            if (result[hooksIndex] != replacement)
            {
                result[hooksIndex] = replacement;
                changed = true;
            }
        }

        if (layout.FeaturesCodexHooksIndex is int codexHooksIndex)
        {
            if (layout.FeaturesHooksIndex is null)
            {
                result[codexHooksIndex] = RewriteCodexHooksLine(result[codexHooksIndex]);
                changed = true;
            }
            else if (result[codexHooksIndex].Length > 0)
            {
                result[codexHooksIndex] = string.Empty;
                changed = true;
            }
        }

        foreach (var index in layout.StrayAssignmentIndices)
        {
            if (result[index].Length > 0)
            {
                result[index] = string.Empty;
                changed = true;
            }
        }

        if (layout.FeaturesHooksIndex is null && layout.FeaturesCodexHooksIndex is null)
        {
            InsertCodexHooksFeature(result, layout.FeaturesInsertIndex);
            changed = true;
        }

        return (CompactBlankLines(result), changed);
    }

    private sealed record CodexHooksFeatureLayout(
        int? FeaturesInsertIndex,
        int? FeaturesHooksIndex,
        int? FeaturesCodexHooksIndex,
        IReadOnlyList<int> StrayAssignmentIndices);

    private static CodexHooksFeatureLayout InspectCodexHooksFeatureLayout(List<string> lines)
    {
        int? featuresInsertIndex = null;
        int? featuresHooksIndex = null;
        int? featuresCodexHooksIndex = null;
        var strayAssignmentIndices = new List<int>();
        string? currentSection = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (TryParseTomlSection(trimmed, out var section))
            {
                currentSection = section;
                if (section == "features")
                    featuresInsertIndex = i + 1;
                continue;
            }

            if (currentSection == "features")
                featuresInsertIndex = i + 1;

            var assignment = GetCodexHooksAssignmentName(trimmed);
            if (assignment is null)
                continue;

            if (currentSection == "features" && assignment == "hooks" && featuresHooksIndex is null)
                featuresHooksIndex = i;
            else if (currentSection == "features" && assignment == "codex_hooks" && featuresCodexHooksIndex is null)
                featuresCodexHooksIndex = i;
            else
                strayAssignmentIndices.Add(i);
        }

        return new CodexHooksFeatureLayout(
            featuresInsertIndex,
            featuresHooksIndex,
            featuresCodexHooksIndex,
            strayAssignmentIndices);
    }

    private static void InsertCodexHooksFeature(List<string> lines, int? insertIndex)
    {
        if (insertIndex is int index)
        {
            while (index > 0 && lines[index - 1].Trim().Length == 0)
                index--;
            lines.Insert(index, "hooks = true");
            return;
        }

        if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            lines.Add(string.Empty);

        lines.Add("[features]");
        lines.Add("hooks = true");
    }

    private static bool TryParseTomlSection(string trimmed, out string section)
    {
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            section = trimmed.TrimStart('[').TrimEnd(']').Trim();
            return true;
        }

        section = string.Empty;
        return false;
    }

    private static string? GetCodexHooksAssignmentName(string trimmed)
    {
        var withoutComment = trimmed.Split('#')[0].Trim();
        if (IsTomlAssignment(withoutComment, "codex_hooks"))
            return "codex_hooks";
        if (IsTomlAssignment(withoutComment, "hooks"))
            return "hooks";
        return null;
    }

    private static bool IsTomlAssignment(string line, string key) =>
        line.StartsWith(key, StringComparison.Ordinal) &&
        line[key.Length..].TrimStart().StartsWith("=", StringComparison.Ordinal);

    private static string RewriteCodexHooksLine(string line)
    {
        var indentLength = line.TakeWhile(char.IsWhiteSpace).Count();
        var indent = line[..indentLength];
        var comment = line.IndexOf('#') is var commentIndex && commentIndex >= 0
            ? line[commentIndex..].TrimEnd()
            : null;

        return string.IsNullOrWhiteSpace(comment)
            ? $"{indent}hooks = true"
            : $"{indent}hooks = true  {comment}";
    }

    private static List<string> CompactBlankLines(List<string> lines)
    {
        var compacted = new List<string>(lines.Count);
        var previousBlank = false;
        foreach (var line in lines)
        {
            var isBlank = line.Trim().Length == 0;
            if (isBlank && previousBlank)
                continue;
            compacted.Add(line);
            previousBlank = isBlank;
        }

        while (compacted.Count > 0 && compacted[^1].Trim().Length == 0)
            compacted.RemoveAt(compacted.Count - 1);

        return compacted;
    }

    private static async Task<InstallEntry> ExecuteInjectFencedBlockAsync(
        InstallOperation.InjectFencedBlock op,
        bool dryRun,
        CancellationToken ct)
    {
        var description = $"CLAUDE.md block [{op.Marker}] in {op.FilePath}";
        try
        {
            var blockStart = $"<!-- {op.Marker} -->";
            var blockEnd = $"<!-- /{op.Marker} -->";
            var block = $"{blockStart}\n{op.Content.Trim()}\n{blockEnd}";

            var dir = Path.GetDirectoryName(op.FilePath);
            if (dir is { Length: > 0 } && !Directory.Exists(dir))
            {
                if (!dryRun) Directory.CreateDirectory(dir);
            }

            string existing = "";
            if (File.Exists(op.FilePath))
                existing = await File.ReadAllTextAsync(op.FilePath, ct);
            else if (!op.CreateIfMissing)
                return new InstallEntry(description, InstallStatus.Skipped, "File not found");

            if (existing.Contains(blockStart))
            {
                var startIdx = existing.IndexOf(blockStart, StringComparison.Ordinal);
                var endIdx = existing.IndexOf(blockEnd, startIdx, StringComparison.Ordinal);
                if (endIdx >= 0)
                {
                    var currentBlock = existing[startIdx..(endIdx + blockEnd.Length)];
                    if (currentBlock == block)
                        return new InstallEntry(description, InstallStatus.AlreadyPresent);

                    var updated = existing[..startIdx] + block + existing[(endIdx + blockEnd.Length)..];
                    if (!dryRun) await WriteAtomicAsync(op.FilePath, updated.Trim() + "\n", ct);
                    return new InstallEntry(description, InstallStatus.Installed);
                }
            }

            var appended = existing.Trim().Length == 0
                ? block + "\n"
                : existing.TrimEnd() + "\n\n" + block + "\n";

            if (!dryRun) await WriteAtomicAsync(op.FilePath, appended, ct);
            return new InstallEntry(description, InstallStatus.Installed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InstallEntry(description, InstallStatus.Error, ex.Message);
        }
    }

    private static bool ContainsLine(string content, string line) =>
        content.Split('\n').Any(l => l.Trim() == line.Trim());

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }
}
