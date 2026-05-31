#!/usr/bin/env bash
# Builds libtree-sitter-markdown.so/.dylib for the current platform.
# Outputs to native/runtimes/<RID>/native/ relative to the repo root.
# Idempotent: skips build if the output already exists unless FORCE_BUILD=1.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
GRAMMAR_REPO="https://github.com/tree-sitter-grammars/tree-sitter-markdown.git"
GRAMMAR_CACHE="/tmp/tree-sitter-markdown-src"

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

echo "Building tree-sitter-markdown for $RID..."

# Clone grammar source if not cached
if [[ ! -d "$GRAMMAR_CACHE" ]]; then
  git clone --depth 1 "$GRAMMAR_REPO" "$GRAMMAR_CACHE"
fi

SRC_DIR="$GRAMMAR_CACHE/tree-sitter-markdown/src"
mkdir -p "$OUTPUT_DIR"

gcc $COMPILE_FLAGS -O2 \
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

echo "Built and verified: $OUTPUT_PATH"
