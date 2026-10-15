# Releasing HddBackup

Releases are built by GitHub Actions (`.github/workflows/release.yml`) from a version tag on
`main` and created as **drafts**. A draft becomes public only when the maintainer publishes it
after the manual hardware checklist has passed (constitution Principle VI).

Published releases are **immutable**: their tag and files can never change. A mistake in a
published release is fixed by releasing a new version.

## 1. Write the release notes

In a pull request, add a section to [`release-notes.md`](release-notes.md):

```markdown
## 1.2.0 — short title (YYYY-MM-DD)

What changed, verification status, …
```

- The heading must start with `## ` followed by the exact version (`1.2.0`, or `1.2.0-rc.1`
  for a pre-release).
- Everything up to the next `## ` heading becomes the release text.

Merge the pull request (squash).

## 2. Tag the release

```powershell
git switch main
git pull --ff-only
git tag v1.2.0          # or v1.2.0-rc.1 for a pre-release
git push origin v1.2.0
```

Only the repository owner can create `v*` tags. The `Release` workflow then:

1. Checks that the tag is `vX.Y.Z[-suffix]`, that its commit is on `main`, that the notes section
   exists, and that no release exists for the tag.
2. Builds and tests that commit exactly as CI does, with the version stamped into the
   executable.
3. Creates a **draft** release with `HddBackup.exe`, `HddBackup.exe.config` and `SHA256SUMS`.
   Tags with a suffix produce a pre-release.

If any step fails, nothing is published. Fix the problem and release a **new** version; do not
reuse the tag.

## 3. Hardware checklist

Download the draft's `HddBackup.exe` and check it:

```powershell
Get-FileHash .\HddBackup.exe        # must equal the HddBackup.exe line in SHA256SUMS
```

Then:

1. Confirm that the About dialog shows the tagged version.
2. Run [`manual-hardware-checklist.md`](manual-hardware-checklist.md) on Windows 7 SP1 and
   Windows 11 with this exact file.

## 4. Publish

Edit the draft on GitHub:

1. Add the checklist results (date, OS, media, pass/fail per step).
2. Remove the "Hardware checklist pending" banner.
3. Publish.

From this moment the release is final.

If the checklist fails, delete the draft (drafts can be deleted), fix the problem, and release
a new version.

## Emergency procedures (main branch protection)

`main` accepts changes only through pull requests with green `build-test` and `ext4-check`
checks, merged by squash. Direct pushes are rejected for everyone, including the owner.

- **CI is broken and blocks every pull request**: open the pull request with the fix. As the
  owner, choose **"Merge without waiting for requirements to be met (bypass rules)"** in the
  merge box. The bypass is recorded in the repository's rule insights.
- **A direct push to `main` is unavoidable**:
  ```powershell
  pwsh tools/github/Set-RepoProtection.ps1 -Suspend main-protection   # asks for the name to confirm
  # … push …
  pwsh tools/github/Set-RepoProtection.ps1                            # re-applies all rules
  pwsh tools/github/Set-RepoProtection.ps1 -Show                      # exit 0 = rules match the files
  ```

The rules themselves live in [`.github/rulesets/`](../.github/rulesets/). Change them through a
pull request, then re-run the script.
