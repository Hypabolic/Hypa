using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Infrastructure.Hooks;

public sealed class HookUninstaller : IHookUninstaller
{
    private static readonly HashSet<string> SharedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "settings.json", "settings.local.json", "CLAUDE.md", "AGENTS.md", "config.toml", "hooks.json",
    };

    public async Task<UninstallReport> UninstallAsync(
        UninstallPlan plan,
        string harnessKey,
        bool dryRun,
        CancellationToken ct = default)
    {
        var entries = new List<UninstallEntry>();
        var backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var op in plan.Operations)
        {
            var entry = op switch
            {
                UninstallOperation.RemoveJsonHook rjh => await ExecuteRemoveJsonHookAsync(rjh, dryRun, backedUp, ct),
                UninstallOperation.RemoveTomlKey rtk => await ExecuteRemoveTomlKeyAsync(rtk, dryRun, backedUp, ct),
                UninstallOperation.DeleteFile df => ExecuteDeleteFile(df, dryRun),
                UninstallOperation.DeleteDirectory dd => ExecuteDeleteDirectory(dd, dryRun),
                UninstallOperation.RemoveLine rl => await ExecuteRemoveLineAsync(rl, dryRun, backedUp, ct),
                UninstallOperation.RemoveJsonObject rjo => await ExecuteRemoveJsonObjectAsync(rjo, dryRun, backedUp, ct),
                UninstallOperation.RemoveFencedBlock rfb => await ExecuteRemoveFencedBlockAsync(rfb, dryRun, backedUp, ct),
                UninstallOperation.NotSupported ns => new UninstallEntry(ns.Message, UninstallStatus.Skipped),
                _ => new UninstallEntry("Unknown operation", UninstallStatus.Error, "Unrecognised operation type"),
            };
            entries.Add(entry);
        }

        return new UninstallReport(harnessKey, entries);
    }

    private static async Task<UninstallEntry> ExecuteRemoveJsonHookAsync(
        UninstallOperation.RemoveJsonHook op,
        bool dryRun,
        HashSet<string> backedUp,
        CancellationToken ct)
    {
        var description = $"Hook removed from {op.FilePath}";
        try
        {
            if (!File.Exists(op.FilePath))
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            var existing = await File.ReadAllTextAsync(op.FilePath, ct);
            JsonNode root;
            try { root = JsonNode.Parse(existing) ?? new JsonObject(); }
            catch (JsonException ex) { return new UninstallEntry(description, UninstallStatus.Error, $"Invalid JSON: {ex.Message}"); }

            var hookNode = JsonNode.Parse(op.HookJson);
            if (hookNode is null)
                return new UninstallEntry(description, UninstallStatus.Error, "Invalid hook JSON in uninstall plan");

            var hooks = root["hooks"];
            if (hooks is null)
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            var eventHooks = hooks[op.HookEventName];
            if (eventHooks is not JsonArray arr)
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            var idx = FindMatchingHook(arr, hookNode);
            if (idx < 0)
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            var backupDetail = await BackupIfNeededAsync(op.FilePath, dryRun, backedUp, ct);

            arr.RemoveAt(idx);

            if (arr.Count == 0)
                ((JsonObject)hooks).Remove(op.HookEventName);

            if (hooks is JsonObject hooksObj && hooksObj.Count == 0)
                ((JsonObject)root).Remove("hooks");

            if (!dryRun)
                await WriteAtomicAsync(op.FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);

            return new UninstallEntry(description, UninstallStatus.Removed, backupDetail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UninstallEntry(description, UninstallStatus.Error, ex.Message);
        }
    }

    private static async Task<UninstallEntry> ExecuteRemoveTomlKeyAsync(
        UninstallOperation.RemoveTomlKey op,
        bool dryRun,
        HashSet<string> backedUp,
        CancellationToken ct)
    {
        var description = $"Config {op.Section}.{op.Key} removed from {op.FilePath}";
        try
        {
            if (!File.Exists(op.FilePath))
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            var lines = (await File.ReadAllLinesAsync(op.FilePath, ct)).ToList();
            var (patched, changed) = RemoveTomlKey(lines, op.Section, op.Key);
            if (!changed)
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            var backupDetail = await BackupIfNeededAsync(op.FilePath, dryRun, backedUp, ct);

            if (!dryRun)
                await WriteAtomicAsync(op.FilePath, string.Join(Environment.NewLine, patched) + Environment.NewLine, ct);

            return new UninstallEntry(description, UninstallStatus.Removed, backupDetail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UninstallEntry(description, UninstallStatus.Error, ex.Message);
        }
    }

    private static UninstallEntry ExecuteDeleteFile(UninstallOperation.DeleteFile op, bool dryRun)
    {
        var description = $"{op.FilePath} deleted";
        try
        {
            if (!File.Exists(op.FilePath))
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            if (!dryRun)
                File.Delete(op.FilePath);

            return new UninstallEntry(description, UninstallStatus.Removed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UninstallEntry(description, UninstallStatus.Error, ex.Message);
        }
    }

    private static UninstallEntry ExecuteDeleteDirectory(UninstallOperation.DeleteDirectory op, bool dryRun)
    {
        var description = $"{op.DirectoryPath} deleted";
        try
        {
            if (!Directory.Exists(op.DirectoryPath))
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            if (!dryRun)
                Directory.Delete(op.DirectoryPath, recursive: true);

            return new UninstallEntry(description, UninstallStatus.Removed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UninstallEntry(description, UninstallStatus.Error, ex.Message);
        }
    }

    private static async Task<UninstallEntry> ExecuteRemoveLineAsync(
        UninstallOperation.RemoveLine op,
        bool dryRun,
        HashSet<string> backedUp,
        CancellationToken ct)
    {
        var description = $"Line removed from {op.FilePath}";
        try
        {
            if (!File.Exists(op.FilePath))
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            var content = await File.ReadAllTextAsync(op.FilePath, ct);
            var lines = content.Split('\n').ToList();
            var lineCount = lines.Count;
            // Use full Trim() to match install detection (ContainsLine uses Trim())
            lines = lines.Where(l => l.Trim() != op.Line.Trim()).ToList();

            if (lines.Count == lineCount)
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            var backupDetail = await BackupIfNeededAsync(op.FilePath, dryRun, backedUp, ct);
            var result = CollapseBlankLines(string.Join("\n", lines));

            if (!dryRun)
            {
                if (result.Trim().Length == 0)
                    File.Delete(op.FilePath);
                else
                    await WriteAtomicAsync(op.FilePath, result, ct);
            }

            return new UninstallEntry(description, UninstallStatus.Removed, backupDetail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UninstallEntry(description, UninstallStatus.Error, ex.Message);
        }
    }

    private static async Task<UninstallEntry> ExecuteRemoveJsonObjectAsync(
        UninstallOperation.RemoveJsonObject op,
        bool dryRun,
        HashSet<string> backedUp,
        CancellationToken ct)
    {
        var description = $"{op.TopLevelKey}.{op.ObjectKey} removed from {op.FilePath}";
        try
        {
            if (!File.Exists(op.FilePath))
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            var existing = await File.ReadAllTextAsync(op.FilePath, ct);
            JsonNode root;
            try { root = JsonNode.Parse(existing) ?? new JsonObject(); }
            catch (JsonException ex) { return new UninstallEntry(description, UninstallStatus.Error, $"Invalid JSON: {ex.Message}"); }

            if (root[op.TopLevelKey] is not JsonObject topObj)
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            if (!topObj.ContainsKey(op.ObjectKey))
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            var backupDetail = await BackupIfNeededAsync(op.FilePath, dryRun, backedUp, ct);

            topObj.Remove(op.ObjectKey);

            if (topObj.Count == 0)
                ((JsonObject)root).Remove(op.TopLevelKey);

            if (!dryRun)
                await WriteAtomicAsync(op.FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);

            return new UninstallEntry(description, UninstallStatus.Removed, backupDetail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UninstallEntry(description, UninstallStatus.Error, ex.Message);
        }
    }

    private static async Task<UninstallEntry> ExecuteRemoveFencedBlockAsync(
        UninstallOperation.RemoveFencedBlock op,
        bool dryRun,
        HashSet<string> backedUp,
        CancellationToken ct)
    {
        var description = $"Hypa block removed from {op.FilePath}";
        try
        {
            if (!File.Exists(op.FilePath))
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            var content = await File.ReadAllTextAsync(op.FilePath, ct);
            var blockStart = $"<!-- {op.Marker} -->";
            var blockEnd = $"<!-- /{op.Marker} -->";

            var startIdx = content.IndexOf(blockStart, StringComparison.Ordinal);
            if (startIdx < 0)
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            var endIdx = content.IndexOf(blockEnd, startIdx, StringComparison.Ordinal);
            if (endIdx < 0)
                return new UninstallEntry(description, UninstallStatus.NotPresent);

            var backupDetail = await BackupIfNeededAsync(op.FilePath, dryRun, backedUp, ct);

            var before = content[..startIdx];
            var after = content[(endIdx + blockEnd.Length)..];
            var result = CollapseBlankLines(before + after);

            if (!dryRun)
                await WriteAtomicAsync(op.FilePath, result.Trim().Length == 0 ? "" : result.TrimEnd() + "\n", ct);

            return new UninstallEntry(description, UninstallStatus.Removed, backupDetail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UninstallEntry(description, UninstallStatus.Error, ex.Message);
        }
    }

    private static async Task<string?> BackupIfNeededAsync(
        string path,
        bool dryRun,
        HashSet<string> backedUp,
        CancellationToken ct)
    {
        var fileName = Path.GetFileName(path);
        if (!SharedFiles.Contains(fileName))
            return null;

        if (!backedUp.Add(path))
            return null;

        var bakPath = path + ".hypa.bak";
        if (!dryRun && !File.Exists(bakPath))
            await File.WriteAllBytesAsync(bakPath, await File.ReadAllBytesAsync(path, ct), ct);

        return $"backup: {bakPath}";
    }

    private static int FindMatchingHook(JsonArray arr, JsonNode target)
    {
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not null && JsonNodesEqual(arr[i]!, target))
                return i;
        }
        return -1;
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
                var ea = arrA[i];
                var eb = arrB[i];
                if (ea is null && eb is null) continue;
                if (ea is null || eb is null) return false;
                if (!JsonNodesEqual(ea, eb)) return false;
            }
            return true;
        }

        if (a is JsonValue && b is JsonValue)
            return a.ToJsonString() == b.ToJsonString();

        return false;
    }

    private static (List<string> Lines, bool Changed) RemoveTomlKey(List<string> lines, string section, string key)
    {
        var result = new List<string>(lines);
        var sectionHeader = $"[{section}]";
        var keyAssignment = $"{key} =";

        var sectionIndex = result.FindIndex(l => l.Trim() == sectionHeader);
        if (sectionIndex < 0) return (result, false);

        var i = sectionIndex + 1;
        var keyIndex = -1;
        while (i < result.Count && !result[i].Trim().StartsWith('['))
        {
            if (result[i].Trim().StartsWith(keyAssignment))
            {
                keyIndex = i;
                break;
            }
            i++;
        }

        if (keyIndex < 0) return (result, false);

        result.RemoveAt(keyIndex);

        // Remove the section header if no non-blank, non-comment key lines remain
        var sectionHasKeys = false;
        var j = sectionIndex + 1;
        while (j < result.Count && !result[j].Trim().StartsWith('['))
        {
            var trimmed = result[j].Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                sectionHasKeys = true;
                break;
            }
            j++;
        }

        if (!sectionHasKeys)
            result.RemoveAt(sectionIndex);

        return (result, true);
    }

    private static string CollapseBlankLines(string content)
    {
        var lines = content.Split('\n');
        var result = new List<string>();
        var prevBlank = false;
        foreach (var line in lines)
        {
            var isBlank = line.Trim('\r').Length == 0;
            if (isBlank && prevBlank) continue;
            result.Add(line);
            prevBlank = isBlank;
        }
        return string.Join("\n", result);
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }
}
