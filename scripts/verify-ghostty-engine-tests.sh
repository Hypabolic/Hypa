#!/usr/bin/env bash
# Hard gate for the test classes with the trait Category=Ghostty. These are
# the classes that need libghostty-vt. The trait is on the class, so the gate
# also runs the tests in those classes that do not need the library. Every
# selected test must run and pass. A skipped test fails the gate. A filter
# that selects no test fails the gate.
#
# A new test class that calls GhosttyTestRequire.RequireNativeLibrary must
# have [Trait("Category", "Ghostty")]. The gate then runs the class. The
# build-test job fails a class that calls the library and has no trait.
# An opt-in class (a benchmark) that skips on its own environment variable
# before the library check must not have the trait, because the gate fails
# on its skip. Example: HistoryCopyBenchmarkTests.
#
# Prerequisites:
#   - libghostty-vt built (scripts/build-libghostty-vt.sh)
#   - hypa-pty-host built (scripts/build-hypa-pty-host.sh). The script builds it when it is missing.
#   - HYPA_REQUIRE_GHOSTTY_TESTS=1 turns a missing library or helper into a failure.
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

# Some tagged tests start a pane on a real PTY, so the gate needs hypa-pty-host.
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

FILTER="Category=Ghostty"
RESULTS_DIR="$REPO_ROOT/TestResults/ghostty-gate"
TRX="$RESULTS_DIR/ghostty.trx"
rm -rf "$RESULTS_DIR"

echo "Ghostty hard-gate: HYPA_GHOSTTY_VT=$HYPA_GHOSTTY_VT"
echo "Ghostty hard-gate: HYPA_PTY_HOST=$HYPA_PTY_HOST"
echo "Filter: $FILTER"

dotnet test tests/Hypa.AgentRuntime.Tests/Hypa.AgentRuntime.Tests.csproj \
  -c Release \
  --filter "$FILTER" \
  --results-directory "$RESULTS_DIR" \
  --logger "trx;LogFileName=ghostty.trx" \
  --logger "console;verbosity=normal"

# xunit leaves the TRX notExecuted counter at 0 for a skipped test. A skip
# shows as a test that is counted in total but not in executed.
if [[ ! -f "$TRX" ]]; then
  echo "ERROR: no TRX result at $TRX" >&2
  exit 1
fi
counter() {
  grep -o "<Counters [^>]*" "$TRX" | head -n 1 | grep -o " $1=\"[0-9]*\"" | grep -o "[0-9]*" || true
}
TOTAL="$(counter total)"
EXECUTED="$(counter executed)"
if [[ -z "$TOTAL" || -z "$EXECUTED" ]]; then
  echo "ERROR: could not read the TRX counters in $TRX" >&2
  exit 1
fi
SKIPPED=$((TOTAL - EXECUTED))
echo "Ghostty hard-gate: total=$TOTAL executed=$EXECUTED skipped=$SKIPPED"
if [[ "$EXECUTED" -eq 0 ]]; then
  echo "ERROR: the filter $FILTER selected no test" >&2
  exit 1
fi
if [[ "$SKIPPED" -ne 0 ]]; then
  echo "ERROR: $SKIPPED Ghostty test(s) skipped. A skip fails this gate." >&2
  exit 1
fi

# The mux stop tests in the unit project start a real mux, which needs the
# library and the hypa binary. A missing binary fails here too.
UNIT_FILTER="FullyQualifiedName~MuxStopCommandTests"
echo "Unit filter: $UNIT_FILTER"
HYPA_REQUIRE_UNIT_TOOLS=1 dotnet test tests/Hypa.UnitTests/Hypa.UnitTests.csproj \
  -c Release \
  --filter "$UNIT_FILTER" \
  --logger "console;verbosity=normal"

echo "OK: every Category=Ghostty test ran and passed, and the mux stop tests passed"
