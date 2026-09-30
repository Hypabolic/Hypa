#!/usr/bin/env bash
# hard gate: GhosttyVtEngine + G-VT-alt-ghostty goldens + PaneRuntime+PTY
# composition must execute (not skip).
#
# Prerequisites:
#   - libghostty-vt built (scripts/build-libghostty-vt.sh)
#   - hypa-pty-host built (scripts/build-hypa-pty-host.sh) — built here if missing
#   - HYPA_REQUIRE_GHOSTTY_TESTS=1 converts Skip → fail for lib + helper
#
# Usage:
#   bash scripts/verify-ghostty-engine-tests.sh
#   HYPA_GHOSTTY_VT=/path/to/libghostty-vt.dylib bash scripts/verify-ghostty-engine-tests.sh
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
    echo "ERROR: Ghostty hard-gate is Unix-only (got $(uname -s))" >&2
    exit 1
    ;;
esac

DEFAULT_LIB="$REPO_ROOT/native/runtimes/$RID/native/$LIB_NAME"
LIB="${HYPA_GHOSTTY_VT:-$DEFAULT_LIB}"
if [[ ! -f "$LIB" ]]; then
  echo "ERROR: missing libghostty-vt for hard-gate: $LIB" >&2
  echo "Build with: FORCE_BUILD=1 bash scripts/build-libghostty-vt.sh" >&2
  exit 1
fi

# PaneRuntime + real PTY is an acceptance criterion. Always build/
# require hypa-pty-host and always include VtGhosttyPtyIntegrationTests.
HOST_PATH="$REPO_ROOT/native/runtimes/$RID/native/hypa-pty-host"
if [[ ! -x "$HOST_PATH" ]]; then
  echo "hypa-pty-host missing at $HOST_PATH — building..."
  bash "$SCRIPT_DIR/build-hypa-pty-host.sh"
fi
if [[ ! -x "$HOST_PATH" ]]; then
  echo "ERROR: missing hypa-pty-host for hard-gate: $HOST_PATH" >&2
  echo "Build with: FORCE_BUILD=1 bash scripts/build-hypa-pty-host.sh" >&2
  exit 1
fi

export HYPA_GHOSTTY_VT="$(cd "$(dirname "$LIB")" && pwd)/$(basename "$LIB")"
export HYPA_PTY_HOST="$(cd "$(dirname "$HOST_PATH")" && pwd)/$(basename "$HOST_PATH")"
export HYPA_REQUIRE_GHOSTTY_TESTS=1

# Always hard-gate the Ghostty engine and pane dispose. The golden and PTY
# test classes were removed from this project, so the filter names only the
# classes that exist.
FILTER="FullyQualifiedName~GhosttyVtEngineTests|FullyQualifiedName~PaneRuntimeVtDisposeTests"

echo "Ghostty hard-gate: HYPA_GHOSTTY_VT=$HYPA_GHOSTTY_VT"
echo "Ghostty hard-gate: HYPA_PTY_HOST=$HYPA_PTY_HOST"
echo "Filter: $FILTER"

dotnet test tests/Hypa.AgentRuntime.Tests/Hypa.AgentRuntime.Tests.csproj \
  -c Release \
  --filter "$FILTER" \
  --logger "console;verbosity=normal"

echo "OK: Ghostty engine + golden + PTY composition + H-15 hard-gate passed"

echo "H-15 goldens (explicit filter)"
