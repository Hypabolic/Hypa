#!/usr/bin/env bash
# Mux release pack profile. Gate B.
# One definition of forbidden binaries, verbs, and store path patterns.
# makes the product capability the compile default.
# A build switch is Gate C. Do not add an MSBuild property that no project reads.

mux_profile_assert_known() {
  local profile="${1:-}"
  case "$profile" in
    mux-release|full) return 0 ;;
    *)
      echo "ERROR: unknown pack profile: ${profile:-<empty>} (expected mux-release or full)" >&2
      return 2
      ;;
  esac
}

mux_profile_forbidden_binaries() {
  printf '%s\n' hypa-relay hypa-relay.exe
}

# Placement root: src/Hypa.Placement/Infrastructure/PlacementStatePaths.cs
# Continuity spool: src/Hypa.Continuity/Infrastructure/LocalWorkPackSpool.cs
# Pi report root: src/Hypa.Continuity.Harnesses/Pi/PiResumeReport.cs
mux_profile_store_patterns() {
  printf '%s\n' \
    '*/hypa/placements' \
    '*/hypa/placements/*' \
    '*/hypa/continuity' \
    '*/hypa/continuity/*' \
    '*/.hypa/continuity' \
    '*/.hypa/continuity/*'
}

# Prints nothing today. makes Product the compile default.
# A build switch is Gate C. Do not add an MSBuild property that no project reads.
mux_profile_publish_properties() {
  return 0
}

mux_profile_host_rid() {
  local sys mach
  sys="$(uname -s 2>/dev/null || true)"
  mach="$(uname -m 2>/dev/null || true)"
  case "$sys" in
    Darwin)
      case "$mach" in
        x86_64) printf '%s\n' osx-x64 ;;
        arm64|aarch64) printf '%s\n' osx-arm64 ;;
        *) printf '\n' ;;
      esac
      ;;
    Linux)
      case "$mach" in
        x86_64) printf '%s\n' linux-x64 ;;
        aarch64|arm64) printf '%s\n' linux-arm64 ;;
        *) printf '\n' ;;
      esac
      ;;
    *) printf '\n' ;;
  esac
}

mux_profile_reject_forbidden_binaries() {
  local root="${1:-}"
  local profile="${2:-}"
  if [[ -z "$root" ]]; then
    echo "ERROR: mux_profile_reject_forbidden_binaries requires a root" >&2
    return 1
  fi
  if [[ "$profile" != "mux-release" ]]; then
    echo "    relay_guard=skipped (full profile)"
    return 0
  fi
  local name hit found=0
  while IFS= read -r name; do
    [[ -z "$name" ]] && continue
    while IFS= read -r hit; do
      [[ -z "$hit" ]] && continue
      echo "ERROR: mux-release pack must not include $name ($hit)" >&2
      found=1
    done < <(find "$root" -name "$name" -print)
  done < <(mux_profile_forbidden_binaries)
  if [[ "$found" -ne 0 ]]; then
    return 1
  fi
  echo "    relay_guard=absent"
  return 0
}

mux_profile_export_isolation() {
  local prefix="${1:-}"
  if [[ -z "$prefix" ]]; then
    echo "ERROR: mux_profile_export_isolation requires a prefix" >&2
    return 1
  fi
  export XDG_STATE_HOME="$prefix/xdg-state"
  export XDG_DATA_HOME="$prefix/xdg-data"
  export XDG_CONFIG_HOME="$prefix/xdg-config"
  export HYPA_OPERATOR_HOME="$prefix/operator"
  mkdir -p "$XDG_STATE_HOME" "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" "$HYPA_OPERATOR_HOME"
}

mux_profile_store_roots() {
  printf '%s\n' \
    "${XDG_STATE_HOME}/hypa/placements" \
    "${XDG_STATE_HOME}/hypa/continuity/spool" \
    "${HOME}/.hypa/continuity"
}

mux_profile_hash_file() {
  local f="${1:-}"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$f" | awk '{ print $1 }'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$f" | awk '{ print $1 }'
  else
    echo "ERROR: sha256sum or shasum is required for store canaries" >&2
    return 1
  fi
}

mux_profile_seed_store_sentinels() {
  local record_dir="${1:-}"
  mkdir -p "$record_dir"
  local i=0
  local root
  while IFS= read -r root; do
    [[ -z "$root" ]] && continue
    i=$((i + 1))
    mkdir -p "$root"
    printf 'canary-%s\n' "$i" >"$root/canary"
    mux_profile_hash_file "$root/canary" >"$record_dir/hash.$i"
    find "$root" -print | sort >"$record_dir/list.$i"
    printf '%s\n' "$root" >"$record_dir/root.$i"
  done < <(mux_profile_store_roots)
  printf '%s\n' "$i" >"$record_dir/count"
}

mux_profile_assert_store_sentinels() {
  local record_dir="${1:-}"
  local count i root expected now extra
  count="$(cat "$record_dir/count")"
  i=1
  while [[ "$i" -le "$count" ]]; do
    root="$(cat "$record_dir/root.$i")"
    expected="$(cat "$record_dir/hash.$i")"
    now="$(mux_profile_hash_file "$root/canary")"
    if [[ "$expected" != "$now" ]]; then
      echo "ERROR: Continuity store canary changed: $root/canary" >&2
      return 1
    fi
    find "$root" -print | sort >"$record_dir/list.$i.now"
    if ! cmp -s "$record_dir/list.$i" "$record_dir/list.$i.now"; then
      extra="$(comm -13 "$record_dir/list.$i" "$record_dir/list.$i.now" || true)"
      echo "ERROR: Continuity store listing changed under $root" >&2
      if [[ -n "$extra" ]]; then
        echo "ERROR: new store entry: $extra" >&2
      fi
      return 1
    fi
    i=$((i + 1))
  done
}

mux_profile_path_is_store() {
  local path="${1:-}"
  local pat
  while IFS= read -r pat; do
    [[ -z "$pat" ]] && continue
    case "$path" in
      $pat) return 0 ;;
    esac
  done < <(mux_profile_store_patterns)
  return 1
}

mux_profile_assert_store_trace() {
  local trace="${1:-}"
  local socket_dir="${2:-}"
  if [[ ! -s "$trace" ]]; then
    echo "ERROR: store read proof is void (empty or missing trace)" >&2
    return 1
  fi
  if ! grep -E 'openat\(' "$trace" | grep -F "$socket_dir" >/dev/null; then
    echo "ERROR: store read proof is void (missing socket directory openat)" >&2
    return 1
  fi
  local quoted path
  while IFS= read -r quoted; do
    [[ -z "$quoted" ]] && continue
    path="${quoted#\"}"
    path="${path%\"}"
    if mux_profile_path_is_store "$path"; then
      echo "ERROR: ordinary mux opened Continuity store path $path" >&2
      return 1
    fi
  done < <(grep -oE '"[^"]+"' "$trace" || true)
}

mux_profile_prepare_self_test_trace() {
  local src="${1:-}"
  local dest="${2:-}"
  local socket_dir="${3:-}"
  local socket_path="${4:-}"
  if [[ ! -f "$src" ]]; then
    echo "ERROR: store-proof trace is missing: $src" >&2
    return 1
  fi
  sed -e "s|__SOCKET_DIR__|${socket_dir}|g" -e "s|__SOCKET__|${socket_path}|g" "$src" >"$dest"
}
