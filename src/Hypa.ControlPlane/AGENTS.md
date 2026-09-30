# Mux control plane

When you change mux I/O, render fanout, or live paint payload:

Mux sends pane cell grids. Mux does not send host ANSI. A live update
is a dirty-row cell payload, not pane-relative blit ANSI.
