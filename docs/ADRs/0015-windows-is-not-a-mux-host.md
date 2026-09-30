# ADR 0015: Windows is not a mux host

## Status

Accepted

## Date

2026-09-12

## Context

Mux product hosts are Linux and macOS.
C-69 pinned MsQuic on Windows only.
Later work treated that pin, and TLS/TCP, as the Mac and Linux story.
That path is wrong.
Windows is not a mux host.

## Decision

Hypa mux, attach, PTY, VT, and host paint run on Linux and macOS only.

Do not add a Windows mux RID.
Do not add named-pipe mux transport.
Do not add ConPTY as a product pane path.
Do not treat a Windows MsQuic pin as a mux support claim.

The compression CLI may still build on Windows.
That path is not mux.

On Linux and macOS, a named QUIC peer uses QUIC.
SSH is the fallback when QUIC cannot start.

## Consequences

### Positive

- New mux work targets Linux and macOS only.
- Agents do not reopen Windows pipe or ConPTY work.

### Negative / Trade-offs

- Windows users do not get mux.
- Windows library pin code may remain for tests.
- That code does not unlock a mux host.

## Implementation Notes

Publish mux archives for `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.
There is no `hypa-win-*.zip` mux archive.
`install.ps1` fails closed.

H-28 is `[wontfix]`.

## Alternatives Considered

### Keep Windows as a later mux RID

This repeats named-pipe and ConPTY work.
The product does not need a Windows mux host.

### Treat TLS/TCP as the Unix support story

QUIC is mandatory on Linux and macOS.
SSH is the fallback.
TLS/TCP is not that story.

## Related Decisions

- ADR 0014: Hypa native app as a thin mux client
