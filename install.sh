#!/usr/bin/env sh
set -eu

version="${1:-latest}"
repo="matt-gribben/Hypa"
bin_dir="$HOME/.local/bin"
app_dir="$HOME/.local/share/hypa"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "error: required command '$1' was not found" >&2
    exit 1
  fi
}

case "$(uname -s)" in
  Linux) os="linux" ;;
  Darwin) os="osx" ;;
  *)
    echo "error: unsupported OS '$(uname -s)'" >&2
    exit 1
    ;;
esac

case "$(uname -m)" in
  x86_64|amd64) arch="x64" ;;
  arm64|aarch64) arch="arm64" ;;
  *)
    echo "error: unsupported architecture '$(uname -m)'" >&2
    exit 1
    ;;
esac

rid="$os-$arch"
asset="hypa-$rid.tar.gz"

if [ "$version" = "latest" ]; then
  release_url="https://github.com/$repo/releases/latest/download"
else
  case "$version" in
    v*) tag="$version" ;;
    *) tag="v$version" ;;
  esac
  release_url="https://github.com/$repo/releases/download/$tag"
fi

tmp_dir="$(mktemp -d)"
cleanup() {
  rm -rf "$tmp_dir"
}
trap cleanup EXIT INT TERM

download() {
  url="$1"
  output="$2"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$url" -o "$output"
  elif command -v wget >/dev/null 2>&1; then
    wget -q "$url" -O "$output"
  else
    echo "error: curl or wget is required" >&2
    exit 1
  fi
}

checksum_file="$tmp_dir/SHA256SUMS"
archive_file="$tmp_dir/$asset"

download "$release_url/SHA256SUMS" "$checksum_file"
download "$release_url/$asset" "$archive_file"

expected="$(awk -v asset="$asset" '$2 == asset { print $1 }' "$checksum_file")"
if [ -z "$expected" ]; then
  echo "error: checksum for $asset was not found in SHA256SUMS" >&2
  exit 1
fi

if command -v sha256sum >/dev/null 2>&1; then
  actual="$(sha256sum "$archive_file" | awk '{ print $1 }')"
elif command -v shasum >/dev/null 2>&1; then
  actual="$(shasum -a 256 "$archive_file" | awk '{ print $1 }')"
else
  echo "error: sha256sum or shasum is required" >&2
  exit 1
fi

if [ "$expected" != "$actual" ]; then
  echo "error: checksum verification failed for $asset" >&2
  exit 1
fi

extract_dir="$tmp_dir/extract"
mkdir -p "$extract_dir"
tar -xzf "$archive_file" -C "$extract_dir"

package_dir="$(find "$extract_dir" -type f -name hypa -exec dirname {} \; | head -n 1)"
if [ -z "$package_dir" ]; then
    echo "error: hypa executable was not found in $asset" >&2
    exit 1
fi

mkdir -p "$app_dir" "$bin_dir"
cp -R "$package_dir"/. "$app_dir/"
chmod +x "$app_dir/hypa"
ln -sfn "$app_dir/hypa" "$bin_dir/hypa"

echo "installed hypa files to $app_dir"
echo "linked hypa to $bin_dir/hypa"

case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *)
    echo "warning: $bin_dir is not on PATH" >&2
    ;;
esac
