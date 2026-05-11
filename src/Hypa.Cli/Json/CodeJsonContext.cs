using System.Text.Json.Serialization;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Cli.Json;

[JsonSerializable(typeof(CodeIndexResult))]
[JsonSerializable(typeof(CodeGraphResult))]
[JsonSerializable(typeof(IReadOnlyList<CodeSymbol>))]
[JsonSerializable(typeof(IReadOnlyList<CodeDiagnostic>))]
[JsonSerializable(typeof(IReadOnlyList<CodeProviderHealth>))]
internal sealed partial class CodeJsonContext : JsonSerializerContext;
