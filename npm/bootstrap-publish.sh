#!/usr/bin/env bash
# Publishes placeholder packages to npmjs.com under the --tag bootstrap
# so that trusted publishers can be configured on each package page.
# These placeholders will never be installed by users (latest tag is untouched).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT

VERSION="0.0.0-bootstrap.1"

echo "Staging dir: $STAGING"
echo "Publishing version $VERSION with --tag bootstrap"
echo

for RID in linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64 win-arm64; do
  case "$RID" in
    linux-x64)   NPM=linux-x64;    OS=linux;  CPU=x64;   BIN=hypa     ;;
    linux-arm64) NPM=linux-arm64;  OS=linux;  CPU=arm64; BIN=hypa     ;;
    osx-x64)     NPM=darwin-x64;   OS=darwin; CPU=x64;   BIN=hypa     ;;
    osx-arm64)   NPM=darwin-arm64; OS=darwin; CPU=arm64; BIN=hypa     ;;
    win-x64)     NPM=win32-x64;    OS=win32;  CPU=x64;   BIN=hypa.exe ;;
    win-arm64)   NPM=win32-arm64;  OS=win32;  CPU=arm64; BIN=hypa.exe ;;
  esac

  STAGE="$STAGING/hypa-$NPM"
  mkdir -p "$STAGE/bin"

  # Stub binary — just a placeholder so the package has valid structure
  printf '#!/bin/sh\necho "placeholder — install via @hypabolic/hypa"\n' > "$STAGE/bin/$BIN"
  chmod +x "$STAGE/bin/$BIN"

  cp "$SCRIPT_DIR/hypa-platform/postinstall.js" "$STAGE/postinstall.js"

  jq \
    --arg name "@hypabolic/hypa-$NPM" \
    --arg version "$VERSION" \
    --arg os "$OS" \
    --arg cpu "$CPU" \
    --arg desc "Hypa native binary for $OS $CPU" \
    '.name=$name | .version=$version | .description=$desc | .os=[$os] | .cpu=[$cpu]' \
    "$SCRIPT_DIR/hypa-platform/package.json" > "$STAGE/package.json"

  echo "Publishing @hypabolic/hypa-$NPM ..."
  npm publish "$STAGE" --access public --tag bootstrap
done

# Main wrapper
STAGE="$STAGING/hypa-main"
mkdir -p "$STAGE"

jq \
  --arg version "$VERSION" \
  'def stamp: if type == "string" and . == "0.0.0" then $version else . end;
   .version = $version |
   .optionalDependencies = (.optionalDependencies | with_entries(.value |= stamp))' \
  "$SCRIPT_DIR/hypa/package.json" > "$STAGE/package.json"

cp "$SCRIPT_DIR/hypa/bin.js" "$STAGE/bin.js"
cp "$SCRIPT_DIR/hypa/README.md" "$STAGE/README.md"

echo "Publishing @hypabolic/hypa ..."
npm publish "$STAGE" --access public --tag bootstrap

echo
echo "Done. All 7 packages published with --tag bootstrap (not latest)."
echo
echo "Next steps:"
echo "  1. For each package on npmjs.com → Settings → Trusted Publishing → Add:"
echo "     Provider: GitHub Actions"
echo "     Owner:    Hypabolic"
echo "     Repo:     Hypa"
echo "     Workflow: release.yml"
echo "  Packages:"
for NPM in linux-x64 linux-arm64 darwin-x64 darwin-arm64 win32-x64 win32-arm64; do
  echo "    https://www.npmjs.com/package/@hypabolic/hypa-$NPM"
done
echo "    https://www.npmjs.com/package/@hypabolic/hypa"
