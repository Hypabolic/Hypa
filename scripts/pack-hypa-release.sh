#!/usr/bin/env bash
# Release pack. F1 and F2 packers must call this script.
#
# Do not copy only hypa-runtime.
# That pattern drops libe_sqlite3 and hypa-pty-host.
# Unix F1 and F2 pack libghostty-vt. F1 must not write hypa.channel.
# F2 remains hypa-f2-<rid> plus marker plus recorded goldens.
# accepted-digest lineage is an F2 gate. Unix F1 fails closed when
# the RID lib, sidecar, arch, or license notices are missing.
# Windows is not a Ghostty-only F1 mux RID. No libghostty-vt.dll.
#
# Usage:
#   scripts/pack-hypa-release.sh \
#     --rid linux-x64 \
#     --publish-dir artifacts/publish/linux-x64 \
#     --name hypa \
#     --out-dir artifacts/dist \
#     [--archive tar.gz|zip] \
#     [--channel f1|f2] \
#     [--profile mux-release|full]

set -euo pipefail

RID=""
PUBLISH_DIR=""
NAME="hypa"
OUT_DIR=""
ARCHIVE=""
CHANNEL="f1"
PROFILE="mux-release"

usage() {
  echo "Usage: $0 --rid RID --publish-dir DIR --name hypa|hypa-runtime --out-dir DIR [--archive tar.gz|zip] [--channel f1|f2] [--profile mux-release|full]" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid) RID="${2:-}"; shift 2 ;;
    --publish-dir) PUBLISH_DIR="${2:-}"; shift 2 ;;
    --name) NAME="${2:-}"; shift 2 ;;
    --out-dir) OUT_DIR="${2:-}"; shift 2 ;;
    --archive) ARCHIVE="${2:-}"; shift 2 ;;
    --channel) CHANNEL="${2:-}"; shift 2 ;;
    --profile) PROFILE="${2:-}"; shift 2 ;;
    -h|--help) usage ;;
    *)
      echo "ERROR: unknown option: $1" >&2
      usage
      ;;
  esac
done

if [[ -z "$RID" || -z "$PUBLISH_DIR" || -z "$OUT_DIR" ]]; then
  usage
fi
if [[ ! -d "$PUBLISH_DIR" ]]; then
  echo "ERROR: publish dir missing: $PUBLISH_DIR" >&2
  exit 1
fi
# Product default is --name hypa. That pack requires the hypa-runtime
# sibling beside hypa. --name hypa-runtime remains an internal debug
# alias pack and must be requested explicitly. The two roles do not
# share one name check.
if [[ "$NAME" != "hypa" && "$NAME" != "hypa-runtime" ]]; then
  echo "ERROR: --name must be hypa or hypa-runtime" >&2
  exit 2
fi
if [[ "$CHANNEL" != "f1" && "$CHANNEL" != "f2" ]]; then
  echo "ERROR: --channel must be f1 or f2" >&2
  exit 2
fi
if [[ "$RID" == win-* ]]; then
  echo "ERROR: Windows is not a Ghostty-only F1 mux RID. No libghostty-vt.dll. Do not pack a mux archive that cannot mux." >&2
  exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib-mux-release-profile.sh
source "$ROOT/scripts/lib-mux-release-profile.sh"
mux_profile_assert_known "$PROFILE" || exit $?
# shellcheck source=lib-ghostty-rid-arch.sh
source "$ROOT/scripts/lib-ghostty-rid-arch.sh"

# Regular file only. -f follows a symlink; that hashes the target and
# can leave an external link in the archive.
require_regular_file() {
  local path="$1"
  local label="$2"
  if [[ -L "$path" ]]; then
    echo "ERROR: $label must be a regular file, not a symlink: $path" >&2
    exit 1
  fi
  if [[ ! -f "$path" ]]; then
    echo "ERROR: $label must be a regular file: $path" >&2
    exit 1
  fi
}

file_sha256() {
  local f="$1"
  require_regular_file "$f" "hash input"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$f" | awk '{ print $1 }'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$f" | awk '{ print $1 }'
  else
    echo "ERROR: sha256sum or shasum is required to verify libghostty-vt" >&2
    exit 1
  fi
}

normalize_sha256() {
  printf '%s' "$1" | tr -d '[:space:]' | tr 'A-F' 'a-f'
}

lookup_h15_digest() {
  local rid="$1"
  local manifest="$2"
  [[ -f "$manifest" ]] || return 1
  awk -v rid="$rid" '
    /^[[:space:]]*#/ { next }
    /^[[:space:]]*$/ { next }
    $1 == rid { print $2; exit }
  ' "$manifest"
}

PACK_GHOSTTY=0
case "$RID" in
  linux-*|osx-*) PACK_GHOSTTY=1 ;;
esac
if [[ "$CHANNEL" == "f2" ]]; then
  case "$RID" in
    linux-*|osx-*) ;;
    *)
      echo "ERROR: F2 channel is Unix only (linux-* / osx-*). Got '$RID'" >&2
      exit 2
      ;;
  esac
  PACK_GHOSTTY=1
fi

if [[ "$PACK_GHOSTTY" -eq 1 ]]; then
  case "$RID" in
    linux-*)
      GHOSTTY_LIB="$PUBLISH_DIR/libghostty-vt.so"
      GHOSTTY_WRONG="$PUBLISH_DIR/libghostty-vt.dylib"
      ;;
    osx-*)
      GHOSTTY_LIB="$PUBLISH_DIR/libghostty-vt.dylib"
      GHOSTTY_WRONG="$PUBLISH_DIR/libghostty-vt.so"
      ;;
    *)
      echo "ERROR: Ghostty pack is Unix only (linux-* / osx-*). Got '$RID'" >&2
      exit 2
      ;;
  esac
  if [[ -L "$GHOSTTY_LIB" ]]; then
    echo "ERROR: packed Ghostty must be a regular file, not a symlink: $GHOSTTY_LIB" >&2
    exit 1
  fi
  if [[ ! -f "$GHOSTTY_LIB" ]]; then
    echo "ERROR: pack for $RID requires $(basename "$GHOSTTY_LIB") in the publish dir" >&2
    find "$PUBLISH_DIR" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
  require_regular_file "$GHOSTTY_LIB" "publish Ghostty lib"
  if [[ -e "$GHOSTTY_WRONG" || -L "$GHOSTTY_WRONG" ]]; then
    echo "ERROR: pack for $RID must not include $(basename "$GHOSTTY_WRONG")" >&2
    exit 1
  fi
  assert_ghostty_rid_arch "$GHOSTTY_LIB" "$RID"
fi

if [[ -z "$ARCHIVE" ]]; then
  case "$RID" in
    win-*) ARCHIVE="zip" ;;
    *) ARCHIVE="tar.gz" ;;
  esac
fi

if [[ "$CHANNEL" == "f2" ]]; then
  bash "$ROOT/scripts/verify-h15-f2-prerequisite.sh"
elif [[ "$PACK_GHOSTTY" -eq 1 ]]; then
  PIN="$ROOT/native/ghostty/PIN.md"
  LOADER="$ROOT/src/Hypa.Terminal/Vt/Ghostty/GhosttyLibraryLoader.cs"
  PIN_SHA="c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3"
  if [[ ! -f "$PIN" ]]; then
    echo "ERROR: missing PIN.md: $PIN" >&2
    exit 1
  fi
  if ! grep -q "$PIN_SHA" "$PIN"; then
    echo "ERROR: PIN.md must contain pin $PIN_SHA" >&2
    exit 1
  fi
  if [[ ! -f "$LOADER" ]]; then
    echo "ERROR: missing GhosttyLibraryLoader.cs: $LOADER" >&2
    exit 1
  fi
  if ! grep -q "$PIN_SHA" "$LOADER"; then
    echo "ERROR: GhosttyLibraryLoader must pin $PIN_SHA" >&2
    exit 1
  fi
fi

STAGE_PARENT="$(mktemp -d "${TMPDIR:-/tmp}/hypa-pack-stage.XXXXXX")"
cleanup() { rm -rf "$STAGE_PARENT"; }
trap cleanup EXIT

if [[ "$CHANNEL" == "f2" ]]; then
  BUNDLE="${NAME}-f2-${RID}"
else
  BUNDLE="${NAME}-${RID}"
fi
STAGE="${STAGE_PARENT}/${BUNDLE}"
mkdir -p "$STAGE" "$OUT_DIR"

echo "==> H-20 pack ${BUNDLE} (channel=${CHANNEL})"
echo "    publish=$PUBLISH_DIR"
echo "    profile=$PROFILE"

# Copy the full publish tree minus debug and test fixtures.
# Do not copy only two binaries.
# Packed Ghostty is staged as regular-file bytes below. cp -R on a symlink
# would archive the link and leave the package not self-contained.
while IFS= read -r -d '' entry; do
  base="$(basename "$entry")"
  case "$base" in
    *.pdb|*.dbg|Fixtures|*.dSYM) continue ;;
    libghostty-vt.so|libghostty-vt.dylib|libghostty-vt.dll)
      if [[ "$PACK_GHOSTTY" -eq 1 ]]; then
        continue
      fi
      ;;
  esac
  cp -R "$entry" "$STAGE/"
done < <(find "$PUBLISH_DIR" -mindepth 1 -maxdepth 1 -print0)

mux_profile_reject_forbidden_binaries "$STAGE" "$PROFILE"

if [[ "$CHANNEL" == "f1" ]]; then
  rm -f "$STAGE"/hypa.channel
  if [[ "$PACK_GHOSTTY" -eq 0 ]]; then
    rm -f "$STAGE"/libghostty-vt.so "$STAGE"/libghostty-vt.dylib "$STAGE"/libghostty-vt.dll
  fi
fi

if [[ "$PACK_GHOSTTY" -eq 1 ]]; then
  GHOSTTY_DOCS="$ROOT/native/ghostty"
  for doc in NOTICE LICENSE.Ghostty PIN.md abi-manifest.json sbom.cdx.json; do
    if [[ ! -f "$GHOSTTY_DOCS/$doc" ]]; then
      echo "ERROR: pack missing $GHOSTTY_DOCS/$doc" >&2
      exit 1
    fi
    cp "$GHOSTTY_DOCS/$doc" "$STAGE/"
  done
  SIDECAR_NAME="libghostty-vt.${RID}.sha256"
  SIDECAR=""
  if [[ -f "$PUBLISH_DIR/$SIDECAR_NAME" && ! -L "$PUBLISH_DIR/$SIDECAR_NAME" ]]; then
    SIDECAR="$PUBLISH_DIR/$SIDECAR_NAME"
  elif [[ -f "$ROOT/native/runtimes/$RID/native/$SIDECAR_NAME" && ! -L "$ROOT/native/runtimes/$RID/native/$SIDECAR_NAME" ]]; then
    SIDECAR="$ROOT/native/runtimes/$RID/native/$SIDECAR_NAME"
  else
    echo "ERROR: pack requires $SIDECAR_NAME next to the Ghostty lib" >&2
    find "$PUBLISH_DIR" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
  require_regular_file "$SIDECAR" "Ghostty sidecar"
  cp -f "$SIDECAR" "$STAGE/$SIDECAR_NAME"
  require_regular_file "$STAGE/$SIDECAR_NAME" "staged Ghostty sidecar"

  STAGED_LIB=""
  case "$RID" in
    linux-*) STAGED_LIB="$STAGE/libghostty-vt.so" ;;
    osx-*) STAGED_LIB="$STAGE/libghostty-vt.dylib" ;;
  esac
  # Byte-copy the publish lib. Never leave a symlink in the stage tree.
  rm -f "$STAGED_LIB"
  require_regular_file "$GHOSTTY_LIB" "publish Ghostty lib"
  cp -f "$GHOSTTY_LIB" "$STAGED_LIB"
  require_regular_file "$STAGED_LIB" "staged Ghostty lib"
  assert_ghostty_rid_arch "$STAGED_LIB" "$RID"
  EXPECTED_HASH="$(normalize_sha256 "$(awk '{ print $1 }' "$STAGE/$SIDECAR_NAME")")"
  ACTUAL_HASH="$(normalize_sha256 "$(file_sha256 "$STAGED_LIB")")"
  if [[ -z "$EXPECTED_HASH" ]]; then
    echo "ERROR: $SIDECAR_NAME is empty" >&2
    exit 1
  fi
  if [[ "$EXPECTED_HASH" != "$ACTUAL_HASH" ]]; then
    echo "ERROR: packed $(basename "$STAGED_LIB") sha256 $ACTUAL_HASH does not match sidecar $EXPECTED_HASH" >&2
    exit 1
  fi

  if [[ "$CHANNEL" == "f2" ]]; then
    # PIN-SHA equality is not lineage. F2 packed lib must be an accepted hash.
    # Unix F1 ships the RID lib without this golden-lineage gate.
    H15_DIGEST="$(normalize_sha256 "${HYPA_F2_H15_DIGEST:-}")"
    H15_SOURCE="HYPA_F2_H15_DIGEST"
    if [[ -z "$H15_DIGEST" ]]; then
      H15_DIGEST="$(normalize_sha256 "$(lookup_h15_digest "$RID" "$ROOT/native/ghostty/h15-accepted-digests")")"
      H15_SOURCE="native/ghostty/h15-accepted-digests"
    fi
    if [[ -z "$H15_DIGEST" ]]; then
      echo "ERROR: F2 pack for $RID has no H-15 accepted digest" >&2
      echo "Run scripts/verify-h15-rc-replay.sh on this exact lib." >&2
      echo "Then set HYPA_F2_H15_DIGEST or record the sha256 in native/ghostty/h15-accepted-digests." >&2
      exit 1
    fi
    if [[ ! "$H15_DIGEST" =~ ^[0-9a-f]{64}$ ]]; then
      echo "ERROR: H-15 digest from $H15_SOURCE is not a 64-char sha256" >&2
      exit 1
    fi
    if [[ "$ACTUAL_HASH" != "$H15_DIGEST" ]]; then
      echo "ERROR: packed $(basename "$STAGED_LIB") sha256 $ACTUAL_HASH is not the H-15 accepted digest $H15_DIGEST ($H15_SOURCE)" >&2
      exit 1
    fi
    echo "    h15_digest=$ACTUAL_HASH ($H15_SOURCE)"
    printf 'f2\n' >"$STAGE/hypa.channel"
  fi
fi

if [[ "$RID" == linux-* ]] && { [[ -f "$STAGE/libmsquic.so" ]] || [[ -f "$STAGE/libmsquic.so.2" ]]; }; then
  require_regular_file "$STAGE/libnuma.so.1" "bundled libnuma"
  require_regular_file "$STAGE/libnuma.so.1.sha256" "libnuma digest sidecar"
  NUMA_EXPECTED="$(normalize_sha256 "$(awk '{ print $1 }' "$STAGE/libnuma.so.1.sha256")")"
  NUMA_ACTUAL="$(normalize_sha256 "$(file_sha256 "$STAGE/libnuma.so.1")")"
  if [[ -z "$NUMA_EXPECTED" || "$NUMA_EXPECTED" != "$NUMA_ACTUAL" ]]; then
    echo "ERROR: packed libnuma.so.1 sha256 $NUMA_ACTUAL does not match sidecar $NUMA_EXPECTED" >&2
    exit 1
  fi
  NUMA_NOTICE="$ROOT/native/numactl/NOTICE.libnuma"
  NUMA_LICENSE="$ROOT/native/numactl/LICENSE.libnuma"
  if [[ ! -f "$NUMA_NOTICE" ]]; then
    echo "ERROR: pack missing $NUMA_NOTICE" >&2
    exit 1
  fi
  if [[ ! -f "$NUMA_LICENSE" ]]; then
    echo "ERROR: pack missing $NUMA_LICENSE" >&2
    exit 1
  fi
  cp "$NUMA_NOTICE" "$STAGE/NOTICE.libnuma"
  cp "$NUMA_LICENSE" "$STAGE/LICENSE.libnuma"
  require_regular_file "$STAGE/NOTICE.libnuma" "libnuma license notice"
  require_regular_file "$STAGE/LICENSE.libnuma" "libnuma LGPL-2.1 text"
fi

if [[ "$NAME" == "hypa" ]]; then
  if [[ ! -e "$STAGE/hypa" && ! -e "$STAGE/hypa.exe" ]]; then
    echo "ERROR: --name hypa requires the hypa product binary in the publish dir" >&2
    find "$STAGE" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
  if [[ ! -e "$STAGE/hypa-attach" && ! -e "$STAGE/hypa-attach.exe" ]]; then
    echo "ERROR: --name hypa requires hypa-attach beside hypa (lean attach sibling, not a product brand)" >&2
    find "$STAGE" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
  if [[ ! -e "$STAGE/hypa-annotate" && ! -e "$STAGE/hypa-annotate.exe" ]]; then
    echo "ERROR: --name hypa requires hypa-annotate beside hypa (annotate sibling, not a product brand)" >&2
    find "$STAGE" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
  if [[ ! -e "$STAGE/hypa-runtime" && ! -e "$STAGE/hypa-runtime.exe" ]]; then
    echo "ERROR: --name hypa requires hypa-runtime beside hypa (lean mux sibling, not a product brand)" >&2
    find "$STAGE" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
else
  if [[ ! -e "$STAGE/hypa-runtime" && ! -e "$STAGE/hypa-runtime.exe" ]]; then
    echo "ERROR: --name hypa-runtime requires the hypa-runtime debug-alias binary" >&2
    find "$STAGE" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
  if [[ -e "$STAGE/hypa" || -e "$STAGE/hypa.exe" ]]; then
    echo "ERROR: --name hypa-runtime refuses a stage that holds hypa (debug-alias pack is not the product pack)" >&2
    find "$STAGE" -maxdepth 2 -type f -print >&2 || true
    exit 1
  fi
fi

PACK_NOTES_TAG="${PACK_NOTES_TAG:-${CHANNEL}-local}"
bash "$ROOT/scripts/emit-mux-release-notes.sh" --profile "$PROFILE" --tag "$PACK_NOTES_TAG" >"$STAGE/hypa.pack-notes.txt"
require_regular_file "$STAGE/hypa.pack-notes.txt" "pack notes"

# hypa-attach is a required same-directory sibling of product hypa, not a
# product brand. Keep libe_sqlite3 and hypa-pty-host. Do not pack only two
# binaries.
for exe in hypa hypa.exe hypa-attach hypa-attach.exe hypa-annotate hypa-annotate.exe hypa-runtime hypa-runtime.exe hypa-pty-host; do
  if [[ -e "$STAGE/$exe" ]]; then
    chmod +x "$STAGE/$exe" || true
  fi
done

# install_name_tool clears a signature. Ad-hoc sign Mach-O in an osx pack
# so codesign --verify succeeds on the archive the clean-install job downloads.
# Signing changes bytes. Refresh a sibling .sha256 and the Ghostty sidecar
# after the pin check above, so the archive digest still matches the file.
# F2 Ghostty keeps its bytes: the build signed it before the H-15 digest.
if [[ "$RID" == osx-* ]]; then
  if ! command -v codesign >/dev/null 2>&1; then
    echo "ERROR: codesign is required to pack $RID" >&2
    exit 1
  fi
  while IFS= read -r -d '' macho; do
    if [[ "$CHANNEL" == "f2" && "$(basename "$macho")" == "libghostty-vt.dylib" ]]; then
      # The build signs this file before its digest. Do not change its bytes.
      if ! codesign --verify "$macho"; then
        echo "ERROR: F2 $macho has no valid signature. Rebuild it with scripts/build-libghostty-vt.sh." >&2
        exit 1
      fi
      continue
    fi
    if file -b "$macho" | grep -q 'Mach-O'; then
      codesign --force --sign - "$macho"
      if [[ -f "${macho}.sha256" ]]; then
        normalize_sha256 "$(file_sha256 "$macho")" >"${macho}.sha256"
      fi
    fi
  done < <(find "$STAGE" -type f -print0)
  if [[ "$CHANNEL" != "f2" && -f "$STAGE/libghostty-vt.dylib" && -f "$STAGE/libghostty-vt.${RID}.sha256" ]]; then
    normalize_sha256 "$(file_sha256 "$STAGE/libghostty-vt.dylib")" >"$STAGE/libghostty-vt.${RID}.sha256"
  fi
fi

mkdir -p "$OUT_DIR"
OUT_ABS="$(cd "$OUT_DIR" && pwd)"
if [[ "$ARCHIVE" == "tar.gz" ]]; then
  ARTIFACT="${OUT_ABS}/${BUNDLE}.tar.gz"
  tar -czf "$ARTIFACT" -C "$STAGE_PARENT" "$BUNDLE"
elif [[ "$ARCHIVE" == "zip" ]]; then
  ARTIFACT="${OUT_ABS}/${BUNDLE}.zip"
  (
    cd "$STAGE_PARENT"
    if command -v zip >/dev/null 2>&1; then
      zip -qr "$ARTIFACT" "$BUNDLE"
    else
      echo "ERROR: zip is required to build a .zip archive" >&2
      exit 1
    fi
  )
else
  echo "ERROR: --archive must be tar.gz or zip" >&2
  exit 2
fi

echo "    artifact=$ARTIFACT"

SMOKE="$ROOT/scripts/verify-f1-pack-smoke.sh"
SMOKE_ARGS=(--rid "$RID" --channel "$CHANNEL" --name "$NAME" --profile "$PROFILE")
bash "$SMOKE" "${SMOKE_ARGS[@]}" "$ARTIFACT"

echo "PASS: packed $ARTIFACT"
