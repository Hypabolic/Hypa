# Key bindings

Prefix then `?` opens the list of active key bindings.

## Sub-features

- `help-open` shows the binding list.

## How to get to it (user POV)

- Finish onboarding, press `ctrl+b`, then press `?`.

## Driving it with verify-mux-gui

Preconditions:

- The onboarding overlay is what a new session shows first. The script presses Enter before the prefix.

- **Continue.** Enter leaves onboarding.
- **Open help.** `ctrl+b`, then `shift+slash` (the `?` key). `keybindings-after.png` and `xterm.log` show the binding list.

```bash
bash .cursor/skills/verify-hypa/scripts/verify-mux-gui.sh keybindings
```

## Gotchas

- `?` is `shift+slash` on a US keyboard. The container does not send a literal question mark from another layout.
- Prefix then `?` while the overlay is still up arms prefix mode on the overlay. The script continues first.
- The help key in the container config is `prefix+?`.
