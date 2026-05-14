using System.Text.Json;

namespace Hypa.Runtime.Domain.Hooks;

public sealed record AgentHookInput(
    string ToolName,
    string Command,
    JsonElement RawPayload,
    string? Path = null
);
