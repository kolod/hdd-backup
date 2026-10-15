---

description: "Task list for Continuous Integration and Automated GitHub Releases"
---

# Tasks: Continuous Integration and Automated GitHub Releases

**Input**: Design documents from `/specs/003-ci-release-automation/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: INCLUDED. Constitution Principle VI and the plan require them. The release and CI
scripts get Pester tests (written first). Workflows are validated end to end per
quickstart.md, because they cannot be unit-tested.

**Organization**: grouped by user story. US1 (P1) CI on every change; US2 (P1) draft releases
from tags; US3 (P3) independent ext4 check. Main/tag protection (FR-003a–e) belongs to US1,
but it is applied last, because its required checks include `ext4-check` from US3 and both
checks must have run once (plan "Phase notes").

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on unfinished tasks)
- **[Story]**: US1, US2, US3 from spec.md
- All code, comments, docs and commit messages in English (Principle V)

## Path Conventions

- Workflows and rules: `.github/`; scripts: `tools/`; Pester tests: `tools/tests/`
- Build properties: repository root (`global.json`, `Directory.Build.props`)
- xUnit test project: `tests/HddBackup.Core.Tests/`

---

## Phase 1: Setup

- [X] T001 Create `global.json` at the repository root pinning the .NET SDK to the version used locally (`10.0.402`, `"rollForward": "latestFeature"`) per research R-1
- [X] T002 [P] Create `Directory.Build.props` at the repository root. Add a `PackageReference` to `Microsoft.NETFramework.ReferenceAssemblies` (latest stable 1.0.x, `PrivateAssets="all"`) for `net48` projects. Verify locally that `dotnet build HddBackup.sln -c Release -warnaserror` succeeds with 0 warnings and that `tests/check-artifact.ps1` still passes (no new files next to the exe)
- [X] T003 [P] Create `.github/dependabot.yml` (`package-ecosystem: github-actions`, directory `/`, weekly schedule, commit-message prefix `ci`) per research R-11
- [X] T004 [P] Create the folders `tools/ci/`, `tools/release/`, `tools/github/`, `tools/tests/` and `tools/tests/data/` with a short `tools/README.md` that lists each script and how to run the Pester tests (`Invoke-Pester tools/tests`)

---

## Phase 2: Foundational (blocks US1 and US2)

**⚠️ CRITICAL**: the shared build/test path must work on a clean machine before any workflow
uses it.

### Tests first

- [X] T005 [P] Add a small sample TRX file with 3 passed, 1 failed (with message) and 1 skipped test to `tools/tests/data/sample.trx`, and write Pester tests in `tools/tests/Write-TestSummary.Tests.ps1`:
  - totals line;
  - failed-test table with the first message line;
  - skipped list;
  - exit 0 when tests failed;
  - exit 1 for a missing file;
  - output goes to `$env:GITHUB_STEP_SUMMARY` when set, otherwise stdout.

### Implementation

- [X] T006 Change `tests/HddBackup.Core.Tests/TestPaths.cs` so the current `run-<pid>` folder is deleted on `AppDomain.CurrentDomain.ProcessExit`, ignoring IO errors (research R-4, contracts/scripts.md "Test project additions")
- [X] T007 [P] Create `tests/HddBackup.Core.Tests/OptInFactAttribute.cs` (`[OptInFact("ENV_VAR")]`, derives from `FactAttribute`, sets `Skip` when the variable is empty). Apply it to `tests/HddBackup.Core.Tests/Hardware/PassThroughSmokeTests.cs` (`HDDBACKUP_PT_DISK`) and `tests/HddBackup.Core.Tests/Shrinking/ShrinkPerformanceTests.cs` (`HDDBACKUP_PERF_IMAGE`), and remove their early `return`. Verify that `dotnet test` reports them as skipped
- [X] T008 Measure peak temp usage of a full local test run (free space before, during and after, via `Get-PSDrive`). If the peak exceeds 4 GB, delete large outputs at the end of the biggest tests (`ImageShrinkerTests`, `Ext4WriterTests` callers) in `finally` blocks. Record the measured peak in `specs/003-ci-release-automation/quickstart.md` (Validation log)
- [X] T009 [P] Implement `tools/ci/Write-TestSummary.ps1` per contracts/scripts.md (makes T005 pass)
- [X] T010 Create the composite action `.github/actions/build-test/action.yml` per contracts/workflows.md:
  - inputs `version` and `export-ext4`;
  - `actions/setup-dotnet` pinned to a commit SHA (version in a comment), reading `global.json`;
  - `TEMP`/`TMP` set to `$RUNNER_TEMP`;
  - build with `-warnaserror -p:Version=${{ inputs.version }}`;
  - test with the TRX logger, and `HDDBACKUP_EXPORT_DIR=$RUNNER_TEMP/ext4-exports` when `export-ext4` is true;
  - `Write-TestSummary.ps1` with `if: always()`;
  - `tests/check-artifact.ps1`;
  - free disk space written to the summary;
  - outputs `exe-dir`, `trx-path`, `exports-dir`.

**Checkpoint**: the build/test path is reproducible, tests clean up after themselves, and
opt-in tests are reported honestly.

---

## Phase 3: User Story 1 — Every change is checked automatically (Priority: P1) 🎯 MVP

**Goal**: every PR and push to main gets a Windows build + test + artifact check with a readable
summary and downloadable outputs.

**Independent Test**: quickstart.md §2. A harmless PR goes green; a failing test or a new
warning turns it red and names the cause.

### Implementation

- [X] T011 [US1] Create `.github/workflows/ci.yml` (name `CI`) per contracts/workflows.md:
  - triggers `pull_request` (main), `push` (main) and `workflow_dispatch`;
  - workflow-level `permissions: contents: read`;
  - concurrency `ci-${{ github.ref }}`, cancelling in progress only for pull requests;
  - job **`build-test`** on `windows-2025` (timeout 30 min): `actions/checkout` pinned by SHA, the composite action with `version: 0.0.0-ci.${{ github.run_number }}` and `export-ext4: true`;
  - `actions/upload-artifact` pinned by SHA for `hddbackup-exe` (exe + config, 14 days), `test-results` (TRX, 14 days, `if: always()`) and `ext4-exports` (1 day, `if-no-files-found: error`).
- [ ] T012 [US1] Push the branch and open the feature PR. Confirm that `build-test` runs, the summary shows the totals, and the artifacts download. Fix any warnings or test failures that only appear on the hosted image. Record the duration (SC-001 ≤ 15 min) in `specs/003-ci-release-automation/quickstart.md`
- [ ] T013 [US1] Negative checks on a throwaway branch (quickstart §2.2–2.3): a failing assertion names the test in the summary, and an unused variable fails the build. Record both in the Validation log, then delete the branch

**Checkpoint**: MVP. CI runs on every change. Protection comes in Phase 6.

---

## Phase 4: User Story 2 — Publishing a release takes one action (Priority: P1)

**Goal**: a `vX.Y.Z[-suffix]` tag on main produces a verified **draft** release with the
stamped exe, config, `SHA256SUMS` and the notes section. It never overwrites a release.

**Independent Test**: quickstart.md §3 (pre-release dry run plus negative tags).

### Tests first

- [X] T014 [P] [US2] Pester tests in `tools/tests/Test-ReleaseTag.Tests.ps1`:
  - valid tags `v1.2.3` and `v1.2.3-rc.1` give the right `Version`, `NumericVersion` and `IsPrerelease`;
  - malformed tags `v1.2`, `1.2.3`, `release-1` and `v1.2.3.4` exit 1;
  - `$GITHUB_OUTPUT` lines are written;
  - ancestry: a temporary git repository with a commit on main and one on a side branch → exit 2 for the side-branch commit.
- [X] T015 [P] [US2] Pester tests in `tools/tests/Get-ReleaseNotes.Tests.ps1`, using fixture `tools/tests/data/release-notes.md` with sections `1.1.0`, `1.1.0-rc.1` and `1.0.0`:
  - exact-version match;
  - `1.1.0` must not match `1.1.0-rc.1`;
  - the section ends at the next `## `;
  - the heading line is excluded;
  - a missing or empty section exits 1;
  - the real `docs/release-notes.md` has sections `1.1.0` and `1.0.0`.
- [X] T016 [P] [US2] Pester tests in `tools/tests/New-ReleaseBody.Tests.ps1`:
  - `SHA256SUMS` has two-space format, lowercase hex, sorted names and LF endings, and its values match `Get-FileHash` of the sample files;
  - `body.md` order is banner, notes, `### Checksums`;
  - the banner text matches the contract;
  - a missing `HddBackup.exe` exits 1.

### Implementation

- [X] T017 [P] [US2] Implement `tools/release/Test-ReleaseTag.ps1` per contracts/scripts.md and data-model.md "Version tag" (makes T014 pass)
- [X] T018 [P] [US2] Implement `tools/release/Get-ReleaseNotes.ps1` per contracts/scripts.md and data-model.md "Release notes section" (makes T015 pass)
- [X] T019 [P] [US2] Implement `tools/release/New-ReleaseBody.ps1` per contracts/scripts.md and data-model.md "Release" / "SHA256SUMS" (makes T016 pass)
- [X] T020 [US2] Create `.github/workflows/release.yml` (name `Release`) per contracts/workflows.md:
  - trigger: `push` of tags `v*` only;
  - `permissions: contents: read`; concurrency `release-${{ github.ref }}` without cancel;
  - job **`validate`** (ubuntu-24.04, checkout with `fetch-depth: 0`): `Test-ReleaseTag.ps1`, `Get-ReleaseNotes.ps1`, then fail if `gh release view` finds the tag (`GH_TOKEN: ${{ github.token }}`); outputs `version` and `is_prerelease`;
  - job **`build`** (windows-2025, needs validate): checkout the tag, composite action with `version: ${{ needs.validate.outputs.version }}` and `export-ext4: false`, upload `release-files` (3 days);
  - job **`publish`** (ubuntu-24.04, needs validate + build, `permissions: contents: write`): download `release-files`, run `New-ReleaseBody.ps1`, then `gh release create … --draft --verify-tag [--prerelease]` with the exe, config and `SHA256SUMS`;
  - all actions pinned by SHA.
- [X] T021 [P] [US2] Write `docs/releasing.md`:
  1. Add the `## X.Y.Z — …` section to `docs/release-notes.md` via a PR.
  2. Tag on main and push the tag.
  3. Download the draft assets and run the hardware checklist.
  4. Add the results to the draft, remove the banner and publish (final, because releases are immutable).
  5. A failed release means fixing and using a new version number (published tags cannot move).
  6. Emergency bypass procedures (merge-dialog bypass; `Set-RepoProtection.ps1 -Suspend`).
  Link it from `README.md` (Build and test section).
- [ ] T022 [US2] After merging to main (Pester tests also pass in CI, see T030), run the dry run from quickstart §3:
  1. Add a `## 1.1.0-rc.1 — CI dry run` section via a PR.
  2. Push the tag `v1.1.0-rc.1`.
  3. Verify the draft pre-release, its assets and its body; check that the downloaded exe's SHA-256 and the About version match.
  4. Run the negative tags `v9.9.9` (no notes) and a re-run of the same tag (already exists).
  5. Delete the draft and the test tag afterwards.
  Record the results in the Validation log.

**Checkpoint**: one tag push yields a complete, verified draft release.

---

## Phase 5: User Story 3 — Independent file-system checks run in CI (Priority: P3)

**Goal**: the rebuilt and shrunk images exported by the Windows tests are checked by e2fsprogs
on Linux in every CI run.

**Independent Test**: quickstart.md §4. All exported images report OK, and a corrupted image
fails the job and is named.

### Implementation

- [X] T023 [P] [US3] Implement `tools/ci/Invoke-Ext4Checks.sh` per contracts/scripts.md:
  - map `<fixture>-<factor>.ext4` (regex `^(.+)-[0-9]+\.[0-9]{2}\.ext4$`) and `shrunk-<fixture>.img` to `<fixtures-dir>/<fixture>.img.gz`;
  - run `tests/fixtures/ext4/check-output.sh` for each;
  - write one summary line per image to `$GITHUB_STEP_SUMMARY`;
  - exit 1 when no image is found or any check fails.
  Mark it executable (`git update-index --chmod=+x`) and keep LF endings (`.gitattributes` already covers `*.sh`).
- [X] T024 [US3] Verify the script locally in Docker (`debian:bookworm` with e2fsprogs, dosfstools and fdisk) against exports from `HDDBACKUP_EXPORT_DIR`: all images OK; a copy with one flipped group-descriptor byte gives exit 1 naming the image
- [X] T025 [US3] Add job **`ext4-check`** to `.github/workflows/ci.yml` (ubuntu-24.04, `needs: build-test`, timeout 20 min):
  - checkout;
  - download `ext4-exports`;
  - `sudo apt-get update && sudo apt-get install -y e2fsprogs dosfstools fdisk`;
  - `tools/ci/Invoke-Ext4Checks.sh "$RUNNER_TEMP/ext4-exports" tests/HddBackup.Core.Tests/Fixtures/ext4`.
  Confirm on the feature PR that it lists every image as OK.

**Checkpoint**: both required checks (`build-test`, `ext4-check`) exist and have run on the PR.

---

## Phase 6: User Story 1 (continued) — Main and tag protection (FR-003a–e)

**Goal**: main accepts only squash-merged PRs with green checks; direct pushes, force pushes
and deletion are rejected for everyone; `v*` tags are owner-only; published releases are
immutable.

**Independent Test**: quickstart.md §5 table (SC-006).

### Implementation

- [X] T026 [P] [US1] Create `.github/rulesets/main.json` exactly as in contracts/rulesets.md (name `main-protection`, `~DEFAULT_BRANCH`, deletion, non_fast_forward, required_linear_history, pull_request with 0 approvals and squash only, required checks `build-test` and `ext4-check` with integration 15368, bypass `RepositoryRole` 5 in `pull_request` mode)
- [X] T027 [P] [US1] Create `.github/rulesets/release-tags.json` exactly as in contracts/rulesets.md (name `release-tags`, `refs/tags/v*`, creation, update, deletion, non_fast_forward, bypass `RepositoryRole` 5 in `always` mode)
- [X] T028 [P] [US1] Pester tests in `tools/tests/Rulesets.Tests.ps1`:
  - both JSON files parse;
  - names are unique;
  - required check contexts equal the job names found in `.github/workflows/ci.yml` (guards the "renamed check" edge case);
  - main bypass mode is `pull_request`;
  - the main merge methods are exactly `squash`.
- [X] T029 [US1] Implement `tools/github/Set-RepoProtection.ps1` per contracts/scripts.md:
  - admin check via `gh api repos/<repo> --jq .permissions.admin` (exit 3);
  - match rulesets by name (POST/PUT);
  - `PATCH` the merge settings (squash only, `delete_branch_on_merge`, PR title/body for squash commits);
  - `PUT immutable-releases`;
  - `-Show` with diff (exit 4 on mismatch);
  - `-WhatIf`;
  - `-Suspend <name>` with typed confirmation;
  - never delete unmanaged rulesets.
- [X] T030 [US1] Add a Pester step to the composite action `.github/actions/build-test/action.yml` (`Invoke-Pester tools/tests -CI`, before the artifact check) so script and ruleset tests run in every CI and release build
- [ ] T031 [US1] After the feature PR is squash-merged, the owner runs `Set-RepoProtection.ps1 -WhatIf`, then a plain run, then `-Show` (expect exit 0). Walk through the quickstart §5 table, including a direct owner push to main being rejected and the PR bypass option being offered. Record the results in the Validation log

**Checkpoint**: SC-006 verified; main and release tags are protected.

---

## Phase 7: Polish & Cross-Cutting

- [X] T032 [P] Update `README.md`: CI status badge for `ci.yml`; "Releases" section pointing to GitHub Releases, the `SHA256SUMS` check (`Get-FileHash`/`certutil`), the SmartScreen note (unsigned exe) and `docs/releasing.md`
- [X] T033 [P] Add a short note at the top of `docs/release-notes.md` that each `## X.Y.Z` section is the source of the GitHub release text (the heading format is required by `Get-ReleaseNotes.ps1`)
- [X] T034 [P] Update `docs/safety-review.md` with a "Release integrity" section: tag-on-main check, draft-only creation, immutable published releases, SHA-256 in body and `SHA256SUMS`, SHA-pinned GitHub-owned actions, write permission only in `publish`
- [ ] T035 Run all quickstart.md sections that are not yet logged, complete the Validation log, and confirm that SC-001…SC-006 are met. Mark tasks.md checkboxes

---

## Dependencies & Execution Order

- **Setup (T001–T004)** → **Foundational (T005–T010)** → US1 (T011–T013) → US2 (T014–T022)
  and US3 (T023–T025) → protection (T026–T031) → polish (T032–T035).
- **US2** depends on the composite action (T010). Its workflow can be written in parallel with
  US1, but T022 (the real tag dry run) needs the workflows on main.
- **US3** T025 depends on T011 (same workflow file) and on the `ext4-exports` artifact (T010,
  T011).
- **Protection** T026–T029 can be written any time after US3. T031 (applying the rules) must
  come after both required checks have run on main, otherwise every PR is blocked.
- Files shared across tasks, done sequentially: `.github/workflows/ci.yml` (T011 → T025),
  `.github/actions/build-test/action.yml` (T010 → T030).

### Parallel Opportunities

- T002, T003 and T004; T005 with T007; T014, T015 and T016 (tests); T017, T018 and T019
  (scripts); T026, T027 and T028; T032, T033 and T034.

## Parallel Example: User Story 2

```text
Task: "Pester tests for Test-ReleaseTag in tools/tests/Test-ReleaseTag.Tests.ps1"
Task: "Pester tests for Get-ReleaseNotes in tools/tests/Get-ReleaseNotes.Tests.ps1"
Task: "Pester tests for New-ReleaseBody in tools/tests/New-ReleaseBody.Tests.ps1"
# then
Task: "Implement tools/release/Test-ReleaseTag.ps1"
Task: "Implement tools/release/Get-ReleaseNotes.ps1"
Task: "Implement tools/release/New-ReleaseBody.ps1"
```

## Implementation Strategy

1. **MVP = Setup + Foundational + US1 (T001–T013)**: CI on every change. This already enforces
   the constitution's merge gate in practice.
2. **US2**: draft releases from tags (scripts with Pester first, then the workflow, then the
   dry run).
3. **US3**: the Linux ext4 job; it completes the set of required checks.
4. **Protection**: rules files, script and tests, applied by the owner after merge.
5. **Polish**: README, docs, full validation log.

## Notes

- Never push test tags without a matching cleanup step. Test drafts can be deleted; published
  releases cannot (immutable releases).
- The owner's real emergency path must be documented before protection is applied (T021 before
  T031).
- Workflows cannot be unit-tested; their contracts are checked by the end-to-end validation
  tasks (T012, T013, T022, T025, T031).
