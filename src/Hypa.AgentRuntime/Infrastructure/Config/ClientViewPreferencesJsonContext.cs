using System.Text.Json.Serialization;
using Hypa.AgentRuntime.Application.Sidebar;

namespace Hypa.AgentRuntime.Infrastructure.Config;

[JsonSerializable(typeof(ClientViewPreferences))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
internal sealed partial class ClientViewPreferencesJsonContext : JsonSerializerContext;
