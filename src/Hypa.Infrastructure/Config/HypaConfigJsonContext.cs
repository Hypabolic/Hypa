using System.Text.Json.Serialization;
using Hypa.Runtime.Domain.Config;

namespace Hypa.Infrastructure.Config;

[JsonSerializable(typeof(HypaConfig))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
public sealed partial class HypaConfigJsonContext : JsonSerializerContext { }
