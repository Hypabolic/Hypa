---
name: hypa
description: Context-optimized command runtime. Rewrites verbose commands into token-efficient equivalents. Use instead of running git/dotnet/docker/kubectl directly.
---

<!-- section:1 -->
## Setup + Core Usage

Hypa is a context-aware command runtime that rewrites verbose tool commands into token-efficient equivalents.

**Setup check:** `which hypa || hypa init --global`

**Core usage:**
- `hypa git status` — compressed git output
- `hypa dotnet build` — compressed build output
- `hypa -c "any-cmd"` — generic wrapper for any command

Exit codes:
- Runtime commands (`hypa git`, `hypa dotnet`, `hypa docker`, `hypa kubectl`, `hypa -c`) return the wrapped command's exit code.
- Rewrite decision codes (0/1/2/3) are exposed by `hypa rewrite`.

<!-- section:2 -->
## Command Reference

**Git:** `hypa git status`, `hypa git diff`, `hypa git log --oneline`
**Dotnet:** `hypa dotnet build`, `hypa dotnet test`, `hypa dotnet restore`
**Docker:** `hypa docker ps`, `hypa docker logs <container>`, `hypa docker build .`
**Kubectl:** `hypa kubectl get pods`, `hypa kubectl describe pod <name>`, `hypa kubectl logs <pod>`
**Generic:** `hypa -c "any command"` — wraps and compresses output of any command

**Rewrite decisions:**
- Rewritten: hypa substitutes the command with a compressed equivalent
- Passthrough: command runs unchanged (unsafe or unsupported)
- Deny: command blocked by policy
- Ask: user confirmation required

<!-- section:3 -->
## Session + Trust + Filters

**Sessions:** `hypa session list`, `hypa session show <id>`
**Trust:** `hypa trust status`, `hypa trust add <pattern>`
**Filters:** `hypa filters list`, `hypa filters add <name>`

**Custom filter DSL example:**
```json
{
  "name": "my-filter",
  "stages": [{"kind": "grep", "pattern": "error|warning"}]
}
```

<!-- section:4 -->
## Advanced / Analytics

**Savings:** `hypa filters savings` — estimates token savings for current session
**Diagnostics:** `hypa doctor` — health check for all subsystems

**`hypa rewrite` exit code contract:**
| Code | Meaning |
|------|---------|
| 0    | Rewrite (use the rewritten command) |
| 1    | Passthrough (run original command) |
| 2    | Deny (do not run) |
| 3    | Ask (confirm with user) |
