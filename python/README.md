# Hypa

Hypa is a workspace for coding agents. It runs your agents in terminal panes, keeps them alive when you close the terminal, lets agents drive other panes, and cuts the noise from command output before it reaches an agent.

Hypa is local-first. It needs no cloud service.

## Installation

```bash
pipx install hypa
# or
uv tool install hypa
# or
pip install hypa
```

The wheel carries the native build for your platform: Linux (glibc 2.34 or newer) or macOS 12 or newer, on x64 or arm64. Python 3.9 or newer is needed to launch it. Windows is not supported by the mux. Build the compression CLI from source there.

Check the install:

```bash
hypa --version
hypa doctor
```

Other ways to install the same release: Homebrew (`brew install hypabolic/tap/hypa`), npm (`npm install --global @hypabolic/hypa`), or the install script. See the [repository README](https://github.com/Hypabolic/Hypa#install).

## Update

```bash
pipx upgrade hypa
# or
uv tool upgrade hypa
# or
python3 -m pip install --upgrade hypa
```

`hypa update --check` shows whether a newer release exists. After an upgrade, restart a running mux server (`hypa mux stop`, then `hypa attach`) so that it runs the new version.

## Quick start

```bash
# Start the local mux server, or reconnect to it, and attach
hypa

# Run a command and keep what matters from its output
hypa -c "kubectl get pods -A"

# Let Hypa find your agents and show every file it writes before it writes
hypa integration install
```

Bare `hypa` attaches to the mux. Detach with `ctrl+b` then `q`. The server and your panes keep running, and `hypa` attaches again.

## Documentation

[github.com/Hypabolic/Hypa](https://github.com/Hypabolic/Hypa)

## License

[Functional Source License 1.1, ALv2 Future License](https://github.com/Hypabolic/Hypa/blob/main/license.md)
