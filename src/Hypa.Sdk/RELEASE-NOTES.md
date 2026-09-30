# Hypa.Sdk release notes

## Unreleased (v0.3.0 target)

### Additive — doc-section ↔ symbol reference links (`hypa.code-index/2`, version `2.2`)

- Markdown files emit `dependencyEdges` with kind `documents` when a section names or
  links a code symbol/file (inline code `` `Symbol` `` / `` `Ns.Type.Member` ``, or
  markdown links to indexable files). `SourceId` is the section moniker; `SourceSpan` is
  the doc-side occurrence span.
- Successful binds use `targetResolutionStatus: "local-symbol"` (symbol) or the additive
  `"local-file"` (indexed file path). Ambiguous simple names stay `"unresolved"` (no
  same-name false-bind); unknown names are `"external-name"`.
- Incremental `--files` re-emits markdown reverse-dependents when a linked symbol or file
  path re-parses, and re-resolves when either side changes.
- Envelope records and existing edge kinds are unchanged. No SQLite schema revision
  (edges already persist kind/span/resolution status).
- Migration: tolerant consumers may keep pinning major id `hypa.code-index/2` and ignore
  unknown edge kinds/statuses. Consumers that want doc-affinity should require
  `schemaVersion >= 2.2` and project `documents` edges (and optionally `MarkdownSection`
  rows) into their doc-section graph.

### Additive — symbol surface metadata (`hypa.code-index/2`, version `2.1`)

- `CodeSymbol` gains optional `accessibility`, `exportStatus`, `modifiers`, and `signature`
  fields. Existing fields, ordering guarantees, IDs, and envelope records are unchanged.
- C# AST-query symbols report effective accessibility (including omitted-keyword defaults),
  surface-relevant modifiers, and source-text parameter/return-type signatures.
- TypeScript/TSX AST-query symbols report module export status, class-member accessibility
  (including `#private`), modifiers, and source-text signatures.
- Full and incremental exports use the same `CodeSymbol` shape. SQLite schema revision 5
  persists the optional fields so unchanged reverse-dependent records retain them.
- Migration: tolerant existing consumers may continue pinning major id `hypa.code-index/2`
  and ignore the new fields. Consumers that classify public surface should require
  `schemaVersion >= 2.1`, then use C# `accessibility`, or TypeScript `exportStatus` together
  with member `accessibility`. Missing fields mean "metadata unavailable", not public.

### Breaking — index artifact schema `hypa.code-index/2` (introduced at `2.0`)

- `IndexArtifactSchema.Id` changed to `"hypa.code-index/2"`; its initial version was `"2.0"`.
  Full and incremental exports both emit the major id.
- Header `mode` is now meaningful: `"full"` (default) or `"incremental"`.
- File-record `status` is now meaningful: `"indexed"` or `"deleted"` (empty
  symbols/references/edges/sections on deleted).
- Footer gains optional `totalFiles` (project-wide indexed-file count after the
  update), present only in incremental mode (`null`/omitted for full exports).
- Footer `counts` remain sums over **emitted** records only.
- Consumers must tolerate the new major id. Atomic-Private's structural ingestion
  adapter should re-pin to the matching Hypa binary and update its reserved
  `OnlyFiles` path to call `hypa code index --files <manifest>`.

### Additive CLI (paired with this schema)

- `hypa code index --workspace <w> --emit ndjson --files <manifest>` performs an
  incremental export against the persistent SQLite index. Exit code **4** means
  incremental is unavailable (missing/stale db metadata) and the caller should
  fall back to a full export.
