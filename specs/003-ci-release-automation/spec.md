# Feature Specification: Continuous Integration and Automated GitHub Releases

**Feature Branch**: `003-ci-release-automation`

**Created**: 2026-10-15

**Status**: Draft

**Input**: User description: "github actions для публікації ралізів на гітхаб та ci" (GitHub Actions
for publishing releases on GitHub, and CI)

## Clarifications

### Session 2026-10-15

- Q: How should the mandatory manual hardware checklist (Principle VI) gate publication? → A:
  A tag creates a **draft** release; the maintainer publishes it manually after the checklist
  has passed.
- Q: How strictly is the main branch protected against direct pushes? → A: Changes reach main
  only through a pull request with a passing CI check; the repository owner may deliberately
  bypass this in an emergency.
- Q: How many pull-request approvals are required? → A: None; a passing CI check is enough
  while the project has a single maintainer.
- Q: Are version tags (`v*`) protected? → A: Only the owner may create them; deleting or moving
  them is blocked (owner bypass only).
- Q: How are the protection rules configured? → A: As rule definition files in the repository,
  applied by one script run by the owner with their own GitHub rights.
- Q: How are pull requests merged into main? → A: Squash merge only; main keeps a linear
  history.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Every change is checked automatically (Priority: P1)

A contributor (today mainly the maintainer) pushes commits or opens a pull request. Without
running anything locally, they see within minutes whether the change builds cleanly, passes all
automated tests, and still produces a valid portable artifact (single executable, size limit,
all six languages embedded, administrator manifest). A pull request with a failing check is
clearly marked, so it is not merged by accident.

**Why this priority**: the constitution's merge gate (clean build, all tests pass, artifact
rules) is currently enforced only by hand on one developer machine. Automating it protects
every later change, including the release process itself.

**Independent Test**: open a pull request with a harmless change → the check passes and is
shown on the pull request; open one with a deliberately failing test → the check fails and
names the failing test.

**Acceptance Scenarios**:

1. **Given** a pull request against the main branch, **When** it is opened or updated, **Then**
   the project is built in release configuration, all automated tests run, the artifact check
   runs, and the combined result is reported on the pull request.
2. **Given** a change that introduces a compiler warning in a core project, a failing test, or
   an artifact that breaks the portability rules, **When** the check runs, **Then** it fails and
   its log names the cause.
3. **Given** a push to the main branch, **When** the check completes, **Then** the built
   executable and the test results are kept as downloadable build outputs for later inspection.
4. **Given** a change that only touches documentation, **When** it is pushed, **Then** the check
   still runs (it is fast enough that no path filtering is needed).
5. **Given** the protection rules have been applied, **When** anyone tries to push directly to
   main, force-push it or delete it without the owner's bypass, **Then** GitHub rejects it; a
   pull request with a passing check can be merged, by squash merge only.

---

### User Story 2 - Publishing a release takes one action (Priority: P1)

The maintainer decides a version is ready and marks it with a version tag (for example `v1.1.0`).
The release is then built from exactly that commit, verified, stamped with that version, and
prepared as a draft on the project's GitHub Releases page with the executable, its SHA-256
checksum, and the release notes for that version. After running the manual hardware checklist,
the maintainer publishes the draft, and users download it from one well-known place.

**Why this priority**: releases are the only way users get the tool; today they would be built
and uploaded by hand, which is error-prone (wrong commit, unrecorded hash, missing notes).

**Independent Test**: push a test tag on a fork or a pre-release tag → a draft release appears with
the executable, a checksum file whose value matches the downloaded executable, and the notes
taken from the repository's release notes for that version; the About dialog of the downloaded
executable shows the tagged version.

**Acceptance Scenarios**:

1. **Given** a commit on the main branch whose release notes contain a section for version
   X.Y.Z, **When** the maintainer pushes the tag `vX.Y.Z`, **Then** a release named for X.Y.Z is
   created with the executable, a SHA-256 checksum file, and the notes of that section.
2. **Given** a tagged build, **When** the executable is started, **Then** it reports version
   X.Y.Z (About dialog and file properties).
3. **Given** a tag whose build or tests fail, **When** the release run finishes, **Then** no
   release is published and the failure is visible in the run log.
4. **Given** a tag whose version has no section in the release notes, **When** the release run
   starts, **Then** it stops with a clear message before building and nothing is published.
5. **Given** a tag with a pre-release suffix (for example `v1.2.0-rc.1`), **When** it is
   released, **Then** the release is marked as a pre-release.
6. **Given** a published release, **When** a user compares the checksum file with the downloaded
   executable, **Then** they match, and the same value is recorded in the release notes text.
7. **Given** a successful release run, **When** it finishes, **Then** the release exists only as
   a **draft** (not visible to users); it becomes public only when the maintainer publishes it
   after the manual hardware checklist (constitution Principle VI) has passed and its results
   are added to the release text.

---

### User Story 3 - Independent file-system checks run in CI (Priority: P3)

For the ext4 shrink feature, the outputs produced by the test suite are additionally checked by
the reference Linux tools (file-system checker and tree comparison) on a Linux machine in CI,
the same independent check that is currently run by hand in Docker on the developer machine.

**Why this priority**: it turns the strongest correctness evidence for the riskiest feature
(rewriting file systems) into a repeatable check, but the product already has its own checker
and comparer in the regular tests, so it is an extra layer, not the first line of defense.

**Independent Test**: on a pull request, the shrunk and rebuilt images from the test run are
handed to a Linux job; a deliberately corrupted image makes that job fail.

**Acceptance Scenarios**:

1. **Given** the Windows test job exported its rebuilt and shrunk images, **When** the Linux
   check job runs, **Then** every image passes the reference file-system check and the tree
   comparison with its source fixture.
2. **Given** an exported image with an inconsistency, **When** the Linux job runs, **Then** it
   fails and names the image and the finding.

---

### Edge Cases

- A tag is pushed for a commit that is not on the main branch → the release is refused.
- A tag is pushed again after deleting and recreating it, or a release for that version already
  exists → the run does not silently overwrite published assets; it fails with a message.
- The version in the tag is malformed (`v1.2`, `release-1`) → the run stops before building.
- Two tags are pushed at once → each release is built independently from its own commit.
- The hosted Windows build machines no longer offer the required .NET Framework targeting
  pack → the build fails visibly rather than producing a different runtime target.
- Tests that need real hardware or very large images (hardware pass-through, 16 GiB benchmark)
  are skipped in CI as they are today on machines without the opt-in settings; the log shows
  them as not run, not as passed.
- The test suite writes several gigabytes of temporary images → it must fit the hosted machine's
  free disk space.
- A pull request from a fork → checks run without access to any publishing credentials.
- A direct push, force push or deletion of main without the owner's bypass → rejected by GitHub.
- The CI check itself is broken, so no pull request can pass → the owner fixes it through the
  emergency bypass; the bypass is visible in the repository's rule history.
- The required check is renamed in the CI configuration → pull requests would wait forever for
  the old name; the rule files and the CI check name must change together, and the protection
  script's status view shows the mismatch.
- The protection script is run by someone without administrator rights → it fails with a clear
  message and changes nothing.

## Requirements *(mandatory)*

### Functional Requirements

**Continuous integration**

- **FR-001**: Every pull request to the main branch and every push to the main branch MUST
  trigger a check that builds the solution in release configuration, runs all automated tests,
  and runs the artifact check.
- **FR-002**: The check MUST fail if the build reports any warning in core projects, if any
  test fails, or if the artifact check fails; the failing step MUST be identifiable from the
  run summary.
- **FR-003**: The check result MUST be visible on the pull request, and a pull request whose
  check has not passed MUST NOT be mergeable into the main branch.

**Main branch protection**

- **FR-003a**: Changes MUST reach the main branch only through a pull request whose CI check has
  passed; direct pushes to main MUST be rejected for everyone, the owner included. The
  owner's emergency bypass MUST require a deliberate action: explicitly merging a pull request
  despite a failing check (for example a broken CI configuration that blocks every pull request),
  or temporarily suspending the rule with the protection script. An ordinary push MUST never
  bypass the rule. Force-pushing to main and deleting main MUST be rejected under the same rule.
- **FR-003b**: No review approval is required to merge; a passing CI check is sufficient while
  the project has a single maintainer (GitHub does not allow approving one's own pull request).
- **FR-003c**: Only the repository owner MAY create tags matching `v*`; deleting or moving
  (force-updating) such a tag MUST be rejected for everyone else. Once a release is published,
  its tag and files MUST NOT be changeable or deletable by anyone, the owner included. A
  published version therefore always points to the commit it was built from.
- **FR-003d**: The rules of FR-003 to FR-003e MUST be defined in files in the repository and
  applied by a single script that the owner runs with their own GitHub rights. Running it again
  MUST update the existing rules rather than duplicate them, and it MUST be able to show the
  rules currently active on GitHub so differences from the files can be seen. No stored
  administrator credential is needed in CI.
- **FR-003e**: Pull requests MUST be merged into main by squash merge only, so that every commit
  on main corresponds to one pull request that passed CI and main keeps a linear history.
- **FR-004**: The check MUST keep the built executable and the test result report as
  downloadable outputs for at least 14 days.
- **FR-005**: The check MUST run on Windows with .NET Framework 4.8 as the target; it MUST NOT
  change the product's target runtime.
- **FR-006**: The maintainer MUST be able to start the check manually for any branch.

**Releases**

- **FR-007**: Pushing a tag of the form `vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH-SUFFIX` MUST
  start a release run; no other event may publish a release.
- **FR-008**: The release run MUST build and test exactly the tagged commit and MUST refuse
  tags whose commit is not contained in the main branch.
- **FR-009**: The version from the tag MUST be stamped into the executable (assembly and file
  version, shown in the About dialog and in the file properties).
- **FR-010**: The release MUST contain the executable and a `SHA256SUMS` checksum file; the
  release text MUST include the executable's SHA-256 (constitution: releases record the hash).
- **FR-011**: The release text MUST be taken from the section for that version in
  `docs/release-notes.md`; a missing section MUST stop the run before anything is published.
- **FR-012**: Tags with a suffix MUST produce a pre-release; tags without one a normal release.
- **FR-013**: The release run MUST NOT overwrite or modify an already published release.
- **FR-014**: The release run MUST create the release as a draft and MUST NOT make it public;
  only the maintainer publishes it, after the manual hardware checklist has passed. The draft
  text MUST state that the hardware checklist is pending, so a draft published by mistake is
  recognizable.
- **FR-015**: Publishing credentials MUST be limited to the release run and MUST NOT be
  available to pull-request checks.

**Independent checks (P3)**

- **FR-016**: The CI check SHOULD hand the images exported by the test suite to a Linux job that
  runs the reference ext4 checker and tree comparison and fails on any finding.

### Key Entities

- **Check run**: one automated verification of a commit; result (pass/fail per step), logs,
  build outputs (executable, test report), retention period.
- **Version tag**: `vX.Y.Z[-suffix]` on a main-branch commit; the single source of the release
  version.
- **Release**: version, pre-release flag, notes (from `docs/release-notes.md`), assets
  (executable, `SHA256SUMS`), executable SHA-256.
- **Protection rule set**: the rules for main (pull request + passing check, no approvals,
  squash only, no force push or deletion) and for `v*` tags (owner-only creation, no deletion or
  move), with the owner as the only bypass; kept as files in the repository and applied by the
  protection script.
- **Release notes section**: the part of `docs/release-notes.md` headed with that version; holds
  the user-facing changes, verification status and hardware checklist results.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For every pull request, the maintainer sees a pass/fail result within 15 minutes
  of pushing, without running anything locally.
- **SC-002**: Producing a complete release draft requires exactly one maintainer action after
  the release notes are written (pushing the tag); making it public requires one more (publishing
  the draft after the hardware checklist).
- **SC-003**: 100% of published executables have a checksum file whose value matches the
  downloaded file, and report the tagged version in the About dialog.
- **SC-004**: No release is ever published from a commit whose build or tests failed (verified
  by deliberately failing a pre-release tag once).
- **SC-005**: A failing check identifies the failing step or test in its summary, so the cause
  is found without reading the full log in at least 9 out of 10 failures.
- **SC-006**: After the protection script has run, 100% of attempts to push directly to main,
  force-push it, delete it, or move or delete a `v*` tag without the owner's bypass are rejected
  (verified once with a test push of each kind), and every commit added to main afterwards
  corresponds to a squash-merged pull request with a passing check.

## Assumptions

- The repository stays on GitHub (`kolod/hdd-backup`) and uses GitHub's hosted build machines;
  no self-hosted machines are needed.
- Hosted Windows machines can build .NET Framework 4.8 projects with the existing build
  commands. Windows 7 cannot be tested on hosted machines; Windows 7 coverage stays in the
  manual hardware checklist.
- Code signing of the executable is out of scope (no certificate is available); Windows may show
  a SmartScreen warning for downloaded releases. Signing can be added later as its own feature.
- Shipping `HddBackup.exe.config` next to the executable is recommended for Windows 7/8.1 (see
  `docs/release-notes.md`); it is attached to the release as an optional second asset.
- The repository owner is the only maintainer and the only person who pushes version tags; the
  protection rules (FR-003a–FR-003e) are applied by the owner with the provided script after this
  feature is merged.
- The build is deterministic enough that the same tagged commit produces a functionally
  identical executable; byte-for-byte reproducible builds are not required.
- The CI configuration is developer tooling; it adds nothing to the shipped executable and does
  not affect constitution Principle III.
