#!/usr/bin/env bash
# Builds libghostty-vt (shared) for the current platform from the pinned Ghostty
# commit in native/ghostty/PIN.md.
#
# Outputs to native/runtimes/<RID>/native/libghostty-vt.{so,dylib}.
# Idempotent: skips when the output exists unless FORCE_BUILD=1.
#
# Env:
#   FORCE_BUILD=1           rebuild even if output exists
#   HYPA_GHOSTTY_SRC=path   use an existing Ghostty / libghostty-vt tree
#   ZIG=path                zig binary (else PATH, else download pin)
#   LIBGHOSTTY_VT_OPTIMIZE  default ReleaseFast
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
PIN_FILE="$REPO_ROOT/native/ghostty/PIN.md"
ABI_MANIFEST="$REPO_ROOT/native/ghostty/abi-manifest.json"
PATCH_DIR="$REPO_ROOT/native/ghostty/patches"
CACHE_ROOT="$REPO_ROOT/native/ghostty/.cache"

if [[ ! -f "$PIN_FILE" ]]; then
  echo "ERROR: missing pin file $PIN_FILE" >&2
  exit 1
fi

# Parse machine tokens from PIN.md (GHOSTTY_COMMIT=... / ZIG_VERSION=...).
GHOSTTY_COMMIT="$(grep -E '^GHOSTTY_COMMIT=' "$PIN_FILE" | head -1 | cut -d= -f2- | tr -d '[:space:]')"
ZIG_VERSION="$(grep -E '^ZIG_VERSION=' "$PIN_FILE" | head -1 | cut -d= -f2- | tr -d '[:space:]')"
if [[ -z "$GHOSTTY_COMMIT" || -z "$ZIG_VERSION" ]]; then
  echo "ERROR: PIN.md must define GHOSTTY_COMMIT= and ZIG_VERSION=" >&2
  exit 1
fi

OPTIMIZE="${LIBGHOSTTY_VT_OPTIMIZE:-ReleaseFast}"

case "$(uname -s)" in
  Linux)
    case "$(uname -m)" in
      x86_64)  RID="linux-x64"  ;;
      aarch64) RID="linux-arm64" ;;
      armv7l)  RID="linux-arm"  ;;
      *) echo "Unsupported Linux architecture: $(uname -m)" >&2; exit 1 ;;
    esac
    LIB_NAME="libghostty-vt.so"
    ;;
  Darwin)
    case "$(uname -m)" in
      x86_64) RID="osx-x64"   ;;
      arm64)  RID="osx-arm64" ;;
      *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 1 ;;
    esac
    LIB_NAME="libghostty-vt.dylib"
    ;;
  *)
    echo "Unsupported OS: $(uname -s). libghostty-vt spike is Unix-only." >&2
    exit 1
    ;;
esac

OUTPUT_DIR="$REPO_ROOT/native/runtimes/$RID/native"
OUTPUT_PATH="$OUTPUT_DIR/$LIB_NAME"
CHECKSUM_PATH="$OUTPUT_DIR/libghostty-vt.${RID}.sha256"

if [[ -f "$OUTPUT_PATH" && "${FORCE_BUILD:-0}" != "1" ]]; then
  echo "Already built: $OUTPUT_PATH (set FORCE_BUILD=1 to rebuild)"
  exit 0
fi

LOCK_DIR="/tmp/hypa-libghostty-vt-build-lock-$RID"
release_lock() { rmdir "$LOCK_DIR" 2>/dev/null || true; }
trap release_lock EXIT
attempts=0
while ! mkdir "$LOCK_DIR" 2>/dev/null; do
  attempts=$((attempts + 1))
  if [[ $attempts -gt 600 ]]; then
    echo "ERROR: timed out waiting for build lock $LOCK_DIR" >&2
    exit 1
  fi
  sleep 0.2
done

if [[ -f "$OUTPUT_PATH" && "${FORCE_BUILD:-0}" != "1" ]]; then
  echo "Already built (by a concurrent invocation): $OUTPUT_PATH"
  exit 0
fi

mkdir -p "$CACHE_ROOT" "$OUTPUT_DIR"

# --- Zig toolchain ---------------------------------------------------------
# Zig 0.15+ tarball names use arch-os (zig-x86_64-macos-0.15.2), not os-arch.
resolve_zig() {
  if [[ -n "${ZIG:-}" && -x "${ZIG}" ]]; then
    printf '%s\n' "$ZIG"
    return
  fi
  if command -v zig >/dev/null 2>&1; then
    local found
    found="$(command -v zig)"
    local ver
    ver="$("$found" version 2>/dev/null || true)"
    if [[ "$ver" == "$ZIG_VERSION" ]]; then
      printf '%s\n' "$found"
      return
    fi
    echo "NOTE: zig on PATH is '$ver', pin wants $ZIG_VERSION; will use cache if present" >&2
  fi

  local os arch tarball extract_dir zig_dir
  case "$(uname -s)" in
    Linux)  os="linux" ;;
    Darwin) os="macos" ;;
  esac
  case "$(uname -m)" in
    x86_64)  arch="x86_64" ;;
    aarch64|arm64) arch="aarch64" ;;
    *) echo "ERROR: no Zig download mapping for $(uname -m)" >&2; exit 1 ;;
  esac
  tarball="zig-${arch}-${os}-${ZIG_VERSION}.tar.xz"
  extract_dir="zig-${arch}-${os}-${ZIG_VERSION}"
  zig_dir="$CACHE_ROOT/${extract_dir}"
  if [[ ! -x "$zig_dir/zig" ]]; then
    echo "Downloading Zig $ZIG_VERSION ($arch/$os)..." >&2
    local url="https://ziglang.org/download/${ZIG_VERSION}/${tarball}"
    local tmp
    tmp="$(mktemp -d "${TMPDIR:-/tmp}/hypa-zig.XXXXXX")"
    if command -v curl >/dev/null 2>&1; then
      curl -fsSL "$url" -o "$tmp/$tarball"
    else
      wget -q -O "$tmp/$tarball" "$url"
    fi
    tar -xJf "$tmp/$tarball" -C "$tmp"
    rm -rf "$zig_dir"
    mv "$tmp/$extract_dir" "$zig_dir"
    rm -rf "$tmp"
  fi
  printf '%s\n' "$zig_dir/zig"
}

ZIG_BIN="$(resolve_zig)"
echo "Using Zig: $ZIG_BIN ($("$ZIG_BIN" version))"

# --- Source tree -----------------------------------------------------------
# Prefer HYPA_GHOSTTY_SRC, then cached checkout at pin, then shallow fetch.
SRC_DIR=""
if [[ -n "${HYPA_GHOSTTY_SRC:-}" ]]; then
  if [[ ! -f "${HYPA_GHOSTTY_SRC}/build.zig" ]]; then
    echo "ERROR: HYPA_GHOSTTY_SRC has no build.zig: $HYPA_GHOSTTY_SRC" >&2
    exit 1
  fi
  SRC_DIR="$HYPA_GHOSTTY_SRC"
  echo "Using HYPA_GHOSTTY_SRC=$SRC_DIR"
else
  SRC_DIR="$CACHE_ROOT/ghostty-${GHOSTTY_COMMIT}"
  if [[ ! -f "$SRC_DIR/build.zig" ]]; then
    echo "Fetching Ghostty $GHOSTTY_COMMIT into $SRC_DIR ..."
    rm -rf "$SRC_DIR"
    mkdir -p "$SRC_DIR"
    git init -q "$SRC_DIR"
    git -C "$SRC_DIR" remote add origin https://github.com/ghostty-org/ghostty.git
    # Depth-1 fetch of the exact commit (works when the object is reachable).
    if ! git -C "$SRC_DIR" fetch --depth 1 origin "$GHOSTTY_COMMIT"; then
      echo "Depth-1 fetch failed; trying full blobless fetch of commit..."
      git -C "$SRC_DIR" fetch --filter=blob:none origin "$GHOSTTY_COMMIT"
    fi
    git -C "$SRC_DIR" checkout -q FETCH_HEAD
    # Record pin for diagnostics.
    echo "$GHOSTTY_COMMIT" >"$SRC_DIR/.hypa-ghostty-pin"
  else
    echo "Using cached Ghostty source: $SRC_DIR"
  fi
fi

# Work in a temp copy so patches never dirty a shared cache / HYPA_GHOSTTY_SRC.
BUILD_SRC="$(mktemp -d "${TMPDIR:-/tmp}/hypa-ghostty-src.XXXXXX")"
cleanup_src() {
  rm -rf "$BUILD_SRC"
  release_lock
}
trap cleanup_src EXIT

# Copy only what zig build needs when possible; fall back to full tree.
# rsync is preferred; cp -R is fine for CI.
if command -v rsync >/dev/null 2>&1; then
  rsync -a --delete \
    --exclude '.git' \
    --exclude 'zig-out' \
    --exclude 'zig-cache' \
    --exclude '.zig-cache' \
    "$SRC_DIR/" "$BUILD_SRC/"
else
  # Portable copy without shell redirection of content.
  cp -R "$SRC_DIR/." "$BUILD_SRC/"
  rm -rf "$BUILD_SRC/.git" "$BUILD_SRC/zig-out" "$BUILD_SRC/zig-cache" "$BUILD_SRC/.zig-cache"
fi

# Apply numbered patches (sorted). Empty patches/ is OK.
if [[ -d "$PATCH_DIR" ]]; then
  shopt -s nullglob
  patches=("$PATCH_DIR"/*.patch)
  shopt -u nullglob
  if [[ ${#patches[@]} -gt 0 ]]; then
    for p in "${patches[@]}"; do
      echo "Applying patch: $p"
      # Prefer git apply; fall back to patch -p1.
      if ! git -C "$BUILD_SRC" apply --whitespace=nowarn "$p" 2>/dev/null; then
        patch -d "$BUILD_SRC" -p1 <"$p"
      fi
    done
  else
    echo "No patches under $PATCH_DIR"
  fi
fi

# Zig 0.15.2 cannot link the macOS 26 SDK: TBD stubs dropped arm64-macos
# (only arm64e-macos remains). That is a Zig linker bug, not a Ghostty pin
# change. Point DEVELOPER_DIR at a MacOSX 15 SDK so zig ld uses Darwin
# stubs that still export arm64-macos.
maybe_select_macos15_sdk_for_zig_015() {
  [[ "$(uname -s)" == "Darwin" ]] || return 0
  local default_sdk tbd first_targets sdk15 clt_sdks overlay
  default_sdk="$(xcrun --show-sdk-path 2>/dev/null || true)"
  tbd="${default_sdk}/usr/lib/libSystem.tbd"
  [[ -n "$default_sdk" && -f "$tbd" ]] || return 0
  first_targets="$(grep -m1 '^targets:' "$tbd" || true)"
  [[ "$first_targets" == *arm64e-macos* && "$first_targets" != *arm64-macos* ]] || return 0

  clt_sdks="/Library/Developer/CommandLineTools/SDKs"
  if [[ -d "${clt_sdks}/MacOSX15.4.sdk" ]]; then
    sdk15="${clt_sdks}/MacOSX15.4.sdk"
  elif [[ -d "${clt_sdks}/MacOSX15.sdk" ]]; then
    sdk15="${clt_sdks}/MacOSX15.sdk"
  else
    echo "ERROR: Zig $ZIG_VERSION cannot link this macOS 26 SDK (no arm64-macos in libSystem.tbd)." >&2
    echo "Install Command Line Tools SDK MacOSX15.4.sdk, or set DEVELOPER_DIR to a MacOSX 15 SDK." >&2
    echo "Do not bump the Ghostty pin for this linker failure." >&2
    exit 1
  fi

  overlay="$CACHE_ROOT/darwin-developer-macos15"
  mkdir -p "$overlay/SDKs" "$overlay/usr/bin"
  ln -sfn "$sdk15" "$overlay/SDKs/MacOSX.sdk"
  # zig ld uses DEVELOPER_DIR to skip the macOS 26 syslibroot. Ghostty
  # findNative and Apple ranlib still call xcrun. Keep a wrapper at the
  # path ranlib checks: $DEVELOPER_DIR/usr/bin/xcrun.
  cat >"$overlay/usr/bin/xcrun" <<EOF
#!/bin/bash
if [[ " \$* " == *" --show-sdk-path "* ]]; then
  printf '%s\\n' "$sdk15"
  exit 0
fi
if [[ " \$* " == *" --show-sdk-version "* ]]; then
  printf '%s\\n' "15.4"
  exit 0
fi
exec env -u DEVELOPER_DIR /usr/bin/xcrun "\$@"
EOF
  chmod +x "$overlay/usr/bin/xcrun"
  export DEVELOPER_DIR="$overlay"
  export SDKROOT="$sdk15"
  export PATH="$overlay/usr/bin:$PATH"
  echo "NOTE: Zig $ZIG_VERSION cannot link macOS 26 TBD stubs. Using $sdk15 via DEVELOPER_DIR."
}

maybe_select_macos15_sdk_for_zig_015

echo "Building libghostty-vt ($OPTIMIZE) for $RID from pin $GHOSTTY_COMMIT ..."
(
  cd "$BUILD_SRC"
  # -Dcpu=baseline: Zig builds for the host CPU by default. A library built on a
  # runner with AVX-512 crashes with "Illegal instruction" on a CPU without it.
  # -Demit-xcframework=false: avoid xcodebuild on macOS for the spike shared lib.
  "$ZIG_BIN" build \
    -Demit-lib-vt \
    -Dcpu=baseline \
    -Doptimize="$OPTIMIZE" \
    -Demit-xcframework=false \
    --prefix "$BUILD_SRC/zig-out-hypa"
)

# Locate shared library under prefix / zig-out.
CANDIDATES=(
  "$BUILD_SRC/zig-out-hypa/lib/$LIB_NAME"
  "$BUILD_SRC/zig-out/lib/$LIB_NAME"
  "$BUILD_SRC/zig-out-hypa/lib/libghostty-vt.so"
  "$BUILD_SRC/zig-out-hypa/lib/libghostty-vt.dylib"
)
FOUND=""
for c in "${CANDIDATES[@]}"; do
  if [[ -f "$c" ]]; then
    FOUND="$c"
    break
  fi
done
if [[ -z "$FOUND" ]]; then
  echo "ERROR: built library not found. Searched:" >&2
  printf '  %s\n' "${CANDIDATES[@]}" >&2
  find "$BUILD_SRC" -name 'libghostty-vt*' 2>/dev/null | head -20 >&2 || true
  exit 1
fi

cp "$FOUND" "$OUTPUT_PATH"
chmod 755 "$OUTPUT_PATH"

# macOS: ensure install name is loader-relative for beside-apphost layout.
if [[ "$(uname -s)" == "Darwin" ]] && command -v install_name_tool >/dev/null 2>&1; then
  install_name_tool -id "@loader_path/libghostty-vt.dylib" "$OUTPUT_PATH" || true
  # install_name_tool breaks the linker signature. Apple Silicon does not
  # load arm64 code with a broken signature. Sign again before the digest,
  # so the digest covers the signed bytes. An ad-hoc signature is stable.
  codesign --force --sign - "$OUTPUT_PATH"
  codesign --verify "$OUTPUT_PATH"
fi

# Checksum
if command -v shasum >/dev/null 2>&1; then
  shasum -a 256 "$OUTPUT_PATH" | awk '{print $1}' >"$CHECKSUM_PATH"
elif command -v sha256sum >/dev/null 2>&1; then
  sha256sum "$OUTPUT_PATH" | awk '{print $1}' >"$CHECKSUM_PATH"
fi

# --- Symbol gate -----------------------------------------------------------
# Hard gate: missing nm fails the build (no soft skip). CI and local ship gates
# must install binutils / Xcode CLT so ABI symbols are always verified.
REQUIRED_SYMBOLS=(ghostty_build_info ghostty_terminal_new ghostty_terminal_free ghostty_terminal_vt_write)
if ! command -v nm >/dev/null 2>&1; then
  echo "ERROR: nm is required for Ghostty ABI symbol gate (install binutils/Xcode CLT)" >&2
  exit 1
fi
NM_OUT="$(nm -gU "$OUTPUT_PATH" 2>/dev/null || nm -D "$OUTPUT_PATH" 2>/dev/null || nm "$OUTPUT_PATH")"
for sym in "${REQUIRED_SYMBOLS[@]}"; do
  if ! grep -q "$sym" <<<"$NM_OUT"; then
    echo "ERROR: required symbol missing from $OUTPUT_PATH: $sym" >&2
    exit 1
  fi
done
echo "Symbol gate OK: ${REQUIRED_SYMBOLS[*]}"

# --- Undeclared runtime lib gate ------------------------------------------
# Allow only common system libs. Anything else is a spike failure.
check_undeclared_libs() {
  local lib="$1"
  local line dep
  local -a allowed_re=(
    'libghostty-vt'
    'linux-vdso'
    'ld-linux'
    'ld-musl'
    'libc\.so'
    'libm\.so'
    'libdl\.so'
    'libpthread\.so'
    'librt\.so'
    'libgcc_s'
    'libstdc\+\+'
    'libSystem'
    'libobjc'
    'libiconv'
    'libresolv'
    'libunwind'
    'libcxx'
    'libc\+\+'
    'libcompiler_rt'
    '/usr/lib/'
    '/lib/'
    '/System/Library/'
    '\[vdso\]'
  )
  local deps_raw=""
  local deps_rc=0
  local tool=""
  if [[ "$(uname -s)" == "Darwin" ]]; then
    if ! command -v otool >/dev/null 2>&1; then
      echo "ERROR: otool is required for undeclared-lib gate on Darwin" >&2
      return 1
    fi
    tool="otool"
    deps_raw="$(otool -L "$lib" 2>&1)" && deps_rc=0 || deps_rc=$?
  else
    if ! command -v ldd >/dev/null 2>&1; then
      echo "ERROR: ldd is required for undeclared-lib gate on Linux" >&2
      return 1
    fi
    tool="ldd"
    deps_raw="$(ldd "$lib" 2>&1)" && deps_rc=0 || deps_rc=$?
  fi
  if [[ "$deps_rc" -ne 0 ]]; then
    echo "ERROR: $tool failed (exit=$deps_rc) on $lib" >&2
    printf '%s\n' "$deps_raw" >&2
    return 1
  fi
  if ! printf '%s' "$deps_raw" | grep -q '[^[:space:]]'; then
    echo "ERROR: $tool produced empty dependency dump for $lib" >&2
    return 1
  fi
  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    # Skip the library path header line on macOS (first column is self).
    if [[ "$(uname -s)" == "Darwin" ]]; then
      # otool lines: $'\t'path (compatibility ... )
      dep="$(echo "$line" | sed -E 's/^[[:space:]]+//;s/ \(.*//')"
      [[ "$dep" == "$lib" || "$dep" == *libghostty-vt* ]] && continue
      [[ "$dep" == @loader_path/* || "$dep" == @rpath/* ]] && continue
    else
      # ldd: name => path or name => not found
      if [[ "$line" == *"not found"* ]]; then
        echo "ERROR: unresolved dependency: $line" >&2
        return 1
      fi
      dep="$(echo "$line" | awk '{print $1}')"
    fi
    local ok=0
    for re in "${allowed_re[@]}"; do
      if [[ "$dep" =~ $re ]]; then
        ok=1
        break
      fi
    done
    if [[ $ok -eq 0 ]]; then
      echo "ERROR: undeclared runtime dependency: $dep" >&2
      echo "Full dependency dump:" >&2
      echo "$deps_raw" >&2
      return 1
    fi
  done <<<"$deps_raw"
  echo "Undeclared-lib gate OK"
}

check_undeclared_libs "$OUTPUT_PATH"

# Cross-check abi-manifest presence (symbol content already enforced above).
if [[ -f "$ABI_MANIFEST" ]]; then
  echo "ABI manifest: $ABI_MANIFEST (link_mode=dynamic)"
fi

echo "Built: $OUTPUT_PATH"
if [[ -f "$CHECKSUM_PATH" ]]; then
  echo "Checksum: $(cat "$CHECKSUM_PATH") ($CHECKSUM_PATH)"
fi
