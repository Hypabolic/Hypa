# Hypa CLI mux and attach

When you change Attach or Mux, implement it as close or identical to

Compose one host-size cell frame, then encode once. Do not write pane
blit ANSI to the host TTY. Do not CUP-remap pane ANSI onto chrome.
