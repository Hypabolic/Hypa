#!/usr/bin/env bash
# Fail when any ELF in a publish directory or pack needs a GLIBC_ symbol
# version above 2.34.
#
# Usage:
#   scripts/verify-glibc-floor.sh PATH [PATH...]
#
# PATH is a directory, a .tar.gz archive, or a .tgz archive.

set -euo pipefail

FLOOR="2.34"

usage() {
  echo "Usage: $0 PATH [PATH...]" >&2
  echo "PATH is a publish directory or a .tar.gz pack." >&2
  exit 2
}

if [[ $# -lt 1 ]]; then
  usage
fi

if ! command -v readelf >/dev/null 2>&1; then
  echo "ERROR: readelf is required to verify the glibc floor" >&2
  exit 1
fi

version_above_floor() {
  local ver="$1"
  local floor="$2"
  local IFS=.
  local -a left right
  read -r -a left <<< "$ver"
  read -r -a right <<< "$floor"
  local i n=${#left[@]} av bv
  if (( ${#right[@]} > n )); then
    n=${#right[@]}
  fi
  for ((i = 0; i < n; i++)); do
    av=$((10#${left[i]:-0}))
    bv=$((10#${right[i]:-0}))
    if ((av > bv)); then
      return 0
    fi
    if ((av < bv)); then
      return 1
    fi
  done
  return 1
}

is_elf() {
  local magic
  magic="$(od -An -t x1 -N 4 "$1" | tr -d '[:space:]' | tr 'A-F' 'a-f')"
  [[ "$magic" == "7f454c46" ]]
}

scan_file() {
  local file="$1"
  local ver seen=""
  if ! is_elf "$file"; then
    return 0
  fi
  while IFS= read -r ver; do
    [[ -z "$ver" ]] && continue
    case " $seen " in
      *" $ver "*) continue ;;
    esac
    seen="$seen $ver"
    if version_above_floor "$ver" "$FLOOR"; then
      echo "ERROR: $file needs GLIBC_$ver (floor is GLIBC_$FLOOR)" >&2
      return 1
    fi
  done < <( {
    readelf -W -V "$file" 2>/dev/null || true
    readelf -W -s "$file" 2>/dev/null || true
  } | grep -oE 'GLIBC_[0-9]+(\.[0-9]+)*' | sed 's/^GLIBC_//' | sort -u || true)
}

scan_tree() {
  local root="$1"
  local file failed=0
  while IFS= read -r -d '' file; do
    if ! scan_file "$file"; then
      failed=1
    fi
  done < <(find "$root" -type f -print0)
  return "$failed"
}

prepare_input() {
  local input="$1"
  local tmp
  if [[ ! -f "$input" ]]; then
    echo "ERROR: pack is missing: $input" >&2
    return 1
  fi
  tmp="$(mktemp -d "${TMPDIR:-/tmp}/hypa-glibc-floor.XXXXXX")"
  tar -xzf "$input" -C "$tmp"
  printf '%s\n' "$tmp"
}

failed=0
for input in "$@"; do
  extracted=""
  case "$input" in
    *.tar.gz|*.tgz)
      extracted="$(prepare_input "$input")"
      echo "==> glibc floor $FLOOR $input"
      if ! scan_tree "$extracted"; then
        failed=1
      fi
      rm -rf "$extracted"
      ;;
    *)
      if [[ ! -d "$input" ]]; then
        echo "ERROR: path is not a directory or pack: $input" >&2
        failed=1
        continue
      fi
      echo "==> glibc floor $FLOOR $input"
      if ! scan_tree "$input"; then
        failed=1
      fi
      ;;
  esac
done

if [[ "$failed" -ne 0 ]]; then
  exit 1
fi

echo "PASS: glibc floor $FLOOR"
