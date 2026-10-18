#!/bin/bash
# One side of a remote-cube drive. ROLE is host or client.
set -euo pipefail
ROLE="${ROLE:?host or client}"
export DISPLAY="${DISPLAY:-:99}"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export HYPA_GHOSTTY_VT="${HYPA_GHOSTTY_VT:-/opt/native/libghostty-vt.so}"
export HYPA_PTY_HOST="${HYPA_PTY_HOST:-/opt/native/hypa-pty-host}"
export TERM=xterm-256color
export LANG=C.UTF-8
EVIDENCE="${EVIDENCE:-/evidence}"
mkdir -p "$EVIDENCE" /tmp/hypa-home /tmp/hypa-runtime
export HOME=/tmp/hypa-home
COLS=110
ROWS=32
GEOM="${COLS}x${ROWS}+8+8"
READY=HOSTREADY
if [[ "$ROLE" == host ]]; then export HYPA_SESSION=cube; else export HYPA_SESSION=local; fi
# Share looks up ~/.config/hypa/runtime/<session>/hypa.sock and ignores HYPA_RUNTIME_SOCKET.
export HYPA_RUNTIME_STATE_DIR="$HOME/.config/hypa/runtime/$HYPA_SESSION"
export HYPA_RUNTIME_SOCKET="$HYPA_RUNTIME_STATE_DIR/hypa.sock"
mkdir -p "$HYPA_RUNTIME_STATE_DIR"
export HYPA_CONFIG_PATH=$HYPA_RUNTIME_STATE_DIR/config.toml
cat > "$HYPA_CONFIG_PATH" <<'EOF'
onboarding = true
[theme]
name = "catppuccin"
[ui]
startup_splash = false
[keys]
prefix = "ctrl+b"
EOF
say() { echo "$(date -u +%H:%M:%S) $ROLE $*" | tee -a "$EVIDENCE/$ROLE-drive.txt" >&2; }
plain() { sed -E $'s/\x1b\\[[0-9;?]*[ -/]*[@-~]//g' "$1" > "$2"; }
wait_text() {
  local log=$1 pattern=$2 dest=$3 tries=${4:-40} i
  for i in $(seq 1 "$tries"); do
    if [[ -f "$log" ]] && plain "$log" "$dest" && grep -a -F -q -- "$pattern" "$dest"; then
      return 0
    fi
    sleep 0.25
  done
  import -display "$DISPLAY" -window root "$EVIDENCE/$ROLE-fail.png" || true
  python3 /usr/local/bin/screengrid.py dump "$log" > "$EVIDENCE/$ROLE-fail-grid.txt" || true
  say "missing text: $pattern"
  return 1
}
shot() {
  local name=$1
  import -display "$DISPLAY" -window root "$EVIDENCE/${ROLE}-${name}.png"
  local size
  size=$(stat -c%s "$EVIDENCE/${ROLE}-${name}.png")
  say "shot $name bytes=$size"
  [[ "$size" -ge 8000 ]]
}
Xvfb "$DISPLAY" -screen 0 1400x900x24 +extension RANDR >"$EVIDENCE/$ROLE-xvfb.log" 2>&1 &
XVFB=$!
XTERMS=()
cleanup() {
  if [[ "$ROLE" == client && ! -f "$EVIDENCE/client-done" ]]; then
    echo fail > "$EVIDENCE/client-fail"
  fi
  local pid
  for pid in "${XTERMS[@]+"${XTERMS[@]}"}"; do
    kill "$pid" 2>/dev/null || true
  done
  /opt/hypa/hypa mux stop >"$EVIDENCE/$ROLE-mux-stop.txt" 2>&1 || true
  kill "${OPENBOX:-}" "$XVFB" 2>/dev/null || true
}
trap cleanup EXIT
for _ in $(seq 1 50); do xdpyinfo -display "$DISPLAY" >/dev/null 2>&1 && break; sleep 0.1; done
openbox >"$EVIDENCE/$ROLE-openbox.log" 2>&1 &
OPENBOX=$!
sleep 0.3
start_xterm() {
  local name=$1 log=$2 cmd=$3
  : > "$log"
  xterm -display "$DISPLAY" +sb -bw 0 -name "$name" -T "$name" \
    -geometry "$GEOM" -fa 'DejaVu Sans Mono' -fs 13 \
    -xrm 'XTerm*internalBorder: 0' \
    -l -lf "$log" -e bash -c 'exec bash -c "$1"' _ "$cmd" \
    >"$EVIDENCE/${name}.err" 2>&1 &
  XTERMS+=("$!")
}
find_window() {
  local name=$1 id="" i
  for i in $(seq 1 50); do
    id=$(xdotool search --limit 1 --classname "$name" 2>/dev/null || true)
    [[ -n "$id" ]] && { echo "$id"; return 0; }
    sleep 0.2
  done
  return 1
}
focus() { xdotool windowfocus --sync "$WID"; xdotool windowactivate --sync "$WID"; sleep 0.1; }
key() { focus; xdotool key --delay 40 --window "$WID" "$@"; }
click_cell() {
  local row=$1 col=$2 line w h x y cw ch px py
  line=$(xwininfo -id "$WID")
  w=$(awk '/Width:/ {print $2; exit}' <<<"$line")
  h=$(awk '/Height:/ {print $2; exit}' <<<"$line")
  x=$(awk '/Absolute upper-left X:/ {print $4; exit}' <<<"$line")
  y=$(awk '/Absolute upper-left Y:/ {print $4; exit}' <<<"$line")
  cw=$((w / COLS)); ch=$((h / ROWS))
  [[ "$cw" -ge 4 && "$ch" -ge 8 ]]
  px=$((x + (col - 1) * cw + cw / 2))
  py=$((y + (row - 1) * ch + ch / 2))
  say "click row=$row col=$col px=$px py=$py cell=${cw}x${ch}"
  xdotool mousemove --sync "$px" "$py"
  xdotool click 1
}
cell_of() {
  python3 /usr/local/bin/screengrid.py find "$1" "$2"
}
type_text() {
  focus; sleep 0.12
  local text=$1 i ch name
  if [[ "${2:-shell}" == code ]]; then xdotool key --window "$WID" space; xdotool key --window "$WID" BackSpace
  else xdotool key --window "$WID" space; fi
  for ((i=0; i<${#text}; i++)); do
    ch=${text:i:1}
    case "$ch" in " ") name=space ;; -) name=minus ;; _) name=underscore ;; :) name=colon ;; *) name=$ch ;; esac
    xdotool key --delay 12 --window "$WID" "$name"
  done
}
LOG=$EVIDENCE/$ROLE-mux.log
if [[ "$ROLE" == host ]]; then
  echo onlyonhost > "$HOME/onlyonhost"
  # Container-to-container addresses do not answer here. The published port does, on the gateway.
  HOST_IP=$(ip route | awk '/default/ {print $3; exit}')
  echo "$HOST_IP" > "$EVIDENCE/host.ip"
  export HYPA_ADVERTISE_HOST="$HOST_IP"
  say "advertise $HOST_IP"
  start_xterm cubeHost "$LOG" "/opt/hypa/hypa; echo HYPA-CLIENT-EXIT; exec sleep 600"
  WID=$(find_window cubeHost)
  wait_text "$LOG" "terminal workspace manager" "$EVIDENCE/host-plain-onboarding.txt" 80
  key Return; sleep 0.6; key Escape; sleep 0.3; key Escape
wait_text "$LOG" "~#" "$EVIDENCE/host-plain-shell.txt" 80
type_text "echo $READY"
key Return
wait_text "$LOG" "$READY" "$EVIDENCE/host-plain-ready.txt" 40
hit=$(cell_of "$LOG" " share") || { say "share row was not on screen"; exit 1; }
  read -r ROW COL <<<"$hit"
  click_cell "$ROW" "$COL"
  wait_text "$LOG" "share this cube" "$EVIDENCE/host-plain-share.txt" 40
  key Return
  wait_text "$LOG" "hypa-invite:" "$EVIDENCE/host-plain-invite.txt" 80
  python3 /usr/local/bin/screengrid.py invite "$LOG" > "$EVIDENCE/invite.txt"
  grep -q '^hypa-invite:' "$EVIDENCE/invite.txt"
  shot 01-share
  say "invite bytes=$(wc -c < "$EVIDENCE/invite.txt")"
  for _ in $(seq 1 400); do
    [[ -f "$EVIDENCE/do-handoff" ]] && break
    [[ -f "$EVIDENCE/client-done" || -f "$EVIDENCE/client-fail" ]] && exit 0
    sleep 0.5
  done
  [[ -f "$EVIDENCE/do-handoff" ]]
  start_xterm cubeHand "$EVIDENCE/host-handoff.log" \
    "/opt/hypa/hypa mux live-handoff > /evidence/live-handoff.txt 2>&1; echo LIVE-HANDOFF-EXIT:\$?; cat /evidence/live-handoff.txt; exec sleep 30"
  wait_text "$EVIDENCE/host-handoff.log" '"ok":true' "$EVIDENCE/host-plain-handoff.txt" 40
  shot 02-handoff
  echo host-handoff-done > "$EVIDENCE/host-handoff-done"
  for _ in $(seq 1 120); do
    [[ -f "$EVIDENCE/client-done" || -f "$EVIDENCE/client-fail" ]] && break
    sleep 0.5
  done
  exit 0
fi
# client
trap 'echo fail > "$EVIDENCE/client-fail"' ERR
for _ in $(seq 1 180); do [[ -s "$EVIDENCE/invite.txt" ]] && break; sleep 0.5; done
[[ -s "$EVIDENCE/invite.txt" ]]
INVITE=$(cat "$EVIDENCE/invite.txt")
HOST_IP=$(cat "$EVIDENCE/host.ip")
start_xterm cubeClient "$LOG" "/opt/hypa/hypa; echo HYPA-CLIENT-EXIT; exec sleep 600"
WID=$(find_window cubeClient)
wait_text "$LOG" "terminal workspace manager" "$EVIDENCE/client-plain-onboarding.txt" 80
key Return; sleep 0.6; key Escape; sleep 0.3; key Escape
wait_text "$LOG" "~#" "$EVIDENCE/client-plain-shell.txt" 80
hit=$(cell_of "$LOG" " add") || { say "add row was not on screen"; exit 1; }
read -r ROW COL <<<"$hit"
click_cell "$ROW" "$COL"
wait_text "$LOG" "add cube" "$EVIDENCE/client-plain-add.txt" 40
type_text "$INVITE" code
key Tab
type_text remote code
wait_text "$LOG" "hypa-invite:" "$EVIDENCE/client-plain-typed.txt" 40
shot 01-add
key Return
wait_text "$LOG" "remote" "$EVIDENCE/client-plain-paired.txt" 120
if grep -a -F -q "invite is invalid" "$EVIDENCE/client-plain-paired.txt"; then
  say "redeem rejected the invite"
  exit 1
fi
hit=$(cell_of "$LOG" "remote") || { say "remote cube was not on screen"; exit 1; }
read -r ROW COL <<<"$hit"
click_cell "$ROW" "$COL"
wait_text "$LOG" "$READY" "$EVIDENCE/client-plain-connected.txt" 120
type_text hostname
key Return
wait_text "$LOG" "cubehost" "$EVIDENCE/client-plain-host.txt" 40
type_text "cat onlyonhost"
key Return
wait_text "$LOG" "onlyonhost" "$EVIDENCE/client-plain-remote.txt" 40
shot 02-remote
key ctrl+b; sleep 0.3; key q
wait_text "$LOG" "HYPA-CLIENT-EXIT" "$EVIDENCE/client-plain-detach.txt" 40
shot 03-detach
start_xterm cubeAgain "$EVIDENCE/client-reattach.log" "/opt/hypa/hypa; echo HYPA-CLIENT-EXIT; exec sleep 600"
WID=$(find_window cubeAgain)
wait_text "$EVIDENCE/client-reattach.log" "~#" "$EVIDENCE/client-plain-rehome.txt" 80
key Escape
sleep 0.4
hit=$(cell_of "$EVIDENCE/client-reattach.log" "remote") || { say "remote cube missing after reattach"; exit 1; }
read -r ROW COL <<<"$hit"
click_cell "$ROW" "$COL"
wait_text "$EVIDENCE/client-reattach.log" "onlyonhost" "$EVIDENCE/client-plain-reattach.txt" 80
shot 04-reattach
echo go > "$EVIDENCE/do-handoff"
for _ in $(seq 1 80); do [[ -f "$EVIDENCE/host-handoff-done" ]] && break; sleep 0.25; done
[[ -f "$EVIDENCE/host-handoff-done" ]]
grep -q '"ok":true' "$EVIDENCE/live-handoff.txt"
if grep -a -F -q HYPA-CLIENT-EXIT "$EVIDENCE/client-reattach.log"; then
  say "handoff dropped the client"
  exit 1
fi
shot 05-handoff
# The dial is an IPv6-mapped socket, so the v4 rule does not see it. Reset both.
iptables -I OUTPUT -p tcp -d "$HOST_IP" -j REJECT --reject-with tcp-reset
ip6tables -I OUTPUT -p tcp -d "::ffff:$HOST_IP" -j REJECT --reject-with tcp-reset
wait_text "$EVIDENCE/client-reattach.log" "Reconnecting to" "$EVIDENCE/client-plain-drop.txt" 120
shot 06-link-drop
iptables -D OUTPUT -p tcp -d "$HOST_IP" -j REJECT --reject-with tcp-reset
ip6tables -D OUTPUT -p tcp -d "::ffff:$HOST_IP" -j REJECT --reject-with tcp-reset
wait_text "$EVIDENCE/client-reattach.log" "onlyonhost" "$EVIDENCE/client-plain-back.txt" 80
shot 07-link-back
echo ok > "$EVIDENCE/client-done"
say "cube journey complete"
