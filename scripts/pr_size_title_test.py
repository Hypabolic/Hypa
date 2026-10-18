#!/usr/bin/env python3
"""Local proof for the pull request size and title check."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pr_size_title import FAIL_LINES, WARN_LINES, FileChange, evaluate, is_excluded


def files(*rows: tuple[str, int, int]) -> list[FileChange]:
    return [FileChange(name, additions, deletions) for name, additions, deletions in rows]


class SizeTitleTest(unittest.TestCase):
    def test_conventional_title_passes(self) -> None:
        outcome = evaluate("ci: check pull request size", files(("a.cs", 3, 1)), [])
        self.assertTrue(outcome.title_ok)
        self.assertEqual(outcome.counted, 4)
        self.assertEqual(outcome.label, "size/XS")
        self.assertFalse(outcome.warn)
        self.assertFalse(outcome.fail)

    def test_scoped_breaking_title_passes(self) -> None:
        outcome = evaluate("feat(mux)!: restart the server", files(("a.cs", 1, 0)), [])
        self.assertTrue(outcome.title_ok)

    def test_imperative_title_without_type_fails(self) -> None:
        outcome = evaluate("Add WAL mode to the metrics store", files(("a.cs", 1, 0)), [])
        self.assertFalse(outcome.title_ok)
        self.assertIn("Conventional Commit", outcome.title_error)

    def test_warns_at_400_and_fails_at_1000(self) -> None:
        under = evaluate("fix: small", files(("a.cs", WARN_LINES - 1, 0)), [])
        warned = evaluate("fix: medium", files(("a.cs", WARN_LINES, 0)), [])
        limited = evaluate("fix: large", files(("a.cs", FAIL_LINES - 1, 0)), [])
        blocked = evaluate("fix: huge", files(("a.cs", FAIL_LINES, 0)), [])
        self.assertFalse(under.warn)
        self.assertEqual(under.label, "size/M")
        self.assertTrue(warned.warn)
        self.assertFalse(warned.fail)
        self.assertEqual(warned.label, "size/L")
        self.assertTrue(limited.warn)
        self.assertFalse(limited.fail)
        self.assertEqual(limited.label, "size/L")
        self.assertTrue(blocked.fail)
        self.assertEqual(blocked.label, "size/XL")

    def test_override_label_allows_1000_lines(self) -> None:
        outcome = evaluate(
            "refactor: split later",
            files(("a.cs", FAIL_LINES, 40)),
            ["size/override"],
        )
        self.assertEqual(outcome.label, "size/XL")
        self.assertTrue(outcome.warn)
        self.assertFalse(outcome.fail)

    def test_lockfiles_and_generated_files_are_excluded(self) -> None:
        self.assertTrue(is_excluded("packages/pi-hypa/package-lock.json"))
        self.assertTrue(is_excluded("src/Foo.generated.cs"))
        self.assertTrue(is_excluded("src/obj/Debug/Bar.cs"))
        self.assertFalse(is_excluded("src/Hypa.Cli/Program.cs"))
        outcome = evaluate(
            "chore: refresh locks",
            files(
                ("packages/pi-hypa/package-lock.json", 800, 800),
                ("src/Generated/Proxy.cs", 300, 0),
                ("src/Hypa.Cli/Program.cs", 12, 3),
            ),
            [],
        )
        self.assertEqual(outcome.counted, 15)
        self.assertEqual(outcome.excluded, 1900)
        self.assertEqual(outcome.label, "size/S")
        self.assertFalse(outcome.fail)


if __name__ == "__main__":
    unittest.main()
