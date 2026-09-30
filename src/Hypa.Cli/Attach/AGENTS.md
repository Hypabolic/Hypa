# Attach host paint

Implement this path as close or identical

Compose one host-size cell frame. Stamp chrome. Stamp pane cells into
the inner rectangle. Encode once. Never write mux pane blit ANSI to the
host TTY.

Wheel and scrollbar call `pane.scroll` only. Stamp Ghostty origin cells
at the inner rectangle. Do not seed `PaneHistoryView` for wheel. Do not
rebuild scrollback from `pane.read` text.

Do not grow `PaneLiveClip` as a second host VT.
Do not keep `VtBlitChromeRemapper`.
