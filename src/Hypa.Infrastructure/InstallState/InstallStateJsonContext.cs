using System.Text.Json.Serialization;
using Hypa.Runtime.Domain;

namespace Hypa.Infrastructure.InstallState;

[JsonSerializable(typeof(HypaInstallState))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class InstallStateJsonContext : JsonSerializerContext { }
