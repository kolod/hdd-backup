<#
.SYNOPSIS
    Writes a Markdown summary of an xUnit TRX result file.
.DESCRIPTION
    Appends totals, a table of failed tests (first line of each message) and the names of
    skipped tests to $env:GITHUB_STEP_SUMMARY, or prints them when that is not set. Exits 0
    even when tests failed: the test step itself carries the failure. Exits 1 when the TRX file
    is missing or unreadable. Contract: specs/003-ci-release-automation/contracts/scripts.md.
#>
param(
    [Parameter(Mandatory)] [string] $TrxPath
)

$ErrorActionPreference = 'Stop'

function Fail([string] $message) {
    if ($env:GITHUB_ACTIONS) { Write-Host "::error::$message" }
    [Console]::Error.WriteLine($message)
    exit 1
}

function Escape([string] $text) {
    return $text.Replace('|', '\|').Replace("`r", '').Trim()
}

if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
    Fail "TRX file not found: $TrxPath"
}

try {
    [xml] $trx = Get-Content -LiteralPath $TrxPath -Raw
}
catch {
    Fail "TRX file is not valid XML: $TrxPath ($($_.Exception.Message))"
}

$ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
$results = @(Select-Xml -Xml $trx -XPath '//t:UnitTestResult' -Namespace $ns | ForEach-Object { $_.Node })
$passed = @($results | Where-Object outcome -eq 'Passed')
$failed = @($results | Where-Object { $_.outcome -eq 'Failed' -or $_.outcome -eq 'Error' -or $_.outcome -eq 'Timeout' })
$skipped = @($results | Where-Object { $_.outcome -eq 'NotExecuted' -or $_.outcome -eq 'Inconclusive' })

$lines = [System.Collections.Generic.List[string]]::new()
$status = if ($failed.Count -gt 0) { ':x: Tests failed' } else { ':white_check_mark: Tests passed' }
$lines.Add("### $status")
$lines.Add('')
$lines.Add("Passed: $($passed.Count) · Failed: $($failed.Count) · Skipped: $($skipped.Count) · Total: $($results.Count)")

if ($failed.Count -gt 0) {
    $lines.Add('')
    $lines.Add('| Failed test | Message |')
    $lines.Add('|-------------|---------|')
    foreach ($test in $failed) {
        $message = ''
        $node = Select-Xml -Xml $test -XPath './/t:ErrorInfo/t:Message' -Namespace $ns | Select-Object -First 1
        if ($node) {
            $message = ($node.Node.InnerText -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
        }
        $lines.Add("| $(Escape $test.testName) | $(Escape $message) |")
    }
}

if ($skipped.Count -gt 0) {
    $lines.Add('')
    $lines.Add('Skipped (not run, not counted as passed):')
    foreach ($test in $skipped) {
        $lines.Add("- $(Escape $test.testName)")
    }
}

$lines.Add('')
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $lines -Encoding utf8
}
else {
    $lines | Write-Output
}

exit 0
