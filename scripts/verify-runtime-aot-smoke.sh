#!/usr/bin/env bash
# Smoke-test published hypa-runtime with product hypa as the client.
#
# Usage:
#   scripts/verify-runtime-aot-smoke.sh <path-to-hypa-runtime> <path-to-hypa>
#
# Checks:
#   1. Both binaries print help and exit 0
#   2. Start server with a unique session, client ping succeeds, clean shutdown
#
# Notes:
#   - Clang and zlib1g-dev are BUILD-time dependencies for Native AOT on Linux.
#     Published binaries do not need clang on the runtime host.
#   - Unix default panes use hypa-pty-host. process-io is opt-in for tests.
# This smoke checks help + ping only.

set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 <path-to-hypa-runtime> <path-to-hypa>" >&2
  exit 2
fi

RUNTIME_BIN="$1"
CLI_BIN="$2"

if [[ ! -x "$RUNTIME_BIN" ]]; then
  echo "ERROR: hypa-runtime not executable: $RUNTIME_BIN" >&2
  exit 1
fi
if [[ ! -x "$CLI_BIN" ]]; then
  echo "ERROR: hypa not executable: $CLI_BIN" >&2
  exit 1
fi

echo "==> Help smoke: hypa-runtime"
"$RUNTIME_BIN" --help >/dev/null

echo "==> Help smoke: hypa"
"$CLI_BIN" --help >/dev/null

SESSION="ci-aot-$$"
RUNTIME_DIR="${HOME}/.config/hypa/runtime/${SESSION}"
SOCKET_PATH="${RUNTIME_DIR}/hypa.sock"
STATUS_PATH="${RUNTIME_DIR}/runtime.status.json"
SERVER_PID=""

cleanup() {
  if [[ -n "${SERVER_PID}" ]] && kill -0 "${SERVER_PID}" 2>/dev/null; then
    kill "${SERVER_PID}" 2>/dev/null || true
    # Give the host a moment to dispose panes and remove the socket.
    for _ in 1 2 3 4 5; do
      if ! kill -0 "${SERVER_PID}" 2>/dev/null; then
        break
      fi
      sleep 0.2
    done
    kill -9 "${SERVER_PID}" 2>/dev/null || true
    wait "${SERVER_PID}" 2>/dev/null || true
  fi
  # Best-effort cleanup of session files left by a hard kill.
  rm -f "${SOCKET_PATH}" "${STATUS_PATH}" 2>/dev/null || true
  rmdir "${RUNTIME_DIR}" 2>/dev/null || true
}
trap cleanup EXIT

echo "==> Integration smoke: session=${SESSION}"
"$RUNTIME_BIN" --session "${SESSION}" --cwd "${PWD}" &
SERVER_PID=$!

# Wait for socket readiness (status file or socket path).
READY=0
for _ in $(seq 1 50); do
  if [[ -S "${SOCKET_PATH}" ]] || [[ -f "${STATUS_PATH}" ]]; then
    READY=1
    break
  fi
  if ! kill -0 "${SERVER_PID}" 2>/dev/null; then
    echo "ERROR: hypa-runtime exited before becoming ready" >&2
    wait "${SERVER_PID}" || true
    exit 1
  fi
  sleep 0.1
done

if [[ "${READY}" -ne 1 ]]; then
  echo "ERROR: timed out waiting for socket at ${SOCKET_PATH}" >&2
  exit 1
fi

# Short settle so the accept loop is listening.
sleep 0.2

echo "==> Client ping"
PING_OUT="$("$CLI_BIN" --session "${SESSION}" ping)"
echo "${PING_OUT}"

# Match FeedbackFixesTests.UnixSocket_roundtrip_ping: ok == true, protocol == 1.
# Prefer python3 structured parse when available; fall back to field greps.
assert_ping_contract() {
  local out="$1"
  if command -v python3 >/dev/null 2>&1; then
    python3 -c '
import json, sys
raw = sys.stdin.read()
try:
    obj = json.loads(raw)
except json.JSONDecodeError as e:
    print(f"ERROR: ping response is not JSON: {raw!r} ({e})", file=sys.stderr)
    sys.exit(1)
if not isinstance(obj, dict):
    print(f"ERROR: ping response is not a JSON object: {raw!r}", file=sys.stderr)
    sys.exit(1)
if obj.get("ok") is not True:
    print(f"ERROR: expected ok==true, got {obj!r}", file=sys.stderr)
    sys.exit(1)
if obj.get("protocol") != 1:
    print(f"ERROR: expected protocol==1, got {obj!r}", file=sys.stderr)
    sys.exit(1)
print("ping contract ok (python3)")
' <<<"${out}"
    return
  fi

  # No python3: require object braces plus the two contract fields.
  if ! grep -q '{' <<<"${out}"; then
    echo "ERROR: ping response was not JSON: ${out}" >&2
    return 1
  fi
  if ! grep -qE '"ok"[[:space:]]*:[[:space:]]*true' <<<"${out}"; then
    echo "ERROR: ping missing ok:true: ${out}" >&2
    return 1
  fi
  if ! grep -qE '"protocol"[[:space:]]*:[[:space:]]*1([^0-9]|$)' <<<"${out}"; then
    echo "ERROR: ping missing protocol:1: ${out}" >&2
    return 1
  fi
  echo "ping contract ok (grep)"
}

if ! assert_ping_contract "${PING_OUT}"; then
  exit 1
fi

echo "==> Shutting down server"
kill "${SERVER_PID}" 2>/dev/null || true
wait "${SERVER_PID}" 2>/dev/null || true
SERVER_PID=""

echo "OK: runtime AOT smoke passed (help + ping ok/protocol)"
