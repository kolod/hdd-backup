BeforeAll {
    $script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
    $script:RulesetDir = Join-Path $script:RepoRoot '.github\rulesets'
    $script:Main = Get-Content (Join-Path $script:RulesetDir 'main.json') -Raw | ConvertFrom-Json
    $script:Tags = Get-Content (Join-Path $script:RulesetDir 'release-tags.json') -Raw | ConvertFrom-Json

    function Get-Rule($ruleset, [string] $type) { $ruleset.rules | Where-Object type -eq $type }
}

Describe 'Ruleset files' {
    It 'every file parses and has a unique name' {
        $names = Get-ChildItem $script:RulesetDir -Filter *.json | ForEach-Object { (Get-Content $_.FullName -Raw | ConvertFrom-Json).name }
        $names.Count | Should -BeGreaterOrEqual 2
        ($names | Sort-Object -Unique).Count | Should -Be $names.Count
    }

    It 'are active' {
        $script:Main.enforcement | Should -Be 'active'
        $script:Tags.enforcement | Should -Be 'active'
    }
}

Describe 'main-protection (FR-003a, b, e)' {
    It 'targets the default branch' {
        $script:Main.target | Should -Be 'branch'
        $script:Main.conditions.ref_name.include | Should -Be @('~DEFAULT_BRANCH')
    }

    It 'blocks deletion and force pushes and requires linear history' {
        foreach ($t in 'deletion', 'non_fast_forward', 'required_linear_history') {
            Get-Rule $script:Main $t | Should -Not -BeNullOrEmpty -Because "rule $t"
        }
    }

    It 'requires a pull request with no approvals, squash only' {
        $pr = (Get-Rule $script:Main 'pull_request').parameters
        $pr.required_approving_review_count | Should -Be 0
        $pr.allowed_merge_methods | Should -Be @('squash')
    }

    It 'requires exactly the CI job names as status checks (renamed-check guard)' {
        $required = (Get-Rule $script:Main 'required_status_checks').parameters.required_status_checks.context | Sort-Object
        $ci = Get-Content (Join-Path $script:RepoRoot '.github\workflows\ci.yml') -Raw
        $jobNames = [regex]::Matches($ci, '(?m)^\s{4}name:\s*(\S+)\s*$') | ForEach-Object { $_.Groups[1].Value } | Sort-Object
        $required | Should -Be @('build-test', 'ext4-check')
        $jobNames | Should -Be $required
    }

    It 'lets the owner bypass only through a pull request (never by a plain push)' {
        $script:Main.bypass_actors.Count | Should -Be 1
        $script:Main.bypass_actors[0].actor_type | Should -Be 'RepositoryRole'
        $script:Main.bypass_actors[0].actor_id | Should -Be 5
        $script:Main.bypass_actors[0].bypass_mode | Should -Be 'pull_request'
    }
}

Describe 'release-tags (FR-003c)' {
    It 'targets v* tags' {
        $script:Tags.target | Should -Be 'tag'
        $script:Tags.conditions.ref_name.include | Should -Be @('refs/tags/v*')
    }

    It 'restricts creation, update, deletion and force pushes' {
        foreach ($t in 'creation', 'update', 'deletion', 'non_fast_forward') {
            Get-Rule $script:Tags $t | Should -Not -BeNullOrEmpty -Because "rule $t"
        }
    }

    It 'only the owner (repository admin) bypasses' {
        $script:Tags.bypass_actors.Count | Should -Be 1
        $script:Tags.bypass_actors[0].actor_type | Should -Be 'RepositoryRole'
        $script:Tags.bypass_actors[0].actor_id | Should -Be 5
    }
}
