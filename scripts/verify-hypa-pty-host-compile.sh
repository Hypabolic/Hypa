#!/usr/bin/env bash
# compile hypa-pty-host with the release flags (-std=c11 -Werror).
# Fail the job if compile fails. macOS success is not Linux evidence.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/native/hypa-pty-host"

echo "==> hypa-pty-host compile verify"
echo "uname=$(uname -s) arch=$(uname -m)"
echo "CC=${CC:-cc}"
echo "Makefile CFLAGS / CPPFLAGS:"
grep -E '^(CFLAGS|CPPFLAGS)' "$SRC/Makefile" || true
echo "Expected: -std=c11 -Werror and POSIX/XOPEN feature macros."

if ! grep -q -- '-std=c11' "$SRC/Makefile"; then
  echo "ERROR: Makefile must keep -std=c11" >&2
  exit 1
fi
if ! grep -q -- '-Werror' "$SRC/Makefile"; then
  echo "ERROR: Makefile must keep -Werror" >&2
  exit 1
fi
if grep -q -- '-std=gnu11' "$SRC/Makefile"; then
  echo "ERROR: do not switch the helper to -std=gnu11" >&2
  exit 1
fi
if ! grep -q '_POSIX_C_SOURCE=200809L' "$SRC/Makefile"; then
  echo "ERROR: Makefile must set _POSIX_C_SOURCE=200809L" >&2
  exit 1
fi
if ! grep -q '_XOPEN_SOURCE=700' "$SRC/Makefile"; then
  echo "ERROR: Makefile must set _XOPEN_SOURCE=700" >&2
  exit 1
fi

export FORCE_BUILD=1
bash "$ROOT/scripts/build-hypa-pty-host.sh"

# In-tree clean rebuild so compiler warnings are not hidden by a stale object.
make -C "$SRC" clean all

echo "PASS: hypa-pty-host compiled with -std=c11 -Werror"
exit 0
