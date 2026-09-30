using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hypa.Cli.Attach.Keys;

/// <summary>
/// Receives every dispatched keyboard action. Mouse applies through
/// <c>ApplyMouseResultAsync</c> / <c>ChromeHitApply</c> onto
/// <see cref="IAttachCommandPort"/>, not this sink.
/// </summary>
public interface IKeyActionSink
{
    void Handle(KeyActionRequest request);
}

/// <summary>Maps an action to a control-plane RPC or a client mode change.</summary>
public interface IAttachCommandPort
{
    Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct);
}
