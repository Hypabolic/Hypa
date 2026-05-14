using System.Text.Json.Serialization;
using Hypa.Runtime.Domain.Runner;
using Hypa.Runtime.Domain.Sessions;

namespace Hypa.Infrastructure.Storage;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionStats))]
[JsonSerializable(typeof(ToolCallRecord))]
[JsonSerializable(typeof(FileTouchRecord))]
[JsonSerializable(typeof(CommandMetricsRecord))]
internal sealed partial class StorageJsonContext : JsonSerializerContext { }
