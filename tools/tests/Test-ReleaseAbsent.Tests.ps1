# Uses a fake `gh` (a PowerShell function wins over the executable); nothing reaches GitHub.
BeforeAll {
    $script:Script = Join-Path $PSScriptRoot '..\release\Test-ReleaseAbsent.ps1'

    function global:gh {
        $global:FakeGhArgs = $args
        $global:LASTEXITCODE = $global:FakeGhExit
        return $global:FakeGhLines
    }

    function Invoke-Check([string] $tag, [string[]] $lines, [int] $exit = 0) {
        $global:FakeGhLines = $lines
        $global:FakeGhExit = $exit
        $out = & $script:Script -Tag $tag -Repo 'owner/repo' *>&1
        [pscustomobject]@{ Exit = $LASTEXITCODE; Output = ($out -join "`n") }
    }
}

AfterAll {
    Remove-Item function:global:gh -ErrorAction SilentlyContinue
    Remove-Variable FakeGhArgs, FakeGhLines, FakeGhExit -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Test-ReleaseAbsent' {
    It 'passes when no release has the tag' {
        $r = Invoke-Check 'v1.2.0' @('v1.1.0 false 11', 'v1.2.0-rc.1 true 12')
        $r.Exit | Should -Be 0
        $global:FakeGhArgs | Should -Contain '--paginate'
    }

    It 'fails for an existing DRAFT (gh release view does not see drafts)' {
        $r = Invoke-Check 'v1.1.0-rc.1' @('v1.1.0-rc.1 true 42')
        $r.Exit | Should -Be 1
        $r.Output | Should -Match 'already exists \(draft 42\)'
    }

    It 'fails for an existing published release' {
        $r = Invoke-Check 'v1.0.0' @('v1.0.0 false 7')
        $r.Exit | Should -Be 1
        $r.Output | Should -Match 'published 7'
    }

    It 'does not match a tag that only starts with the same text' {
        (Invoke-Check 'v1.1.0' @('v1.1.0-rc.1 true 42')).Exit | Should -Be 0
    }

    It 'passes for a repository without releases' {
        (Invoke-Check 'v1.0.0' @()).Exit | Should -Be 0
    }

    It 'fails when the release list cannot be read' {
        $r = Invoke-Check 'v1.0.0' @('HTTP 403') 1
        $r.Exit | Should -Be 1
        $r.Output | Should -Match 'Could not list releases'
    }
}
