# VT structured snapshot contract

**Schema version:** `1`  
**Floor:** Shipping provider is Ghostty. The Basic-vs-Ghostty fill table below is historical test documentation, not the F1 default.

## Purpose

Freeze a **provider-neutral** structured grid snapshot so goldens can assert cells and cursor without native handles.

Surface:

- C# types: `Hypa.Terminal.Vt.VtStructuredSnapshot` and nested records
- Capture API: `IVtEngine.CaptureSnapshot()`
- Normalization: `VtSnapshotNormalizer`
- AOT JSON: `VtSnapshotJsonContext` (source-generated, snake_case)

There is **no** `pane.snapshot` control-plane method. `agent.read` stays text.

## No native handles

The snapshot surface must not expose:

- `IntPtr`, native file descriptors, or Ghostty handles
- libghostty types or package references on the snapshot DTO path

Ghostty maps native state into these value objects. Historical Basic tests used the same DTO with honest defaults.

## Schema (`schema_version: 1`)

| Field | Type | Notes |
| --- | --- | --- |
| `schema_version` | int | Always `1` after normalize |
| `provider` | string | shipping `ghostty` |
| `cols` / `rows` | int | Grid dimensions |
| `cursor` | object or null | `{ col, row, visible, shape }` — **0-based**. `shape` is DECSCUSR 0–6. Null is no cursor (HAS_VALUE false or failed get). |
| `active_screen` | string | `main` or `alt` |
| `scroll_region` | object | `{ top, bottom }` inclusive **0-based** rows |
| `modes` | object | See modes table |
| `cells` | array of rows | `cells[row][col]` cell objects |

### Cursor

| Field | Notes |
| --- | --- |
| `col` / `row` | 0-based; clamped to the grid on normalize |
| `visible` | Basic always `true`. Ghostty uses render_state mode 25. |
| `shape` | DECSCUSR 0–6. 0 is the terminal default. Basic always `0`. Compact JSON omits `0`. |

Host paint parks last-visible without CUP. A present object with `visible: false` (including origin) is DECTCEM off. Host paint CUPs to that cell and hides. Compact JSON omits a null cursor. Canonical JSON writes `"cursor": null`. Normalize does not invent a visible caret.

### Modes

| Field | Basic default |
| --- | --- |
| `origin` | `false` |
| `auto_wrap` | `true` |
| `insert` | `false` |
| `bracketed_paste` | `false` |
| `mouse` | `"none"` |
| `focus_reporting` | `false` |
| `sync` | `false` |
| `application_cursor` | `false` |

`sync` is DEC 2026 synchronized output. Host paint hides the host cursor
while this flag is true. Compact wire omits `false`.

`application_cursor` is DEC private mode 1 (DECCKM). The packer writes it
last, after `sync`. Compact wire omits `false`. A missing field means false.

### Cell

| Field | Notes |
| --- | --- |
| `text` | Grapheme / character string. Empty cell → `" "` |
| `width` | Display columns. Basic always `1` |
| `is_continuation` | Trailing half of a wide cell. Basic always `false` |
| `style` | See style table |

### Style

| Field | Basic default | Ghostty |
| --- | --- | --- |
| `fg` / `bg` | `null` (no colour state) | `palette:N` or `#RRGGBB` (null when unset) |
| `bold` / `dim` / `italic` / `underline` / `inverse` / `invisible` / `strikethrough` | `false` | real cell style |

Colour wire encoding is frozen for goldens: palette indices as `palette:N`
(decimal), direct RGB as `#` + six uppercase hex digits.

## Historical Basic vs Ghostty fill rules

This table is historical test documentation. It is not the shipping F1 default.

| Field | Historical Basic | Ghostty (shipping) |
| --- | --- | --- |
| `active_screen` | always `main` | real main/alt |
| `scroll_region` | full screen `0..rows-1` | real DECSTBM |
| SGR / style | defaults only; CSI `m` is a no-op | real cell style |
| Wide chars | one UTF-16 code unit per cell, `width=1`, no continuation | width + continuation model |
| Surrogate pairs | F1 limitation: may split across cells | proper grapheme handling |

Historical Basic must **not** invent SGR, alt-screen, or scroll-region state. Sequences that request those remain parser no-ops on that old engine. Snapshots report documented defaults so historical G-VT-alt fixtures can assert shape. Shipping panes use Ghostty.

## Dimension budget

Resize, engine screen allocation, and snapshot normalize/deserialize share one budget
(aligned constants on `VtFloorDefaults` and `BasicVtFloor`):

| Bound | Value | Applies to |
| --- | --- | --- |
| `MaxCols` | 1024 | `pane.resize`, Ghostty ctor/Resize, normalizer |
| `MaxRows` | 512 | same |
| `MaxCells` | 262144 | product `cols * rows` (rejects tall×wide extremes) |

Oversize requests fail closed **before** full-grid allocation:

- Control plane: `pane.resize` → `invalid_params` (`-32602`)
- Engine: `ArgumentOutOfRangeException` from `BasicVtFloor.EnsureValidDimensions`
- Normalizer / `FromJson`: same engine exception (no expand-then-allocate of untrusted grids)
- `FromJson` pre-scan uses streaming `Utf8JsonReader` (no `JsonDocument` DOM). It rejects
  oversize declared dims, jagged row counts, and jagged row widths as soon as the extent
  is observed — without reading the remainder of a large `cells` array. A finite input
  character bound (`VtSnapshotNormalizer.MaxJsonInputChars`) rejects multi-gig payloads
  before UTF-8 transcode.

## Golden normalization

`VtSnapshotNormalizer.Normalize`:

1. Force `schema_version` to `1`
2. Map empty cell `text` to `" "`
3. Clamp a present cursor into the grid. Keep a null cursor as null.
4. Enforce rectangular `rows × cols` cell grid within the dimension budget
5. Always emit explicit style and mode fields (no omit-null flakiness)
6. Canonical compare via `VtSnapshotJsonContext` source-gen JSON

Pin small grids in G-VT-alt-ghostty fixtures (for example 20×6 or 40×8) so expected JSON stays readable.
Agent goldens use the product default **120×40**. That full-grid JSON is large. That is acceptable.

Suite layout: `tests/Hypa.AgentRuntime.Tests/Fixtures/vt/g-vt-alt-ghostty/` with `*.input.txt`, `*.snapshot.expected.json`, and `meta.json`. Visible-text Ghostty goldens live in `g-vt-basic/`.
Agent suites: [`VT-f2-agent-goldens.md`](VT-f2-agent-goldens.md).

## Text projection lossiness (`agent.read` / `GetVisibleText`)

Plain text projection from the grid is **lossy**. Normative rules:

1. Active visible cells only (Ghostty uses the active screen).
2. Drop style, modes, `active_screen`, `scroll_region`, and cursor.
3. Concatenate cell `text` left → right; skip cells where `is_continuation` is true.
4. Optional per-line trailing whitespace trim; newline (`\n`) between rows.
5. Do not re-encode display width.

`GetVisibleText` / `GetRecentText` / `agent.read` follow this projection. Structured goldens use `CaptureSnapshot`, not text alone.

## Attach delivery (H-27)

When a client attaches with `terminal.observe` or `terminal.control`, the server
sends the current grid as the first live `terminal.render` event.

The payload has `kind` set to `snapshot`. The `snapshot` object uses this
schema (`schema_version` 1). Cells, cursor, modes, and `active_screen` come
from `IVtEngine.CaptureSnapshot` after `VtSnapshotNormalizer.Normalize`.

There is no `pane.snapshot` method. The observe and control RPC results do not
include a snapshot field.

The server never writes this event to the journal.

If the formatted NDJSON line is larger than 1 MiB, the server sends row slices.
Each slice has `kind` set to `snapshot`. `row_start` is inclusive. `row_end` is
exclusive. The last slice has `complete` set to `true`. Each `snapshot.cells`
array holds only the rows in that slice. The wrapper has `grid_rows` and
`grid_cols` so the client can assemble the full grid.

A default 120×40 Basic snapshot fits in one line. A Ghostty snapshot with style
on a 120×40 grid can use more than one slice.

The server redacts the full visible grid before it sends the snapshot. The
server joins active cell text in row-major order. The server applies the same
stream-keyed redaction as live terminal bytes. The server then writes the
redacted text back by each cell's original text length. When redaction does
not change the text, the server leaves cell graphemes unchanged. A secret
that wraps from one row to the next does not stay visible.

## Related documents

| Item | Path |
| --- | --- |
| F1 unsupported matrix | [VT-unsupported.md](VT-unsupported.md) |
| Ghostty F2 plan | [docs/plans/AgentRuntime/ghostty-vt-parity-milestone.md](../../docs/plans/AgentRuntime/ghostty-vt-parity-milestone.md) |
| Agent goldens | [VT-f2-agent-goldens.md](VT-f2-agent-goldens.md) |
