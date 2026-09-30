using System.Text.Json.Serialization;

namespace Hypa.Cli.Attach.ReleaseNotes;

[JsonSerializable(typeof(PackNotesSeenState))]
internal sealed partial class PackNotesSeenJsonContext : JsonSerializerContext;

internal sealed record PackNotesSeenState(string Version);
