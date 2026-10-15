BeforeAll {
    $script:Script = Join-Path $PSScriptRoot '..\release\New-ReleaseBody.ps1'
    $script:Notes = Join-Path $PSScriptRoot 'data\release-notes.md'

    function New-Files([string[]] $names) {
        $dir = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $dir | Out-Null
        foreach ($n in $names) { Set-Content (Join-Path $dir $n) "content of $n" -NoNewline }
        $dir
    }

    function Invoke-Body([string] $filesDir) {
        $outDir = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $out = & pwsh -NoProfile -File $script:Script -Version '1.1.0' -FilesDir $filesDir -OutDir $outDir -NotesPath $script:Notes 2>&1
        [pscustomobject]@{ Exit = $LASTEXITCODE; OutDir = $outDir; Output = ($out -join "`n") }
    }
}

Describe 'New-ReleaseBody' {
    BeforeAll {
        $script:Files = New-Files @('HddBackup.exe.config', 'HddBackup.exe')
        $script:Result = Invoke-Body $script:Files
        $script:Sums = Join-Path $script:Result.OutDir 'SHA256SUMS'
        $script:Body = Join-Path $script:Result.OutDir 'body.md'
    }

    It 'succeeds' {
        $script:Result.Exit | Should -Be 0
    }

    It 'writes SHA256SUMS in sha256sum format, sorted, lowercase, LF endings' {
        $raw = [IO.File]::ReadAllText($script:Sums)
        $raw | Should -Not -Match "`r"
        $lines = $raw.TrimEnd("`n") -split "`n"
        $lines.Count | Should -Be 2
        $lines[0] | Should -Match '^[0-9a-f]{64}  HddBackup\.exe$'
        $lines[1] | Should -Match '^[0-9a-f]{64}  HddBackup\.exe\.config$'
        foreach ($line in $lines) {
            $hash, $name = $line -split '  ', 2
            $hash | Should -Be (Get-FileHash (Join-Path $script:Files $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    It 'writes the body in the order banner, notes, checksums' {
        $text = Get-Content $script:Body -Raw
        $banner = $text.IndexOf('**Hardware checklist pending.**')
        $notes = $text.IndexOf('Stable notes for 1.1.0.')
        $sums = $text.IndexOf('### Checksums')
        $banner | Should -BeGreaterOrEqual 0
        $notes | Should -BeGreaterThan $banner
        $sums | Should -BeGreaterThan $notes
    }

    It 'uses the banner text from the contract' {
        (Get-Content $script:Body -Raw) | Should -Match ([regex]::Escape('> **Hardware checklist pending.** Do not publish this draft until docs/manual-hardware-checklist.md has passed on Windows 7 SP1 and Windows 11 and the results are recorded below.'))
    }

    It 'repeats the executable checksum in the body' {
        $exeLine = (Get-Content $script:Sums)[0]
        (Get-Content $script:Body -Raw) | Should -Match ([regex]::Escape($exeLine))
    }

    It 'fails when HddBackup.exe is missing' {
        $r = Invoke-Body (New-Files @('HddBackup.exe.config'))
        $r.Exit | Should -Be 1
        $r.Output | Should -Match 'HddBackup\.exe'
    }

    It 'fails when the notes section is missing' {
        $outDir = Join-Path $TestDrive 'nosection'
        & pwsh -NoProfile -File $script:Script -Version '9.9.9' -FilesDir $script:Files -OutDir $outDir -NotesPath $script:Notes 2>$null
        $LASTEXITCODE | Should -Be 1
    }
}
