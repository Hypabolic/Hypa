#!/usr/bin/env bash
# Builds libtree-sitter-markdown.so/.dylib for the current platform.
# Outputs to native/runtimes/<RID>/native/ relative to the repo root.
# Idempotent: skips build if the output already exists unless FORCE_BUILD=1.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
GRAMMAR_REPO="https://github.com/tree-sitter-grammars/tree-sitter-markdown.git"
# Pin to a released tag for reproducible, reviewable builds — an unpinned clone of
# the default branch would let arbitrary upstream changes flow into shipped
# artifacts. The cache is keyed by ref so changing the pin re-clones cleanly.
GRAMMAR_REF="v0.5.3"
GRAMMAR_CACHE="/tmp/tree-sitter-markdown-src-$GRAMMAR_REF"

# Detect RID and output filename
case "$(uname -s)" in
  Linux)
    case "$(uname -m)" in
      x86_64)  RID="linux-x64"  ;;
      aarch64) RID="linux-arm64" ;;
      armv7l)  RID="linux-arm"  ;;
      *) echo "Unsupported Linux architecture: $(uname -m)" >&2; exit 1 ;;
    esac
    OUTPUT_FILE="libtree-sitter-markdown.so"
    COMPILE_FLAGS="-shared -fPIC"
    ;;
  Darwin)
    case "$(uname -m)" in
      x86_64) RID="osx-x64"   ;;
      arm64)  RID="osx-arm64" ;;
      *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 1 ;;
    esac
    OUTPUT_FILE="libtree-sitter-markdown.dylib"
    COMPILE_FLAGS="-dynamiclib"
    ;;
  *)
    echo "Unsupported OS: $(uname -s). Use build-tree-sitter-markdown.ps1 on Windows." >&2
    exit 1
    ;;
esac

OUTPUT_DIR="$REPO_ROOT/native/runtimes/$RID/native"
OUTPUT_PATH="$OUTPUT_DIR/$OUTPUT_FILE"

if [[ -f "$OUTPUT_PATH" && "${FORCE_BUILD:-0}" != "1" ]]; then
  echo "Already built: $OUTPUT_PATH (set FORCE_BUILD=1 to rebuild)"
  exit 0
fi

# Multiple MSBuild projects in the same solution build can independently decide
# the grammar is missing and invoke this script at the same time (e.g. `dotnet
# build` on the solution parallelizes across projects). Serialize with a
# mkdir-based lock — mkdir is an atomic test-and-set on all POSIX filesystems,
# so exactly one concurrent invocation wins the race — then re-check under the
# lock in case a sibling invocation just finished the build while we waited.
LOCK_DIR="/tmp/tree-sitter-markdown-build-lock-$RID"
release_lock() { rmdir "$LOCK_DIR" 2>/dev/null || true; }
trap release_lock EXIT
attempts=0
while ! mkdir "$LOCK_DIR" 2>/dev/null; do
  attempts=$((attempts + 1))
  if [[ $attempts -gt 600 ]]; then
    echo "ERROR: timed out waiting for build lock $LOCK_DIR (held by a concurrent build?)" >&2
    exit 1
  fi
  sleep 0.2
done

if [[ -f "$OUTPUT_PATH" && "${FORCE_BUILD:-0}" != "1" ]]; then
  echo "Already built (by a concurrent invocation): $OUTPUT_PATH"
  exit 0
fi

echo "Building tree-sitter-markdown for $RID..."

# Clone the pinned grammar tag if not cached
if [[ ! -d "$GRAMMAR_CACHE" ]]; then
  git clone --depth 1 --branch "$GRAMMAR_REF" "$GRAMMAR_REPO" "$GRAMMAR_CACHE"
fi

SRC_DIR="$GRAMMAR_CACHE/tree-sitter-markdown/src"
mkdir -p "$OUTPUT_DIR"

# -DNDEBUG: the markdown external scanner uses assert(); production must not abort on assert.
# -O2: match released grammar build quality. Still not used on the hot parse path after #92
# (managed ExtractMarkdown), but health probes and any residual native loads stay safe.
gcc $COMPILE_FLAGS -O2 -DNDEBUG \
  -o "$OUTPUT_PATH" \
  "$SRC_DIR/parser.c" \
  "$SRC_DIR/scanner.c" \
  -I"$SRC_DIR"

# Verify the required symbol is exported.
# Use nm -gU on macOS (global defined symbols; -D is Linux-only and fails on dylibs).
# Use nm -D on Linux (dynamic symbol table for shared libraries).
case "$(uname -s)" in
  Darwin) NM_FLAGS="-gU" ;;
  *)      NM_FLAGS="-D"  ;;
esac

if ! nm $NM_FLAGS "$OUTPUT_PATH" | grep -q "tree_sitter_markdown"; then
  echo "ERROR: tree_sitter_markdown symbol not found in $OUTPUT_PATH" >&2
  exit 1
fi

# Verify the binary's architecture matches the target RID, so a mis-targeted
# toolchain (e.g. a cross-arch compiler) can never silently ship a wrong-arch
# library that the runtime would fail to load.
case "$RID" in
  *-x64)   EXPECT_ARCH_RE="x86[_-]64|x86_64" ;;
  *-arm64) EXPECT_ARCH_RE="arm64|aarch64" ;;
  *)       EXPECT_ARCH_RE="" ;;
esac
if [[ -n "$EXPECT_ARCH_RE" ]] && command -v file >/dev/null 2>&1; then
  FILE_DESC="$(file -b "$OUTPUT_PATH")"
  if ! grep -Eqi "$EXPECT_ARCH_RE" <<<"$FILE_DESC"; then
    echo "ERROR: $OUTPUT_PATH architecture does not match $RID: $FILE_DESC" >&2
    exit 1
  fi
fi

echo "Built and verified: $OUTPUT_PATH"
