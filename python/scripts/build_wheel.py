"""Build a platform-specific wheel for hypa.

Usage:
    python build_wheel.py --rid linux-x64 --version 1.2.3 \
        --artifact-dir dist-artifacts --output-dir wheelhouse
"""

import argparse
import glob
import os
import shutil
import subprocess
import sys
import tarfile
import tempfile

RID_TO_WHEEL_TAG = {
    "linux-x64":   "manylinux2014_x86_64",
    "linux-arm64": "manylinux2014_aarch64",
    "osx-x64":     "macosx_10_9_x86_64",
    "osx-arm64":   "macosx_11_0_arm64",
}


def run(*args, **kwargs):
    subprocess.run(args, check=True, **kwargs)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", required=True, choices=RID_TO_WHEEL_TAG)
    parser.add_argument("--version", required=True)
    parser.add_argument("--artifact-dir", required=True)
    parser.add_argument("--output-dir", required=True)
    args = parser.parse_args()

    platform_tag = RID_TO_WHEEL_TAG[args.rid]
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    artifact_dir = os.path.abspath(args.artifact_dir)
    output_dir = os.path.abspath(args.output_dir)
    os.makedirs(output_dir, exist_ok=True)

    archive = os.path.join(artifact_dir, f"hypa-{args.rid}.tar.gz")
    if not os.path.exists(archive):
        print(f"ERROR: archive not found: {archive}", file=sys.stderr)
        sys.exit(1)

    with tempfile.TemporaryDirectory() as staging:
        # 1. Copy python/ layout into staging
        python_src = repo_root
        for item in os.listdir(python_src):
            src = os.path.join(python_src, item)
            dst = os.path.join(staging, item)
            if os.path.isdir(src):
                shutil.copytree(src, dst)
            else:
                shutil.copy2(src, dst)

        # 2. Patch __version__
        init_path = os.path.join(staging, "hypa", "__init__.py")
        with open(init_path, "w") as f:
            f.write(f'__version__ = "{args.version}"\n')

        # 3. Extract binary into hypa/bin/
        bin_dir = os.path.join(staging, "hypa", "bin")
        os.makedirs(bin_dir, exist_ok=True)
        with tarfile.open(archive, "r:gz") as tf:
            for member in tf.getmembers():
                # strip the top-level "hypa-{rid}/" component
                parts = member.name.split("/", 1)
                if len(parts) < 2 or not parts[1]:
                    continue
                member.name = parts[1]
                tf.extract(member, bin_dir)

        # 4. Build a generic wheel
        tmp_dist = os.path.join(staging, "_dist")
        os.makedirs(tmp_dist, exist_ok=True)
        run(sys.executable, "-m", "build", "--wheel", "--outdir", tmp_dist, cwd=staging)

        # 5. Retag to the target platform
        wheels = glob.glob(os.path.join(tmp_dist, "*.whl"))
        if not wheels:
            print("ERROR: no wheel produced by build step", file=sys.stderr)
            sys.exit(1)
        wheel = wheels[0]
        run(
            sys.executable, "-m", "wheel", "tags",
            "--python-tag", "py3",
            "--abi-tag", "none",
            "--platform-tag", platform_tag,
            wheel,
            cwd=tmp_dist,
        )

        # 6. Move retagged wheel to output-dir (wheel tags writes alongside original)
        retagged = [w for w in glob.glob(os.path.join(tmp_dist, "*.whl")) if w != wheel]
        if not retagged:
            # wheel tags may overwrite in-place depending on version; accept original
            retagged = [wheel]
        shutil.move(retagged[0], output_dir)
        print(f"Built: {os.path.basename(retagged[0])}")


if __name__ == "__main__":
    main()
