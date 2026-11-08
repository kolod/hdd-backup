<#
.SYNOPSIS
    Builds HddBackup in Release configuration and launches it.
.PARAMETER NoRun
    Build only, do not launch the app.
.PARAMETER Clean
    Clean the Release output before building.
#>
[CmdletBinding()]
param(
    [switch]$NoRun,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$project = Join-Path $root 'src\HddBackup.App\HddBackup.App.csproj'
$exe     = Join-Path $root 'src\HddBackup.App\bin\Release\net48\HddBackup.exe'

if ($Clean) {
    Write-Host '==> Cleaning Release...' -ForegroundColor Cyan
    dotnet clean $project -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Clean failed.' }
}

Write-Host '==> Building (Release)...' -ForegroundColor Cyan
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }

if (-not (Test-Path $exe)) { throw "Built executable not found: $exe" }
Write-Host "==> Built: $exe" -ForegroundColor Green

if ($NoRun) { return }

Write-Host '==> Launching...' -ForegroundColor Cyan
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
