<img width="1169" height="581" alt="Hypa" src="https://github.com/user-attachments/assets/f8390eab-a3b7-4227-9c8e-da61d2b3663d" />

# Hypa

Hypa is a workspace for coding agents. It runs your agents in terminal panes, keeps them alive when you close the terminal, lets agents drive other panes, and cuts the noise from command output before it reaches an agent.

Hypa is local-first. It needs no cloud service.

## What Hypa does

- **Workspace mux.** `hypa` starts a local server that owns your panes. Attach from any terminal, detach, and attach again. Tabs, splits, workspaces, a sidebar, copy mode, popups, themes, and settings are built in.
- **Agent runtime.** An agent in a Hypa pane can read other panes, start work in a new pane or a hidden pane, wait for another agent, and report its state. Hypa installs a skill that teaches an agent how.
- **Share and connect.** Share a session with another machine. The other machine connects over QUIC, with TCP and TLS as the fallback. A certificate pin protects every connection.
- **Output compression.** `hypa -c "command"` runs a command, removes noise from the output, and keeps what matters: errors, warnings, file paths, failing tests, and exit codes. The reduction is deterministic and local. It is not an LLM summary.

## Install

Pick the channel that fits how you manage tools. Every channel installs the same release archive for your platform, so the mux works the same way in each.

| Channel | Command | Update with |
| --- | --- | --- |
| Install script | `curl -fsSL https://raw.githubusercontent.com/Hypabolic/Hypa/main/install.sh \| sh` | `hypa update` |
| Homebrew | `brew install hypabolic/tap/hypa` | `brew upgrade hypa` |
| npm | `npm install --global @hypabolic/hypa` | `npm install --global @hypabolic/hypa@latest` |
| PyPI | `pipx install hypa` | `pipx upgrade hypa` |
| Release archive | Download from [GitHub Releases](https://github.com/Hypabolic/Hypa/releases) | Download again |
| Source | [Build from source](#build-from-source) | `git pull` and build again |

Releases from 1.0.1 on are published to Homebrew, npm, and PyPI. Version 1.0.0 is available only as a release archive and through the install script.

Install one copy. If you switch channels, remove the old copy first, so that only one `hypa` is on your `PATH`.

Check any install:

```bash
hypa --version
hypa doctor
```

### Requirements

| Platform | Needs |
| --- | --- |
| Linux x64 or arm64 | glibc 2.34 or newer. Alpine and other musl systems are not supported. |
| macOS x64 or arm64 | macOS 12 or newer. |
| Windows | Not a mux host. See [Windows](#windows). |

The npm package needs Node.js 18 or newer. The PyPI package needs Python 3.9 or newer. Neither needs a .NET runtime.

### Install script

Linux and macOS:

```bash
curl -fsSL https://raw.githubusercontent.com/Hypabolic/Hypa/main/install.sh | sh
```

The installer downloads the release archive for your platform, checks it against `SHA256SUMS`, and installs `hypa` in a bin directory that you can write to (`~/.local/bin` by default). It prints a warning when that directory is not on your `PATH`.

Installer options:

```bash
HYPA_VERSION=1.0.1 HYPA_INSTALL_DIR="$HOME/bin" sh install.sh
HYPA_ARCHIVE=/path/to/hypa-osx-arm64.tar.gz HYPA_INSTALL_DIR="$HOME/bin" HYPA_APP_DIR="$HOME/share/hypa" sh install.sh
```

### Homebrew

macOS and Linux:

```bash
brew install hypabolic/tap/hypa
```

This adds the `hypabolic/tap` tap and installs the formula. Homebrew keeps the whole release directory in the Cellar and links `hypa` into your `PATH`.

### npm

```bash
npm install --global @hypabolic/hypa
```

The `@hypabolic/hypa` package picks the matching platform package, for example `@hypabolic/hypa-darwin-arm64`, as an optional dependency. If your package manager skips optional dependencies, install the platform package yourself.

### PyPI

```bash
pipx install hypa
# or
uv tool install hypa
# or
pip install hypa
```

Prefer `pipx` or `uv tool`, which keep `hypa` out of your project environments. The wheel carries the native build, and the `hypa` command starts it.

### Release archive

Download the archive for your platform from [GitHub Releases](https://github.com/Hypabolic/Hypa/releases), together with `SHA256SUMS`, and check the archive against it.

| Platform | Archive |
| --- | --- |
| Linux x64 | `hypa-linux-x64.tar.gz` |
| Linux arm64 | `hypa-linux-arm64.tar.gz` |
| macOS x64 | `hypa-osx-x64.tar.gz` |
| macOS arm64 | `hypa-osx-arm64.tar.gz` |

The archive unpacks to a `hypa-<platform>` directory. Keep its files together: `hypa` finds the mux host, the attach client, and its native libraries next to itself. Run `hypa` from that directory, or link it into a directory on your `PATH`.

### Update

`hypa update --check` shows whether a newer release exists. `hypa update` upgrades a copy that the install script put in place. For a copy that a package manager installed, it prints the command to run:

| Installed with | Upgrade with |
| --- | --- |
| Homebrew | `brew upgrade hypa` |
| npm | `npm install --global @hypabolic/hypa@latest` |
| pipx | `pipx upgrade hypa` |
| uv | `uv tool upgrade hypa` |
| pip | `python3 -m pip install --upgrade hypa` |

After an upgrade, a running mux server still runs the old version. When you next attach, Hypa offers to restart it. While you are attached, the sidebar shows **↻ restart to update**; click it to restart the mux and reattach on the new version. From a terminal outside Hypa, run `hypa mux restart`. A restart closes every pane.

### Windows

Windows is not a mux host. There is no Windows mux archive, and `install.ps1` stops with an error. The compression CLI can still run on Windows. Build it from source (see below).

## Quick start

```bash
hypa
```

Bare `hypa` starts the local mux server, or reconnects to it, and attaches. The first run shows an onboarding overlay.

Keys use a prefix, `ctrl+b` by default:

| Keys | Action |
| --- | --- |
| `prefix` then `?` | List the active key bindings. Press `/` to filter. |
| `prefix` then `q` | Detach. The server and your panes keep running. |
| `prefix` then `s` | Open settings. |

Close the terminal, or detach, and attach again with `hypa`. Panes keep running while you are away.

Install the agent integration. Hypa finds the agents on your machine and shows every file it writes before it writes:

```bash
hypa integration install
hypa integration status
```

`hypa init` installs hooks and skills for the detected agent harnesses. Use `--dry-run` to preview either command.

## Workspace mux

A session holds workspaces, each workspace holds tabs, and each tab holds panes. One mux server owns one session.

```bash
hypa --session NAME                  # attach to a named session
hypa session list
hypa session attach NAME
hypa session stop NAME
hypa session delete NAME
hypa status                          # mux client and server status
hypa mux serve --session default     # run the server without attaching
hypa mux stop
hypa mux restart                     # stop, then start on the installed version
```

`hypa ping`, `hypa snapshot`, `hypa workspace`, `hypa tab`, `hypa pane`, and `hypa layout` talk to a running server. They do not start it.

Attach to a mux on another machine through OpenSSH:

```bash
hypa --remote user@host
hypa --remote user@host --session NAME
```

OpenSSH owns keys and credentials. Hypa stores none. See [`docs/guides/remote-ssh-attach.md`](docs/guides/remote-ssh-attach.md).

Panes use a real PTY and the Ghostty terminal engine. Hypa paints one composed frame for your terminal. The host palette, focus events, mouse, and bracketed paste reach the panes, and arrow, Home, and End keys use the form that the running program asks for.

Customize Hypa in `~/.config/hypa/config.toml`:

```bash
hypa --default-config                # print the default file
hypa config check                    # validate your file
```

There are 19 built-in themes. An invalid value in the file stops Hypa with an error. It does not fall back in silence.

A saved screen history for restored panes is off by default. See [`docs/guides/session-state.md`](docs/guides/session-state.md).

## Agent runtime

Every pane child gets `HYPA_ENV=1`, `HYPA_BIN_PATH`, `HYPA_RUNTIME_SOCKET`, and pane identity keys. An agent uses the `hypa` command with those keys to control the session. See [`docs/guides/pane-environment.md`](docs/guides/pane-environment.md).

```bash
hypa --skill                         # print the agent skill
hypa pane --help                     # pane operations for scripts and agents
hypa api schema --json               # the control-plane protocol
```

What an agent can do:

- Inspect workspaces, tabs, panes, and agents.
- Split a pane and run a command, then read its output or wait for it.
- Start another agent in a new pane.
- Create a **hidden pane** for background work. A hidden pane has a live PTY and no place in the layout. You can show, hide, and read it from the attach view. See [`docs/guides/background-panes.md`](docs/guides/background-panes.md).

The skill is installed for the agents that Hypa detects. See [`docs/guides/agent-skill.md`](docs/guides/agent-skill.md). Hypa supports many agent harnesses, including Claude, Codex, Copilot, Cursor, Grok, Pi, and others. Run `hypa integration install --help` for the list.

A plugin runs as your user with full permissions. It is not a sandbox.

## Share and connect

Open the share dialog in the attach view to make an invite. The invite carries every reachable address of your machine, so you do not set an address first. The other machine redeems the invite, tries each address, and reports the address that answered.

- QUIC is the first path. TCP with TLS is the fallback.
- A certificate pin protects every connection. The secret is sent only after the pin matches.
- A saved peer is a **cube**. Connect moves your view to that peer.
- `hypa connectivity accept` runs the listening side by hand. `hypa device` pairs and revokes devices.

An invite holds at most 32 addresses. Use `--advertise-host` to name one address. See [`docs/guides/share-invite.md`](docs/guides/share-invite.md) and [`docs/guides/connectivity-accept.md`](docs/guides/connectivity-accept.md).

## Output compression

Hypa runs a command, reduces the output, and returns a compact result:

```text
shell command
  -> Hypa command runner
  -> command-specific reducer, when available
  -> built-in or trusted DSL filter, when applicable
  -> token accounting and savings metadata
  -> compact output returned to the caller
```

If the result saves tokens, Hypa appends a footer:

```text
[hypa: 1200→340 tok, -72%, reducer=dotnet-build]
```

<img width="1272" height="744" alt="Hypa output compression" src="https://github.com/user-attachments/assets/ed7bcd40-041e-4be7-a575-fc9af814d64d" />

For failures or truncation, Hypa can tee the full output to a local artifact, so the compact output stays small and you can still recover everything.

```bash
hypa -c "dotnet test"                # run and compress
hypa -t dotnet test                  # run unmodified, streamed to the terminal
hypa git status                      # first-class wrappers: git, dotnet, kubectl, docker
hypa rewrite "git status"            # show how a command would be rewritten
hypa filters list                    # built-in and configured filters
hypa filters test NAME ./output.txt  # test a filter against a saved output
hypa filters savings --markdown      # estimate savings for the filter suite
hypa read PATH                       # read a file in a context-aware mode
hypa search QUERY                    # search files, symbols, and indexed context
hypa code                            # index and query source code structure
hypa compress                        # compress text from stdin or a file
```

Built-in filters cover build and test tools (`dotnet`, `cargo`, `gradle`, `mvn`, `go test`, `jest`, `pytest`, `xcodebuild`), package managers (`npm`, `pnpm`, `yarn`, `pip`, `poetry`, `uv`), linters, infrastructure tools (`terraform`, `helm`, `kubectl`, `docker`, `aws`, `gcloud`), system tools, monorepo task runners, and source control. Run `hypa filters list` for the current list.

Token estimates use `Microsoft.ML.Tokenizers` with the `o200k_base` tokenizer. Savings reports use synthetic payloads. Treat them as repeatable estimates, not as a measure of your own projects.

Hypa runs trusted project filters from a repository `.hypa/` directory. It does not run every repository filter on its own. Use `hypa trust status` and `hypa trust filters`.

Hypa also works as an MCP server (`hypa serve`) and through agent hooks (`hypa hook`, installed by `hypa init`). The Pi extension has its own guide: [`docs/guides/pi.md`](docs/guides/pi.md).

## Local data

Hypa keeps runtime data in `~/.hypa/`:

- `hypa.db`: SQLite database with sessions, command metrics, parse metrics, trust records, and artifacts.
- `artifacts/`: full command output, kept for recovery.
- `config.json`: optional compression configuration.

The mux configuration is `~/.config/hypa/config.toml`. It is a separate file from `config.json`. Do not merge them.

## Platforms and limits

- Linux and macOS are the mux hosts (`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`).
- Windows is not a mux host. See [`docs/ADRs/0015-windows-is-not-a-mux-host.md`](docs/ADRs/0015-windows-is-not-a-mux-host.md).
- A running mux keeps the binary that started it. After an upgrade, stop the old mux and start it again.
- QUIC does not start when you bind `hypa connectivity accept` to a specific address. That accept uses TCP with TLS.

## Build from source

You need the .NET 10 SDK. Linux or macOS is required for the mux.

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes --no-restore
dotnet run --project src/Hypa.Cli -- --help
```

Publish a local binary (Linux x64 example):

```bash
dotnet publish src/Hypa.Cli/Hypa.Cli.csproj -c Release -r linux-x64
```

The executable is written under `src/Hypa.Cli/bin/Release/net10.0/linux-x64/publish/`. Put that directory on your `PATH`, or link the executable as `hypa`.

To run the current working tree as your `hypa` command while a release is installed, use the preview helper:

```bash
scripts/hypa-preview install --test    # publish, test, and switch to the preview
scripts/hypa-preview install --fast    # skip tests and NativeAOT
scripts/hypa-preview status
scripts/hypa-preview restore           # switch back to the release
```

While a preview is active, `hypa update` does not replace it.

## Documentation

- [`docs/guides/`](docs/guides/): how-to guides for the mux, the agent skill, panes, sharing, and remote attach.
- [`docs/architecture/`](docs/architecture/) and [`docs/ADRs/`](docs/ADRs/): design and decisions.
- [`docs/release-notes/`](docs/release-notes/): release notes.

## Status

Hypa 1.0.0 is the first release with the workspace mux and the agent runtime. Report faults at <https://github.com/Hypabolic/Hypa/issues>.

## License

Hypa is licensed under the Functional Source License, Version 1.1, with an Apache 2.0 future license. See `license.md` in the public repository.
