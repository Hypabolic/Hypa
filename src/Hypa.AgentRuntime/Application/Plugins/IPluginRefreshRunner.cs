namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
/// Runs one pull-refresh command to completion. One timeout. One output size cap.
/// </summary>
public interface IPluginRefreshRunner
{
    PluginProcessExit Run(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        int outputCapBytes,
        TimeSpan timeout);
}
