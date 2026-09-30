# hypa-pty-host

Native PTY helper for hypa-runtime. The helper owns `openpty` + `fork` + `exec`
so the managed server does not call `fork` from a multi-threaded process.

## Protocol

Length-prefixed frames on stdio. See
[`docs/plans/AgentRuntime/pty-host-ipc.md`](../../docs/plans/AgentRuntime/pty-host-ipc.md).

## Build

```bash
bash scripts/build-hypa-pty-host.sh
```

Output:

```text
native/runtimes/<RID>/native/hypa-pty-host
```

MSBuild copies this binary for `Hypa.AgentServer`, `Hypa.Cli`, and
`Hypa.AgentRuntime.Tests`. Resolution uses the binary directory or a pinned
RID layout. Cwd and parent walks are not used.

Supported RIDs: `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

Set `FORCE_BUILD=1` to rebuild. Set `HYPA_PTY_HOST` to an absolute path to
override the helper at runtime. Relative paths are rejected.

Compile with `-std=c11 -Werror`. Use `scripts/verify-hypa-pty-host-compile.sh`.
Linux compile evidence is required. A macOS-only build is not enough.

## Scope (H-04 / H-05 / H-08)

- Hello handshake, Spawn, Spawned (child pid ack), Output, Input, Resize, Signal, Exit, Close
- Hosted child env from Spawn only
- Process-group terminate on Close / parent death
- Managed adapter: `PtyHostProcess` behind `IPtyProcess` (`HYPA_PTY_PROVIDER=hypa-pty-host`)
- **H-08 same-host H2 handoff:** PauseOutput / Adopt / Adopted / CloseOldOwner
  - Master FD moves via AF_UNIX `SCM_RIGHTS` (temporary path in frame; never on stdio)
  - CloseOldOwner closes master only — never kills the child
  - 5s pause timeout resumes output if Commit never arrives
  - Nonce + generation on every handoff frame; mismatch fails closed

Not in this helper:

- Windows ConPTY
- Cross-host live PTY migration (not claimed)

## Honesty

Unix default panes use `hypa-pty-host`.
Windows is not a mux host. Do not add ConPTY here.
`process-io` is opt-in for tests and batch work.
`runtime.health` reports the selected provider and `interactive` flag.
H-08 enables same-host H2 only; process-io never claims H2.
