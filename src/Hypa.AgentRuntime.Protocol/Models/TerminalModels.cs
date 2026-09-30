using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>terminal.observe</c> params.</summary>
public sealed record TerminalObserveParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("subscription_id")]
    public string? SubscriptionId { get; init; }

    /// <summary>
    /// When true, drop other observe attachments on this subscription so a
    /// focus retarget cannot fill <c>max_attachments</c>.
    /// </summary>
    [JsonPropertyName("replace")]
    public bool? Replace { get; init; }
}

/// <summary><c>terminal.observe</c> result.</summary>
public sealed record TerminalObserveResult
{
    [JsonPropertyName("attachment_id")]
    public string? AttachmentId { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("output_event")]
    public string? OutputEvent { get; init; }

    [JsonPropertyName("render_event")]
    public string? RenderEvent { get; init; }
}

/// <summary><c>terminal.visible_set</c> params.</summary>
public sealed record TerminalVisibleSetParams
{
    [JsonPropertyName("subscription_id")]
    public string? SubscriptionId { get; init; }

    [JsonPropertyName("pane_ids")]
    public List<string>? PaneIds { get; init; }

    [JsonPropertyName("overlay_pane_id")]
    public string? OverlayPaneId { get; init; }

    [JsonPropertyName("failed")]
    public bool? Failed { get; init; }
}

/// <summary><c>terminal.visible_set</c> result. No snapshot tokens.</summary>
public sealed record TerminalVisibleSetResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("pane_ids")]
    public IReadOnlyList<string>? PaneIds { get; init; }

    [JsonPropertyName("overlay_pane_id")]
    public string? OverlayPaneId { get; init; }

    [JsonPropertyName("revealed_pane_ids")]
    public IReadOnlyList<string>? RevealedPaneIds { get; init; }

    [JsonPropertyName("live_client_count")]
    public int LiveClientCount { get; init; }

    [JsonPropertyName("fail_open")]
    public bool FailOpen { get; init; }
}

/// <summary><c>terminal.control</c> params.</summary>
public sealed record TerminalControlParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }

    [JsonPropertyName("subscription_id")]
    public string? SubscriptionId { get; init; }

    /// <summary>
    /// When true, drop other control attachments on this subscription so a
    /// focus retarget cannot fill <c>max_attachments</c>.
    /// </summary>
    [JsonPropertyName("replace")]
    public bool? Replace { get; init; }
}

/// <summary><c>terminal.control</c> result.</summary>
public sealed record TerminalControlResult
{
    [JsonPropertyName("attachment_id")]
    public string? AttachmentId { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }

    [JsonPropertyName("output_event")]
    public string? OutputEvent { get; init; }

    [JsonPropertyName("render_event")]
    public string? RenderEvent { get; init; }
}

/// <summary>
/// Live <c>terminal.render</c> payload when <c>kind</c> is <c>snapshot</c>.
/// Byte renders omit <c>kind</c> and use <c>encoding</c>/<c>data</c>/<c>byte_count</c>.
/// </summary>
public sealed record TerminalRenderSnapshotPayload
{
    public const string KindSnapshot = "snapshot";

    [JsonPropertyName("pane_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PaneId { get; init; }

    /// <summary>
    /// Overlay target. <c>popup</c> snapshots omit <c>pane_id</c>.
    /// </summary>
    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("row_start")]
    public int RowStart { get; init; }

    [JsonPropertyName("row_end")]
    public int RowEnd { get; init; }

    [JsonPropertyName("complete")]
    public bool Complete { get; init; }

    [JsonPropertyName("grid_cols")]
    public int GridCols { get; init; }

    [JsonPropertyName("grid_rows")]
    public int GridRows { get; init; }

    /// <summary>
    /// Per-paint snapshot id. Slices from different paints must not assemble
    /// together. 0 means unspecified (legacy / unit fixtures).
    /// </summary>
    [JsonPropertyName("generation")]
    public long Generation { get; init; }

    /// <summary>
    /// When true, apply this slice into the last complete grid for the pane.
    /// A patch without a retained same-size frame must not assemble.
    /// </summary>
    [JsonPropertyName("patch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Patch { get; init; }

    /// <summary>
    /// Occupant generation of the pane that produced this grid. 0 is omitted
    /// (legacy fixtures). A torn Full must not fill from a different occupant.
    /// </summary>
    [JsonPropertyName("occupant_generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OccupantGeneration { get; init; }

    /// <summary>
    /// Generation of the retained frame this patch applies to. 0 means
    /// unspecified (legacy). A mismatch must reject the patch and re-anchor.
    /// </summary>
    [JsonPropertyName("base_generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long BaseGeneration { get; init; }

    [JsonPropertyName("snapshot")]
    public JsonElement Snapshot { get; init; }
}

/// <summary>
/// Legacy ANSI blit payload. Live paint must use
/// <see cref="TerminalRenderCellsPayload"/>. Attach refuses leftover
/// <c>kind=blit</c> / <c>ansi</c>.
/// </summary>
public sealed record TerminalRenderBlitPayload
{
    public const string KindBlit = "blit";

    [JsonPropertyName("pane_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PaneId { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("ansi")]
    public string? Ansi { get; init; }

    [JsonPropertyName("full")]
    public bool Full { get; init; }

    [JsonPropertyName("grid_cols")]
    public int GridCols { get; init; }

    [JsonPropertyName("grid_rows")]
    public int GridRows { get; init; }

    [JsonPropertyName("generation")]
    public long Generation { get; init; }

    [JsonPropertyName("base_generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long BaseGeneration { get; init; }

    [JsonPropertyName("occupant_generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OccupantGeneration { get; init; }

    [JsonPropertyName("reanchor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Reanchor { get; init; }

    [JsonPropertyName("changed_cells")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ChangedCells { get; init; }

    [JsonPropertyName("wire_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WireBytes { get; init; }
}

/// <summary>
/// Compact dirty-row cell payload. Live mux paint; not pane ANSI and not
/// a per-cell snapshot object array.
/// </summary>
public sealed record TerminalRenderCellsPayload
{
    public const string KindCells = "cells";

    [JsonPropertyName("pane_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PaneId { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("full")]
    public bool Full { get; init; }

    [JsonPropertyName("grid_cols")]
    public int GridCols { get; init; }

    [JsonPropertyName("grid_rows")]
    public int GridRows { get; init; }

    [JsonPropertyName("generation")]
    public long Generation { get; init; }

    [JsonPropertyName("base_generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long BaseGeneration { get; init; }

    [JsonPropertyName("occupant_generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OccupantGeneration { get; init; }

    [JsonPropertyName("reanchor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Reanchor { get; init; }

    [JsonPropertyName("changed_cells")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ChangedCells { get; init; }

    [JsonPropertyName("wire_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WireBytes { get; init; }

    [JsonPropertyName("rows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TerminalRenderCellRow>? Rows { get; init; }

    [JsonPropertyName("cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TerminalRenderCursorPayload? Cursor { get; init; }

    [JsonPropertyName("active_screen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveScreen { get; init; }

    [JsonPropertyName("provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; init; }

    [JsonPropertyName("viewport_origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ViewportOrigin { get; init; }

    /// <summary>DEC 2026 on the focused pane. Compact wire omits false.</summary>
    [JsonPropertyName("sync")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Sync { get; init; }

    /// <summary>
    /// Ghostty tracking token: <c>any</c>, <c>button</c>, <c>normal</c>,
    /// <c>tracking</c>, <c>none</c>. Compact wire omits null. Omitted on a
    /// patch does not restore a stale attach snapshot.
    /// </summary>
    [JsonPropertyName("mouse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mouse { get; init; }

    /// <summary>
    /// Ghostty encoding: <c>sgr</c>, <c>utf8</c>, <c>urxvt</c>,
    /// <c>sgr_pixels</c>, <c>default</c>. Compact wire omits null (unobserved
    // / SGR).
    /// </summary>
    [JsonPropertyName("mouse_encoding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MouseEncoding { get; init; }

    /// <summary>
    /// DEC private mode 2004 on the focused pane. Each body states the
    /// current value. Compact wire omits false.
    /// </summary>
    [JsonPropertyName("bracketed_paste")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BracketedPaste { get; init; }

    /// <summary>
    /// DEC private mode 1 (DECCKM) on the focused pane. Each body states the
    /// current value. Compact wire omits false.
    /// </summary>
    [JsonPropertyName("application_cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ApplicationCursor { get; init; }

    /// <summary>
    // / Optional hyperlink URI table.
    /// <c>src/protocol/wire.rs:741</c> <c>hyperlinks: Vec&lt;String&gt;</c>.
    /// Absent by default. The attach unpacker reads both shapes.
    /// </summary>
    [JsonPropertyName("hl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Hl { get; init; }
}

/// <summary>One packed grid row. Missing rows on a full frame are blank.</summary>
public sealed record TerminalRenderCellRow
{
    [JsonPropertyName("i")]
    public int I { get; init; }

    [JsonPropertyName("t")]
    public required string T { get; init; }

    [JsonPropertyName("w")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? W { get; init; }

    /// <summary>
    /// UTF-16 lengths of each packed primary glyph in <see cref="T"/>.
    /// Omitted when every glyph is one UTF-16 unit.
    /// </summary>
    [JsonPropertyName("g")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? G { get; init; }

    [JsonPropertyName("s")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TerminalRenderStyleRun>? S { get; init; }

    /// <summary>
    /// Start column of this row segment. Null replaces the whole row and
    /// blanks the rest. A value applies the packed cells from that column
    // / and leaves every other column unchanged.
    /// <c>src/protocol/render_ansi.rs:1004-1005</c> writes a changed run at
    /// a CUP and leaves the rest of the row untouched.
    /// </summary>
    [JsonPropertyName("c")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? C { get; init; }
}

/// <summary>Sparse style run. Default style is omitted.</summary>
public sealed record TerminalRenderStyleRun
{
    [JsonPropertyName("c")]
    public int C { get; init; }

    [JsonPropertyName("n")]
    public int N { get; init; }

    [JsonPropertyName("fg")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Fg { get; init; }

    [JsonPropertyName("bg")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Bg { get; init; }

    [JsonPropertyName("bold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Bold { get; init; }

    [JsonPropertyName("dim")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Dim { get; init; }

    [JsonPropertyName("italic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Italic { get; init; }

    [JsonPropertyName("underline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Underline { get; init; }

    [JsonPropertyName("inverse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Inverse { get; init; }

    [JsonPropertyName("invisible")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Invisible { get; init; }

    [JsonPropertyName("strikethrough")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strikethrough { get; init; }

    [JsonPropertyName("blink")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Blink { get; init; }

    [JsonPropertyName("overline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Overline { get; init; }

    [JsonPropertyName("underline_color")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint UnderlineColor { get; init; }

    [JsonPropertyName("underline_style")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UnderlineStyle { get; init; }

    [JsonPropertyName("hyperlink")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hyperlink { get; init; }

    /// <summary>
    /// Optional index plus one into the payload <c>hl</c> table. Absent by
    // / default.
    /// <c>src/protocol/wire.rs:785-789</c>.
    /// </summary>
    [JsonPropertyName("hi")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Hi { get; init; }
}

/// <summary>Pane cursor carried with a cell payload.</summary>
public sealed record TerminalRenderCursorPayload
{
    [JsonPropertyName("col")]
    public int Col { get; init; }

    [JsonPropertyName("row")]
    public int Row { get; init; }

    [JsonPropertyName("visible")]
    public bool Visible { get; init; }

    [JsonPropertyName("shape")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Shape { get; init; }

    /// <summary>
    // Compact omits
    /// null. A missing field on a present object is Some.
    /// </summary>
    [JsonPropertyName("has_cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasCursor { get; init; }
}
