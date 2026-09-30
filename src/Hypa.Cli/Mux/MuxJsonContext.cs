using System.Text.Json.Serialization;
using Hypa.Cli.Attach;

namespace Hypa.Cli.Mux;

public sealed record RuntimeStatusFile(string Session, string Socket, string Cwd, int Pid);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RuntimeStatusFile))]
[JsonSerializable(typeof(MuxAttachClientState))]
internal partial class MuxJsonContext : JsonSerializerContext;
