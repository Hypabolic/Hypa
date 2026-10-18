#!/bin/bash
# Drive one mux session the way a person would: real xterm, real keys.
set -euo pipefail
export DISPLAY="${DISPLAY:-:99}"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export HYPA_GHOSTTY_VT="${HYPA_GHOSTTY_VT:-/opt/native/libghostty-vt.so}"
export HYPA_PTY_HOST="${HYPA_PTY_HOST:-/opt/native/hypa-pty-host}"
export TERM=xterm-256color
export LANG=C.UTF-8
EVIDENCE="${EVIDENCE:-/evidence}"
mkdir -p "$EVIDENCE" /tmp/hypa-home /tmp/hypa-runtime/ja /tmp/hypa-runtime/jb
export HOME=/tmp/hypa-home
SESSION_A="${SESSION_A:-ja}"
SESSION_B="${SESSION_B:-jb}"
STATE_A=/tmp/hypa-runtime/ja
STATE_B=/tmp/hypa-runtime/jb
SOCK_A=$STATE_A/hypa.sock
SOCK_B=$STATE_B/hypa.sock
CONFIG_A=$STATE_A/config.toml
CONFIG_B=$STATE_B/config.toml
MARKER=HYPAKEEP7F3A
RIGHT=RIGHTPANE9
TABNAME=keptab
write_config() {
  cat > "$1" <<'EOF'
onboarding = true
[experimental]
pane_history = true
[theme]
name = "catppuccin"
[ui]
startup_splash = false
[keys]
prefix = "ctrl+b"
EOF
}
write_config "$CONFIG_A"
write_config "$CONFIG_B"
export HYPA_SESSION="$SESSION_A"
export HYPA_RUNTIME_STATE_DIR="$STATE_A"
export HYPA_RUNTIME_SOCKET="$SOCK_A"
export HYPA_CONFIG_PATH="$CONFIG_A"
say() { echo "$(date -u +%H:%M:%S) $*" | tee -a "$EVIDENCE/drive.txt" >&2; }
doctor() {
  local ok=0
  {
    echo "binary=$(/opt/hypa/hypa --version 2>&1 || true)"
    echo "ghostty=$HYPA_GHOSTTY_VT"
    echo "pty=$HYPA_PTY_HOST"
    echo "session_a=$SESSION_A"
    echo "session_b=$SESSION_B"
    echo "socket_a=$SOCK_A"
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
Xvfb "$DISPLAY" -screen 0 1600x1000x24 +extension RANDR >"$EVIDENCE/xvfb.log" 2>&1 &
XVFB=$!
XTERMS=()
cleanup() {
  local pid
  for pid in "${XTERMS[@]+"${XTERMS[@]}"}"; do
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
      kill "$pid" 2>/dev/null || true
      wait "$pid" 2>/dev/null || true
    fi
  done
  HYPA_SESSION="$SESSION_B" HYPA_RUNTIME_SOCKET="$SOCK_B" HYPA_RUNTIME_STATE_DIR="$STATE_B" \
    HYPA_CONFIG_PATH="$CONFIG_B" /opt/hypa/hypa mux stop >"$EVIDENCE/mux-stop-b.txt" 2>&1 || true
  HYPA_SESSION="$SESSION_A" HYPA_RUNTIME_SOCKET="$SOCK_A" HYPA_RUNTIME_STATE_DIR="$STATE_A" \
    HYPA_CONFIG_PATH="$CONFIG_A" /opt/hypa/hypa mux stop >"$EVIDENCE/mux-stop-a.txt" 2>&1 || true
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
sleep 0.4
# Drop CSI color and cursor sequences. Leave the characters the mux painted.
# A greedy OSC match will swallow the screen, so this does not try to parse OSC.
plain() {
  sed -E $'s/\x1b\\[[0-9;?]*[ -/]*[@-~]//g' "$1" > "$2"
}
has_text() {
  local log=$1 pattern=$2 dest=$3
  plain "$log" "$dest"
  grep -a -F -q -- "$pattern" "$dest"
}
wait_text() {
  local log=$1 pattern=$2 dest=$3 tries=${4:-40}
  local i
  for i in $(seq 1 "$tries"); do
    if [[ -f "$log" ]] && has_text "$log" "$pattern" "$dest"; then
      return 0
    fi
    sleep 0.25
  done
  import -display "$DISPLAY" -window root "$EVIDENCE/fail.png" || true
  say "missing text: $pattern"
  return 1
}
wait_regex() {
  local log=$1 pattern=$2 dest=$3 tries=${4:-40}
  local i
  for i in $(seq 1 "$tries"); do
    if [[ -f "$log" ]]; then
      plain "$log" "$dest"
      if grep -a -E -q -- "$pattern" "$dest"; then
        return 0
      fi
    fi
    sleep 0.25
  done
  import -display "$DISPLAY" -window root "$EVIDENCE/fail.png" || true
  say "missing regex: $pattern"
  return 1
}
shot() {
  local name=$1
  import -display "$DISPLAY" -window root "$EVIDENCE/${name}.png"
  local size
  size=$(stat -c%s "$EVIDENCE/${name}.png")
  say "shot $name bytes=$size"
  if [[ "$size" -lt 8000 ]]; then
    echo "screenshot $name is too small ($size)" >&2
    exit 1
  fi
}
find_window() {
  local name=$1 id="" i
  for i in $(seq 1 50); do
    id=$(xdotool search --limit 1 --classname "$name" 2>/dev/null || true)
    if [[ -z "$id" ]]; then
      id=$(xdotool search --limit 1 --name "$name" 2>/dev/null || true)
    fi
    if [[ -n "$id" ]]; then
      echo "$id"
      return 0
    fi
    sleep 0.2
  done
  return 1
}
start_xterm() {
  local name=$1 geom=$2 log=$3 cmd=$4
  : > "$log"
  # The inner bash sees cmd unchanged, including $?.
  xterm -display "$DISPLAY" +sb -name "$name" -T "$name" \
    -geometry "$geom" -fa 'DejaVu Sans Mono' -fs 13 \
    -l -lf "$log" -e bash -c 'exec bash -c "$1"' _ "$cmd" \
    >"$EVIDENCE/${name}.err" 2>&1 &
  XTERMS+=("$!")
  echo "$!"
}
focus() {
  xdotool windowfocus --sync "$WID"
  xdotool windowactivate --sync "$WID"
  sleep 0.15
}
key() {
  focus
  xdotool key --delay 60 --window "$WID" "$@"
}
# xdotool type drops the first character into this xterm. Send one key each.
type_text() {
  focus
  sleep 0.2
  # A dropped first event must not eat the command.
  xdotool key --delay 20 --window "$WID" space
  local text=$1 i ch
  for ((i = 0; i < ${#text}; i++)); do
    ch=${text:i:1}
    if [[ "$ch" == " " ]]; then
      xdotool key --delay 20 --window "$WID" space
    else
      xdotool key --delay 20 --window "$WID" "$ch"
    fi
  done
}
prefix() {
  key ctrl+b
  sleep 0.35
  key "$1"
  sleep 0.7
}
GEOM_MAIN=112x26+10+10
GEOM_SECOND=96x16+70+520
GEOM_SIDE=130x8+10+830
LOG_A=$EVIDENCE/session-a.log
LOG_B=$EVIDENCE/session-b.log
LOG_C=$EVIDENCE/reattach.log
LOG_H=$EVIDENCE/handoff-xterm.log
LOG_S=$EVIDENCE/stop-xterm.log
say "start session $SESSION_A"
XTERM_A=$(start_xterm hypaA "$GEOM_MAIN" "$LOG_A" \
  "/opt/hypa/hypa; echo HYPA-CLIENT-EXIT; /opt/hypa/hypa status; echo STATUS-DONE; exec sleep 600")
WID=$(find_window hypaA) || { echo "session A window did not appear" >&2; exit 1; }
WID_A=$WID
say "window A=$WID pid=$XTERM_A"
wait_text "$LOG_A" "terminal workspace manager" "$EVIDENCE/plain-onboarding.txt" 80
shot 01-onboarding
key Return
sleep 0.8
key Escape
sleep 0.4
key Escape
# The pane shell starts in $HOME, so the prompt is "~#" rather than ":/#".
wait_regex "$LOG_A" '~#|:/#' "$EVIDENCE/plain-workspace.txt" 80
shot 02-workspace
type_text "echo $MARKER"
key Return
wait_text "$LOG_A" "$MARKER" "$EVIDENCE/plain-output.txt" 40
shot 03-output
prefix shift+slash
wait_text "$LOG_A" "Keyboard and mouse reach the same layout" "$EVIDENCE/plain-help.txt" 30
shot 04-help
key Escape
sleep 0.4
prefix v
sleep 0.8
shot 05-split
prefix h
prefix l
type_text "echo $RIGHT"
key Return
wait_text "$LOG_A" "$RIGHT" "$EVIDENCE/plain-pane.txt" 30
shot 06-pane-switch
prefix shift+t
sleep 0.4
type_text "$TABNAME"
key Return
wait_text "$LOG_A" "$TABNAME" "$EVIDENCE/plain-tab.txt" 30
shot 07-named-tab
say "start session $SESSION_B"
XTERM_B=$(start_xterm hypaB "$GEOM_SECOND" "$LOG_B" \
  "env HYPA_SESSION=$SESSION_B HYPA_RUNTIME_STATE_DIR=$STATE_B HYPA_RUNTIME_SOCKET=$SOCK_B HYPA_CONFIG_PATH=$CONFIG_B /opt/hypa/hypa; echo HYPA-B-EXIT; exec sleep 600")
WID_B=$(find_window hypaB) || { echo "session B window did not appear" >&2; exit 1; }
say "window B=$WID_B pid=$XTERM_B"
wait_text "$LOG_B" "terminal workspace manager" "$EVIDENCE/plain-session-b.txt" 50
shot 08-second-session
WID=$WID_A
prefix q
wait_text "$LOG_A" "server.alive=true" "$EVIDENCE/plain-detach.txt" 60
cp "$EVIDENCE/plain-detach.txt" "$EVIDENCE/status-after-detach.txt"
shot 09-detach
kill "$XTERM_A" 2>/dev/null || true
wait "$XTERM_A" 2>/dev/null || true
say "reattach $SESSION_A"
XTERM_C=$(start_xterm hypaC "$GEOM_MAIN" "$LOG_C" \
  "/opt/hypa/hypa; echo HYPA-CLIENT-EXIT; exec sleep 600")
WID=$(find_window hypaC) || { echo "reattach window did not appear" >&2; exit 1; }
say "window C=$WID pid=$XTERM_C"
# The focused pane still shows the line typed after the split. The earlier
# line in the other pane can scroll out of the visible screen on resize.
wait_text "$LOG_C" "$RIGHT" "$EVIDENCE/plain-reattach.txt" 50
wait_text "$LOG_C" "$TABNAME" "$EVIDENCE/plain-tab-reattach.txt" 20
shot 10-reattach
say "live-handoff"
XTERM_H=$(start_xterm hypaH "$GEOM_SIDE" "$LOG_H" \
  "/opt/hypa/hypa mux live-handoff > /evidence/live-handoff.txt 2>&1; echo LIVE-HANDOFF-EXIT:\$?; cat /evidence/live-handoff.txt; exec sleep 600")
find_window hypaH >/dev/null || { echo "handoff window did not appear" >&2; exit 1; }
wait_regex "$LOG_H" '"ok"[[:space:]]*:[[:space:]]*true' "$EVIDENCE/plain-handoff.txt" 60
if has_text "$LOG_C" "HYPA-CLIENT-EXIT" "$EVIDENCE/plain-reattach-after-handoff.txt"; then
  echo "live-handoff stopped the attached client" >&2
  exit 1
fi
shot 11-live-handoff
kill "$XTERM_H" 2>/dev/null || true
wait "$XTERM_H" 2>/dev/null || true
say "session stop"
XTERM_S=$(start_xterm hypaS "$GEOM_SIDE" "$LOG_S" \
  "/opt/hypa/hypa session stop > /evidence/session-stop.txt 2>&1; echo STOP-EXIT:\$?; cat /evidence/session-stop.txt; exec sleep 600")
find_window hypaS >/dev/null || { echo "stop window did not appear" >&2; exit 1; }
wait_text "$LOG_S" "Stopped mux session=$SESSION_A" "$EVIDENCE/plain-stop.txt" 60
wait_text "$LOG_C" "HYPA-CLIENT-EXIT" "$EVIDENCE/plain-client-gone.txt" 60
shot 12-session-stop
if [[ -f "$STATE_A/session-history.json" ]]; then
  cp "$STATE_A/session-history.json" "$EVIDENCE/session-history.json"
  say "history file copied"
fi
differ() {
  if cmp -s "$EVIDENCE/$1.png" "$EVIDENCE/$2.png"; then
    echo "screenshots match, so $2 did not change the screen: $1" >&2
    exit 1
  fi
}
differ 01-onboarding 02-workspace
differ 02-workspace 03-output
differ 03-output 04-help
differ 04-help 05-split
differ 05-split 06-pane-switch
differ 06-pane-switch 07-named-tab
differ 07-named-tab 08-second-session
differ 08-second-session 09-detach
differ 09-detach 10-reattach
differ 10-reattach 11-live-handoff
differ 11-live-handoff 12-session-stop
say "journey complete marker=$MARKER tab=$TABNAME"
