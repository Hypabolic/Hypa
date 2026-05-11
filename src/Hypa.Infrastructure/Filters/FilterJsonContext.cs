using System.Text.Json.Serialization;
using Hypa.Runtime.Domain.Filters;

namespace Hypa.Infrastructure.Filters;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(FilterDefinition))]
[JsonSerializable(typeof(FilterStage))]
[JsonSerializable(typeof(FilterStageKind))]
[JsonSerializable(typeof(FilterScope))]
[JsonSerializable(typeof(List<FilterStage>))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class FilterJsonContext : JsonSerializerContext { }
