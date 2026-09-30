using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>Wire names for <see cref="ResumeEvidence"/>. Lives outside Domain.</summary>
public sealed class ResumeEvidenceJsonConverter : JsonConverter<ResumeEvidence>
{
    public override ResumeEvidence Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n))
        {
            return Enum.IsDefined(typeof(ResumeEvidence), n)
                ? (ResumeEvidence)n
                : ResumeEvidence.None;
        }

        var text = reader.GetString();
        return text switch
        {
            "none" => ResumeEvidence.None,
            "store_presence" => ResumeEvidence.StorePresence,
            "index_listing" => ResumeEvidence.IndexListing,
            "harness_reported" => ResumeEvidence.HarnessReported,
            _ => ResumeEvidence.None,
        };
    }

    public override void Write(Utf8JsonWriter writer, ResumeEvidence value, JsonSerializerOptions options)
    {
        var text = value switch
        {
            ResumeEvidence.StorePresence => "store_presence",
            ResumeEvidence.IndexListing => "index_listing",
            ResumeEvidence.HarnessReported => "harness_reported",
            _ => "none",
        };
        writer.WriteStringValue(text);
    }
}
