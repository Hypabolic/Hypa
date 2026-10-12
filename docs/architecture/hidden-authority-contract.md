# Hidden-authority contract — occupant, parent, and provenance

This contract is the durable interface for authority, create provenance,
and show or hide authorization. Tiled and overlay show share the same
occupant, parent, and same-socket lease credentials. Overlay also
needs `attach_client_id`.

This is a Hypa product slice.
placement capability.
resolution.

The client owns chrome compose. The mux owns `AppState`, PTY, and VT.
Do not write pane-relative ANSI. Do not add a second host encoder.

It does not issue a secret.

- `src/pane.rs:92-108` `PaneLaunchEnv` holds `PaneLaunchIdentity`.
- `src/pane.rs:118-130` `with_identity` stores workspace, tab, and pane
  id.
- `src/pane.rs:138-156` `apply_pane_launch_env` writes
- `src/workspace.rs:363-367` workspace spawn calls `with_identity`.
- `src/app/popup.rs:139` popup spawn calls `without_pane_identity`.
  Hidden occupancy is not a popup. Do not omit pane identity.

Hypa already copies the managed identity sequence. It writes
`HYPA_PANE_ID` at spawn. That public id is not a credential.

## Scope

In scope:

- Occupant credential at runtime spawn
- Parent capability on create
- Validated `parent_pane_id` provenance
- Persist provenance only
- Sequence check and apply
- Create integration
- Tiled and overlay show and hide credentials
- Attach-client owner fields on snapshot and overlay show
- CLI argument transport for create, show, and hide

Out of scope for this contract:

- Combined conformance results
- A settings page that grants placement

Agent CLI steps live in
[`../guides/background-panes.md`](../guides/background-panes.md).
Human menu evidence lives in
[`../issues/2026-08-31-h-140-hidden-pane-human-menu.md`](../issues/2026-08-31-h-140-hidden-pane-human-menu.md).

## Identity and secrets

Occupant identity uses an unforgeable per-pane token. The token is
issued at runtime spawn. `HYPA_PANE_ID` is public. A caller-supplied
`pane_id` is not proof.

Parent capability is issued when a caller creates a child. That
capability authorizes that child only.

Status authority from `pane.report_agent` does not grant placement.
A blocked status does not grant show or hide.

### Environment

Child spawn environment:

| Key | Secret | Meaning |
| --- | --- | --- |
| `HYPA_PANE_ID` | no | Public pane id. |
| `HYPA_PANE_TOKEN` | yes | Occupant credential for this pane and generation. |

Do not export `HYPA_PANE_TOKEN` to popups. Do not write it to disk.

### Create result credentials

`pane.create` returns public pane fields plus one parent secret:

```json
{
  "pane_id": "p_child",
  "tab_id": "tab_1",
  "workspace_id": "w_1",
  "placement": "hidden",
  "hidden": true,
  "parent_pane_id": "p_parent",
  "parent_capability": "$PARENT_CAP"
}
```

`parent_capability` appears on the create result only.
`pane.get`, `pane.list`, and `session.snapshot` omit it.
`occupant_token` is not on the create result. The child reads it from
`HYPA_PANE_TOKEN`.

### Wire fields

`pane.create` extra params:

| Field | Required | Meaning |
| --- | --- | --- |
| `parent_pane_id` | no | Claimed parent id. The mux does not trust it. |
| `occupant_token` | no | Occupant token of the claimed parent. |
| `lease_id` | no | Owner lease on the claimed parent. |

`pane.show` params:

| Field | Required | Meaning |
| --- | --- | --- |
| `pane_id` | yes | Target pane. |
| `occupant_token` | no | Occupant credential for that pane. |
| `parent_capability` | no | Parent capability for that child. |
| `lease_id` | no | Human owner lease. |
| `seq` | occupant and parent | Monotonic retry id. |
| `mode` | no | `tiled` or `overlay`. Default `tiled`. |
| `attach_client_id` | overlay | Overlay owner from `attach_clients`. |
| `focus` | no | Tiled show may keep session focus off. |
| `area_cols` / `area_rows` | no | Overlay inner size hint. |
| `overlay_generation` | no | Overlay hide generation. |
| `tab_id` | no | Target tab. |
| `target_pane_id` | no | Split neighbour. |
| `direction` | no | `right` or `down`. |
| `ratio` | no | Split ratio in `(0,1)`. |

`pane.hide` params:

| Field | Required | Meaning |
| --- | --- | --- |
| `pane_id` | yes | Target pane. |
| `occupant_token` | no | Occupant credential. |
| `parent_capability` | no | Parent capability. |
| `lease_id` | no | Human owner lease. |
| `seq` | occupant and parent | Monotonic retry id. |
| `attach_client_id` | overlay hide | Overlay owner. Omit to use this socket. |
| `overlay_generation` | no | Overlay hide generation. |

## Capability lifecycle

The mux holds secrets in process memory only.

Issue:

1. Spawn issues one occupant token for that pane generation.
2. Successful create issues one parent capability for that child.
3. Restore spawn issues a new occupant token. Old secrets stay dead.

Revoke:

- Pane close revokes occupant token and parent capability.
- Failed spawn revokes any issued occupant token.
- Occupant replace revokes the old occupant token.
- The mux issues a new occupant token for the new generation.
- Parent capability survives occupant replace on the same pane.
- Process exit revokes the occupant token.
- Parent capability stays until pane close.

Independence:

- Status authority does not revoke placement secrets.
- Placement secrets do not change waits or agent status.
- Parent close does not erase child `parent_pane_id`.

Reconnect:

- Persist `parent_pane_id`.
- Do not persist secrets.
- After restart, parent capability is gone.
- The new occupant uses the new token.
- A human lease remains valid.

## Confidentiality

Do not write occupant tokens or parent capabilities to:

- `layout.export`
- persisted graph
- journal payloads
- normal logs
- status sources
- `pane.get` / `pane.list` / `session.snapshot`
- other panes

`parent_pane_id` is not a secret. Sidebar tree may read it.

## Validated parent field

The mux persists `parent_pane_id` only after proof.

Proof is one of:

1. Occupant token that matches a live parent pane.
2. Active owner lease on the claimed parent pane.

Owner lease scopes are `input` and `admin`.
The mux sets `HolderId` from the authenticated connection.
A caller cannot supply holder identity on the wire.

Reject:

- A claimed `parent_pane_id` with no proof.
- An occupant token for a different pane.
- A status source used as parent proof.
- A foreign or expired lease.

When proof is absent and `parent_pane_id` is absent, create succeeds.
The child has no parent provenance. The caller still receives
`parent_capability`.

## Sequence

Sequence binds to pane and credential kind.

Key: `pane_id` + role + credential id.

Rules:

1. Occupant and parent calls require `seq`.
2. A human lease may omit `seq`.
3. `seq` less than or equal to the last accepted value is stale.
4. A stale retry returns success with `changed=false`.
5. The mux does not mutate on a stale retry.
6. Check and apply share one lock.
7. A failed graph mutation does not consume `seq`.
8. A later retry with the same `seq` may apply.

## Overlay fences

Overlay show needs an explicit attach-client owner.
A busy modal surface rejects overlay show with `-32005`
and message `ui_busy`.

`session.snapshot` lists:

| Field | Meaning |
| --- | --- |
| `attach_clients` | Rows with `attach_client_id`, `mode`, and `client_mode`. |
| `attach_client_ids` | The same ids only. No `client_mode`. |

Pick the `attach_clients` row whose `client_mode` is `terminal`.
Require exactly one ready row. Do not take an id from
`attach_client_ids` when `client_mode` is missing or busy.
There is no attach-client environment variable.

## Public service symbols

```
IPanePlacementAuthorityService
  IssueOccupant(PaneId paneId, int generation)
    -> string token
  IssueParentCapability(PaneId childPaneId)
    -> string capability
  ValidateCreateParent(PlacementParentProof proof)
    -> Result<PaneId?, PlacementAuthorityError>
  Authorize(PlacementAuthorityRequest request)
    -> Result<PlacementAuthorityGrant, PlacementAuthorityError>
  Apply(PlacementAuthorityRequest request, Func<Result<T, E>> mutate)
    -> Result<PlacementApplyResult<T>, PlacementAuthorityError>
  RevokePane(PaneId paneId)
  RevokeOccupant(PaneId paneId)
  HasOccupant(PaneId paneId)
  HasParentCapability(PaneId paneId)

PlacementParentProof
  string? ClaimedParentPaneId
  string? OccupantToken
  string? LeaseId
  string? HolderId
  string? StatusSource

PlacementAuthorityRequest
  PaneId PaneId
  string? OccupantToken
  string? ParentCapability
  string? LeaseId
  string? HolderId
  string? StatusSource
  long? Sequence
  string Mode                 // tiled | overlay
  string? AttachClientId
  IOverlayPlacementFence? Fence

PlacementAuthorityGrant
  PlacementAuthorityRole Role
  PaneId PaneId
  string CredentialId
  bool SequenceRequired

PlacementApplyResult<T>
  bool Changed
  T? Value

PlacementAuthorityError
  string Code
  string Message
  static ForgedIdentity
  static ForeignCredential
  static StatusCredential
  static MissingSequence
  static StaleSequence
  static MissingAttachClient
  static OverlayBusy
  static UnprovenParent
  static NotFound

PlacementAuthorityRole
  Occupant | Parent | HumanLease

IOverlayPlacementFence
  Check(string mode, string? attachClientId)
    -> Result<bool, PlacementAuthorityError>

Result<T, E> lives in Hypa.AgentRuntime.Domain.
```

`Apply` serializes authorize, sequence check, mutate, and sequence
commit. A failed mutate leaves the last accepted sequence unchanged.

## CLI syntax

Create with optional parent proof:

```
hypa pane create [--workspace ID] [--command CMD] [--args ARG]...
  [--label L] [--placement hidden|tiled]
  [--parent-pane-id ID] [--occupant-token TOKEN] [--lease-id ID]
```

The CLI copies `HYPA_PANE_TOKEN` into `--occupant-token` when the
flag is absent. An explicit `--parent-capability` or `--lease-id`
suppresses that copy. The CLI copies `HYPA_PANE_ID` into
`--parent-pane-id` only when the occupant token is in use.

Show from the occupant environment:

```
hypa pane show [--pane-id ID] [--mode tiled|overlay] [--seq N]
  [--tab-id ID] [--target-pane-id ID] [--direction right|down]
  [--ratio N] [--no-focus] [--attach-client-id ID]
  [--occupant-token TOKEN | --parent-capability CAP]
```

When `--pane-id` is absent, the CLI uses `HYPA_PANE_ID`.
When `--occupant-token` is absent, the CLI uses `HYPA_PANE_TOKEN`.
An explicit `--parent-capability` suppresses the token copy.
`--no-focus` keeps session focus.

A hidden self-show into an occupied owner tab with no split target
opens a new tab in the same workspace. A repeated show of a pane
that is already tiled in that tab is a no-op. An explicit
`--target-pane-id` and `--direction` still split that neighbour.

Overlay show uses the same occupant or parent proof. It also
needs `--attach-client-id` from the one `attach_clients` row
whose `client_mode` is `terminal`.

Show from the parent after create:

```
hypa pane show --pane-id "$CHILD" --parent-capability "$PARENT_CAP" \
  --mode tiled --seq "$PSEQ"
```

Hide:

```
hypa pane hide [--pane-id ID] [--seq N] [--attach-client-id ID]
  [--occupant-token TOKEN | --parent-capability CAP]
```

`--lease-id` remains a wire field for a same-socket human or
control client. The mux binds holder identity to that connection.
A CLI process cannot reuse a lease from another invocation.

Expected errors use the catalog. Occupant and parent failures use
`-32009` `capability_invalid`. An unproven parent on create uses
`-32602` `invalid_params`. Overlay busy uses `-32005` and message
`ui_busy`. A missing overlay owner uses `-32602`.

## Parent command examples

A parent agent creates a hidden child. The create result returns
`parent_capability`. The parent then shows that child.

```
hypa pane create --placement hidden --command /bin/echo \
  --parent-pane-id "$HYPA_PANE_ID" --occupant-token "$HYPA_PANE_TOKEN"
hypa pane show --pane-id "$CHILD" --parent-capability "$PARENT_CAP" \
  --mode tiled --seq "$PSEQ"
```

The child asks for tiled visibility from its own env.

```
hypa pane show --mode tiled --seq "$SEQ"
```

That call opens a new tab only when the pane is hidden and the
owner tab already has leaves.

## Graph apply

`pane.show` and `pane.hide` authorize first. Tiled mode then
calls `ShowTiled` or `HideTiled`. Overlay mode uses the overlay
owner map and `attach_client_id`. A failed graph mutation does
not consume `seq`. A failed durable persist rolls back only that
pane and does not consume `seq`. Hide restore puts the pane
back on the same split side with the same direction and ratio.
Other tab labels, ratios, zoom, and focus stay. A stolen
session focus returns to the prior workspace tab when no later
focus change exists. A same-socket human lease remains valid.
