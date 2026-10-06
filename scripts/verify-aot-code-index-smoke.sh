#!/usr/bin/env bash
# Code-index smokes on a published AOT hypa. The JIT fidelity harness cannot
# prove either check; only the AOT binary can.
#
#   1. The markdown provider reports healthy on a small workspace. This proves
#      provider health, not the time to index this repository.
#   2. tree-sitter queries derive the C# property, field and constructor that
#      the regex fallback misses (Deep Code Graph Slice 1), and the TypeScript
#      interface, type alias, ParentId and queryVersion facts (Slice 4).
#
# Usage:
#   scripts/verify-aot-code-index-smoke.sh <path-to-hypa>
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <path-to-hypa>" >&2
  exit 2
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HYPA="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
if [[ ! -x "$HYPA" ]]; then
  echo "ERROR: hypa not executable: $HYPA" >&2
  exit 1
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/hypa-code-index-smoke.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

echo "==> Markdown provider health"
mkdir "$WORK/ws"
printf '# Smoke\n\nText.\n' >"$WORK/ws/README.md"
(cd "$WORK/ws" && timeout 300 "$HYPA" code index --json) >"$WORK/index.json"
python3 - "$WORK/index.json" <<'PY'
import json, sys
h = json.load(open(sys.argv[1]))['providerHealth']
md = [p for p in h if p['providerId'] == 'markdown']
print(md)
sys.exit(0 if md and md[0]['status'] == 'ok' else 1)
PY

echo "==> tree-sitter query extraction"
"$HYPA" code index --workspace "$ROOT/tests/fixtures/aot-query-smoke" --no-db --emit ndjson --profile code-graph --quiet \
  >"$WORK/query.ndjson"
python3 - "$WORK/query.ndjson" <<'PY'
import json, sys
path = sys.argv[1]
kinds = set()
names_by_kind = {}
providers = set()
with open(path) as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        obj = json.loads(line)
        # NDJSON stream: header, per-file records, footer.
        symbols = obj.get("symbols") or []
        for sym in symbols:
            k = sym.get("kind")
            kinds.add(k)
            names_by_kind.setdefault(k, set()).add(sym.get("name"))
            prov = (sym.get("provenance") or {}).get("providerId")
            if prov:
                providers.add(prov)
print("kinds:", sorted(k for k in kinds if k))
print("names_by_kind:", {k: sorted(v) for k, v in sorted(names_by_kind.items())})
print("providers:", sorted(providers))
missing = [k for k in ("property", "field", "constructor") if k not in kinds]
if missing:
    print("FAIL: AOT binary missing query-derived kinds:", missing, file=sys.stderr)
    sys.exit(1)
expected = {
    "property": "Name",
    "field": "FieldValue",
    "constructor": "Sample",
}
for kind, name in expected.items():
    if name not in names_by_kind.get(kind, set()):
        print(f"FAIL: expected {kind} name {name!r}, got {sorted(names_by_kind.get(kind, set()))}", file=sys.stderr)
        sys.exit(1)
if "tree-sitter-query" not in providers:
    print("FAIL: expected providerId tree-sitter-query, got", sorted(providers), file=sys.stderr)
    sys.exit(1)
print("OK: AOT binary query-derived property/Name + field/FieldValue + constructor/Sample via tree-sitter-query")

# Slice 4: TypeScript AST facts (interface, type-alias, ctor/field ParentId, queryVersion).
ts_kinds = set()
ts_names = {}
ts_parented = []
ts_providers = set()
ts_query_versions = set()
with open(path) as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        obj = json.loads(line)
        if obj.get("record") != "file":
            continue
        lang = (obj.get("file") or {}).get("language")
        if lang != "typescript":
            continue
        for sym in obj.get("symbols") or []:
            k = sym.get("kind")
            ts_kinds.add(k)
            ts_names.setdefault(k, set()).add(sym.get("name"))
            if sym.get("parentId") and k in ("method", "field", "constructor", "property"):
                ts_parented.append((k, sym.get("name")))
            prov = sym.get("provenance") or {}
            if prov.get("providerId"):
                ts_providers.add(prov.get("providerId"))
            if prov.get("queryVersion"):
                ts_query_versions.add(prov.get("queryVersion"))
print("ts_kinds:", sorted(k for k in ts_kinds if k))
print("ts_names:", {k: sorted(v) for k, v in sorted(ts_names.items())})
print("ts_parented:", ts_parented)
print("ts_providers:", sorted(ts_providers))
print("ts_query_versions:", sorted(ts_query_versions))
for kind, name in (
    ("interface", "Renderable"),
    ("type-alias", "WidgetId"),
    ("field", "fieldValue"),
    ("method", "render"),
    ("constructor", "constructor"),
):
    if name not in ts_names.get(kind, set()):
        print(f"FAIL: TS missing {kind} {name!r}, got {sorted(ts_names.get(kind, set()))}", file=sys.stderr)
        sys.exit(1)
if not any(k == "method" and n == "render" for k, n in ts_parented):
    print("FAIL: TS method render missing ParentId", file=sys.stderr)
    sys.exit(1)
if not any(k == "field" and n == "fieldValue" for k, n in ts_parented):
    print("FAIL: TS field fieldValue missing ParentId", file=sys.stderr)
    sys.exit(1)
if not any(k == "constructor" and n == "constructor" for k, n in ts_parented):
    print("FAIL: TS constructor missing ParentId", file=sys.stderr)
    sys.exit(1)
if "tree-sitter-query" not in ts_providers:
    print("FAIL: expected providerId tree-sitter-query for TS, got", sorted(ts_providers), file=sys.stderr)
    sys.exit(1)
# Must match TreeSitterQueryRegistry.TypescriptQueryVersion
# (src/Hypa.Infrastructure/CodeIntelligence/TreeSitterQueryRegistry.cs).
if "tree-sitter-query-typescript-3" not in ts_query_versions:
    print(f"FAIL: expected TS queryVersion tree-sitter-query-typescript-3, got {sorted(ts_query_versions)}", file=sys.stderr)
    sys.exit(1)
print("OK: AOT binary TS query-derived interface/type-alias/ctor/field ParentId + queryVersion")
PY

echo "OK: AOT code-index smokes passed"
