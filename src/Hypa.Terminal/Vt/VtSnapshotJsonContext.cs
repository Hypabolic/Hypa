using System.Text.Json.Serialization;

namespace Hypa.Terminal.Vt;

/// <summary>
/// AOT-safe source-generated JSON for structured VT snapshots.
/// Explicit <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/> on every property;
/// snake_case policy is defence-in-depth. Defaults are always written (no WhenWritingNull)
/// so golden JSON stays stable.
/// </summary>
[JsonSerializable(typeof(VtStructuredSnapshot))]
[JsonSerializable(typeof(VtCursorSnapshot))]
[JsonSerializable(typeof(VtScrollRegionSnapshot))]
[JsonSerializable(typeof(VtModesSnapshot))]
[JsonSerializable(typeof(VtCellSnapshot))]
[JsonSerializable(typeof(VtCellStyleSnapshot))]
[JsonSerializable(typeof(VtCellSnapshot[]))]
[JsonSerializable(typeof(VtCellSnapshot[][]))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = true)]
public sealed partial class VtSnapshotJsonContext : JsonSerializerContext;
