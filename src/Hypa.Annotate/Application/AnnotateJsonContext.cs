using System.Text.Json.Serialization;
using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

[JsonSerializable(typeof(Annotation))]
[JsonSerializable(typeof(ArchivedAnnotationSet))]
[JsonSerializable(typeof(CaptureContext))]
[JsonSerializable(typeof(PendingAnnotation))]
[JsonSerializable(typeof(PendingLastReview))]
[JsonSerializable(typeof(PluginDoctorStdout))]
[JsonSerializable(typeof(Annotation[]))]
[JsonSerializable(typeof(ArchivedAnnotationSet[]))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class AnnotateJsonContext : JsonSerializerContext;
