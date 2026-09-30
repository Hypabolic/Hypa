using System.Text.Json.Serialization;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.App;

[JsonSerializable(typeof(TerminalRenderCellsPayload))]
[JsonSerializable(typeof(TerminalRenderCellRow))]
[JsonSerializable(typeof(TerminalRenderStyleRun))]
[JsonSerializable(typeof(TerminalRenderCursorPayload))]
internal partial class AppJsonContext : JsonSerializerContext;
