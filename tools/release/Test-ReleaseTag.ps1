<#
.SYNOPSIS
    Validates a release tag and checks that its commit is on main.
.DESCRIPTION
    Accepts vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-SUFFIX. Writes version, numeric_version and
    is_prerelease to $env:GITHUB_OUTPUT when set and returns an object with the same values.
    Exit 1: malformed tag. Exit 2: the tag does not exist or its commit is not on -MainRef.
    Contract: specs/003-ci-release-automation/contracts/scripts.md.
#>
param(
    [Parameter(Mandatory)] [string] $Tag,
    [string] $MainRef = 'origin/main',
    # For unit tests only: skip the git ancestry check.
    [switch] $SkipAncestryCheck
)

$ErrorActionPreference = 'Stop'

function Fail([int] $code, [string] $message) {
    if ($env:GITHUB_ACTIONS) { Write-Host "::error::$message" }
    [Console]::Error.WriteLine($message)
    exit $code
}

$match = [regex]::Match($Tag, '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?$')
if (-not $match.Success -or $Tag.EndsWith('-')) {
    Fail 1 "'$Tag' is not a release tag. Expected vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-SUFFIX (for example v1.2.0 or v1.2.0-rc.1)."
}

$numeric = '{0}.{1}.{2}' -f $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value
$version = $Tag.Substring(1)
$isPrerelease = $match.Groups[4].Success

if (-not $SkipAncestryCheck) {
    $commit = git rev-parse --verify --quiet "refs/tags/$Tag^{commit}"
    if ($LASTEXITCODE -ne 0 -or -not $commit) {
        Fail 2 "Tag '$Tag' does not exist in this clone."
    }

    git merge-base --is-ancestor $commit $MainRef
    if ($LASTEXITCODE -ne 0) {
        Fail 2 "Tag '$Tag' points to $commit, which is not on main ($MainRef). Releases are built only from main."
    }
}

if ($env:GITHUB_OUTPUT) {
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Value @(
        "version=$version"
        "numeric_version=$numeric"
        "is_prerelease=$($isPrerelease.ToString().ToLowerInvariant())"
    )
}

[pscustomobject]@{
    Version        = $version
    NumericVersion = $numeric
    IsPrerelease   = $isPrerelease
}
exit 0   # callers using & check $LASTEXITCODE
