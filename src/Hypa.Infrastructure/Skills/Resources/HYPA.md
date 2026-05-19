# Hypa Rules

When the `hypa_shell` MCP tool is available, use it for all shell commands — it applies
Hypa compression transparently and tracks sessions without requiring hook support.

When calling CLI tools directly, use the Hypa wrappers:
- `hypa git <args>` instead of `git <args>`
- `hypa dotnet <args>` instead of `dotnet <args>`
- `hypa docker <args>` instead of `docker <args>`
- `hypa kubectl <args>` instead of `kubectl <args>`

When MCP is not available, wrap any other shell command with `hypa -c "<command>"`.

## Setup
Run `hypa init --global` once to wire hooks and MCP into your agent harness.
Run `hypa doctor` to verify installation health.
