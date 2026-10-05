# VT F2 agent goldens

**Schema version:** `1`  
**Floor:** F2 Ghostty package floor.

## Purpose

Pin recorded or reconstructed agent chrome.  
Compare structured cells and detection input.  
Do not use live model runs as CI.

## Fixture schema (`fixture_schema: 1`)

Layout:

```
tests/Hypa.AgentRuntime.Tests/Fixtures/vt/
  g-agent-codex/
    meta.json
    <checkpoint>.script.ndjson
    <checkpoint>.snapshot.expected.json
    <checkpoint>.detection.expected.json
  g-agent-claude/   (same shape)
  g-agent-codex-recorded/   (offline_recording)
  g-agent-claude-recorded/  (offline_recording)
  g-detect/
    meta.json
    <case>.script.ndjson
    <case>.detection.expected.json
    <case>.snapshot.expected.json   (optional; required when the case also asserts grid)
```

`meta.json` uses snake_case. Required fields:

| Field | Rule |
| --- | --- |
| `suite` | `g-agent-codex`, `g-agent-claude`, `g-agent-codex-recorded`, `g-agent-claude-recorded`, or `g-detect` |
| `floor` | `F2` |
| `provider` | `ghostty` |
| `cols` / `rows` | Product default **120×40**. Do not use the 20×6 G-VT-alt toy grid. |
| `term` | `xterm-256color` |
| `lang` | `C.UTF-8` |
| `tz` | `UTC` |
| `agent_name` / `agent_version` / `process_name` | Pinned per suite. G-detect may use mixed names. |
| `ghostty_commit` | Must equal `native/ghostty/PIN.md` (`c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3`) |
| `zig_version` | `0.15.2` |
| `fixture_schema` | `1` |
| `capture_kind` | `offline_recording` or `reconstructed_chrome` |
| `checkpoints` | Labelled states for the suite |
| `sanitized` | `true` (no tokens, no home paths, no `.env`) |

Script line types (one JSON object per line):

- `{"type":"feed","encoding":"base64","data":"..."}`
- `{"type":"resize","cols":120,"rows":40}`
- `{"type":"checkpoint","name":"working"}`

Replay builds `GhosttyVtEngine(cols, rows, libraryPathOverride)`.  
It applies events in order.  
At each checkpoint it captures:

- `VtSnapshotNormalizer.ToCanonicalJson(CaptureSnapshot())`
- detection text = `GetRecentText(80)` (`PaneRuntime.ReadDetectionText()`)

## Capture rule

1. Prefer one offline capture of pinned `codex` / `claude` binaries.
2. If those binaries are not available, commit reconstructed chrome.
3. Set `capture_kind` honestly.
4. Do not write that sequences match a UI unless the bytes come from that UI.
5. Do not call live model APIs in CI.
6. Do not hash-only compare.

The reconstructed corpus uses `capture_kind=reconstructed_chrome`.  
It is not an offline PTY recording of a live binary.  
Recorded suites live in `g-agent-codex-recorded/` and `g-agent-claude-recorded/`.  
Those use `capture_kind=offline_recording`.  
They hold working, blocked, idle, and done.  
Codex recorded frames stay on the main screen.  
The pinned Codex binary does not emit `?1049h`.  
Claude recorded idle, working, and done enter alt-screen.  
Claude recorded blocked stays on the main screen.  
The scripts for the reconstructed corpus emit a generic 120×40 box TUI:

- DEC alt-screen `?1049h`
- hide cursor `?25l`
- synchronized update `?2026h` / `?2026l`
- DECSTBM scroll region
- CUP to the footer (row 40)
- box drawing
- RGB SGR (`38;2`)
- DECSC / DECRC

Do not print `reconstructed-chrome` on the grid.  
`agent_version` is the target UI pin (`0.147.0` / `2.1.233`). It is not proof of capture.  
Do not write that sequences match those UIs unless the bytes come from an offline recording.

Sanitize before commit. Do not store tokens, home paths, or `.env`.

120×40 full-grid JSON is large. That is acceptable.

## Minimum labelled checkpoints

Each reconstructed agent suite must include:

| Name | Chrome |
| --- | --- |
| `working` | Alt-screen chrome plus spinner / “Thinking” / tool line |
| `blocked` | Approval / “Do you want to proceed” / `(y/n)` |
| `idle` | Settled `›` / `❯` prompt above footer or status rows |
| `done` | “Done.” / “Completed” / equivalent chrome above footer |

G-detect adds unique scenes. It must not clone G-agent scripts.

- chrome-only Codex / Claude kind (`process_name` null)
- leftover Working chrome above an idle prompt or a Done line
- leftover blocked approval above a later idle prompt
- leftover What’s-new `…` in the 24-line window with a later prompt (Idle, not Working)
- unknown idle text
- stale “Thinking” above the 24-line window
- shell-only prompt
- wrong-kind chrome
- in-progress `Completed 3 of 10 tools` with no Working marker (not Done)

Detection labels must include `status` and `agent_kind` (or null).  
They must include `min_confidence`.  
They must include `max_confidence` when status is `unknown`.  
They must set `detection_source` to `GetRecentText(80)`.

## Detection thresholds (measure only)

Canonical source is `ReadDetectionText()` / `GetRecentText(80)`.  
Then call `HeuristicAgentDetector.Detect(text, process_name)`.

| Metric | Bound |
| --- | ---: |
| Structured snapshot mismatch | 0 |
| False negative: labelled `working` / `blocked` / `done` not detected | 0 / N |
| False positive: labelled `idle` / `unknown` reported as `working` or `blocked` | 0 / N |
| Kind mismatch when `process_name` or chrome kind is pinned | 0 / N |
| Unknown idle still `Unknown` @ `0.0` | must hold |

N = all labelled G-detect + G-agent + recorded checkpoints.

Current corpus: **N = 27** unique labelled scenes.

| Suite | Count | Labels |
| --- | ---: | --- |
| G-agent-codex | 4 | working, blocked, idle, done |
| G-agent-claude | 4 | working, blocked, idle, done |
| G-detect | 11 | chrome-kind-codex, chrome-kind-claude, leftover-working-idle, leftover-working-done, leftover-blocked-idle, leftover-ellipsis-idle, unknown-idle, stale-thinking, shell-only, wrong-kind, completed-count |
| G-agent-codex-recorded | 4 | working, blocked, idle, done |
| G-agent-claude-recorded | 4 | working, blocked, idle, done |

Measured rates after this gate:

| Metric | Rate |
| --- | ---: |
| Snapshot mismatch | 0 / 8 reconstructed agent goldens + 7 unique G-detect grids + 8 recorded |
| False negative | 0 / 16 labelled working+blocked+done |
| False positive working/blocked | 0 / 11 labelled idle+unknown |
| Kind mismatch | 0 / 25 (null-kind cases omitted) |
| Unknown idle | `Unknown` @ `0.0` |

These denoms include recorded working, blocked, idle, and done frames from the pinned UIs.

Idle/done frames keep leftover Working chrome above the prompt or Done line.  
The prompt or Done line sits above footer or status rows.  
At least one checkpoint per reconstructed agent suite omits `process_name`.  
Kind then comes from chrome.

Allowed detector edits: add Codex/Claude chrome markers.  
Allowed: classify the last prompt or Done line in the 24-line window and ignore status rows under it.  
Forbidden: change the unknown fallback.  
Forbidden: restore `Working@0.2`.  
Forbidden: make idle unknown look `Working`.  
Forbidden: treat a bare `…` as Working.

Hold these tests:

- `IntelligenceAndDetectorTests.Detector_unknown_idle_text_is_not_working_at_0_2`
- `AgentWaitUnknownIdleTests.Agent_wait_until_working_on_unknown_idle_times_out_not_busy`

## F2 capability matrix

| Capability | Decision |
| --- | --- |
| Alt screen, SGR, scroll region, wide char | Required. G-VT-alt already proves these. Agent fixtures must enter alt-screen. |
| Bracketed paste / mouse modes | Required on the working checkpoint per agent. This pin reports `bracketed_paste=true` and `mouse=normal` after `?2004h` / `?1000h`. Assert `modes` on the snapshot. |
| OSC 8 hyperlinks | **Deferred.** Schema v1 has no hyperlink cell field. This is an explicit non-claim. |
| Soft reflow / Windows ConPTY | Out of scope (Unix F2). |

Do not extend snapshot schema unless a golden cannot compare without it.

## RID matrix

Hard goldens run on the Unix runner that built `libghostty-vt`.

| Cell | Requirement |
| --- | --- |
| Managed goldens | Hard-required when the lib is present. `HYPA_REQUIRE_GHOSTTY_TESTS=1`. |
| RC-lib replay | Same staged `libghostty-vt` next to the Ghostty RC. AOT `VtGoldenGen --compare-h15` plus the agent golden filter. |
| AOT load smoke | Four-RID `dist` job in `build-dist.yml` (`verify-ghostty-rc-engine.sh`). |
| linux-x64 AOT execute | Residual. Last recorded run [31638514396](https://github.com/Hypabolic/Hypa-Private/actions/runs/31638514396) failed Zig fetch (`HttpConnectionClosing`). Do not invent green. |
| linux-arm64 / osx-arm64 / osx-x64 load | Succeeded on that run. Do not skip goldens on those RIDs when the lib loads. |
| Windows | Out of scope. `GhosttyTestRequire` skips unless hard-required. |

Scripts:

- `scripts/verify-h15-goldens.sh`
- `scripts/verify-h15-rc-replay.sh`
- `scripts/verify-ghostty-engine-tests.sh`
- `scripts/verify-ghostty-rc-engine.sh` (keep)

CI job `build-test` runs the engine script on all four RIDs after it builds `libghostty-vt` and `hypa-pty-host`.  
The `dist` job in `build-dist.yml` runs `verify-h15-rc-replay.sh` on the published lib before it packs F1 and F2.

Do not publish an F2 `hypa` tarball from this golden suite.

## Related documents

| Item | Path |
| --- | --- |
| Structured snapshot | [VT-structured-snapshot.md](VT-structured-snapshot.md) |
| F1 unsupported matrix | [VT-unsupported.md](VT-unsupported.md) |
| F2 golden gate | [docs/plans/AgentRuntime/f2-golden-gate.md](../../docs/plans/AgentRuntime/f2-golden-gate.md) |
| Ghostty pin | [native/ghostty/PIN.md](../../native/ghostty/PIN.md) |
