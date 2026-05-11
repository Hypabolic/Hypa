# ADR 0007: SDK Multi-Language Generation Strategy

## Status

Accepted

## Date

2026-05-11

## Context

`Hypa.Sdk` contains the shared domain model for Hypa's code intelligence output — types such as `CodeSymbol`, `CodeDependencyEdge`, `CodeReference`, `CodeStructureDocument`, `CodeGraphResult`, `CodeFileIdentity`, `SourceSpan`, and their associated query/result contracts.

This model was extracted from `Hypa.Runtime` so that external consumers — primarily the Atomic platform — could reference it without taking a dependency on Hypa's infrastructure. However, both Hypa and Atomic are .NET projects. As the ecosystem grows, other consumers may be written in TypeScript (Next.js front-end, agent tooling), Python (AI agent frameworks), Go, or other languages.

There are two distinct integration surfaces to consider:

1. **Data model only** — a consumer needs to deserialise `Hypa.Sdk` types from JSON (e.g. a CLI output, an MCP tool response, or a stored index file). No HTTP call is involved.
2. **Service client** — a consumer calls a Hypa HTTP API and needs a typed request/response client. This applies once Hypa exposes an HTTP or MCP-over-HTTP endpoint.

These surfaces require different generation strategies. Conflating them leads to overly complex tooling or generated clients that carry unused HTTP plumbing.

## Decision

Multi-language SDK generation is split into two tiers, applied in order of need:

### Tier 1 — JSON Schema type generation (immediate)

`Hypa.Sdk` emits a `hypa-sdk-schema.json` as a build artefact. This schema is generated from the C# record types using **NJsonSchema** and is committed to the repository as a versioned contract file.

**Quicktype** consumes the schema in CI to generate typed model classes for each target language. The initial targets are:
- TypeScript (for the Atomic Next.js frontend and agent tooling)
- Python (for AI agent frameworks that consume Hypa output)

Additional languages (Go, Rust, Java) are added on-demand by extending the CI matrix — no changes to `Hypa.Sdk` itself are required.

The C# records remain the single source of truth. All generated files are derived artefacts.

### Tier 2 — OpenAPI client generation (deferred until HTTP API exists)

Once Hypa exposes an HTTP endpoint (via a future `Hypa.Api` project or the existing MCP server surface), an OpenAPI specification is generated from that API. **Microsoft Kiota** then generates strongly-typed HTTP clients from the spec.

Kiota is preferred over OpenAPI Generator for this tier because:
- It is maintained by Microsoft and is the standard for .NET-first API client generation.
- It generates idiomatic clients in C#, TypeScript, Python, Go, Java, PHP, and Ruby.
- It is already used by the Microsoft Graph SDK, which shares architectural patterns with Hypa's MCP integration.
- It produces minimal, composable client code without heavy runtime dependencies.

Tier 2 is explicitly deferred. No HTTP generation tooling is added until there is a defined HTTP surface to generate from.

## Consequences

### Positive

- TypeScript types for `CodeSymbol`, `CodeGraphResult`, etc. are available to the Atomic Next.js frontend without manual duplication.
- The schema file acts as a versioned, language-neutral contract — consumers can validate their deserialisation against it independently.
- CI catches any breaking model changes automatically by failing schema generation or downstream type generation.
- Adding a new target language requires only a Quicktype CLI flag, not a code change.
- Tier 2 is well-scoped and not attempted prematurely — no HTTP plumbing added before the service exists.

### Negative / Trade-offs

- Generated files in other languages must be regenerated and republished when `Hypa.Sdk` types change. This adds a release coordination step.
- NJsonSchema's JSON Schema output may not perfectly represent all C# constructs (discriminated unions, nullable reference types). Minor schema annotations or workarounds may be needed.
- Quicktype-generated Python/TypeScript code does not include serialisation logic beyond simple JSON parse — consumers using non-standard transports must handle their own marshalling.
- Kiota (Tier 2) requires an OpenAPI spec to exist before client generation can begin. If Hypa's HTTP surface is ad-hoc or undocumented, Tier 2 cannot be applied.

## Implementation Notes

### Tier 1 — JSON Schema + Quicktype

**Step 1: Emit schema from `Hypa.Sdk`**

Add NJsonSchema as a build-time tool or post-build target in `Hypa.Sdk.csproj`:

```xml
<Target Name="GenerateJsonSchema" AfterTargets="Build">
  <Exec Command="dotnet njsonschema /input:$(OutputPath)Hypa.Sdk.dll /output:$(RepoRoot)docs/sdk/hypa-sdk-schema.json" />
</Target>
```

Alternatively, write a small console tool in `tools/GenerateSchema/` that uses the `NJsonSchema.JsonSchema.FromType<T>()` API to emit a consolidated schema for all public SDK types.

The schema file lives at `docs/sdk/hypa-sdk-schema.json` and is committed to the repository.

**Step 2: Generate language targets in CI**

```yaml
# .github/workflows/sdk-codegen.yml (excerpt)
- name: Generate TypeScript types
  run: npx quicktype --src docs/sdk/hypa-sdk-schema.json --lang typescript -o sdk/typescript/hypaTypes.ts

- name: Generate Python types
  run: npx quicktype --src docs/sdk/hypa-sdk-schema.json --lang python -o sdk/python/hypa_types.py
```

Generated files are placed under `sdk/{language}/` and committed (or published as release artefacts depending on consumption pattern).

**Step 3: Atomic integration**

Atomic's Next.js frontend imports from `sdk/typescript/hypaTypes.ts` directly (via a path alias or npm local package). No manual type duplication. `CodeSymbol`, `CodeGraphResult`, and related types are available with full IntelliSense.

### Tier 2 — OpenAPI + Kiota (deferred)

When a `Hypa.Api` project exists:

```bash
# Install Kiota
dotnet tool install Microsoft.OpenApi.Kiota -g

# Generate TypeScript client
kiota generate \
  --openapi ./docs/sdk/hypa-openapi.json \
  --language typescript \
  --class-name HypaClient \
  --output ./sdk/typescript/client

# Generate Python client
kiota generate \
  --openapi ./docs/sdk/hypa-openapi.json \
  --language python \
  --class-name HypaClient \
  --output ./sdk/python/client
```

Tier 2 clients import Tier 1 model types — the request/response bodies use the same `CodeSymbol`, `CodeGraphResult` records as the schema-generated types.

### Directory layout

```
docs/
  sdk/
    hypa-sdk-schema.json        ← Tier 1: committed, generated from Hypa.Sdk
    hypa-openapi.json           ← Tier 2: committed when API exists
sdk/
  typescript/
    hypaTypes.ts                ← Tier 1: Quicktype output
    client/                     ← Tier 2: Kiota output (future)
  python/
    hypa_types.py               ← Tier 1: Quicktype output
    client/                     ← Tier 2: Kiota output (future)
```

## Alternatives Considered

### OpenAPI Generator (instead of Kiota for Tier 2)

OpenAPI Generator supports more languages and has a larger community. It was not chosen for Tier 2 because it produces heavier, more opinionated client code and requires a Java runtime. Kiota is a better fit for the .NET-first toolchain, produces leaner output, and is the direction Microsoft is investing in for Graph SDK-style API clients.

### TypeSpec as source of truth (instead of C# records)

Microsoft TypeSpec allows defining a language-neutral API schema that generates both OpenAPI and language-specific clients. It was considered as a replacement for NJsonSchema because it handles discriminated unions and complex types more cleanly.

It was deferred rather than rejected. If the JSON Schema approach produces too many annotation workarounds, migrating `Hypa.Sdk` to TypeSpec as the canonical model definition is the natural next step. TypeSpec can generate C# types (replacing the handwritten records), a JSON Schema, and an OpenAPI spec from a single `.tsp` source file.

### Protobuf / gRPC

Protobuf was considered for its strong cross-language support and efficient binary encoding. It was not chosen because Hypa's primary integration pattern is JSON over HTTP/MCP, the toolchain overhead is significant for an early-stage project, and the schema-first model in Protobuf would require rewriting the existing C# records rather than deriving from them.

## Related Decisions

- ADR 0003: Code Intelligence Provider Strategy
- ADR 0001: Local Context Runtime Operating Model
