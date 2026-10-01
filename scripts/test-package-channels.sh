#!/usr/bin/env bash
# Exercise the npm, PyPI and Homebrew packaging against a stub release archive.
#
# The release workflow is the first place these packaging scripts run for real,
# and by then the GitHub release already exists. This builds a fake
# hypa-<rid>.tar.gz (shell-script stubs, no real binaries) for the current
# machine and runs it through the same scripts the release uses, then runs the
# installed command. It checks that:
#   - the packages carry the whole release directory, not just `hypa`;
#   - `hypa` finds its sibling executables after install;
#   - a missing execute bit on a sibling is repaired at launch;
#   - the Homebrew template renders to valid Ruby.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)          RID=linux-x64;   EXT=so;    NPM=linux-x64 ;;
  Linux-aarch64|Linux-arm64) RID=linux-arm64; EXT=so; NPM=linux-arm64 ;;
  Darwin-x86_64)         RID=osx-x64;     EXT=dylib; NPM=darwin-x64 ;;
  Darwin-arm64)          RID=osx-arm64;   EXT=dylib; NPM=darwin-arm64 ;;
  *) echo "unsupported host: $(uname -s)-$(uname -m)" >&2; exit 2 ;;
esac
VERSION="9.8.7"

step() { printf '\n==> %s\n' "$*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

step "Build a stub release archive for $RID"
PKG="$WORK/archive/hypa-$RID"
mkdir -p "$PKG"
# hypa starts a sibling from its own directory, like the real binary.
cat > "$PKG/hypa" <<'STUB'
#!/bin/sh
case "$1" in
  --version) echo "9.8.7" ;;
  *) exec "$(cd "$(dirname "$0")" && pwd)/hypa-attach" "$@" ;;
esac
STUB
for sibling in hypa-attach hypa-annotate hypa-runtime hypa-pty-host; do
  printf '#!/bin/sh\necho "%s-ok"\n' "$sibling" > "$PKG/$sibling"
done
chmod +x "$PKG"/hypa*
: > "$PKG/libghostty-vt.$EXT"
: > "$PKG/libe_sqlite3.$EXT"
tar -czf "$WORK/hypa-$RID.tar.gz" -C "$WORK/archive" "hypa-$RID"

step "npm: stage, pack and install the platform package"
bash "$ROOT/npm/scripts/stage-platform-package.sh" "$RID" "$VERSION" "$WORK/hypa-$RID.tar.gz" "$WORK/npm-platform"
[[ "$(jq -r .name "$WORK/npm-platform/package.json")" == "@hypabolic/hypa-$NPM" ]] || fail "wrong npm package name"
if [[ "$EXT" == so ]]; then
  jq -e '.libc == ["glibc"]' "$WORK/npm-platform/package.json" >/dev/null || fail "linux package must declare libc glibc"
fi
(cd "$WORK/npm-platform" && npm pack --silent >/dev/null)

mkdir "$WORK/npm-main"
jq --arg v "$VERSION" --arg dep "$NPM" --arg tgz "file:$WORK/npm-platform/hypabolic-hypa-$NPM-$VERSION.tgz" \
  '.version=$v | .optionalDependencies={("@hypabolic/hypa-" + $dep): $tgz}' \
  "$ROOT/npm/hypa/package.json" > "$WORK/npm-main/package.json"
cp "$ROOT/npm/hypa/bin.js" "$ROOT/npm/hypa/README.md" "$WORK/npm-main/"
(cd "$WORK/npm-main" && npm pack --silent >/dev/null)
npm install --prefix "$WORK/npm-prefix" --ignore-scripts --no-audit --no-fund "$WORK/npm-main/hypabolic-hypa-$VERSION.tgz" >/dev/null

NPM_HYPA="$WORK/npm-prefix/node_modules/.bin/hypa"
[[ "$("$NPM_HYPA" --version)" == "$VERSION" ]] || fail "npm: hypa --version"
[[ "$("$NPM_HYPA" attach)" == "hypa-attach-ok" ]] || fail "npm: hypa did not reach its sibling"
chmod -x "$WORK/npm-prefix/node_modules/@hypabolic/hypa-$NPM/bin/hypa-attach"
[[ "$("$NPM_HYPA" attach)" == "hypa-attach-ok" ]] || fail "npm: launcher did not repair a missing execute bit"

step "PyPI: build the wheel and install it"
python3 -m venv "$WORK/build-venv"
"$WORK/build-venv/bin/pip" install --quiet build wheel
"$WORK/build-venv/bin/python" "$ROOT/python/scripts/build_wheel.py" \
  --rid "$RID" --version "$VERSION" --artifact-dir "$WORK" --output-dir "$WORK/wheelhouse"
WHEEL="$(ls "$WORK"/wheelhouse/*.whl)"
case "$RID" in
  linux-*) [[ "$WHEEL" == *manylinux_2_34_* ]] || fail "wrong wheel tag: $WHEEL" ;;
  osx-*)   [[ "$WHEEL" == *macosx_12_0_* ]]    || fail "wrong wheel tag: $WHEEL" ;;
esac
python3 -m venv "$WORK/py-venv"
"$WORK/py-venv/bin/pip" install --quiet --no-index "$WHEEL"
PY_HYPA="$WORK/py-venv/bin/hypa"
[[ "$("$PY_HYPA" --version)" == "$VERSION" ]] || fail "pip: hypa --version"
[[ "$("$PY_HYPA" attach)" == "hypa-attach-ok" ]] || fail "pip: hypa did not reach its sibling"
SITE="$("$WORK/py-venv/bin/python" -c 'import hypa, os; print(os.path.dirname(hypa.__file__))')"
chmod -x "$SITE/bin/hypa-attach"
[[ "$("$PY_HYPA" attach)" == "hypa-attach-ok" ]] || fail "pip: launcher did not repair a missing execute bit"

step "Homebrew: render the formula template"
sed \
  -e "s|PLACEHOLDER_VERSION|$VERSION|g" \
  -e "s|PLACEHOLDER_SHA_[A-Z0-9_]*|$(printf '0%.0s' $(seq 1 64))|g" \
  "$ROOT/homebrew/hypa.rb.tpl" > "$WORK/hypa.rb"
grep -q PLACEHOLDER "$WORK/hypa.rb" && fail "formula still has placeholders"
if command -v ruby >/dev/null; then
  ruby -c "$WORK/hypa.rb" >/dev/null
fi

printf '\nAll package channel checks passed (%s).\n' "$RID"
