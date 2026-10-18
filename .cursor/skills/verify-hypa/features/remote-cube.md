# Remote cube

A second container is the cube host. The GUI client pairs with it and drives a shell there.

## Sub-features

- `share-open` starts sharing from the sidebar and shows an invite.
- `pair-redeem` pastes that invite in Add cube and names the cube `remote`.
- `remote-shell` prints `cubehost` and `onlyonhost` from the host.
- `remote-detach` and `remote-reattach` leave the client and open the cube again.
- `remote-handoff` runs `hypa mux live-handoff` on the host. The client stays up.
- `link-drop` rejects the path, then lets it through. The shell then prints `LINKBACK`.

## How to get to it (user POV)

- Host: Share in the cubes section, then Enter.
- Client: Add cube, paste the `hypa-invite:` code, name it, Enter, then click the cube.
- Detach with `ctrl+b` then `q`. Attach again and click the cube.
- On the host, `hypa mux live-handoff`.
- A dropped path shows `Reconnecting to`. The shell paints again when the path returns.

## Driving it with verify-mux-cube

```bash
bash .cursor/skills/verify-hypa/scripts/verify-mux-cube.sh
```

The host writes `invite.txt` from the share dialog. The client types that code. `onlyonhost` is a file that exists only on the host. `cubehost` is that container's hostname.

## Gotchas

- Add and Share are sidebar clicks. The drive clicks the cell the xterm log shows.
- The invite secret is only on screen. The drive reads it from the share dialog.
- `HYPA_ADVERTISE_HOST` is the address the client can dial. The host publishes port 7443.
- Reattach does not connect by itself. The drive clicks the cube again.
- The link drop rejects the gateway from the client, then that rule is removed. A silent drop does not wake the client.
