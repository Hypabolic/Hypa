using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>Template values for occupant manifest expansion.</summary>
public sealed record OccupantTemplateContext
{
    /// <summary>Workspace / pane cwd.</summary>
    public required string Workspace { get; init; }

    /// <summary>Isolated cube HOME. Not the laptop HOME by default.</summary>
    public required string CubeHome { get; init; }

    /// <summary>
    /// Unique live-resume attempt. Empty means expand mints a new id.
    /// A fixed manifest value is not sufficient.
    /// </summary>
    public string ResumeAttemptId { get; init; } = "";
}

/// <summary>Expands <c>{workspace}</c>, <c>{cube_home}</c>, and <c>{resume_attempt_id}</c>.</summary>
public static class OccupantTemplateExpander
{
    public const string WorkspaceToken = "{workspace}";
    public const string CubeHomeToken = "{cube_home}";
    public const string ResumeAttemptIdToken = "{resume_attempt_id}";

    public static string Expand(string template, OccupantTemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        var attempt = ResolveAttemptId(context);
        return template
            .Replace(WorkspaceToken, context.Workspace, StringComparison.Ordinal)
            .Replace(CubeHomeToken, context.CubeHome, StringComparison.Ordinal)
            .Replace(ResumeAttemptIdToken, attempt, StringComparison.Ordinal);
    }

    public static OccupantManifest Expand(OccupantManifest manifest, OccupantTemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(context);

        var bound = context with { ResumeAttemptId = ResolveAttemptId(context) };
        var command = new string[manifest.Command.Count];
        for (var i = 0; i < manifest.Command.Count; i++)
            command[i] = Expand(manifest.Command[i], bound);

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in manifest.Env)
            env[kv.Key] = Expand(kv.Value, bound);

        return manifest with
        {
            Command = command,
            Cwd = Expand(manifest.Cwd, bound),
            Env = env,
            TranscriptRoot = Expand(manifest.TranscriptRoot, bound),
        };
    }

    private static string ResolveAttemptId(OccupantTemplateContext context)
    {
        if (OccupantResumeReporter.IsAttemptId(context.ResumeAttemptId))
            return context.ResumeAttemptId.Trim();
        return OccupantResumeReporter.NewAttemptId();
    }
}
