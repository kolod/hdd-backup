# Implementation Plan: Continuous Integration and Automated GitHub Releases

**Branch**: `003-ci-release-automation` | **Date**: 2026-10-15 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/003-ci-release-automation/spec.md`

## Summary

Add GitHub Actions automation around the existing build:

- A **CI workflow** builds with warnings as errors, runs the full test suite and the artifact
  check on Windows for every pull request and push to main. It publishes a readable test summary
  and keeps the exe and test report. A Linux job then re-checks the exported ext4 images with
  e2fsprogs.
- A **release workflow**, started by a `vX.Y.Z[-suffix]` tag on main, validates the tag and the
  release notes, builds the same way with the tag's version, and creates a **draft** GitHub
  release with the exe, `.exe.config` and `SHA256SUMS`.
- **Main and tag protection** is kept as ruleset JSON files in the repository and applied by one
  owner-run script:
  - main: PR + green checks, no approvals, squash only, no force-push or deletion. Direct pushes
    are blocked for everyone, the owner included. The owner's bypass works only by explicitly
    merging a PR (research R-12).
  - tags `v*`: only the owner creates them.
  - Immutable releases are enabled, so a published release's tag and files are frozen for
    everyone.

Product code changes are limited to build properties (version stamping, reference assemblies)
and test hygiene (cleanup, honest skips).

## Technical Context

**Language/Version**:
- Automation: YAML workflows and PowerShell 7 scripts (`pwsh`, preinstalled on hosted runners).
- Product: unchanged, C# 7.3 / .NET Framework 4.8.
- Linux check: Bash, reusing `check-output.sh`.

**Primary Dependencies**:
- GitHub-owned actions only, pinned to commit SHAs: checkout, setup-dotnet, upload-artifact,
  download-artifact.
- GitHub CLI `gh`, preinstalled.
- `Microsoft.NETFramework.ReferenceAssemblies` (build time only).
- Pester 5 for script tests (preinstalled on Windows runners).

**Storage**:
- Build artifacts on GitHub: 14 days for CI outputs, 1 day for exported ext4 images.
- Release assets on GitHub Releases.

**Testing**:
- Existing xUnit suite.
- New Pester tests for the release scripts (tag validation, notes extraction, body composition)
  and for the ruleset files (schema sanity).
- End-to-end validation per [quickstart.md](quickstart.md): a test PR, a pre-release tag, and a
  rejected push.

**Target Platform**:
- `windows-2025` and `ubuntu-24.04` hosted runners.
- The product target is unchanged: Windows 7 SP1 to 11.

**Project Type**: build/release infrastructure for an existing desktop application.

**Performance Goals**:
- CI result in ≤ 15 min (SC-001); estimate about 10 min (research R-14).
- Release draft in ≤ 20 min after the tag push.

**Constraints**:
- No stored admin credentials. Write permission exists only in the release `publish` job.
- No third-party actions.
- Never overwrite a release.
- Peak test disk usage must fit a hosted runner (R-4).

**Scale/Scope**:
- One repository, one maintainer.
- 2 workflows, 1 composite action, 2 ruleset files, 5 PowerShell scripts, 1 Dependabot config.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Assessment | Status |
|-----------|------------|--------|
| I. Data Safety First | No device code is touched. CI runs only in-memory/file-backed tests; hardware tests stay opt-in and are reported as skipped. | PASS |
| II. Faithful Imaging | No change. | PASS |
| III. Portable & Compact | Release assets are the same single exe. `.exe.config` is optional, as already documented. The reference-assembly package is build-time only; the artifact check runs on every build. | PASS |
| IV. Windows 7–11 | The target stays net48; Win7 cannot run on hosted runners, so it stays in the manual checklist (spec assumption). | PASS |
| V. English-First | Workflows, scripts, docs and commit messages in English; no UI strings added. | PASS |
| VI. Test-Verified Disk I/O | Strengthened: tests run on every change. The manual hardware checklist gates publication through draft releases (clarification Q1). | PASS |
| VII. Simplicity | GitHub-native tools only (Actions, rulesets, `gh`); no release/versioning frameworks; scripts are small and testable. | PASS |
| Workflow & Quality Gates | Automates the merge gate (no warnings, all tests, artifact rules) and the release record (SHA-256 in release text, checklist results before publishing). | PASS |

No violations, so Complexity Tracking stays empty.

**Post-design re-check (after Phase 1)**: PASS.
- The contracts add no product dependencies.
- The ruleset JSON matches FR-003a–e.
- The release body contract includes the SHA-256 and the pending-checklist banner.

## Project Structure

### Documentation (this feature)

```text
specs/003-ci-release-automation/
├── plan.md              # This file
├── research.md          # Phase 0: decisions R-1 … R-14
├── data-model.md        # Phase 1: tag, release, check run, rule set, notes section
├── quickstart.md        # Phase 1: end-to-end validation
├── contracts/
│   ├── workflows.md     # triggers, jobs, required check names, artifacts, permissions
│   ├── scripts.md       # PowerShell script interfaces and exit codes
│   └── rulesets.md      # ruleset files ↔ FR-003a–e mapping
└── tasks.md             # Phase 2 (/speckit-tasks)
```

### Source Code (repository root)

```text
.github/
├── workflows/
│   ├── ci.yml                     # PR/push/manual: build-test (Windows) → ext4-check (Ubuntu)
│   └── release.yml                # tag v*: validate → build → publish (draft)
├── actions/
│   └── build-test/action.yml      # composite: setup, build -warnaserror, test, summary, artifact check
├── rulesets/
│   ├── main.json                  # FR-003a/b/e
│   └── release-tags.json          # FR-003c
└── dependabot.yml                 # weekly github-actions updates

tools/
├── ci/
│   ├── Write-TestSummary.ps1      # TRX → $GITHUB_STEP_SUMMARY
│   └── Invoke-Ext4Checks.sh       # loop over exported images → check-output.sh
├── release/
│   ├── Test-ReleaseTag.ps1        # tag format + on-main check → version, prerelease
│   ├── Get-ReleaseNotes.ps1       # section of docs/release-notes.md
│   └── New-ReleaseBody.ps1        # banner + notes + checksums → body.md, SHA256SUMS
├── github/
│   └── Set-RepoProtection.ps1     # apply/show rulesets and merge settings (owner, gh)
└── tests/
    └── *.Tests.ps1                # Pester tests for the scripts above

global.json                        # SDK 10.0.x, rollForward latestFeature
Directory.Build.props              # net48 reference assemblies (PrivateAssets=all)

tests/HddBackup.Core.Tests/
├── TestPaths.cs                   # + delete own run folder on exit
├── OptInFactAttribute.cs          # reports opt-in tests as skipped
├── Hardware/PassThroughSmokeTests.cs      # use OptInFact
└── Shrinking/ShrinkPerformanceTests.cs    # use OptInFact

docs/
├── release-notes.md               # unchanged format; documented as release source
└── releasing.md                   # how to cut a release, publish the draft, emergency bypass
```

**Structure Decision**:
- Automation lives in `.github/` (workflows, composite action, rulesets) and `tools/` (scripts,
  testable locally with Pester).
- The product projects only get build properties through `Directory.Build.props`; the tests
  get hygiene fixes.
- No new projects are added to the solution.

## Phase notes

- **Order**:
  1. Build reproducibility: `global.json`, reference assemblies, warnings as errors on a clean
     machine.
  2. Test hygiene: cleanup, opt-in skips.
  3. CI workflow (US1).
  4. Release scripts with Pester tests, then the release workflow (US2).
  5. ext4 check job (US3).
  6. Rulesets and protection script, applied last, after the CI check names exist on main.
     Otherwise required checks named before they ever ran would block every PR.
- **Bootstrapping**: the first PR that adds `ci.yml` runs CI on itself. Protection is applied by
  the owner after that PR is merged, following `docs/releasing.md`.

## Complexity Tracking

No constitution violations.
