# Share invite reach

The share dialog lists every reachable address of the host.
The invite carries those addresses.
A person does not set `HYPA_ADVERTISE_HOST` before they share.

The invite omits loopback, link-local, and wildcard addresses.
The listener can still bind every address.

A person limits the invite to one address in three ways.
Type the address in the `host` field under `advanced`.
Set `HYPA_ADVERTISE_HOST`.
Pass `--advertise-host` to `hypa connectivity accept`.

The `host` field shows `all addresses` when it is empty.
After a person types an address, the dialog shows `reach is this address`.
`peers dial` lists every address the invite carries.

The default bind address `0.0.0.0` serves IPv4 only. The invite then carries IPv4 addresses only.
Bind to `::` to serve IPv4 and IPv6. The invite then carries both.

Bind and advertise stay separate.
Bind selects the sockets that listen.
Advertise selects the addresses in the invite.
When the bind address is specific and the advertise field is empty, the invite carries only that bind address.

The transfer document stays schema 1.
The `host` field holds the first address.
The `hosts` field holds every address.
An old invite has no `hosts` field.
The client dials that one host.

The client dials each address in order.
It checks the certificate on the first connection that answers.
It sends the secret only after that check succeeds.
It records the address that answered.
A dead address does not fail the redeem while another address answers.
