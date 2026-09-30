using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Send review text through <c>HYPA_BIN_PATH plugin pane send-text</c>.
/// The host appends one newline. Do not append a second newline.
/// </summary>
public sealed class HypaBinPluginPaneSender : IPluginPaneSender
{
    public Task<Result<Unit, string>> SendTextAsync(
        PluginPaneSendTextRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ran = HypaBinCli.Run(
            BuildSendTextArguments(request.TargetPaneId, request.Text));
        return Task.FromResult(
            ran.IsOk
                ? Result<Unit, string>.Ok(default)
                : Result<Unit, string>.Fail(ran.Error));
    }

    internal static List<string> BuildSendTextArguments(string paneId, string text) =>
    [
        "plugin",
        "pane",
        "send-text",
        paneId,
        "--text",
        text,
    ];
}
