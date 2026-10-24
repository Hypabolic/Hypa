#!/usr/bin/env python3
"""Decisions for the Review gate."""

from __future__ import annotations

import io
import os
import sys
import unittest
import urllib.error
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import review_gate
from review_gate import (
    CHECK_NAME,
    Decision,
    PostedCheck,
    Pull,
    Review,
    Thread,
    check_run_request,
    combine,
    consistent_groups,
    decision_for,
    evaluate_number,
    is_copilot,
    is_dependabot,
    limit_summary,
    needs_new_check,
    publish_if_needed,
)


def pull(**overrides: object) -> Pull:
    base: dict[str, object] = {
        "number": 7,
        "head_sha": "abc",
        "author_login": "octocat",
        "author_type": "User",
        "labels": (),
        "reviews": (),
        "threads": (),
    }
    base.update(overrides)
    return Pull(**base)  # type: ignore[arg-type]


def copilot(commit_id: str = "abc", state: str = "COMMENTED") -> Review:
    return Review("copilot-pull-request-reviewer[bot]", "Bot", state, commit_id)


class ReviewGateTest(unittest.TestCase):
    def test_copilot_on_the_head_passes_when_threads_are_resolved(self) -> None:
        decision = decision_for(pull(reviews=(copilot(),), threads=(Thread(True, "a.py", "https://example/1"),)))
        self.assertTrue(decision.passed)

    def test_review_on_an_older_sha_does_not_count(self) -> None:
        decision = decision_for(pull(reviews=(copilot("old"),)))
        self.assertFalse(decision.passed)
        self.assertIn("abc", decision.message)

    def test_dismissed_review_does_not_count(self) -> None:
        self.assertFalse(decision_for(pull(reviews=(copilot(state="DISMISSED"),))).passed)

    def test_human_named_copilot_does_not_pass(self) -> None:
        review = Review("someone-copilot", "User", "COMMENTED", "abc")
        self.assertFalse(is_copilot(review.login, review.user_type))
        self.assertFalse(decision_for(pull(reviews=(review,))).passed)

    def test_unresolved_thread_fails(self) -> None:
        decision = decision_for(
            pull(reviews=(copilot(),), threads=(Thread(False, "a.py", "https://example/thread"),))
        )
        self.assertFalse(decision.passed)
        self.assertIn("https://example/thread", decision.message)

    def test_dependabot_passes_and_a_human_with_that_name_does_not(self) -> None:
        bot = pull(author_login="dependabot[bot]", author_type="Bot")
        human = pull(author_login="dependabot", author_type="User")
        self.assertTrue(is_dependabot("dependabot[bot]", "Bot"))
        self.assertTrue(decision_for(bot).passed)
        self.assertFalse(decision_for(human).passed)

    def test_skip_label_passes_only_that_pull_request(self) -> None:
        skipped = decision_for(pull(labels=("review-gate/skip",)))
        waiting = decision_for(pull(number=8))
        self.assertTrue(skipped.passed)
        combined = combine([skipped, waiting])
        self.assertFalse(combined.passed)
        self.assertNotIn("review-gate/skip", combined.message)

    def test_summary_stays_within_the_checks_api_byte_limit(self) -> None:
        text = ("é" * 40000) + " https://example/thread"
        limited = limit_summary(text, limit=120)
        self.assertLessEqual(len(limited.encode("utf-8")), 120)
        self.assertTrue(limited.endswith("Checks API limit."))
        decision = combine([Decision(False, text)])
        self.assertLessEqual(len(decision.message.encode("utf-8")), review_gate.SUMMARY_LIMIT)
        request = check_run_request("abc", decision)
        output = request["output"]
        assert isinstance(output, dict)
        self.assertEqual(output["summary"], decision.message)
        self.assertEqual(request["name"], CHECK_NAME)
        self.assertEqual(request["conclusion"], "failure")

    def test_needs_new_check_compares_conclusion_and_summary(self) -> None:
        decision = Decision(True, "ok")
        self.assertTrue(needs_new_check(decision, []))
        current = PostedCheck(3, "completed", "success", "ok")
        self.assertFalse(needs_new_check(decision, [current, PostedCheck(1, "completed", "failure", "old")]))
        self.assertTrue(needs_new_check(decision, [PostedCheck(4, "completed", "failure", "ok")]))
        self.assertTrue(needs_new_check(decision, [PostedCheck(4, "completed", "success", "stale")]))

    def test_moved_head_is_not_grouped_with_the_old_listing(self) -> None:
        listed = [(1, "aaa"), (2, "aaa")]
        views = {1: pull(number=1, head_sha="bbb", reviews=(copilot("bbb"),)), 2: pull(number=2, head_sha="aaa")}
        self.assertIsNone(consistent_groups(listed, views, {}))

    def test_evaluation_error_fails_that_head_without_dropping_the_other(self) -> None:
        listed = [(1, "aaa"), (2, "bbb")]
        views = {2: pull(number=2, head_sha="bbb", reviews=(copilot("bbb"),))}
        grouped = consistent_groups(listed, views, {1: RuntimeError("boom")})
        assert grouped is not None
        self.assertFalse(grouped["aaa"].passed)
        self.assertIn("boom", grouped["aaa"].message)
        self.assertTrue(grouped["bbb"].passed)

    def test_shared_head_fails_when_any_pull_request_fails(self) -> None:
        class Fake:
            def __init__(self) -> None:
                self.published: list[tuple[str, Decision]] = []

            def load_pull(self, number: int) -> Pull:
                if number == 1:
                    return pull(number=1, reviews=(copilot(),))
                if number == 2:
                    raise RuntimeError("threads unreadable")
                raise AssertionError(number)

            def list_open(self) -> list[tuple[int, str]]:
                return [(1, "abc"), (2, "abc")]

            def posted_checks(self, sha: str) -> list[PostedCheck]:
                return [PostedCheck(9, "completed", "success", "stale success")]

            def publish(self, sha: str, decision: Decision) -> None:
                self.published.append((sha, decision))

        fake = Fake()
        sha, decision = evaluate_number(fake, 1)  # type: ignore[arg-type]
        self.assertEqual(sha, "abc")
        self.assertFalse(decision.passed)
        with redirect_stdout(io.StringIO()):
            publish_if_needed(fake, sha, decision)  # type: ignore[arg-type]
        self.assertEqual(fake.published[0][0], "abc")
        self.assertFalse(fake.published[0][1].passed)

    def test_open_list_failure_fails_the_known_head(self) -> None:
        class Fake:
            def load_pull(self, number: int) -> Pull:
                return pull(number=number, labels=("review-gate/skip",))

            def list_open(self) -> list[tuple[int, str]]:
                raise RuntimeError("labels unreadable")

        sha, decision = evaluate_number(Fake(), 7)  # type: ignore[arg-type]
        self.assertEqual(sha, "abc")
        self.assertFalse(decision.passed)
        self.assertIn("labels unreadable", decision.message)

    def test_fetch_failure_still_returns_a_failing_decision_for_the_head(self) -> None:
        class Fake:
            def load_pull(self, number: int) -> Pull:
                raise RuntimeError("GitHub GET failed: timed out")

            def list_open(self) -> list[tuple[int, str]]:
                return []

        sha, decision = evaluate_number(Fake(), 4, "deadbeef")  # type: ignore[arg-type]
        self.assertEqual(sha, "deadbeef")
        self.assertFalse(decision.passed)
        self.assertIn("timed out", decision.message)

    def test_urlerror_becomes_runtime_error(self) -> None:
        github = review_gate.GitHub("o/r", "token")
        with mock.patch.object(review_gate.urllib.request, "urlopen", side_effect=urllib.error.URLError("timed out")):
            with self.assertRaises(RuntimeError) as caught:
                github.request("GET", "/repos/o/r/pulls/1")
        self.assertIn("timed out", str(caught.exception))

    def test_http_error_includes_the_status(self) -> None:
        github = review_gate.GitHub("o/r", "token")
        error = urllib.error.HTTPError(
            "https://api.github.com/repos/o/r/pulls/1",
            502,
            "bad gateway",
            hdrs=None,  # type: ignore[arg-type]
            fp=io.BytesIO(b"unavailable"),
        )
        with mock.patch.object(review_gate.urllib.request, "urlopen", side_effect=error):
            with self.assertRaises(RuntimeError) as caught:
                github.request("GET", "/repos/o/r/pulls/1")
        self.assertIn("502", str(caught.exception))

    def test_missing_token_does_not_publish(self) -> None:
        out = io.StringIO()
        with mock.patch.dict(os.environ, {"GITHUB_TOKEN": "", "GH_TOKEN": ""}, clear=False):
            with redirect_stderr(out):
                status = review_gate.main(["--repo", "o/r", "--publish"])
        self.assertEqual(status, 1)
        self.assertIn("GITHUB_TOKEN", out.getvalue())

    def test_local_run_returns_the_decision_without_publishing(self) -> None:
        ready = pull(reviews=(copilot(),))

        class Fake:
            def __init__(self) -> None:
                self.published = 0

            def load_pull(self, number: int) -> Pull:
                return ready

            def list_open(self) -> list[tuple[int, str]]:
                return [(7, "abc")]

            def publish(self, sha: str, decision: Decision) -> None:
                self.published += 1

        fake = Fake()
        with mock.patch.object(review_gate, "GitHub", return_value=fake):
            with redirect_stdout(io.StringIO()):
                status = review_gate.main(["--repo", "o/r", "--pr", "7"])
        self.assertEqual(status, 0)
        self.assertEqual(fake.published, 0)


if __name__ == "__main__":
    unittest.main()
