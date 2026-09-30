using System.Buffers;
using System.Text;
using System.Text.Json;
using Hypa.Terminal.Vt.Ghostty;

namespace Hypa.Terminal.Vt;

/// <summary>
/// Golden-comparison normalization for structured VT snapshots.
/// Forces schema_version 1, empty-cell text to space, clamped cursor,
/// rectangular grid shape, and explicit style/mode defaults.
/// </summary>
public static class VtSnapshotNormalizer
{
    public const int SchemaVersion = 1;

    /// <summary>
    /// Hard ingress bound on untrusted snapshot JSON character length before any
    /// UTF-8 transcode or token scan. Large enough for budget-max full cell grids
    /// with headroom; finite so adversarial multi-gig payloads fail closed.
    /// </summary>
    public const int MaxJsonInputChars = 32 * 1024 * 1024;

    /// <summary>Max JSON nesting depth during the budget pre-scan (matches STJ default).</summary>
    private const int JsonPreScanMaxDepth = 64;

    /// <summary>
    /// Normalize a snapshot for golden compare. Does not mutate the input.
    /// </summary>
    public static VtStructuredSnapshot Normalize(VtStructuredSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var cols = Math.Max(1, snapshot.Cols);
        var rows = Math.Max(1, snapshot.Rows);

        // Prefer declared dimensions; if cells disagree, expand to cover both.
        if (snapshot.Cells is { Length: > 0 })
        {
            rows = Math.Max(rows, snapshot.Cells.Length);
            for (var r = 0; r < snapshot.Cells.Length; r++)
            {
                var row = snapshot.Cells[r];
                if (row is not null)
                    cols = Math.Max(cols, row.Length);
            }
        }

        // Reject before any full-grid allocation (untrusted JSON / oversize capture).
        BasicVtFloor.EnsureValidDimensions(cols, rows);

        var cells = new VtCellSnapshot[rows][];
        for (var r = 0; r < rows; r++)
        {
            cells[r] = new VtCellSnapshot[cols];
            var srcRow = snapshot.Cells is not null && r < snapshot.Cells.Length
                ? snapshot.Cells[r]
                : null;
            for (var c = 0; c < cols; c++)
            {
                VtCellSnapshot? src = srcRow is not null && c < srcRow.Length
                    ? srcRow[c]
                    : null;
                cells[r][c] = NormalizeCell(src);
            }
        }

        VtCursorSnapshot? cursor;
        if (snapshot.Cursor is { } srcCursor)
        {
            cursor = new VtCursorSnapshot
            {
                Col = Math.Clamp(srcCursor.Col, 0, cols - 1),
                Row = Math.Clamp(srcCursor.Row, 0, rows - 1),
                Visible = srcCursor.Visible,
                Shape = Math.Clamp(srcCursor.Shape, 0, 6),
            };
        }
        else
            cursor = null;

        var scrollTop = snapshot.ScrollRegion?.Top ?? 0;
        var scrollBottom = snapshot.ScrollRegion?.Bottom ?? rows - 1;
        scrollTop = Math.Clamp(scrollTop, 0, rows - 1);
        scrollBottom = Math.Clamp(scrollBottom, scrollTop, rows - 1);

        var modes = snapshot.Modes ?? VtModesSnapshot.BasicDefaults;
        var mouse = string.IsNullOrEmpty(modes.Mouse) ? "none" : modes.Mouse;

        var activeScreen = snapshot.ActiveScreen;
        if (activeScreen is not ("main" or "alt"))
            activeScreen = "main";

        // Omitted wire provider is ghostty, never the historical Basic token.
        var provider = string.IsNullOrEmpty(snapshot.Provider)
            ? GhosttyVtEngine.Provider
            : snapshot.Provider;

        return new VtStructuredSnapshot
        {
            SchemaVersion = SchemaVersion,
            Provider = provider,
            Cols = cols,
            Rows = rows,
            Cursor = cursor,
            ActiveScreen = activeScreen,
            ScrollRegion = new VtScrollRegionSnapshot
            {
                Top = scrollTop,
                Bottom = scrollBottom,
            },
            Modes = new VtModesSnapshot
            {
                Origin = modes.Origin,
                AutoWrap = modes.AutoWrap,
                Insert = modes.Insert,
                BracketedPaste = modes.BracketedPaste,
                Mouse = mouse,
                MouseEncoding = modes.MouseEncoding,
                FocusReporting = modes.FocusReporting,
                Sync = modes.Sync,
                ApplicationCursor = modes.ApplicationCursor,
            },
            Cells = cells,
        };
    }

    /// <summary>
    /// Serialize a normalized snapshot to canonical indented JSON via source-gen context.
    /// </summary>
    public static string ToCanonicalJson(VtStructuredSnapshot snapshot)
    {
        var normalized = Normalize(snapshot);
        return JsonSerializer.Serialize(normalized, VtSnapshotJsonContext.Default.VtStructuredSnapshot);
    }

    /// <summary>
    /// Serialize a normalized snapshot to compact JSON for live attach.
    /// Golden compare stays on <see cref="ToCanonicalJson"/>.
    /// </summary>
    public static string ToCompactJson(VtStructuredSnapshot snapshot)
    {
        var normalized = Normalize(snapshot);
        return JsonSerializer.Serialize(normalized, VtSnapshotWireJsonContext.Default.VtStructuredSnapshot);
    }

    /// <summary>
    /// Deserialize snapshot JSON (golden file) and return a normalized instance.
    /// Declared and jagged cell extents are budget-checked via a streaming
    /// <see cref="Utf8JsonReader"/> pre-scan (no <see cref="JsonDocument"/> DOM)
    /// before materializing the full cell DTO graph (fail-closed untrusted ingress).
    /// </summary>
    public static VtStructuredSnapshot FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        // Bound input length, cols/rows/cells array shape before allocating DTO graphs.
        EnsureJsonWithinBudget(json);
        var snapshot = JsonSerializer.Deserialize(json, VtSnapshotJsonContext.Default.VtStructuredSnapshot)
            ?? throw new InvalidOperationException("Snapshot JSON deserialized to null.");
        return Normalize(snapshot);
    }

    /// <summary>
    /// Stream-scan declared dimensions and jagged <c>cells</c> array lengths with
    /// <see cref="Utf8JsonReader"/> — no full JSON DOM. Rejects resource-exhaustion
    /// shapes as soon as an oversize extent is observed (before reading the rest of
    /// a large array, and before
    /// <see cref="JsonSerializer.Deserialize{TValue}(string, System.Text.Json.Serialization.Metadata.JsonTypeInfo{TValue})"/>).
    /// </summary>
    private static void EnsureJsonWithinBudget(string json)
    {
        if (json.Length > MaxJsonInputChars)
        {
            throw new ArgumentOutOfRangeException(
                nameof(json),
                json.Length,
                $"snapshot JSON length must be <= {MaxJsonInputChars} (got {json.Length})");
        }

        var maxByteCount = Encoding.UTF8.GetMaxByteCount(json.Length);
        var rented = ArrayPool<byte>.Shared.Rent(maxByteCount);
        try
        {
            var byteCount = Encoding.UTF8.GetBytes(json, rented);
            var reader = new Utf8JsonReader(
                rented.AsSpan(0, byteCount),
                new JsonReaderOptions { MaxDepth = JsonPreScanMaxDepth });

            // Match Normalize: coerce non-positive declared dims to 1, then expand from cells.
            var cols = 1;
            var rows = 1;

            if (!reader.Read())
                return;

            // Non-object roots: leave schema failure to Deserialize.
            if (reader.TokenType != JsonTokenType.StartObject)
                return;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                var name = reader.GetString();
                if (!reader.Read())
                    break;

                switch (name)
                {
                    case "cols":
                        if (reader.TokenType == JsonTokenType.Number
                            && reader.TryGetInt32(out var declaredCols))
                        {
                            cols = Math.Max(1, declaredCols);
                            RejectIfOverBudget(cols, rows);
                        }
                        else
                        {
                            SkipValue(ref reader);
                        }

                        break;

                    case "rows":
                        if (reader.TokenType == JsonTokenType.Number
                            && reader.TryGetInt32(out var declaredRows))
                        {
                            rows = Math.Max(1, declaredRows);
                            RejectIfOverBudget(cols, rows);
                        }
                        else
                        {
                            SkipValue(ref reader);
                        }

                        break;

                    case "cells":
                        ScanCellsArray(ref reader, ref cols, ref rows);
                        break;

                    default:
                        SkipValue(ref reader);
                        break;
                }
            }

            BasicVtFloor.EnsureValidDimensions(cols, rows);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Walk the <c>cells</c> outer array token-by-token. Counts every element as a
    /// row (matching jagged expansion). For array rows, counts elements with
    /// early exit on MaxCols / MaxCells without consuming the remainder of the
    /// row or subsequent rows.
    /// </summary>
    private static void ScanCellsArray(ref Utf8JsonReader reader, ref int cols, ref int rows)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            SkipValue(ref reader);
            return;
        }

        var cellRowCount = 0;
        var maxRowLen = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                break;

            cellRowCount++;
            rows = Math.Max(rows, cellRowCount);

            // Reject as soon as jagged row count alone exceeds the budget.
            if (rows > BasicVtFloor.MaxRows)
                BasicVtFloor.EnsureValidDimensions(Math.Min(cols, BasicVtFloor.MaxCols), rows);

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var rowLen = 0;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                        break;

                    rowLen++;

                    // Fail closed on first oversize cell without scanning the rest
                    // of this row or any subsequent rows / trailing JSON.
                    if (rowLen > BasicVtFloor.MaxCols)
                    {
                        cols = Math.Max(cols, rowLen);
                        BasicVtFloor.EnsureValidDimensions(cols, rows);
                    }

                    // Product may blow MaxCells even when each axis is under its max.
                    var expandedCols = Math.Max(cols, rowLen);
                    if ((long)expandedCols * rows > BasicVtFloor.MaxCells)
                    {
                        cols = expandedCols;
                        BasicVtFloor.EnsureValidDimensions(cols, rows);
                    }

                    // Nested cell object/array: skip body; scalar already consumed.
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        SkipValue(ref reader);
                }

                if (rowLen > maxRowLen)
                    maxRowLen = rowLen;
            }
            else
            {
                // Non-array row element (object / null / scalar): skip body only.
                SkipValue(ref reader);
            }
        }

        cols = Math.Max(cols, maxRowLen);
    }

    private static void RejectIfOverBudget(int cols, int rows)
    {
        if (cols > BasicVtFloor.MaxCols
            || rows > BasicVtFloor.MaxRows
            || (long)cols * rows > BasicVtFloor.MaxCells)
        {
            BasicVtFloor.EnsureValidDimensions(cols, rows);
        }
    }

    /// <summary>
    /// Skip the value at the current reader position (no-op for scalars already on the token).
    /// </summary>
    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
            return;

        var depth = reader.CurrentDepth;
        while (reader.Read() && reader.CurrentDepth > depth)
        {
            // Consume nested tokens until the matching end.
        }
    }

    /// <summary>
    /// Project cell text to visible lines (agent.read / GetVisibleText lossiness rules):
    /// active cells only, drop style/modes/cursor, skip continuation cells, optional trim, LF between rows.
    /// </summary>
    public static string ProjectVisibleText(VtStructuredSnapshot snapshot, bool trimTrailingWhitespace = true)
    {
        var n = Normalize(snapshot);
        var lines = new string[n.Rows];
        for (var r = 0; r < n.Rows; r++)
        {
            var sb = new System.Text.StringBuilder(n.Cols);
            for (var c = 0; c < n.Cols; c++)
            {
                var cell = n.Cells[r][c];
                if (cell.IsContinuation)
                    continue;
                sb.Append(cell.Text);
            }

            var line = sb.ToString();
            if (trimTrailingWhitespace)
                line = line.TrimEnd();
            lines[r] = line;
        }

        return string.Join('\n', lines);
    }

    private static VtCellSnapshot NormalizeCell(VtCellSnapshot? src)
    {
        if (src is null)
            return VtCellSnapshot.Empty;

        var text = src.Text;
        if (string.IsNullOrEmpty(text))
            text = " ";

        var width = src.Width < 1 ? 1 : src.Width;
        var style = src.Style ?? VtCellStyleSnapshot.Default;

        return new VtCellSnapshot
        {
            Text = text,
            Width = width,
            IsContinuation = src.IsContinuation,
            Style = new VtCellStyleSnapshot
            {
                Fg = style.Fg,
                Bg = style.Bg,
                Bold = style.Bold,
                Dim = style.Dim,
                Italic = style.Italic,
                Underline = style.Underline,
                Inverse = style.Inverse,
                Invisible = style.Invisible,
                Strikethrough = style.Strikethrough,
                Blink = style.Blink,
                Overline = style.Overline,
                UnderlineColor = style.UnderlineColor,
                UnderlineStyle = style.UnderlineStyle,
            },
        };
    }
}
