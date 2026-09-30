namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Live mux attach after the CLI resolves an agent pane.
// / <c>run_terminal_attach</c>.
/// lines 8-15 enters the client. Hypa reuses the attach driver.
/// </summary>
public interface ILiveAttachHost
{
    Task<int> AttachToPaneAsync(
        string paneId,
        string? sessionOption,
        string? socketOverride,
        bool sessionOptionWasSet,
        CancellationToken ct);
}
