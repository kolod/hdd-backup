<#
.SYNOPSIS
    Fails when any release (draft or published) already exists for a tag.
.DESCRIPTION
    `gh release view <tag>` does not find drafts (the by-tag lookup ignores them), so this lists
    all releases and compares tag names. Exit 0 when none exists, 1 when one exists or the list
    cannot be read. Contract: specs/003-ci-release-automation/contracts/scripts.md (FR-013).
#>
param(
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [string] $Repo
)

$ErrorActionPreference = 'Stop'

function Fail([string] $message) {
    # Write-Host (not [Console]::Error) so in-process callers and Pester can capture it.
    Write-Host "$(if ($env:GITHUB_ACTIONS) { '::error::' })$message"
    exit 1
}

$out = gh api --paginate "repos/$Repo/releases?per_page=100" --jq '.[] | [.tag_name, (.draft | tostring), (.id | tostring)] | join(" ")' 2>&1
if ($LASTEXITCODE -ne 0) {
    Fail "Could not list releases of ${Repo}: $($out -join ' ')"
}

$existing = @($out | Where-Object { $_ } | ForEach-Object {
    $tagName, $draft, $id = "$_" -split ' '
    [pscustomobject]@{ Tag = $tagName; Draft = $draft -eq 'true'; Id = $id }
} | Where-Object Tag -eq $Tag)

if ($existing.Count -gt 0) {
    $kinds = ($existing | ForEach-Object { if ($_.Draft) { "draft $($_.Id)" } else { "published $($_.Id)" } }) -join ', '
    Fail "A release for $Tag already exists ($kinds). Releases are never overwritten; delete the draft or use a new version."
}

Write-Host "No release exists for $Tag."
exit 0
