#!/usr/bin/env bash
# Clean-install check for an osx-x64 or osx-arm64 F1 archive.
#
# Usage:
#   scripts/verify-clean-install-macos.sh --archive PATH
#
# Installs into an isolated HOME and XDG prefix. Starts mux on a session
# name that is not default. Stops that session with
# hypa mux stop --session NAME. Does not signal other processes.

set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "ERROR: this check runs on macOS" >&2
  exit 1
fi

ARCHIVE=""

usage() {
  echo "Usage: $0 --archive PATH" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --archive)
      [[ $# -ge 2 ]] || usage
      ARCHIVE="$2"
      shift 2
      ;;
    -h|--help)
      usage
      ;;
    *)
      echo "ERROR: unknown option: $1" >&2
      usage
      ;;
  esac
done

if [[ -z "$ARCHIVE" || ! -f "$ARCHIVE" ]]; then
  echo "ERROR: --archive must be a file" >&2
  usage
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARCHIVE="$(cd "$(dirname "$ARCHIVE")" && pwd)/$(basename "$ARCHIVE")"
INSTALL_SH="$ROOT/install.sh"
if [[ ! -f "$INSTALL_SH" ]]; then
  echo "ERROR: install.sh is missing: $INSTALL_SH" >&2
  exit 1
fi

REAL_HOME="${HOME}"
# macOS Unix sockets accept at most 104 bytes. TMPDIR under
# /var/folders is already too long once the session path is added.
# /tmp resolves to /private/tmp and stays inside the limit.
PREFIX="$(mktemp -d /tmp/hypa-ci.XXXXXX)"
HOME_DIR="$PREFIX/home"
INSTALL_DIR="$PREFIX/bin"
APP_DIR="$PREFIX/share/hypa"
SESSION="ci$$"

if [[ "$SESSION" == "default" ]]; then
  echo "ERROR: refusing to use the default mux session" >&2
  exit 1
fi

export HOME="$HOME_DIR"
export XDG_CONFIG_HOME="$PREFIX/xdg-config"
export XDG_DATA_HOME="$PREFIX/xdg-data"
export XDG_STATE_HOME="$PREFIX/xdg-state"
unset HYPA_RUNTIME_SOCKET || true
unset HYPA_RUNTIME_STATE_DIR || true
mkdir -p "$HOME_DIR" "$INSTALL_DIR" "$APP_DIR" \
  "$XDG_CONFIG_HOME" "$XDG_DATA_HOME" "$XDG_STATE_HOME"
sock_preview="$(cd "$HOME_DIR" && pwd -P)/.config/hypa/runtime/$SESSION/hypa.sock"
if [[ ${#sock_preview} -gt 103 ]]; then
  echo "FAIL: mux socket path is ${#sock_preview} bytes. The limit is 104 including NUL." >&2
  printf '%s\n' "$sock_preview" >&2
  exit 1
fi

HYPA=""
MUX_PID=""

cleanup() {
  if [[ -n "$HYPA" && -e "$HYPA" && "$SESSION" != "default" ]]; then
    "$HYPA" mux stop --session "$SESSION" >/dev/null 2>&1 || true
  fi
  if [[ -n "$MUX_PID" ]]; then
    local i=0
    while [[ "$i" -lt 10 ]]; do
      if ! kill -0 "$MUX_PID" 2>/dev/null; then
        break
      fi
      i=$((i + 1))
      sleep 1
    done
    wait "$MUX_PID" 2>/dev/null || true
  fi
  rm -rf "$PREFIX"
}
trap cleanup EXIT

echo "==> isolated prefix"
echo "    prefix=$PREFIX"
echo "    home=$HOME"
echo "    session=$SESSION"

QUARANTINE_VALUE="0083;$(date +%s);hypa-clean-install;"
QUARANTINED="$PREFIX/pack.tar.gz"
cp "$ARCHIVE" "$QUARANTINED"
xattr -w com.apple.quarantine "$QUARANTINE_VALUE" "$QUARANTINED"
got_q="$(xattr -p com.apple.quarantine "$QUARANTINED" | tr -d '\n')"
if [[ "$got_q" != "$QUARANTINE_VALUE" ]]; then
  echo "FAIL: quarantine was not set on the archive" >&2
  printf 'want %s\n got %s\n' "$QUARANTINE_VALUE" "$got_q" >&2
  exit 1
fi

echo "==> install.sh"
HYPA_ARCHIVE="$QUARANTINED" \
  HYPA_INSTALL_DIR="$INSTALL_DIR" \
  HYPA_APP_DIR="$APP_DIR" \
  HOME="$HOME_DIR" \
  sh "$INSTALL_SH" --from-archive "$QUARANTINED"

HYPA="$INSTALL_DIR/hypa"
if [[ ! -e "$HYPA" || ! -f "$APP_DIR/hypa" ]]; then
  echo "FAIL: installer did not place hypa" >&2
  exit 1
fi

installed_q="$(xattr -p com.apple.quarantine "$APP_DIR" 2>/dev/null | tr -d '\n' || true)"
if [[ "$installed_q" != "$QUARANTINE_VALUE" ]]; then
  echo "FAIL: install did not record quarantine on $APP_DIR" >&2
  printf 'want %s\n got %s\n' "$QUARANTINE_VALUE" "$installed_q" >&2
  exit 1
fi
echo "quarantine recorded on $APP_DIR"

echo "==> hypa --version"
"$HYPA" --version

if [[ ! -f "$APP_DIR/libmsquic.dylib" ]]; then
  echo "FAIL: bundled libmsquic.dylib is missing from $APP_DIR" >&2
  exit 1
fi
if [[ ! -f "$APP_DIR/libcrypto.3.dylib" ]]; then
  echo "FAIL: bundled libcrypto.3.dylib is missing from $APP_DIR" >&2
  exit 1
fi

resolve_dep() {
  local bin="$1"
  local dep="$2"
  local app="$3"
  local dir rest rpath candidate found
  dir="$(cd "$(dirname "$bin")" && pwd)"
  case "$dep" in
    @loader_path/*)
      printf '%s\n' "$dir/${dep#@loader_path/}"
      ;;
    @executable_path/*)
      printf '%s\n' "$app/${dep#@executable_path/}"
      ;;
    @rpath/*)
      rest="${dep#@rpath/}"
      found=""
      while IFS= read -r rpath; do
        [[ -z "$rpath" ]] && continue
        rpath="${rpath/@loader_path/$dir}"
        rpath="${rpath/@executable_path/$app}"
        candidate="$rpath/$rest"
        if [[ -e "$candidate" ]]; then
          found="$candidate"
          break
        fi
      done < <(otool -l "$bin" | awk '/cmd LC_RPATH/{p=1} p && /path /{print $2; p=0}')
      if [[ -z "$found" && -e "$dir/$rest" ]]; then
        found="$dir/$rest"
      fi
      if [[ -z "$found" && -e "$app/$rest" ]]; then
        found="$app/$rest"
      fi
      if [[ -z "$found" ]]; then
        printf 'MISSING:%s\n' "$dep"
      else
        printf '%s\n' "$found"
      fi
      ;;
    /*)
      printf '%s\n' "$dep"
      ;;
    *)
      if [[ -e "$dir/$dep" ]]; then
        printf '%s\n' "$dir/$dep"
      else
        printf '%s\n' "$app/$dep"
      fi
      ;;
  esac
}

echo "==> otool -L"
missing_deps=0
while IFS= read -r -d '' macho; do
  if ! file -b "$macho" | grep -q 'Mach-O'; then
    continue
  fi
  own_id="$(otool -D "$macho" 2>/dev/null | awk 'NR==2 { print $1 }')"
  while IFS= read -r line; do
    if [[ "$line" != *"(compatibility version"* ]]; then
      continue
    fi
    dep="${line%% (*}"
    dep="${dep#"${dep%%[![:space:]]*}"}"
    dep="${dep%"${dep##*[![:space:]]}"}"
    [[ -z "$dep" ]] && continue
    if [[ -n "$own_id" && "$dep" == "$own_id" ]]; then
      continue
    fi
    resolved="$(resolve_dep "$macho" "$dep" "$APP_DIR")"
    # /usr/lib and /System live in the dyld shared cache. The path
    # does not have to be a file on disk.
    case "$resolved" in
      /usr/lib/*|/System/*) continue ;;
    esac
    if [[ "$resolved" == MISSING:* || ! -e "$resolved" ]]; then
      echo "FAIL: missing dependency $dep for $macho" >&2
      printf 'resolved %s\n' "$resolved" >&2
      missing_deps=1
    fi
  done < <(otool -L "$macho")
done < <(find "$APP_DIR" -type f -print0)
if [[ "$missing_deps" -ne 0 ]]; then
  exit 1
fi

if ! otool -L "$APP_DIR/libmsquic.dylib" | grep -F 'libcrypto.3.dylib' >/dev/null; then
  echo "FAIL: libmsquic.dylib does not link libcrypto.3.dylib" >&2
  otool -L "$APP_DIR/libmsquic.dylib" >&2 || true
  exit 1
fi
crypto_dep="$(otool -L "$APP_DIR/libmsquic.dylib" | awk '/libcrypto/{print $1; exit}')"
crypto_resolved="$(resolve_dep "$APP_DIR/libmsquic.dylib" "$crypto_dep" "$APP_DIR")"
if [[ ! -e "$crypto_resolved" ]]; then
  echo "FAIL: libmsquic libcrypto dependency does not resolve ($crypto_dep)" >&2
  exit 1
fi
echo "libmsquic.dylib -> $crypto_resolved"

echo "==> codesign --verify"
while IFS= read -r -d '' macho; do
  if ! file -b "$macho" | grep -q 'Mach-O'; then
    continue
  fi
  if ! codesign --verify --verbose "$macho"; then
    echo "FAIL: codesign --verify failed for $macho" >&2
    exit 1
  fi
done < <(find "$APP_DIR" -type f -print0)

echo "==> quic-capability"
quic_out="$("$HYPA" connectivity quic-capability 2>&1)" || {
  echo "FAIL: quic-capability exited non-zero" >&2
  printf '%s\n' "$quic_out" >&2
  exit 1
}
if ! printf '%s\n' "$quic_out" | grep -F 'is_supported: True' >/dev/null; then
  echo "FAIL: quic-capability is not supported" >&2
  printf '%s\n' "$quic_out" >&2
  exit 1
fi
printf '%s\n' "$quic_out"

echo "==> mux serve + ping"
"$HYPA" mux serve --session "$SESSION" >"$PREFIX/mux.log" 2>&1 &
MUX_PID=$!
ready=0
i=0
while [[ "$i" -lt 30 ]]; do
  if "$HYPA" ping --session "$SESSION" >"$PREFIX/ping.out" 2>"$PREFIX/ping.err"; then
    ready=1
    break
  fi
  if ! kill -0 "$MUX_PID" 2>/dev/null; then
    echo "FAIL: mux exited before ping" >&2
    cat "$PREFIX/mux.log" >&2 || true
    exit 1
  fi
  i=$((i + 1))
  sleep 1
done
if [[ "$ready" -ne 1 ]]; then
  echo "FAIL: ping did not succeed" >&2
  cat "$PREFIX/ping.err" >&2 || true
  cat "$PREFIX/mux.log" >&2 || true
  exit 1
fi
sock="$HOME/.config/hypa/runtime/$SESSION/hypa.sock"
if [[ ! -S "$sock" ]]; then
  echo "FAIL: mux socket is not the isolated session socket ($sock)" >&2
  exit 1
fi
case "$sock" in
  "$PREFIX"/*) ;;
  *)
    echo "FAIL: mux socket is outside the isolated prefix ($sock)" >&2
    exit 1
    ;;
esac
if [[ -e "$REAL_HOME/.config/hypa/runtime/$SESSION/hypa.sock" ]]; then
  echo "FAIL: mux socket landed in the operator home" >&2
  exit 1
fi
echo "ping: $(cat "$PREFIX/ping.out")"
echo "socket: $sock"
"$HYPA" mux stop --session "$SESSION"
wait "$MUX_PID" 2>/dev/null || true
MUX_PID=""

echo "==> doctor"
doctor_out="$("$HYPA" doctor 2>&1)" || {
  echo "FAIL: doctor exited non-zero" >&2
  printf '%s\n' "$doctor_out" >&2
  exit 1
}
if printf '%s\n' "$doctor_out" | grep -i '\[fail\]' >/dev/null; then
  echo "FAIL: doctor reported a failure" >&2
  printf '%s\n' "$doctor_out" >&2
  exit 1
fi
if printf '%s\n' "$doctor_out" | grep -F 'check failed' >/dev/null \
  && printf '%s\n' "$doctor_out" | grep -i 'ssl' >/dev/null; then
  echo "FAIL: doctor update check failed with an SSL warning" >&2
  printf '%s\n' "$doctor_out" >&2
  exit 1
fi
printf '%s\n' "$doctor_out"
echo "PASS: macOS clean install checks"
