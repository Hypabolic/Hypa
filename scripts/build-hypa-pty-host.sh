#!/usr/bin/env bash
# Builds hypa-pty-host for the current platform.
# Outputs to native/runtimes/<RID>/native/hypa-pty-host relative to the repo root.
# Idempotent: skips build if the output already exists unless FORCE_BUILD=1.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
SRC_DIR="$REPO_ROOT/native/hypa-pty-host"

case "$(uname -s)" in
  Linux)
    case "$(uname -m)" in
      x86_64)  RID="linux-x64"  ;;
      aarch64) RID="linux-arm64" ;;
      armv7l)  RID="linux-arm"  ;;
      *) echo "Unsupported Linux architecture: $(uname -m)" >&2; exit 1 ;;
    esac
    ;;
  Darwin)
    case "$(uname -m)" in
      x86_64) RID="osx-x64"   ;;
      arm64)  RID="osx-arm64" ;;
      *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 1 ;;
    esac
    ;;
  *)
    echo "Unsupported OS: $(uname -s). hypa-pty-host is Unix-only in H-04." >&2
    exit 1
    ;;
esac

OUTPUT_DIR="$REPO_ROOT/native/runtimes/$RID/native"
OUTPUT_PATH="$OUTPUT_DIR/hypa-pty-host"

if [[ -f "$OUTPUT_PATH" && "${FORCE_BUILD:-0}" != "1" ]]; then
  echo "Already built: $OUTPUT_PATH (set FORCE_BUILD=1 to rebuild)"
  exit 0
fi

LOCK_DIR="/tmp/hypa-pty-host-build-lock-$RID"
release_lock() { rmdir "$LOCK_DIR" 2>/dev/null || true; }
trap release_lock EXIT
attempts=0
while ! mkdir "$LOCK_DIR" 2>/dev/null; do
  attempts=$((attempts + 1))
  if [[ $attempts -gt 600 ]]; then
    echo "ERROR: timed out waiting for build lock $LOCK_DIR" >&2
    exit 1
  fi
  sleep 0.2
done

if [[ -f "$OUTPUT_PATH" && "${FORCE_BUILD:-0}" != "1" ]]; then
  echo "Already built (by a concurrent invocation): $OUTPUT_PATH"
  exit 0
fi

mkdir -p "$OUTPUT_DIR"
echo "Building hypa-pty-host for $RID..."

# Build in a temp dir so parallel RIDs never share object files.
BUILD_DIR="$(mktemp -d "${TMPDIR:-/tmp}/hypa-pty-host-build.XXXXXX")"
cleanup_build() {
  rm -rf "$BUILD_DIR"
  release_lock
}
trap cleanup_build EXIT

cp -R "$SRC_DIR/." "$BUILD_DIR/"
make -C "$BUILD_DIR" OUTPUT="$OUTPUT_PATH" clean all

chmod +x "$OUTPUT_PATH"
echo "Built: $OUTPUT_PATH"
