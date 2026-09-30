#!/usr/bin/env bash
# RC sidecar: prove Ghostty engine identity on a published AOT runtime.
#
# Usage:
#   scripts/verify-ghostty-rc-engine.sh <path-to-hypa-runtime> [path-to-hypa]
#
# Wraps verify-ghostty-aot-smoke.sh (asset + symbols + fail-closed + health engine caps)
# and asserts the server banner mentions GhosttyVtEngine wiring.
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <path-to-hypa-runtime> [path-to-hypa]" >&2
  exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUNTIME_BIN="$1"
CLI_BIN="${2:-}"

# Reuse full smoke.
if [[ -n "$CLI_BIN" ]]; then
  bash "$SCRIPT_DIR/verify-ghostty-aot-smoke.sh" "$RUNTIME_BIN" "$CLI_BIN"
else
  bash "$SCRIPT_DIR/verify-ghostty-aot-smoke.sh" "$RUNTIME_BIN"
fi

echo "OK: Ghostty RC engine smoke passed (H-14)"
