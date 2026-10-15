# Quickstart & Validation: CI and Automated GitHub Releases

## Prerequisites

- GitHub CLI authenticated as the repository owner (`gh auth status`).
- PowerShell 7 and Pester 5 (`Install-Module Pester -Scope CurrentUser` if missing) for the
  local script tests.
- The feature branch merged through a pull request (bootstrapping order in [plan.md](plan.md)).

## 1. Local checks (before pushing)

```powershell
dotnet build HddBackup.sln -c Release -warnaserror -p:Version=0.0.0-local
dotnet test tests/HddBackup.Core.Tests -c Release --no-build --logger "trx;LogFileName=results.trx"
pwsh tools/ci/Write-TestSummary.ps1 -TrxPath (Get-ChildItem -Recurse -Filter results.trx | Select -First 1).FullName
Invoke-Pester tools/tests
```

Expected:
- 0 warnings.
- All tests pass. The hardware and performance tests are listed as **skipped**, not passed.
- Pester is green.
- `%TEMP%\HddBackupTests` holds no `run-*` folder after the test process exits.

## 2. CI on a pull request (US1)

1. Open a PR with a harmless change. Expected: `build-test` and `ext4-check` turn green within
   15 min (SC-001). The run summary shows test totals and the ext4 image list. Artifacts
   `hddbackup-exe` and `test-results` are downloadable.
2. Push a commit with a failing assertion. Expected: `build-test` fails, and the summary names
   the test and its message (SC-005).
3. Push a commit that adds an unused variable (a CS0168 warning). Expected: the build step fails.
4. Revert both commits.

## 3. Release dry run (US2)

1. Add a `## 1.1.0-rc.1 — test` section to `docs/release-notes.md` via a PR and merge it.
2. Create and push the tag `v1.1.0-rc.1` on main (owner).
3. Expected:
   - A **draft pre-release** `HddBackup 1.1.0-rc.1` exists with `HddBackup.exe`,
     `HddBackup.exe.config` and `SHA256SUMS`.
   - The body starts with the "Hardware checklist pending" banner and ends with the checksums.
4. Download the exe. `Get-FileHash` equals the `SHA256SUMS` line. The About dialog and file
   properties show 1.1.0 (SC-003).
5. Negative checks:
   - A tag `v9.9.9` without a notes section → `validate` fails and no release appears.
   - Re-run the workflow for `v1.1.0-rc.1` → `validate` fails with "release already exists"
     (FR-013).
   - Push the tag `v1.1.1-rc.1` on a commit that is not on main → `validate` fails (FR-008).
6. Delete the test draft (owner) when done.

## 4. Independent ext4 check (US3)

On the PR from step 2.1, `ext4-check` lists every `*.ext4` and `shrunk-*.img` with "OK".
Locally, corrupt one exported image (flip a byte in a group descriptor) and run
`tools/ci/Invoke-Ext4Checks.sh` in Docker. Expected: exit 1 naming that image.

## 5. Protection (FR-003a–e, SC-006)

```powershell
pwsh tools/github/Set-RepoProtection.ps1 -WhatIf   # review requests
pwsh tools/github/Set-RepoProtection.ps1           # apply (owner)
pwsh tools/github/Set-RepoProtection.ps1 -Show     # exit 0 = matches files
```

Expected checks:

| Attempt | Expected |
|---------|----------|
| Push a commit directly to main (any account, **owner included**) | rejected |
| Merge a PR with red checks | blocked; the owner sees an explicit "bypass rules and merge" option, logged in rule insights |
| Merge a green PR | only "Squash and merge" offered; branch deleted afterwards |
| Force-push or delete main (non-admin) | rejected |
| Non-admin creates `v0.0.1-test` | rejected |
| Owner deletes or moves the tag of a **published** release | rejected (immutable releases) |
| `Set-RepoProtection.ps1 -Suspend main-protection`, then a plain run | main unprotected in between; plain run restores it and `-Show` exits 0 |
| Run the script as a non-admin | exit 3, nothing changed |

Record the results in `specs/003-ci-release-automation/quickstart.md` (Validation log).

## Validation log

| Date | Check | Result |
|------|-------|--------|
| 2026-10-15 | Local build `-warnaserror` with reference-assembly package; artifact check | 0 warnings; artifact check passed |
| 2026-10-15 | Full test run: skipped tests | 231 passed, 2 skipped (hardware, 16 GiB benchmark), shown as skipped in TRX summary |
| 2026-10-15 | Peak temp usage of a full test run (T008) | before per-test cleanup: 9.0 GB left behind per run; after `TempDirs` in Ext4/shrink tests: peak 1.57 GB, 0.72 GB left (removed by the next run). The test host is killed without `ProcessExit`, so exit cleanup is best effort |
| 2026-10-15 | Pester `Write-TestSummary.Tests.ps1` | 5/5 passed |
| 2026-10-15 | Local `Invoke-Ext4Checks.sh` in Docker (T024) | 12/12 exported images OK in 3.0 min (4 CPUs); a copy with one flipped group-descriptor byte → exit 1, image named with the e2fsck findings |
| 2026-10-15 | First CI run on PR #2 (T012) | failed: Pester data files were excluded by a too-broad `data/` ignore rule; fixed (`/data/`) |
| 2026-10-15 | CI run 38773508509 on PR #2 (T012, SC-001) | `build-test` 3m08s (build 0 warnings; 231 passed, 2 skipped; Pester 52/52; artifact check passed) → `ext4-check` 3m33s (12/12 images OK); whole run 6.8 min ≤ 15 min |
| 2026-10-15 | Negative CI checks (T013, SC-005), temporary branches via `workflow_dispatch` | failing assertion → run 38774567003 fails, log and summary name `CiNegativeTests.DeliberatelyFails_ForCiCheck` with "Assert.Equal() Failure: Values differ"; unused variable → run 38774571597 fails at build with `error CS0168` at `Checksums.cs(9,55)`. Branches deleted |
| 2026-10-15 | CI races found on fresh runners before merging PR #2 | `ext4-check`: SIGPIPE race in `check-output.sh` (`echo \| grep -q` under pipefail) → fixed with a bash substring test; `build-test`: two test classes extracted the same fixture at once (`ConcurrentDictionary.GetOrAdd` runs the factory more than once) → `Lazy<T>` + unique temp name. Both green on a run and a full re-run of the same commit (run 38777518975) |
| 2026-10-15 | Release dry run `v1.1.0-rc.1` (T022, SC-002, SC-003) | run 38780738155: validate → build → publish green; **draft pre-release** "HddBackup 1.1.0-rc.1" with `HddBackup.exe`, `HddBackup.exe.config`, `SHA256SUMS`; downloaded exe SHA-256 = `SHA256SUMS` line = body line; FileVersion 1.1.0.0, ProductVersion 1.1.0-rc.1+c570d24; body starts with the pending-checklist banner |
| 2026-10-15 | Negative tags (T022, FR-008, FR-011) | `v9.9.9` → validate fails "No section '## 9.9.9'"; `v1.1.1-rc.1` on a commit not on main → validate fails "not on main"; nothing built or published |
| 2026-10-15 | Re-run for an existing tag (T022, FR-013) | **bug found**: re-run created a second draft (`gh release view <tag>` ignores drafts). Fixed in PR #6 (`Test-ReleaseAbsent.ps1`). Re-test (run 38783227405): `validate` cannot see drafts with its read-only token, `publish` stops with "already exists (draft …)"; still exactly one draft. Test drafts and tags deleted |
| 2026-10-15 | Protection applied (T031): `-WhatIf`, apply, `-Show` | 2 rulesets created, squash-only merge settings, immutable releases on. `-Show` reported `release-tags` as different (GitHub drops `update_allows_fetch_and_merge` for tag rules); file fixed, then `-Show` exit 0 |
| 2026-10-15 | SC-006 attempts with the owner account | direct push to main → rejected ("2 of 2 required status checks are expected"); force push main~1 → rejected; delete main → rejected; owner creates and deletes a `v0.0.0-protection-test` tag → allowed (bypass), its release run fails in validate, nothing published. Not testable with one account: non-admin push/tag attempts (covered by the ruleset files, `Rulesets.Tests.ps1` and `-Show`); moving a published release tag (no published release yet; immutable releases enabled) |
| 2026-10-15 | Dependabot PRs #4, #5 (action updates) | CI green, squash-merged |
