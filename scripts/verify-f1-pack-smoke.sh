#!/usr/bin/env bash
# fail-closed inventory for an F1 hypa / hypa-runtime pack.
#
# Accepts a publish directory or a .tar.gz / .tgz / .zip archive.
# Product --name hypa requires the hypa binary. The hypa-runtime name
# is an internal debug alias and must be requested explicitly.
# F1 also requires the SQLite native and (on Unix) an executable
# hypa-pty-host. Unix F1 requires libghostty-vt. F1 forbids hypa.channel.
# A raw AOT publish directory is not an F2-shaped Ghostty bundle: require
# the RID lib (and sidecar hash when that file is present). NOTICE, PIN,
# license, ABI, and SBOM are required on a packed archive. The packer
# copies those docs from native/ghostty into the stage tree.
#
# Usage:
#   scripts/verify-f1-pack-smoke.sh [--rid RID] [--channel f1] [--name hypa|hypa-runtime] [--profile mux-release|full] <dir-or-archive>
#
# and must call this script. Do not copy only two binaries.

set -euo pipefail

RID=""
CHANNEL="f1"
NAME="hypa"
PROFILE="mux-release"
INPUT=""

usage() {
  echo "Usage: $0 [--rid RID] [--channel f1|f2] [--name hypa|hypa-runtime] [--profile mux-release|full] <dir-or-archive>" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      [[ $# -ge 2 ]] || usage
      RID="$2"
      shift 2
      ;;
    --channel)
      [[ $# -ge 2 ]] || usage
      CHANNEL="$2"
      shift 2
      ;;
    --name)
      [[ $# -ge 2 ]] || usage
      NAME="$2"
      shift 2
      ;;
    --profile)
      [[ $# -ge 2 ]] || usage
      PROFILE="$2"
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
      echo "ERROR: unknown option: $1" >&2
      usage
      ;;
    *)
      if [[ -n "$INPUT" ]]; then
        echo "ERROR: extra argument: $1" >&2
        usage
      fi
      INPUT="$1"
      shift
      ;;
  esac
done

if [[ -z "$INPUT" ]]; then
  usage
fi

if [[ "$CHANNEL" != "f1" && "$CHANNEL" != "f2" ]]; then
  echo "ERROR: --channel must be f1 or f2 (got '$CHANNEL')" >&2
  exit 2
fi

if [[ "$NAME" != "hypa" && "$NAME" != "hypa-runtime" ]]; then
  echo "ERROR: --name must be hypa or hypa-runtime (got '$NAME')" >&2
  exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib-mux-release-profile.sh
source "$SCRIPT_DIR/lib-mux-release-profile.sh"
mux_profile_assert_known "$PROFILE" || exit $?

if [[ ! -e "$INPUT" ]]; then
  echo "ERROR: pack path does not exist: $INPUT" >&2
  exit 1
fi

CLEANUP_DIR=""
EXERCISE_PREFIX=""
cleanup() {
  if [[ -n "$CLEANUP_DIR" && -d "$CLEANUP_DIR" ]]; then
    rm -rf "$CLEANUP_DIR"
  fi
  if [[ -n "$EXERCISE_PREFIX" && -d "$EXERCISE_PREFIX" ]]; then
    rm -rf "$EXERCISE_PREFIX"
  fi
}
trap cleanup EXIT

ROOT=""
IS_ARCHIVE=0
if [[ -d "$INPUT" ]]; then
  ROOT="$INPUT"
else
  IS_ARCHIVE=1
  CLEANUP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/hypa-f1-pack-smoke.XXXXXX")"
  case "$INPUT" in
    *.tar.gz|*.tgz)
      tar -xzf "$INPUT" -C "$CLEANUP_DIR"
      ;;
    *.zip)
      if command -v unzip >/dev/null 2>&1; then
        unzip -q "$INPUT" -d "$CLEANUP_DIR"
      else
        tar -xf "$INPUT" -C "$CLEANUP_DIR"
      fi
      ;;
    *)
      echo "ERROR: unsupported archive type (use a directory, .tar.gz, or .zip): $INPUT" >&2
      exit 1
      ;;
  esac
  # Single top-level directory (hypa-<rid>/) becomes the inventory root.
  entries=()
  while IFS= read -r -d '' entry; do
    entries+=("$entry")
  done < <(find "$CLEANUP_DIR" -mindepth 1 -maxdepth 1 -print0)
  if [[ ${#entries[@]} -eq 1 && -d "${entries[0]}" ]]; then
    ROOT="${entries[0]}"
  else
    ROOT="$CLEANUP_DIR"
  fi
fi

mux_profile_reject_forbidden_binaries "$ROOT" "$PROFILE"

# Regular files only. A symlink inventory entry is not a self-contained pack.
find_name() {
  local name="$1"
  find "$ROOT" -type f -name "$name" -not -path '*.dSYM/*' -print -quit
}

find_symlink() {
  local name="$1"
  find "$ROOT" -type l -name "$name" -not -path '*.dSYM/*' -print -quit
}

exercise_extracted_tree() {
  local host_rid bin help_out st verb prefix
  if [[ "$IS_ARCHIVE" -ne 1 ]]; then
    echo "    exercise=skipped (publish dir)"
    return 0
  fi
  host_rid="$(mux_profile_host_rid)"
  if [[ -z "$host_rid" || "$RID" != "$host_rid" ]]; then
    echo "    exercise=skipped (rid mismatch)"
    return 0
  fi
  if [[ ! -x "$product" ]]; then
    echo "    exercise=skipped (not executable)"
    return 0
  fi

  prefix="$(mktemp -d "${TMPDIR:-/tmp}/hypa-pack-exercise.XXXXXX")"
  EXERCISE_PREFIX="$prefix"
  mkdir -p "$prefix/home" "$prefix/state" "$prefix/data" "$prefix/config" "$prefix/operator"
  export HOME="$prefix/home"
  mux_profile_export_isolation "$prefix"
  unset HYPA_RUNTIME_SOCKET || true
  unset HYPA_RUNTIME_STATE_DIR || true
  unset HYPA_PTY_PROVIDER || true
  unset HYPA_VT_PROVIDER || true

  run_help() {
    local label="$1"
    local path="$2"
    local out st
    set +e
    out="$("$path" --help 2>&1)"
    st=$?
    set -e
    if [[ "$st" -ne 0 ]]; then
      echo "ERROR: $label --help failed (exit $st)" >&2
      printf '%s\n' "$out" >&2
      return 1
    fi
    printf '%s\n' "$out"
  }

  if [[ "$NAME" == "hypa" ]]; then
    run_help hypa "$product" >/dev/null
    if [[ -z "${attach:-}" ]]; then
      echo "ERROR: hypa-attach missing for extracted exercise" >&2
      return 1
    fi
    if [[ ! -x "$attach" ]]; then
      echo "ERROR: hypa-attach is not executable: $attach" >&2
      return 1
    fi
    run_help hypa-attach "$attach" >/dev/null
    if [[ -z "${annotate:-}" ]]; then
      echo "ERROR: hypa-annotate missing for extracted exercise" >&2
      return 1
    fi
    if [[ ! -x "$annotate" ]]; then
      echo "ERROR: hypa-annotate is not executable: $annotate" >&2
      return 1
    fi
    run_help hypa-annotate "$annotate" >/dev/null
    if [[ -z "${runtime:-}" ]]; then
      echo "ERROR: hypa-runtime missing for extracted exercise" >&2
      return 1
    fi
    if [[ ! -x "$runtime" ]]; then
      echo "ERROR: hypa-runtime is not executable: $runtime" >&2
      return 1
    fi
    run_help hypa-runtime "$runtime" >/dev/null
    echo "    exercise=hypa,hypa-attach,hypa-annotate,hypa-runtime"
  else
    run_help hypa-runtime "$product" >/dev/null
    echo "    exercise=hypa-runtime"
  fi
}

infer_rid() {
  if [[ -n "$RID" ]]; then
    return
  fi
  if [[ -n "$(find_name 'hypa.exe')" || -n "$(find_name 'e_sqlite3.dll')" ]]; then
    RID="win-x64"
    return
  fi
  if [[ -n "$(find_name 'libe_sqlite3.dylib')" ]]; then
    RID="osx-x64"
    return
  fi
  if [[ -n "$(find_name 'libe_sqlite3.so')" ]]; then
    RID="linux-x64"
    return
  fi
  case "$(uname -s)" in
    Darwin) RID="osx-x64" ;;
    Linux) RID="linux-x64" ;;
    MINGW*|MSYS*|CYGWIN*) RID="win-x64" ;;
    *)
      echo "ERROR: cannot infer RID; pass --rid" >&2
      exit 1
      ;;
  esac
}

infer_rid

is_windows=0
is_unix=0
case "$RID" in
  win-*) is_windows=1 ;;
  linux-*|osx-*) is_unix=1 ;;
  *)
    echo "ERROR: unknown RID '$RID' (expected linux-*, osx-*, or win-*)" >&2
    exit 1
    ;;
esac
if [[ "$CHANNEL" == "f1" && "$is_windows" -eq 1 ]]; then
  echo "ERROR: Windows is not a Ghostty-only F1 mux RID. No libghostty-vt.dll. Mux cannot start. Do not treat pack-smoke green as a mux ship." >&2
  exit 1
fi

if [[ "$CHANNEL" == "f2" ]]; then
  echo "==> F2 pack smoke"
else
  echo "==> F1 pack smoke"
fi
echo "    path=$INPUT"
echo "    root=$ROOT"
echo "    rid=$RID"
echo "    channel=$CHANNEL"
echo "    name=$NAME"
echo "    profile=$PROFILE"

product=""
if [[ "$NAME" == "hypa" ]]; then
  for name in hypa hypa.exe; do
    hit="$(find_name "$name")"
    if [[ -n "$hit" ]]; then
      product="$hit"
      break
    fi
  done
  if [[ -z "$product" ]]; then
    echo "ERROR: missing product binary (hypa). --name hypa does not accept hypa-runtime." >&2
    find "$ROOT" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
else
  for name in hypa-runtime hypa-runtime.exe; do
    hit="$(find_name "$name")"
    if [[ -n "$hit" ]]; then
      product="$hit"
      break
    fi
  done
  if [[ -z "$product" ]]; then
    echo "ERROR: missing debug-alias binary (hypa-runtime)" >&2
    find "$ROOT" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
  product_hypa=""
  for name in hypa hypa.exe; do
    hit="$(find_name "$name")"
    if [[ -n "$hit" ]]; then
      product_hypa="$hit"
      break
    fi
  done
  if [[ -n "$product_hypa" ]]; then
    echo "ERROR: --name hypa-runtime refuses a pack that holds hypa (debug-alias pack is not the product pack)" >&2
    find "$ROOT" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
fi
echo "    binary=$product"

sqlite=""
case "$RID" in
  linux-*) sqlite="$(find_name 'libe_sqlite3.so')" ;;
  osx-*) sqlite="$(find_name 'libe_sqlite3.dylib')" ;;
  win-*) sqlite="$(find_name 'e_sqlite3.dll')" ;;
esac
if [[ -z "$sqlite" ]]; then
  echo "ERROR: missing SQLite native for RID $RID (libe_sqlite3.so|.dylib or e_sqlite3.dll)" >&2
  find "$ROOT" -maxdepth 2 -type f -print >&2 || true
  exit 1
fi
echo "    sqlite=$sqlite"

if [[ "$is_unix" -eq 1 ]]; then
  helper="$(find_name 'hypa-pty-host')"
  if [[ -z "$helper" ]]; then
    echo "ERROR: missing hypa-pty-host in Unix F1 pack (RID $RID)" >&2
    echo "       A two-binary tarball is not an F1 pack." >&2
    find "$ROOT" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
  if [[ ! -x "$helper" ]]; then
    echo "ERROR: hypa-pty-host is not executable: $helper" >&2
    exit 1
  fi
  echo "    helper=$helper"
else
  echo "    helper=not required on Windows F1"
fi

if [[ "$NAME" == "hypa" ]]; then
  attach=""
  for name in hypa-attach hypa-attach.exe; do
    hit="$(find_name "$name")"
    if [[ -n "$hit" ]]; then
      attach="$hit"
      break
    fi
  done
  if [[ -z "$attach" ]]; then
    echo "ERROR: missing hypa-attach beside hypa (lean attach sibling, not a product brand)" >&2
    find "$ROOT" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
  if [[ "$is_unix" -eq 1 && ! -x "$attach" ]]; then
    echo "ERROR: hypa-attach is not executable: $attach" >&2
    exit 1
  fi
  echo "    attach=$attach"

  annotate=""
  for name in hypa-annotate hypa-annotate.exe; do
    hit="$(find_name "$name")"
    if [[ -n "$hit" ]]; then
      annotate="$hit"
      break
    fi
  done
  if [[ -z "$annotate" ]]; then
    echo "ERROR: missing hypa-annotate beside hypa (annotate sibling, not a product brand)" >&2
    find "$ROOT" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
  if [[ "$is_unix" -eq 1 && ! -x "$annotate" ]]; then
    echo "ERROR: hypa-annotate is not executable: $annotate" >&2
    exit 1
  fi
  echo "    annotate=$annotate"

  runtime=""
  for name in hypa-runtime hypa-runtime.exe; do
    hit="$(find_name "$name")"
    if [[ -n "$hit" ]]; then
      runtime="$hit"
      break
    fi
  done
  if [[ -z "$runtime" ]]; then
    echo "ERROR: missing hypa-runtime beside hypa (lean mux sibling, not a product brand)" >&2
    find "$ROOT" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
  if [[ "$is_unix" -eq 1 && ! -x "$runtime" ]]; then
    echo "ERROR: hypa-runtime is not executable: $runtime" >&2
    exit 1
  fi
  echo "    runtime=$runtime"
fi

file_sha256() {
  local f="$1"
  if [[ -L "$f" ]]; then
    echo "ERROR: refuse to hash symlink $f (Ghostty must be a regular file)" >&2
    exit 1
  fi
  if [[ ! -f "$f" ]]; then
    echo "ERROR: hash input must be a regular file: $f" >&2
    exit 1
  fi
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$f" | awk '{ print $1 }'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$f" | awk '{ print $1 }'
  else
    echo "ERROR: sha256sum or shasum is required to verify libghostty-vt" >&2
    exit 1
  fi
}

ghostty=""
ghostty_wrong=""
ghostty_symlink=""
case "$RID" in
  linux-*)
    ghostty="$(find_name 'libghostty-vt.so')"
    ghostty_wrong="$(find_name 'libghostty-vt.dylib')"
    ghostty_symlink="$(find_symlink 'libghostty-vt.so')"
    ;;
  osx-*)
    ghostty="$(find_name 'libghostty-vt.dylib')"
    ghostty_wrong="$(find_name 'libghostty-vt.so')"
    ghostty_symlink="$(find_symlink 'libghostty-vt.dylib')"
    ;;
  win-*)
    ghostty="$(find_name 'libghostty-vt.dll')"
    ghostty_symlink="$(find_symlink 'libghostty-vt.dll')"
    ;;
esac
if [[ -z "$ghostty_symlink" ]]; then
  for name in libghostty-vt.so libghostty-vt.dylib libghostty-vt.dll; do
    hit="$(find_symlink "$name")"
    if [[ -n "$hit" ]]; then
      ghostty_symlink="$hit"
      break
    fi
  done
fi
if [[ -z "$ghostty" ]]; then
  for name in libghostty-vt.so libghostty-vt.dylib libghostty-vt.dll; do
    hit="$(find_name "$name")"
    if [[ -n "$hit" ]]; then
      ghostty="$hit"
      break
    fi
  done
fi

if [[ "$CHANNEL" == "f2" ]]; then
  # shellcheck source=lib-ghostty-rid-arch.sh
  source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib-ghostty-rid-arch.sh"
  case "$RID" in
    linux-*) expected_ghostty="libghostty-vt.so" ;;
    osx-*) expected_ghostty="libghostty-vt.dylib" ;;
    *)
      echo "ERROR: F2 pack smoke is Unix only (got RID $RID)" >&2
      exit 1
      ;;
  esac
  if [[ -n "$ghostty_symlink" ]]; then
    echo "ERROR: F2 pack Ghostty must be a regular file, not a symlink: $ghostty_symlink" >&2
    exit 1
  fi
  if [[ -z "$ghostty" || "$(basename "$ghostty")" != "$expected_ghostty" ]]; then
    echo "ERROR: F2 pack for $RID is missing $expected_ghostty (H-11-F2)" >&2
    find "$ROOT" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
  if [[ -L "$ghostty" ]]; then
    echo "ERROR: F2 pack Ghostty must be a regular file, not a symlink: $ghostty" >&2
    exit 1
  fi
  if [[ -n "$ghostty_wrong" ]]; then
    echo "ERROR: F2 pack for $RID must not include $(basename "$ghostty_wrong")" >&2
    exit 1
  fi
  echo "    ghostty=$ghostty"
  assert_ghostty_rid_arch "$ghostty" "$RID"

  require_f2_file() {
    local name="$1"
    local hit
    hit="$(find_name "$name")"
    if [[ -z "$hit" ]]; then
      echo "ERROR: F2 pack is missing $name" >&2
      find "$ROOT" -maxdepth 2 -type f -print >&2 || true
      exit 1
    fi
    echo "    $name=$hit"
    F2_HIT="$hit"
  }

  require_f2_file NOTICE
  require_f2_file LICENSE.Ghostty
  require_f2_file PIN.md
  PIN="$F2_HIT"
  require_f2_file abi-manifest.json
  require_f2_file hypa.channel
  CHANNEL_FILE="$F2_HIT"
  require_f2_file sbom.cdx.json

  CHANNEL_TOKEN="$(tr -d '[:space:]' <"$CHANNEL_FILE")"
  if [[ "$CHANNEL_TOKEN" != "f2" ]]; then
    echo "ERROR: hypa.channel must be the single token f2 (got '$CHANNEL_TOKEN')" >&2
    exit 1
  fi

  if ! grep -q 'c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3' "$PIN"; then
    echo "ERROR: F2 PIN.md must contain pin c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3" >&2
    exit 1
  fi

  require_f2_file "libghostty-vt.${RID}.sha256"
  SIDECAR="$F2_HIT"
  EXPECTED_HASH="$(awk '{ print $1 }' "$SIDECAR" | tr -d '[:space:]')"
  ACTUAL_HASH="$(file_sha256 "$ghostty")"
  if [[ -z "$EXPECTED_HASH" ]]; then
    echo "ERROR: libghostty-vt.${RID}.sha256 is empty" >&2
    exit 1
  fi
  if [[ "$EXPECTED_HASH" != "$ACTUAL_HASH" ]]; then
    echo "ERROR: packed $(basename "$ghostty") sha256 $ACTUAL_HASH does not match sidecar $EXPECTED_HASH" >&2
    exit 1
  fi
  echo "    ghostty_sha256=$ACTUAL_HASH"

  exercise_extracted_tree
  echo "PASS: F2 pack smoke ($RID $CHANNEL)"
else
  channel_hit="$(find_name 'hypa.channel')"
  if [[ -z "$channel_hit" ]]; then
    channel_hit="$(find_symlink 'hypa.channel')"
  fi
  if [[ -n "$channel_hit" ]]; then
    echo "ERROR: F1 pack must not include hypa.channel ($channel_hit). F2 channel is F2 only." >&2
    exit 1
  fi

  case "$RID" in
    linux-*|osx-*)
      # shellcheck source=lib-ghostty-rid-arch.sh
      source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib-ghostty-rid-arch.sh"
      case "$RID" in
        linux-*) expected_ghostty="libghostty-vt.so" ;;
        osx-*) expected_ghostty="libghostty-vt.dylib" ;;
      esac
      if [[ -n "$ghostty_symlink" ]]; then
        echo "ERROR: F1 pack Ghostty must be a regular file, not a symlink: $ghostty_symlink" >&2
        exit 1
      fi
      if [[ -z "$ghostty" || "$(basename "$ghostty")" != "$expected_ghostty" ]]; then
        echo "ERROR: F1 pack for $RID is missing $expected_ghostty" >&2
        find "$ROOT" -maxdepth 2 -type f -print >&2 || true
        exit 1
      fi
      if [[ -L "$ghostty" ]]; then
        echo "ERROR: F1 pack Ghostty must be a regular file, not a symlink: $ghostty" >&2
        exit 1
      fi
      if [[ -n "$ghostty_wrong" ]]; then
        echo "ERROR: F1 pack for $RID must not include $(basename "$ghostty_wrong")" >&2
        exit 1
      fi
      echo "    ghostty=$ghostty"
      assert_ghostty_rid_arch "$ghostty" "$RID"

      require_f1_ghostty_file() {
        local name="$1"
        local hit
        hit="$(find_name "$name")"
        if [[ -z "$hit" ]]; then
          echo "ERROR: F1 pack is missing $name" >&2
          find "$ROOT" -maxdepth 2 -type f -print >&2 || true
          exit 1
        fi
        echo "    $name=$hit"
        F1_HIT="$hit"
      }

      verify_f1_sidecar() {
        local sidecar="$1"
        local expected actual
        expected="$(awk '{ print $1 }' "$sidecar" | tr -d '[:space:]')"
        actual="$(file_sha256 "$ghostty")"
        if [[ -z "$expected" ]]; then
          echo "ERROR: libghostty-vt.${RID}.sha256 is empty" >&2
          exit 1
        fi
        if [[ "$expected" != "$actual" ]]; then
          echo "ERROR: packed $(basename "$ghostty") sha256 $actual does not match sidecar $expected" >&2
          exit 1
        fi
        echo "    ghostty_sha256=$actual"
      }

      # Packed archives must include license/PIN/SBOM (the packer copies them
      # from native/ghostty). A raw AOT publish dir only ships the RID lib
      # and optional sidecar; do not treat it as an F2-shaped bundle.
      if [[ "$IS_ARCHIVE" -eq 1 ]]; then
        require_f1_ghostty_file NOTICE
        require_f1_ghostty_file LICENSE.Ghostty
        require_f1_ghostty_file PIN.md
        PIN="$F1_HIT"
        require_f1_ghostty_file abi-manifest.json
        require_f1_ghostty_file sbom.cdx.json
        if ! grep -q 'c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3' "$PIN"; then
          echo "ERROR: F1 PIN.md must contain pin c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3" >&2
          exit 1
        fi
        require_f1_ghostty_file "libghostty-vt.${RID}.sha256"
        verify_f1_sidecar "$F1_HIT"
      else
        SIDECAR="$(find_name "libghostty-vt.${RID}.sha256")"
        if [[ -n "$SIDECAR" ]]; then
          echo "    libghostty-vt.${RID}.sha256=$SIDECAR"
          verify_f1_sidecar "$SIDECAR"
        else
          echo "    libghostty-vt.${RID}.sha256=absent (publish dir; packer requires sidecar)"
        fi
      fi
      ;;
    win-*)
      echo "ERROR: Windows is not a Ghostty-only F1 mux RID. No libghostty-vt.dll. Mux cannot start. Do not treat pack-smoke green as a mux ship." >&2
      exit 1
      ;;
    *)
      echo "ERROR: unknown RID for F1 Ghostty check: $RID" >&2
      exit 2
      ;;
  esac
  echo "    hypa.channel=absent (F1 required)"
  exercise_extracted_tree
  echo "PASS: F1 pack smoke ($RID $CHANNEL)"
fi
exit 0
