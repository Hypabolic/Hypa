#!/usr/bin/env bash
# Recorded goldens are a hard prerequisite for the F2 pack.
# This script checks the gate file, the pin, and the digest manifest.
# It does not replay goldens. The packer binds the exact lib hash.
#
# Usage:
#   scripts/verify-h15-f2-prerequisite.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
GATE="$REPO_ROOT/docs/plans/AgentRuntime/f2-golden-gate.md"
PIN="$REPO_ROOT/native/ghostty/PIN.md"
LOADER="$REPO_ROOT/src/Hypa.Terminal/Vt/Ghostty/GhosttyLibraryLoader.cs"
DIGESTS="$REPO_ROOT/native/ghostty/h15-accepted-digests"
PIN_SHA="c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3"

if [[ ! -f "$GATE" ]]; then
  echo "ERROR: missing gate file: $GATE" >&2
  exit 1
fi
if [[ ! -f "$PIN" ]]; then
  echo "ERROR: missing PIN.md: $PIN" >&2
  exit 1
fi
if [[ ! -f "$LOADER" ]]; then
  echo "ERROR: missing GhosttyLibraryLoader.cs: $LOADER" >&2
  exit 1
fi

if ! grep -q 'h15_goldens: recorded' "$GATE"; then
  echo "ERROR: $GATE must contain h15_goldens: recorded" >&2
  exit 1
fi
if grep -q 'h15_goldens: pass' "$GATE"; then
  echo "ERROR: $GATE must not contain h15_goldens: pass" >&2
  exit 1
fi
if grep -q 'h15_goldens: reconstructed-only' "$GATE"; then
  echo "ERROR: $GATE must not contain h15_goldens: reconstructed-only" >&2
  exit 1
fi

if ! grep -q "$PIN_SHA" "$GATE"; then
  echo "ERROR: $GATE must contain pin $PIN_SHA" >&2
  exit 1
fi

PIN_COMMIT=""
if PIN_LINE="$(grep -E '^GHOSTTY_COMMIT=' "$PIN" | head -n 1)"; then
  PIN_COMMIT="${PIN_LINE#GHOSTTY_COMMIT=}"
fi
if [[ -z "$PIN_COMMIT" ]]; then
  echo "ERROR: $PIN must declare GHOSTTY_COMMIT=" >&2
  exit 1
fi
if [[ "$PIN_COMMIT" != "$PIN_SHA" ]]; then
  echo "ERROR: PIN.md commit $PIN_COMMIT != required $PIN_SHA" >&2
  exit 1
fi

LOADER_COMMIT=""
if LOADER_LINE="$(grep -E 'PinnedCommit = "' "$LOADER" | head -n 1)"; then
  LOADER_COMMIT="$(printf '%s' "$LOADER_LINE" | sed -n 's/.*PinnedCommit = "\([0-9a-f]\{40\}\)".*/\1/p')"
fi
if [[ -z "$LOADER_COMMIT" ]]; then
  echo "ERROR: GhosttyLibraryLoader.PinnedCommit was not found" >&2
  exit 1
fi
if [[ "$LOADER_COMMIT" != "$PIN_SHA" ]]; then
  echo "ERROR: GhosttyLibraryLoader.PinnedCommit $LOADER_COMMIT != $PIN_SHA" >&2
  exit 1
fi

if [[ ! -f "$DIGESTS" ]]; then
  echo "ERROR: missing H-15 digest manifest: $DIGESTS" >&2
  exit 1
fi

digest_count=0
while IFS= read -r line || [[ -n "$line" ]]; do
  [[ -z "$line" || "$line" == \#* ]] && continue
  rid="${line%%[[:space:]]*}"
  hash="$(printf '%s' "$line" | awk '{ print $2 }' | tr -d '[:space:]' | tr 'A-F' 'a-f')"
  extra="$(printf '%s' "$line" | awk '{ print $3 }')"
  if [[ -z "$rid" || -z "$hash" ]]; then
    echo "ERROR: $DIGESTS has a malformed digest line" >&2
    exit 1
  fi
  if [[ -n "$extra" ]]; then
    echo "ERROR: $DIGESTS line for $rid has extra fields" >&2
    exit 1
  fi
  if [[ ! "$hash" =~ ^[0-9a-f]{64}$ ]]; then
    echo "ERROR: $DIGESTS $rid digest is not a 64-char sha256" >&2
    exit 1
  fi
  if [[ "$hash" == "$PIN_SHA" ]]; then
    echo "ERROR: $DIGESTS must not use the Ghostty pin SHA as a lib digest" >&2
    exit 1
  fi
  digest_count=$((digest_count + 1))
done <"$DIGESTS"

if [[ "$digest_count" -lt 1 ]]; then
  echo "ERROR: $DIGESTS must record at least one live H-15 lib digest" >&2
  exit 1
fi

echo "OK: H-15 F2 prerequisite (h15_goldens: recorded, pin $PIN_SHA, $digest_count digest(s))"
exit 0
