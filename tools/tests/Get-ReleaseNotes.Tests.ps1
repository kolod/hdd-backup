BeforeAll {
    $script:Script = Join-Path $PSScriptRoot '..\release\Get-ReleaseNotes.ps1'
    $script:Notes = Join-Path $PSScriptRoot 'data\release-notes.md'
    $script:RealNotes = Join-Path $PSScriptRoot '..\..\docs\release-notes.md'

    function Invoke-Notes([string] $version, [string] $path = $script:Notes) {
        $out = & pwsh -NoProfile -File $script:Script -Version $version -Path $path 2>&1
        [pscustomobject]@{ Exit = $LASTEXITCODE; Text = ($out -join "`n") }
    }
}

Describe 'Get-ReleaseNotes' {
    It 'returns the section of an exact version without its heading' {
        $r = Invoke-Notes '1.1.0'
        $r.Exit | Should -Be 0
        $r.Text | Should -Match '^Stable notes for 1\.1\.0\.'
        $r.Text | Should -Match 'Feature A\.'
        $r.Text | Should -Not -Match '## 1\.1\.0'
    }

    It 'does not match a pre-release that starts with the same version' {
        (Invoke-Notes '1.1.0').Text | Should -Not -Match 'release candidate\.$|Notes of the release candidate'
    }

    It 'returns a pre-release section' {
        $r = Invoke-Notes '1.1.0-rc.1'
        $r.Exit | Should -Be 0
        $r.Text | Should -Be 'Notes of the release candidate.'
    }

    It 'stops at the next level-2 heading but keeps level-3 headings' {
        $r = Invoke-Notes '1.1.0'
        $r.Text | Should -Match '### Contents'
        $r.Text | Should -Not -Match 'Notes of the release candidate'
    }

    It 'returns the last section up to the end of the file' {
        (Invoke-Notes '1.0.0').Text | Should -Be 'First release.'
    }

    It 'fails for a missing section' {
        $r = Invoke-Notes '9.9.9'
        $r.Exit | Should -Be 1
        $r.Text | Should -Match 'No section'
    }

    It 'fails for an empty section' {
        $r = Invoke-Notes '1.0.5'
        $r.Exit | Should -Be 1
        $r.Text | Should -Match 'empty'
    }

    It 'finds the existing sections in docs/release-notes.md' -ForEach @('1.1.0', '1.0.0') {
        (Invoke-Notes $_ $script:RealNotes).Exit | Should -Be 0
    }
}
