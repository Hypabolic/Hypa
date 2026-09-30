using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.Cli.Attach.Keys;

/// <summary>Client chrome policy from <c>ui.*</c>. Default is passthrough for existing tests.</summary>
public sealed record AttachChromePolicy(
    bool ConfirmClose = false,
    bool PromptNewTabName = false,
    bool PromptNewWorkspaceName = false)
{
    public static AttachChromePolicy Passthrough { get; } = new();

    public static AttachChromePolicy FromUi(AttachUiConfig ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        return new AttachChromePolicy(
            ui.ConfirmClose,
            ui.PromptNewTabName,
            ui.PromptNewWorkspaceName);
    }
}
