using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
/// Redacts invocation context to pane-title level. Omits server-derived
/// selected_text. Program name, not full argv. Cap 16 KiB.
/// </summary>
public static class PluginContextJson
{
    public static PluginResult<string> Serialize(PluginInvocationContext context)
    {
        var redacted = context with { SelectedText = null };
        var json = Write(redacted, includeOptional: true);
        if (Encoding.UTF8.GetByteCount(json) <= PluginCommandLimits.ContextJsonMaxBytes)
            return PluginResult<string>.Ok(json);

        json = Write(redacted with
        {
            WorkspaceLabel = null,
            TabLabel = null,
            WorkspaceCwd = null,
            FocusedPaneCwd = null,
        }, includeOptional: false);
        if (Encoding.UTF8.GetByteCount(json) <= PluginCommandLimits.ContextJsonMaxBytes)
            return PluginResult<string>.Ok(json);

        return PluginResult<string>.Fail(
            PluginError.InvalidContext,
            "plugin context exceeds 16 KiB");
    }

    private static string Write(PluginInvocationContext context, bool includeOptional)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteString(writer, "workspace_id", context.WorkspaceId);
            if (includeOptional)
            {
                WriteString(writer, "workspace_label", context.WorkspaceLabel);
                WriteString(writer, "workspace_cwd", context.WorkspaceCwd);
            }

            WriteWorktree(writer, context.Worktree);
            WriteString(writer, "tab_id", context.TabId);
            if (includeOptional)
                WriteString(writer, "tab_label", context.TabLabel);
            WriteString(writer, "focused_pane_id", context.FocusedPaneId);
            if (includeOptional)
                WriteString(writer, "focused_pane_cwd", context.FocusedPaneCwd);
            WriteString(writer, "focused_pane_agent", context.FocusedPaneAgent);
            WriteString(writer, "focused_pane_status", context.FocusedPaneStatus);
            WriteAgentSession(writer, context.AgentSession);
            WriteString(writer, "program_name", context.ProgramName);
            WriteString(writer, "invocation_source", context.InvocationSource);
            WriteString(writer, "correlation_id", context.CorrelationId);
            WriteString(writer, "clicked_url", context.ClickedUrl);
            WriteString(writer, "link_handler_id", context.LinkHandlerId);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        writer.WriteString(name, value);
    }

    private static void WriteWorktree(Utf8JsonWriter writer, PluginWorktreeContext? worktree)
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

    private static void WriteAgentSession(Utf8JsonWriter writer, NativeAgentSessionRef? session)
    {
        if (session is null || !PluginInvocationContextSession.IsValid(session))
            return;

        writer.WriteStartObject("agent_session");
        writer.WriteString("kind", session.Kind);
        writer.WriteString("value", session.Value);
        writer.WriteString("source", session.Source);
        writer.WriteString("agent", session.Agent);
        WriteString(writer, "session_start_source", session.SessionStartSource);
        writer.WriteEndObject();
    }
}
