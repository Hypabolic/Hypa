# Agent instructions for Hypa

Guidance for AI assistants (Claude Code, Copilot, etc.) working in this repo.
For build/test/architecture details see `.github/copilot-instructions.md`.

---

## Commit & PR conventions (these drive the release notes)

Release notes are written from **merged pull requests**: GitHub's generated
notes group them by `.github/release.yml` (category config), and the
hand-written `docs/release-notes/<tag>.md` the Release workflow publishes is
drafted from them. The **PR title becomes the changelog line** and the **PR's
label decides the category**. The repo squash- and rebase-merges (no merge
commits), so a clean PR title is what ends up in both the changelog and git
history. Write accordingly.

### PR titles — write them as changelog entries
- Imperative mood, lead with a verb: **Add / Fix / Remove / Improve / Update / Document / Refactor**.
- Describe the change and its user-facing impact, not the files touched.
  - Good: `Add WAL mode to the SQLite metrics store`
  - Good: `Fix crash when a reducer returns empty output`
  - Bad: `wip`, `updates`, `fix bug`, `changes to CommandRunner.cs`
- Capitalize the first word, no trailing period, keep under ~72 chars.
- **Do not** add a `feat:` / `fix:` prefix — the category comes from the label, not the title.

### PR labels — apply exactly one primary category (first match wins)
| Label | Release-notes section |
| --- | --- |
| `breaking-change` | ⚠️ Breaking Changes |
| `enhancement` / `feature` | 🚀 Features |
| `bug` / `bugfix` | 🐛 Bug Fixes |
| `documentation` | 📚 Documentation |
| `chore` / `dependencies` / `maintenance` / `refactor` | 🧰 Maintenance |
| _(no label)_ | Other Changes — avoid; always label |
| `duplicate` / `invalid` / `wontfix` / `ignore-for-release` | Excluded from notes |

- Anything that changes CLI flags, command output format, `~/.hypa/` config/schema,
  or the public `Hypa.Sdk` API in a backwards-incompatible way gets `breaking-change`,
  and the PR body must describe the migration.

### Commit messages
- Imperative subject ≤72 chars, capitalized, no trailing period.
- Add a body (wrapped ~72) when the *why* isn't obvious; reference issues with
  `Refs #123` / `Closes #123`.
- Because PRs squash-merge, keep individual commit subjects clean too — one of
  them may become the squash title.

### Opening a PR
- Set a clean, changelog-ready title and exactly one primary category label.
- Summarize the change and rationale in the body; call out breaking changes and
  migration steps explicitly.

---

## Cutting a release

1. Open a PR adding `docs/release-notes/vX.Y.Z.md` (label `documentation`;
   copy the layout of the previous version). Merge it once `main` CI is green.
2. Tag the merge commit on `main` and push the tag:
   `git tag -a vX.Y.Z -m "Hypa X.Y.Z" && git push origin vX.Y.Z`.
3. The Release workflow (`.github/workflows/release.yml`) builds the archives
   with `.github/workflows/build-dist.yml` (the same build CI runs), installs
   them on clean hosts, publishes the GitHub Release with the notes file, then
   npm, PyPI, Homebrew and the Hypa.Sdk NuGet package.

To check a release change without publishing anything, run the Release
workflow from the Actions tab with **dry run** ticked (or
`gh workflow run release.yml --ref <branch> -f tag=vX.Y.Z -f dry_run=true`).
It builds the selected branch with that version and stages every channel.
Publishing happens in separate jobs that never run in a dry run, and the jobs
that run the branch's code hold no write scope, OIDC token or secret.

To re-run a real release by hand, dispatch it from the tag itself (choose the
tag under **Use workflow from**). A non-dry dispatch from a branch fails.
