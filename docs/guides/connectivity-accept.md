# Connectivity accept bind

The command `hypa connectivity accept` binds TCP to `--bind`.
The default bind address is `0.0.0.0`.

MsQuic opens one dual-mode wildcard UDP socket for the QUIC port.
It does not bind the address in `ListenEndPoint`.
That address is only a filter inside MsQuic.
The socket table shows every interface.

The helper starts QUIC only when `--bind` is `0.0.0.0` or `::`.
A specific address does not start QUIC.
TCP still listens on that address.
The command writes the reason on stderr.
The JSON field `quic_listening` is false.
The JSON field `quic_detail` has the reason.

Use TLS on the specific address when the join must use that address only.
Pass `--bind 0.0.0.0` or `--bind ::` when the join needs QUIC.
A wildcard bind opens the UDP port on every interface.

The JSON field `bind` is the TCP address.
The JSON field `quic_bind` is the QUIC address.
`quic_bind` is present only when QUIC listens.
That value is a wildcard address.
