#!/usr/bin/env bash
# RC replay: AOT VtGoldenGen --compare-h15 against the staged
# libghostty-vt, then the full test filter on that same lib.
#
# Usage:
#   scripts/verify-h15-rc-replay.sh <libghostty-vt> [VtGoldenGen-aot]
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <path-to-libghostty-vt> [path-to-VtGoldenGen-aot]" >&2
  exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
cd "$REPO_ROOT"

LIB="$1"
COMPARE_BIN="${2:-}"

if [[ ! -f "$LIB" ]]; then
  echo "ERROR: missing staged libghostty-vt: $LIB" >&2
  exit 1
fi

export HYPA_GHOSTTY_VT="$(cd "$(dirname "$LIB")" && pwd)/$(basename "$LIB")"
export HYPA_REQUIRE_GHOSTTY_TESTS=1

resolve_compare_bin() {
  if [[ -n "$COMPARE_BIN" && -x "$COMPARE_BIN" ]]; then
    if "$COMPARE_BIN" --help 2>&1 | grep -q -- "--compare"; then
      echo "$COMPARE_BIN"
      return 0
    fi
  fi

  case "$(uname -s)" in
    Linux)
      case "$(uname -m)" in
        x86_64)  RID="linux-x64"  ;;
        aarch64) RID="linux-arm64" ;;
        *) echo "Unsupported Linux arch: $(uname -m)" >&2; exit 1 ;;
      esac
      ;;
    Darwin)
      case "$(uname -m)" in
        x86_64) RID="osx-x64"   ;;
        arm64)  RID="osx-arm64" ;;
        *) echo "Unsupported macOS arch: $(uname -m)" >&2; exit 1 ;;
      esac
      ;;
    *)
      echo "ERROR: H-15 AOT compare is Unix-only (got $(uname -s))" >&2
      exit 1
      ;;
  esac

  local out="$REPO_ROOT/artifacts/vt-golden-gen-$RID"
  echo "H-15 RC replay: publishing AOT VtGoldenGen for $RID" >&2
  # Publish logs stay on stderr so this function returns only the binary path.
  dotnet publish "$REPO_ROOT/tools/VtGoldenGen/VtGoldenGen.csproj" \
    -r "$RID" \
    -c Release \
    -o "$out" \
    /m:1 \
    /p:BuildInParallel=false >&2
  if [[ ! -x "$out/VtGoldenGen" ]]; then
    echo "ERROR: AOT VtGoldenGen missing at $out/VtGoldenGen" >&2
    exit 1
  fi
  echo "$out/VtGoldenGen"
}

COMPARE="$(resolve_compare_bin)"

echo "H-15 RC replay: HYPA_GHOSTTY_VT=$HYPA_GHOSTTY_VT"
echo "H-15 RC replay: AOT compare bin=$COMPARE"

"$COMPARE" --compare-h15

# The H-15 agent golden test classes were removed from
# Hypa.AgentRuntime.Tests. Only the AOT compare above remains.
echo "OK: H-15 AOT compare passed on staged RC lib"
