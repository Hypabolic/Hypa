#!/usr/bin/env bash
# Fail-closed Ghostty CPU-arch check for F2 pack/smoke.
# Filename .so/.dylib is not enough: linux-x64 vs linux-arm64 and
# osx-x64 vs osx-arm64 must inspect ELF e_machine / Mach-O cputype.
# Do not use python. od -t x1/u2/u4 is POSIX.

assert_ghostty_rid_arch() {
  local lib="${1:-}"
  local rid="${2:-}"
  if [[ -z "$lib" || -z "$rid" ]]; then
    echo "ERROR: assert_ghostty_rid_arch requires LIB and RID" >&2
    return 1
  fi
  # -f follows a symlink. Hashing/arch-checking the target while packing
  # the link leaves F2 not self-contained.
  if [[ -L "$lib" ]]; then
    echo "ERROR: Ghostty lib must be a regular file, not a symlink: $lib" >&2
    return 1
  fi
  if [[ ! -f "$lib" ]]; then
    echo "ERROR: Ghostty lib missing for arch check: $lib" >&2
    return 1
  fi
  if ! command -v od >/dev/null 2>&1; then
    echo "ERROR: od is required to read ELF/Mach-O architecture" >&2
    return 1
  fi

  local magic
  magic="$(od -An -t x1 -N 4 -j 0 "$lib" | tr -d ' \n')"
  if [[ -z "$magic" ]]; then
    echo "ERROR: could not read magic bytes from $lib" >&2
    return 1
  fi

  case "$rid" in
    linux-x64|linux-arm64)
      if [[ "$magic" != "7f454c46" ]]; then
        echo "ERROR: F2 $rid Ghostty lib is not ELF (magic=$magic)" >&2
        return 1
      fi
      local ei_class
      ei_class="$(od -An -t u1 -N 1 -j 4 "$lib" | tr -d ' \n')"
      if [[ "$ei_class" != "2" ]]; then
        echo "ERROR: F2 $rid Ghostty lib is not ELF64 (ei_class=$ei_class)" >&2
        return 1
      fi
      local machine
      machine="$(od -An -t u2 -N 2 -j 18 "$lib" | tr -d ' \n')"
      local expect=62
      [[ "$rid" == "linux-arm64" ]] && expect=183
      if [[ "$machine" != "$expect" ]]; then
        echo "ERROR: F2 $rid Ghostty lib ELF e_machine=$machine, expected $expect" >&2
        return 1
      fi
      ;;
    osx-x64|osx-arm64)
      case "$magic" in
        cffaedfe) ;;
        feedfacf)
          echo "ERROR: F2 $rid Ghostty lib is big-endian Mach-O" >&2
          return 1
          ;;
        cafebabe|bebafeca|cafebabf|bfbafeca)
          echo "ERROR: F2 $rid Ghostty lib is a fat Mach-O; ship a thin RID slice" >&2
          return 1
          ;;
        *)
          echo "ERROR: F2 $rid Ghostty lib is not Mach-O (magic=$magic)" >&2
          return 1
          ;;
      esac
      local cputype
      cputype="$(od -An -t u4 -N 4 -j 4 "$lib" | tr -d ' \n')"
      local expect=16777223
      [[ "$rid" == "osx-arm64" ]] && expect=16777228
      if [[ "$cputype" != "$expect" ]]; then
        echo "ERROR: F2 $rid Ghostty lib Mach-O cputype=$cputype, expected $expect" >&2
        return 1
      fi
      ;;
    *)
      echo "ERROR: F2 arch check has no CPU map for RID $rid" >&2
      return 1
      ;;
  esac
  echo "    ghostty_arch=$rid"
  return 0
}
