BeforeAll {
    $script:Script = Join-Path $PSScriptRoot '..\release\Test-ReleaseTag.ps1'

    function Invoke-Tag([string] $tag, [string[]] $extra = @('-SkipAncestryCheck')) {
        $out = & pwsh -NoProfile -File $script:Script -Tag $tag @extra 2>&1
        [pscustomobject]@{ Exit = $LASTEXITCODE; Output = ($out -join "`n") }
    }
}

Describe 'Test-ReleaseTag format' {
    It 'accepts <Tag>' -ForEach @(
        @{ Tag = 'v1.2.3'; Version = '1.2.3'; Numeric = '1.2.3'; Pre = 'false' }
        @{ Tag = 'v1.2.3-rc.1'; Version = '1.2.3-rc.1'; Numeric = '1.2.3'; Pre = 'true' }
        @{ Tag = 'v10.0.0-beta-2'; Version = '10.0.0-beta-2'; Numeric = '10.0.0'; Pre = 'true' }
    ) {
        $saved = $env:GITHUB_OUTPUT
        try {
            $env:GITHUB_OUTPUT = Join-Path $TestDrive 'out.txt'
            $r = Invoke-Tag $Tag
            $r.Exit | Should -Be 0
            $lines = Get-Content $env:GITHUB_OUTPUT
            $lines | Should -Contain "version=$Version"
            $lines | Should -Contain "numeric_version=$Numeric"
            $lines | Should -Contain "is_prerelease=$Pre"
        }
        finally {
            $env:GITHUB_OUTPUT = $saved
        }
    }

    It 'rejects <_>' -ForEach @('v1.2', '1.2.3', 'release-1', 'v1.2.3.4', 'v1.2.3-', 'v01.2.3x', 'v1.2.3+build') {
        $r = Invoke-Tag $_
        $r.Exit | Should -Be 1
        $r.Output | Should -Match 'not a release tag'
    }

    It 'returns an object when dot-sourced' {
        $result = & $script:Script -Tag 'v2.0.1-rc.3' -SkipAncestryCheck
        $result.Version | Should -Be '2.0.1-rc.3'
        $result.NumericVersion | Should -Be '2.0.1'
        $result.IsPrerelease | Should -BeTrue
    }
}

Describe 'Test-ReleaseTag ancestry' {
    BeforeAll {
        $script:Repo = Join-Path $TestDrive 'repo'
        New-Item -ItemType Directory $script:Repo | Out-Null
        Push-Location $script:Repo
        git init -q -b main
        git config user.email 'test@example.invalid'
        git config user.name 'test'
        git config commit.gpgsign false   # the developer's global config may require signing
        git config tag.gpgsign false
        git commit -q --allow-empty -m 'on main'
        git tag v1.0.0
        git switch -q -c side
        git commit -q --allow-empty -m 'side only'
        git tag v1.0.1
        git switch -q main
        Pop-Location
    }

    It 'accepts a tag whose commit is on main' {
        Push-Location $script:Repo
        try { $r = Invoke-Tag 'v1.0.0' @('-MainRef', 'main') } finally { Pop-Location }
        $r.Exit | Should -Be 0
    }

    It 'rejects a tag whose commit is not on main' {
        Push-Location $script:Repo
        try { $r = Invoke-Tag 'v1.0.1' @('-MainRef', 'main') } finally { Pop-Location }
        $r.Exit | Should -Be 2
        $r.Output | Should -Match 'not on main'
    }

    It 'rejects a tag that does not exist' {
        Push-Location $script:Repo
        try { $r = Invoke-Tag 'v9.9.9' @('-MainRef', 'main') } finally { Pop-Location }
        $r.Exit | Should -Be 2
    }
}
