#!/usr/bin/env python3
"""Publish the Review gate check on a pull request head.

The check fails until copilot-pull-request-reviewer has submitted a review on
the current head SHA and every review thread is resolved. Dependabot passes.
The review-gate/skip label passes a trivial or bot pull request.

The workflow that runs this file checks out the default branch. A pull request
cannot satisfy the gate by editing this script.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass

CHECK_NAME = "Review gate"
SKIP_LABEL = "review-gate/skip"
COPILOT_LOGIN = "copilot-pull-request-reviewer"
DEPENDABOT_LOGIN = "dependabot"
SUBMITTED = frozenset({"COMMENTED", "APPROVED", "CHANGES_REQUESTED"})
SUMMARY_LIMIT = 65535
PAGE_LIMIT = 10
API = "https://api.github.com"

THREADS_QUERY = """
query($owner: String!, $name: String!, $number: Int!, $cursor: String) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      reviewThreads(first: 100, after: $cursor) {
        pageInfo { hasNextPage endCursor }
        nodes {
          isResolved
          comments(first: 1) { nodes { url path } }
        }
      }
    }
  }
}
"""


@dataclass(frozen=True)
class Thread:
    resolved: bool
    path: str
    url: str


@dataclass(frozen=True)
class Review:
    login: str
    user_type: str
    state: str
    commit_id: str


@dataclass(frozen=True)
class Pull:
    number: int
    head_sha: str
    author_login: str
    author_type: str
    labels: tuple[str, ...]
    reviews: tuple[Review, ...]
    threads: tuple[Thread, ...]


@dataclass(frozen=True)
class Decision:
    passed: bool
    message: str


@dataclass(frozen=True)
class PostedCheck:
    check_id: int
    status: str
    conclusion: str
    summary: str


def canonical_login(login: str) -> str:
    lowered = login.strip().lower()
    if lowered.endswith("[bot]"):
        lowered = lowered[: -len("[bot]")]
    return lowered


def is_copilot(login: str, user_type: str) -> bool:
    return user_type == "Bot" and canonical_login(login) == COPILOT_LOGIN


def is_dependabot(login: str, user_type: str) -> bool:
    return user_type == "Bot" and canonical_login(login) == DEPENDABOT_LOGIN


def limit_summary(text: str, limit: int = SUMMARY_LIMIT) -> str:
    encoded = text.encode("utf-8")
    if len(encoded) <= limit:
        return text
    suffix = "\n\n… summary truncated to the Checks API limit."
    budget = limit - len(suffix.encode("utf-8"))
    if budget < 0:
        return suffix.encode("utf-8")[:limit].decode("utf-8", errors="ignore")
    clipped = encoded[:budget]
    while clipped:
        try:
            return clipped.decode("utf-8") + suffix
        except UnicodeDecodeError:
            clipped = clipped[:-1]
    return suffix.encode("utf-8")[:limit].decode("utf-8", errors="ignore")


def decision_for(pull: Pull) -> Decision:
    if is_dependabot(pull.author_login, pull.author_type):
        return Decision(True, f"Dependabot pull requests pass the review gate (#{pull.number}).")
    if SKIP_LABEL in pull.labels:
        return Decision(True, f"{SKIP_LABEL} is set on #{pull.number}.")
    reviewed = any(
        is_copilot(review.login, review.user_type)
        and review.state in SUBMITTED
        and review.commit_id == pull.head_sha
        for review in pull.reviews
    )
    unresolved = tuple(
        sorted(
            (thread for thread in pull.threads if not thread.resolved),
            key=lambda item: (item.path, item.url),
        )
    )
    parts: list[str] = []
    if not reviewed:
        parts.append(f"#{pull.number} is waiting for a review from {COPILOT_LOGIN} on {pull.head_sha}.")
    if unresolved:
        lines = [f"#{pull.number} has {len(unresolved)} unresolved review thread(s):"]
        lines.extend(f"- {thread.path} {thread.url}".rstrip() for thread in unresolved)
        parts.append("\n".join(lines))
    if parts:
        return Decision(False, "\n".join(parts))
    return Decision(
        True,
        f"#{pull.number} has a Copilot review on {pull.head_sha} and every review thread is resolved.",
    )


def combine(decisions: list[Decision]) -> Decision:
    if not decisions:
        return Decision(False, "Review gate has no pull request to evaluate.")
    failed = [decision for decision in decisions if not decision.passed]
    chosen = failed or decisions
    passed = not failed
    message = "\n".join(decision.message for decision in sorted(chosen, key=lambda item: item.message))
    return Decision(passed, limit_summary(message))


def needs_new_check(decision: Decision, checks: list[PostedCheck]) -> bool:
    completed = [check for check in checks if check.status == "completed"]
    if not completed:
        return True
    latest = max(completed, key=lambda check: check.check_id)
    if (latest.conclusion == "success") != decision.passed:
        return True
    return latest.summary.strip() != decision.message.strip()


def check_run_request(head_sha: str, decision: Decision) -> dict[str, object]:
    title = "Review gate passed" if decision.passed else "Review gate failed"
    return {
        "name": CHECK_NAME,
        "head_sha": head_sha,
        "status": "completed",
        "conclusion": "success" if decision.passed else "failure",
        "output": {"title": title, "summary": decision.message},
    }


def consistent_groups(
    listed: list[tuple[int, str]],
    views: dict[int, Pull],
    errors: dict[int, BaseException],
) -> dict[str, Decision] | None:
    grouped: dict[str, list[Decision]] = {}
    for number, sha in listed:
        error = errors.get(number)
        if error is not None:
            grouped.setdefault(sha, []).append(Decision(False, f"Could not evaluate #{number}: {error}"))
            continue
        view = views.get(number)
        if view is None or view.head_sha != sha:
            return None
        grouped.setdefault(sha, []).append(decision_for(view))
    return {sha: combine(items) for sha, items in grouped.items()}


class GitHub:
    def __init__(self, repo: str, token: str) -> None:
        self.repo = repo
        self.token = token

    def request(self, method: str, path: str, body: dict[str, object] | None = None) -> object:
        url = path if path.startswith("https://") else API + path
        data = None if body is None else json.dumps(body).encode("utf-8")
        req = urllib.request.Request(url, data=data, method=method)
        req.add_header("Accept", "application/vnd.github+json")
        req.add_header("Authorization", f"Bearer {self.token}")
        req.add_header("X-GitHub-Api-Version", "2022-11-28")
        req.add_header("User-Agent", "hypa-review-gate")
        if body is not None:
            req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=30) as response:
                raw = response.read().decode("utf-8")
        except urllib.error.HTTPError as error:
            detail = error.read().decode("utf-8", errors="replace")[:500]
            raise RuntimeError(f"GitHub {method} {path} failed ({error.code}): {detail}") from error
        except (urllib.error.URLError, TimeoutError) as error:
            raise RuntimeError(f"GitHub {method} {path} failed: {error}") from error
        if not raw:
            return None
        return json.loads(raw)

    def paginate(self, path: str) -> list[object]:
        items: list[object] = []
        separator = "&" if "?" in path else "?"
        for page in range(1, PAGE_LIMIT + 1):
            batch = self.request("GET", f"{path}{separator}per_page=100&page={page}")
            if not isinstance(batch, list):
                raise RuntimeError(f"GitHub GET {path} did not return a list")
            items.extend(batch)
            if len(batch) < 100:
                return items
        raise RuntimeError(f"GitHub GET {path} exceeded {PAGE_LIMIT} pages")

    def list_open(self) -> list[tuple[int, str]]:
        listed: list[tuple[int, str]] = []
        for item in self.paginate(f"/repos/{self.repo}/pulls?state=open"):
            if not isinstance(item, dict):
                continue
            head = item.get("head")
            sha = head.get("sha") if isinstance(head, dict) else ""
            listed.append((int(item["number"]), str(sha or "")))
        return listed

    def load_pull(self, number: int) -> Pull:
        payload = self.request("GET", f"/repos/{self.repo}/pulls/{number}")
        if not isinstance(payload, dict):
            raise RuntimeError(f"pull request #{number} did not return an object")
        user = payload.get("user") if isinstance(payload.get("user"), dict) else {}
        head = payload.get("head") if isinstance(payload.get("head"), dict) else {}
        labels = tuple(
            str(label.get("name", ""))
            for label in payload.get("labels", [])
            if isinstance(label, dict)
        )
        reviews = tuple(
            Review(
                login=str((item.get("user") or {}).get("login", "")),
                user_type=str((item.get("user") or {}).get("type", "")),
                state=str(item.get("state", "")),
                commit_id=str(item.get("commit_id") or ""),
            )
            for item in self.paginate(f"/repos/{self.repo}/pulls/{number}/reviews")
            if isinstance(item, dict)
        )
        return Pull(
            number=number,
            head_sha=str(head.get("sha") or ""),
            author_login=str(user.get("login", "")),
            author_type=str(user.get("type", "")),
            labels=labels,
            reviews=reviews,
            threads=tuple(self.threads(number)),
        )

    def threads(self, number: int) -> list[Thread]:
        owner, name = self.repo.split("/", 1)
        cursor: str | None = None
        found: list[Thread] = []
        for _ in range(PAGE_LIMIT):
            payload = self.request(
                "POST",
                "/graphql",
                {
                    "query": THREADS_QUERY,
                    "variables": {"owner": owner, "name": name, "number": number, "cursor": cursor},
                },
            )
            if not isinstance(payload, dict):
                raise RuntimeError(f"review threads for #{number} did not return an object")
            if payload.get("errors"):
                raise RuntimeError(f"review threads for #{number} failed: {payload['errors']}")
            repository = (payload.get("data") or {}).get("repository") or {}
            pull = repository.get("pullRequest") or {}
            connection = pull.get("reviewThreads") or {}
            for node in connection.get("nodes") or []:
                if not isinstance(node, dict):
                    continue
                comments = ((node.get("comments") or {}).get("nodes") or [{}])
                comment = comments[0] if comments else {}
                found.append(
                    Thread(
                        resolved=bool(node.get("isResolved")),
                        path=str((comment or {}).get("path") or ""),
                        url=str((comment or {}).get("url") or ""),
                    )
                )
            page = connection.get("pageInfo") or {}
            if not page.get("hasNextPage"):
                return found
            cursor = str(page.get("endCursor") or "")
        raise RuntimeError(f"review threads for #{number} exceeded {PAGE_LIMIT} pages")

    def posted_checks(self, sha: str) -> list[PostedCheck]:
        quoted = urllib.parse.quote(sha, safe="")
        name = urllib.parse.quote(CHECK_NAME)
        payload = self.request(
            "GET",
            f"/repos/{self.repo}/commits/{quoted}/check-runs?check_name={name}&filter=latest&per_page=100",
        )
        runs = payload.get("check_runs", []) if isinstance(payload, dict) else []
        checks: list[PostedCheck] = []
        for run in runs:
            if not isinstance(run, dict) or run.get("name") != CHECK_NAME:
                continue
            output = run.get("output") if isinstance(run.get("output"), dict) else {}
            checks.append(
                PostedCheck(
                    check_id=int(run.get("id") or 0),
                    status=str(run.get("status") or ""),
                    conclusion=str(run.get("conclusion") or ""),
                    summary=str(output.get("summary") or ""),
                )
            )
        return checks

    def publish(self, sha: str, decision: Decision) -> None:
        self.request("POST", f"/repos/{self.repo}/check-runs", check_run_request(sha, decision))


def sync(github: GitHub) -> dict[str, Decision]:
    last: list[tuple[int, str]] = []
    for _ in range(3):
        listed = github.list_open()
        last = listed
        views: dict[int, Pull] = {}
        errors: dict[int, BaseException] = {}
        for number, _sha in listed:
            try:
                views[number] = github.load_pull(number)
            except Exception as error:  # isolated per pull request; the head still fails closed
                errors[number] = error
        grouped = consistent_groups(listed, views, errors)
        if grouped is None:
            continue
        if sorted(github.list_open()) != sorted(listed):
            continue
        return grouped
    failed: dict[str, list[Decision]] = {}
    for number, sha in last:
        failed.setdefault(sha, []).append(
            Decision(False, f"Could not confirm a consistent snapshot for #{number} ({sha}).")
        )
    return {sha: combine(items) for sha, items in failed.items()}


def head_from_listing(github: GitHub, number: int) -> str:
    try:
        for peer, sha in github.list_open():
            if peer == number:
                return sha
    except Exception:
        return ""
    return ""


def evaluate_number(github: GitHub, number: int, fallback_sha: str = "") -> tuple[str, Decision]:
    try:
        view = github.load_pull(number)
    except Exception as error:
        sha = fallback_sha or head_from_listing(github, number)
        message = limit_summary(f"Could not evaluate #{number}: {error}")
        if not sha:
            return "", Decision(False, message)
        return sha, Decision(False, message)
    sha = view.head_sha
    if not sha:
        return "", Decision(False, f"#{number} has no head SHA.")
    peers = [peer for peer, peer_sha in github.list_open() if peer_sha == sha]
    if number not in peers:
        peers.append(number)
    decisions: list[Decision] = []
    for peer in peers:
        try:
            loaded = view if peer == number else github.load_pull(peer)
        except Exception as error:
            decisions.append(Decision(False, f"Could not evaluate #{peer}: {error}"))
            continue
        if loaded.head_sha != sha:
            decisions.append(Decision(False, f"#{peer} is no longer on {sha}."))
            continue
        decisions.append(decision_for(loaded))
    return sha, combine(decisions)


def publish_if_needed(github: GitHub, sha: str, decision: Decision) -> None:
    if not needs_new_check(decision, github.posted_checks(sha)):
        print(f"{sha} {'pass' if decision.passed else 'fail'}; check is current")
        return
    github.publish(sha, decision)
    print(f"published {'pass' if decision.passed else 'fail'} on {sha}")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    parser.add_argument("--pr", type=int, default=int(os.environ.get("PR_NUMBER") or 0))
    parser.add_argument("--head", default=os.environ.get("HEAD_SHA", ""))
    parser.add_argument("--publish", action="store_true", default=os.environ.get("PUBLISH") == "1")
    args = parser.parse_args(argv)
    if not args.repo:
        parser.error("set GITHUB_REPOSITORY")
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN") or ""
    if args.publish and not token:
        print("::error::GITHUB_TOKEN is required to publish Review gate", file=sys.stderr)
        return 1
    github = GitHub(args.repo, token)
    try:
        if args.pr:
            sha, decision = evaluate_number(github, args.pr, args.head)
            if not sha:
                print(f"::error::{decision.message}", file=sys.stderr)
                return 1
            targets = {sha: decision}
        else:
            targets = sync(github)
    except Exception as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    publish_failed = False
    for sha, decision in sorted(targets.items()):
        state = "pass" if decision.passed else "fail"
        print(f"{state} {sha} {decision.message.splitlines()[0]}")
        if not args.publish:
            continue
        try:
            publish_if_needed(github, sha, decision)
        except Exception as error:
            publish_failed = True
            print(f"::error::Could not publish Review gate on {sha}: {error}", file=sys.stderr)
    if publish_failed:
        return 1
    if args.publish:
        return 0
    return 0 if all(decision.passed for decision in targets.values()) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
