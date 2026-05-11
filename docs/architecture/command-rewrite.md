# Command Rewrite

Hypa's command rewrite layer is responsible for turning agent-proposed shell commands into Hypa-optimised commands when doing so is safe and useful.

This design uses a command rewrite model and combines it with a generic compression wrapper pattern.

## Design Summary

Hypa supports two rewrite targets:

```text
First-class command module
  git status        -> hypa git status
  dotnet test       -> hypa dotnet test
  kubectl get pods  -> hypa kubectl get pods

Generic compression wrapper
  custom-tool check -> hypa -c "custom-tool check"
```

The rewrite registry chooses the best target:

```text
known command with specialised reducer
  -> first-class command module

unknown safe command
  -> generic compression wrapper

interactive / streaming / unsafe / excluded command
  -> passthrough
```

## Runtime Flow

```text
Agent proposes shell command
        |
        v
Agent hook / shell hook / plugin
        |
        v
hypa rewrite "<command>"
        |
        v
RewriteRegistry
        |
        |-- first-class rewrite
        |-- generic wrapper rewrite
        |-- ask / deny / passthrough
        v
Host-specific hook response
        |
        v
Agent executes rewritten command
        |
        v
Hypa command runner + compression pipeline
```

Hooks must be thin delegates. They should parse host-specific JSON, call Hypa's rewrite service, and format host-specific responses. They must not contain their own rewrite rule logic.

## Rewrite Decision Types

```csharp
public enum RewriteDecisionKind
{
    Rewrite,
    Passthrough,
    Ask,
    Deny
}

public enum RewriteTargetKind
{
    None,
    FirstClassCommand,
    GenericCompressionWrapper
}

public sealed record RewriteDecision(
    RewriteDecisionKind Kind,
    RewriteTargetKind TargetKind,
    string OriginalCommand,
    string? RewrittenCommand,
    string? Reason,
    PermissionDecision Permission);
```

## Rewrite Rules

Rules should be data-driven where possible.

```csharp
public sealed record RewriteRule(
    string Id,
    string Category,
    string Pattern,
    string TargetCommand,
    RewriteTargetKind TargetKind,
    double EstimatedSavingsPercent,
    IReadOnlyList<string> Prefixes,
    IReadOnlyList<string> Exclusions);
```

Example rules:

```text
id = git-status
pattern = ^git\s+status(\s|$)
target = hypa git
kind = FirstClassCommand
estimated = 70

id = dotnet-test
pattern = ^dotnet\s+test(\s|$)
target = hypa dotnet test
kind = FirstClassCommand
estimated = 85
```

## Generic Wrapper Fallback

For commands without a first-class reducer, Hypa may rewrite to:

```bash
hypa -c "<original command>"
```

Only do this when the command is safe to buffer and compress.

Generic wrapper is suitable for:

- short-running commands;
- non-interactive commands;
- commands without live TTY requirements;
- commands with bounded output;
- commands where output format changes do not affect downstream shell semantics.

Generic wrapper is not suitable for:

- interactive shells/editors;
- long-running dev servers;
- streaming logs;
- auth/device-code flows;
- commands with complex heredocs;
- commands where precise raw output is consumed by another process.

## Shell Parsing Requirements

Naive string splitting is not good enough. The rewrite layer needs shell-aware tokenisation.

Minimum token kinds:

```text
Arg
Operator        && || ;
Pipe            |
Redirect        > >> < 2>&1 <<
Shellism        &
Whitespace
QuotedArg
```

The lexer should preserve byte offsets so rewritten commands can preserve original suffixes, redirects, and spacing where useful.

## Compound Commands

Rewrite command segments independently.

```text
cargo fmt --all && cargo test
  -> hypa cargo fmt --all && hypa cargo test

npm run lint || npm test
  -> hypa npm run lint || hypa npm test
```

Pipe handling should be conservative:

```text
git log --oneline | head -20
  -> hypa git log --oneline | head -20
```

Usually only the left side of a pipe should be rewritten. The right side consumes the producer's output and may expect native formatting.

Commands such as `find` or `fd` before a pipe should usually remain raw because downstream commands often depend on exact path-per-line output.

## Environment Prefixes

Handle environment and shell prefixes:

```text
VAR=value git status
sudo git status
env VAR=value git status
command git status
noglob grep foo *.cs
```

The classifier should strip prefixes for matching but re-prepend them when constructing the rewritten command.

Special override:

```bash
HYPA_DISABLED=1 git status
```

This must force passthrough.

## Redirects

Trailing redirects can often be preserved:

```text
git status 2>&1
  -> hypa git status 2>&1
```

Commands where redirects change semantic intent should pass through. Example:

```text
cat file > out.txt
```

This is a write operation, not a read operation, and should not be rewritten to `hypa read`.

## Passthrough and Exclusions

Passthrough when:

```text
command is empty
command already starts with hypa
command has HYPA_DISABLED=1
command matches exclude list
command is interactive
command is streaming
command contains heredoc
command uses unsupported shell arithmetic or expansion
command is likely to change semantics if wrapped
```

Suggested config:

```toml
[hooks]
exclude_commands = ["curl", "playwright", "kubectl logs -f"]

[rewrite]
generic_wrapper = true
first_class = true
```

## Permission-Aware Hook Contract

For hooks that call `hypa rewrite` as a subprocess, use exit codes:

```text
0 = rewritten and allowed
1 = no rewrite, passthrough
2 = denied by policy
3 = rewrite available but ask/confirmation required
```

This lets Hypa avoid accidentally granting extra permissions just because a command can be rewritten.

## Host Adapter Behaviour

### Claude / Cursor-style updated input

Input:

```json
{
  "tool_name": "Bash",
  "tool_input": { "command": "git status" }
}
```

Output when rewritten:

```json
{
  "hookSpecificOutput": {
    "updatedInput": { "command": "hypa git status" }
  }
}
```

### Hosts without mutable input

Return a deny/suggestion or prompt-level instruction:

```text
Use `hypa git status` instead for compact output.
```

### Rules-file-only hosts

Install concise instructions telling the agent to prefer Hypa tools and shell wrappers.

## Canonical Test Cases

The following table defines the exact expected behaviour for the Phase 2 registry implementation.
Unit tests in `tests/Hypa.UnitTests/Infrastructure/` enumerate these cases directly.

| Input | Output | Outcome |
|---|---|---|
| `git status` | `hypa git status` | Rewritten |
| `git diff HEAD~1` | `hypa git diff HEAD~1` | Rewritten |
| `git log --oneline` | `hypa git log --oneline` | Rewritten |
| `dotnet build` | `hypa dotnet build` | Rewritten |
| `dotnet test` | `hypa dotnet test` | Rewritten |
| `docker ps` | `hypa docker ps` | Rewritten |
| `docker logs my-container` | `hypa docker logs my-container` | Rewritten |
| `kubectl get pods` | `hypa kubectl get pods` | Rewritten |
| `kubectl describe pod my-pod` | `hypa kubectl describe pod my-pod` | Rewritten |
| `custom-check --json` | `hypa -c "custom-check --json"` | GenericWrapper |
| `pnpm install` | `hypa -c "pnpm install"` | GenericWrapper |
| `tsc --watch` | `hypa -c "tsc --watch"` | GenericWrapper |
| `vim file.cs` | *(passthrough)* | Passthrough |
| `git push origin main` | *(passthrough)* | Passthrough |
| `cat file \| grep foo` | *(passthrough)* | Passthrough |
| `kubectl logs -f pod/foo` | *(passthrough)* | Passthrough |
| `git status && dotnet build` | `hypa git status && hypa dotnet build` | Rewritten |
| `git status \|\| dotnet build` | `hypa git status \|\| hypa dotnet build` | Rewritten |
| `git status ; dotnet build` | `hypa git status ; hypa dotnet build` | Rewritten |

## Testing

Rewrite tests should cover:

- basic known commands;
- unknown command generic wrapper;
- already-Hypa passthrough;
- env prefixes;
- redirects;
- compound `&&`, `||`, `;`;
- pipes;
- heredocs;
- excluded commands;
- `HYPA_DISABLED=1`;
- permission allow/ask/deny/default;
- malformed shell input.

Example cases:

```text
git status                         -> hypa git status
dotnet test --no-build             -> hypa dotnet test --no-build
custom-check --json                 -> hypa -c "custom-check --json"
HYPA_DISABLED=1 git status          -> passthrough
vim src/Foo.cs                      -> passthrough
kubectl logs -f pod/foo             -> passthrough
git status && dotnet test           -> hypa git status && hypa dotnet test
git log --oneline | head -20        -> hypa git log --oneline | head -20
cat input.txt > output.txt          -> passthrough
```

## Relationship to Compression

Rewrite decides how a command enters Hypa.

Compression decides how output is reduced after execution.

```text
rewrite -> command runner -> parser/reducer/filter -> compressed output -> tracking/evidence
```

Do not put compression logic in the rewrite layer.
