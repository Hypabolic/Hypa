#!/usr/bin/env python3
"""Label a pull request by counted diff size and require a Conventional Commits title.

Counted lines are additions plus deletions. Lockfiles and generated files are
not counted. Warns at 400 counted lines and fails at 1000 unless the pull
request has the size/override label. The logic lives in this file.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import urllib.parse
from dataclasses import dataclass

WARN_LINES = 400
FAIL_LINES = 1000
OVERRIDE_LABEL = "size/override"
SIZE_LABELS = ("size/XS", "size/S", "size/M", "size/L", "size/XL")
LOCKFILES = frozenset(
    {
        "package-lock.json",
        "npm-shrinkwrap.json",
        "yarn.lock",
        "pnpm-lock.yaml",
        "bun.lock",
        "bun.lockb",
        "cargo.lock",
        "poetry.lock",
        "pipfile.lock",
        "uv.lock",
        "composer.lock",
        "gemfile.lock",
        "packages.lock.json",
        "package.resolved",
        "gradle.lockfile",
        "pubspec.lock",
    }
)
TITLE_RE = re.compile(
    r"^(build|chore|ci|docs|feat|fix|perf|refactor|revert|style|test)"
    r"(\([a-z0-9._/-]+\))?!?: [^ ].*$"
)
LABEL_COLOR = {
    "size/XS": "0e8a16",
    "size/S": "2da44e",
    "size/M": "fbca04",
    "size/L": "d93f0b",
    "size/XL": "b60205",
    OVERRIDE_LABEL: "5319e7",
}


@dataclass(frozen=True)
class FileChange:
    filename: str
    additions: int
    deletions: int


@dataclass(frozen=True)
class Outcome:
    counted: int
    excluded: int
    label: str
    title_ok: bool
    title_error: str
    warn: bool
    fail: bool
    fail_reason: str


def is_excluded(path: str) -> bool:
    name = path.rsplit("/", 1)[-1].lower()
    if name in LOCKFILES or name.endswith((".generated.cs", ".g.cs", ".g.i.cs", ".designer.cs")):
        return True
    parts = {part.lower() for part in path.split("/")}
    return bool(parts & {"obj", "bin", "generated"})


def size_label(counted: int) -> str:
    if counted < 10:
        return "size/XS"
    if counted < 100:
        return "size/S"
    if counted < WARN_LINES:
        return "size/M"
    if counted < FAIL_LINES:
        return "size/L"
    return "size/XL"


def evaluate(title: str, files: list[FileChange], labels: list[str]) -> Outcome:
    counted = excluded = 0
    for change in files:
        lines = change.additions + change.deletions
        if is_excluded(change.filename):
            excluded += lines
        else:
            counted += lines
    title_ok = TITLE_RE.fullmatch(title.strip()) is not None
    title_error = ""
    if not title_ok:
        title_error = (
            "Pull request title must be a Conventional Commit "
            "(type(scope): subject). Types: build, chore, ci, docs, feat, "
            "fix, perf, refactor, revert, style, test."
        )
    fail = counted >= FAIL_LINES and OVERRIDE_LABEL not in labels
    return Outcome(
        counted=counted,
        excluded=excluded,
        label=size_label(counted),
        title_ok=title_ok,
        title_error=title_error,
        warn=counted >= WARN_LINES,
        fail=fail,
        fail_reason=(
            f"Counted diff is {counted} lines. The limit is {FAIL_LINES}. "
            "Split the pull request, or add the size/override label."
            if fail
            else ""
        ),
    )


def gh_json(path: str) -> object:
    result = subprocess.run(["gh", "api", path], check=True, capture_output=True, text=True)
    return json.loads(result.stdout)


def gh(*args: str) -> None:
    subprocess.run(["gh", "api", *args], check=True, capture_output=True, text=True)


def pull_request(repo: str, number: int) -> tuple[str, list[str], list[FileChange]]:
    payload = gh_json(f"repos/{repo}/pulls/{number}")
    assert isinstance(payload, dict)
    files: list[FileChange] = []
    page = 1
    while True:
        batch = gh_json(f"repos/{repo}/pulls/{number}/files?per_page=100&page={page}")
        assert isinstance(batch, list)
        for item in batch:
            assert isinstance(item, dict)
            files.append(
                FileChange(str(item["filename"]), int(item.get("additions", 0)), int(item.get("deletions", 0)))
            )
        if len(batch) < 100:
            break
        page += 1
    labels = [str(item["name"]) for item in payload.get("labels", [])]
    return str(payload["title"]), labels, files


def ensure_label(repo: str, name: str) -> None:
    encoded = urllib.parse.quote(name, safe="")
    if subprocess.run(["gh", "api", f"repos/{repo}/labels/{encoded}"], capture_output=True).returncode == 0:
        return
    gh(
        "--method",
        "POST",
        f"repos/{repo}/labels",
        "-f",
        f"name={name}",
        "-f",
        f"color={LABEL_COLOR[name]}",
        "-f",
        f"description={name}",
    )


def apply_size_label(repo: str, number: int, label: str) -> None:
    for name in (*SIZE_LABELS, OVERRIDE_LABEL):
        ensure_label(repo, name)
    current = gh_json(f"repos/{repo}/issues/{number}/labels")
    assert isinstance(current, list)
    present = {str(item["name"]) for item in current if isinstance(item, dict)}
    for name in SIZE_LABELS:
        if name != label and name in present:
            gh("--method", "DELETE", f"repos/{repo}/issues/{number}/labels/{urllib.parse.quote(name, safe='')}")
    if label not in present:
        gh("--method", "POST", f"repos/{repo}/issues/{number}/labels", "-f", f"labels[]={label}")


def label_best_effort(repo: str, number: int, label: str) -> None:
    # A fork pull request gets a read-only token. A label write then fails,
    # but the size and title verdict must still decide the check.
    try:
        apply_size_label(repo, number, label)
    except subprocess.CalledProcessError as error:
        detail = (error.stderr or "").strip().splitlines()
        reason = detail[-1] if detail else f"exit {error.returncode}"
        print(f"::warning::Could not apply {label}: {reason}")


def emit(outcome: Outcome, title: str) -> int:
    print(f"title={title!r} counted={outcome.counted} excluded={outcome.excluded} label={outcome.label}")
    status = 0
    if not outcome.title_ok:
        print(f"::error::{outcome.title_error} Got {title!r}.")
        status = 1
    if outcome.warn and not outcome.fail:
        print(f"::warning::Counted diff is {outcome.counted} lines (warning at {WARN_LINES}).")
    if outcome.fail:
        print(f"::error::{outcome.fail_reason}")
        status = 1
    return status


def load_fixture(path: str) -> tuple[str, list[str], list[FileChange]]:
    with open(path, encoding="utf-8") as handle:
        payload = json.load(handle)
    files = [
        FileChange(str(item["filename"]), int(item.get("additions", 0)), int(item.get("deletions", 0)))
        for item in payload["files"]
    ]
    return str(payload["title"]), list(payload.get("labels", [])), files


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", help="JSON file with title, labels, and files")
    parser.add_argument("--repo", default=os.environ.get("REPO", ""))
    parser.add_argument("--pr", type=int, default=int(os.environ.get("PR_NUMBER") or 0))
    parser.add_argument("--no-label", action="store_true")
    args = parser.parse_args(argv)
    if args.fixture:
        title, labels, files = load_fixture(args.fixture)
    else:
        if not args.repo or not args.pr:
            parser.error("pass --fixture or set REPO and PR_NUMBER")
        title, labels, files = pull_request(args.repo, args.pr)
    outcome = evaluate(title, files, labels)
    if args.fixture is None and not args.no_label:
        label_best_effort(args.repo, args.pr, outcome.label)
    return emit(outcome, title)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
