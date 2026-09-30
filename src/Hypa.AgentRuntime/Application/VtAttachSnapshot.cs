namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Inclusive scroll region in 0-based row indices, carried beside the
/// typed frame so the pack writes the same <c>scroll_region</c> bytes.
/// </summary>
public readonly record struct VtScrollRegion(int Top, int Bottom);

/// <summary>
// / Typed attach snapshot.
/// <c>src/server/render_stream.rs:138-143</c> moves the typed
/// <c>PaneSurfaceFrame</c> into <c>ServerMessage::PaneSurface</c>. The mux
/// does not turn this frame into a string first. The wrapper carries the
/// wire fields that <see cref="VtFrame"/> does not hold
/// (<c>schema_version</c> and <c>scroll_region</c>) so the wire bytes do
/// not change. A new field on <see cref="VtFrame"/> would change record
/// equality and every construction site.
/// </summary>
public sealed record VtAttachSnapshot(
    VtFrame Frame,
    VtScrollRegion ScrollRegion,
    int SchemaVersion);
