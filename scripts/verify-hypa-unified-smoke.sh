#!/usr/bin/env bash
# smoke-test the unified published hypa binary.
#
# Usage:
#   scripts/verify-hypa-unified-smoke.sh [--profile mux-release|full] <path-to-hypa>
#
# Checks:
#   1. version / doctor / config show / -c echo
#   2. attach --once starts or reconnects the mux and prints ping ok/protocol==1
#   3. mux stop tears the session down

set -euo pipefail

PROFILE="mux-release"
HYPA=""

usage() {
  echo "Usage: $0 [--profile mux-release|full] <path-to-hypa>" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --profile)
      [[ $# -ge 2 ]] || usage
      PROFILE="$2"
      shift 2
      ;;
    -h|--help)
      usage
      ;;
    --)
      shift
      break
      ;;
    -*)
      echo "ERROR: unknown option: $1" >&2
      usage
      ;;
    *)
      if [[ -n "$HYPA" ]]; then
        echo "ERROR: extra argument: $1" >&2
        usage
      fi
      HYPA="$1"
      shift
      ;;
  esac
done

if [[ -z "$HYPA" ]]; then
  usage
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib-mux-release-profile.sh
source "$SCRIPT_DIR/lib-mux-release-profile.sh"
mux_profile_assert_known "$PROFILE" || exit $?

if [[ ! -x "$HYPA" ]]; then
  echo "ERROR: hypa not executable: $HYPA" >&2
  exit 1
fi

echo "    profile=$PROFILE"

if [[ "$PROFILE" == "mux-release" ]]; then
  set +e
  HELP_OUT="$("$HYPA" --help 2>&1)"
  HELP_ST=$?
  set -e
  if [[ "$HELP_ST" -ne 0 ]]; then
    echo "ERROR: hypa --help failed (exit $HELP_ST)" >&2
    printf '%s\n' "$HELP_OUT" >&2
    exit 1
  fi
fi

echo "==> version"
"$HYPA" version

echo "==> doctor"
"$HYPA" doctor

echo "==> config show"
"$HYPA" config show >/dev/null

echo "==> -c echo"
ECHO_OUT="$("$HYPA" -c "echo hello")"
echo "${ECHO_OUT}"
if ! grep -q "hello" <<<"${ECHO_OUT}"; then
  echo "ERROR: hypa -c echo did not print hello" >&2
  exit 1
fi

if [[ "$(uname -s)" == "MINGW"* || "$(uname -s)" == "MSYS"* || "$(uname -s)" == "CYGWIN"* ]]; then
  echo "OK: unified hypa smoke passed (Windows: skipped mux attach)"
  exit 0
fi

SESSION="h16-aot-$$"
RUNTIME_DIR="${HOME}/.config/hypa/runtime/${SESSION}"

cleanup() {
  "$HYPA" mux stop --session "${SESSION}" >/dev/null 2>&1 || true
  rm -f "${RUNTIME_DIR}/hypa.sock" "${RUNTIME_DIR}/runtime.status.json" "${RUNTIME_DIR}/mux.log" 2>/dev/null || true
  rmdir "${RUNTIME_DIR}" 2>/dev/null || true
}
trap cleanup EXIT

echo "==> attach --once session=${SESSION}"
ATTACH_OUT="$("$HYPA" attach --once --session "${SESSION}" --cwd "${PWD}")"
echo "${ATTACH_OUT}"

if ! grep -q "session=${SESSION}" <<<"${ATTACH_OUT}"; then
  echo "ERROR: attach did not print session=${SESSION}" >&2
  exit 1
fi
if ! grep -qE '"ok"[[:space:]]*:[[:space:]]*true' <<<"${ATTACH_OUT}"; then
  echo "ERROR: attach ping missing ok:true" >&2
  exit 1
fi
if ! grep -qE '"protocol"[[:space:]]*:[[:space:]]*1([^0-9]|$)' <<<"${ATTACH_OUT}"; then
  echo "ERROR: attach ping missing protocol:1" >&2
  exit 1
fi

echo "==> mux stop"
"$HYPA" mux stop --session "${SESSION}"
trap - EXIT

echo "OK: unified hypa smoke passed (version, doctor, config, -c, attach ping)"
