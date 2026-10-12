# Remote SSH attach

This guide describes `hypa --remote`. The local process is a thin attach
client. The remote mux owns panes, PTYs, and VT state.

This path is not Atomic gateway attach. Atomic A-04 and A-07 mint a
capability and attach through a gateway. `hypa --remote` uses OpenSSH
only.

## Command

```text
hypa --remote <target>
hypa --remote <target> --session <name>
hypa --remote <target> --remote-keybindings server
hypa --remote <target> --handoff
```

`--remote` accepts an OpenSSH destination. Examples: `host`,
`user@host`, and `ssh://user@host`.

The target must not start with `-`. The target must not contain a
password. OpenSSH owns keys and credentials. Hypa does not store them.

Default keybindings are local. `--remote-keybindings server` uses the
product default table. Local custom commands are not sent.

## Session

`--session` selects the named remote mux session. Spaces stay workspaces
on that mux. A later Cubes `peer` row uses the same profile contract:
id, label, target, session, enabled, and provider.

Provider is `ssh`. Kind stays `peer`.

## Transport

After probe and optional remote server start, the local client binds a
private Unix listener. Each accepted connection spawns SSH stdio to
`hypa remote-client-bridge` on the remote host. That bridge copies bytes
between stdio and the remote mux socket. The attach client then dials the
local listener and uses the same control-plane attach path as local mux.

OpenSSH owns credentials. Hypa stores no credential material.

## SSH config

`[remote].manage_ssh_config` writes a private temporary SSH config.
The file includes the user config first. User keepalive values win.
The file then sets `ServerAliveInterval 15` and `ServerAliveCountMax 4`.

Linux and macOS also use a private control socket. Windows OpenSSH
does not.

## Handoff

`--handoff` is experimental. It is Unix-only. Other platforms fail
closed.

The remote method is `server.live_handoff`. The CLI driver is
`hypa mux live-handoff`. That path replaces the remote mux process.
It does not move Work. It does not satisfy a Continuity generation fence.

A destructive restart needs operator consent on an interactive terminal.

## Platform

Linux and macOS are the mux hosts.
Windows is not a mux host.
A Windows remote host is refused. The client detects the remote OS
with `uname`.
See [`ADR 0015`](../ADRs/0015-windows-is-not-a-mux-host.md).

On Linux and macOS, a named QUIC peer uses QUIC.
SSH is the fallback when QUIC cannot start.

## Reconnect

Each connection attempt gets a generation. The client rejects a stale
generation. Reconnect keeps the named remote session. Reconnect does
not start a second remote mux when one is already running.

## Listen

This path does not add mux `--listen`. The mux must not bind a public address.
