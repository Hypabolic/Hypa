using System.Text.Json.Serialization;

namespace Hypa.Terminal.Vt;

/// <summary>
/// Provider-neutral structured VT grid snapshot (schema_version 1).
/// Normative contract: <c>src/Hypa.AgentRuntime.Protocol/VT-structured-snapshot.md</c>.
/// No native handles, IntPtr, or Ghostty types on this surface.
/// </summary>
public sealed record VtStructuredSnapshot
{
    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("cols")]
    public required int Cols { get; init; }

    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    /// <summary>
    // / Pane cursor.
    /// failed get). A present object with <c>visible: false</c> is DECTCEM
    // / off, including origin.
    /// </summary>
    [JsonPropertyName("cursor")]
    public VtCursorSnapshot? Cursor { get; init; }

    /// <summary><c>main</c> or <c>alt</c>. Basic always reports <c>main</c>.</summary>
    [JsonPropertyName("active_screen")]
    public required string ActiveScreen { get; init; }

    [JsonPropertyName("scroll_region")]
    public required VtScrollRegionSnapshot ScrollRegion { get; init; }

    [JsonPropertyName("modes")]
    public required VtModesSnapshot Modes { get; init; }

    /// <summary>Row-major grid: <c>cells[row][col]</c>.</summary>
    [JsonPropertyName("cells")]
    public required VtCellSnapshot[][] Cells { get; init; }
}

/// <summary>Cursor position (0-based) and visibility.</summary>
public sealed record VtCursorSnapshot
{
    [JsonPropertyName("col")]
    public required int Col { get; init; }

    [JsonPropertyName("row")]
    public required int Row { get; init; }

    /// <summary>
    /// Compact attach uses WhenWritingDefault. False is the CLR default, so this
    /// field must always write or a hidden cursor is dropped.
    /// </summary>
    [JsonPropertyName("visible")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required bool Visible { get; init; }

    /// <summary>DECSCUSR parameter 0–6. 0 is the terminal default.</summary>
    [JsonPropertyName("shape")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Shape { get; init; }
}

/// <summary>Inclusive scroll region in 0-based row indices.</summary>
public sealed record VtScrollRegionSnapshot
{
    [JsonPropertyName("top")]
    public required int Top { get; init; }

    [JsonPropertyName("bottom")]
    public required int Bottom { get; init; }
}

/// <summary>Relevant terminal modes. Basic reports honest F1 defaults only.</summary>
public sealed record VtModesSnapshot
{
    [JsonPropertyName("origin")]
    public required bool Origin { get; init; }

    [JsonPropertyName("auto_wrap")]
    public required bool AutoWrap { get; init; }

    [JsonPropertyName("insert")]
    public required bool Insert { get; init; }

    [JsonPropertyName("bracketed_paste")]
    public required bool BracketedPaste { get; init; }

    /// <summary>Mouse mode token; Basic always <c>none</c>.</summary>
    [JsonPropertyName("mouse")]
    public required string Mouse { get; init; }

    /// <summary>
    /// Ghostty encoding token. Not snapshot JSON; packed on cells as
    // / <c>mouse_encoding</c>.
    /// </summary>
    [JsonIgnore]
    public string? MouseEncoding { get; init; }

    [JsonPropertyName("focus_reporting")]
    public required bool FocusReporting { get; init; }

    /// <summary>DEC 2026 synchronized output. Basic default is false.</summary>
    [JsonPropertyName("sync")]
    public bool Sync { get; init; }

    /// <summary>
    /// DEC private mode 1 (DECCKM). Wire omits false.
    /// </summary>
    [JsonPropertyName("application_cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ApplicationCursor { get; init; }

    /// <summary>Documented Basic F1 defaults (no real mode state).</summary>
    public static VtModesSnapshot BasicDefaults { get; } = new()
    {
        Origin = false,
        AutoWrap = true,
        Insert = false,
        BracketedPaste = false,
        Mouse = "none",
        FocusReporting = false,
        Sync = false,
        ApplicationCursor = false,
    };
}

/// <summary>One grid cell: text, display width, continuation flag, style.</summary>
public sealed record VtCellSnapshot
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("width")]
    public required int Width { get; init; }

    [JsonPropertyName("is_continuation")]
    public required bool IsContinuation { get; init; }

    [JsonPropertyName("style")]
    public required VtCellStyleSnapshot Style { get; init; }

    /// <summary>Empty / space cell with Basic default width and style.</summary>
    public static VtCellSnapshot Empty { get; } = new()
    {
        Text = " ",
        Width = 1,
        IsContinuation = false,
        Style = VtCellStyleSnapshot.Default,
    };
}

/// <summary>
/// Cell style attributes. Basic always emits defaults (no SGR state).
/// Colour fields are null when the provider has no colour (Basic).
/// </summary>
public sealed record VtCellStyleSnapshot
{
    [JsonPropertyName("fg")]
    public string? Fg { get; init; }

    [JsonPropertyName("bg")]
    public string? Bg { get; init; }

    [JsonPropertyName("bold")]
    public required bool Bold { get; init; }

    [JsonPropertyName("dim")]
    public required bool Dim { get; init; }

    [JsonPropertyName("italic")]
    public required bool Italic { get; init; }

    [JsonPropertyName("underline")]
    public required bool Underline { get; init; }

    [JsonPropertyName("inverse")]
    public required bool Inverse { get; init; }

    [JsonPropertyName("invisible")]
    public required bool Invisible { get; init; }

    [JsonPropertyName("strikethrough")]
    public required bool Strikethrough { get; init; }

    [JsonPropertyName("blink")]
    public bool Blink { get; init; }

    [JsonPropertyName("overline")]
    public bool Overline { get; init; }

    [JsonPropertyName("underline_color")]
    public string? UnderlineColor { get; init; }

    [JsonPropertyName("underline_style")]
    public int UnderlineStyle { get; init; }

    /// <summary>Default style (no colour, all attrs false).</summary>
    public static VtCellStyleSnapshot Default { get; } = new()
    {
        Fg = null,
        Bg = null,
        Bold = false,
        Dim = false,
        Italic = false,
        Underline = false,
        Inverse = false,
        Invisible = false,
        Strikethrough = false,
        Blink = false,
        Overline = false,
        UnderlineColor = null,
        UnderlineStyle = 0,
    };
}
