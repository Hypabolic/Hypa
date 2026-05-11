# Hypa Architecture

Hypa is a local context runtime for agentic development. It provides tool-call optimisation, deterministic command-output compression, code intelligence, session continuity, and optional synchronisation with Atomic.

The target is practical feature set with the useful operating models while keeping Hypa aligned with the Atomic product ontology and a .NET-first implementation.

- Hypa implements a local context runtime, MCP/tool surface, generic `-c "<command>"` compression wrapper, session continuity, and code-context concepts.
- Hypa implements a first-class command rewrite registry, thin hook delegates, command-specific reducer modules, declarative filter DSL, parser tiers, tee recovery, and SQLite token-savings analytics.

## Core Goals

Hypa should:

- run locally as a CLI, MCP server, and optional background service;
- work standalone without requiring Atomic authentication;
- integrate with agent harnesses through MCP, shell hooks, and host-specific tool-use hooks;
- transparently rewrite suitable shell commands to Hypa optimised equivalents;
- reduce context waste before tool output reaches agents;
- build durable local context from files, shell commands, code maps, findings, decisions, and evidence;
- optionally attach local events to Atomic users, agent sessions, objectives, jobs, runs, and steps;
- expose diagnostics so agents and humans know which capabilities are available.

## Documentation Map

- [Engineering Principles](./engineering-principles.md) — clean architecture, hexagonal patterns, required design patterns
- [Runtime Design](./runtime-design.md)
- [Data Model](./data-model.md)
- [Command Rewrite](./command-rewrite.md)
- [Compression Pipeline](./compression-pipeline.md)
- [Code Intelligence](./code-intelligence.md)
- [MCP and Tool Surface](./mcp-and-tools.md)
- [Feature Parity Roadmap](./feature-parity-roadmap.md)

## High-Level Architecture

```text
Agent Harness / Editor / Human CLI
        |
        |-- MCP tools
        |-- Shell wrappers
        |-- PreToolUse / command rewrite hooks
        |-- CLI commands
        v
+-----------------------------+
| Hypa Runtime                |
|                             |
|  Runtime Host               |
|  Session Manager            |
|  Command Rewrite Registry   |
|  Command Runner             |
|  Compression Engine         |
|  Code Intelligence Engine   |
|  Tool Registry              |
|  Event / Evidence Ledger    |
|  Atomic Sync Client         |
+-----------------------------+
        |
        |-- Local storage / SQLite
        |-- Project files
        |-- Language servers
        |-- Atomic API, optional
        v
Local Context Store / Code Graph / Atomic Memory
```

## Architectural Principles

### Local-first

Hypa must remain useful without a cloud connection. Atomic authentication adds synchronisation and product integration, not baseline runtime capability.

### Host-adapter model

Agent harnesses change. Hypa should integrate through adapters and stable internal contracts rather than relying on private transcript or session file formats.

### Thin hooks, central rewrite logic

Agent hooks should be thin delegates. They parse host-specific JSON, call Hypa's rewrite service, and format host-specific responses. Command classification, permission decisions, exclusions, and rewrites belong in the central rewrite registry.

### Generic wrapper plus first-class reducers

Hypa supports both generic compression wrapping and first-class command rewrites. Unknown safe commands can route through `hypa -c "<command>"`; known high-value commands should route to specialised modules such as `hypa dotnet test` or `hypa git status`.

### Deterministic baseline

Compression, parsing, and indexing should have deterministic local baselines. LLM summarisation and remote enrichment may exist later, but they should not be required for correctness.

### Provider-based intelligence

Code intelligence is assembled from multiple providers: Roslyn, Tree-sitter, optional language servers, and fallback text parsing. Provider outputs are normalised into Hypa-owned DTOs.

### Provenance everywhere

Facts entering the context store should carry source, provider, confidence, timestamp, and scope. This is critical when merging syntax-derived, semantic, parser-derived, reducer-derived, and agent-authored facts.

### Atomic alignment without Atomic dependency

Local sessions should be able to attach to Atomic concepts when available, but not require them at process start. Job/run/step context can be attached later.

## Initial Implementation Boundaries

Recommended solution/module boundaries:

```text
Hypa.Cli
Hypa.Runtime
Hypa.McpServer
Hypa.Shell
Hypa.AgentHooks
Hypa.CommandRewrite
Hypa.CommandRunner
Hypa.Compression
Hypa.FilterDsl
Hypa.CodeIntelligence
Hypa.ContextSessions
Hypa.Storage
Hypa.AtomicClient
Hypa.Telemetry
```

The modules above are logical boundaries. They do not all need to become separate assemblies immediately, but their responsibilities should remain distinct.

## Related ADRs

- [ADR 0001: Local Context Runtime Operating Model](../ADRs/0001-local-context-runtime-operating-model.md)
- [ADR 0002: Deterministic Tool Output Compression](../ADRs/0002-deterministic-tool-output-compression.md)
- [ADR 0003: Code Intelligence Provider Strategy](../ADRs/0003-code-intelligence-provider-strategy.md)
- [ADR 0004: Optional Language Server Enrichment](../ADRs/0004-optional-language-server-enrichment.md)
- [ADR 0005: Command Rewrite Registry](../ADRs/0005-command-rewrite-registry.md)
- [ADR 0006: Filter DSL and Parser Tiers](../ADRs/0006-filter-dsl-and-parser-tiers.md)
