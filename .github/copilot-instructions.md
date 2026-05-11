# Copilot instructions for Hypa

Purpose: concise, machine-friendly guidance so Copilot sessions (and other assistants) can quickly find build/test/lint commands, the high-level architecture, and repository-specific conventions.

---

## Quick commands (build / test / lint / publish)

- Build (local):
  - dotnet build src/Hypa.Cli/Hypa.Cli.csproj
- Build (CI-like):
  - dotnet build -c Release /p:TreatWarningsAsErrors=true
- Run CLI from source / try commands:
  - dotnet run --project src/Hypa.Cli -- --help
  - dotnet run --project src/Hypa.Cli -- -c "dotnet build"
- Run all tests:
  - dotnet test
  - CI runs unit + golden tests (see .github/workflows/ci.yml)
- Run a single test (example patterns):
  - By test project and filter: dotnet test path/to/Project.Tests --filter "FullyQualifiedName~<TestMethodName>"
  - By display name substring: dotnet test --filter "DisplayName~<substring>"
  - Example: dotnet test src/Hypa.Runtime.Tests --filter "FullyQualifiedName~Hypa.Runtime.Tests.SomeTestClass.SomeTestMethod"
- Format check / lint (CI):
  - dotnet format --verify-no-changes
- AOT publish (example):
  - dotnet publish src/Hypa.Cli/Hypa.Cli.csproj -c Release -r linux-x64

Notes: CI uses dotnet restore → build (Release, TreatWarningsAsErrors) → unit & golden tests → dotnet format --verify-no-changes. See .github/workflows/ci.yml for exact steps.

---

## High-level architecture (big picture)

- Architectural style: Clean Architecture / Hexagonal (Ports & Adapters). The dependency rule is enforced: Hypa.Cli → Hypa.Infrastructure → Hypa.Runtime (Domain + Application).
- Layers:
  - Domain: core business types and rules. No external dependencies.
  - Application: use-cases, port interfaces (repositories, clocks, file providers).
  - Infrastructure: concrete adapters (SQLite, filesystem, network, process runner).
  - CLI: thin frontend that wires DI and commands (src/Hypa.Cli).
- Primary responsibilities:
  - Command runner captures stdout/stderr, applies deterministic reducers and DSL filters, records metrics to local SQLite, and emits compact output.
  - Compression/rewrite pipeline is pluggable via registries and reducers.
- Places to read for details:
  - docs/architecture/engineering-principles.md (design constraints, patterns, naming)
  - README.md (usage, install, local runtime details)

---

## Key repository conventions (non-obvious)

- Immutability and types
  - Prefer C# record types and init-only properties for DTOs/configs.
  - No static mutable state. All services are injected via constructor DI.

- Error handling
  - Use Result<T,E> for expected failures (not exceptions). Exceptions are for programmer/salient failures only.

- AOT and JSON
  - Projects target AOT compatibility. All JSON serialization must use System.Text.Json source-generation (no reflection-based polymorphic patterns that break AOT).

- DI and lifetimes
  - Use IServiceCollection for wiring. Avoid singletons with mutable state; prefer scoped or transient where appropriate.

- Naming and ports
  - Port interfaces follow I<Name> naming (e.g., IFileStore, ICommandRunner).
  - Repositories, providers, and adapters use conventional suffixes (Repository, Provider, Adapter).

- Required design patterns
  - Strategy (algorithms), Registry (rewrite/reducer registries), Chain of Responsibility (compression/doctor checks), Command (CLI subcommands), Repository (persistence), Value Object (record DTOs), and Result<T,E>.

- Tests
  - Project includes unit tests and "golden" tests. Golden tests assert textual outputs and require careful update procedures when outputs intentionally change.

- Local runtime data
  - Hypa stores runtime artifacts under ~/.hypa/ (hypa.db, artifacts, config.json). Useful when debugging local behavior.

---

## Where to look first (short pointers)

- README.md — usage, install, run-from-source, basic commands and examples.
- docs/architecture/engineering-principles.md — authoritative design rules, conventions, and patterns required by the codebase.
- .github/workflows/ci.yml — exact CI validation steps (restore, build, tests, format, AOT publish).
- src/Hypa.Cli — entrypoint and DI wiring.

---

## Notes for Copilot sessions

- Prefer changes that respect the dependency rule (no references from Domain/Application to Infrastructure or CLI).
- When adding JSON types, include System.Text.Json source-generation attributes and register the generated context to keep AOT compatibility.
- For quick local tests, run dotnet test against the specific test project and use --filter to limit to a single test.
- When proposing new public APIs, prefer Result<T,E> over throwing for expected failure modes.

---

If this file should incorporate content from any other repo-specific AI config files (CLAUDE.md, AGENTS.md, CONVENTIONS.md, .clinerules, .cursorrules, .windsurfrules), none were found in the repository root.

---

References: README.md, docs/architecture/engineering-principles.md, .github/workflows/ci.yml

(Generated for Copilot sessions to save time — keep this file focused and update when process/CI changes.)
