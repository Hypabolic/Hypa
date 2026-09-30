using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
// / Plugin-only worktree lifecycle JSON.
/// <c>src/api/schema/events.rs:455-470</c>. Journal payloads stay redacted.
/// </summary>
public static class PluginWorktreeEventJson
{
    public static string Write(
        string workspaceId,
        string? workspaceLabel,
        PluginWorktreeContext? nestedWorktree,
        WorktreeInfo row,
        bool? alreadyOpen,
        bool? forced)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrEmpty(workspaceId))
                writer.WriteString("workspace_id", workspaceId);
            writer.WriteStartObject("workspace");
            if (!string.IsNullOrEmpty(workspaceId))
                writer.WriteString("workspace_id", workspaceId);
            if (!string.IsNullOrEmpty(workspaceLabel))
                writer.WriteString("label", workspaceLabel);
            WriteNestedWorktree(writer, nestedWorktree);
            writer.WriteEndObject();
            writer.WriteStartObject("worktree");
            writer.WriteString("path", row.Path);
            if (!string.IsNullOrEmpty(row.Branch))
                writer.WriteString("branch", row.Branch);
            writer.WriteBoolean("is_bare", row.IsBare);
            writer.WriteBoolean("is_detached", row.IsDetached);
            writer.WriteBoolean("is_prunable", row.IsPrunable);
            writer.WriteBoolean("is_linked_worktree", row.IsLinkedWorktree);
            if (!string.IsNullOrEmpty(row.OpenWorkspaceId))
                writer.WriteString("open_workspace_id", row.OpenWorkspaceId);
            writer.WriteString("label", row.Label);
            writer.WriteEndObject();
            if (alreadyOpen is { } alreadyOpenValue)
                writer.WriteBoolean("already_open", alreadyOpenValue);
            if (forced is { } forcedValue)
                writer.WriteBoolean("forced", forcedValue);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string? ReadWorkspaceId(string eventJson)
    {
        if (!TryRoot(eventJson, out var root))
            return null;
        var id = ReadString(root, "workspace_id");
        if (!string.IsNullOrEmpty(id))
            return id;
        if (root.TryGetProperty("workspace", out var workspace)
            && workspace.ValueKind == JsonValueKind.Object)
        {
            return ReadString(workspace, "workspace_id");
        }

        return null;
    }

    public static string? ReadWorkspaceLabel(string eventJson)
    {
        if (!TryRoot(eventJson, out var root))
            return null;
        if (root.TryGetProperty("workspace", out var workspace)
            && workspace.ValueKind == JsonValueKind.Object)
        {
            var label = ReadString(workspace, "label");
            if (!string.IsNullOrEmpty(label))
                return label;
        }

        if (root.TryGetProperty("worktree", out var row)
            && row.ValueKind == JsonValueKind.Object)
        {
            return ReadString(row, "label");
        }

        return null;
    }

    public static PluginWorktreeContext? ReadNestedWorktree(string eventJson)
    {
        if (!TryRoot(eventJson, out var root)
            || !root.TryGetProperty("workspace", out var workspace)
            || workspace.ValueKind != JsonValueKind.Object
            || !workspace.TryGetProperty("worktree", out var nested)
            || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!nested.TryGetProperty("is_linked_worktree", out var linkedEl)
            || (linkedEl.ValueKind != JsonValueKind.True && linkedEl.ValueKind != JsonValueKind.False))
        {
            return null;
        }

        var mapped = new PluginWorktreeContext
        {
            RepoKey = ReadString(nested, "repo_key") ?? "",
            RepoName = ReadString(nested, "repo_name") ?? "",
            RepoRoot = ReadString(nested, "repo_root") ?? "",
            CheckoutPath = ReadString(nested, "checkout_path") ?? "",
            IsLinkedWorktree = linkedEl.GetBoolean(),
        };
        return mapped;
    }

    public static string? ReadWorktreePath(string eventJson)
    {
        if (!TryRoot(eventJson, out var root)
            || !root.TryGetProperty("worktree", out var row)
            || row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(row, "path");
    }

    private static void WriteNestedWorktree(Utf8JsonWriter writer, PluginWorktreeContext? worktree)
    {
        if (worktree is null)
            return;

        writer.WriteStartObject("worktree");
        writer.WriteString("repo_key", worktree.RepoKey);
        writer.WriteString("repo_name", worktree.RepoName);
        writer.WriteString("repo_root", worktree.RepoRoot);
        writer.WriteString("checkout_path", worktree.CheckoutPath);
        writer.WriteBoolean("is_linked_worktree", worktree.IsLinkedWorktree);
        writer.WriteEndObject();
    }

    private static bool TryRoot(string eventJson, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(eventJson))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(eventJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
