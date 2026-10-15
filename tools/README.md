# Developer and CI tools

Scripts used by the GitHub Actions workflows (`.github/workflows/`) and by the maintainer. They
are developer tooling only; nothing here is part of the shipped executable.

| Script | Used by | Purpose |
|--------|---------|---------|
| `ci/Write-TestSummary.ps1` | CI, release | Turns the xUnit TRX file into a Markdown summary (totals, failed tests, skipped tests) |
| `ci/Invoke-Ext4Checks.sh` | CI (`ext4-check`) | Runs `tests/fixtures/ext4/check-output.sh` on every ext4 image exported by the tests |
| `release/Test-ReleaseTag.ps1` | release | Validates a `vX.Y.Z[-suffix]` tag and checks that its commit is on main |
| `release/Get-ReleaseNotes.ps1` | release | Extracts the `## X.Y.Z` section from `docs/release-notes.md` |
| `release/New-ReleaseBody.ps1` | release | Writes `SHA256SUMS` and the draft release text |
| `github/Set-RepoProtection.ps1` | maintainer | Applies or shows the main/tag protection rules (`.github/rulesets/`) |

Interfaces: `specs/003-ci-release-automation/contracts/scripts.md`.

## Tests

The PowerShell scripts are covered by Pester 5 tests in `tests/`:

```powershell
Install-Module Pester -RequiredVersion 5.7.1 -Scope CurrentUser   # once
Invoke-Pester tools/tests
```

CI runs the same tests in every build.
