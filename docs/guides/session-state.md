# Session screen history

`experimental.pane_history` is off by default.

Set `experimental.pane_history = true` to store recent pane screen in
`session-history.json`.

The mux writes that file next to the session graph. The file can hold secrets.

The mux does not write the file when the flag is off.
A later persist removes a stale history file.

Official agent restore owns conversation history.
History replay does not run when that restore plan exists.

This is not the attach copy-mode overlay.

## Enable

1. Set `experimental.pane_history = true` in the attach config.
2. Restrict file access on the mux state directory.
3. Restart the mux.

## Restore

The mux starts a new shell. It does not reattach the old process.
It seeds the pane VT from the saved ANSI when the flag is on.

## Official restore

When a pane has an official agent session and resume is on, the mux types the resume command.
It does not replay saved screen ANSI for that pane.
A duplicate session of the same agent also skips history replay.
