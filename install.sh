#!/usr/bin/env sh
set -eu

# F1 Unix installer. Product PATH name is hypa.
# Honour HYPA_ARCHIVE / --from-archive for a local pack (CI).
# Honour HYPA_INSTALL_DIR, HYPA_REPO, HYPA_APP_DIR, HYPA_VERSION, HYPA_SHA256SUMS.
# Honour HYPA_CHANNEL / --channel f1|f2. Default channel is f1 (hypa-$rid.tar.gz).
# Channel f2 fetches hypa-f2-$rid.tar.gz and requires Ghostty, NOTICE,
# PIN.md, and hypa.channel token f2. Checksums come from SHA256SUMS-F2.
# F2 ship matrix is linux-arm64, osx-arm64, and osx-x64.
# linux-x64 Ghostty AOT is a residual. Channel f2 fails closed on that RID.
# F1 install into HYPA_APP_DIR removes leftover hypa.channel.
# Unix F1 keeps libghostty-vt.
# After extract, fail closed if a required sibling is missing.
# Required files include hypa, hypa-attach, hypa-annotate, and hypa-runtime.
# Also require SQLite native, hypa-pty-host, and Unix libghostty-vt.
# Do not treat hypa-runtime + hypa-runtime-cli as an F1 pack.
# hypa-attach, hypa-annotate, and hypa-runtime are siblings, not PATH names.
# User command stays hypa / hypa attach / hypa mux serve.

repo="${HYPA_REPO:-Hypabolic/Hypa}"
bin_dir="${HYPA_INSTALL_DIR:-$HOME/.local/bin}"
app_dir_override="${HYPA_APP_DIR:-}"
app_dir="${HYPA_APP_DIR:-$HOME/.local/share/hypa}"
archive="${HYPA_ARCHIVE:-}"
checksums_override="${HYPA_SHA256SUMS:-}"
version="${HYPA_VERSION:-latest}"
channel="${HYPA_CHANNEL:-f1}"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "error: required command '$1' was not found" >&2
    exit 1
  fi
}

# Keep this text identical to LinuxOpenSsl3Requirement.MissingMessage.
# ELF class is byte 5. e_machine is bytes 19 and 20, little-endian.
# 2 62 is ELF64 EM_X86_64. 2 183 is ELF64 EM_AARCH64. 1 3 is ELF32 EM_386.
elf_id() {
  _elf_file=$1
  if [ ! -f "$_elf_file" ]; then
    return 1
  fi
  _elf_id=$(od -An -t u1 -N 20 "$_elf_file" 2>/dev/null | awk '
    {
      for (i = 1; i <= NF; i++) {
        n++
        b[n] = $i + 0
      }
    }
    END {
      if (n < 20) exit 1
      if (b[1] != 127 || b[2] != 69 || b[3] != 76 || b[4] != 70) exit 1
      printf "%d %d\n", b[5], b[19] + (b[20] * 256)
    }') || return 1
  if [ -z "$_elf_id" ]; then
    return 1
  fi
  printf '%s\n' "$_elf_id"
}

process_elf_id() {
  if [ -r /proc/self/exe ]; then
    elf_id /proc/self/exe && return 0
  fi
  case "$(uname -m)" in
    x86_64|amd64) printf '%s\n' "2 62" ;;
    aarch64|arm64) printf '%s\n' "2 183" ;;
    i386|i686|x86) printf '%s\n' "1 3" ;;
    *) return 1 ;;
  esac
}

openssl3_file_matches() {
  _lib=$1
  _want=$2
  if [ ! -f "$_lib" ]; then
    return 1
  fi
  _got=$(elf_id "$_lib") || return 1
  [ "$_got" = "$_want" ]
}

openssl3_soname_present() {
  _soname=$1
  _want=$2
  if command -v ldconfig >/dev/null 2>&1; then
    _paths=$(ldconfig -p 2>/dev/null | awk -v soname="$_soname" '
      {
        line = $0
        sub(/^[[:space:]]+/, "", line)
        if (index(line, soname) != 1) next
        rest = substr(line, length(soname) + 1, 1)
        if (rest != "" && rest != " " && rest != "\t" && rest != "(") next
        arrow = index(line, "=>")
        if (arrow == 0) next
        path = substr(line, arrow + 2)
        sub(/^[[:space:]]+/, "", path)
        sub(/[[:space:]]+$/, "", path)
        if (path != "") print path
      }')
    _old_ifs=$IFS
    IFS='
'
    for _path in $_paths; do
      if openssl3_file_matches "$_path" "$_want"; then
        IFS=$_old_ifs
        return 0
      fi
    done
    IFS=$_old_ifs
  fi
  for _dir in /lib /usr/lib /lib64 /usr/lib64 \
    /usr/lib/x86_64-linux-gnu /lib/x86_64-linux-gnu \
    /usr/lib/aarch64-linux-gnu /lib/aarch64-linux-gnu
  do
    if openssl3_file_matches "$_dir/$_soname" "$_want"; then
      return 0
    fi
  done
  return 1
}

require_linux_openssl3() {
  if [ "$os" != "linux" ]; then
    return 0
  fi
  _elf=$(process_elf_id) || {
    echo "error: cannot determine the process ELF class." >&2
    exit 1
  }
  if openssl3_soname_present libssl.so.3 "$_elf" && openssl3_soname_present libcrypto.so.3 "$_elf"; then
    return 0
  fi
  echo "error: OpenSSL 3 is required. libssl.so.3 and libcrypto.so.3 were not found." >&2
  echo "Install OpenSSL 3 with one of these commands:" >&2
  echo "  apt-get install -y libssl3" >&2
  echo "  dnf install -y openssl-libs" >&2
  echo "  zypper install -y libopenssl3" >&2
  exit 1
}

ca_bundle_present() {
  for _path in \
    /etc/ssl/certs/ca-certificates.crt \
    /etc/pki/tls/certs/ca-bundle.crt \
    /etc/ssl/ca-bundle.pem \
    /etc/ssl/cert.pem
  do
    if [ -f "$_path" ]; then
      return 0
    fi
  done
  return 1
}

require_linux_ca_bundle() {
  if [ "$os" != "linux" ]; then
    return 0
  fi
  if ca_bundle_present; then
    return 0
  fi
  echo "error: a CA trust bundle is required. None of these files were found:" >&2
  echo "  /etc/ssl/certs/ca-certificates.crt" >&2
  echo "  /etc/pki/tls/certs/ca-bundle.crt" >&2
  echo "  /etc/ssl/ca-bundle.pem" >&2
  echo "  /etc/ssl/cert.pem" >&2
  echo "Install CA certificates with one of these commands:" >&2
  echo "  apt-get install -y ca-certificates" >&2
  echo "  dnf install -y ca-certificates" >&2
  echo "  zypper install -y ca-certificates" >&2
  exit 1
}

require_unix_ghostty() {
  _dir="$1"
  _label="$2"
  case "$rid" in
    linux-*)
      if [ ! -f "$_dir/libghostty-vt.so" ]; then
        echo "error: $_label is missing libghostty-vt.so beside hypa" >&2
        exit 1
      fi
      if [ -f "$_dir/libghostty-vt.dylib" ]; then
        echo "error: linux pack must not include libghostty-vt.dylib" >&2
        exit 1
      fi
      ;;
    osx-*)
      if [ ! -f "$_dir/libghostty-vt.dylib" ]; then
        echo "error: $_label is missing libghostty-vt.dylib beside hypa" >&2
        exit 1
      fi
      if [ -f "$_dir/libghostty-vt.so" ]; then
        echo "error: osx pack must not include libghostty-vt.so" >&2
        exit 1
      fi
      ;;
    *)
      echo "error: Unix Ghostty check has no map for '$rid'" >&2
      exit 1
      ;;
  esac
}

usage() {
  echo "Usage: $0 [version] [--from-archive PATH] [--channel f1|f2]" >&2
  echo "  HYPA_ARCHIVE, HYPA_INSTALL_DIR, HYPA_APP_DIR, HYPA_REPO, HYPA_VERSION, HYPA_SHA256SUMS, HYPA_CHANNEL" >&2
  exit 2
}

while [ $# -gt 0 ]; do
  case "$1" in
    --from-archive)
      if [ $# -lt 2 ]; then
        echo "error: --from-archive requires a path" >&2
        usage
      fi
      archive="$2"
      shift 2
      ;;
    --channel)
      if [ $# -lt 2 ]; then
        echo "error: --channel requires f1 or f2" >&2
        usage
      fi
      channel="$2"
      shift 2
      ;;
    -h|--help)
      usage
      ;;
    --)
      shift
      break
      ;;
    -*)
      echo "error: unknown option '$1'" >&2
      usage
      ;;
    *)
      version="$1"
      shift
      ;;
  esac
done

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
require_linux_openssl3
require_linux_ca_bundle
if [ "$channel" != "f1" ] && [ "$channel" != "f2" ]; then
  echo "error: --channel / HYPA_CHANNEL must be f1 or f2 (got '$channel')" >&2
  exit 2
fi
if [ "$channel" = "f2" ]; then
  case "$rid" in
    linux-arm64|osx-arm64|osx-x64) ;;
    linux-x64)
      echo "error: F2 channel does not ship linux-x64." >&2
      echo "error: linux-x64 Ghostty AOT is the H-13 residual (Zig fetch)." >&2
      echo "error: F2 ship matrix is linux-arm64, osx-arm64, and osx-x64." >&2
      exit 1
      ;;
    *)
      echo "error: F2 channel does not ship '$rid'." >&2
      echo "error: F2 ship matrix is linux-arm64, osx-arm64, and osx-x64." >&2
      exit 1
      ;;
  esac
  asset="hypa-f2-$rid.tar.gz"
else
  asset="hypa-$rid.tar.gz"
fi

if [ -n "$archive" ]; then
  case "$archive" in
    /*) ;;
    *) archive="$(pwd)/$archive" ;;
  esac
  if [ ! -f "$archive" ]; then
    echo "error: HYPA_ARCHIVE / --from-archive is not a file: $archive" >&2
    exit 1
  fi
  asset="$(basename "$archive")"
fi

if [ -z "$archive" ]; then
  if [ "$version" = "latest" ]; then
    release_url="https://github.com/$repo/releases/latest/download"
  else
    case "$version" in
      v*) tag="$version" ;;
      *) tag="v$version" ;;
    esac
    release_url="https://github.com/$repo/releases/download/$tag"
  fi
fi
if [ "$channel" = "f2" ]; then
  checksum_asset="SHA256SUMS-F2"
else
  checksum_asset="SHA256SUMS"
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

verify_checksum() {
  _sums="$1"
  _file="$2"
  _name="$3"
  expected="$(awk -v asset="$_name" '$2 == asset { print $1 }' "$_sums")"
  if [ -z "$expected" ]; then
    echo "error: checksum for $_name was not found in SHA256SUMS" >&2
    exit 1
  fi

  if command -v sha256sum >/dev/null 2>&1; then
    actual="$(sha256sum "$_file" | awk '{ print $1 }')"
  elif command -v shasum >/dev/null 2>&1; then
    actual="$(shasum -a 256 "$_file" | awk '{ print $1 }')"
  else
    echo "error: sha256sum or shasum is required" >&2
    exit 1
  fi

  if [ "$expected" != "$actual" ]; then
    echo "error: checksum verification failed for $_name" >&2
    exit 1
  fi
}

if [ -n "$archive" ]; then
  archive_file="$archive"
  if [ -n "$checksums_override" ]; then
    if [ ! -f "$checksums_override" ]; then
      echo "error: HYPA_SHA256SUMS is not a file: $checksums_override" >&2
      exit 1
    fi
    verify_checksum "$checksums_override" "$archive_file" "$asset"
  fi
else
  checksum_file="$tmp_dir/$checksum_asset"
  archive_file="$tmp_dir/$asset"
  download "$release_url/$checksum_asset" "$checksum_file"
  download "$release_url/$asset" "$archive_file"
  verify_checksum "$checksum_file" "$archive_file" "$asset"
fi

extract_dir="$tmp_dir/extract"
mkdir -p "$extract_dir"
tar -xzf "$archive_file" -C "$extract_dir"

find_named() {
  find "$extract_dir" -type f -name "$1" | head -n 1
}

package_dir="$(find "$extract_dir" -type f -name hypa -exec dirname {} \; | head -n 1)"
runtime_bin="$(find_named hypa-runtime)"
runtime_cli="$(find_named hypa-runtime-cli)"

if [ -z "$package_dir" ]; then
  if [ -n "$runtime_bin" ] && [ -n "$runtime_cli" ]; then
    echo "error: two-binary archive (hypa-runtime + hypa-runtime-cli) is not an F1 pack" >&2
    echo "error: F1 requires hypa, hypa-attach, libe_sqlite3, hypa-pty-host, and libghostty-vt" >&2
    exit 1
  fi
  echo "error: hypa executable was not found in $asset" >&2
  exit 1
fi

sqlite=""
if [ -f "$package_dir/libe_sqlite3.so" ]; then
  sqlite="$package_dir/libe_sqlite3.so"
elif [ -f "$package_dir/libe_sqlite3.dylib" ]; then
  sqlite="$package_dir/libe_sqlite3.dylib"
else
  sqlite="$(find "$package_dir" \( -name 'libe_sqlite3.so' -o -name 'libe_sqlite3.dylib' \) | head -n 1)"
fi
if [ -z "$sqlite" ]; then
  echo "error: F1 archive is missing libe_sqlite3.so or libe_sqlite3.dylib" >&2
  exit 1
fi

helper=""
if [ -f "$package_dir/hypa-pty-host" ]; then
  helper="$package_dir/hypa-pty-host"
else
  helper="$(find "$package_dir" -type f -name hypa-pty-host | head -n 1)"
fi
if [ -z "$helper" ]; then
  echo "error: F1 archive is missing hypa-pty-host" >&2
  exit 1
fi

attach=""
if [ -f "$package_dir/hypa-attach" ]; then
  attach="$package_dir/hypa-attach"
else
  attach="$(find "$package_dir" -type f -name hypa-attach | head -n 1)"
fi
if [ -z "$attach" ]; then
  echo "error: F1 archive is missing hypa-attach" >&2
  exit 1
fi

annotate=""
if [ -f "$package_dir/hypa-annotate" ]; then
  annotate="$package_dir/hypa-annotate"
else
  annotate="$(find "$package_dir" -type f -name hypa-annotate | head -n 1)"
fi
if [ -z "$annotate" ]; then
  echo "error: F1 archive is missing hypa-annotate" >&2
  exit 1
fi

runtime=""
if [ -f "$package_dir/hypa-runtime" ]; then
  runtime="$package_dir/hypa-runtime"
else
  runtime="$(find "$package_dir" -type f -name hypa-runtime | head -n 1)"
fi
if [ -z "$runtime" ]; then
  echo "error: F1 archive is missing hypa-runtime" >&2
  exit 1
fi

if [ "$channel" = "f1" ]; then
  require_unix_ghostty "$package_dir" "F1 archive"
else
  require_unix_ghostty "$package_dir" "F2 archive"
fi

mkdir -p "$bin_dir"

if [ -n "$app_dir_override" ]; then
  mkdir -p "$app_dir"
  cp -R "$package_dir"/. "$app_dir/"
else
  # Install into a uniquely-named versioned directory so `hypa update` can
  # atomically swap the $app_dir symlink without a window where $app_dir is absent.
  install_id="$(LC_ALL=C tr -dc 'a-f0-9' < /dev/urandom 2>/dev/null | head -c 16)"
  versioned_dir="${HOME}/.local/share/hypa-${install_id}"
  mkdir -p "$versioned_dir"
  cp -R "$package_dir"/. "$versioned_dir/"

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
fi

if [ "$channel" = "f1" ]; then
  rm -f "$app_dir/hypa.channel"
  if [ -e "$app_dir/hypa.channel" ]; then
    echo "error: F1 install must not leave hypa.channel" >&2
    exit 1
  fi
fi

chmod +x "$app_dir/hypa"
if [ ! -f "$app_dir/hypa-attach" ]; then
  echo "error: hypa-attach was not copied beside hypa" >&2
  exit 1
fi
chmod +x "$app_dir/hypa-attach"
if [ ! -f "$app_dir/hypa-annotate" ]; then
  echo "error: hypa-annotate was not copied beside hypa" >&2
  exit 1
fi
chmod +x "$app_dir/hypa-annotate"
if [ ! -f "$app_dir/hypa-runtime" ]; then
  echo "error: hypa-runtime was not copied beside hypa" >&2
  exit 1
fi
chmod +x "$app_dir/hypa-runtime"
if [ -f "$app_dir/hypa-pty-host" ]; then
  chmod +x "$app_dir/hypa-pty-host"
else
  echo "error: hypa-pty-host was not copied beside hypa" >&2
  exit 1
fi

if [ ! -f "$app_dir/libe_sqlite3.so" ] && [ ! -f "$app_dir/libe_sqlite3.dylib" ]; then
  echo "error: SQLite native was not copied beside hypa" >&2
  exit 1
fi

if [ "$channel" = "f1" ]; then
  require_unix_ghostty "$app_dir" "F1 install"
fi

if [ "$channel" = "f2" ]; then
  require_unix_ghostty "$app_dir" "F2 install"
  if [ ! -f "$app_dir/NOTICE" ]; then
    echo "error: F2 archive is missing NOTICE beside hypa" >&2
    exit 1
  fi
  if [ ! -f "$app_dir/PIN.md" ]; then
    echo "error: F2 archive is missing PIN.md beside hypa" >&2
    exit 1
  fi
  if [ ! -f "$app_dir/hypa.channel" ]; then
    echo "error: F2 archive is missing hypa.channel beside hypa" >&2
    exit 1
  fi
  channel_token="$(tr -d '[:space:]' < "$app_dir/hypa.channel")"
  if [ "$channel_token" != "f2" ]; then
    echo "error: hypa.channel must be the token f2 (got '$channel_token')" >&2
    exit 1
  fi
fi

# A downloaded archive carries com.apple.quarantine. Apple tar copies that
# attribute onto each member. An ad-hoc signed Mach-O with the attribute
# is killed on exec. Record the archive value on the install directory,
# then remove it from the installed files so hypa can run.
if [ "$(uname -s)" = "Darwin" ] && [ -n "$archive" ] && [ -f "$archive" ]; then
  _quarantine=$(xattr -p com.apple.quarantine "$archive" 2>/dev/null || true)
  if [ -n "$_quarantine" ]; then
    xattr -w com.apple.quarantine "$_quarantine" "$app_dir"
  fi
  find "$app_dir" -mindepth 1 -print | while IFS= read -r _item; do
    xattr -d com.apple.quarantine "$_item" 2>/dev/null || true
  done
fi

# Link $HYPA_INSTALL_DIR/hypa. Do not link hypa-runtime as the PATH name.
ln -sfn "$app_dir/hypa" "$bin_dir/hypa"

hypa_data_dir="$HOME/.hypa"
mkdir -p "$hypa_data_dir"
installed_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

# Write install.json with POSIX shell only. Do not call jq or python3.
json_escape() {
  _in=$1
  _out=
  _nl='
'
  _cr=$(printf '\r')
  _tab=$(printf '\t')
  while [ -n "$_in" ]; do
    _ch=${_in%"${_in#?}"}
    _in=${_in#?}
    case "$_ch" in
      \\) _out="${_out}\\\\" ;;
      \") _out="${_out}\\\"" ;;
      "$_nl") _out="${_out}\\n" ;;
      "$_cr") _out="${_out}\\r" ;;
      "$_tab") _out="${_out}\\t" ;;
      *)
        _ord=$(printf '%s' "$_ch" | od -An -t u1 | awk 'NR==1 { print $1; exit }')
        if [ -n "${_ord}" ] && [ "${_ord}" -lt 32 ]; then
          _out="${_out}$(printf '\\u%04x' "${_ord}")"
        else
          _out="${_out}${_ch}"
        fi
        ;;
    esac
  done
  printf '%s' "$_out"
}

write_install_json() {
  _out=$1
  _rid_esc=$(json_escape "$2")
  _dir_esc=$(json_escape "$3")
  _link_esc=$(json_escape "$4")
  _exec_esc=$(json_escape "$5")
  _at_esc=$(json_escape "$6")
  {
    printf '%s\n' '{'
    printf '%s\n' '  "source": "script",'
    printf '  "runtime_identifier": "%s",\n' "$_rid_esc"
    printf '  "install_directory": "%s",\n' "$_dir_esc"
    printf '  "bin_link_path": "%s",\n' "$_link_esc"
    printf '  "executable_path": "%s",\n' "$_exec_esc"
    printf '%s\n' '  "installed_version": null,'
    printf '  "installed_at": "%s"\n' "$_at_esc"
    printf '%s\n' '}'
  } > "$_out"
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
