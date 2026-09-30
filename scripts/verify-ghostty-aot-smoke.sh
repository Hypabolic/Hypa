#!/usr/bin/env bash
# Smoke-test AOT hypa or hypa-runtime with Ghostty VT required.
#
# Usage:
#   scripts/verify-ghostty-aot-smoke.sh <path-to-hypa|hypa-runtime> [path-to-cli]
#
# When the first binary is hypa, start with `hypa mux serve --session`.
# Health uses that same hypa unless a second CLI path is given.
#
# Checks:
#   1. libghostty-vt asset present beside the binary (or HYPA_GHOSTTY_VT)
#   2. nm required symbols; undeclared-lib gate (ldd/otool)
#   3. Start server with HYPA_VT_PROVIDER=ghostty HYPA_VT_REQUIRED=ghostty
#   4. Server banner contains vt.provider=ghostty (+ vt.ghostty_version when printed)
#   5. When CLI given: hard-required `rpc runtime.health` reports vt.provider=ghostty
#      plus ghostty_version, abi, and engine capabilities (sgr/alt_screen/wide_char).
#      No soft-pass NOTE path. Engine identity, not probe-only.
#   6. Negative: missing asset + required → non-zero exit
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <path-to-hypa-runtime> [path-to-hypa]" >&2
  exit 2
fi

RUNTIME_BIN="$1"
CLI_BIN="${2:-}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
ABI_MANIFEST="$REPO_ROOT/native/ghostty/abi-manifest.json"

if [[ ! -x "$RUNTIME_BIN" ]]; then
  echo "ERROR: host binary not executable: $RUNTIME_BIN" >&2
  exit 1
fi

RUNTIME_DIR_REAL="$(cd "$(dirname "$RUNTIME_BIN")" && pwd)"
RUNTIME_NAME="$(basename "$RUNTIME_BIN")"
RUNTIME_BIN="$RUNTIME_DIR_REAL/$RUNTIME_NAME"

case "$(uname -s)" in
  Darwin) LIB_NAME="libghostty-vt.dylib" ;;
  Linux)  LIB_NAME="libghostty-vt.so" ;;
  *) echo "ERROR: unsupported OS for Ghostty smoke" >&2; exit 1 ;;
esac

LIB_PATH="${HYPA_GHOSTTY_VT:-$RUNTIME_DIR_REAL/$LIB_NAME}"
if [[ ! -f "$LIB_PATH" ]]; then
  echo "ERROR: missing Ghostty native asset: $LIB_PATH" >&2
  echo "Build with scripts/build-libghostty-vt.sh and republish AgentServer." >&2
  exit 1
fi
echo "Asset present: $LIB_PATH"

# Symbol gate
REQUIRED_SYMBOLS=(ghostty_build_info ghostty_terminal_new ghostty_terminal_free ghostty_terminal_vt_write)
if [[ -f "$ABI_MANIFEST" ]]; then
  # Prefer manifest list: portable grep (no mapfile — macOS bash 3.2).
  MANIFEST_SYMS="$(grep -oE 'ghostty_[a-z0-9_]+' "$ABI_MANIFEST" | sort -u | tr '\n' ' ')"
  if [[ -n "${MANIFEST_SYMS// }" ]]; then
    # shellcheck disable=SC2206
    REQUIRED_SYMBOLS=($MANIFEST_SYMS)
  fi
fi

# Symbol gate is hard: missing nm is a ship-gate failure (no soft skip).
if ! command -v nm >/dev/null 2>&1; then
  echo "ERROR: nm is required for Ghostty ABI symbol gate (install binutils/Xcode CLT)" >&2
  exit 1
fi
NM_OUT="$(nm -gU "$LIB_PATH" 2>/dev/null || nm -D "$LIB_PATH" 2>/dev/null || nm "$LIB_PATH")"
for sym in "${REQUIRED_SYMBOLS[@]}"; do
  if ! grep -q "$sym" <<<"$NM_OUT"; then
    echo "ERROR: ABI mismatch — symbol missing: $sym" >&2
    exit 1
  fi
done
echo "Symbol gate OK"

# Undeclared lib gate — same allowlist policy as scripts/build-libghostty-vt.sh.
# Smoke must independently reject undeclared non-system libraries on published artifacts.
check_undeclared_libs() {
  local lib="$1"
  local line dep
  local -a allowed_re=(
    'libghostty-vt'
    'linux-vdso'
    'ld-linux'
    'ld-musl'
    'libc\.so'
    'libm\.so'
    'libdl\.so'
    'libpthread\.so'
    'librt\.so'
    'libgcc_s'
    'libstdc\+\+'
    'libSystem'
    'libobjc'
    'libiconv'
    'libresolv'
    'libunwind'
    'libcxx'
    'libc\+\+'
    'libcompiler_rt'
    '/usr/lib/'
    '/lib/'
    '/System/Library/'
    '\[vdso\]'
  )
  local deps_raw=""
  local deps_rc=0
  local tool=""
  if [[ "$(uname -s)" == "Darwin" ]]; then
    if ! command -v otool >/dev/null 2>&1; then
      echo "ERROR: otool is required for undeclared-lib gate on Darwin" >&2
      return 1
    fi
    tool="otool"
    deps_raw="$(otool -L "$lib" 2>&1)" && deps_rc=0 || deps_rc=$?
  else
    if ! command -v ldd >/dev/null 2>&1; then
      echo "ERROR: ldd is required for undeclared-lib gate on Linux" >&2
      return 1
    fi
    tool="ldd"
    deps_raw="$(ldd "$lib" 2>&1)" && deps_rc=0 || deps_rc=$?
  fi
  if [[ "$deps_rc" -ne 0 ]]; then
    echo "ERROR: $tool failed (exit=$deps_rc) on $lib" >&2
    printf '%s\n' "$deps_raw" >&2
    return 1
  fi
  if ! printf '%s' "$deps_raw" | grep -q '[^[:space:]]'; then
    echo "ERROR: $tool produced empty dependency dump for $lib" >&2
    return 1
  fi
  echo "$deps_raw"
  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    if [[ "$(uname -s)" == "Darwin" ]]; then
      dep="$(echo "$line" | sed -E 's/^[[:space:]]+//;s/ \(.*//')"
      [[ "$dep" == "$lib" || "$dep" == *libghostty-vt* ]] && continue
      [[ "$dep" == @loader_path/* || "$dep" == @rpath/* ]] && continue
    else
      if [[ "$line" == *"not found"* ]]; then
        echo "ERROR: unresolved dependency: $line" >&2
        return 1
      fi
      dep="$(echo "$line" | awk '{print $1}')"
    fi
    local ok=0
    for re in "${allowed_re[@]}"; do
      if [[ "$dep" =~ $re ]]; then
        ok=1
        break
      fi
    done
    if [[ $ok -eq 0 ]]; then
      echo "ERROR: undeclared runtime dependency: $dep" >&2
      echo "Full dependency dump:" >&2
      echo "$deps_raw" >&2
      return 1
    fi
  done <<<"$deps_raw"
  echo "Undeclared-lib gate OK"
}

check_undeclared_libs "$LIB_PATH"
echo "Dependency dump OK"

# --- Positive: required Ghostty starts and reports health -----------------
# Socket path matches UnixSocketServer default: ~/.config/hypa/runtime/<session>/hypa.sock
SESSION="ci-ghostty-$$"
RUNTIME_DIR="${HOME}/.config/hypa/runtime/${SESSION}"
SOCKET_PATH="${RUNTIME_DIR}/hypa.sock"
STATUS_PATH="${RUNTIME_DIR}/runtime.status.json"
SERVER_PID=""
SERVER_LOG="${TMPDIR:-/tmp}/hypa-ghostty-server-$$.log"

cleanup() {
  if [[ -n "${SERVER_PID}" ]] && kill -0 "${SERVER_PID}" 2>/dev/null; then
    kill "${SERVER_PID}" 2>/dev/null || true
    for _ in 1 2 3 4 5; do
      if ! kill -0 "${SERVER_PID}" 2>/dev/null; then break; fi
      sleep 0.2
    done
    kill -9 "${SERVER_PID}" 2>/dev/null || true
    wait "${SERVER_PID}" 2>/dev/null || true
  fi
  rm -f "${SOCKET_PATH}" "${STATUS_PATH}" 2>/dev/null || true
  rmdir "${RUNTIME_DIR}" 2>/dev/null || true
  rm -f "${SERVER_LOG}" 2>/dev/null || true
}
trap cleanup EXIT

export HYPA_VT_PROVIDER=ghostty
export HYPA_VT_REQUIRED=ghostty
export HYPA_GHOSTTY_VT="$LIB_PATH"

IS_HYPA_PRODUCT=0
case "$RUNTIME_NAME" in
  hypa|hypa.exe) IS_HYPA_PRODUCT=1 ;;
esac
if [[ "$IS_HYPA_PRODUCT" -eq 1 && -z "$CLI_BIN" ]]; then
  CLI_BIN="$RUNTIME_BIN"
fi

echo "==> Start host with Ghostty required (name=$RUNTIME_NAME)"
# Capture stdout/stderr so we can hard-assert the VT provider banner.
if [[ "$IS_HYPA_PRODUCT" -eq 1 ]]; then
  "$RUNTIME_BIN" mux serve --session "${SESSION}" --cwd "${PWD}" >"${SERVER_LOG}" 2>&1 &
else
  "$RUNTIME_BIN" --session "${SESSION}" --cwd "${PWD}" >"${SERVER_LOG}" 2>&1 &
fi
SERVER_PID=$!

READY=0
for _ in $(seq 1 80); do
  if [[ -S "${SOCKET_PATH}" ]] || [[ -f "${STATUS_PATH}" ]]; then
    READY=1
    break
  fi
  if ! kill -0 "${SERVER_PID}" 2>/dev/null; then
    echo "ERROR: host exited before ready (Ghostty load failed?)" >&2
    echo "--- server log ---" >&2
    cat "${SERVER_LOG}" >&2 || true
    wait "${SERVER_PID}" || true
    exit 1
  fi
  sleep 0.1
done
if [[ "${READY}" -ne 1 ]]; then
  echo "ERROR: timed out waiting for runtime socket" >&2
  echo "--- server log ---" >&2
  cat "${SERVER_LOG}" >&2 || true
  exit 1
fi
sleep 0.2

# Belt-and-suspenders: process-up alone is not enough — banner must identify Ghostty.
if ! grep -qE 'vt\.provider=ghostty' "${SERVER_LOG}"; then
  echo "ERROR: expected vt.provider=ghostty in server banner" >&2
  echo "--- server log ---" >&2
  cat "${SERVER_LOG}" >&2 || true
  exit 1
fi
if ! grep -qE 'vt\.ghostty_version=' "${SERVER_LOG}"; then
  echo "ERROR: expected vt.ghostty_version= in server banner" >&2
  echo "--- server log ---" >&2
  cat "${SERVER_LOG}" >&2 || true
  exit 1
fi
BANNER_VERSION="$(sed -n 's/.*vt\.ghostty_version=\([^[:space:]]*\).*/\1/p' "${SERVER_LOG}" | head -n 1)"
BANNER_ABI="$(sed -n 's/.*[[:space:]]abi=\([^[:space:]]*\).*/\1/p' "${SERVER_LOG}" | head -n 1)"
if [[ -z "$BANNER_VERSION" || "$BANNER_VERSION" == "present" ]]; then
  echo "ERROR: banner vt.ghostty_version must be a version id (got '$BANNER_VERSION')" >&2
  echo "--- server log ---" >&2
  cat "${SERVER_LOG}" >&2 || true
  exit 1
fi
case "$BANNER_VERSION" in
  v*|V*) BANNER_VERSION="${BANNER_VERSION:1}" ;;
esac
# Do not use 0.1[.-+]* — bash 3.2 treats [.-+] as an empty range.
if [[ "$BANNER_VERSION" != "0.1" && "$BANNER_VERSION" != 0.1.* && "$BANNER_VERSION" != 0.1-* && "$BANNER_VERSION" != 0.1+* ]]; then
  echo "ERROR: banner vt.ghostty_version must be the pinned 0.1 family (got '$BANNER_VERSION')" >&2
  echo "--- server log ---" >&2
  cat "${SERVER_LOG}" >&2 || true
  exit 1
fi
if [[ "$BANNER_ABI" != "1" ]]; then
  echo "ERROR: banner abi must be 1 (got '$BANNER_ABI')" >&2
  echo "--- server log ---" >&2
  cat "${SERVER_LOG}" >&2 || true
  exit 1
fi
echo "Server banner OK (vt.provider=ghostty + vt.ghostty_version=$BANNER_VERSION abi=$BANNER_ABI)"

if [[ -n "$CLI_BIN" ]]; then
  if [[ ! -x "$CLI_BIN" ]]; then
    echo "ERROR: CLI path provided but not executable: $CLI_BIN" >&2
    exit 1
  fi

  # hypa supports: rpc <method> [params-json]
  # (no 'health' or 'call' subcommands).
  echo "==> Client health via rpc runtime.health"
  set +e
  HEALTH_OUT="$("$CLI_BIN" --session "${SESSION}" rpc runtime.health 2>&1)"
  HEALTH_RC=$?
  set -e
  if [[ "$HEALTH_RC" -ne 0 ]]; then
    echo "ERROR: rpc runtime.health failed (exit=$HEALTH_RC): $HEALTH_OUT" >&2
    exit 1
  fi
  if [[ -z "$HEALTH_OUT" ]]; then
    echo "ERROR: rpc runtime.health returned empty output" >&2
    exit 1
  fi

  # Nested vt object: {"vt":{"provider":"ghostty","ghostty_version":"...","abi":"1",...}}
  if ! grep -qE '"provider"[[:space:]]*:[[:space:]]*"ghostty"' <<<"$HEALTH_OUT"; then
    echo "ERROR: expected vt.provider=ghostty in health RPC: $HEALTH_OUT" >&2
    exit 1
  fi
  HEALTH_VERSION="$(printf '%s' "$HEALTH_OUT" | sed -n 's/.*"ghostty_version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)"
  HEALTH_ABI="$(printf '%s' "$HEALTH_OUT" | sed -n 's/.*"abi"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)"
  if [[ -z "$HEALTH_VERSION" || "$HEALTH_VERSION" == "present" ]]; then
    echo "ERROR: expected Ghostty version id in health RPC (got '$HEALTH_VERSION'): $HEALTH_OUT" >&2
    exit 1
  fi
  case "$HEALTH_VERSION" in
    v*|V*) HEALTH_VERSION="${HEALTH_VERSION:1}" ;;
  esac
  if [[ "$HEALTH_VERSION" != "0.1" && "$HEALTH_VERSION" != 0.1.* && "$HEALTH_VERSION" != 0.1-* && "$HEALTH_VERSION" != 0.1+* ]]; then
    echo "ERROR: health ghostty_version must be the pinned 0.1 family (got '$HEALTH_VERSION'): $HEALTH_OUT" >&2
    exit 1
  fi
  if [[ "$HEALTH_ABI" != "1" ]]; then
    echo "ERROR: health abi must be 1 (got '$HEALTH_ABI'): $HEALTH_OUT" >&2
    exit 1
  fi
  # engine identity (not probe-only) — expanded capabilities when GhosttyVtEngine is wired.
  if ! grep -qE '"sgr"' <<<"$HEALTH_OUT"; then
    echo "ERROR: expected vt.capabilities to include sgr (Ghostty engine wired): $HEALTH_OUT" >&2
    exit 1
  fi
  if ! grep -qE '"alt_screen"' <<<"$HEALTH_OUT"; then
    echo "ERROR: expected vt.capabilities to include alt_screen: $HEALTH_OUT" >&2
    exit 1
  fi
  if ! grep -qE '"wide_char"' <<<"$HEALTH_OUT"; then
    echo "ERROR: expected vt.capabilities to include wide_char: $HEALTH_OUT" >&2
    exit 1
  fi
  echo "Health contract OK (provider=ghostty + ghostty_version + abi + engine caps)"

  echo "==> Client ping"
  PING_OUT="$("$CLI_BIN" --session "${SESSION}" ping)"
  if ! grep -qE '"ok"[[:space:]]*:[[:space:]]*true' <<<"$PING_OUT"; then
    echo "ERROR: ping failed: $PING_OUT" >&2
    exit 1
  fi
else
  echo "NOTE: no CLI provided; banner provider/version check still hard-required (load OK)"
fi

kill "${SERVER_PID}" 2>/dev/null || true
wait "${SERVER_PID}" 2>/dev/null || true
SERVER_PID=""

# --- Negative: missing asset must fail closed -----------------------------
echo "==> Negative: missing asset fail-closed"
NEG_DIR="${TMPDIR:-/tmp}/hypa-ghostty-neg-$$"
mkdir -p "$NEG_DIR"
# Copy only the binary without the lib (when lib is beside binary).
cp "$RUNTIME_BIN" "$NEG_DIR/$RUNTIME_NAME"
chmod +x "$NEG_DIR/$RUNTIME_NAME"
# Force exclusive missing path; do not inherit HYPA_GHOSTTY_VT from positive path.
export HYPA_GHOSTTY_VT="$NEG_DIR/missing-libghostty-vt.dylib"
export HYPA_VT_PROVIDER=ghostty
export HYPA_VT_REQUIRED=ghostty
set +e
if [[ "$IS_HYPA_PRODUCT" -eq 1 ]]; then
  "$NEG_DIR/$RUNTIME_NAME" mux serve --session "neg-$$" --cwd "$PWD" >/dev/null 2>"$NEG_DIR/err.txt"
else
  "$NEG_DIR/$RUNTIME_NAME" --session "neg-$$" --cwd "$PWD" >/dev/null 2>"$NEG_DIR/err.txt"
fi
NEG_RC=$?
set -e
if [[ "$NEG_RC" -eq 0 ]]; then
  echo "ERROR: expected non-zero exit when libghostty-vt is missing and required" >&2
  cat "$NEG_DIR/err.txt" >&2 || true
  rm -rf "$NEG_DIR"
  exit 1
fi
rm -rf "$NEG_DIR"
echo "Fail-closed OK (exit=$NEG_RC)"

echo "OK: Ghostty AOT smoke passed (asset + symbols + banner + health + fail-closed)"
