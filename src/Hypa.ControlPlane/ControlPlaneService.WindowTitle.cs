using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.WindowTitle;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal const int WindowTitleMax = WindowTitleExpander.MaxCols;

    internal string? WindowTitleOverride
    {
        get
        {
            lock (_windowTitleGate)
                return _windowTitleOverride;
        }
    }

    internal Task<JsonElement> HandleWindowTitleSetAsync(
        WindowTitleSetParams p, CancellationToken ct) =>
        WindowTitleSetAsync(p, ct);

    internal Task<JsonElement> HandleWindowTitleClearAsync(
        EmptyParams _, CancellationToken ct) =>
        WindowTitleClearAsync(ct);

    private async Task<JsonElement> WindowTitleSetAsync(
        WindowTitleSetParams p,
        CancellationToken ct)
    {
        var title = NormalizeWindowTitle(p.Title);
        if (string.IsNullOrEmpty(title))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "title is required");
        }

        lock (_windowTitleGate)
            _windowTitleOverride = title;

        await EmitWindowTitleChangedAsync(overridden: true, title, ct).ConfigureAwait(false);
        return OkTyped(
            new WindowTitleSetResult { Overridden = true, Title = title },
            ProtocolJsonContext.Default.WindowTitleSetResult);
    }

    private async Task<JsonElement> WindowTitleClearAsync(CancellationToken ct)
    {
        lock (_windowTitleGate)
            _windowTitleOverride = null;

        await EmitWindowTitleChangedAsync(overridden: false, title: null, ct).ConfigureAwait(false);
        return OkTyped(
            new WindowTitleClearResult { Overridden = false },
            ProtocolJsonContext.Default.WindowTitleClearResult);
    }

    private async Task EmitWindowTitleChangedAsync(bool overridden, string? title, CancellationToken ct)
    {
        var payload = RuntimeEventPayloadJson.WriteWindowTitleChanged(overridden, title);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.ClientWindowTitleChanged, payload);
        await EmitReliableAsync(
                EventClass.Control,
                ProtocolEventTypes.ClientWindowTitleChanged,
                payload,
                ct)
            .ConfigureAwait(false);
    }

    internal static string NormalizeWindowTitle(string? title)
    {
        var encoded = SafeDisplayText.Encode(title?.Trim());
        if (encoded.Length <= WindowTitleMax)
            return encoded;
        return SafeDisplayText.Clip(encoded, WindowTitleMax);
    }
}
