import os
import stat
import sys
from importlib.resources import files

# The wheel carries the whole release directory under hypa/bin/. hypa starts the
# others (attach client, mux host, PTY helper) from beside itself, and pip may
# unpack them without the execute bit.
EXECUTABLES = ("hypa", "hypa-attach", "hypa-annotate", "hypa-runtime", "hypa-pty-host")


def _ensure_executable(path):
    try:
        st = os.stat(path)
    except FileNotFoundError:
        return
    if st.st_mode & stat.S_IXUSR:
        return
    try:
        os.chmod(path, st.st_mode | 0o111)
    except PermissionError:
        raise SystemExit(
            "hypa could not make its bundled executable runnable: "
            f"{path}. This installation may be read-only or "
            "system-managed. Please fix the permissions manually, "
            f"for example: chmod +x {path}"
        )


def main():
    if sys.platform == "win32":
        raise SystemExit(
            "The Hypa mux is not available on Windows. "
            "Build the CLI from source: https://github.com/Hypabolic/Hypa"
        )

    bin_dir = str(files("hypa") / "bin")
    for name in EXECUTABLES:
        _ensure_executable(os.path.join(bin_dir, name))

    binary = os.path.join(bin_dir, "hypa")
    # Replace this process so hypa owns the terminal, signals and exit status.
    try:
        os.execv(binary, [binary, *sys.argv[1:]])
    except OSError as err:
        raise SystemExit(f"hypa could not start {binary}: {err}")
