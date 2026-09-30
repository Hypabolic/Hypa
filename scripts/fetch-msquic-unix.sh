#!/bin/sh
# Fetch a pinned official MsQuic native library for one Unix RID.
# Linux uses the Microsoft Debian package. macOS uses the Homebrew formula.
# Linux also fetches a pinned Debian libnuma1 package. libmsquic needs
# libnuma.so.1. The pack ships that file. The host does not install it.
set -eu

ROOT="$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)"
RID="${1:-}"
if [ -z "$RID" ]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) RID=linux-x64 ;;
    Linux-aarch64) RID=linux-arm64 ;;
    Darwin-x86_64) RID=osx-x64 ;;
    Darwin-arm64) RID=osx-arm64 ;;
    *)
      echo "fetch-msquic-unix: pass a RID (linux-x64, linux-arm64, osx-x64, osx-arm64)" >&2
      exit 2
      ;;
  esac
fi

OUT="$ROOT/native/msquic/$RID"
mkdir -p "$OUT"

linux_rid=0
case "$RID" in
  linux-*) linux_rid=1 ;;
esac

have_msquic=0
if [ -f "$OUT/libmsquic.so" ] || [ -f "$OUT/libmsquic.dylib" ]; then
  have_msquic=1
fi
# Linux always re-checks libnuma against the pinned package.
# An existing libnuma.so.1 is not enough. A stale file must not ship.
if [ "$have_msquic" -eq 1 ] && [ "$linux_rid" -eq 0 ]; then
  echo "fetch-msquic-unix: ready $OUT"
  exit 0
fi

extract_deb_data() {
  deb="$1"
  dest="$2"
  rm -rf "$dest"
  mkdir -p "$dest"
  python3 - "$deb" "$dest" <<'PY'
import sys, tarfile, io
from pathlib import Path
deb = Path(sys.argv[1])
dest = Path(sys.argv[2])
data = deb.read_bytes()
# Debian ar: 8-byte magic, then 60-byte headers.
offset = 8
payload = None
name = ""
while offset + 60 <= len(data):
    header = data[offset:offset + 60]
    name = header[0:16].decode("ascii", "replace").strip()
    size = int(header[48:58].decode("ascii").strip())
    offset += 60
    body = data[offset:offset + size]
    offset += size + (size % 2)
    if name.startswith("data.tar"):
        payload = body
        break
if payload is None:
    raise SystemExit("data.tar member is missing")
mode = "r:xz"
if name.endswith(".gz"):
    mode = "r:gz"
elif name.endswith(".bz2"):
    mode = "r:bz2"
with tarfile.open(fileobj=io.BytesIO(payload), mode=mode) as tar:
    tar.extractall(dest, filter="data")
PY
}

fetch_linux_deb() {
  arch="$1"
  sha="$2"
  url="https://packages.microsoft.com/debian/12/prod/pool/main/libm/libmsquic/libmsquic_2.6.1_${arch}.deb"
  deb="$OUT/libmsquic_2.6.1_${arch}.deb"
  if [ ! -f "$deb" ]; then
    curl -fsSL -o "$deb" "$url"
  fi
  actual="$(shasum -a 256 "$deb" | awk '{print $1}')"
  if [ "$actual" != "$sha" ]; then
    echo "fetch-msquic-unix: digest mismatch for $deb" >&2
    echo "expected $sha" >&2
    echo "actual   $actual" >&2
    exit 1
  fi
  work="$OUT/extract"
  extract_deb_data "$deb" "$work"
  so="$(find "$work" -name 'libmsquic.so.2.6.1' -type f | head -n 1)"
  if [ -z "$so" ]; then
    echo "fetch-msquic-unix: libmsquic.so.2.6.1 is missing from the package" >&2
    exit 1
  fi
  cp "$so" "$OUT/libmsquic.so.2.6.1"
  cp "$so" "$OUT/libmsquic.so.2"
  cp "$so" "$OUT/libmsquic.so"
}

# Debian 11 libnuma1. Its GLIBC_ symbol versions stay at or below 2.17.
fetch_libnuma_deb() {
  sha="$1"
  deb_name="$2"
  url="https://deb.debian.org/debian/pool/main/n/numactl/${deb_name}"
  deb="$OUT/$deb_name"
  if [ ! -f "$deb" ]; then
    curl -fsSL -o "$deb" "$url"
  fi
  actual="$(shasum -a 256 "$deb" | awk '{print $1}')"
  if [ "$actual" != "$sha" ]; then
    echo "fetch-msquic-unix: digest mismatch for $deb" >&2
    echo "expected $sha" >&2
    echo "actual   $actual" >&2
    exit 1
  fi
  work="$OUT/numa-extract"
  extract_deb_data "$deb" "$work"
  so="$(find "$work" -name 'libnuma.so.1.*' -type f | head -n 1)"
  if [ -z "$so" ]; then
    echo "fetch-msquic-unix: libnuma.so.1 is missing from the package" >&2
    exit 1
  fi
  cp "$so" "$OUT/libnuma.so.1"
  chmod 644 "$OUT/libnuma.so.1"
}

fetch_macos_brew() {
  if ! command -v brew >/dev/null 2>&1; then
    echo "fetch-msquic-unix: Homebrew is required for macOS MsQuic" >&2
    exit 1
  fi
  export HOMEBREW_NO_AUTO_UPDATE=1
  if [ ! -f /usr/local/opt/libmsquic/lib/libmsquic.dylib ] \
    && [ ! -f /opt/homebrew/opt/libmsquic/lib/libmsquic.dylib ]; then
    brew install libmsquic
  fi
  prefix="$(brew --prefix libmsquic)"
  dylib="$prefix/lib/libmsquic.dylib"
  if [ ! -f "$dylib" ]; then
    echo "fetch-msquic-unix: $dylib is missing after brew install" >&2
    exit 1
  fi
  cp "$dylib" "$OUT/libmsquic.dylib"
  if [ -f "$prefix/lib/libmsquic.2.dylib" ]; then
    cp "$prefix/lib/libmsquic.2.dylib" "$OUT/libmsquic.2.dylib"
  else
    cp "$dylib" "$OUT/libmsquic.2.dylib"
  fi
}

case "$RID" in
  linux-x64)
    if [ "$have_msquic" -eq 0 ]; then
      fetch_linux_deb amd64 1bdfc12ac97c572f6d511dfdedd9e49cbef17be58a189d9d633ca94e143df275
    fi
    fetch_libnuma_deb 5a0d21a96ec7a5d50e0c2352ac086dde7dd9cd6018f80f2a74ec6fd4dd47b4bf \
      libnuma1_2.0.12-1+b1_amd64.deb
    ;;
  linux-arm64)
    if [ "$have_msquic" -eq 0 ]; then
      fetch_linux_deb arm64 c04695aa3ade68ccfb378e1aa09dcb32be32b452ba8fcfa1c55ee1dc4965a641
    fi
    fetch_libnuma_deb 4eda519ae1f36f6376380fb2798ca0f50e104930845d8c51561ec455e98c57fc \
      libnuma1_2.0.12-1+b1_arm64.deb
    ;;
  osx-x64|osx-arm64)
    if [ "$have_msquic" -eq 0 ]; then
      fetch_macos_brew
    fi
    ;;
  *)
    echo "fetch-msquic-unix: unsupported RID $RID" >&2
    exit 2
    ;;
esac

echo "fetch-msquic-unix: ready $OUT"
