<#
.SYNOPSIS
    Applies or shows the main-branch and release-tag protection of the GitHub repository.
.DESCRIPTION
    The rules live in .github/rulesets/*.json (one repository ruleset per file, matched on
    GitHub by name). A plain run creates missing rulesets, updates existing ones, sets the merge
    options (squash only) and enables immutable releases, then shows the result.

    -Show     Prints the active state and the differences from the files (exit 4 if any).
    -WhatIf   Prints the changes without sending them.
    -Suspend  Emergency only: disables one ruleset after a typed confirmation. A plain run
              restores it.

    Must be run by a repository admin with an authenticated GitHub CLI (gh); it never stores a
    credential and never deletes rulesets it does not manage.
    Exit codes: 0 ok, 1 error, 3 not an admin, 4 differences found (-Show).
    Contract: specs/003-ci-release-automation/contracts/scripts.md and contracts/rulesets.md.
#>
[CmdletBinding(SupportsShouldProcess, PositionalBinding = $false)]
param(
    [string] $Repo = 'kolod/hdd-backup',
    [switch] $Show,
    [string] $Suspend,
    [string] $RulesetDir = (Join-Path $PSScriptRoot '..\..\.github\rulesets')
)

$ErrorActionPreference = 'Stop'

$MergeSettings = [ordered]@{
    allow_squash_merge          = $true
    allow_merge_commit          = $false
    allow_rebase_merge          = $false
    delete_branch_on_merge      = $true
    squash_merge_commit_title   = 'PR_TITLE'
    squash_merge_commit_message = 'PR_BODY'
}

function Fail([int] $code, [string] $message) {
    [Console]::Error.WriteLine($message)
    exit $code
}

# One GitHub API call through the GitHub CLI. Returns the parsed JSON (or $null for no content).
function Invoke-Api([string] $method, [string] $path, $body = $null) {
    $ghArgs = @('api', '-X', $method, '-H', 'Accept: application/vnd.github+json', $path)
    $tmp = $null
    if ($null -ne $body) {
        $tmp = [IO.Path]::GetTempFileName()
        $body | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding utf8NoBOM
        $ghArgs += @('--input', $tmp)
    }
    try {
        $out = gh @ghArgs 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        if ($tmp) { Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue }
    }
    if ($code -ne 0) {
        throw "gh api $method $path failed: $($out -join ' ')"
    }
    $text = ($out -join "`n").Trim()
    if ($text) { return $text | ConvertFrom-Json -Depth 50 }
    return $null
}

# True when every value in $expected is present and equal in $actual (GitHub adds defaults).
function Test-Subset($expected, $actual) {
    # [pscustomobject] would also match any PSObject-wrapped value; test the exact type.
    if ($expected -is [System.Collections.IDictionary] -or $expected -is [System.Management.Automation.PSCustomObject]) {
        if ($null -eq $actual) { return $false }
        $names = if ($expected -is [System.Collections.IDictionary]) { $expected.Keys } else { $expected.PSObject.Properties.Name }
        foreach ($name in $names) {
            $e = if ($expected -is [System.Collections.IDictionary]) { $expected[$name] } else { $expected.$name }
            $a = if ($actual -is [System.Collections.IDictionary]) { $actual[$name] } else { $actual.PSObject.Properties[$name].Value }
            if (-not (Test-Subset $e $a)) { return $false }
        }
        return $true
    }
    if ($expected -is [array]) {
        $actualItems = @($actual)
        if ($actualItems.Count -ne $expected.Count) { return $false }
        foreach ($e in $expected) {
            if (-not ($actualItems | Where-Object { Test-Subset $e $_ } | Select-Object -First 1)) { return $false }
        }
        return $true
    }
    return "$expected" -eq "$actual"
}

function Get-DesiredRulesets {
    Get-ChildItem -LiteralPath $RulesetDir -Filter *.json | Sort-Object Name | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -Depth 50
    }
}

function Get-ActiveRulesets {
    $list = @(Invoke-Api GET "repos/$Repo/rulesets?per_page=100")
    $list | Where-Object { $_ } | ForEach-Object { Invoke-Api GET "repos/$Repo/rulesets/$($_.id)" }
}

function Show-State {
    $desired = @(Get-DesiredRulesets)
    $active = @(Get-ActiveRulesets)
    $differences = 0

    foreach ($want in $desired) {
        $have = $active | Where-Object name -eq $want.name | Select-Object -First 1
        if (-not $have) {
            Write-Host "MISSING   ruleset '$($want.name)'"
            $differences++
            continue
        }
        $ok = Test-Subset $want ($have | Select-Object name, target, enforcement, conditions, bypass_actors, rules)
        $checks = ($have.rules | Where-Object type -eq 'required_status_checks').parameters.required_status_checks.context -join ', '
        $line = "{0,-9} ruleset '{1}' ({2}, {3}; rules: {4}{5}; bypass: {6})" -f `
            ($(if ($ok) { 'OK' } else { 'DIFFERS' })), $have.name, $have.target, $have.enforcement,
            (($have.rules.type | Sort-Object) -join ', '),
            ($(if ($checks) { "; checks: $checks" } else { '' })),
            (($have.bypass_actors | ForEach-Object { "$($_.actor_type) $($_.actor_id) ($($_.bypass_mode))" }) -join ', ')
        Write-Host $line
        if (-not $ok) { $differences++ }
    }

    foreach ($extra in $active | Where-Object { $desired.name -notcontains $_.name }) {
        Write-Host "UNMANAGED ruleset '$($extra.name)' (left unchanged)"
    }

    $repoInfo = Invoke-Api GET "repos/$Repo"
    foreach ($key in $MergeSettings.Keys) {
        $value = $repoInfo.$key
        $ok = "$value" -eq "$($MergeSettings[$key])"
        Write-Host ("{0,-9} {1} = {2}" -f ($(if ($ok) { 'OK' } else { 'DIFFERS' })), $key, $value)
        if (-not $ok) { $differences++ }
    }

    $immutable = Invoke-Api GET "repos/$Repo/immutable-releases"
    $ok = [bool]$immutable.enabled
    Write-Host ("{0,-9} immutable releases = {1}" -f ($(if ($ok) { 'OK' } else { 'DIFFERS' })), [bool]$immutable.enabled)
    if (-not $ok) { $differences++ }

    return $differences
}

# --- Admin check (read-only, done in every mode) ---
try {
    $repoInfo = Invoke-Api GET "repos/$Repo"
}
catch {
    Fail 1 "Cannot read $Repo with gh: $($_.Exception.Message)"
}
if (-not $repoInfo.permissions.admin) {
    Fail 3 "You are not an administrator of $Repo; nothing was changed. Run this script as the repository owner (gh auth login)."
}

try {
    if ($Show) {
        $diff = Show-State
        if ($diff -gt 0) {
            Write-Host "$diff difference(s). Run the script without -Show to apply the files."
            exit 4
        }
        Write-Host 'Protection matches .github/rulesets and the expected repository settings.'
        exit 0
    }

    if ($Suspend) {
        $want = Get-DesiredRulesets | Where-Object name -eq $Suspend | Select-Object -First 1
        $have = Get-ActiveRulesets | Where-Object name -eq $Suspend | Select-Object -First 1
        if (-not $want -or -not $have) { Fail 1 "No managed ruleset named '$Suspend' is active on $Repo." }
        Write-Warning "This disables '$Suspend' on $Repo. Protection stays off until the script is run again."
        $typed = Read-Host "Type the ruleset name to confirm"
        if ($typed -ne $Suspend) { Fail 1 'Not confirmed; nothing was changed.' }
        $body = $want | ConvertTo-Json -Depth 50 | ConvertFrom-Json -Depth 50
        $body.enforcement = 'disabled'
        if ($PSCmdlet.ShouldProcess("ruleset '$Suspend' (id $($have.id))", 'PUT enforcement=disabled')) {
            Invoke-Api PUT "repos/$Repo/rulesets/$($have.id)" $body | Out-Null
            Write-Host "Suspended '$Suspend'. Restore with: pwsh tools/github/Set-RepoProtection.ps1"
        }
        exit 0
    }

    # --- Apply ---
    $active = @(Invoke-Api GET "repos/$Repo/rulesets?per_page=100") | Where-Object { $_ }
    foreach ($want in Get-DesiredRulesets) {
        $have = $active | Where-Object name -eq $want.name | Select-Object -First 1
        if ($have) {
            if ($PSCmdlet.ShouldProcess("ruleset '$($want.name)' (id $($have.id))", 'PUT')) {
                Invoke-Api PUT "repos/$Repo/rulesets/$($have.id)" $want | Out-Null
                Write-Host "Updated ruleset '$($want.name)'."
            }
        }
        elseif ($PSCmdlet.ShouldProcess("ruleset '$($want.name)'", 'POST (create)')) {
            Invoke-Api POST "repos/$Repo/rulesets" $want | Out-Null
            Write-Host "Created ruleset '$($want.name)'."
        }
    }

    if ($PSCmdlet.ShouldProcess($Repo, 'PATCH merge settings (squash only)')) {
        Invoke-Api PATCH "repos/$Repo" $MergeSettings | Out-Null
        Write-Host 'Merge settings: squash only, branches deleted after merge.'
    }

    if ($PSCmdlet.ShouldProcess($Repo, 'PUT immutable-releases')) {
        Invoke-Api PUT "repos/$Repo/immutable-releases" | Out-Null
        Write-Host 'Immutable releases enabled.'
    }

    if ($WhatIfPreference) { exit 0 }

    Write-Host ''
    $diff = Show-State
    exit $(if ($diff -gt 0) { 4 } else { 0 })
}
catch {
    Fail 1 $_.Exception.Message
}
