# Hypa

Hypa is a local context runtime for agentic development. It runs shell commands, compresses noisy output before it reaches an agent context window, and records local evidence about what happened.

The project is designed for developers using coding agents who want shorter, higher-signal command output without losing the details that matter: errors, warnings, file paths, failing tests, exit codes, and recovery artifacts.

Hypa is local-first. It does not need a cloud service (or any other local service) to run.

## What It Does

- Runs commands through a buffered compression path with `hypa -c "command"`.
- Provides first-class reducers for common tools such as `git`, `dotnet`, `kubectl`, and `docker`.
- Applies built-in declarative filters for many developer tools, linters, build systems, package managers, cloud CLIs, and infrastructure tools.
- Estimates token savings with `Microsoft.ML.Tokenizers` using the `o200k_base` tokenizer.
- Records command metrics, parser/filter metadata, and artifact references in local SQLite storage.
- Supports passthrough mode for commands that should not be buffered or rewritten.

Hypa is not an LLM summarizer by default. Its command reduction path is deterministic, local, and testable.

## How It Works

```text
shell command
  -> Hypa command runner
  -> command-specific reducer, when available
  -> built-in or trusted DSL filter, when applicable
  -> token accounting and savings metadata
  -> compact output returned to the caller
```

The command runner captures stdout and stderr, computes a baseline token count, reduces the output, applies matching DSL filters, then computes the final token count after filtering. If the result saves tokens, Hypa appends a footer:

```text
[hypa: 1200→340 tok, -72%, reducer=dotnet-build]
```

For failures or truncation, Hypa can tee full output to a local artifact so the compact output can stay small while preserving recovery access.

## Installation

### Install A Release

Linux and macOS:

```bash
curl -fsSL https://hypabolic.github.io/Hypa/install.sh | sh
```

Windows PowerShell:

```powershell
irm https://hypabolic.github.io/Hypa/install.ps1 | iex
```

The installers download the matching GitHub Release asset for your platform, verify it against `SHA256SUMS`, and install `hypa` into a user-writable bin directory.

Supported prebuilt platforms:

- Linux x64: `hypa-linux-x64.tar.gz`
- Linux arm64: `hypa-linux-arm64.tar.gz`
- macOS x64: `hypa-osx-x64.tar.gz`
- macOS arm64: `hypa-osx-arm64.tar.gz`
- Windows x64: `hypa-win-x64.zip`
- Windows arm64: `hypa-win-arm64.zip`

Installer overrides:

```bash
HYPA_VERSION=0.1.0 HYPA_INSTALL_DIR="$HOME/bin" sh install.sh
HYPA_VERSION=v0.1.0 HYPA_REPO=owner/Hypa sh install.sh
```

```powershell
$env:HYPA_VERSION = "0.1.0"
$env:HYPA_INSTALL_DIR = "$HOME\bin"
irm https://hypabolic.github.io/Hypa/install.sh | iex
```

### Build From Source

Prerequisites:

- .NET 10 SDK
- Linux, macOS, or Windows with a shell environment

Run from source:

```bash
dotnet build src/Hypa.Cli/Hypa.Cli.csproj
dotnet run --project src/Hypa.Cli -- --help
```

Run a command through Hypa:

```bash
dotnet run --project src/Hypa.Cli -- -c "dotnet build"
```

Publish a local binary:

Linux x64 example:

```bash
dotnet publish src/Hypa.Cli/Hypa.Cli.csproj -c Release -r linux-x64
```

The published executable is written under:

```text
src/Hypa.Cli/bin/Release/net10.0/linux-x64/publish/
```

You can place that directory on your `PATH`, or symlink the executable as `hypa`.

## Basic Usage

Run and compress a command:

```bash
hypa -c "dotnet test"
```

Run a command with no compression:

```bash
hypa raw dotnet test
```

or:

```bash
hypa -t dotnet test
```

Use first-class command wrappers:

```bash
hypa git status
hypa dotnet build
hypa kubectl get pods
hypa docker ps
```

Ask Hypa how it would rewrite a command:

```bash
hypa rewrite "git status"
```

List available built-in and configured filters:

```bash
hypa filters list
```

Test a filter against a saved output file:

```bash
hypa filters test dotnet-msbuild-noise ./build-output.txt
```

Check runtime health:

```bash
hypa doctor
```

## Savings Estimates

Hypa can estimate savings for the built-in filter suite using synthetic payloads. This is useful for checking broad coverage without running real infrastructure commands.

```bash
hypa filters savings
```

Limit the report:

```bash
hypa filters savings --min-saved 100
hypa filters savings --id kubectl-logs
```

The default output is a fixed-width table:

```text
FILTER                     APPLIES                    ORIG     COMP    SAVED  SAVE%
--------------------------------------------------------------------------------------
dotnet-msbuild-noise       dotnet                      635        5      630    99%
--------------------------------------------------------------------------------------
TOTAL                                                  635        5      630    99%
```

### Markdown Savings Output

Use `--markdown` or `--format markdown` to generate a Markdown table for issues, pull requests, docs, or benchmark notes.

```bash
hypa filters savings --markdown
hypa filters savings --id dotnet-msbuild-noise --format markdown
```

Example:

```markdown
| Filter | Applies | Original Tokens | Compressed Tokens | Saved Tokens | Saved |
|---|---:|---:|---:|---:|---:|
| dotnet-msbuild-noise | dotnet | 635 | 5 | 630 | 99% |
| **TOTAL** |  | **635** | **5** | **630** | **99%** |
```

Savings reports use synthetic command-output payloads and the configured tokenizer. Treat them as repeatable estimates, not a replacement for measuring real project commands.

## Filter Coverage

Hypa includes built-in filters for common development workflows, including:

- Build and test: `dotnet`, `cargo`, `gradle`, `mvn`, `make`, `gcc`, `go test`, `rspec`, `mocha`, `jest`, `vitest`, `pytest`, `xcodebuild`, `cmake`, `ninja`.
- JavaScript and Python package management: `npm`, `pnpm`, `yarn`, `pip`, `poetry`, `uv`.
- Linters and formatters: `eslint`, `biome`, `oxlint`, `shellcheck`, `yamllint`, `hadolint`, `markdownlint`, `mypy`, `pyright`.
- Infrastructure and cloud: `terraform`, `tofu`, `ansible-playbook`, `helm`, `kubectl`, `docker`, `aws`, `gcloud`.
- System tools: `ping`, `df`, `du`, `ps`, `stat`, `systemctl`, `jq`.
- Monorepo and task runners: `turbo`, `nx`, `just`, `task`, `mise`.
- Source control and related tools: `git status`, `git log`, `git diff`, `yadm`.

Run `hypa filters list` for the current built-in list.

## Local Data

Hypa stores local runtime data under:

```text
~/.hypa/
```

Important files include:

- `hypa.db`: SQLite database for sessions, command metrics, parse metrics, trust records, and artifacts.
- `artifacts/`: tee output artifacts for recovery when full command output is retained.
- `config.json`: optional user configuration.

Project-local trusted filters can also live under a repository `.hypa/` directory.

## Trust And Project Filters

Hypa supports trusted project-local filters, but it does not blindly execute every repository filter. Use:

```bash
hypa trust status
hypa trust filters
```

This lets teams keep useful local filters in a repo while making trust explicit.

## Development

Build:

```bash
dotnet build
```

Run tests:

```bash
dotnet test
```

Check formatting:

```bash
dotnet format --verify-no-changes --no-restore
```

Run the CLI from source:

```bash
dotnet run --project src/Hypa.Cli -- filters savings --markdown
```

## Project Status

Hypa is early-stage open source software. The current implementation focuses on local command execution, deterministic compression, built-in filters, token-savings analytics, and CLI workflows. The architecture leaves room for richer parser tiers, MCP tools, shell hooks, code intelligence, and optional Atomic integration.

See `docs/architecture/` and `docs/ADRs/` for design details.
