<#
.SYNOPSIS
    Writes SHA256SUMS and the draft release text for one version.
.DESCRIPTION
    SHA256SUMS: one "<sha256>  <file>" line per file in -FilesDir (sha256sum format, lowercase,
    sorted by name, LF endings). body.md: the pending-hardware-checklist banner, the release
    notes section of the version, and a checksum block. Exit 1 when HddBackup.exe is missing or
    the notes section is missing. Contract: specs/003-ci-release-automation/contracts/scripts.md.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $FilesDir,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $NotesPath = (Join-Path $PSScriptRoot '..\..\docs\release-notes.md')
)

$ErrorActionPreference = 'Stop'

function Fail([string] $message) {
    if ($env:GITHUB_ACTIONS) { Write-Host "::error::$message" }
    [Console]::Error.WriteLine($message)
    exit 1
}

if (-not (Test-Path -LiteralPath (Join-Path $FilesDir 'HddBackup.exe') -PathType Leaf)) {
    Fail "HddBackup.exe not found in $FilesDir."
}

$notes = & (Join-Path $PSScriptRoot 'Get-ReleaseNotes.ps1') -Version $Version -Path $NotesPath
if ($LASTEXITCODE -ne 0) {
    exit 1   # Get-ReleaseNotes already reported the reason
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$sums = Get-ChildItem -LiteralPath $FilesDir -File |
    Sort-Object Name -Culture ([Globalization.CultureInfo]::InvariantCulture) |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS'), (($sums -join "`n") + "`n"), $utf8)

$body = @(
    '> **Hardware checklist pending.** Do not publish this draft until docs/manual-hardware-checklist.md has passed on Windows 7 SP1 and Windows 11 and the results are recorded below.'
    ''
    ($notes -join "`n")
    ''
    '### Checksums'
    ''
    'Verify the download with `certutil -hashfile HddBackup.exe SHA256` or `Get-FileHash HddBackup.exe`:'
    ''
    '```text'
    $sums
    '```'
)
[IO.File]::WriteAllText((Join-Path $OutDir 'body.md'), (($body -join "`n") + "`n"), $utf8)
