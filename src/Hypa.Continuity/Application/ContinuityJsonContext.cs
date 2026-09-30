using System.Text.Json.Serialization;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

[JsonSerializable(typeof(ResumeProbeResult))]
[JsonSerializable(typeof(ResumeEvidence))]
[JsonSerializable(typeof(DestApplyRequest))]
[JsonSerializable(typeof(DestApplyResult))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    Converters = [typeof(ResumeEvidenceJsonConverter)])]
public partial class ContinuityJsonContext : JsonSerializerContext;
