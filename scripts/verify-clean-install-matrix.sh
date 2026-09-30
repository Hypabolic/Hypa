#!/usr/bin/env bash
# Clean-install matrix for a linux-x64 F1 archive.
#
# Hosts: debian:12, ubuntu:22.04, ubuntu:24.04, fedora:40.
# Each host installs OpenSSL 3 and the CA trust bundle first.
# Those packages are host requirements. libnuma is bundled.
# The host must not install libnuma.
#
# Usage:
#   scripts/verify-clean-install-matrix.sh --archive PATH
#   HYPA_DOCKER_CONTEXT=desktop-linux scripts/verify-clean-install-matrix.sh --archive PATH
#
# macOS packs (osx-x64 and osx-arm64) are checked by
# scripts/verify-clean-install-macos.sh on the macOS runner.
# That script installs into an isolated HOME, checks bundled libmsquic
# and libcrypto.3.dylib, codesign, quarantine, QUIC, mux ping, and doctor.
# libnuma is Linux only. macOS does not install libssl3. OpenSSL ships
# as the bundled libcrypto.3.dylib.

set -euo pipefail

ARCHIVE=""
ONLY_IMAGE=""

usage() {
  echo "Usage: $0 --archive PATH [--image IMAGE]" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --archive)
      [[ $# -ge 2 ]] || usage
      ARCHIVE="$2"
      shift 2
      ;;
    --image)
      [[ $# -ge 2 ]] || usage
      ONLY_IMAGE="$2"
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

docker_cmd() {
  if [[ -n "${HYPA_DOCKER_CONTEXT:-}" ]]; then
    docker --context "$HYPA_DOCKER_CONTEXT" "$@"
  else
    docker "$@"
  fi
}

IMAGES=(debian:12 ubuntu:22.04 ubuntu:24.04 fedora:40)
if [[ -n "$ONLY_IMAGE" ]]; then
  IMAGES=("$ONLY_IMAGE")
fi

INNER="$(mktemp "${TMPDIR:-/tmp}/hypa-clean-install.XXXXXX")"
cleanup() { rm -f "$INNER"; }
trap cleanup EXIT

cat > "$INNER" <<'EOF'
#!/bin/sh
set -eu

echo "==> install OpenSSL 3 and the CA trust bundle (host requirements)"
if [ "$HYPA_PKG_FAMILY" = "dnf" ]; then
  dnf install -y openssl-libs ca-certificates util-linux
else
  export DEBIAN_FRONTEND=noninteractive
  apt-get update
  apt-get install -y ca-certificates
  apt-get install -y libssl3 || apt-get install -y libssl3t64
fi

if command -v ldconfig >/dev/null 2>&1; then
  if ldconfig -p 2>/dev/null | grep -F 'libnuma.so.1' >/dev/null 2>&1; then
    echo "FAIL: host provides libnuma.so.1. The pack must bundle it." >&2
    exit 1
  fi
fi
for _numa in \
  /usr/lib/x86_64-linux-gnu/libnuma.so.1 \
  /lib/x86_64-linux-gnu/libnuma.so.1 \
  /usr/lib/aarch64-linux-gnu/libnuma.so.1 \
  /lib/aarch64-linux-gnu/libnuma.so.1 \
  /usr/lib64/libnuma.so.1 \
  /lib64/libnuma.so.1
do
  if [ -e "$_numa" ]; then
    echo "FAIL: host provides $_numa. The pack must bundle it." >&2
    exit 1
  fi
done

echo "==> install.sh"
HYPA_ARCHIVE=/opt/hypa-archive.tar.gz sh /opt/install.sh
export PATH="$HOME/.local/bin:$PATH"

echo "==> hypa --version"
hypa --version

app="$(cd "$HOME/.local/share/hypa" && pwd -P)"
if [ ! -f "$app/libnuma.so.1" ]; then
  echo "FAIL: bundled libnuma.so.1 is missing from $app" >&2
  exit 1
fi

echo "==> ldd"
find "$app" -type f > /tmp/hypa-elf-files
while IFS= read -r file; do
  magic="$(od -An -t x1 -N 4 "$file" | tr -d ' \n' | tr 'A-F' 'a-f')"
  if [ "$magic" != "7f454c46" ]; then
    continue
  fi
  ldd_out="$(ldd "$file" 2>&1 || true)"
  if printf '%s\n' "$ldd_out" | grep -F 'not found' >/dev/null 2>&1; then
    echo "FAIL: ldd reports not found for $file" >&2
    printf '%s\n' "$ldd_out" >&2
    exit 1
  fi
done < /tmp/hypa-elf-files

echo "==> quic-capability"
quic_out="$(hypa connectivity quic-capability 2>&1)" || {
  echo "FAIL: quic-capability exited non-zero" >&2
  printf '%s\n' "$quic_out" >&2
  exit 1
}
if ! printf '%s\n' "$quic_out" | grep -F 'is_supported: True' >/dev/null 2>&1; then
  echo "FAIL: quic-capability is not supported" >&2
  printf '%s\n' "$quic_out" >&2
  exit 1
fi
printf '%s\n' "$quic_out"

echo "==> root-owned system install, run by another user"
# A package manager or sudo install leaves root-owned files.
# QUIC must still load for a user who does not own them.
sys_app=/opt/hypa-system
cp -R "$app" "$sys_app"
chown -R 0:0 "$sys_app"
chmod -R go-w "$sys_app"
sys_quic="$(setpriv --reuid=65534 --regid=65534 --clear-groups \
  env HOME=/tmp "$sys_app/hypa" connectivity quic-capability 2>&1)" || {
  echo "FAIL: quic-capability as uid 65534 exited non-zero" >&2
  printf '%s\n' "$sys_quic" >&2
  exit 1
}
if ! printf '%s\n' "$sys_quic" | grep -F 'is_supported: True' >/dev/null 2>&1; then
  echo "FAIL: QUIC is not supported for a root-owned install run as uid 65534" >&2
  printf '%s\n' "$sys_quic" >&2
  exit 1
fi
echo "system install: QUIC supported as uid 65534"
rm -rf "$sys_app"

echo "==> mux serve + ping"
hypa mux serve --session default > /tmp/hypa-mux.log 2>&1 &
mux_pid=$!
ready=0
i=0
while [ "$i" -lt 30 ]; do
  if hypa ping --session default > /tmp/hypa-ping.out 2>/tmp/hypa-ping.err; then
    ready=1
    break
  fi
  if ! kill -0 "$mux_pid" 2>/dev/null; then
    echo "FAIL: mux exited before ping" >&2
    cat /tmp/hypa-mux.log >&2 || true
    exit 1
  fi
  i=$((i + 1))
  sleep 1
done
if [ "$ready" -ne 1 ]; then
  echo "FAIL: ping did not succeed" >&2
  cat /tmp/hypa-ping.err >&2 || true
  cat /tmp/hypa-mux.log >&2 || true
  exit 1
fi
echo "ping: $(cat /tmp/hypa-ping.out)"
kill "$mux_pid" 2>/dev/null || true
wait "$mux_pid" 2>/dev/null || true

echo "==> doctor"
doctor_out="$(hypa doctor 2>&1)" || {
  echo "FAIL: doctor exited non-zero" >&2
  printf '%s\n' "$doctor_out" >&2
  exit 1
}
if printf '%s\n' "$doctor_out" | grep -i '\[fail\]' >/dev/null 2>&1; then
  echo "FAIL: doctor reported a failure" >&2
  printf '%s\n' "$doctor_out" >&2
  exit 1
fi
if printf '%s\n' "$doctor_out" | grep -F 'check failed' >/dev/null 2>&1 \
  && printf '%s\n' "$doctor_out" | grep -i 'ssl' >/dev/null 2>&1; then
  echo "FAIL: doctor update check failed with an SSL warning" >&2
  printf '%s\n' "$doctor_out" >&2
  exit 1
fi
printf '%s\n' "$doctor_out"
echo "PASS: clean install checks"
EOF

failed=0
for image in "${IMAGES[@]}"; do
  family=apt
  case "$image" in
    fedora:*) family=dnf ;;
  esac
  echo "======== $image ========"
  if docker_cmd run --rm \
    -e HYPA_PKG_FAMILY="$family" \
    -v "$ARCHIVE:/opt/hypa-archive.tar.gz:ro" \
    -v "$INSTALL_SH:/opt/install.sh:ro" \
    -v "$INNER:/opt/check.sh:ro" \
    "$image" \
    sh /opt/check.sh
  then
    echo "PASS $image"
  else
    echo "FAIL $image"
    failed=1
  fi
done

if [[ "$failed" -ne 0 ]]; then
  exit 1
fi

echo "PASS: clean-install matrix"
