# Contract: Workflows

Job names are part of the contract: `build-test` and `ext4-check` are the required status
checks in `.github/rulesets/main.json`. Renaming a job requires changing the ruleset in the same
pull request (spec edge case).

## `.github/workflows/ci.yml` — name `CI`

| Item | Value |
|------|-------|
| Triggers | `pull_request` (branches: main), `push` (branches: main), `workflow_dispatch` |
| Default permissions | `contents: read` (workflow level) |
| Concurrency | group `ci-${{ github.ref }}`, cancel in progress for pull requests |

### Job `build-test` (runs-on `windows-2025`, timeout 30 min)

1. Checkout.
2. Composite action `./.github/actions/build-test` with `version: 0.0.0-ci.${{ github.run_number }}`
   and `export-ext4: true`.
3. Upload artifacts:
   - `hddbackup-exe` — `HddBackup.exe`, `HddBackup.exe.config`, 14 days.
   - `test-results` — `results.trx`, 14 days, uploaded even on failure.
   - `ext4-exports` — exported images, 1 day.

Fails when: build warning or error, any test failure, artifact check failure.

### Job `ext4-check` (runs-on `ubuntu-24.04`, needs `build-test`, timeout 20 min)

1. Checkout (for fixtures and `check-output.sh`).
2. Download `ext4-exports`.
3. `sudo apt-get install -y e2fsprogs dosfstools fdisk`.
4. `tools/ci/Invoke-Ext4Checks.sh <exports dir> tests/HddBackup.Core.Tests/Fixtures/ext4`.
   Writes one line per image to `$GITHUB_STEP_SUMMARY`; fails if any image fails or none was
   found.

## `.github/workflows/release.yml` — name `Release`

| Item | Value |
|------|-------|
| Triggers | `push` of tags `v*` only |
| Default permissions | `contents: read` |
| Concurrency | group `release-${{ github.ref }}`, no cancel |

### Job `validate` (ubuntu-24.04)

Outputs `version`, `is_prerelease`.

1. Checkout with `fetch-depth: 0`.
2. `tools/release/Test-ReleaseTag.ps1 -Tag ${{ github.ref_name }}`. Fails on a malformed tag
   or a commit that is not on main.
3. `tools/release/Get-ReleaseNotes.ps1 -Version <version>`. Fails if the section is missing.
4. `tools/release/Test-ReleaseAbsent.ps1`. Fails if a release (draft or published) already exists.

### Job `build` (windows-2025, needs `validate`)

1. Checkout the tag.
2. Composite action with `version: <version>` and `export-ext4: false`.
3. Upload `release-files` (exe, config), 3 days.

### Job `publish` (ubuntu-24.04, needs `validate` + `build`)

- Permissions: `contents: write`. This is the only job with write access (FR-015).
- Steps:
  1. Download `release-files`.
  2. `tools/release/New-ReleaseBody.ps1` writes `body.md` and `SHA256SUMS`; `Test-ReleaseAbsent.ps1` runs again.
  3. `gh release create <tag> --draft --verify-tag --title "HddBackup <version>" --notes-file
     body.md [--prerelease] HddBackup.exe HddBackup.exe.config SHA256SUMS`.
- Fails if the release appeared in the meantime; it never edits an existing release.

## Composite action `.github/actions/build-test`

| Input | Meaning |
|-------|---------|
| `version` | value passed as `-p:Version=` |
| `export-ext4` | `true` sets `HDDBACKUP_EXPORT_DIR` for the tests |

Steps:

1. `setup-dotnet` (from `global.json`).
2. Set `TEMP`/`TMP` to `$RUNNER_TEMP`.
3. `dotnet build HddBackup.sln -c Release -warnaserror -p:Version=<version>`.
4. `dotnet test` with a TRX logger and `--no-build`.
5. `tools/ci/Write-TestSummary.ps1`, always run.
6. `tests/check-artifact.ps1`.
7. Write the free disk space to the summary.

## `.github/dependabot.yml`

- `package-ecosystem: github-actions`, directory `/`, weekly.
- Pull requests go through CI like any other change.
