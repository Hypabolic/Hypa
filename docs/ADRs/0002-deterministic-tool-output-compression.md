# ADR 0002: Deterministic Tool Output Compression

## Status

Accepted

## Date

2026-05-06

## Context

Agentic development sessions often waste context on noisy tool output: build logs, package manager output, test runner boilerplate, git output, container output, Kubernetes output, and long command traces.

A local runtime can reduce this waste before output reaches the agent. The compression mechanism must preserve actionable information such as errors, warnings, failing tests, file paths, line numbers, exit codes, and authentication/device-code instructions.

LLM-based summarisation is tempting but inappropriate as the default compression path. It adds cost, latency, nondeterminism, and failure modes. The runtime needs a reliable baseline that can run locally and safely in tight tool loops.

## Decision

Hypa will use deterministic, command-aware tool output compression as the default compression strategy.

Compression will be implemented as a pipeline:

1. classify command and output stream;
2. bypass known interactive, streaming, or authentication commands;
3. apply command-specific reducers where available;
4. apply generic cleanup and safe truncation where no reducer exists;
5. preserve safety-relevant lines from omitted output;
6. verify that compressed output is meaningfully smaller and not suspiciously lossy;
7. append compact compression metadata for observability.

LLM summarisation may be added later as an explicit, opt-in strategy for specific non-critical outputs, but it is not the default compression mechanism.

## Consequences

### Positive

- Compression is fast, local, repeatable, and testable.
- Agents receive denser output without waiting for another model call.
- Important command semantics can be preserved per tool family.
- Compression decisions can be covered by golden-file tests.
- The runtime can operate offline and without API keys.

### Negative / Trade-offs

- Command-specific reducers require ongoing maintenance.
- Unknown tools fall back to generic compression with lower quality.
- New ecosystems need explicit support to achieve high compression ratios.
- Deterministic reducers cannot infer hidden intent as flexibly as an LLM summariser.

## Implementation Notes

Recommended core abstractions:

```csharp
public sealed record CommandInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string OriginalCommand,
    string? WorkingDirectory);

public sealed record CommandOutput(
    string Stdout,
    string Stderr,
    int ExitCode);

public sealed record CompressionResult(
    string Output,
    int OriginalTokens,
    int CompressedTokens,
    double SavingsPercent,
    CompressionDecision Decision,
    IReadOnlyList<string> Warnings);

public interface ICommandOutputCompressor
{
    bool CanHandle(CommandInvocation command);

    CompressionResult Compress(
        CommandInvocation command,
        CommandOutput output,
        CompressionOptions options);
}
```

Initial high-value compressors:

- Git
- .NET build/test
- npm/pnpm/yarn
- TypeScript compiler
- Docker
- Kubernetes / kubectl
- Helm
- PostgreSQL / psql
- Terraform/OpenTofu
- GitHub CLI

The shell integration should support at least two modes:

```bash
hypa -t <argv>      # track only; preserve normal output
hypa -c <command>   # compress buffered output
```

Default shell hooks should prefer track mode for interactive use. Agents and supported hooks can explicitly route compressible commands through compression mode.

Bypass/passthrough handling must include:

- interactive shells and editors;
- long-running dev servers and watchers;
- terminal multiplexers;
- streaming logs;
- authentication and device-code flows;
- commands with output redirection where wrapping could change semantics;
- commands that require a live TTY.

Compression must include guardrails:

- do not compress very small outputs;
- do not return compressed output if it is not smaller;
- reject suspiciously extreme compression unless produced by a known safe reducer;
- preserve auth URLs and codes unmodified;
- preserve failing file paths, line numbers, diagnostic IDs, and exit codes;
- optionally tee full output to a local redacted, TTL-limited archive for debugging.

## Alternatives Considered

### LLM summarisation by default

Rejected. It is non-deterministic, expensive, slower, and less suitable for inner-loop tool execution. It may be useful later as an explicit mode for large narrative logs.

### Raw output only with token accounting

Rejected. Token accounting is useful but does not solve the context-pressure problem.

### Generic truncation only

Rejected. Generic truncation can remove the exact lines an agent needs. Command-specific reducers provide materially better fidelity.

## Related Decisions

- ADR 0001: Local Context Runtime Operating Model
- ADR 0004: Optional Language Server Enrichment
