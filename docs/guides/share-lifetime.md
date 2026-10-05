# Share lifetime

The mux owns the share listener.
The attach that started share does not.
Closing or detaching that attach does not stop share.
Peers keep their connection.

Start share in the share dialog.
Share runs until a person presses stop in the share dialog.
Any attach on the same mux shows the running share and can stop it.

## What runs

The mux runs `hypa connectivity accept` as a child process.
The listener is a separate process so a fault in the network stack cannot stop your panes.
The mux holds the child's stdin open.
The child exits when that pipe closes.
A mux that exits for any reason, even `SIGKILL`, takes the listener with it.

## Restarts

The mux starts the listener again when it exits.
It waits 1 second, then 2, 4, 8, 16, and at most 30 seconds between tries.
A listener that ran for a minute resets the wait.
The share dialog shows the last error while the mux retries.
The dialog still offers stop, because share stays on while it retries.
To retry now, press stop and then start.

## Mux restarts and upgrades

The mux writes `share.json` beside `hypa.sock` and `accept.pfx` while share is on.
The next mux on that session reads it and starts share again.
Stop removes the file.
`accept.pfx` keeps the same certificate, so saved peers keep the same pin.

## Control plane

| Method | Result |
| --- | --- |
| `cube.share.status` | Current share state. |
| `cube.share.start` | Turn share on. `bind` defaults to `0.0.0.0`. `port` defaults to 7443. |
| `cube.share.stop` | Turn share off. |

Each method returns `enabled`, `state`, `bind`, `port`, `listen`, `error`, `restarts`, and `persist_error`.
`bind` must be an IP address.
`persist_error` says when `share.json` could not be written or removed.
`state` is `stopped`, `starting`, `running`, or `retrying`.
`listen` is present while `state` is `running`.
It holds the bound address, port, certificate fingerprint, QUIC status, and the pairing store the listener admits joins from.
The share dialog mints invites in that pairing store.

A mux from before this change has no share methods.
The share dialog then asks you to restart the mux.
