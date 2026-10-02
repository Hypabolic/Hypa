# Live workspace directory and repository identity

## Behavior

Each tab retains an identity pane: its original tiled pane. The workspace uses
that pane in its first tab, ordered by tab ordinal, regardless of focus. Other
panes and tabs may navigate independently. Hidden panes never supply workspace
identity. When the identity pane leaves the layout, promote the first remaining
layout leaf; retain that choice through subsequent splits and rearrangements.
Closing the first tab makes the next tab the identity source. Empty workspaces
fall back to their creation directory.

Track each pane's current directory using a valid local OSC 7 file URI, falling
back to the shell process directory on Linux and macOS. Accept absolute existing
directories only; reject remote URI authorities, controls, malformed encodings,
queries, and fragments. Replayed history must not become a live directory report.
The last observed directory survives process exit and restart. A new occupant
starts with fresh directory evidence.

Keep the workspace creation directory as a seed for explicit workspace operations.
Publish `resolved_cwd` and `identity_pane_id` alongside the existing `cwd` field.
The workspace panel uses resolved_cwd. Automatic labels use the repository root
name inside a repository and the directory basename elsewhere (`~` at home).
Explicit user labels always win. Navigating out of a repository clears its Git
metadata. Explicit worktree membership is independent of navigation.

## Refresh and ownership

The mux host observes directories and resolves Git metadata, so managed remote
sessions use the remote filesystem. Directory reports are observed with terminal
output; a one-second host timer also catches navigation without shell integration.
Output requests are coalesced to at most four observations per second; refreshes
do not overlap. Occupant identity, generation, and freeze
state guard directory writes. Changes update pane cwd, persist best effort, and
publish workspace lifecycle updates that cause attached clients to refresh.

Git probes run asynchronously, using the existing bounded cache, admission rules,
and timeout. Rendering and snapshot serialization only read cached results.
Repository discovery is keyed by the resolved directory, and status probes are
shared by checkout root. Separate worktrees retain independent status. A result
for an old directory cannot replace a new directory's metadata. Git metadata refreshes after
its five-second cache lifetime. Clients consume host-provided metadata instead of
probing a remote path locally.

## Persistence and compatibility

Add nullable tabs.identity_pane_id and workspaces.custom_label columns through
idempotent migrations. Old tabs select their first tiled layout leaf. For old
workspaces, labels equal to the creation directory or its basename are treated as
automatic; other labels are preserved as custom. New create and rename operations
record explicit label ownership. Existing cwd fields retain their meaning and new
wire fields are additive.

## Validation

Cover fragmented OSC 7 reports, invalid and remote URIs, process directory lookup,
root pane selection through focus, split, hide, move, and close, custom label
ownership across persistence, navigation between repositories and outside Git,
remote metadata consumption, and rejection of stale occupant observations.
