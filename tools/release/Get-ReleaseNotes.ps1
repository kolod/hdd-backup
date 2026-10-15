<#
.SYNOPSIS
    Returns the release notes section of one version from docs/release-notes.md.
.DESCRIPTION
    A section starts at a heading "## <version>" (followed by end of line or a space, e.g.
    "## 1.1.0 — release candidate") and ends before the next "## " heading. The heading line is
    not included. Exit 1 when the section is missing or empty.
    Contract: specs/003-ci-release-automation/contracts/scripts.md.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Path = (Join-Path $PSScriptRoot '..\..\docs\release-notes.md')
)

$ErrorActionPreference = 'Stop'

function Fail([string] $message) {
    if ($env:GITHUB_ACTIONS) { Write-Host "::error::$message" }
    [Console]::Error.WriteLine($message)
    exit 1
}

if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    Fail "Release notes file not found: $Path"
}

$lines = (Get-Content -LiteralPath $Path -Raw -Encoding utf8) -split "\r?\n"
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    $m = [regex]::Match($lines[$i], '^## (\S+)(\s|$)')
    if ($m.Success -and $m.Groups[1].Value -eq $Version) {
        $start = $i + 1
        break
    }
}

if ($start -lt 0) {
    Fail "No section '## $Version' in $Path. Add the release notes for $Version before tagging."
}

$end = $lines.Count
for ($i = $start; $i -lt $lines.Count; $i++) {
    if ($lines[$i].StartsWith('## ')) {
        $end = $i
        break
    }
}

$text = ($lines[$start..([Math]::Max($start, $end - 1))] -join "`n").Trim()
if ($end -eq $start -or -not $text) {
    Fail "The section '## $Version' in $Path is empty."
}

$text
exit 0   # callers using & check $LASTEXITCODE
