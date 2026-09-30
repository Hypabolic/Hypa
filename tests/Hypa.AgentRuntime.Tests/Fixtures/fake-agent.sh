#!/bin/sh
# Fake agent for recipe tests.
# agent start accepts this script as --command.
# Args: <socket> <hypa-binary-or-dll>
# Reports working, idle, and blocked with source fixture:fake-agent.
# HYPA_PANE_ID comes from the pane environment.

set -u

socket="${1:?socket path}"
cli="${2:?hypa binary or dll}"
pane="${HYPA_PANE_ID:?HYPA_PANE_ID}"
source="fixture:fake-agent"
agent="fake"

hypa() {
  if [ "${cli##*.}" = "dll" ]; then
    dotnet exec "$cli" "$@"
  else
    "$cli" "$@"
  fi
}

report() {
  state="$1"
  seq="$2"
  attempt=0
  while [ "$attempt" -lt 20 ]; do
    if hypa --session default --socket "$socket" \
      pane report-agent "$pane" \
      --source "$source" \
      --agent "$agent" \
      --state "$state" \
      --seq "$seq" >/dev/null 2>&1
    then
      return 0
    fi
    attempt=$((attempt + 1))
    sleep 0.2
  done
  echo "fake-agent: report $state failed" >&2
  return 1
}

report working 1 || exit 1
report idle 2 || exit 1

while IFS= read -r line; do
  [ -n "$line" ] || continue
  report working 3 || exit 1
  printf '%s\n' "$line"
  report blocked 4 || exit 1
  break
done

while true; do
  sleep 30
done
