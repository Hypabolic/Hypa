# libghostty-vt pin (H-13)

This file is the single source of truth for the Ghostty VT native pin.
`scripts/build-libghostty-vt.sh` reads the values below.

## Pins

| Field | Value |
| --- | --- |
| Upstream project | [ghostty-org/ghostty](https://github.com/ghostty-org/ghostty) |
| Source commit SHA | `c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3` |
| Source URL | `https://github.com/ghostty-org/ghostty` |
| Pin date (UTC) | `2026-08-12` |
| Zig version | `0.15.2` |
| Zig download base | `https://ziglang.org/download` |
| Library name | `libghostty-vt` (shared) |
| Optimize | `ReleaseFast` |
| Link mode | **dynamic** shared library (preferred) |
| Static fallback | Only if a RID hard-fails dynamic load; record in spike report |

## Patches

Directory: `native/ghostty/patches/`.

Numbered unified diffs apply in sort order after source fetch.
Empty set is allowed for H-13/H-14 (no local behaviour change required for
load/probe or structured snapshot goldens at this pin). Scroll region is
dual-tracked in managed code (no C API getter). Grapheme-cluster mode is the
upstream default at this pin (no herdr patch required for wide-char goldens).

## Build output

```
native/runtimes/<rid>/native/libghostty-vt.so      # Linux
native/runtimes/<rid>/native/libghostty-vt.dylib   # macOS
```

Checksum sidecar: `libghostty-vt.<rid>.sha256` next to the library.

## Machine-readable tokens

```
GHOSTTY_COMMIT=c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3
ZIG_VERSION=0.15.2
LINK_MODE=dynamic
LIBRARY=libghostty-vt
```
