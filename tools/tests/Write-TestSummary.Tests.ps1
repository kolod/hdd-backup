BeforeAll {
    $script:Script = Join-Path $PSScriptRoot '..\ci\Write-TestSummary.ps1'
    $script:Trx = Join-Path $PSScriptRoot 'data\sample.trx'
}

Describe 'Write-TestSummary' {
    BeforeEach {
        $script:SavedSummary = $env:GITHUB_STEP_SUMMARY
        $env:GITHUB_STEP_SUMMARY = Join-Path $TestDrive 'summary.md'
    }

    AfterEach {
        $env:GITHUB_STEP_SUMMARY = $script:SavedSummary
    }

    It 'writes totals, failed tests with their first message line, and skipped tests' {
        & pwsh -NoProfile -File $script:Script -TrxPath $script:Trx
        $LASTEXITCODE | Should -Be 0

        $text = Get-Content $env:GITHUB_STEP_SUMMARY -Raw
        $text | Should -Match 'Passed: 3'
        $text | Should -Match 'Failed: 1'
        $text | Should -Match 'Skipped: 1'
        $text | Should -Match 'Suite\.Beta\.Fails'
        $text | Should -Match 'Assert\.Equal\(\) Failure: Values differ'
        $text | Should -Not -Match 'Expected: 1'      # only the first message line
        $text | Should -Match 'Suite\.Gamma\.NeedsHardware'
    }

    It 'appends to an existing summary instead of replacing it' {
        Set-Content $env:GITHUB_STEP_SUMMARY 'earlier step'
        & pwsh -NoProfile -File $script:Script -TrxPath $script:Trx
        (Get-Content $env:GITHUB_STEP_SUMMARY -Raw) | Should -Match '^earlier step'
    }

    It 'escapes pipe characters in test names and messages' {
        $trx = Join-Path $TestDrive 'pipe.trx'
        (Get-Content $script:Trx -Raw).Replace('Values differ', 'a | b') | Set-Content $trx
        & pwsh -NoProfile -File $script:Script -TrxPath $trx
        (Get-Content $env:GITHUB_STEP_SUMMARY -Raw) | Should -Match 'a \\\| b'
    }

    It 'prints to stdout when GITHUB_STEP_SUMMARY is not set' {
        $env:GITHUB_STEP_SUMMARY = $null
        $out = & pwsh -NoProfile -File $script:Script -TrxPath $script:Trx
        ($out -join "`n") | Should -Match 'Failed: 1'
    }

    It 'fails for a missing TRX file' {
        & pwsh -NoProfile -File $script:Script -TrxPath (Join-Path $TestDrive 'missing.trx') 2>$null
        $LASTEXITCODE | Should -Be 1
    }
}
