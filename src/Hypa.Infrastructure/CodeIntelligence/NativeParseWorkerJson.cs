using System.Text.Json.Serialization;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Infrastructure.CodeIntelligence;

/// <summary>
/// Stdout envelope for <c>hypa code parse-worker</c>. Source-generated for AOT.
/// </summary>
public sealed record NativeParseWorkerEnvelope
{
    public required bool Ok { get; init; }
    public CodeStructureDocument? Document { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NativeParseWorkerEnvelope))]
[JsonSerializable(typeof(CodeStructureDocument))]
public sealed partial class NativeParseWorkerJsonContext : JsonSerializerContext;
