#!/usr/bin/env bash
# Test the package manager behaviour of install.sh without a network or a real
# package manager. Fake brew, npm, node, pipx, uv, and curl record what they are
# asked to do. HYPA_TTY / HYPA_TTY_OUT stand in for the terminal.
#
# "direct" below means that the script went on to download the release archive,
# which the fake curl records and then fails, so nothing is installed.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FAKE="$WORK/fake-bin"       # package managers and curl
EMPTY="$WORK/empty-bin"     # a PATH with none of them
LOG="$WORK/calls.log"
mkdir -p "$FAKE" "$EMPTY"

for tool in brew npm pipx uv curl; do
  printf '#!/bin/sh\necho "%s $*" >> "%s"\n' "$tool" "$LOG" > "$FAKE/$tool"
done
# A failing curl ends the direct install right after the download starts.
printf '#!/bin/sh\necho "curl $*" >> "%s"\nexit 22\n' "$LOG" > "$FAKE/curl"
printf '#!/bin/sh\necho 22\n' > "$FAKE/node"
cp "$FAKE/curl" "$EMPTY/curl"
chmod +x "$FAKE"/* "$EMPTY"/*

SYS_PATH="/usr/bin:/bin:/usr/sbin:/sbin"
failures=0

# run NAME [ENV=VALUE ...] -- [install.sh args]   with the fake tools on PATH
# Set USE_PATH to override the PATH. Sets RC and OUT; the call log is $LOG.
run() {
  local name="$1"; shift
  local envs=()
  while [[ $# -gt 0 && "$1" != "--" ]]; do envs+=("$1"); shift; done
  [[ "${1:-}" == "--" ]] && shift
  : > "$LOG"
  : > "$WORK/tty-out"
  OUT="$(env -i HOME="$WORK/home" PATH="${USE_PATH:-$FAKE:$SYS_PATH}" \
    HYPA_TTY_OUT="$WORK/tty-out" ${envs[@]+"${envs[@]}"} "${INSTALL_SH:-sh}" "$ROOT/install.sh" "$@" 2>&1)"
  RC=$?
  TTY_OUT="$(cat "$WORK/tty-out")"
}

pass() { printf 'ok   %s\n' "$1"; }
fail() { printf 'FAIL %s\n' "$1"; failures=$((failures + 1)); }
expect() {  # expect DESCRIPTION CONDITION...
  local desc="$1"; shift
  if "$@"; then pass "$desc"; else fail "$desc (rc=$RC)"; printf '%s\n--- calls:\n%s\n' "$OUT" "$(cat "$LOG")" | sed 's/^/     /'; fi
}
not() { ! "$@"; }
logged() { grep -qxF "$1" "$LOG"; }
not_logged() { ! grep -qF "$1" "$LOG"; }
direct() { grep -q '^curl ' "$LOG"; }
rc_is() { [[ "$RC" == "$1" ]]; }
out_has() { grep -qF "$1" <<< "$OUT"; }
tty_has() { grep -qF "$1" <<< "$TTY_OUT"; }

mkdir -p "$WORK/home"
answer() { printf '%s\n' "$1" > "$WORK/tty-in"; echo "$WORK/tty-in"; }

echo "== explicit method =="
run via-brew HYPA_INSTALL_METHOD=brew --
expect "--via brew runs brew install" logged "brew install hypabolic/tap/hypa"
expect "--via brew exits 0" rc_is 0

run via-npm-pinned HYPA_INSTALL_METHOD=npm HYPA_VERSION=1.2.3 --
expect "npm install pins the version" logged "npm install --global @hypabolic/hypa@1.2.3"

run via-pipx-flag -- --via pipx v1.2.3
expect "pipx install pins the version (v prefix dropped)" logged "pipx install hypa==1.2.3"

run via-uv HYPA_INSTALL_METHOD=uv --
expect "uv tool install" logged "uv tool install hypa"

run brew-pinned HYPA_INSTALL_METHOD=brew HYPA_VERSION=1.2.3 --
expect "Homebrew refuses a pinned version" rc_is 2
expect "Homebrew does not run" not_logged "brew install"

run bad-method HYPA_INSTALL_METHOD=apt --
expect "unknown method is rejected" rc_is 2

run via-with-archive HYPA_INSTALL_METHOD=brew HYPA_ARCHIVE=/nonexistent.tar.gz --
expect "--via with an archive is rejected" rc_is 2

echo "== offer on an interactive run =="
run menu HYPA_TTY="$(answer 2)" --
expect "menu lists direct first" tty_has "1) Install directly (default)"
expect "menu lists Homebrew" tty_has "2) Homebrew"
expect "menu lists npm" tty_has "3) npm"
expect "answer 2 installs with Homebrew" logged "brew install hypabolic/tap/hypa"

run pick-npm HYPA_TTY="$(answer 3)" --
expect "answer 3 installs with npm" logged "npm install --global @hypabolic/hypa"

run pick-default HYPA_TTY="$(answer '')" --
expect "empty answer installs directly" direct
expect "empty answer runs no package manager" not_logged "install --global"

run pick-one HYPA_TTY="$(answer 1)" --
expect "answer 1 installs directly" direct

run pick-bad HYPA_TTY="$(answer 9)" --
expect "invalid answer stops" rc_is 2
expect "invalid answer installs nothing" not_logged "curl "

run pinned-menu HYPA_TTY="$(answer 2)" HYPA_VERSION=1.2.3 --
expect "pinned version leaves Homebrew off the menu" not tty_has "Homebrew"

USE_PATH="$EMPTY:$SYS_PATH" run no-manager HYPA_TTY="$(answer 2)" --
expect "no package manager: no prompt" not tty_has "Choose"
unset USE_PATH

run no-tty HYPA_TTY=/nonexistent/tty --
expect "no terminal: no prompt" not tty_has "Choose"
expect "no terminal: installs directly" direct
expect "no terminal: runs no package manager" not_logged "brew install"

run dir-set HYPA_TTY="$(answer 2)" HYPA_INSTALL_DIR="$WORK/bin" --
expect "HYPA_INSTALL_DIR skips the offer" not tty_has "Choose"
expect "HYPA_INSTALL_DIR installs directly" direct

run f2-set HYPA_TTY="$(answer 2)" HYPA_CHANNEL=f2 --
expect "the F2 channel skips the offer" not tty_has "Choose"

echo "== a package-managed copy already exists =="
CELLAR="$WORK/prefix/Cellar/hypa/1.0.0/libexec"
mkdir -p "$CELLAR" "$WORK/prefix/bin"
printf '#!/bin/sh\n' > "$CELLAR/hypa"; chmod +x "$CELLAR/hypa"
ln -s ../Cellar/hypa/1.0.0/libexec/hypa "$WORK/prefix/bin/hypa"
BREW_PATH="$WORK/prefix/bin:$FAKE:$SYS_PATH"

USE_PATH="$BREW_PATH" run owned-no-tty HYPA_TTY=/nonexistent/tty --
expect "existing Homebrew copy stops a non-interactive install" rc_is 1
expect "the message names the owner and the upgrade command" out_has "brew upgrade hypa"
expect "nothing is installed" not_logged "curl "

USE_PATH="$BREW_PATH" run owned-no HYPA_TTY="$(answer n)" --
expect "declining leaves things alone" rc_is 0
expect "declining installs nothing" not_logged "curl "

USE_PATH="$BREW_PATH" run owned-yes HYPA_TTY="$(answer y)" --
expect "agreeing installs a second copy directly" direct

USE_PATH="$BREW_PATH" run owned-script HYPA_INSTALL_METHOD=script --
expect "--via script installs a second copy without asking" direct

NPM_PATH="$WORK/node/bin"
mkdir -p "$WORK/node/lib/node_modules/@hypabolic/hypa" "$NPM_PATH"
printf '#!/usr/bin/env node\n' > "$WORK/node/lib/node_modules/@hypabolic/hypa/bin.js"
chmod +x "$WORK/node/lib/node_modules/@hypabolic/hypa/bin.js"
ln -s ../lib/node_modules/@hypabolic/hypa/bin.js "$NPM_PATH/hypa"
USE_PATH="$NPM_PATH:$FAKE:$SYS_PATH" run owned-npm HYPA_TTY=/nonexistent/tty --
expect "existing npm copy is recognised" out_has "npm install --global @hypabolic/hypa@latest"

PIP_PATH="$WORK/venv/bin"
mkdir -p "$PIP_PATH"
printf '#!/usr/bin/python3\nfrom hypa._runner import main\n' > "$PIP_PATH/hypa"; chmod +x "$PIP_PATH/hypa"
USE_PATH="$PIP_PATH:$FAKE:$SYS_PATH" run owned-pip HYPA_TTY=/nonexistent/tty --
expect "existing pip console script is recognised" out_has "python3 -m pip install --upgrade hypa"

USE_PATH="$BREW_PATH" run owned-archive HYPA_ARCHIVE=/nonexistent.tar.gz --
expect "an archive install ignores package-managed copies" not out_has "already installed"

echo
if [[ $failures -ne 0 ]]; then
  echo "$failures check(s) failed" >&2
  exit 1
fi
echo "All install.sh package manager checks passed."
