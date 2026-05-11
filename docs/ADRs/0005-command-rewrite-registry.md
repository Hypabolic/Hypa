# ADR 0005: Command Rewrite Registry

## Status

Accepted

## Date

2026-05-07

## Context

Hypa needs to reduce token waste in real agent sessions without requiring agents or humans to remember to call Hypa explicitly. Command interception is a critical adoption mechanism.

Hypa should combine two approaches:

- Generic command wrapping for broad coverage;
- First-class command rewrites for high-quality known reducers.

## Decision

Hypa will implement a first-class command rewrite registry.

The registry will classify proposed shell commands and return one of three outcomes:

1. Rewrite to a first-class Hypa command module when a specialised reducer exists.
2. Rewrite to a generic Hypa compression wrapper when the command is safe and compressible but has no specialised module.
3. Pass through unchanged when the command is unsafe, interactive, streaming, unsupported, explicitly excluded, or already routed through Hypa.

The registry is the single source of truth for command rewrite decisions. Agent hooks, shell hooks, and plugins must delegate to it rather than duplicating rewrite rules.

## Consequences

### Positive

- Hypa can achieve transparent adoption in agent harnesses.
- Known commands can get high-quality command-specific reducers.
- Unknown but safe commands can still benefit from generic compression.
- Hook implementations stay small and host-specific only.
- Rewrite logic can be tested independently from agent integrations.
- Security and permission decisions can be handled consistently.

### Negative / Trade-offs

- The rewrite layer requires shell-aware parsing rather than naive string prefix matching.
- Command classification rules require ongoing maintenance.
- Compound shell commands, pipes, redirects, heredocs, and environment prefixes create edge cases.
- Incorrect rewrites can change command semantics, so passthrough must be conservative.

## Implementation Notes

Recommended module boundary:

```text
Hypa.CommandRewrite
  ShellLexer
  RewriteRegistry
  RewriteRule
  RewriteDecision
  PermissionPolicy
  ExclusionPolicy
  AgentRewriteProtocol
```

Suggested API:

```csharp
public interface ICommandRewriteService
{
    RewriteDecision Rewrite(
        string command,
        RewriteContext context);
}

public sealed record RewriteDecision(
    RewriteDecisionKind Kind,
    string? RewrittenCommand,
    string? Reason,
    PermissionDecision Permission,
    RewriteTargetKind TargetKind);

public enum RewriteDecisionKind
{
    Rewrite,
    Passthrough,
    Deny,
    Ask
}

public enum RewriteTargetKind
{
    None,
    FirstClassCommand,
    GenericCompressionWrapper
}
```

Rewrite examples:

```text
git status
  -> hypa git status
  target = FirstClassCommand

dotnet test
  -> hypa dotnet test
  target = FirstClassCommand

some-custom-tool --check
  -> hypa -c "some-custom-tool --check"
  target = GenericCompressionWrapper

vim src/Foo.cs
  -> passthrough

kubectl logs -f pod/foo
  -> passthrough

HYPA_DISABLED=1 git status
  -> passthrough
```

The registry should handle:

- empty commands;
- already-Hypa commands;
- `HYPA_DISABLED=1` override;
- explicit user/project exclusions;
- environment prefixes such as `VAR=value`, `env`, and `sudo`;
- absolute binary paths such as `/usr/bin/git`;
- shell operators `&&`, `||`, `;`, and background `&`;
- pipes, with conservative handling of the right-hand side;
- redirects, preserving trailing redirection when safe;
- heredocs and complex shell arithmetic by passing through;
- known unsafe/interacting/streaming commands.

For compound commands, rewrite at segment level:

```text
cargo fmt --all && cargo test
  -> hypa cargo fmt --all && hypa cargo test

git log --oneline | head -20
  -> hypa git log --oneline | head -20
```

The pipe consumer should usually remain raw because it depends on the producer's output format. Commands such as `find` or `fd` before pipes should be treated conservatively because downstream tools often expect exact native output.

Suggested hook exit-code contract for subprocess-based adapters:

```text
0 = rewritten and allowed
1 = no rewrite, passthrough
2 = denied by policy
3 = rewrite available but requires ask/confirmation
```

This implements useful permission-aware pattern and avoids accidentally auto-allowing commands merely because they have a Hypa rewrite.

## Alternatives Considered

### Generic wrapper only

Rejected as the only strategy. It is good for coverage and fallback, but weaker than first-class command modules for commands where Hypa can parse structured output, inject JSON reporters, preserve test failures, or produce high-quality compact summaries.

### First-class command rewrites only

Rejected as the only strategy. It leaves unknown tools with no optimisation path and slows early usefulness.

### Hook-specific rewrite logic

Rejected. Each agent hook should only parse host JSON and format host responses. Duplicated rewrite rules would drift and become untestable.

## Related Decisions

- ADR 0001: Local Context Runtime Operating Model
- ADR 0002: Deterministic Tool Output Compression
- ADR 0006: Filter DSL and Parser Tiers
