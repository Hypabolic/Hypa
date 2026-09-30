#!/usr/bin/env bash
# Emit pack notes for mux-release and full pack profiles. Gate B.
# Continuity is unavailable in this release.
# full-screen. Do not claim native Pi resume. Do not claim two-machine
# handoff.

set -euo pipefail

PROFILE=""
TAG=""

usage() {
  echo "Usage: $0 --profile mux-release|full --tag TAG" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --profile)
      [[ $# -ge 2 ]] || usage
      PROFILE="$2"
      shift 2
      ;;
    --tag)
      [[ $# -ge 2 ]] || usage
      TAG="$2"
      shift 2
      ;;
    -h|--help)
      usage
      ;;
    *)
      echo "ERROR: unknown option: $1" >&2
      usage
      ;;
  esac
done

if [[ -z "$PROFILE" || -z "$TAG" ]]; then
  usage
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib-mux-release-profile.sh
source "$ROOT/scripts/lib-mux-release-profile.sh"
mux_profile_assert_known "$PROFILE" || exit $?

cat <<EOF
Hypa mux release ${TAG}.

This archive ships the mux, local attach, and local reconnect.

Continuity is unavailable in this release.

hypa accepts no rendezvous command.

A full uninstall may still delete ~/.hypa.

The mux never public-binds.

SSH is not the product path.
EOF
