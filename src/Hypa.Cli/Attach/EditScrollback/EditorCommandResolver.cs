using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.EditScrollback;

internal sealed record EditorLaunch(
    string Command,
    IReadOnlyList<string> Args,
    string Editor);

/// <summary>
/// Resolves <c>$VISUAL</c> then <c>$EDITOR</c> into an explicit
/// <c>/bin/sh -c</c> eval wrap. Does not default to vi.
/// </summary>
internal static class EditorCommandResolver
{
    public const string Shell = "/bin/sh";

    public const string UnsetError = "VISUAL/EDITOR unset";

    public static bool IsSet(IAttachConfigEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return FirstNonEmpty(env.GetVariable("VISUAL"), env.GetVariable("EDITOR")) is not null;
    }

    public static bool TryResolve(
        IAttachConfigEnvironment env,
        string filePath,
        out EditorLaunch launch,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(env);
        launch = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "scrollback file missing";
            return false;
        }

        var editor = FirstNonEmpty(
            env.GetVariable("VISUAL"),
            env.GetVariable("EDITOR"));
        if (editor is null)
        {
            error = UnsetError;
            return false;
        }

        var script = string.Concat(
            "eval ",
            Quote(editor),
            " ",
            Quote(filePath),
            "; status=$?; rm -f -- ",
            Quote(filePath),
            "; exit $status");
        launch = new EditorLaunch(Shell, ["-c", script], editor);
        return true;
    }

    private static string? FirstNonEmpty(string? visual, string? editor)
    {
        if (!string.IsNullOrWhiteSpace(visual))
            return visual.Trim();
        if (!string.IsNullOrWhiteSpace(editor))
            return editor.Trim();
        return null;
    }

    internal static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Concat("'", value.Replace("'", "'\\''", StringComparison.Ordinal), "'");
    }
}
