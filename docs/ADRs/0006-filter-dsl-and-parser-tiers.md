# ADR 0006: Filter DSL and Parser Tiers

## Status

Accepted

## Date

2026-05-07

## Context

There are two useful implementation patterns for command-output reduction:

1. First-class compiled filters for complex tools.
2. A declarative TOML filter DSL for simpler line-oriented filters.

The compiled filters are appropriate when Hypa needs structured parsing, state machines, command flag injection, JSON/NDJSON handling, or ecosystem-specific behaviour. The filter DSL is appropriate when a command can be reduced with ANSI stripping, regex replacement, line filtering, truncation, or short-circuit output matching.

Parsers should degrade visibly rather than silently producing bad output. A structured parser can have a full parse path, a degraded fallback path, and a passthrough/truncation path.

Hypa needs these concepts because command reducers will grow over time. Requiring C# code for every simple filter would slow coverage. Letting every filter be regex-only would limit quality for tests, builds, linters, package managers, and cloud tools.

## Decision

Hypa will support both compiled reducers and a declarative filter DSL.

Hypa will also introduce parser tiers for command families that can produce structured output:

1. Full: structured parse succeeded and emitted canonical DTOs.
2. Degraded: partial parse or fallback regex parse succeeded with warnings.
3. Passthrough: structured parse failed; Hypa returned raw or safely truncated output with explicit marker.

Compiled reducers and DSL filters both feed the same compression/evidence pipeline and tracking model.

## Consequences

### Positive

- Simple filters can be added without code changes.
- Complex tools still get high-quality compiled reducers.
- Parser failures become observable instead of silently corrupting context.
- Agents can reason about confidence using parse tier and provenance.
- Project-local filters allow fast experimentation while trust-gating reduces risk.

### Negative / Trade-offs

- Hypa must implement and maintain a filter DSL engine.
- Project-local filters introduce trust and security concerns.
- Parser-tier output requires additional metadata and tests.
- DSL filters may be overused for cases that deserve compiled reducers.

## Implementation Notes

Recommended filter lookup order:

```text
1. project-local .hypa/filters.toml, trust-gated
2. user-global ~/.hypa/filters.toml
3. built-in embedded filters
```

Recommended DSL stages:

```text
strip_ansi
replace
match_output
keep_lines / strip_lines
truncate_lines_at
head_lines / tail_lines
max_lines
on_empty
```

Example filter shape:

```toml
[[filters]]
id = "example-tool-basic"
command = "example-tool"
match = "^example-tool(\\s|$)"
strip_ansi = true
keep_lines = ["(?i)error", "(?i)warning", "failed", "summary"]
truncate_lines_at = 240
max_lines = 80
on_empty = "ok example-tool: no relevant output"
```

Recommended parser abstractions:

```csharp
public interface IOutputParser<T>
{
    ParseResult<T> Parse(string output);
}

public sealed record ParseResult<T>(
    ParseTier Tier,
    T? Value,
    string? FallbackText,
    IReadOnlyList<string> Warnings);

public enum ParseTier
{
    Full,
    Degraded,
    Passthrough
}

public interface ITokenFormatter<T>
{
    string Format(T value, FormatMode mode);
}
```

Canonical DTOs should exist for common output domains:

```text
TestRunResult
LintResult
BuildResult
DependencyState
PackageAuditResult
DeploymentStatus
ContainerStatus
KubernetesResourceSummary
```

Parser-tier examples:

```text
dotnet test --logger trx
  Full: parse TRX or structured logger output
  Degraded: regex parse failing tests from text output
  Passthrough: safe truncation with [HYPA:PASSTHROUGH]

eslint --format json
  Full: parse JSON
  Degraded: parse stylish text output
  Passthrough: safe truncation

go test -json
  Full: parse NDJSON event stream
  Degraded: parse text output
  Passthrough: safe truncation
```

Tracking/evidence should record:

```text
reducer_id
filter_id?
parse_tier?
format_mode
original_tokens
compressed_tokens
saved_tokens
warnings
artifact_ref?
```

Project-local filters should require explicit trust before execution. They are not executable code, but regexes and output matching still influence what agents see.

Suggested trust command:

```bash
hypa trust filters
hypa trust status
```

## Alternatives Considered

### Compiled reducers only

Rejected. This would produce high quality but slow coverage and make user/project customisation too hard.

### DSL filters only

Rejected. Many high-value tools require JSON parsing, NDJSON parsing, state machines, reporter injection, package-manager detection, and domain-specific formatting.

### Silent fallback on parser failure

Rejected. Agents need to know when output is degraded or passthrough so they can decide whether to inspect the full artifact.

## Related Decisions

- ADR 0002: Deterministic Tool Output Compression
- ADR 0005: Command Rewrite Registry
