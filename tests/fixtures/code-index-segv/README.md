# Code-index SEGV fixtures (issue #92)

## Purpose

These fixtures support reliability tests for `hypa code index --emit`.
They exercise native parse cost and isolation wiring.

They do not need to cause a real SIGSEGV on every host.

## Synthetic pack

Path: `synthetic/`

| File | Role |
|---|---|
| `control-small.md` | Small control markdown |
| `stress-md-35k.md` | Stress markdown (~35 KiB) |
| `stress-md-60k.md` | Stress markdown (~60 KiB) |
| `stress-cs-40k.cs` | Stress C# (~40 KiB) |
| `stress-cs-100k.cs` | Stress C# (~100 KiB) |
| `multi/*` | Multi-file workspace for sibling survival tests |

See `MANIFEST.json` for the full list.

## Smoke command

```text
hypa code index --workspace tests/fixtures/code-index-segv/synthetic --emit ndjson --no-db --quiet
```

Accept exit `0` or `3`. Never accept exit `139`.
Require one header line and one footer line in the NDJSON stream.

## Optional Mosaic dogfood

Set `MOSAIC_ROOT` to a local Mosaic checkout. Copy crash paths from issue #92 into a temp workspace. Do not commit Mosaic sources into this repository.

```text
export MOSAIC_ROOT=/path/to/Mosaic
# copy listed crash fixtures into a temp workspace, then:
hypa code index --workspace <tmp> --emit ndjson --no-db --quiet
```

## Isolation notes

Parse isolation is on by default. See
[`docs/architecture/code-index-parse-isolation.md`](../../../docs/architecture/code-index-parse-isolation.md).

Unit tests use a fake worker host to simulate exit 139 without a real SEGV.
