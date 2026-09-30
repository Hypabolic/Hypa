using System.Text.Json.Serialization;

namespace Hypa.UnitTests.Cli;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(M6GoldenMeta))]
[JsonSerializable(typeof(M6GoldenExpected))]
[JsonSerializable(typeof(M6ScriptLine))]
internal sealed partial class M6GoldenJsonContext : JsonSerializerContext;
