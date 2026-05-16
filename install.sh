#!/usr/bin/env sh
set -eu

version="${1:-latest}"
repo="Hypabolic/Hypa"
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

mkdir -p "$bin_dir"

# Install into a uniquely-named versioned directory so `hypa update` can
# atomically swap the $app_dir symlink without a window where $app_dir is absent.
install_id="$(LC_ALL=C tr -dc 'a-f0-9' < /dev/urandom 2>/dev/null | head -c 16)"
versioned_dir="${HOME}/.local/share/hypa-${install_id}"
mkdir -p "$versioned_dir"
cp -R "$package_dir"/. "$versioned_dir/"
chmod +x "$versioned_dir/hypa"

# Point the stable $app_dir symlink at the versioned dir.
# If $app_dir already exists as a real directory (old-format install), rename it
# aside first so the atomic symlink rename does not fail.
if [ -d "$app_dir" ] && [ ! -L "$app_dir" ]; then
    _old_app="${app_dir}.old.$(od -An -N3 -tx1 /dev/urandom | tr -d ' \n')"
    mv "$app_dir" "$_old_app"
    ln -sfn "$versioned_dir" "${app_dir}.new"
    mv -f "${app_dir}.new" "$app_dir"
    rm -rf "$_old_app"
else
    ln -sfn "$versioned_dir" "${app_dir}.new"
    mv -f "${app_dir}.new" "$app_dir"
fi

ln -sfn "$app_dir/hypa" "$bin_dir/hypa"

hypa_data_dir="$HOME/.hypa"
mkdir -p "$hypa_data_dir"
installed_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

# Write install.json with proper JSON escaping.
# jq is preferred; python3 is a reliable fallback on any modern system.
write_install_json() {
  _out="$1"; _rid="$2"; _install_dir="$3"; _bin_link="$4"; _exec_path="$5"; _at="$6"
  if command -v jq >/dev/null 2>&1; then
    jq -n \
      --arg source           "script" \
      --arg rid              "$_rid" \
      --arg install_directory "$_install_dir" \
      --arg bin_link_path    "$_bin_link" \
      --arg executable_path  "$_exec_path" \
      --arg installed_at     "$_at" \
      '{source:$source,runtime_identifier:$rid,install_directory:$install_directory,bin_link_path:$bin_link_path,executable_path:$executable_path,installed_version:null,installed_at:$installed_at}' \
      > "$_out"
  elif command -v python3 >/dev/null 2>&1; then
    python3 -c "
import json, sys
a = sys.argv[1:]
print(json.dumps({'source':'script','runtime_identifier':a[0],'install_directory':a[1],'bin_link_path':a[2],'executable_path':a[3],'installed_version':None,'installed_at':a[4]},indent=2))
" "$_rid" "$_install_dir" "$_bin_link" "$_exec_path" "$_at" > "$_out"
  else
    echo "warning: neither jq nor python3 found; install metadata not written" >&2
    echo "warning: 'hypa update' may not work correctly without install metadata" >&2
  fi
}

write_install_json \
  "$hypa_data_dir/install.json" \
  "$rid" "$app_dir" "$bin_dir/hypa" "$app_dir/hypa" "$installed_at"

echo "installed hypa files to $app_dir"
echo "linked hypa to $bin_dir/hypa"

case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *)
    echo "warning: $bin_dir is not on PATH" >&2
    ;;
esac
