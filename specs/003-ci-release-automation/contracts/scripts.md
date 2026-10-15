# Contract: Scripts

All scripts are PowerShell 7 unless noted. They run on hosted runners and on the developer
machine. On error they write a single-line message to stderr and exit non-zero. On GitHub
Actions they also emit `::error::<message>`.

## `tools/release/Test-ReleaseTag.ps1`

```text
Test-ReleaseTag.ps1 -Tag <string> [-MainRef <string> = 'origin/main'] [-SkipAncestryCheck]
```

- Writes `version=<x>`, `numeric_version=<MAJOR.MINOR.PATCH>` and `is_prerelease=<true|false>`
  to `$GITHUB_OUTPUT` when set. `numeric_version` is used to check the stamped file version.
- Always returns an object `{ Version, NumericVersion, IsPrerelease }`.
- Exit 1 for a malformed tag.
- Exit 2 when the tag commit is not an ancestor of `-MainRef`.
- `-SkipAncestryCheck` exists for the Pester tests only.
- Stricter than the data-model regex: no leading zeros (`v01.2.3`), no trailing `-`.

## `tools/release/Get-ReleaseNotes.ps1`

```text
Get-ReleaseNotes.ps1 -Version <string> [-Path docs/release-notes.md]
```

- Returns the section text, without its heading line, trimmed.
- Exit 1 when the section is missing or empty.
- A heading matches only when it is `## <version>` followed by end of line, a space, or ` — `.
  Example: `1.1.0` must not match `1.1.0-rc.1`.

## `tools/release/Test-ReleaseAbsent.ps1`

```text
Test-ReleaseAbsent.ps1 -Tag <string> -Repo <owner/name>
```

- Lists **all** releases (`gh api --paginate repos/<repo>/releases`) and fails (exit 1) when one
  has the tag, draft or published (FR-013). `gh release view <tag>` cannot be used, because the
  by-tag lookup ignores drafts. That was found in the dry run: a re-run created a second draft.
- Used by `release.yml` in `validate` and again in `publish` right before `gh release create`.
## `tools/release/New-ReleaseBody.ps1`

```text
New-ReleaseBody.ps1 -Version <string> -FilesDir <dir> -OutDir <dir> [-NotesPath docs/release-notes.md]
```

Writes two files to `OutDir`:

- **`SHA256SUMS`**: `sha256sum` format, lowercase hex, sorted by file name, LF line endings.
- **`body.md`**, in this order:
  1. A banner: `> **Hardware checklist pending.** Do not publish this draft until
     docs/manual-hardware-checklist.md has passed on Windows 7 SP1 and Windows 11 and the
     results are recorded below.`
  2. The notes section.
  3. A `### Checksums` block containing the `SHA256SUMS` lines.

Exit 1 when `HddBackup.exe` is missing in `FilesDir`.

## `tools/ci/Write-TestSummary.ps1`

```text
Write-TestSummary.ps1 -TrxPath <file>
```

- Appends Markdown to `$GITHUB_STEP_SUMMARY` (or stdout locally):
  - totals: passed, failed, skipped;
  - a table of failed tests with the first line of each message;
  - the names of skipped tests.
- Exit 0 even when tests failed; the test step itself carries the failure.
- Exit 1 when the TRX file is missing or unreadable.

## `tools/ci/Invoke-Ext4Checks.sh` (Bash)

```text
Invoke-Ext4Checks.sh <exports-dir> <fixtures-dir>
```

- For each exported file, finds its source fixture and runs
  `tests/fixtures/ext4/check-output.sh <image> <fixtures-dir>/<fixture>.img.gz`:
  - `<fixture>-<factor>.ext4` (bare rebuilt partition from `Ext4WriterTests`); the fixture name
    is taken from `^(.+)-[0-9]+\.[0-9]{2}\.ext4$`;
  - `shrunk-<fixture>.img` (full shrunk image from `ImageShrinkerTests`).
- Exit 1 when no image is found or any check fails.
- Prints a summary line per image.

## `tools/github/Set-RepoProtection.ps1`

```text
Set-RepoProtection.ps1 [-Repo kolod/hdd-backup] [-Show] [-WhatIf]
Set-RepoProtection.ps1 -Suspend <ruleset name> [-Repo …]
```

- Requires `gh` to be authenticated as a repository admin. Checks
  `gh api repos/<repo> --jq .permissions.admin` and exits 3 with no changes if it is false.
- **`-Show`**: prints the active rulesets (name, target, enforcement, rules, required checks,
  bypass actors) and the merge settings. Marks differences from `.github/rulesets/*.json` and
  the expected merge settings. Exit 0 when they match, 4 when they differ.
- **Default**: for each JSON file, finds the ruleset with the same `name`:
  - missing → `POST /repos/<repo>/rulesets`;
  - existing → `PUT /repos/<repo>/rulesets/<id>`.
  Then `PATCH /repos/<repo>` with the merge settings and `PUT /repos/<repo>/immutable-releases`,
  and finally runs `-Show`. `-Show` also reports whether immutable releases are on.
- **`-Suspend <name>`**: emergency only. Sets that ruleset's enforcement to `disabled` after a
  typed confirmation (the user must enter the ruleset name). Prints the command that restores
  it, which is a plain run of the script.
- **`-WhatIf`**: prints the requests without sending them.
- The script never deletes rulesets it does not manage.

## Test project additions

- **`OptInFactAttribute`** (`[OptInFact("ENV_VAR")]`): when the environment variable is empty,
  it sets `Skip = "Set ENV_VAR to run this test"`, so xUnit reports the test as skipped.
- **`TestPaths`**: registers `AppDomain.ProcessExit` to delete its own `run-<pid>` folder;
  failures are ignored.
