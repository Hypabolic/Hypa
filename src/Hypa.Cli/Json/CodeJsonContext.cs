using System.Text.Json.Serialization;
using Hypa.Cli.Commands;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Cli.Json;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CodeIndexResult))]
[JsonSerializable(typeof(CodeGraphResult))]
[JsonSerializable(typeof(IReadOnlyList<CodeSymbol>))]
[JsonSerializable(typeof(IReadOnlyList<MarkdownSection>))]
[JsonSerializable(typeof(IReadOnlyList<CodeDiagnostic>))]
[JsonSerializable(typeof(IReadOnlyList<CodeProviderHealth>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(MarkdownQueryJsonResult))]
internal sealed partial class CodeJsonContext : JsonSerializerContext;
