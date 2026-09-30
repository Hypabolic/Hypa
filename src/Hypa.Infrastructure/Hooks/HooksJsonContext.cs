using System.Text.Json.Serialization;
using Hypa.Infrastructure.Hooks.Adapters;

namespace Hypa.Infrastructure.Hooks;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClaudeUpdatedInput))]
[JsonSerializable(typeof(ClaudeUpdatedCommand))]
[JsonSerializable(typeof(ClaudeBlockDecision))]
[JsonSerializable(typeof(ClaudeRedirectPath))]
[JsonSerializable(typeof(ClaudeHookOutputRewrite))]
[JsonSerializable(typeof(ClaudeHookSpecificOutputRewrite))]
[JsonSerializable(typeof(ClaudeHookOutputRedirect))]
[JsonSerializable(typeof(ClaudeHookSpecificOutputRedirect))]
[JsonSerializable(typeof(CopilotCliOutput))]
[JsonSerializable(typeof(CopilotCliModifiedArgs))]
[JsonSerializable(typeof(CodexHookOutput))]
[JsonSerializable(typeof(CodexHookSpecificOutput))]
internal sealed partial class HooksJsonContext : JsonSerializerContext { }
