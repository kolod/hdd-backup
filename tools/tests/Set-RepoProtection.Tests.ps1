# Set-RepoProtection.ps1 is tested against a fake `gh` (a PowerShell function takes precedence
# over the gh executable), so no request ever reaches GitHub.
BeforeAll {
    $script:Script = Join-Path $PSScriptRoot '..\github\Set-RepoProtection.ps1'
    $script:RulesetDir = Join-Path $PSScriptRoot '..\..\.github\rulesets'

    function New-FakeGitHub([bool] $admin = $true, [object[]] $rulesets = @(), [hashtable] $repoSettings = @{}, [bool] $immutable = $false) {
        $global:FakeGhCalls = [System.Collections.Generic.List[string]]::new()
        $global:FakeGhState = @{ Admin = $admin; Rulesets = [System.Collections.Generic.List[object]]::new(); Repo = $repoSettings; Immutable = $immutable; NextId = 100 }
        foreach ($r in $rulesets) { $global:FakeGhState.Rulesets.Add($r) }
    }

    function global:gh {
        $method = 'GET'; $path = $null; $payload = $null
        for ($i = 1; $i -lt $args.Count; $i++) {
            switch ($args[$i]) {
                '-X' { $method = $args[++$i] }
                '-H' { $i++ }
                '--input' { $payload = Get-Content $args[++$i] -Raw | ConvertFrom-Json -Depth 50 }
                default { $path = $args[$i] }
            }
        }
        $global:FakeGhCalls.Add("$method $path")
        $global:LASTEXITCODE = 0
        $s = $global:FakeGhState
        switch -Regex ("$method $path") {
            '^GET repos/[^/]+/[^/]+$' {
                $o = @{ permissions = @{ admin = $s.Admin } } + $s.Repo
                return ($o | ConvertTo-Json -Depth 10)
            }
            '^GET .*/rulesets\?' { return (ConvertTo-Json -InputObject @($s.Rulesets | ForEach-Object { @{ id = $_.id; name = $_.name } }) -Depth 5) }
            '^GET .*/rulesets/(\d+)$' { return ($s.Rulesets | Where-Object id -eq ([int]$Matches[1]) | ConvertTo-Json -Depth 50) }
            '^GET .*/immutable-releases$' { return (@{ enabled = $s.Immutable } | ConvertTo-Json) }
            '^POST .*/rulesets$' { $payload | Add-Member id ($s.NextId++); $s.Rulesets.Add($payload); return ($payload | ConvertTo-Json -Depth 50) }
            '^PUT .*/rulesets/(\d+)$' {
                $id = [int]$Matches[1]; $old = $s.Rulesets | Where-Object id -eq $id
                $s.Rulesets.Remove($old) | Out-Null; $payload | Add-Member id $id; $s.Rulesets.Add($payload)
                return ($payload | ConvertTo-Json -Depth 50)
            }
            '^PATCH repos/[^/]+/[^/]+$' { foreach ($p in $payload.PSObject.Properties) { $s.Repo[$p.Name] = $p.Value }; return '{}' }
            '^PUT .*/immutable-releases$' { $s.Immutable = $true; return '' }
            default { $global:LASTEXITCODE = 1; return "unexpected call $method $path" }
        }
    }

    function Invoke-Protection([hashtable] $extra = @{}) {
        $out = & $script:Script -Repo 'owner/repo' -RulesetDir $script:RulesetDir @extra *>&1
        [pscustomobject]@{ Exit = $LASTEXITCODE; Output = ($out -join "`n") }
    }

    function Get-Desired([string] $name) {
        Get-ChildItem $script:RulesetDir -Filter *.json | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json -Depth 50 } | Where-Object name -eq $name
    }
}

AfterAll {
    Remove-Item function:global:gh -ErrorAction SilentlyContinue
    Remove-Variable FakeGhCalls, FakeGhState -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Set-RepoProtection' {
    It 'refuses a non-admin with exit 3 and makes no write call' {
        New-FakeGitHub -admin $false
        (Invoke-Protection).Exit | Should -Be 3
        $global:FakeGhCalls | Where-Object { $_ -notmatch '^GET ' } | Should -BeNullOrEmpty
    }

    It 'creates missing rulesets, sets squash-only merging and immutable releases, then matches' {
        New-FakeGitHub
        $r = Invoke-Protection
        $r.Exit | Should -Be 0 -Because $r.Output
        ($global:FakeGhCalls | Where-Object { $_ -match '^POST .*/rulesets$' }).Count | Should -Be 2
        $global:FakeGhCalls | Should -Contain 'PATCH repos/owner/repo'
        $global:FakeGhCalls | Should -Contain 'PUT repos/owner/repo/immutable-releases'
        $global:FakeGhState.Repo.allow_merge_commit | Should -BeFalse
        $global:FakeGhState.Repo.allow_rebase_merge | Should -BeFalse
        $global:FakeGhState.Repo.allow_squash_merge | Should -BeTrue
    }

    It 'updates existing rulesets by name instead of duplicating them' {
        $old = Get-Desired 'main-protection'
        $old.enforcement = 'disabled'
        $old | Add-Member id 7
        New-FakeGitHub -rulesets @($old)
        (Invoke-Protection).Exit | Should -Be 0
        $global:FakeGhCalls | Should -Contain 'PUT repos/owner/repo/rulesets/7'
        ($global:FakeGhState.Rulesets | Where-Object name -eq 'main-protection').Count | Should -Be 1
        ($global:FakeGhState.Rulesets | Where-Object name -eq 'main-protection').enforcement | Should -Be 'active'
    }

    It 'never touches unmanaged rulesets' {
        New-FakeGitHub -rulesets @([pscustomobject]@{ id = 3; name = 'someone-elses'; target = 'branch'; enforcement = 'active'; rules = @() })
        (Invoke-Protection).Exit | Should -Be 0
        $global:FakeGhCalls | Where-Object { $_ -match '/rulesets/3$' -and $_ -notmatch '^GET ' } | Should -BeNullOrEmpty
        $global:FakeGhState.Rulesets.name | Should -Contain 'someone-elses'
    }

    It '-WhatIf sends no write request' {
        New-FakeGitHub
        (Invoke-Protection @{ WhatIf = $true }).Exit | Should -Be 0
        $global:FakeGhCalls | Where-Object { $_ -notmatch '^GET ' } | Should -BeNullOrEmpty
    }

    It '-Show exits 4 when rules are missing and 0 after applying' {
        New-FakeGitHub
        $r = Invoke-Protection @{ Show = $true }
        $r.Exit | Should -Be 4
        $r.Output | Should -Match "MISSING\s+ruleset 'main-protection'"
        (Invoke-Protection).Exit | Should -Be 0
        $r = Invoke-Protection @{ Show = $true }
        $r.Exit | Should -Be 0 -Because $r.Output
        $r.Output | Should -Match 'checks: build-test, ext4-check'
    }

    It '-Show reports a changed rule as a difference' {
        New-FakeGitHub
        Invoke-Protection | Out-Null
        ($global:FakeGhState.Rulesets | Where-Object name -eq 'main-protection').bypass_actors[0].bypass_mode = 'always'
        $r = Invoke-Protection @{ Show = $true }
        $r.Exit | Should -Be 4
        $r.Output | Should -Match "DIFFERS\s+ruleset 'main-protection'"
    }
}
