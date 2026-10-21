#!/usr/bin/env python3
"""Replay an xterm log into cells. Modes: find, invite, dump."""
import re, sys
COLS, ROWS = 110, 36

def replay(text):
    grid = [[" "] * COLS for _ in range(ROWS)]
    row = col = i = 0
    n = len(text)
    while i < n:
        if text[i] == "\x1b" and i + 1 < n and text[i + 1] == "[":
            m = re.match(r"\x1b\[(\d*);(\d*)H", text[i:])
            if m:
                row, col = max(0, int(m.group(1) or 1) - 1), max(0, int(m.group(2) or 1) - 1)
                i += m.end(); continue
            m = re.match(r"\x1b\[(\d*)([CD])", text[i:])
            if m:
                step = int(m.group(1) or 1)
                col = col + step if m.group(2) == "C" else max(0, col - step)
                i += m.end(); continue
            m = re.match(r"\x1b\[[0-9;?]*[ -/]*[@-~]", text[i:])
            if m:
                i += m.end(); continue
        if text[i] == "\x1b" and i + 1 < n and text[i + 1] == "]":
            bel, st = text.find("\x07", i), text.find("\x1b\\", i)
            ends = [x for x in (bel, st) if x != -1]
            i = (min(ends) + (1 if min(ends) == bel else 2)) if ends else i + 1
            continue
        if text[i] == "\x1b":
            i += 1; continue
        ch = text[i]; i += 1
        if ch == "\n":
            row, col = min(ROWS - 1, row + 1), 0; continue
        if ch == "\r":
            col = 0; continue
        if ord(ch) < 32:
            continue
        if row < ROWS and col < COLS:
            grid[row][col] = ch
        col += 1
        if col >= COLS:
            col, row = 0, min(ROWS - 1, row + 1)
    return grid

def main():
    mode, path = sys.argv[1], sys.argv[2]
    grid = replay(open(path, encoding="latin1").read())
    if mode == "dump":
        sys.stdout.write("\n".join("".join(r).rstrip() for r in grid) + "\n"); return
    if mode == "find":
        needle = sys.argv[3]
        for r in range(len(grid) - 1, -1, -1):
            p = "".join(grid[r]).rfind(needle)
            if p >= 0:
                print(r + 1, p + 1 + len(needle) // 2); return
        sys.exit(1)
    import base64, json
    plain = re.sub(r"\x1b\[[0-9;?]*[ -/]*[@-~]", "", open(path, encoding="latin1").read())
    best = ""
    for token in re.findall(r"hypa-invite:[A-Za-z0-9_-]+", plain):
        body = token.split(":", 1)[1]
        padded = body + "=" * ((4 - len(body) % 4) % 4)
        try:
            doc = json.loads(base64.urlsafe_b64decode(padded))
        except (ValueError, json.JSONDecodeError):
            continue
        if isinstance(doc, dict) and doc.get("type") == "host_invite" and len(token) > len(best):
            best = token
    if not best:
        sys.exit(1)
    print(best)

if __name__ == "__main__":
    main()
