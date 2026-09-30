using System.Text.Json.Serialization;
using Hypa.AgentRuntime.Application.Plugins;

namespace Hypa.Cli.Doctor;

[JsonSerializable(typeof(PluginDoctorStdoutDto))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
internal sealed partial class PluginDoctorJsonContext : JsonSerializerContext;
