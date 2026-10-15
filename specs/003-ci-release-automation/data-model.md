# Data Model: CI and Automated GitHub Releases

No database; the "entities" are Git/GitHub objects and repository files.

## Version tag

| Field | Rule |
|-------|------|
| name | `^v(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$`; anything else → release run stops before building |
| version | name without `v` (e.g. `1.2.0-rc.1`) |
| numericVersion | `MAJOR.MINOR.PATCH` (assembly/file version base) |
| isPrerelease | true when a suffix is present |
| commit | must be an ancestor of `origin/main` (FR-008) |

Protected by `release-tags.json`: only the owner creates; no update, move or delete (FR-003c).

## Release notes section

| Field | Rule |
|-------|------|
| heading | first line `## <version>` followed by end of line, space or ` — …` |
| body | lines after the heading up to the next `## ` heading or end of file |
| required | missing section → release run fails in `validate` (FR-011) |

Source: `docs/release-notes.md` at the tagged commit.

## Release

| Field | Value |
|-------|-------|
| tag | the version tag |
| title | `HddBackup <version>` |
| draft | always true when created (FR-014) |
| prerelease | `isPrerelease` (FR-012) |
| body | pending-checklist banner + notes section + checksum block (FR-010, FR-014) |
| assets | `HddBackup.exe`, `HddBackup.exe.config`, `SHA256SUMS` |

**State transitions**:

```text
(none) --release.yml publish job--> Draft --owner publishes after checklist--> Published
```

- Existing Draft or Published for the same tag → the run fails and changes nothing (FR-013).
- Only the owner moves Draft → Published, after adding the checklist results and removing the
  banner.
- Published is final: with immutable releases on, the tag and assets cannot change. A fix ships
  as a new version.

## SHA256SUMS

Plain text, one line per asset, in `sha256sum` format: `<64 hex>  <file name>` (two spaces).
It covers `HddBackup.exe` and `HddBackup.exe.config`. The exe line is repeated in the release
body.

## Check run

| Field | Value |
|-------|-------|
| trigger | `pull_request` → main, `push` → main, `workflow_dispatch` |
| jobs | `build-test` (Windows), `ext4-check` (Ubuntu, needs `build-test`) |
| required for merge | both job names (`main.json`) |
| artifacts | `hddbackup-exe` (exe + config, 14 days), `test-results` (TRX, 14 days), `ext4-exports` (images, 1 day) |
| version stamped | `0.0.0-ci.<run_number>` |

## Protection rule set

| File | Target | Rules | Bypass |
|------|--------|-------|--------|
| `.github/rulesets/main.json` | default branch | deletion, non_fast_forward, required_linear_history, pull_request (0 approvals, squash only), required_status_checks (`build-test`, `ext4-check`) | RepositoryRole 5 (admin), pull_request only |
| `.github/rulesets/release-tags.json` | `refs/tags/v*` | creation, update, deletion, non_fast_forward | RepositoryRole 5 (admin), always |

Identity: ruleset `name` (`main-protection`, `release-tags`). The script matches existing
rulesets by name, so reruns update rather than duplicate (FR-003d).

Repository settings applied by the same script:
- merge options: squash on, merge commit off, rebase off, delete branch on merge on;
- **immutable releases** on: a published release's tag and assets are frozen for everyone.
