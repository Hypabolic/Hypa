# Ghostty VT capability matrix (F1 shipping)

**Floor:** Unix F1/F2. Shipping provider is Ghostty.  
**Health (shipping):** `runtime.health.vt.provider=ghostty` with default dimensions from `BasicVtFloor` (120×40).

This matrix is the shipping Ghostty contract. A historical Basic appendix follows. It is not the shipping provider.

## Supported (required F1)

| Capability | Notes |
| --- | --- |
| UTF-8 printables | Printable characters and NBSP (`U+00A0`) write into the grid |
| C0: CR, LF, TAB, BS | Carriage return, line feed, tab (8-col), backspace |
| CSI cursor | `A`/`B`/`C`/`D` (relative); `H`/`f` (CUP); `G` (CHA) |
| CSI erase | `J` (ED), `K` (EL) |
| CSI SGR | Colour and style state on cells, including colon-form truecolour `38:2:r:g:b` |
| Alternate screen | DEC private `?1049h` / `?1049l`. Visible text is the active viewport |
| Scroll regions | DECSTBM |
| Wide / combining | Wide-character cells with continuation |
| Scrollback | Bounded ring (engine cap); text via `GetRecentText` |
| Resize / reset | `Resize`, `Reset` on `IVtEngine` |
| Text projection | `GetVisibleText`, `GetRecentText` (lossy vs structured snapshot) |
| Structured snapshot | `IVtEngine.CaptureSnapshot` — cells/cursor/modes ([VT-structured-snapshot.md](VT-structured-snapshot.md)) |

Shell and simple TUI fixtures live under `tests/Hypa.AgentRuntime.Tests/Fixtures/vt/g-vt-basic/`.
CSI `?1049h` goldens live under `g-vt-alt-ghostty`.

## Consumed no-op (safe ignore)

These sequences do not throw. The parser returns to ground. They do not write ESC bytes into cells.

| Sequence | Final / form | Behaviour |
| --- | --- | --- |
| CSI DSR | final `n` | No-op. No device status reply. |
| CSI with intermediates | params + intermediates then final (e.g. soft-reset `ESC[!p`) | Consume full sequence; no-op unless Ghostty implements that command. Intermediate bytes `0x20–0x2F` are not printables. |
| OSC (any) | OSC … BEL or ST (`ESC \`) | Strip until terminator. Includes OSC 8 hyperlinks. URL payload does not enter the grid. |
| DCS | `ESC P` … ST, or C1 `0x90` | Consume string until ST (`ESC \`), CAN, or SUB. Payload does not enter the grid or `agent.read`. |
| APC | `ESC _` … ST, or C1 `0x9F` | Consume string until ST, CAN, or SUB. Payload does not enter the grid or `agent.read`. |
| PM / SOS | `ESC ^` / `ESC X`, or C1 `0x9E` / `0x98` | Same string consume as DCS/APC. |
| Two-byte ESC / intermediates | ESC plus a final, or ESC plus `0x20–0x2F` then a final | No-op unless Ghostty implements that command. Do not print the payload. |
| Unknown CSI finals | other finals (`0x40–0x7E`) | Ignore; return to ground without cell write. |

CSI parse follows ECMA-48: parameter bytes `0x30–0x3F` (incl. `:`), intermediate bytes `0x20–0x2F`, final bytes `0x40–0x7E` only. Incomplete CSI is cancelled; it does not print into cells.

## Out of scope (F1)

These items are not part of the Ghostty F1 contract:

- Codex / Claude TUI parity
- Mouse as a published parity surface
- Soft reflow on resize as a published surface

## Health dimensions

`runtime.health.vt.cols` and `runtime.health.vt.rows` report **default pane / floor** dimensions (`BasicVtFloor.DefaultCols` × `BasicVtFloor.DefaultRows`).  
They are not the max over live panes. Panes may resize independently.

Hard upper budget (shared with `VtFloorDefaults`): `MaxCols` 1024, `MaxRows` 512, `MaxCells` 262144.  
`pane.resize` and structured snapshot normalize reject oversize grids before allocation.

## Forward links

| Item | Path |
| --- | --- |
| Structured snapshot contract (normative) | [VT-structured-snapshot.md](VT-structured-snapshot.md) |
| Ghostty VT parity milestone (F2) | [docs/plans/AgentRuntime/ghostty-vt-parity-milestone.md](../../docs/plans/AgentRuntime/ghostty-vt-parity-milestone.md) |
| Fixture schema + RID matrix | [VT-f2-agent-goldens.md](VT-f2-agent-goldens.md) |

The structured snapshot contract defines the provider-neutral grid snapshot before Ghostty goldens assert cells.

Ground-state BEL bytes in pane output are exposed as the reliable `pane.bell`
resource event with `pane_id` and a per-feed `count`. A BEL terminating OSC is
not a pane bell.

## Historical Basic appendix

The deleted Basic engine treated CSI SGR as a no-op with no colour, treated
DEC `?1049h` as a no-op, and did not implement alternate screen. Those rows
are not the shipping F1 contract. Do not select `HYPA_VT_PROVIDER=basic`.
That token is rejected.
