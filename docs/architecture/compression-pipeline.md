# Compression Pipeline

Hypa compresses tool and shell output before it reaches an agent context window. The default compression path is deterministic and command-aware.

This design uses a generic `-c "<command>"` compression wrapper with stronger command-specific filters, filter DSL, parser tiers, tee recovery, and token-savings analytics.

## Goals

The compression pipeline should:

- reduce token usage from noisy tool output;
- preserve actionable details;
- avoid interfering with interactive or streaming commands;
- remain deterministic and testable;
- work offline;
- record token savings and provenance;
- expose parser/filter degradation clearly;
- keep raw output available by reference when needed.

## Non-Goals

The default pipeline is not an LLM summariser.

LLM summarisation may become an explicit opt-in mode for selected outputs, but should not be part of the default inner-loop shell/tool path.

## Pipeline Overview

```text
CommandInvocation + stdout/stderr
        |
        v
1. Command classification / rewrite target selected
        |
        v
2. Shared command runner executes or streams process
        |
        v
3. Passthrough / bypass / auth-flow checks
        |
        v
4. Structured parser, if available
        |
        |-- Full parse
        |-- Degraded parse
        |-- Passthrough parse
        v
5. Command-specific reducer or formatter
        |
        v
6. Declarative filter DSL fallback, if matched
        |
        v
7. Generic cleanup + safe truncation
        |
        v
8. Compression verification
        |
        v
9. Tee recovery + artifact reference, if needed
        |
        v
10. Metadata + evidence + analytics recording
        |
        v
Compressed output returned to caller
```

## Relationship to Command Rewrite

Rewrite decides how a command enters Hypa.

Compression decides how output is reduced after execution.

Examples:

```text
git status
  -> rewrite: hypa git status
  -> compression: GitStatusReducer

custom-check --verbose
  -> rewrite: hypa -c "custom-check --verbose"
  -> compression: DSL filter or generic compression

vim src/Foo.cs
  -> rewrite: passthrough
  -> compression: none
```

## Command Runner

Reducers should not spawn processes directly. A shared command runner should own process execution, capture, streaming, tee recovery, tracking, and exit-code preservation.

Suggested shape:

```csharp
public enum ToolRunMode
{
    Filtered,
    Streamed,
    Passthrough
}

public sealed record ToolRunOptions(
    string? TeeLabel,
    bool FilterStdoutOnly,
    bool SkipFilterOnFailure,
    bool NoTrailingNewline,
    TimeSpan Timeout);

public interface ICommandRunner
{
    Task<CommandRunResult> RunAsync(
        CommandInvocation command,
        ToolRunMode mode,
        IOutputFilter? filter,
        ToolRunOptions options,
        CancellationToken cancellationToken);
}
```

The runner owns:

```text
process execution
stdout/stderr capture
streaming mode
exit code preservation
timeout/cancellation
tee recovery
tracking/evidence
```

## Command Classification

Command parsing should produce:

```csharp
public sealed record CommandInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string OriginalCommand,
    string? WorkingDirectory,
    bool IsCompound,
    IReadOnlyList<CommandSegment> Segments);
```

Compound commands should be parsed conservatively. Rewriting should avoid heredocs, complex shell quoting, and commands where wrapping would change semantics.

## Bypass Rules

Compression should be bypassed for:

- interactive commands;
- commands requiring a TTY;
- long-running servers and watchers;
- streaming logs unless a streaming filter exists;
- authentication and device-code flows;
- editor/pager commands;
- terminal multiplexers;
- commands with redirection or heredocs where wrapping is unsafe;
- outputs below a small token threshold.

Examples:

```text
ssh, vim, nvim, less, more, tmux, screen
next dev, vite dev, dotnet run, dotnet watch
kubectl logs -f, docker logs -f, tail -f, journalctl -f
gh auth login, az login, aws sso login, gcloud auth login
```

## Reducer Registry

Reducers are command-specific compressors.

```csharp
public interface ICommandOutputCompressor
{
    string Id { get; }

    bool CanHandle(CommandInvocation command);

    CompressionResult Compress(
        CommandInvocation command,
        CommandOutput output,
        CompressionOptions options);
}
```

The registry should route by executable and subcommand:

```text
git status       -> GitStatusCompressor
git diff         -> GitDiffCompressor
dotnet build     -> DotNetBuildCompressor
dotnet test      -> DotNetTestCompressor
kubectl get      -> KubectlGetCompressor
kubectl describe -> KubectlDescribeCompressor
pnpm install     -> PackageManagerInstallCompressor
tsc              -> TypeScriptCompilerCompressor
```

## Parser Tiers

For structured tools, compression should usually parse to canonical DTOs before formatting compact output.

Parser tiers:

```text
Full
  structured parse succeeded, e.g. JSON, TRX, JUnit, NDJSON

Degraded
  structured parse failed but fallback regex/state parsing produced useful partial output

Passthrough
  parser failed; Hypa returned raw or safely truncated output with explicit marker
```

Recommended abstractions:

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

public interface ITokenFormatter<T>
{
    string Format(T value, FormatMode mode);
}
```

Canonical DTOs:

```text
TestRunResult
LintResult
BuildResult
DependencyState
PackageAuditResult
ContainerStatus
KubernetesResourceSummary
DeploymentStatus
```

Parser tiers should be recorded in evidence and analytics.

## Filter DSL

Hypa should support a declarative filter DSL for simple line-oriented filters.

Lookup order:

```text
1. project-local .hypa/filters.toml, trust-gated
2. user-global ~/.hypa/filters.toml
3. built-in embedded filters
```

Stages:

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

Example:

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

DSL filters are not a replacement for compiled reducers. Use compiled reducers when the tool needs JSON parsing, state machines, reporter injection, package-manager detection, or domain-specific formatting.

## Filtering Strategy Taxonomy

Hypa should explicitly model these strategies:

```text
StatsExtraction
ErrorOnly
GroupingByPattern
Deduplication
StructureOnly
CodeFiltering
FailureFocus
TreeCompression
ProgressFiltering
JsonTextDualMode
StateMachineParsing
NdjsonStreaming
GenericSafeTruncation
```

Reducers should identify the strategy they used for diagnostics and improvement tracking.

## Initial Reducer Set

MVP reducers should prioritise the stack Hypa and Atomic actually use:

### Git

Preserve:

- branch;
- staged/unstaged/untracked summaries;
- changed file paths;
- commit hashes where relevant;
- conflict markers;
- ahead/behind status.

### .NET build/test

Preserve:

- project name;
- target framework;
- error/warning diagnostic IDs;
- file paths and line/column numbers;
- failing test names;
- assertion messages;
- total/pass/fail counts;
- exit code.

Prefer structured outputs where possible:

```text
dotnet test --logger trx
coverlet / test reports, later
MSBuild binary log, optional later
```

### TypeScript / frontend tools

Preserve:

- compiler errors;
- file paths and line/column numbers;
- failed build step;
- package manager errors;
- lockfile or peer dependency problems.

Prefer JSON reporters where stable.

### Docker / Kubernetes / Helm

Preserve:

- failing resource names;
- namespaces;
- container names;
- image names/tags;
- pod phases;
- events and reasons;
- exit codes;
- readiness/liveness probe failures.

### Database tools

Preserve:

- SQL errors;
- migration names;
- relation/table names;
- row counts;
- constraint names;
- connection/authentication errors.

## Generic Cleanup

When no reducer or DSL filter applies, Hypa should perform conservative cleanup:

- strip ANSI control sequences;
- collapse repeated blank lines;
- deduplicate repeated log lines where safe;
- remove progress bars/spinners;
- trim obvious boilerplate;
- preserve first and last lines.

## Safe Truncation

For long output, preserve:

```text
first N lines
last N lines
important middle lines
omission marker
```

Important lines include:

```text
error, failed, failure, exception, warning, warn, fatal, panic
line/column diagnostics
stack trace anchors
file paths
HTTP 4xx/5xx
Kubernetes Warning events
compiler diagnostic IDs
```

Example marker:

```text
[147 lines omitted, 12 safety-relevant lines preserved]
```

## Verification

A compression result should be accepted only if:

- output is non-empty when original was non-empty;
- compressed token count is lower than original token count;
- saved percentage clears the configured threshold;
- compression is not suspiciously extreme unless from a trusted reducer;
- required safety patterns were preserved.

Suggested thresholds:

```text
small output threshold: 50 tokens
minimum useful saving: 5%
suspicious extreme compression: below 5% of original for outputs above 100 tokens
```

## Metadata

Compressed output should optionally append a compact metadata line:

```text
[hypa: 2200→410 tok, -81%, parser=Full, reducer=dotnet-test]
```

The metadata line should be excluded from output-token accounting where possible.

## Tee Recovery and Raw Output Retention

For debugging and audit, Hypa may tee full output to a redacted local artifact.

Retention policies:

```text
success output: do not retain by default
failure output: retain redacted full output for short TTL
truncated success output: optionally retain full output for recovery
explicit debug mode: retain with configured TTL
Atomic-bound run: optionally upload selected artifact references
```

The compressed output should include a reference when raw output is retained:

```text
[hypa: full output -> artifact:cmdout_abc123, redacted, expires in 24h]
```

Tee recovery lets agents inspect full output without rerunning expensive or state-changing commands.

## Shell Modes

Hypa should expose:

```bash
hypa -t <argv>      # track mode: preserve output, record stats when useful
hypa -c <command>   # compress mode: buffer and compress output
hypa raw <command>  # bypass mode
```

Shell hooks should default to track mode for human shells. Agent hook adapters can rewrite selected commands to first-class command modules or compression mode.

## Testing Strategy

Compression must be covered with golden-file tests.

Recommended test structure:

```text
tests/compression/git/status.clean.input.txt
tests/compression/git/status.clean.expected.txt
tests/compression/dotnet/build.failure.input.txt
tests/compression/dotnet/build.failure.expected.txt
tests/compression/dsl/example-tool.input.txt
tests/compression/dsl/example-tool.expected.txt
```

Each reducer should test:

- clean/success output;
- warning output;
- failure output;
- large noisy output;
- structured parse success;
- degraded parse fallback;
- passthrough fallback;
- edge cases with paths, quoting, Unicode, and ANSI.

Regression tests should assert that safety-critical lines are preserved.

## Metrics

Record:

```text
command
rewrite target kind
reducer id
filter id
parser tier
format mode
original tokens
compressed tokens
tokens saved
compression ratio
exit code
duration
bypass reason
artifact ref, if any
```

These metrics feed local diagnostics, Atomic cost attribution, future optimisation, and `gain` / `discover` reports.
