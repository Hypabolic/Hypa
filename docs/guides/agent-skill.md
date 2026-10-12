# Agent skill

Hypa keeps the runtime skill at `skills/hypa-runtime/SKILL.md`.
The skill teaches an agent how to control Hypa from a Hypa pane.
This guide tells a person how to install that skill.

## What the skill does

The runtime skill applies when `HYPA_ENV` equals `1`.
That value means the agent runs in a Hypa pane.
The agent uses the `hypa` CLI for the current session.

With the skill, an agent can:

- inspect workspaces, tabs, panes, and agents
- split a pane and run a command
- read pane output
- wait for an agent
- start an agent in another pane
- create a hidden pane for background work

The skill file is Markdown.
`hypa --skill` prints the skill text from the installed binary.

## Runtime skill and compression skill

The runtime skill name is `hypa-runtime`.
That skill teaches control of panes, tabs, workspaces, and agents.
The compression skill name is `hypa`.
That skill teaches command rewrite and compressed tool output.

## Install the skill

### Integration install

The command needs a running mux.
Start the mux in one terminal. The command stays in the foreground:

```bash
hypa mux serve
```

Run this command in a second terminal to write the runtime skill for Claude:

```bash
hypa integration install claude
```

The command writes `~/.claude/skills/hypa-runtime/SKILL.md`.
`CLAUDE_CONFIG_DIR` overrides that directory.
Each other integration target has its own skill directory.
`hypa integration status` prints the skill state for each target.

Preview the files before a write:

```bash
hypa integration install --dry-run
hypa integration install claude --dry-run
```

`--dry-run` prints every file that the install writes.
The command writes nothing.
A command with no target prints one block for each target found on `PATH`.

The first-run card does not install an integration.
Continue opens the integrations page.
Enter installs the selected rows.
Esc writes nothing.
`u` removes the highlighted row.

`hypa init` writes the compression skill for Claude.
The file path is `~/.claude/skills/hypa/SKILL.md`.
The default scope is the user home.
Only the user-home install writes that file.

### Public repository

Install the skill from the public repository `Hypabolic/Hypa`:

```bash
npx skills add Hypabolic/Hypa --skill hypa-runtime -g
```

The command reads `skills/hypa-runtime/SKILL.md` on the public default branch.
The public default branch is `main`.
The sync writes that file on the public `develop` branch.
Install the skill after the public develop-to-main promotion.
The `-g` flag installs the skill for the current user.
Leave out `-g` for a project install.

For an agent with a skill system, install the file under the name `hypa-runtime`.
For an agent with no skill system, paste the file into the agent instructions.

### Print the bundled copy

Run this command:

```bash
hypa --skill
```

The command prints the runtime skill from the installed binary.
Paste the text into the agent instructions.

## Safety rule

The skill checks `HYPA_ENV` before a control command.
The agent sends a control command only when the value is `1`.
The agent stops when the value is not `1`.
The agent reports that it is not in a Hypa pane.

## Start in a Hypa pane

Start Hypa:

```bash
hypa
```

Start the coding agent in a Hypa pane.
The pane sets `HYPA_ENV` to `1`.

## Command detail

The skill file holds the command list.
Read the [runtime skill](../../skills/hypa-runtime/SKILL.md).
