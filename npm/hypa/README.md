# Hypa

**A local context runtime for coding agents.**

[![npm](https://img.shields.io/npm/v/@hypabolic/hypa?color=cb3837&logo=npm)](https://www.npmjs.com/package/@hypabolic/hypa)
[![CI](https://github.com/Hypabolic/Hypa/actions/workflows/ci.yml/badge.svg)](https://github.com/Hypabolic/Hypa/actions/workflows/ci.yml)
[![GitHub](https://img.shields.io/github/stars/Hypabolic/Hypa?style=flat&logo=github)](https://github.com/Hypabolic/Hypa)
[![License](https://img.shields.io/badge/license-FSL--1.1--ALv2-blue)](https://github.com/Hypabolic/Hypa/blob/main/license.md)

Hypa reduces the amount of noisy tool output that reaches an AI agent's context window. It runs locally, compresses shell output with deterministic reducers, exposes context-aware file and code tools over MCP, and records enough evidence to preserve the details that matter.

```text
command or tool call
        ↓
      Hypa
        ↓
errors · warnings · changed files · failing tests · exit codes
```

Hypa is not an LLM summarizer. The default reduction path is local, deterministic, and testable. Your source code and command output do not need to be sent to a separate cloud service.

## Why Hypa?

Coding agents routinely consume thousands of tokens from build logs, test runners, package managers, Git output, container tools, and large source files. Most of that output is repetitive, while a small part is essential.

Hypa keeps the useful signal:

- errors, warnings, and failed tests;
- file paths, line numbers, and changed files;
- process exit codes and timing;
- compact command-specific summaries;
- references to retained full-output artifacts when recovery is needed.

It also provides structured ways for agents to inspect files, search code, query symbols, manage sessions, and call upstream MCP servers without loading every available schema or raw response into context.

## Features

| Capability | What Hypa provides |
|---|---|
| Shell compression | Run any command through `hypa -c`, with first-class reducers for common developer tools. |
| Agent integration | Install hooks, rules, skills, and the Hypa MCP server into supported coding-agent harnesses. |
| Context-aware reads | Read files in `full`, `outline`, `signatures`, `pruned`, or `smart` mode. |
| Code intelligence | Index source structure, query symbols, references, callers, callees, and dependency edges. |
| Markdown queries | Retrieve a table of contents, frontmatter, or one named section instead of the whole document. |
| MCP proxy | Import, discover, authenticate, search, and invoke tools from upstream MCP servers. |
| Local sessions | Record command metrics, evidence, checkpoints, and recovery artifacts in local SQLite storage. |
| Extensible filters | Use built-in reducers and trusted project-local declarative filters. |

## Installation

Install the native CLI through npm:

```bash
npm install --global @hypabolic/hypa
```

Then verify the installation:

```bash
hypa version
hypa doctor
```

### Requirements

- Node.js 18 or newer for the npm launcher.
- Linux, macOS, or Windows.
- x64 or arm64.

The npm package selects the matching prebuilt native binary automatically:

| Operating system | Architectures |
|---|---|
| Linux | x64, arm64 |
| macOS | x64, arm64 |
| Windows | x64, arm64 |

No .NET runtime is required when installing the published npm package.

## Quick start

### 1. Compress command output

Prefix a supported tool:

```bash
hypa git status
hypa dotnet build
hypa kubectl get pods --all-namespaces
hypa docker ps
```

Or wrap any shell command:

```bash
hypa -c "npm test"
hypa -c "cargo test"
hypa -c "terraform plan"
hypa -c "git log --oneline -100"
```

When output is reduced, Hypa appends compact savings metadata:

```text
[hypa: 1200→340 tok, -72%, reducer=dotnet-build]
```

Need the original streaming behaviour? Bypass compression:

```bash
hypa raw npm test
# equivalent:
hypa -t npm test
```

### 2. Connect Hypa to your coding agent

Preview the changes first:

```bash
hypa init --global --dry-run
```

Install integrations for detected harnesses:

```bash
hypa init --global
```

Or target one harness:

```bash
hypa init --global --agent claude
hypa init --global --agent codex
hypa init --global --agent pi
```

Use `--project` for repository-local configuration or `--all` for both global and project configuration.

```bash
hypa init --project
hypa init --all
hypa skill list
```

Hypa currently provides automated setup for:

| Harness | Integration |
|---|---|
| Claude Code | Pre-tool hook, MCP server, skill, and instructions. |
| Codex CLI | Pre-tool hook, MCP server, rules, and instructions. |
| Pi | Installs the `@hypabolic/pi-hypa` extension package. |
| OpenCode | Community plugin [`opencode-hypa`](https://github.com/kipyin/opencode-hypa) (not yet via `hypa init`). |

The hook path asks the central rewrite engine how shell commands should be handled. The MCP path gives the agent structured access to Hypa's shell, read, search, code, session, compression, and proxy capabilities.

### 3. Index and query a codebase

```bash
hypa code index
hypa code symbols --query UserService
hypa code graph --callers UserService
hypa code graph --callees symbol-id
hypa code diagnostics
```

Add `--json` when another program or agent needs structured output.

### 4. Read only the Markdown you need

```bash
hypa md README.md --toc
hypa md docs/guide.md --section "Installation"
hypa md docs/guide.md --frontmatter
```

Hypa refreshes the file's index before answering, so a section query does not require loading the complete document into the agent's context.

## MCP tools

Running `hypa serve` starts Hypa's stdio MCP server. `hypa init` configures this automatically for supported harnesses.

| Tool | Purpose |
|---|---|
| `hypa_shell` | Run shell commands with rewrite rules, compression, and evidence recording. |
| `hypa_read` | Read files with full, outline, signatures, pruned, or smart selection. |
| `hypa_search` | Search text, regular expressions, symbols, and indexed context. |
| `hypa_code` | Index code and query symbols, references, graphs, and diagnostics. |
| `hypa_session` | Inspect, initialize, attach, and checkpoint local sessions. |
| `hypa_compress` | Compress explicit shell output, logs, code, or generic text. |
| `hypa_mcp` | Discover and invoke tools from configured upstream MCP servers. |

The MCP server uses JSON-RPC over stdio:

```json
{
  "mcpServers": {
    "hypa": {
      "command": "hypa",
      "args": ["serve"]
    }
  }
}
```

In most cases, prefer `hypa init` so Hypa can write the correct harness-specific configuration.

## Upstream MCP proxy

Hypa can present a compact discovery surface in front of multiple MCP servers. Agents can search for a relevant tool and request its schema only when needed, rather than placing every tool definition into the initial context.

Import existing Claude Code and Codex MCP configuration:

```bash
hypa mcp import --agent all --scope global --dry-run
hypa mcp import --agent all --scope global
```

Inspect and search the imported tools:

```bash
hypa mcp list
hypa mcp tools
hypa mcp search --query "create issue"
hypa mcp schema --server github
```

Invoke a tool:

```bash
hypa mcp invoke \
  --server github \
  --tool get_issue \
  --arguments '{"owner":"Hypabolic","repo":"Hypa","issue_number":1}'
```

Hypa supports stdio and remote MCP transports, several authentication modes, parallel batch invocation, and on-demand tool discovery. Run `hypa mcp add --help` for the complete server and authentication options.

## Output reduction

Hypa combines several local reduction stages:

1. A command-specific reducer handles structured output from tools such as Git, .NET, Docker, and Kubernetes.
2. Built-in declarative filters remove known low-value noise from developer tooling.
3. Generic cleanup and safe truncation handle commands without a dedicated reducer.
4. Token counts are measured before and after reduction.
5. Failures or truncated output can retain a full local artifact for recovery.

Built-in filter coverage includes:

- build and test tools such as .NET, Cargo, Gradle, Maven, Go, pytest, Jest, Vitest, CMake, and Ninja;
- npm, pnpm, Yarn, pip, Poetry, and uv;
- ESLint, Biome, oxlint, ShellCheck, mypy, and Pyright;
- Docker, Kubernetes, Helm, Terraform, OpenTofu, Ansible, AWS, and Google Cloud;
- Git, GitHub CLI, Turbo, Nx, Make, and common Unix utilities.

Inspect the current installed filter set:

```bash
hypa filters list
hypa filters savings
hypa filters savings --markdown
```

Test one filter against captured output:

```bash
hypa filters test dotnet-msbuild-noise ./build-output.txt
```

## Sessions, evidence, and artifacts

Hypa stores runtime state under `~/.hypa/` by default:

```text
~/.hypa/
├── hypa.db
├── artifacts/
└── config.json
```

The SQLite database contains local session state, command metrics, parser/filter metadata, trust records, and artifact references.

```bash
hypa session status
hypa session init
hypa session checkpoint
hypa artifacts list
hypa parse-health
```

Full output retained for recovery stays on your machine in the artifacts directory.

## Trusted project filters

Repositories can define project-local filters under `.hypa/filters/`. Hypa does not silently trust these files.

```bash
hypa trust status
hypa trust filters
```

This lets a team version project-specific noise reduction while keeping execution trust explicit.

## Command reference

| Command | Description |
|---|---|
| `hypa -c "<command>"` | Buffer and compress a command's output. |
| `hypa raw <command>` | Run without compression. |
| `hypa rewrite "<command>"` | Show how Hypa would rewrite a shell command. |
| `hypa init` | Install integrations into supported agent harnesses. |
| `hypa doctor` | Diagnose the local installation and runtime. |
| `hypa code` | Index and query source-code structure. |
| `hypa md` | Query Markdown structure and selected sections. |
| `hypa mcp` | Configure and call upstream MCP servers. |
| `hypa serve` | Start the Hypa MCP server. |
| `hypa filters` | List, test, and estimate built-in filters. |
| `hypa session` | Manage local context sessions. |
| `hypa artifacts` | Inspect retained recovery artifacts. |
| `hypa trust` | Manage trust for project-local filters. |
| `hypa skill` | Inspect Hypa's agent instructions and harness support. |
| `hypa update` | Check for and install a newer Hypa release. |
| `hypa uninstall` | Remove integrations, local data, and the binary. |

Run `hypa --help` or `hypa <command> --help` for all options.

## Privacy and operating model

- Local-first: compression, indexing, metrics, sessions, and artifacts run on your machine.
- Deterministic by default: command reduction does not require an LLM call.
- No Hypa account is required for the local runtime.
- Upstream MCP calls go only to servers you explicitly configure.
- Authentication values can be referenced through environment variables or files rather than embedded directly in configuration.

## Troubleshooting

Run:

```bash
hypa doctor
hypa doctor code-intelligence
hypa skill list
```

If npm skipped the platform package, reinstall with optional dependencies enabled:

```bash
npm install --global @hypabolic/hypa --include=optional
```

If the platform package still cannot be found, install the package named in the error message directly.

## Documentation

- [Documentation](https://hypabolic.dev/products/hypa/docs)
- [GitHub repository](https://github.com/Hypabolic/Hypa)
- [Architecture](https://github.com/Hypabolic/Hypa/tree/main/docs/architecture)
- [Issue tracker](https://github.com/Hypabolic/Hypa/issues)
- [Pi integration](https://github.com/Hypabolic/Hypa/blob/main/docs/guides/pi.md)
- [OpenCode integration](https://github.com/Hypabolic/Hypa/blob/main/docs/guides/opencode.md)

## License

Hypa is licensed under the [Functional Source License 1.1 with an Apache License 2.0 future license](https://github.com/Hypabolic/Hypa/blob/main/license.md).
