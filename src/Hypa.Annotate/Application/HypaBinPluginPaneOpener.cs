using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Open a plugin popup through <c>HYPA_BIN_PATH plugin pane open</c>.
/// </summary>
public sealed class HypaBinPluginPaneOpener : IPluginPaneOpener
{
    public const int EditorWidth = 88;

    public const int EditorHeight = 24;

    public const int ManagerWidth = 100;

    public const int ManagerHeight = 30;

    public Task<Result<Unit, string>> OpenEditorPopupAsync(
        PluginPaneOpenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(Open(
            "editor",
            request.PluginRoot,
            request.TargetPaneId,
            EditorWidth,
            EditorHeight,
            AnnotateEnv.PendingPath,
            request.PendingFilePath));
    }

    public Task<Result<Unit, string>> OpenLastReviewPopupAsync(
        PluginPaneOpenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(Open(
            "last-review",
            request.PluginRoot,
            request.TargetPaneId,
            EditorWidth,
            EditorHeight,
            AnnotateEnv.LastReviewPath,
            request.PendingFilePath));
    }

    public Task<Result<Unit, string>> OpenManagerPopupAsync(
        PluginManagerPaneOpenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(Open(
            "manager",
            request.PluginRoot,
            request.TargetPaneId,
            ManagerWidth,
            ManagerHeight,
            envName: null,
            envValue: null));
    }

    internal static Result<Unit, string> Open(
        string entrypoint,
        string pluginRoot,
        string targetPaneId,
        int width,
        int height,
        string? envName,
        string? envValue)
    {
        var pluginId = Environment.GetEnvironmentVariable(AnnotateEnv.PluginId);
        if (string.IsNullOrWhiteSpace(pluginId))
            pluginId = AnnotateEnv.AnnotatePluginId;

        var arguments = BuildOpenArguments(
            pluginId,
            entrypoint,
            pluginRoot,
            targetPaneId,
            width,
            height,
            envName,
            envValue);
        var ran = HypaBinCli.Run(arguments);
        return ran.IsOk
            ? Result<Unit, string>.Ok(default)
            : Result<Unit, string>.Fail(ran.Error);
    }

    internal static List<string> BuildOpenArguments(
        string pluginId,
        string entrypoint,
        string pluginRoot,
        string targetPaneId,
        int width,
        int height,
        string? envName,
        string? envValue)
    {
        var arguments = new List<string>
        {
            "plugin",
            "pane",
            "open",
            pluginId,
            entrypoint,
            "--placement",
            "popup",
            "--width",
            width.ToString(),
            "--height",
            height.ToString(),
            "--cwd",
            pluginRoot,
            "--focus",
        };
        if (!string.IsNullOrWhiteSpace(targetPaneId))
        {
            arguments.Add("--target-pane");
            arguments.Add(targetPaneId);
        }

        if (!string.IsNullOrWhiteSpace(envName) && envValue is not null)
        {
            arguments.Add("--env");
            arguments.Add(envName + "=" + envValue);
        }

        return arguments;
    }
}
