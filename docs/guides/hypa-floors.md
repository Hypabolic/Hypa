# Hypa floors

This document states the two product floors. F1 is the default install. F2 is a separate pack.

| Floor | What the user gets | Scope |
| --- | --- | --- |
| **F1** (this ship) | Install `hypa`. Bare attach. Real PTY (`hypa-pty-host`). Ghostty VT. Compression (`-c`, rewrites). | Local agent workspace. |
| **F2** (pack) | Same `hypa` plus Ghostty package extras in `hypa-f2-<rid>.tar.gz` (`hypa.channel`, recorded goldens). | Ghostty package floor. |

The install name is **`hypa`**.
`hypa-runtime` is the lean mux sibling.
Do not use that name as the product install name.

## F1 workspace

The F1 archive contains these required files:

- `hypa`
- the SQLite native (`libe_sqlite3.so`, `libe_sqlite3.dylib`, or `e_sqlite3.dll`)
- Unix `hypa-pty-host`
- Unix `libghostty-vt`. Windows is not a Ghostty-only F1 mux RID.

The installer links `$HYPA_INSTALL_DIR/hypa` to the installed `hypa` binary.
It also sets the execute bit on `hypa` and on `hypa-pty-host`.

Unix F1 uses `pty.provider=hypa-pty-host` and `vt.provider=ghostty`.
This is a local agent workspace.

Unix F1 requires `libghostty-vt`. Missing native fails closed.
`HYPA_VT_PROVIDER=basic` is rejected.

## F2 pack

The F2 archive name is `hypa-f2-<rid>.tar.gz`.
The F2 ship matrix is linux-arm64, osx-arm64, and osx-x64.
linux-x64 Ghostty AOT is a residual. The installer rejects that RID for channel f2.
Both archives come from one `hypa` publish per RID (`.github/workflows/build-dist.yml`), so they hold the same binaries.
F1 already ships Ghostty with its NOTICE, license, PIN, ABI manifest, and SBOM.
F2 adds `hypa.channel` and accepts only a `libghostty-vt` whose digest passed the H-15 golden replay.
`hypa.channel` is the token `f2`. F2 is the package floor plus recorded goldens.
If `libghostty-vt` is missing, the pack fails closed.
The default installer still fetches the F1 archive `hypa-<rid>.tar.gz`.
An F1 install into the same prefix removes leftover `hypa.channel`. The F1 archive must include `libghostty-vt`.
See [`f2-golden-gate.md`](../plans/AgentRuntime/f2-golden-gate.md).
M6 is the attach UX axis.
See [`m6-golden-gate.md`](../plans/AgentRuntime/m6-golden-gate.md). Gate `h45_goldens: recorded`.
