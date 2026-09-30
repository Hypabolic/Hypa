#!/usr/bin/env bash
# hard gate: G-agent-codex / G-agent-claude / G-detect / F2 claim gate.
#
# Prerequisites:
#   - libghostty-vt built (scripts/build-libghostty-vt.sh)
#   - HYPA_REQUIRE_GHOSTTY_TESTS=1 converts Skip → fail
#
# Usage:
#   bash scripts/verify-h15-goldens.sh
#   HYPA_GHOSTTY_VT=/path/to/libghostty-vt.dylib bash scripts/verify-h15-goldens.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
cd "$REPO_ROOT"

case "$(uname -s)" in
  Linux)
    case "$(uname -m)" in
      x86_64)  RID="linux-x64"  ;;
      aarch64) RID="linux-arm64" ;;
      *) echo "Unsupported Linux arch: $(uname -m)" >&2; exit 1 ;;
    esac
    LIB_NAME="libghostty-vt.so"
    ;;
  Darwin)
    case "$(uname -m)" in
      x86_64) RID="osx-x64"   ;;
      arm64)  RID="osx-arm64" ;;
      *) echo "Unsupported macOS arch: $(uname -m)" >&2; exit 1 ;;
    esac
    LIB_NAME="libghostty-vt.dylib"
    ;;
  *)
    echo "ERROR: H-15 golden hard-gate is Unix-only (got $(uname -s))" >&2
    exit 1
    ;;
esac

DEFAULT_LIB="$REPO_ROOT/native/runtimes/$RID/native/$LIB_NAME"
LIB="${HYPA_GHOSTTY_VT:-$DEFAULT_LIB}"
if [[ ! -f "$LIB" ]]; then
  echo "ERROR: missing libghostty-vt for H-15 goldens: $LIB" >&2
  echo "Build with: FORCE_BUILD=1 bash scripts/build-libghostty-vt.sh" >&2
  exit 1
fi

export HYPA_GHOSTTY_VT="$(cd "$(dirname "$LIB")" && pwd)/$(basename "$LIB")"
export HYPA_REQUIRE_GHOSTTY_TESTS=1

FILTER="FullyQualifiedName~GAgentCodexGoldenTests|FullyQualifiedName~GAgentClaudeGoldenTests|FullyQualifiedName~GAgentRecordedGoldenTests|FullyQualifiedName~GDetectThresholdTests"

echo "H-15 goldens: HYPA_GHOSTTY_VT=$HYPA_GHOSTTY_VT"
echo "Filter: $FILTER"

dotnet test tests/Hypa.AgentRuntime.Tests/Hypa.AgentRuntime.Tests.csproj \
  -c Release \
  --filter "$FILTER" \
  --logger "console;verbosity=normal"

echo "OK: H-15 agent goldens + detect thresholds + claim gate passed"
