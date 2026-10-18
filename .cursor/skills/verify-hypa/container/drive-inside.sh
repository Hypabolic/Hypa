#!/bin/bash
# Drive the Hypa mux inside Xvfb the way a person would: real xterm, real keys.
set -euo pipefail

export DISPLAY="${DISPLAY:-:99}"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export HYPA_GHOSTTY_VT="${HYPA_GHOSTTY_VT:-/opt/native/libghostty-vt.so}"
export HYPA_PTY_HOST="${HYPA_PTY_HOST:-/opt/native/hypa-pty-host}"
export TERM=xterm-256color
export LANG=C.UTF-8
FEATURE="${FEATURE:-onboarding}"
EVIDENCE="${EVIDENCE:-/evidence}"
mkdir -p "$EVIDENCE" /tmp/hypa-home /tmp/hypa-runtime
export HOME=/tmp/hypa-home
SESSION="v${RUN_ID:-local}"
SESSION="${SESSION:0:12}"
export HYPA_SESSION="$SESSION"
export HYPA_RUNTIME_STATE_DIR=/tmp/hypa-runtime
export HYPA_RUNTIME_SOCKET=/tmp/hypa-runtime/hypa.sock
export HYPA_CONFIG_PATH=/tmp/hypa-runtime/config.toml
cat > "$HYPA_CONFIG_PATH" <<'EOF'
onboarding = true

[theme]
name = "catppuccin"

[keys]
prefix = "ctrl+b"
help = "prefix+?"
EOF

doctor() {
  local ok=0
  {
    echo "binary=$(/opt/hypa/hypa --version 2>&1 || true)"
    echo "ghostty=$HYPA_GHOSTTY_VT"
    echo "pty=$HYPA_PTY_HOST"
    echo "session=$HYPA_SESSION"
    echo "socket=$HYPA_RUNTIME_SOCKET"
    test -x /opt/hypa/hypa || ok=1
    test -f "$HYPA_GHOSTTY_VT" || ok=1
    test -x "$HYPA_PTY_HOST" || ok=1
  } > "$EVIDENCE/doctor.txt"
  return "$ok"
}

if ! doctor; then
  echo "doctor failed" >&2
  cat "$EVIDENCE/doctor.txt" >&2
  exit 1
fi

Xvfb "$DISPLAY" -screen 0 1280x800x24 +extension RANDR >"$EVIDENCE/xvfb.log" 2>&1 &
XVFB=$!
cleanup() {
  if [[ -n "${XTERM:-}" ]] && kill -0 "$XTERM" 2>/dev/null; then
    kill "$XTERM" 2>/dev/null || true
    wait "$XTERM" 2>/dev/null || true
  fi
  /opt/hypa/hypa mux stop >"$EVIDENCE/mux-stop.txt" 2>&1 || true
  if [[ -n "${OPENBOX:-}" ]]; then
    kill "$OPENBOX" 2>/dev/null || true
  fi
  kill "$XVFB" 2>/dev/null || true
  wait "$XVFB" 2>/dev/null || true
}
trap cleanup EXIT

for _ in $(seq 1 50); do
  xdpyinfo -display "$DISPLAY" >/dev/null 2>&1 && break
  sleep 0.1
done
openbox >"$EVIDENCE/openbox.log" 2>&1 &
OPENBOX=$!
sleep 0.5

LOG="$EVIDENCE/xterm.log"
: > "$LOG"
xterm -display "$DISPLAY" -geometry 110x32 -fa 'DejaVu Sans Mono' -fs 14 \
  -l -lf "$LOG" -e /opt/hypa/hypa >"$EVIDENCE/xterm.err" 2>&1 &
XTERM=$!

WID=""
for _ in $(seq 1 50); do
  WID=$(xdotool search --limit 1 --onlyvisible --class XTerm || true)
  [[ -n "$WID" ]] && break
  sleep 0.2
done
if [[ -z "$WID" ]]; then
  echo "xterm window did not appear" >&2
  exit 1
fi

ready=0
for _ in $(seq 1 40); do
  if grep -a -E -q 'hypa|continue|workspace' "$LOG"; then
    ready=1
    break
  fi
  kill -0 "$XTERM" 2>/dev/null || { echo "hypa exited before ready" >&2; exit 1; }
  sleep 0.5
done
echo "ready=$ready window=$WID" >> "$EVIDENCE/doctor.txt"
import -display "$DISPLAY" -window root "$EVIDENCE/${FEATURE}-before.png"

xdotool windowfocus --sync "$WID"
xdotool windowactivate --sync "$WID"
key() { xdotool key --delay 80 --window "$WID" "$@"; }

case "$FEATURE" in
  onboarding)
    key Return
    sleep 1
    import -display "$DISPLAY" -window root "$EVIDENCE/onboarding-after.png"
    ;;
  keybindings)
    key Return
    sleep 1
    key ctrl+b
    sleep 0.4
    key shift+slash
    sleep 1
    import -display "$DISPLAY" -window root "$EVIDENCE/keybindings-after.png"
    ;;
  detach)
    key Return
    sleep 1
    key ctrl+b
    sleep 0.4
    key q
    sleep 1
    import -display "$DISPLAY" -window root "$EVIDENCE/detach-after.png"
    /opt/hypa/hypa status >"$EVIDENCE/status-after-detach.txt" 2>&1 || true
    ;;
  *)
    echo "unknown feature: $FEATURE" >&2
    exit 2
    ;;
esac

echo "feature=$FEATURE session=$SESSION" > "$EVIDENCE/drive.txt"
