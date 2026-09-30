#!/usr/bin/env bash
# F2 install execute smoke from an F2 archive or a linked hypa.
#
# Usage:
#   scripts/verify-f2-install-smoke.sh --archive artifacts/dist-f2/hypa-f2-$RID.tar.gz --rid $RID
#   scripts/verify-f2-install-smoke.sh --hypa /path/to/hypa --rid $RID
#   scripts/verify-f2-install-smoke.sh --self-test-isolation
#   scripts/verify-f2-install-smoke.sh --self-test-version-id
#   scripts/verify-f2-install-smoke.sh --self-test-undeclared-libs
#   scripts/verify-f2-install-smoke.sh --self-test-store-proof PATH
#   scripts/verify-f2-install-smoke.sh --self-test-store-mutate
#
# Do not export HYPA_PTY_PROVIDER=process-io.
# Inventory uses verify-f1-pack-smoke.sh --channel f2 (Ghostty required).
# Do not export HYPA_VT_PROVIDER or HYPA_VT_REQUIRED. Ghostty must come from
# hypa.channel token f2. Bare hypa without --once waits for Ctrl+C.
# This smoke uses attach --once.

set -euo pipefail

ARCHIVE=""
HYPA=""
RID=""
PROFILE="mux-release"
REQUIRE_STORE_TRACE=0
SELF_TEST_ISOLATION=0
SELF_TEST_VERSION_ID=0
SELF_TEST_UNDECLARED=0
SELF_TEST_STORE_PROOF=""
SELF_TEST_STORE_MUTATE=0

usage() {
  echo "Usage: $0 [--archive PATH] [--hypa PATH] [--rid RID] [--profile mux-release|full] [--require-store-trace] [--self-test-isolation] [--self-test-version-id] [--self-test-undeclared-libs] [--self-test-store-proof PATH] [--self-test-store-mutate]" >&2
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
    --self-test-version-id)
      SELF_TEST_VERSION_ID=1
      shift
      ;;
    --self-test-undeclared-libs)
      SELF_TEST_UNDECLARED=1
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

if [[ "$SELF_TEST_ISOLATION" -ne 1 && "$SELF_TEST_VERSION_ID" -ne 1 && "$SELF_TEST_UNDECLARED" -ne 1 && -z "$SELF_TEST_STORE_PROOF" && "$SELF_TEST_STORE_MUTATE" -ne 1 && -z "$ARCHIVE" && -z "$HYPA" ]]; then
  echo "ERROR: pass --archive, --hypa, or a --self-test-* flag" >&2
  usage
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PACK_SMOKE="$REPO_ROOT/scripts/verify-f1-pack-smoke.sh"
INSTALL_SH="$REPO_ROOT/install.sh"
RC_ENGINE="$REPO_ROOT/scripts/verify-ghostty-rc-engine.sh"
ABI_MANIFEST="$REPO_ROOT/native/ghostty/abi-manifest.json"
# shellcheck source=lib-mux-release-profile.sh
source "$SCRIPT_DIR/lib-mux-release-profile.sh"
mux_profile_assert_known "$PROFILE" || exit $?

# Fail if otool/ldd exits non-zero or the dump is empty. Empty dumps skip the
# allow-list and must not print OK.
require_deps_dump() {
  local rc="$1"
  local raw="$2"
  local tool="$3"
  if [[ "$rc" -ne 0 ]]; then
    echo "ERROR: $tool failed (exit=$rc)" >&2
    printf '%s\n' "$raw" >&2
    return 1
  fi
  if ! printf '%s' "$raw" | grep -q '[^[:space:]]'; then
    echo "ERROR: $tool produced empty dependency dump" >&2
    return 1
  fi
  return 0
}

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
  require_deps_dump "$deps_rc" "$deps_raw" "$tool" || return 1
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

check_ghostty_symbols() {
  local lib="$1"
  local -a required_symbols=(ghostty_build_info ghostty_terminal_new ghostty_terminal_free ghostty_terminal_vt_write)
  if [[ -f "$ABI_MANIFEST" ]]; then
    local manifest_syms
    manifest_syms="$(grep -oE 'ghostty_[a-z0-9_]+' "$ABI_MANIFEST" | sort -u | tr '\n' ' ')"
    if [[ -n "${manifest_syms// }" ]]; then
      # shellcheck disable=SC2206
      required_symbols=($manifest_syms)
    fi
  fi
  if ! command -v nm >/dev/null 2>&1; then
    echo "ERROR: nm is required for Ghostty ABI symbol gate (install binutils/Xcode CLT)" >&2
    return 1
  fi
  local nm_out
  nm_out="$(nm -gU "$lib" 2>/dev/null || nm -D "$lib" 2>/dev/null || nm "$lib")"
  local sym
  for sym in "${required_symbols[@]}"; do
    if ! grep -q "$sym" <<<"$nm_out"; then
      echo "ERROR: ABI mismatch — symbol missing: $sym" >&2
      return 1
    fi
  done
  echo "Symbol gate OK"
}

# Same family as GhosttyLibraryLoader.MatchesPinnedAbi (0.1, 0.1.0, 0.1.0-dev).
# Reject empty, "present", 0.10.0, and other product families.
# Do not use 0.1[.-+]* — bash 3.2 treats [.-+] as an empty range and
# rejects the live 0.1.0-dev identity.
matches_pinned_ghostty_version() {
  local s="${1:-}"
  if [[ -z "$s" || "$s" == "present" ]]; then
    return 1
  fi
  case "$s" in
    v*|V*) s="${s:1}" ;;
  esac
  if [[ "$s" == "0.1" || "$s" == 0.1.* || "$s" == 0.1-* || "$s" == 0.1+* ]]; then
    return 0
  fi
  return 1
}

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

extract_json_string() {
  local json="$1"
  local path="$2"
  local key="$3"
  local value=""
  if value="$(json_get "$json" "$path" 2>/dev/null)" && [[ -n "$value" ]]; then
    printf '%s' "$value"
    return 0
  fi
  printf '%s' "$json" | sed -n "s/.*\"${key}\"[[:space:]]*:[[:space:]]*\"\\([^\"]*\\)\".*/\\1/p" | head -n 1
}

assert_ghostty_identity() {
  local health="$1"
  local ghostty_version=""
  local vt_abi=""
  ghostty_version="$(extract_json_string "$health" "vt.ghostty_version" "ghostty_version")"
  vt_abi="$(extract_json_string "$health" "vt.abi" "abi")"
  if [[ -z "$ghostty_version" ]]; then
    echo "ERROR: vt.ghostty_version must be non-empty" >&2
    return 1
  fi
  if [[ "$ghostty_version" == "present" ]]; then
    echo "ERROR: vt.ghostty_version stub 'present' is not a version id" >&2
    return 1
  fi
  if ! matches_pinned_ghostty_version "$ghostty_version"; then
    echo "ERROR: vt.ghostty_version must be the pinned 0.1 family (got '$ghostty_version')" >&2
    return 1
  fi
  if [[ -z "$vt_abi" ]]; then
    echo "ERROR: vt.abi must be non-empty" >&2
    return 1
  fi
  if [[ "$vt_abi" == "present" ]]; then
    echo "ERROR: vt.abi stub 'present' is not an ABI id" >&2
    return 1
  fi
  if [[ "$vt_abi" != "1" ]]; then
    echo "ERROR: vt.abi must be 1 (got '$vt_abi')" >&2
    return 1
  fi
  echo "Ghostty identity OK (ghostty_version=$ghostty_version abi=$vt_abi)"
}

expect_identity_fail() {
  local label="$1"
  local health="$2"
  local rc=0
  set +e
  assert_ghostty_identity "$health" >/dev/null
  rc=$?
  set -e
  if [[ "$rc" -eq 0 ]]; then
    echo "ERROR: $label must fail" >&2
    return 1
  fi
  echo "    fail-closed: $label"
}

run_version_id_fixtures() {
  echo "==> F2 version-id fixtures"
  local rc
  expect_identity_fail "missing ghostty_version" '{"vt":{"provider":"ghostty","abi":"1"}}'
  expect_identity_fail "empty ghostty_version" '{"vt":{"provider":"ghostty","ghostty_version":"","abi":"1"}}'
  expect_identity_fail "stub ghostty_version present" '{"vt":{"provider":"ghostty","ghostty_version":"present","abi":"1"}}'
  expect_identity_fail "wrong family 0.10.0" '{"vt":{"provider":"ghostty","ghostty_version":"0.10.0","abi":"1"}}'
  expect_identity_fail "stub abi present" '{"vt":{"provider":"ghostty","ghostty_version":"0.1.0-dev","abi":"present"}}'
  expect_identity_fail "wrong abi" '{"vt":{"provider":"ghostty","ghostty_version":"0.1.0-dev","abi":"2"}}'
  for sample in "0.1" "0.1.0" "0.1.0-dev" "v0.1.0-dev"; do
    set +e
    matches_pinned_ghostty_version "$sample"
    rc=$?
    set -e
    if [[ "$rc" -ne 0 ]]; then
      echo "ERROR: pinned family sample '$sample' must pass" >&2
      return 1
    fi
  done
  set +e
  assert_ghostty_identity '{"vt":{"provider":"ghostty","ghostty_version":"0.1.0-dev","abi":"1"}}' >/dev/null
  rc=$?
  set -e
  if [[ "$rc" -ne 0 ]]; then
    echo "ERROR: pinned 0.1.0-dev + abi 1 must pass" >&2
    return 1
  fi
  echo "OK: F2 version-id fixtures"
}

run_undeclared_lib_fixtures() {
  echo "==> F2 undeclared-lib fixtures"
  local rc=0
  set +e
  require_deps_dump 1 "libSystem.B.dylib" "otool" >/dev/null
  rc=$?
  set -e
  if [[ "$rc" -eq 0 ]]; then
    echo "ERROR: non-zero otool/ldd must fail" >&2
    return 1
  fi
  echo "    fail-closed: non-zero otool/ldd"
  set +e
  require_deps_dump 0 "" "otool" >/dev/null
  rc=$?
  set -e
  if [[ "$rc" -eq 0 ]]; then
    echo "ERROR: empty dependency dump must fail" >&2
    return 1
  fi
  echo "    fail-closed: empty dependency dump"
  set +e
  require_deps_dump 0 "   	  " "ldd" >/dev/null
  rc=$?
  set -e
  if [[ "$rc" -eq 0 ]]; then
    echo "ERROR: whitespace-only dependency dump must fail" >&2
    return 1
  fi
  echo "    fail-closed: whitespace-only dependency dump"
  set +e
  check_undeclared_libs "/no/such/libghostty-vt.dylib" >/dev/null
  rc=$?
  set -e
  if [[ "$rc" -eq 0 ]]; then
    echo "ERROR: missing lib must fail undeclared-lib gate" >&2
    return 1
  fi
  echo "    fail-closed: missing lib otool/ldd"
  echo "OK: F2 undeclared-lib fixtures"
}

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

PREFIX="$(mktemp -d "${TMPDIR:-/tmp}/hypa-f2-install-smoke.XXXXXX")"
HOME_DIR="$PREFIX/home"
STATE_DIR="$PREFIX/state"
SOCKET_PATH="$STATE_DIR/hypa.sock"
mkdir -p "$HOME_DIR" "$STATE_DIR"

unset HYPA_RUNTIME_SOCKET || true
unset HYPA_RUNTIME_STATE_DIR || true
unset HYPA_PTY_PROVIDER || true
unset HYPA_VT_PROVIDER || true
unset HYPA_VT_REQUIRED || true
export HOME="$HOME_DIR"
export HYPA_RUNTIME_STATE_DIR="$STATE_DIR"
export HYPA_RUNTIME_SOCKET="$SOCKET_PATH"
mux_profile_export_isolation "$PREFIX"

cleanup() {
  if [[ -n "${HYPA:-}" ]]; then
    "$HYPA" mux stop >/dev/null 2>&1 || true
  fi
  rm -rf "$PREFIX"
}
trap cleanup EXIT

if [[ "$SELF_TEST_ISOLATION" -eq 1 ]]; then
  echo "==> F2 install smoke isolation self-test"
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
  echo "OK: F2 install smoke isolation"
  exit 0
fi

SENTINEL_DIR="$PREFIX/store-sentinels"
TRACE="$PREFIX/store.trace"
USE_STRACE=0

if [[ "$PROFILE" == "mux-release" && ( -n "$SELF_TEST_STORE_PROOF" || "$SELF_TEST_STORE_MUTATE" -eq 1 ) ]]; then
  mux_profile_seed_store_sentinels "$SENTINEL_DIR"
  if [[ "$SELF_TEST_STORE_MUTATE" -eq 1 ]]; then
    printf 'mutated\n' >"${XDG_STATE_HOME}/hypa/placements/extra.json"
  fi
  if [[ -n "$SELF_TEST_STORE_PROOF" ]]; then
    mux_profile_assert_store_sentinels "$SENTINEL_DIR"
    mux_profile_prepare_self_test_trace "$SELF_TEST_STORE_PROOF" "$TRACE" "$STATE_DIR" "$SOCKET_PATH"
    mux_profile_assert_store_trace "$TRACE" "$STATE_DIR"
    echo "    store_read_proof=strace"
    echo "OK: F2 install smoke store proof"
    exit 0
  fi
  mux_profile_assert_store_sentinels "$SENTINEL_DIR"
  echo "OK: F2 install smoke store mutate"
  exit 0
fi

if [[ "$SELF_TEST_VERSION_ID" -eq 1 ]]; then
  run_version_id_fixtures
  exit 0
fi

if [[ "$SELF_TEST_UNDECLARED" -eq 1 ]]; then
  run_undeclared_lib_fixtures
  exit 0
fi

if [[ "$PROFILE" == "mux-release" ]]; then
  mux_profile_seed_store_sentinels "$SENTINEL_DIR"
fi

echo "==> F2 install execute smoke"
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
  echo "==> inventory (verify-f1-pack-smoke --channel f2)"
  bash "$PACK_SMOKE" --rid "$RID" --channel f2 --profile "$PROFILE" "$ARCHIVE"

  if [[ "$is_windows" -eq 1 ]]; then
    echo "ERROR: F2 channel is Unix only" >&2
    exit 1
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
    HYPA_CHANNEL=f2 \
    HOME="$HOME_DIR" \
    sh "$INSTALL_SH" --from-archive "$ARCHIVE" --channel f2

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
  case "$RID" in
    linux-*)
      if [[ ! -f "$APP_DIR/libghostty-vt.so" ]]; then
        echo "ERROR: libghostty-vt.so missing under HYPA_APP_DIR" >&2
        exit 1
      fi
      if [[ -f "$APP_DIR/libghostty-vt.dylib" ]]; then
        echo "ERROR: F2 linux install must not include libghostty-vt.dylib" >&2
        exit 1
      fi
      ;;
    osx-*)
      if [[ ! -f "$APP_DIR/libghostty-vt.dylib" ]]; then
        echo "ERROR: libghostty-vt.dylib missing under HYPA_APP_DIR" >&2
        exit 1
      fi
      if [[ -f "$APP_DIR/libghostty-vt.so" ]]; then
        echo "ERROR: F2 osx install must not include libghostty-vt.so" >&2
        exit 1
      fi
      ;;
    *)
      echo "ERROR: F2 channel is Unix only (got '$RID')" >&2
      exit 1
      ;;
  esac
  if [[ ! -f "$APP_DIR/NOTICE" ]]; then
    echo "ERROR: NOTICE missing under HYPA_APP_DIR" >&2
    exit 1
  fi
  if [[ ! -f "$APP_DIR/PIN.md" ]]; then
    echo "ERROR: PIN.md missing under HYPA_APP_DIR" >&2
    exit 1
  fi
  if [[ ! -f "$APP_DIR/hypa.channel" ]]; then
    echo "ERROR: hypa.channel missing under HYPA_APP_DIR" >&2
    exit 1
  fi
  CHANNEL_TOKEN="$(tr -d '[:space:]' <"$APP_DIR/hypa.channel")"
  if [[ "$CHANNEL_TOKEN" != "f2" ]]; then
    echo "ERROR: hypa.channel must be the token f2 (got '$CHANNEL_TOKEN')" >&2
    exit 1
  fi
  echo "    hypa=$HYPA"
  echo "    app=$APP_DIR"
  echo "    channel=$CHANNEL_TOKEN"
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
  echo "ERROR: F2 channel is Unix only" >&2
  exit 1
fi

mkdir -p "$HOME"
unset HYPA_PTY_PROVIDER || true
unset HYPA_VT_PROVIDER || true
unset HYPA_VT_REQUIRED || true

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

echo "==> compression: hypa -c echo hello-f2"
ECHO_OUT="$(run_hypa -c "echo hello-f2")"
echo "${ECHO_OUT}"
if ! grep -q "hello-f2" <<<"${ECHO_OUT}"; then
  echo "ERROR: installed hypa -c did not print hello-f2" >&2
  exit 1
fi

LIB_PATH=""
LIB_DIR="${APP_DIR:-}"
if [[ -z "$LIB_DIR" ]]; then
  LIB_DIR="$(cd "$(dirname "$HYPA")" && pwd)"
fi
case "$RID" in
  osx-*)
    if [[ -f "$LIB_DIR/libghostty-vt.dylib" ]]; then
      LIB_PATH="$LIB_DIR/libghostty-vt.dylib"
    fi
    ;;
  linux-*)
    if [[ -f "$LIB_DIR/libghostty-vt.so" ]]; then
      LIB_PATH="$LIB_DIR/libghostty-vt.so"
    fi
    ;;
esac
if [[ -z "$LIB_PATH" ]]; then
  echo "ERROR: libghostty-vt not found beside installed hypa" >&2
  exit 1
fi

# shellcheck source=lib-ghostty-rid-arch.sh
source "$SCRIPT_DIR/lib-ghostty-rid-arch.sh"
assert_ghostty_rid_arch "$LIB_PATH" "$RID"

echo "==> Ghostty symbol gate on $LIB_PATH"
check_ghostty_symbols "$LIB_PATH"

echo "==> undeclared-lib gate on $LIB_PATH"
check_undeclared_libs "$LIB_PATH"

RC_HYPA="$HYPA"
if [[ -n "${APP_DIR:-}" && -x "$APP_DIR/hypa" ]]; then
  RC_HYPA="$APP_DIR/hypa"
fi
if [[ ! -f "$RC_ENGINE" ]]; then
  echo "ERROR: missing $RC_ENGINE" >&2
  exit 1
fi
echo "==> Ghostty AOT/RC smoke on $RC_HYPA"
# RC smoke waits on HOME/.config/hypa/runtime/<session>.
# Use a short /tmp home so the Unix socket stays under the 104-char macOS cap.
# Do not leak the install-smoke socket override into that child.
RC_HOME="$(mktemp -d /tmp/h11f2.XXXXXX)"
env -u HYPA_RUNTIME_SOCKET -u HYPA_RUNTIME_STATE_DIR \
  -u HYPA_VT_PROVIDER -u HYPA_VT_REQUIRED -u HYPA_GHOSTTY_VT \
  -u XDG_STATE_HOME -u XDG_DATA_HOME -u XDG_CONFIG_HOME -u HYPA_OPERATOR_HOME \
  HOME="$RC_HOME" \
  bash "$RC_ENGINE" "$RC_HYPA"
rm -rf "$RC_HOME"
unset HYPA_VT_PROVIDER || true
unset HYPA_VT_REQUIRED || true
unset HYPA_GHOSTTY_VT || true
export HYPA_RUNTIME_STATE_DIR="$STATE_DIR"
export HYPA_RUNTIME_SOCKET="$SOCKET_PATH"

SESSION="h11-f2-$$"
export HYPA_SESSION="$SESSION"

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

echo "==> rpc runtime.health"
HEALTH_OUT="$(run_hypa --session "${SESSION}" rpc runtime.health)"
echo "${HEALTH_OUT}"

pty_provider=""
pty_interactive=""
vt_provider=""
ghostty_version=""
vt_abi=""
if pty_provider="$(json_get "$HEALTH_OUT" "pty.provider" 2>/dev/null)"; then
  pty_interactive="$(json_get "$HEALTH_OUT" "pty.interactive" 2>/dev/null || true)"
  vt_provider="$(json_get "$HEALTH_OUT" "vt.provider" 2>/dev/null || true)"
  ghostty_version="$(extract_json_string "$HEALTH_OUT" "vt.ghostty_version" "ghostty_version")"
  vt_abi="$(extract_json_string "$HEALTH_OUT" "vt.abi" "abi")"
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
  ghostty_version="$(extract_json_string "$HEALTH_OUT" "vt.ghostty_version" "ghostty_version")"
  vt_abi="$(extract_json_string "$HEALTH_OUT" "vt.abi" "abi")"
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
assert_ghostty_identity "$HEALTH_OUT"
if ! grep -qE '"sgr"' <<<"${HEALTH_OUT}"; then
  echo "ERROR: expected vt.capabilities to include sgr: $HEALTH_OUT" >&2
  exit 1
fi
if ! grep -qE '"alt_screen"' <<<"${HEALTH_OUT}"; then
  echo "ERROR: expected vt.capabilities to include alt_screen: $HEALTH_OUT" >&2
  exit 1
fi
if ! grep -qE '"wide_char"' <<<"${HEALTH_OUT}"; then
  echo "ERROR: expected vt.capabilities to include wide_char: $HEALTH_OUT" >&2
  exit 1
fi

echo "==> workspace create --no-pane"
WS_OUT="$(run_hypa --session "${SESSION}" workspace create --cwd "${PWD}" --no-pane --label f2-smoke)"
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

echo "==> pane create --command /bin/echo --args hello-f2"
PANE_OUT="$(run_hypa --session "${SESSION}" pane create --workspace "${WS_ID}" --command /bin/echo --args hello-f2)"
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

echo "==> mux stop"
run_hypa mux stop
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

echo "OK: F2 install smoke passed (archive/link, -c, Ghostty 0.1 family + abi 1, undeclared-lib, RC AOT, pane, agent.read)"
exit 0
