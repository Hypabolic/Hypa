#!/usr/bin/env bash
# Two containers: a cube host and a GUI client. Evidence stays on the host.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SKILL_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ROOT="$(cd "$SKILL_DIR/../../.." && pwd)"
RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)-$$"
EVIDENCE="${HYPA_VERIFY_EVIDENCE:-$ROOT/artifacts/verify-hypa/$RUN_ID}"
mkdir -p "$EVIDENCE"
BIN_DIR="${HYPA_BIN_DIR:-$ROOT/src/Hypa.Cli/bin/Release/net10.0}"
GHOSTTY="${HYPA_GHOSTTY_VT:-$ROOT/native/runtimes/linux-x64/native/libghostty-vt.so}"
PTY_HOST="${HYPA_PTY_HOST:-$ROOT/native/runtimes/linux-x64/native/hypa-pty-host}"
DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
# A framework-dependent build needs the .NET runtime. A native pack does not.
# Mount the runtime when it is present, and require it for a build with a runtimeconfig.
NEEDED=("$BIN_DIR/hypa" "$GHOSTTY" "$PTY_HOST")
DOTNET_ARGS=()
if [[ -f "$BIN_DIR/hypa.runtimeconfig.json" ]]; then
  NEEDED+=("$DOTNET_ROOT/dotnet")
fi
if [[ -x "$DOTNET_ROOT/dotnet" ]]; then
  DOTNET_ARGS=(-e DOTNET_ROOT=/opt/dotnet -v "$DOTNET_ROOT:/opt/dotnet:ro")
fi
for path in "${NEEDED[@]}"; do
  [[ -e "$path" ]] || { echo "Missing $path. Follow Launch in the skill." >&2; exit 1; }
done
if docker info >/dev/null 2>&1; then DOCKER=(docker); else DOCKER=(sudo docker); fi

IMAGE="hypa-verify-mux:local"
"${DOCKER[@]}" build -t "$IMAGE" "$SKILL_DIR/container" >"$EVIDENCE/docker-build.log"
NET="hypa-cube-$RUN_ID"
HOST="hypa-cube-host-$RUN_ID"
CLIENT="hypa-cube-client-$RUN_ID"
cleanup() {
  "${DOCKER[@]}" rm -f "$HOST" "$CLIENT" >/dev/null 2>&1 || true
  "${DOCKER[@]}" network rm "$NET" >/dev/null 2>&1 || true
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
"${DOCKER[@]}" network create "$NET" >"$EVIDENCE/network.txt"
# Publish the cube port on this run's gateway address only. Another run, or a
# local accept on 7443, holds a different address, so the ports do not collide.
GATEWAY=$("${DOCKER[@]}" network inspect "$NET" -f '{{(index .IPAM.Config 0).Gateway}}')
[[ -n "$GATEWAY" ]] || { echo "Network $NET has no gateway address." >&2; exit 1; }

mounts=(
  -v "$BIN_DIR:/opt/hypa:ro"
  "${DOTNET_ARGS[@]+"${DOTNET_ARGS[@]}"}"
  -v "$GHOSTTY:/opt/native/libghostty-vt.so:ro"
  -v "$PTY_HOST:/opt/native/hypa-pty-host:ro"
  -v "$EVIDENCE:/evidence"
)
START=$(date +%s)
"${DOCKER[@]}" run -d --name "$HOST" --hostname cubehost --network "$NET" -p "$GATEWAY:7443:7443" \
  --entrypoint /usr/local/bin/drive-cube.sh \
  -e ROLE=host -e EVIDENCE=/evidence -e CUBE_GATEWAY="$GATEWAY" \
  -e HYPA_GHOSTTY_VT=/opt/native/libghostty-vt.so -e HYPA_PTY_HOST=/opt/native/hypa-pty-host \
  "${mounts[@]}" "$IMAGE" >/dev/null
"${DOCKER[@]}" run -d --name "$CLIENT" --hostname cubeclient --network "$NET" \
  --cap-add NET_ADMIN \
  --entrypoint /usr/local/bin/drive-cube.sh \
  -e ROLE=client -e EVIDENCE=/evidence \
  -e HYPA_GHOSTTY_VT=/opt/native/libghostty-vt.so -e HYPA_PTY_HOST=/opt/native/hypa-pty-host \
  "${mounts[@]}" "$IMAGE" >/dev/null

set +e
# Wait in the background, so a signal runs the trap at once.
"${DOCKER[@]}" wait "$CLIENT" >"$EVIDENCE/client-exit.txt" &
wait "$!"
CLIENT_STATUS=$(cat "$EVIDENCE/client-exit.txt")
"${DOCKER[@]}" wait "$HOST" >"$EVIDENCE/host-exit.txt" &
wait "$!"
HOST_STATUS=$(cat "$EVIDENCE/host-exit.txt")
set -e
END=$(date +%s)
SHOTS=(
  host-01-share.png
  client-01-add.png
  client-02-remote.png
  client-03-detach.png
  client-04-reattach.png
  host-02-handoff.png
  client-05-handoff.png
  client-06-link-drop.png
  client-07-link-back.png
)
MISSING=0
for shot in "${SHOTS[@]}"; do
  if [[ ! -s "$EVIDENCE/$shot" ]]; then
    echo "Missing screenshot $EVIDENCE/$shot" >&2
    MISSING=1
  fi
done
echo "client=$CLIENT_STATUS host=$HOST_STATUS seconds=$((END - START)) evidence=$EVIDENCE" | tee "$EVIDENCE/run.txt"
[[ "$MISSING" -eq 0 && "$CLIENT_STATUS" == 0 && "$HOST_STATUS" == 0 ]]
