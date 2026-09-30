using System.Text.Json.Serialization;
using Hypa.AgentRuntime.Application.Plugins;

namespace Hypa.AgentRuntime.Infrastructure.Plugins;

[JsonSerializable(typeof(List<InstalledPlugin>))]
[JsonSerializable(typeof(InstalledPlugin))]
[JsonSerializable(typeof(PluginCommandSpec))]
[JsonSerializable(typeof(PluginManifestAction))]
[JsonSerializable(typeof(PluginManifestEventHook))]
[JsonSerializable(typeof(PluginManifestPane))]
[JsonSerializable(typeof(PluginManifestLinkHandler))]
[JsonSerializable(typeof(PluginManifestResource))]
[JsonSerializable(typeof(PluginManifestMenuItem))]
[JsonSerializable(typeof(PluginManifestDoctor))]
[JsonSerializable(typeof(PluginEventFilter))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<PluginCommandSpec>))]
[JsonSerializable(typeof(List<PluginManifestAction>))]
[JsonSerializable(typeof(List<PluginManifestEventHook>))]
[JsonSerializable(typeof(List<PluginManifestPane>))]
[JsonSerializable(typeof(List<PluginManifestLinkHandler>))]
[JsonSerializable(typeof(List<PluginManifestResource>))]
[JsonSerializable(typeof(List<PluginManifestMenuItem>))]
[JsonSerializable(typeof(List<PluginManifestDoctor>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<PluginCommandSpec>))]
[JsonSerializable(typeof(IReadOnlyList<PluginManifestAction>))]
[JsonSerializable(typeof(IReadOnlyList<PluginManifestEventHook>))]
[JsonSerializable(typeof(IReadOnlyList<PluginManifestPane>))]
[JsonSerializable(typeof(IReadOnlyList<PluginManifestLinkHandler>))]
[JsonSerializable(typeof(IReadOnlyList<PluginManifestResource>))]
[JsonSerializable(typeof(IReadOnlyList<PluginManifestMenuItem>))]
[JsonSerializable(typeof(IReadOnlyList<PluginManifestDoctor>))]
[JsonSerializable(typeof(IReadOnlyList<InstalledPlugin>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
internal sealed partial class PluginRegistryJsonContext : JsonSerializerContext;
