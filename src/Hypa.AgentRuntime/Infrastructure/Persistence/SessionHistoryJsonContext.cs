using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
// / AOT JSON for <c>session-history.json</c>.
/// <c>src/persist/io.rs:48-61</c> writes pretty JSON.
/// </summary>
[JsonSerializable(typeof(SessionHistorySnapshot))]
[JsonSerializable(typeof(WorkspaceHistorySnapshot))]
[JsonSerializable(typeof(TabHistorySnapshot))]
[JsonSerializable(typeof(PaneHistorySnapshot))]
[JsonSerializable(typeof(List<WorkspaceHistorySnapshot>))]
[JsonSerializable(typeof(List<TabHistorySnapshot>))]
[JsonSerializable(typeof(Dictionary<string, PaneHistorySnapshot>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
public sealed partial class SessionHistoryJsonContext : JsonSerializerContext;
