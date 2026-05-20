import os
import stat
import subprocess
import sys
from importlib.resources import files


def main():
    ext = ".exe" if sys.platform == "win32" else ""
    binary_path = str(files("hypa") / "bin" / f"hypa{ext}")

    # pip does not preserve execute bits in wheels — self-fix on first run
    if sys.platform != "win32":
        st = os.stat(binary_path)
        if not (st.st_mode & stat.S_IXUSR):
            try:
                os.chmod(binary_path, st.st_mode | 0o111)
            except PermissionError:
                raise SystemExit(
                    "hypa could not make its bundled executable runnable: "
                    f"{binary_path}. This installation may be read-only or "
                    "system-managed. Please fix the permissions manually, "
                    f"for example: chmod +x {binary_path}"
                )

    result = subprocess.run(
        [binary_path, *sys.argv[1:]],
        env={**os.environ, "HYPA_INSTALL_SOURCE": "pip"},
    )
    sys.exit(result.returncode)
