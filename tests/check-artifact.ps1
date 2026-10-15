<#
.SYNOPSIS
    Checks the release artifact against constitution Principle III (portable and compact).
.DESCRIPTION
    Verifies that the Release output is a single HddBackup.exe under 5 MB, with no culture
    satellite folders and no extra DLLs, that all six string tables are embedded, and that the
    manifest requests administrator rights. Prints the SHA-256 of the executable.
#>
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\src\HddBackup.App\bin\Release\net48')
)

$ErrorActionPreference = 'Stop'
$failures = @()
$exe = Join-Path $OutputDir 'HddBackup.exe'

if (-not (Test-Path $exe)) {
    throw "Executable not found: $exe (run 'dotnet build -c Release' first)"
}

$size = (Get-Item $exe).Length
if ($size -ge 5MB) { $failures += "HddBackup.exe is $size bytes (limit 5 MB)" }

$cultureDirs = Get-ChildItem $OutputDir -Directory | Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$' }
if ($cultureDirs) { $failures += "Satellite folders present: $($cultureDirs.Name -join ', ')" }

$dlls = Get-ChildItem $OutputDir -Filter *.dll
if ($dlls) { $failures += "Runtime DLLs present: $($dlls.Name -join ', ')" }

# Resource names and manifest are checked without loading the assembly for execution.
$bytes = [IO.File]::ReadAllBytes($exe)
$text = [Text.Encoding]::ASCII.GetString($bytes)
foreach ($code in 'en', 'uk', 'de', 'fr', 'es', 'ja') {
    if (-not $text.Contains("HddBackup.Strings.$code.resources")) { $failures += "Missing embedded strings: $code" }
}
if (-not $text.Contains('requireAdministrator')) { $failures += 'Manifest does not request administrator rights' }

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
"HddBackup.exe: $size bytes, SHA-256 $hash"

if ($failures) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

'Artifact check passed.'
