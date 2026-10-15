# Research: CI and Automated GitHub Releases

All Technical Context unknowns are resolved below. Each entry: Decision, Rationale, Alternatives.

## R-1. Build machine and .NET Framework 4.8 reference assemblies

- **Decision**: Run Windows jobs on `windows-2025` hosted runners. Install the .NET SDK used by
  the developer machine (10.0.x, pinned through a new `global.json` with
  `rollForward: latestFeature`) with `actions/setup-dotnet`. Reference .NET Framework 4.8
  through the `Microsoft.NETFramework.ReferenceAssemblies` NuGet package (build-time only,
  `PrivateAssets=all`) in a new `Directory.Build.props`, so the build never depends on a
  targeting pack installed with Visual Studio on the image.
- **Rationale**: FR-005 (target stays net48) and the edge case "hosted image loses the
  targeting pack". The reference-assembly package is Microsoft's documented way to build
  .NET Framework projects with the SDK on any machine; it adds nothing to the output.
- **Alternatives**: rely on the Visual Studio targeting pack in the image (works today, breaks
  silently on image changes); `windows-2022` (older, scheduled for retirement first);
  `msbuild` instead of `dotnet build` (no benefit; the project is SDK-style).

## R-2. Warnings as errors

- **Decision**: CI builds with `dotnet build -c Release -warnaserror`. The App project already
  has `TreatWarningsAsErrors`; the switch extends it to Core and tests. Fix any warnings that
  appear on the hosted image before enabling the rule.
- **Rationale**: FR-002 and the constitution merge gate ("builds without warnings").
- **Alternatives**: set `TreatWarningsAsErrors` in every project (same effect, but it also
  breaks local exploratory builds); a log grep for "warning" (fragile).

## R-3. Test results and failure summary

- **Decision**: `dotnet test --no-build -c Release --logger "trx;LogFileName=results.trx"`. A
  small PowerShell script (`tools/ci/Write-TestSummary.ps1`) reads the TRX file and writes the
  pass/fail/skip counts and the names and messages of failed tests to `$GITHUB_STEP_SUMMARY`.
  The TRX file is uploaded as a build artifact.
- **Rationale**: FR-002/SC-005 (cause visible from the summary) without a third-party action
  that would need `checks: write` permissions on pull requests from forks.
- **Alternatives**: `dorny/test-reporter` or similar (extra dependency and permissions);
  reading the raw log (fails SC-005).

## R-4. Disk space used by the test suite

- **Decision**: Make tests clean up after themselves: `TestPaths` (already per-run folders since
  feature 002) deletes the current run folder on process exit, and the largest shrink tests
  delete their output directory in a `finally`. Point `TEMP`/`TMP` at `$RUNNER_TEMP` in CI.
  Record the measured peak usage in the CI summary (`Get-PSDrive`).
- **Rationale**: a full local run left about 9.5 GB behind; hosted runners have limited free
  space, and a full disk fails tests with misleading errors (seen in feature 002).
- **Alternatives**: skip the shrink tests in CI (loses the most valuable coverage); larger
  runners (cost).

## R-5. Opt-in tests (hardware, 16 GiB benchmark)

- **Decision**: Keep them opt-in. Mark them with `Skip`-style behavior that xUnit reports as
  skipped when the environment variable is absent (a `SkippableFact`-like attribute written in
  the test project, about 20 lines, instead of the current early `return`, which counts as a
  pass).
- **Rationale**: edge case "skipped tests show as not run, not as passed".
- **Alternatives**: `Xunit.SkippableFact` package (new dependency for one feature); leave the
  early return (misleading pass).

## R-6. Version stamping

- **Decision**: The version source for a release is the tag. The release workflow passes
  `-p:Version=X.Y.Z[-suffix]` to the build; the SDK derives `AssemblyVersion` (`X.Y.Z.0`),
  `FileVersion` and `InformationalVersion`. The About dialog keeps reading
  `AssemblyName.Version` (3 parts). The `<Version>` in the csproj stays as the development
  default; CI builds on branches get `-p:Version=0.0.0-ci.<run number>` so they can never be
  confused with a release.
- **Rationale**: FR-009; one source of truth; no version file to keep in sync.
- **Alternatives**: a `version.txt` checked by CI (two sources of truth); GitVersion/MinVer
  (new dependency for a one-line need).

## R-7. Tag validation and "tag must be on main"

- **Decision**: `tools/release/Test-ReleaseTag.ps1` validates the tag against
  `^v(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$` and checks
  `git merge-base --is-ancestor <tag commit> origin/main` (checkout with full history). It
  outputs the version and an `is_prerelease` flag.
- **Rationale**: FR-007, FR-008, FR-012; scripted so it can be tested locally with Pester.
- **Alternatives**: workflow `if:` expressions only (cannot check ancestry); GitHub "release
  from branch" rules (not available for tags).

## R-8. Release notes extraction

- **Decision**: `tools/release/Get-ReleaseNotes.ps1 -Version X.Y.Z` returns the section of
  `docs/release-notes.md` whose heading starts with `## X.Y.Z` up to the next `## `. A missing
  section is an error (FR-011). The release body is: a fixed banner "Hardware checklist:
  pending — this draft must not be published until it passes" (FR-014) + the section + a
  checksum block with the SHA-256 (FR-010).
- **Rationale**: the existing release notes already use `## 1.0.0 — …` headings.
- **Alternatives**: GitHub auto-generated notes (lists PR titles, not the curated
  verification status the constitution requires).

## R-9. Creating the draft release

- **Decision**: Use the preinstalled GitHub CLI: `gh release create vX.Y.Z --draft
  --verify-tag --title "HddBackup X.Y.Z" --notes-file body.md [--prerelease] HddBackup.exe
  HddBackup.exe.config SHA256SUMS`. Before creating, `gh release view vX.Y.Z` must report
  "not found"; an existing release (draft or published) stops the run (FR-013). The job has
  `permissions: contents: write`; all other jobs `contents: read` (FR-015).
- **Rationale**: no third-party release action; `--verify-tag` fails if the tag vanished.
- **Alternatives**: `softprops/action-gh-release` (third-party, can update existing releases
  by default — the opposite of FR-013).

## R-10. Workflow layout

- **Decision**: Two workflows.
  - `ci.yml`: `pull_request` (to main), `push` (main), `workflow_dispatch`. Jobs:
    `build-test` (Windows; build, test, artifact check, upload exe + TRX + exported ext4
    images) and `ext4-check` (Ubuntu; needs build-test; downloads the images and runs
    `tests/fixtures/ext4/check-output.sh`). The required status check is the job name
    `build-test` (and `ext4-check`, see R-12).
  - `release.yml`: `push` of tags `v*`. Jobs: `validate` (tag + notes, no build), `build`
    (same steps as CI with the release version, uploads the files), `publish` (needs both;
    creates the draft; the only job with write permission).
  - Shared build steps live in a composite action `.github/actions/build-test/action.yml`
    so CI and release build identically.
- **Rationale**: FR-001, FR-006, FR-007, FR-015; validation before building (scenario 4);
  identical build steps (SC-004).
- **Alternatives**: one workflow with conditionals (harder to reason about permissions);
  reusable workflow (`workflow_call`) instead of composite action (heavier, separate job
  context).

## R-11. Third-party actions and supply chain

- **Decision**: Only GitHub-owned actions (`actions/checkout`, `actions/setup-dotnet`,
  `actions/upload-artifact`, `actions/download-artifact`), each pinned to a full commit SHA with
  the version in a comment; Dependabot (`.github/dependabot.yml`, ecosystem
  `github-actions`, weekly) proposes updates as pull requests, which go through CI like any
  change.
- **Rationale**: SHA pinning prevents a moved tag from changing the release build; Dependabot
  keeps pins current. Matches the constitution's "avoid third-party dependencies".
- **Alternatives**: major-version tags (`@v4`; mutable); no Dependabot (pins rot).

## R-12. Main branch and tag protection (FR-003a–e)

- **Decision**: Repository rulesets, stored as JSON in `.github/rulesets/`:
  - `main.json` — target `branch`, include `~DEFAULT_BRANCH`; rules `deletion`,
    `non_fast_forward`, `required_linear_history`, `pull_request`
    (`required_approving_review_count: 0`, `allowed_merge_methods: ["squash"]`),
    `required_status_checks` (`build-test`, `ext4-check`, integration GitHub Actions,
    `strict_required_status_checks_policy: false`); bypass actor `RepositoryRole` id 5
    (repository admin = the owner), **`bypass_mode: pull_request`**: the owner can only bypass
    when merging a pull request (for example a broken CI that blocks every PR), by explicitly
    choosing "bypass rules" in the merge dialog. A plain `git push` to main is rejected even for
    the owner, so the bypass can never happen by accident. A true direct-push emergency needs a
    deliberate step: `Set-RepoProtection.ps1 -Suspend main-protection` (sets enforcement to
    `disabled`), then reapplying the rules.
  - `release-tags.json` — target `tag`, include `refs/tags/v*`; rules `creation`, `update`,
    `deletion`, `non_fast_forward`; bypass actor `RepositoryRole` id 5, `bypass_mode: always`
    (the tag rule set cannot use `pull_request` mode; the owner creates release tags through
    this bypass).
  - **Immutable releases** enabled for the repository (`PUT /repos/{repo}/immutable-releases`):
    once a release is published, its tag and assets cannot be changed or deleted by anyone,
    the owner included. Drafts stay editable, which matches the draft-then-publish flow
    (clarification Q1). Together with the tag rule set this gives: non-owners cannot touch `v*`
    tags at all; the owner cannot change a published version even by accident.
  - `tools/github/Set-RepoProtection.ps1` (uses `gh api`): `-Show` prints the active rulesets
    and a diff against the files; default mode creates missing rulesets (`POST`) and updates
    existing ones matched by name (`PUT`), and sets repository merge options
    (`allow_squash_merge: true`, `allow_merge_commit: false`, `allow_rebase_merge: false`,
    `delete_branch_on_merge: true`). It checks `gh api repos/{repo}` → `permissions.admin`
    first and exits without changes if false.
- **Rationale**: rulesets are the current GitHub mechanism, support tag rules and merge-method
  restrictions, and are available on public personal repositories. `RepositoryRole` is used
  because `OrganizationAdmin` does not apply to personal repositories. Strict
  "up-to-date branch" checking is off: with one maintainer it only forces extra rebases.
- **Note**: the tag `creation` rule also stops the release workflow's token from creating
  tags; the workflow never creates tags (it only uses the pushed one), so this is intended.
- **Alternatives**: classic branch protection (no tag rules, no merge-method restriction);
  applying rules from a workflow (needs an admin token stored as a secret, rejected in
  clarification Q4); `bypass_mode: always` on main (rejected: the owner's ordinary
  `git push origin main` would silently succeed, so the bypass would not be deliberate).

## R-13. Independent ext4 check in CI (US3)

- **Decision**: The Windows job sets `HDDBACKUP_EXPORT_DIR` so the existing tests export the
  rebuilt and shrunk images, and uploads them as a short-lived artifact (1 day). The Ubuntu job
  installs `e2fsprogs`, `dosfstools`, `fdisk` with apt and runs `check-output.sh` for every
  exported image against its fixture; it fails on the first finding and lists all results in
  the step summary.
- **Rationale**: same script as the manual check in feature 002; no Docker needed on the hosted
  Ubuntu image.
- **Alternatives**: WSL on the Windows runner (slow setup, not always available); skipping US3
  (P3, but cheap once the export exists).

## R-14. Time budget (SC-001: ≤ 15 min)

- **Decision / estimate**: checkout + SDK ≈ 1 min, restore + build ≈ 2 min, tests ≈ 3–5 min on
  hosted hardware (31 s locally), artifact check + uploads ≈ 1 min, Linux check ≈ 3 min in
  parallel stages → about 10 min. No NuGet cache: restore pulls only the xUnit packages and the
  reference assemblies (well under a minute), and `setup-dotnet` caching would require adding
  lock files.
- **Alternatives**: NuGet cache with lock files, if the measurement after implementation exceeds
  the budget.
