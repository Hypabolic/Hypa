#!/bin/sh
# managed by hypa; reinstalling the integration replaces this file.
# HYPA_INTEGRATION_ID=qodercli
# HYPA_INTEGRATION_VERSION=3

[ "${1:-}" = "session" ] || exit 0
[ "${HYPA_ENV:-}" = "1" ] || exit 0
[ -n "${HYPA_RUNTIME_SOCKET:-}" ] || exit 0
[ -n "${HYPA_PANE_ID:-}" ] || exit 0
command -v python3 >/dev/null 2>&1 || exit 0

python3 -c '
import json
import os
import subprocess
import sys
import time

try:
    payload = json.load(sys.stdin)
    session_id = payload.get("session_id")
    if not isinstance(session_id, str) or not session_id:
        raise ValueError
    subprocess.run(
        [
            os.environ.get("HYPA_BIN_PATH") or "hypa",
            "pane", "report-agent-session", os.environ["HYPA_PANE_ID"],
            "--source", "hypa:qodercli", "--agent", "qodercli",
            "--agent-session-id", session_id, "--seq", str(time.time_ns()),
        ],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        timeout=1,
        check=False,
    )
except Exception:
    pass
' 2>/dev/null || true
