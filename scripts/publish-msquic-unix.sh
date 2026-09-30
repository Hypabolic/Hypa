#!/bin/sh
# Copy a fetched Unix MsQuic library into a publish or build output directory.
# Write a SHA-256 sidecar for each copied file.
set -eu

if [ "$#" -lt 2 ]; then
  echo "Usage: publish-msquic-unix.sh SOURCE_DIR DEST_DIR" >&2
  exit 2
fi

SOURCE_DIR="$1"
DEST_DIR="$2"
mkdir -p "$DEST_DIR"

write_sha256() {
  shasum -a 256 "$1" | awk '{print $1}' | tr -d '\n' > "$1.sha256"
}

copied=0
copied_linux_msquic=0
for name in libmsquic.so libmsquic.so.2 libmsquic.so.2.6.1 libmsquic.dylib libmsquic.2.dylib; do
  src="$SOURCE_DIR/$name"
  if [ ! -f "$src" ]; then
    continue
  fi
  dest="$DEST_DIR/$name"
  cp "$src" "$dest"
  chmod 644 "$dest"
  case "$name" in
    libmsquic.so*)
      if ! command -v patchelf >/dev/null 2>&1; then
        echo "publish-msquic-unix: patchelf is required to set RUNPATH \$ORIGIN on $name" >&2
        exit 1
      fi
      # Literal $ORIGIN. The loader resolves it beside libmsquic.
      # shellcheck disable=SC2016
      patchelf --set-rpath '$ORIGIN' "$dest"
      copied_linux_msquic=1
      ;;
  esac
  write_sha256 "$dest"
  copied=$((copied + 1))
done

if [ "$copied" -eq 0 ]; then
  echo "publish-msquic-unix: no libmsquic files in $SOURCE_DIR" >&2
  exit 1
fi

if [ "$copied_linux_msquic" -eq 1 ]; then
  numa_src="$SOURCE_DIR/libnuma.so.1"
  if [ ! -f "$numa_src" ]; then
    echo "publish-msquic-unix: libnuma.so.1 is missing from $SOURCE_DIR" >&2
    exit 1
  fi
  numa_dest="$DEST_DIR/libnuma.so.1"
  cp "$numa_src" "$numa_dest"
  chmod 644 "$numa_dest"
  write_sha256 "$numa_dest"
fi

# Homebrew libmsquic links OpenSSL by absolute cellar path.
# Copy that file beside the library so a Mac host does not need Homebrew at run time.
if [ "$(uname -s)" = "Darwin" ]; then
  dylib=""
  for name in libmsquic.dylib libmsquic.2.dylib; do
    if [ -f "$DEST_DIR/$name" ]; then
      dylib="$DEST_DIR/$name"
      break
    fi
  done
  if [ -n "$dylib" ]; then
    crypto_dep="$(otool -L "$dylib" | awk '/libcrypto/{print $1; exit}')"
    if [ -n "$crypto_dep" ] && [ -f "$crypto_dep" ]; then
      cp "$crypto_dep" "$DEST_DIR/libcrypto.3.dylib"
      chmod 644 "$DEST_DIR/libcrypto.3.dylib"
      write_sha256 "$DEST_DIR/libcrypto.3.dylib"
      for name in libmsquic.dylib libmsquic.2.dylib; do
        if [ ! -f "$DEST_DIR/$name" ]; then
          continue
        fi
        install_name_tool -change "$crypto_dep" "@loader_path/libcrypto.3.dylib" "$DEST_DIR/$name"
        if [ "$name" = "libmsquic.2.dylib" ]; then
          install_name_tool -id "libmsquic.2.dylib" "$DEST_DIR/$name"
        else
          install_name_tool -id "libmsquic.dylib" "$DEST_DIR/$name"
        fi
        write_sha256 "$DEST_DIR/$name"
      done
    fi
  fi
fi
