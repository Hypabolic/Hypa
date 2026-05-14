# Hypa Rules

Use `hypa` instead of raw `git`, `dotnet`, `docker`, and `kubectl` commands.
Hypa compresses output to reduce context window usage.

## Examples
- `hypa git status` instead of `git status`
- `hypa git diff HEAD~1` instead of `git diff HEAD~1`
- `hypa dotnet build` instead of `dotnet build`
- `hypa dotnet test` instead of `dotnet test`
- `hypa docker ps` instead of `docker ps`
- `hypa kubectl get pods` instead of `kubectl get pods`
- `hypa -c "any command"` for generic wrapping

## Setup
Run `hypa init --global` once to wire hooks into your agent harness.
Run `hypa doctor` to verify installation health.
