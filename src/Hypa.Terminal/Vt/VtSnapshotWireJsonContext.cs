using System.Text.Json.Serialization;

namespace Hypa.Terminal.Vt;

/// <summary>
/// Compact AOT source-generated JSON for live attach snapshots.
/// Omits default cell style flags so a Ghostty-sized Full fits the assembler cap.
/// <see cref="VtCursorSnapshot.Visible"/> always writes (Never ignore).
/// Golden compare stays on <see cref="VtSnapshotJsonContext"/> (indented).
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
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    WriteIndented = false)]
public sealed partial class VtSnapshotWireJsonContext : JsonSerializerContext;
