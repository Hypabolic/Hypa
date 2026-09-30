# F2 golden gate

**Status:** H-15 recorded. Not pass.  
**Published claim remains gated by H-68.**

```
h15_goldens: recorded
```

This file is the H-15 claim gate.  
It records that the Unix F2 suites include offline PTY recordings.  
It does **not** pass the herdr unlock.  
H-11-F2 must not claim herdr or full-screen.  
Recorded agent goldens do not publish that claim.  
F1 is not herdr or full-screen.

## Suites

| Suite | Path | Compare |
| --- | --- | --- |
| G-VT-alt-ghostty | `tests/Hypa.AgentRuntime.Tests/Fixtures/vt/g-vt-alt-ghostty/` | Structured snapshot (H-14) |
| G-agent-codex | `tests/Hypa.AgentRuntime.Tests/Fixtures/vt/g-agent-codex/` | Snapshot + detection |
| G-agent-claude | `tests/Hypa.AgentRuntime.Tests/Fixtures/vt/g-agent-claude/` | Snapshot + detection |
| G-detect | `tests/Hypa.AgentRuntime.Tests/Fixtures/vt/g-detect/` | Detector FN/FP |
| G-agent-codex-recorded | `tests/Hypa.AgentRuntime.Tests/Fixtures/vt/g-agent-codex-recorded/` | Offline PTY (working, blocked, idle, done) |
| G-agent-claude-recorded | `tests/Hypa.AgentRuntime.Tests/Fixtures/vt/g-agent-claude-recorded/` | Offline PTY (working, blocked, idle, done) |

Threshold capture kind is `reconstructed_chrome`.  
Recorded suites are `offline_recording`.  
They hold working, blocked, idle, and done.  
Claude recorded idle, working, and done use alt-screen.  
Codex recorded chrome stays on the main screen.  
The pinned Codex binary does not emit `?1049h`.  
They do not unlock herdr.  
Do not call live model APIs in CI.  
The reconstructed scripts emit alt-screen, DECSTBM, CUP footer, box drawing, RGB SGR, `?2026h`, `?25l`, and DECSC.  
They are a generic 120×40 box TUI.  
`agent_version` is the target UI pin. It is not proof of capture.

## Ghostty pin

| Field | Value |
| --- | --- |
| Commit | `c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3` |
| Zig | `0.15.2` |
| Source | `native/ghostty/PIN.md` |
| Grid | 120×40 |
| TERM | `xterm-256color` |
| LANG | `C.UTF-8` |
| TZ | `UTC` |
| Fixture schema | `1` |
| Codex pin | `0.147.0` |
| Claude pin | `2.1.233` |

## Threshold table

Canonical detection input is `GetRecentText(80)` / `ReadDetectionText()`.

| Metric | Bound | Result |
| --- | ---: | ---: |
| Structured snapshot mismatch | 0 | 0 / 8 reconstructed agent + 7 unique G-detect + 8 recorded |
| False negative (working / blocked / done) | 0 / N | 0 / 16 |
| False positive working/blocked on idle/unknown | 0 / N | 0 / 11 |
| Kind mismatch | 0 / N | 0 / 25 |
| Unknown idle | `Unknown` @ `0.0` | hold |

N = 27 unique labelled G-detect + G-agent + recorded checkpoints.  
The denoms include recorded working, blocked, idle, and done.  
G-detect does not clone G-agent scripts.  
Kind N is 25 because two unknown cases have null kind.

Unknown fallback stays `Unknown` at `0.0`.  
Do not restore `Working@0.2`.

## Claim

F2 is the Ghostty package floor (`hypa-f2-<rid>.tar.gz`).
H-15 recorded goldens and H-11-F2 are prerequisites, not the written claim unlock.
H-68 is the only issue that may unlock the written claim.
H-11-F2 must not claim herdr or full-screen.
M6 is not herdr or full-screen. H-45 recorded goldens do not unlock H-68.  
Do not write a present-tense F1 parity sentence on README or the F1 skill.

OSC 8 hyperlinks are deferred. Schema v1 has no hyperlink cell field.

## RID matrix

Hard goldens run on the Unix runner that built `libghostty-vt`.  
AOT `VtGoldenGen --compare-h15` replays the same suites against the staged RC lib.  
H-11-F2 pack requires that exact lib hash.  
The recorded hashes live in `native/ghostty/h15-accepted-digests`.  
A pin SHA is not a digest.  
linux-x64 AOT execute remains an H-13 residual.  
Do not invent a four-RID green.  
Windows is out of scope.
