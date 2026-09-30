#!/usr/bin/env bash
# install execute smoke from an F1 archive or a linked hypa.
#
# Usage:
#   scripts/verify-f1-install-smoke.sh --archive artifacts/dist/hypa-$RID.tar.gz --rid $RID
#   scripts/verify-f1-install-smoke.sh --hypa /path/to/hypa --rid $RID
#   scripts/verify-f1-install-smoke.sh --self-test-isolation
#   scripts/verify-f1-install-smoke.sh --self-test-store-proof PATH
#   scripts/verify-f1-install-smoke.sh --self-test-store-mutate
#
# Do not export HYPA_PTY_PROVIDER=process-io.
# Default VT is Ghostty. Do not export HYPA_VT_PROVIDER=basic.
# Inherited HYPA_RUNTIME_SOCKET / HYPA_RUNTIME_STATE_DIR are cleared and
# replaced with paths under the per-run prefix.
# Bare hypa without --once waits for Ctrl+C. This smoke uses attach --once.

set -euo pipefail

ARCHIVE=""
HYPA=""
RID=""
PROFILE="mux-release"
REQUIRE_STORE_TRACE=0
SELF_TEST_ISOLATION=0
SELF_TEST_STORE_PROOF=""
SELF_TEST_STORE_MUTATE=0

usage() {
  echo "Usage: $0 [--archive PATH] [--hypa PATH] [--rid RID] [--profile mux-release|full] [--require-store-trace] [--self-test-isolation] [--self-test-store-proof PATH] [--self-test-store-mutate]" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --archive)
      [[ $# -ge 2 ]] || usage
      ARCHIVE="$2"
      shift 2
      ;;
    --hypa)
      [[ $# -ge 2 ]] || usage
      HYPA="$2"
      shift 2
      ;;
    --rid)
      [[ $# -ge 2 ]] || usage
      RID="$2"
      shift 2
      ;;
    --profile)
      [[ $# -ge 2 ]] || usage
      PROFILE="$2"
      shift 2
      ;;
    --require-store-trace)
      REQUIRE_STORE_TRACE=1
      shift
      ;;
    --self-test-isolation)
      SELF_TEST_ISOLATION=1
      shift
      ;;
    --self-test-store-proof)
      [[ $# -ge 2 ]] || usage
      SELF_TEST_STORE_PROOF="$2"
      shift 2
      ;;
    --self-test-store-mutate)
      SELF_TEST_STORE_MUTATE=1
      shift
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
      echo "ERROR: extra argument: $1" >&2
      usage
      ;;
  esac
done

if [[ "$SELF_TEST_ISOLATION" -ne 1 && -z "$SELF_TEST_STORE_PROOF" && "$SELF_TEST_STORE_MUTATE" -ne 1 && -z "$ARCHIVE" && -z "$HYPA" ]]; then
  echo "ERROR: pass --archive, --hypa, or --self-test-isolation" >&2
  usage
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PACK_SMOKE="$REPO_ROOT/scripts/verify-f1-pack-smoke.sh"
INSTALL_SH="$REPO_ROOT/install.sh"
# shellcheck source=lib-mux-release-profile.sh
source "$SCRIPT_DIR/lib-mux-release-profile.sh"
mux_profile_assert_known "$PROFILE" || exit $?

if [[ -z "$RID" ]]; then
  case "$(uname -s)" in
    Darwin) RID="osx-$(uname -m | sed 's/x86_64/x64/;s/arm64/arm64/;s/aarch64/arm64/')" ;;
    Linux) RID="linux-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/')" ;;
    MINGW*|MSYS*|CYGWIN*) RID="win-x64" ;;
    *)
      echo "ERROR: cannot infer RID; pass --rid" >&2
      exit 1
      ;;
  esac
fi

is_windows=0
case "$RID" in
  win-*) is_windows=1 ;;
esac
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) is_windows=1 ;;
esac

PREFIX="$(mktemp -d "${TMPDIR:-/tmp}/hypa-f1-install-smoke.XXXXXX")"
HOME_DIR="$PREFIX/home"
STATE_DIR="$PREFIX/state"
SOCKET_PATH="$STATE_DIR/hypa.sock"
mkdir -p "$HOME_DIR" "$STATE_DIR"

# Inherited HYPA_RUNTIME_SOCKET / HYPA_RUNTIME_STATE_DIR can retarget
# attach/health to a shared socket or database. Replace both with paths
# under this per-run prefix. Do not keep the caller HOME as the smoke HOME.
unset HYPA_RUNTIME_SOCKET || true
unset HYPA_RUNTIME_STATE_DIR || true
unset HYPA_PTY_PROVIDER || true
unset HYPA_VT_PROVIDER || true
export HOME="$HOME_DIR"
export HYPA_RUNTIME_STATE_DIR="$STATE_DIR"
export HYPA_RUNTIME_SOCKET="$SOCKET_PATH"
mux_profile_export_isolation "$PREFIX"

cleanup() {
  if [[ -n "${HYPA:-}" && -n "${SESSION:-}" ]]; then
    "$HYPA" mux stop --session "${SESSION}" --socket "${SOCKET_PATH}" >/dev/null 2>&1 || true
  fi
  rm -rf "$PREFIX"
}
trap cleanup EXIT

if [[ "$SELF_TEST_ISOLATION" -eq 1 ]]; then
  echo "==> F1 install smoke isolation self-test"
  echo "    prefix=$PREFIX"
  echo "    home=$HOME"
  echo "    socket=$HYPA_RUNTIME_SOCKET"
  echo "    state=$HYPA_RUNTIME_STATE_DIR"
  case "$HOME" in
    "$PREFIX"/*) ;;
    *)
      echo "ERROR: HOME is not under the per-run prefix ($HOME)" >&2
      exit 1
      ;;
  esac
  case "$HYPA_RUNTIME_SOCKET" in
    "$PREFIX"/*) ;;
    *)
      echo "ERROR: HYPA_RUNTIME_SOCKET is not under the per-run prefix ($HYPA_RUNTIME_SOCKET)" >&2
      exit 1
      ;;
  esac
  case "$HYPA_RUNTIME_STATE_DIR" in
    "$PREFIX"/*) ;;
    *)
      echo "ERROR: HYPA_RUNTIME_STATE_DIR is not under the per-run prefix ($HYPA_RUNTIME_STATE_DIR)" >&2
      exit 1
      ;;
  esac
  echo "OK: F1 install smoke isolation"
  exit 0
fi

SENTINEL_DIR="$PREFIX/store-sentinels"
TRACE="$PREFIX/store.trace"
USE_STRACE=0

if [[ "$PROFILE" == "mux-release" ]]; then
  mux_profile_seed_store_sentinels "$SENTINEL_DIR"
  if [[ "$SELF_TEST_STORE_MUTATE" -eq 1 ]]; then
    printf 'mutated\n' >"${XDG_STATE_HOME}/hypa/placements/extra.json"
  fi
  if [[ -n "$SELF_TEST_STORE_PROOF" ]]; then
    mux_profile_assert_store_sentinels "$SENTINEL_DIR"
    mux_profile_prepare_self_test_trace "$SELF_TEST_STORE_PROOF" "$TRACE" "$STATE_DIR" "$SOCKET_PATH"
    mux_profile_assert_store_trace "$TRACE" "$STATE_DIR"
    echo "    store_read_proof=strace"
    echo "OK: F1 install smoke store proof"
    exit 0
  fi
  if [[ "$SELF_TEST_STORE_MUTATE" -eq 1 ]]; then
    mux_profile_assert_store_sentinels "$SENTINEL_DIR"
    echo "OK: F1 install smoke store mutate"
    exit 0
  fi
fi

echo "==> F1 install execute smoke"
echo "    rid=$RID"
echo "    prefix=$PREFIX"
echo "    home=$HOME"
echo "    socket=$HYPA_RUNTIME_SOCKET"
echo "    state=$HYPA_RUNTIME_STATE_DIR"

if [[ -n "$ARCHIVE" ]]; then
  if [[ ! -f "$ARCHIVE" ]]; then
    echo "ERROR: archive does not exist: $ARCHIVE" >&2
    exit 1
  fi
  if [[ ! -x "$PACK_SMOKE" && ! -f "$PACK_SMOKE" ]]; then
    echo "ERROR: missing $PACK_SMOKE" >&2
    exit 1
  fi
  echo "==> inventory (verify-f1-pack-smoke)"
  bash "$PACK_SMOKE" --rid "$RID" --channel f1 --profile "$PROFILE" "$ARCHIVE"

  if [[ "$is_windows" -eq 1 ]]; then
    echo "OK: F1 install smoke skipped mux on Windows (archive inventory passed)"
    exit 0
  fi

  if [[ ! -f "$INSTALL_SH" ]]; then
    echo "ERROR: missing $INSTALL_SH" >&2
    exit 1
  fi

  INSTALL_DIR="$PREFIX/bin"
  APP_DIR="$PREFIX/share/hypa"
  mkdir -p "$INSTALL_DIR" "$APP_DIR" "$HOME_DIR"

  echo "==> install from archive into isolated prefix"
  HYPA_ARCHIVE="$ARCHIVE" \
    HYPA_INSTALL_DIR="$INSTALL_DIR" \
    HYPA_APP_DIR="$APP_DIR" \
    HOME="$HOME_DIR" \
    sh "$INSTALL_SH" --from-archive "$ARCHIVE"

  HYPA="$INSTALL_DIR/hypa"
  if [[ ! -e "$HYPA" ]]; then
    echo "ERROR: installer did not link $INSTALL_DIR/hypa" >&2
    exit 1
  fi
  if [[ "$(basename "$HYPA")" != "hypa" ]]; then
    echo "ERROR: product PATH name must be hypa (got $(basename "$HYPA"))" >&2
    exit 1
  fi
  if [[ -e "$INSTALL_DIR/hypa-runtime" ]]; then
    echo "ERROR: installer must not link hypa-runtime as the PATH name" >&2
    exit 1
  fi
  if [[ ! -f "$APP_DIR/hypa" ]]; then
    echo "ERROR: hypa was not installed under HYPA_APP_DIR" >&2
    exit 1
  fi
  if [[ ! -x "$APP_DIR/hypa-pty-host" ]]; then
    echo "ERROR: hypa-pty-host missing or not executable under HYPA_APP_DIR" >&2
    exit 1
  fi
  if [[ ! -x "$APP_DIR/hypa-attach" ]]; then
    echo "ERROR: hypa-attach missing or not executable under HYPA_APP_DIR" >&2
    exit 1
  fi
  if [[ ! -x "$APP_DIR/hypa-annotate" ]]; then
    echo "ERROR: hypa-annotate missing or not executable under HYPA_APP_DIR" >&2
    exit 1
  fi
  if [[ ! -x "$APP_DIR/hypa-runtime" ]]; then
    echo "ERROR: hypa-runtime missing or not executable under HYPA_APP_DIR" >&2
    exit 1
  fi
  if [[ ! -f "$APP_DIR/libe_sqlite3.so" && ! -f "$APP_DIR/libe_sqlite3.dylib" ]]; then
    echo "ERROR: SQLite native missing under HYPA_APP_DIR" >&2
    exit 1
  fi
  if [[ -e "$APP_DIR/hypa.channel" ]]; then
    echo "ERROR: F1 install must not leave hypa.channel (F2 leftover)" >&2
    exit 1
  fi
  if [[ ! -f "$APP_DIR/libghostty-vt.so" && ! -f "$APP_DIR/libghostty-vt.dylib" ]]; then
    echo "ERROR: F1 install requires libghostty-vt" >&2
    exit 1
  fi
  echo "    hypa=$HYPA"
  echo "    app=$APP_DIR"
else
  if [[ ! -x "$HYPA" ]]; then
    echo "ERROR: hypa not executable: $HYPA" >&2
    exit 1
  fi
  if [[ "$(basename "$HYPA")" != "hypa" && "$(basename "$HYPA")" != "hypa.exe" ]]; then
    echo "ERROR: product binary name must be hypa (got $(basename "$HYPA"))" >&2
    exit 1
  fi
fi

if [[ "$is_windows" -eq 1 ]]; then
  echo "OK: F1 install smoke skipped mux on Windows (product name hypa)"
  exit 0
fi

# Isolation already replaced HOME / HYPA_RUNTIME_SOCKET / HYPA_RUNTIME_STATE_DIR.
# Do not export HYPA_PTY_PROVIDER=process-io.
# Default VT is Ghostty. Explicit basic must fail.
mkdir -p "$HOME"
unset HYPA_PTY_PROVIDER || true
unset HYPA_VT_PROVIDER || true

if [[ "$PROFILE" == "mux-release" ]]; then
  if command -v strace >/dev/null 2>&1 && [[ "$(uname -s)" == Linux ]]; then
    USE_STRACE=1
    : >"$TRACE"
  elif [[ "$REQUIRE_STORE_TRACE" -eq 1 ]]; then
    echo "ERROR: --require-store-trace needs strace on Linux" >&2
    exit 1
  fi
fi

run_hypa() {
  if [[ "$USE_STRACE" -eq 1 ]]; then
    # strace -f follows a runtime that the client starts and leaves running,
    # so strace does not exit when the client exits. Run the client in a
    # wrapper that records its status, then stop strace. SIGTERM detaches
    # strace and leaves the traced runtime running. Capture stdout in a file:
    # the runtime keeps the pipe of a command substitution open.
    local part outf rcf st strace_pid waited=0
    part="$(mktemp "$PREFIX/strace.XXXXXX")"
    outf="$(mktemp "$PREFIX/strace-out.XXXXXX")"
    rcf="$(mktemp -u "$PREFIX/strace-rc.XXXXXX")"
    RC_FILE="$rcf" strace -f -o "$part" -e trace=openat,open,statx -- \
      bash -c '"$@"; echo $? >"$RC_FILE"' hypa-client "$HYPA" "$@" >"$outf" &
    strace_pid=$!
    while [[ ! -s "$rcf" && "$waited" -lt 600 ]]; do
      sleep 0.1
      waited=$((waited + 1))
    done
    kill -TERM "$strace_pid" 2>/dev/null || true
    waited=0
    while kill -0 "$strace_pid" 2>/dev/null && [[ "$waited" -lt 30 ]]; do
      sleep 0.1
      waited=$((waited + 1))
    done
    # A tracer that dies detaches its tracees. They keep running.
    kill -KILL "$strace_pid" 2>/dev/null || true
    wait "$strace_pid" 2>/dev/null || true
    if [[ -s "$rcf" ]]; then
      st="$(cat "$rcf")"
    else
      echo "ERROR: hypa did not finish within 60 s under strace" >&2
      st=124
    fi
    cat "$outf"
    cat "$part" >>"$TRACE"
    rm -f "$part" "$outf" "$rcf"
    return "$st"
  fi
  "$HYPA" "$@"
}

echo "==> compression: hypa -c echo hello-f1"
ECHO_OUT="$(run_hypa -c "echo hello-f1")"
echo "${ECHO_OUT}"
if ! grep -q "hello-f1" <<<"${ECHO_OUT}"; then
  echo "ERROR: installed hypa -c did not print hello-f1" >&2
  exit 1
fi

SESSION="h11-f1-$$"
export HYPA_SESSION="$SESSION"

json_get() {
  local json="$1"
  local expr="$2"
  if command -v python3 >/dev/null 2>&1; then
    python3 -c "
import json, sys
obj = json.loads(sys.argv[1])
path = sys.argv[2].split('.')
cur = obj
for key in path:
    if isinstance(cur, dict):
        cur = cur.get(key)
    else:
        cur = None
        break
if cur is None:
    sys.exit(1)
if isinstance(cur, bool):
    print('true' if cur else 'false')
else:
    print(cur)
" "$json" "$expr"
  else
    echo ""
    return 1
  fi
}

echo "==> attach --once session=${SESSION}"
ATTACH_OUT="$(run_hypa attach --once --session "${SESSION}" --cwd "${PWD}")"
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

echo "==> ping"
PING_OUT="$(run_hypa --session "${SESSION}" ping)"
echo "${PING_OUT}"
if ! grep -qE '"ok"[[:space:]]*:[[:space:]]*true' <<<"${PING_OUT}"; then
  echo "ERROR: ping missing ok:true" >&2
  exit 1
fi

echo "==> rpc runtime.health"
HEALTH_OUT="$(run_hypa --session "${SESSION}" rpc runtime.health)"
echo "${HEALTH_OUT}"

pty_provider=""
pty_interactive=""
vt_provider=""
if pty_provider="$(json_get "$HEALTH_OUT" "pty.provider" 2>/dev/null)"; then
  pty_interactive="$(json_get "$HEALTH_OUT" "pty.interactive" 2>/dev/null || true)"
  vt_provider="$(json_get "$HEALTH_OUT" "vt.provider" 2>/dev/null || true)"
else
  if grep -qE '"provider"[[:space:]]*:[[:space:]]*"hypa-pty-host"' <<<"${HEALTH_OUT}"; then
    pty_provider="hypa-pty-host"
  fi
  if grep -qE '"interactive"[[:space:]]*:[[:space:]]*true' <<<"${HEALTH_OUT}"; then
    pty_interactive="true"
  fi
  if grep -qE '"provider"[[:space:]]*:[[:space:]]*"ghostty"' <<<"${HEALTH_OUT}"; then
    vt_provider="ghostty"
  fi
  if grep -qE '"provider"[[:space:]]*:[[:space:]]*"process-io"' <<<"${HEALTH_OUT}"; then
    pty_provider="process-io"
  fi
  if grep -qE '"provider"[[:space:]]*:[[:space:]]*"ghostty"' <<<"${HEALTH_OUT}"; then
    vt_provider="ghostty"
  fi
fi

if [[ "$pty_provider" == "process-io" ]]; then
  echo "ERROR: shipped default must not be process-io (pty.provider=$pty_provider)" >&2
  exit 1
fi
if [[ "$pty_provider" != "hypa-pty-host" ]]; then
  echo "ERROR: pty.provider must be hypa-pty-host (got '$pty_provider')" >&2
  exit 1
fi
if [[ "$pty_interactive" != "true" ]]; then
  echo "ERROR: pty.interactive must be true (got '$pty_interactive')" >&2
  exit 1
fi
if [[ "$vt_provider" != "ghostty" ]]; then
  echo "ERROR: vt.provider must be ghostty (got '$vt_provider')" >&2
  exit 1
fi

echo "==> workspace create --no-pane"
WS_OUT="$(run_hypa --session "${SESSION}" workspace create --cwd "${PWD}" --no-pane --label f1-smoke)"
echo "${WS_OUT}"
WS_ID=""
if WS_ID="$(json_get "$WS_OUT" "workspace_id" 2>/dev/null)" && [[ -n "$WS_ID" ]]; then
  :
elif WS_ID="$(printf '%s' "$WS_OUT" | sed -n 's/.*"workspace_id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)" \
  && [[ -n "$WS_ID" ]]; then
  :
else
  echo "ERROR: workspace create did not print workspace_id" >&2
  exit 1
fi

echo "==> pane create --command /bin/echo --args hello-f1"
PANE_OUT="$(run_hypa --session "${SESSION}" pane create --workspace "${WS_ID}" --command /bin/echo --args hello-f1)"
echo "${PANE_OUT}"

PANE_ID=""
if PANE_ID="$(json_get "$PANE_OUT" "pane_id" 2>/dev/null)" && [[ -n "$PANE_ID" ]]; then
  :
elif PANE_ID="$(printf '%s' "$PANE_OUT" | sed -n 's/.*"pane_id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)" \
  && [[ -n "$PANE_ID" ]]; then
  :
else
  echo "ERROR: pane create did not print pane_id" >&2
  exit 1
fi
echo "    pane_id=$PANE_ID"

echo "==> optional pane.wait_for_output"
run_hypa --session "${SESSION}" rpc pane.wait_for_output \
  "{\"pane_id\":\"${PANE_ID}\",\"timeout_ms\":2000}" >/dev/null 2>&1 || true

echo "==> agent read ${PANE_ID}"
READ_OUT="$(run_hypa --session "${SESSION}" agent read "$PANE_ID")"
echo "${READ_OUT}"
if [[ -z "$READ_OUT" ]]; then
  echo "ERROR: agent read printed no output" >&2
  exit 1
fi
if command -v python3 >/dev/null 2>&1; then
  python3 -c "import json,sys; json.loads(sys.argv[1])" "$READ_OUT" \
    || { echo "ERROR: agent read did not print a JSON object" >&2; exit 1; }
else
  if ! grep -q '{' <<<"${READ_OUT}"; then
    echo "ERROR: agent read did not print a JSON object" >&2
    exit 1
  fi
fi

echo "==> mux stop"
run_hypa mux stop --session "${SESSION}" --socket "${SOCKET_PATH}"
SESSION=""

if [[ "$PROFILE" == "mux-release" ]]; then
  mux_profile_assert_store_sentinels "$SENTINEL_DIR"
  if [[ "$USE_STRACE" -eq 1 ]]; then
    mux_profile_assert_store_trace "$TRACE" "$STATE_DIR"
    echo "    store_read_proof=strace"
    echo "    store_read_limit=strace -f may miss a double-forked child"
  else
    echo "    store_read_proof=sentinel"
  fi
fi

echo "OK: F1 install smoke passed (archive/link, -c, attach, ping, health, pane, agent.read)"
exit 0
