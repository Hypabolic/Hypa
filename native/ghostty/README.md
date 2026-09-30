# native/ghostty — libghostty-vt (H-13 / H-14)

Pinned Ghostty VT shared library. Ghostty is the only pane VT.
H-13 proved load/probe. Missing native fails closed. This is not herdr or
full-screen. H-68 remains the claim unlock.

## Build

```bash
# Requires Zig 0.15.2 (script can download into native/ghostty/.cache/zig)
# On macOS 26, Zig 0.15.2 cannot link the default SDK. The script uses a
# MacOSX 15 SDK when present. Do not bump the Ghostty pin for that failure.
bash scripts/build-libghostty-vt.sh

# Force rebuild
FORCE_BUILD=1 bash scripts/build-libghostty-vt.sh

# Optional: use an existing Ghostty / libghostty-vt source tree
HYPA_GHOSTTY_SRC=/path/to/ghostty-or-libghostty-vt-dist bash scripts/build-libghostty-vt.sh
```

Output:

```
native/runtimes/<rid>/native/libghostty-vt.so|.dylib
```

## Runtime selection

| Env | Values | Effect |
| --- | --- | --- |
| `HYPA_VT_PROVIDER` | `ghostty` (default). `basic` and `f1` are rejected. | Provider for health + PaneRuntime engine. |
| `HYPA_VT_REQUIRED` | empty, `ghostty` | Fail closed if Ghostty cannot load |
| `HYPA_GHOSTTY_VT` | absolute path | Override library path |

Ghostty is required. Missing library or ABI mismatch exits non-zero.
PaneRuntime uses `GhosttyVtEngine` for that process (no mid-pane engine swap).

## Files

| Path | Role |
| --- | --- |
| `PIN.md` | Commit SHA, Zig version, link mode |
| `NOTICE` | License inventory |
| `LICENSE.Ghostty` | Upstream MIT text |
| `abi-manifest.json` | Symbols used by loader + GhosttyVtEngine |
| `h15-accepted-digests` | Per-RID lib sha256 from a live H-15 replay |
| `patches/` | Numbered local patches (may be empty) |

## Honesty

H-14 wires the Ghostty engine. It does **not** unlock herdr / full-screen
parity. That claim needs H-15 agent goldens + H-11-F2 packaging.
