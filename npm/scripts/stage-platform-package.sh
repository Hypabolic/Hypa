#!/usr/bin/env bash
# Stage one @hypabolic/hypa-<platform> npm package from a release archive.
#
# Usage: stage-platform-package.sh <rid> <version> <archive> <out-dir>
#   rid      linux-x64 | linux-arm64 | osx-x64 | osx-arm64
#   version  package version without the leading v (for example 1.0.1)
#   archive  hypa-<rid>.tar.gz from the GitHub release
#   out-dir  directory to stage into (created; must not contain files)
#
# The package carries the whole release directory under bin/. hypa finds
# hypa-attach, hypa-runtime, hypa-pty-host, libghostty-vt and its native
# libraries next to its own executable, so the files must stay together.
set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo "usage: $0 <rid> <version> <archive> <out-dir>" >&2
  exit 2
fi

RID="$1"; VERSION="$2"; ARCHIVE="$3"; OUT="$4"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE_DIR="$SCRIPT_DIR/../hypa-platform"

case "$RID" in
  linux-x64)   NPM=linux-x64;    OS=linux;  CPU=x64;   LIBC=glibc ;;
  linux-arm64) NPM=linux-arm64;  OS=linux;  CPU=arm64; LIBC=glibc ;;
  osx-x64)     NPM=darwin-x64;   OS=darwin; CPU=x64;   LIBC=      ;;
  osx-arm64)   NPM=darwin-arm64; OS=darwin; CPU=arm64; LIBC=      ;;
  *) echo "error: unsupported rid '$RID' (the mux ships for linux and macOS only)" >&2; exit 2 ;;
esac

[[ -f "$ARCHIVE" ]] || { echo "error: archive not found: $ARCHIVE" >&2; exit 1; }
if [[ -d "$OUT" ]] && [[ -n "$(ls -A "$OUT")" ]]; then
  echo "error: $OUT is not empty" >&2
  exit 1
fi

mkdir -p "$OUT/bin"
tar -xzf "$ARCHIVE" -C "$OUT/bin" --strip-components=1

# Drop AOT debug sidecars if an older archive still contains them.
find "$OUT/bin" -depth -name '*.dSYM' -type d -exec rm -rf {} +
find "$OUT/bin" \( -name '*.pdb' -o -name '*.dbg' \) -type f -delete

for required in hypa hypa-attach hypa-annotate hypa-runtime hypa-pty-host; do
  [[ -f "$OUT/bin/$required" ]] || { echo "error: $ARCHIVE is missing $required" >&2; exit 1; }
done
compgen -G "$OUT/bin/libghostty-vt.*" >/dev/null \
  || { echo "error: $ARCHIVE is missing libghostty-vt" >&2; exit 1; }

jq \
  --arg name "@hypabolic/hypa-${NPM}" \
  --arg version "${VERSION}" \
  --arg os "${OS}" \
  --arg cpu "${CPU}" \
  --arg libc "${LIBC}" \
  --arg desc "Hypa native binaries for ${OS} ${CPU}" \
  '.name=$name | .version=$version | .description=$desc | .os=[$os] | .cpu=[$cpu]
   | if $libc != "" then .libc=[$libc] else . end' \
  "$TEMPLATE_DIR/package.json" > "$OUT/package.json"

cp "$TEMPLATE_DIR/postinstall.js" "$OUT/postinstall.js"
cp "$TEMPLATE_DIR/README.md" "$OUT/README.md"

echo "Staged @hypabolic/hypa-${NPM}@${VERSION} in ${OUT}"
