# Engineering Principles

This document defines the architectural and code-quality standards that apply to all
Hypa source code. These are not guidelines — they are requirements.

## Clean Architecture / Hexagonal Architecture (Ports and Adapters)

Hypa uses Hexagonal Architecture (also known as Clean Architecture or Ports and Adapters).
All code must respect the dependency rule: source code dependencies point inward only.

### Layer Definitions

```
┌──────────────────────────────────────────────────────┐
│  Infrastructure (Adapters)                           │
│  ┌────────────────────────────────────────────────┐  │
│  │  Application (Use Cases / Services)            │  │
│  │  ┌──────────────────────────────────────────┐  │  │
│  │  │  Domain (Entities, Value Objects, Rules) │  │  │
│  │  └──────────────────────────────────────────┘  │  │
│  └────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────┘
```

**Domain layer** (`Hypa.Runtime/Domain/`)

- Contains entities, value objects, domain events, and domain services.
- Has **zero dependencies** on any framework, NuGet package, or other layer.
- Never references infrastructure concepts (databases, HTTP, file paths).
- All types are immutable records or sealed classes by preference.

**Application layer** (`Hypa.Runtime/Application/`)

- Contains use cases, application services, and **Port interfaces**.
- Depends on the Domain layer only.
- Defines all interfaces (Ports) that infrastructure must implement.
- Orchestrates domain objects; contains no business rules itself.
- Does not reference concrete adapter types.

**Infrastructure layer** (`Hypa.Infrastructure/`, `Hypa.Cli/`, etc.)

- Contains all Adapters: implementations of Ports defined in the Application layer.
- Primary adapters (driving): CLI commands, MCP tool handlers, shell hook handlers.
- Secondary adapters (driven): SQLite repositories, file system access, HTTP clients,
  LSP clients, Atomic sync client.
- May reference any framework or external library.
- Must never be referenced by Domain or Application layers.

### Ports and Adapters (P&A) Rules

1. Every external concern (storage, file system, network, clock, environment) is
   accessed through an interface defined in the Application layer.
2. Infrastructure types register themselves against Application interfaces via DI at the
   Composition Root (`Program.cs`).
3. Adapters implement exactly one Port. An adapter that implements multiple unrelated
   Ports is a design smell — split it.
4. Test doubles (stubs, mocks) replace secondary adapters in unit tests. Tests never
   touch real file systems or databases unless explicitly integration tests.

### Project Dependency Graph

```
Hypa.Cli            → Hypa.Runtime, Hypa.Infrastructure
Hypa.Infrastructure → Hypa.Runtime
Hypa.Runtime        → (nothing external)
Hypa.McpServer      → Hypa.Runtime, Hypa.Infrastructure
Hypa.UnitTests      → Hypa.Runtime, Hypa.Infrastructure (with test doubles)
Hypa.GoldenTests    → Hypa.Cli (binary smoke test)
```

No project may introduce a dependency that violates this graph.

## Required Design Patterns

The following design patterns must be used wherever the problem domain calls for them.
Do not invent ad-hoc solutions when a named pattern fits.

### Strategy

Used for any interchangeable algorithm: compressors, reducers, parsers, code intelligence
providers, filter stages, token counters, project root detectors.

```csharp
// Port in Application layer
public interface ICommandOutputCompressor
{
    string Id { get; }
    bool CanHandle(CommandInvocation command);
    CompressionResult Compress(CommandInvocation command, CommandOutput output, CompressionOptions options);
}

// Concrete strategies in Infrastructure
public sealed class GitStatusCompressor : ICommandOutputCompressor { ... }
public sealed class GenericCompressor   : ICommandOutputCompressor { ... }
```

Strategies are registered with DI and resolved via a **Registry** (see below).

### Registry (Variant of Strategy + Factory)

Used for the Rewrite Registry, Reducer Registry, Parser Registry, and Tool Registry.
A Registry holds a collection of strategies and selects the appropriate one at runtime.

```csharp
public interface ICommandRewriteRegistry
{
    RewriteDecision Decide(string command, RewriteContext context);
}
```

The registry iterates registered strategies in priority order; the first that can handle
the input wins. Adding a new strategy requires no changes to the registry itself
(Open/Closed Principle).

### Chain of Responsibility

Used for the compression pipeline, config loading pipeline, and doctor checks. Each
handler in the chain processes the input and either completes it or passes it to the
next handler.

```csharp
public interface IDoctorCheck
{
    string Category { get; }
    DoctorCheckResult Run();
}

// DoctorService iterates all IDoctorCheck implementations
public sealed class DoctorService(IEnumerable<IDoctorCheck> checks)
{
    public IReadOnlyList<DoctorCheckResult> Run() =>
        checks.Select(c => c.Run()).ToList();
}
```

### Command (CLI Command Pattern)

Each CLI subcommand (`doctor`, `session status`, `git status`, etc.) is a discrete class
injected with its dependencies via DI. The command class is responsible for parsing
command-specific arguments and calling the appropriate application service.

```csharp
public sealed class DoctorCommand
{
    private readonly DoctorService _service;
    public DoctorCommand(DoctorService service) => _service = service;
    public Command Build() { ... } // returns System.CommandLine Command
}
```

### Repository

Used for all persistence: sessions, evidence records, artifacts, command metrics, parse
metrics. The interface lives in the Application layer; the SQLite implementation lives
in Infrastructure.

```csharp
// Application Port
public interface ISessionRepository
{
    Task<ContextSession?> FindLatestAsync(string projectRoot, CancellationToken ct);
    Task SaveAsync(ContextSession session, CancellationToken ct);
}

// Infrastructure Adapter
public sealed class SqliteSessionRepository : ISessionRepository { ... }
```

### Factory / Builder

Used to construct complex objects: `CommandInvocation`, `CompressionResult`,
`ContextSession`, `RewriteDecision`. Prefer named factory methods on records over
multi-parameter constructors.

```csharp
public sealed record RewriteDecision
{
    public static RewriteDecision Rewritten(string command) => new(...);
    public static RewriteDecision Passthrough()              => new(...);
    public static RewriteDecision Denied(string reason)      => new(...);
    public static RewriteDecision Ask()                      => new(...);
}
```

### Decorator

Used to add cross-cutting concerns (logging, metrics, caching) to Port implementations
without modifying them.

```csharp
// Adds timing and token savings recording around any compressor
public sealed class TrackedCompressor(ICommandOutputCompressor inner, IMetricsRecorder metrics)
    : ICommandOutputCompressor { ... }
```

### Observer / Event (Domain Events)

Used to propagate domain events (evidence recorded, session checkpointed, artifact stored)
without coupling the domain to infrastructure consumers.

```csharp
public sealed record EvidenceRecordedEvent(EvidenceRecord Record) : IDomainEvent;
```

Infrastructure handlers subscribe at the Composition Root.

### Template Method

Used inside command runners and parser tiers where the skeleton algorithm is fixed but
specific steps vary. Prefer interface composition (Strategy) over inheritance, but
Template Method is acceptable when subclasses share significant state.

### Value Object

All DTOs that represent domain concepts without identity are value objects (C# `record`).
This includes `HypaConfig`, `CommandInvocation`, `CompressionResult`, `Error`, `Result<T,E>`,
`RewriteDecision`, `DoctorCheckResult`, and `ParseTier`.

## General Code Quality Rules

### Immutability first

Prefer `record` and `init`-only properties. Mutable state must be explicitly justified.
Application and domain types are immutable by default.

### Result types over exceptions

Use `Result<T, E>` for expected failure modes (config not found, parse failed, command
denied). Reserve exceptions for programmer errors and unrecoverable conditions.

### No static state

No `static` mutable fields outside of DI registrations and source-gen contexts.
Loggers, config, and services are always injected.

### AOT compatibility

All serialisation uses `System.Text.Json` source generation. No runtime reflection.
`IsAotCompatible=true` is set on all library projects. AOT-incompatible APIs produce
build-time errors.

### Dependency injection

Every non-trivial service is registered with `IServiceCollection` and resolved via
constructor injection. Service lifetimes follow the rule:
- Domain services: `Singleton` or `Transient` (stateless)
- Application services: `Scoped` or `Singleton`
- Repository adapters: `Scoped` (own their connection lifetime)

### Naming conventions

| Concept | Suffix/Prefix |
|---|---|
| Port interface | `I<Noun>` (e.g., `ISessionRepository`) |
| Adapter / implementation | `<Technology><Noun>` (e.g., `SqliteSessionRepository`) |
| Use case / service | `<Noun>Service` or `<Verb>UseCase` |
| Domain event | `<Noun><PastVerb>Event` (e.g., `EvidenceRecordedEvent`) |
| Value object | plain noun (e.g., `HypaConfig`, `RewriteDecision`) |
| Registry | `<Noun>Registry` |
| Factory method | `Create`, `From`, or past-tense noun (e.g., `RewriteDecision.Rewritten(...)`) |
