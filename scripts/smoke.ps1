<#
.SYNOPSIS
    End-to-end smoke check for Claustrum (docs/PLAN.md "Verification" item 3) on a throwaway git
    repo: `backends doctor` for every registered backend, `claustrum init` plus an idempotent rerun,
    a real `run builder` per installed backend the builder role supports, and two concurrent
    max_parallel builders followed by `jobs clean`. A backend that is not installed SKIPs instead of
    failing, so the one script works on a machine with any subset of them; only a FAIL exits nonzero.
.PARAMETER Binary
    Path to an already-built claustrum executable. Falls back to $env:CLAUSTRUM, then to a Release
    build of src/Claustrum/Claustrum.csproj.
#>
[CmdletBinding()]
param(
    [string]$Binary
)

$ErrorActionPreference = "Stop"

# A nonzero native exit is data here (a backend that fails is a FAIL row, not a crash), so keep
# PowerShell 7.4+ from turning one into a terminating error under $ErrorActionPreference = "Stop".
$PSNativeCommandUseErrorActionPreference = $false

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-ClaustrumBinary {
    if ($Binary) { return (Resolve-Path $Binary).Path }
    if ($env:CLAUSTRUM) { return (Resolve-Path $env:CLAUSTRUM).Path }

    Write-Host "Building claustrum (Release)..."
    dotnet build (Join-Path $repoRoot "src/Claustrum/Claustrum.csproj") -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

    $exe = Get-ChildItem -Path (Join-Path $repoRoot "src/Claustrum/bin/Release") -Recurse -Filter "claustrum.exe" |
        Select-Object -First 1
    if (-not $exe) { throw "could not find a built claustrum.exe under src/Claustrum/bin/Release" }
    return $exe.FullName
}

$claustrum = Resolve-ClaustrumBinary
Write-Host "Using binary: $claustrum"

$results = [System.Collections.Generic.List[pscustomobject]]::new()
function Add-Result([string]$Check, [string]$Status, [string]$Detail) {
    $results.Add([pscustomobject]@{ Check = $Check; Status = $Status; Detail = $Detail })
}

# Multi-line output squeezed onto the table's Detail column (the api backend's `version:` alone is
# four lines of curl banner). Out-String yields $null for a command that printed nothing, so every
# captured output is cast to [string] before anything calls a method on it.
function Join-Lines([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return "" }
    return ($Text.Trim() -replace "\r?\n", ";")
}

$foundBackends = @{}
$script:backendNames = @()
$script:parallelBranches = @()
$script:smokeCastCreated = $false

# Check 4's row names, shared between the checks themselves and the SKIP branches that stand in for
# them, so a renamed row cannot drift between the two.
$castRow = "cast create smoke: builder claude:sonnet, max_parallel 2"
$parallelRow = "2 parallel builders: distinct worktrees + branches"
$cleanRow = "jobs clean: worktrees removed, branches kept"

# The parallel runs' stdout and the cast answers file live beside the repo, not in it: an untracked
# file inside the repo turns up in the next run's changed_files.
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("claustrum-smoke-" + [guid]::NewGuid().ToString("N"))
$tmp = Join-Path $work "repo"
$outputs = Join-Path $work "out"
New-Item -ItemType Directory -Path $tmp | Out-Null
New-Item -ItemType Directory -Path $outputs | Out-Null

# 1. `backends doctor <name>` for every name `backends list` prints. found: False is a SKIP, not a
# failure — the whole point of the loop is that one script runs on any subset of the five.
function Invoke-DoctorCheck([string]$Name) {
    $output = [string](& $claustrum backends doctor $Name 2>&1 | Out-String)
    $exitCode = $LASTEXITCODE
    $found = [regex]::Match($output, "(?m)^\s+found:\s+(\S+)").Groups[1].Value
    $path = [regex]::Match($output, "(?m)^\s+path:\s+(.*)$").Groups[1].Value.Trim()
    $problems = (([regex]::Matches($output, "(?m)^\s+problem:\s+(.*)$") |
        ForEach-Object { $_.Groups[1].Value.Trim() }) -join ";")

    $detail = "path=$path"
    if ($problems) { $detail = "$detail problems=$problems" }

    if ($exitCode -ne 0) {
        Add-Result "backends doctor $Name finds it" "FAIL" "exit=$exitCode $problems"
    }
    elseif ($found -eq "True") {
        $foundBackends[$Name] = $true
        Add-Result "backends doctor $Name finds it" "OK" $detail
    }
    else {
        Add-Result "backends doctor $Name finds it" "SKIP" "not installed: $problems"
    }
}

function Invoke-AllDoctorChecks {
    $script:backendNames = @(& $claustrum backends list | Where-Object { $_ -match "\S" } | ForEach-Object { $_.Trim() })
    if ($script:backendNames.Count -eq 0) {
        Add-Result "backends list yields the registered backends" "FAIL" "no names on stdout"
        return
    }

    Add-Result "backends list yields the registered backends" "OK" ($script:backendNames -join " ")
    foreach ($name in $script:backendNames) { Invoke-DoctorCheck $name }
}

# 2. `claustrum init` has no --cwd: it scaffolds Environment.CurrentDirectory (Cli/InitCommand.cs).
function Invoke-ClaustrumInit {
    Push-Location $tmp
    try {
        $output = [string](& $claustrum init 2>&1 | Out-String)
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    finally {
        Pop-Location
    }
}

function Invoke-InitCheck {
    $init = Invoke-ClaustrumInit
    $missing = [System.Collections.Generic.List[string]]::new()

    if (-not (Test-Path (Join-Path $tmp "claustrum.json"))) { $missing.Add("claustrum.json") }
    if (-not (Test-Path (Join-Path $tmp ".claustrum/casts"))) { $missing.Add(".claustrum/casts/") }
    if (-not (Test-Path (Join-Path $tmp ".claude/agents/builder.md"))) { $missing.Add(".claude/agents/builder.md") }

    $gitignore = Join-Path $tmp ".gitignore"
    $gitignoreText = if (Test-Path $gitignore) { [string](Get-Content -Raw $gitignore) } else { "" }
    foreach ($entry in ".claustrum/worktrees/", ".claustrum/locks/", ".claustrum/briefs/") {
        if (-not $gitignoreText.Contains($entry)) { $missing.Add(".gitignore lacks $entry") }
    }

    if ($init.ExitCode -eq 0 -and $missing.Count -eq 0) {
        Add-Result "claustrum init scaffolds a fresh repo" "OK" (Join-Lines $init.Output)
    }
    else {
        $names = if ($missing.Count -gt 0) { $missing -join ", " } else { "none" }
        Add-Result "claustrum init scaffolds a fresh repo" "FAIL" "exit=$($init.ExitCode) missing: $names"
    }
}

function Get-RepoSnapshot {
    $gitPrefix = Join-Path $tmp ".git"
    $files = Get-ChildItem -Path $tmp -Recurse -File -Force |
        Where-Object { -not $_.FullName.StartsWith($gitPrefix, [System.StringComparison]::Ordinal) } |
        Sort-Object FullName
    return (($files | ForEach-Object { "$($_.FullName) $((Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash)" }) -join "`n")
}

function Invoke-InitRerunCheck {
    $before = Get-RepoSnapshot
    $init = Invoke-ClaustrumInit
    $after = Get-RepoSnapshot

    $unchanged = if ($init.Output.Contains("already present, left unchanged")) { "yes" } else { "no" }
    $upToDate = if ($init.Output.Contains("already up to date")) { "yes" } else { "no" }
    $snapshot = if ($before -eq $after) { "identical" } else { "differs" }

    $detail = "left_unchanged=$unchanged up_to_date=$upToDate snapshot=$snapshot"
    if ($init.ExitCode -eq 0 -and $unchanged -eq "yes" -and $upToDate -eq "yes" -and $snapshot -eq "identical") {
        Add-Result "claustrum init is idempotent on rerun" "OK" $detail
    }
    else {
        Add-Result "claustrum init is idempotent on rerun" "FAIL" "exit=$($init.ExitCode) $detail"
    }
}

# 3. One paid `run builder` per installed backend. The row name keeps M1's wording for claude, whose
# flags are also unchanged, so a regression there reads the same as it always did.
function Get-RunRow([string]$Name) {
    if ($Name -eq "claude") { return "run builder --json: success + hello.txt + report" }
    return "run builder --backend ${Name}: success + hello.txt + report"
}

function Invoke-BuilderRun([string]$Row, [string]$ExpectedFile, [string[]]$Arguments) {
    # An earlier backend's hello.txt would let a backend that did nothing still pass this row.
    Remove-Item -Path (Join-Path $tmp $ExpectedFile) -Force -ErrorAction SilentlyContinue

    $output = (& $claustrum run builder @Arguments --json --cwd $tmp 2>$null)
    try {
        $result = $output | ConvertFrom-Json
        $status = [string]$result.status
        $hasFile = @($result.changed_files | Where-Object { $_.path -eq $ExpectedFile }).Count -gt 0
        $reportOk = $null -ne $result.report
        $detail = "status=$status $ExpectedFile=$hasFile report=$reportOk"
        $ok = $status -eq "success" -and $hasFile -and $reportOk
        Add-Result $Row $(if ($ok) { "OK" } else { "FAIL" }) $detail
    }
    catch {
        Add-Result $Row "FAIL" "could not parse JSON output: $output"
    }
}

function Invoke-AllRunChecks {
    # The builder role's own harness list (roles/builder/role.json) decides who gets a paid run:
    # `api` is registered and its curl is "found", but it has no tools, is not a builder harness, and
    # `--backend api` without an `api:<provider>:<model>` model spec cannot even resolve a model
    # (measured 2026-09-21: "api backend model must be 'openrouter:<model>'... got 'opus'").
    $harnesses = @()
    $harnessLine = (& $claustrum roles show builder | Where-Object { $_ -match "^harnesses:" } | Select-Object -First 1)
    if ($harnessLine -match "^harnesses:\s*(.*)$") { $harnesses = @($Matches[1] -split ",\s*" | ForEach-Object { $_.Trim() }) }
    if ($harnesses.Count -eq 0) {
        # Without the list every backend would SKIP, quietly dropping every paid row.
        Add-Result "roles show builder lists its harnesses" "FAIL" "no harnesses line"
        return
    }

    foreach ($name in $script:backendNames) {
        $row = Get-RunRow $name
        if (-not $foundBackends.ContainsKey($name)) {
            Add-Result $row "SKIP" "not installed"
            continue
        }
        if ($harnesses -notcontains $name) {
            Add-Result $row "SKIP" "not a builder harness (roles show builder)"
            continue
        }

        if ($name -eq "claude") {
            Invoke-BuilderRun $row "hello.txt" @("--brief", "create hello.txt containing hi", "--budget", "0.5", "--model", "sonnet")
        }
        else {
            Invoke-BuilderRun $row "hello.txt" @("--backend", $name, "--brief", "create hello.txt containing hi", "--budget", "0.5")
        }
    }
}

# 4. max_parallel > 1 (docs/PLAN.md §D4) end to end: a cast with max_parallel 2, two concurrent
# builders that must land on their own worktree and branch, then `jobs clean`.
function Invoke-CastCreateCheck {
    $answers = Join-Path $outputs "smoke-answers.json"

    # Only the questions this cast needs: CastBuilder reads an unanswered role as "not needed"
    # (Casts/CastBuilder.cs), so this file stays correct when the role library gains a role.
    @'
{
  "architect": "host",
  "builder": "claude:sonnet",
  "builder_max_parallel": "2",
  "budget": "2"
}
'@ | Out-File -FilePath $answers -Encoding utf8

    Push-Location $tmp
    try {
        $output = [string](& $claustrum cast create --answers $answers --name smoke 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -eq 0 -and (Test-Path (Join-Path $tmp ".claustrum/casts/smoke.json"))) {
        $script:smokeCastCreated = $true
        Add-Result $castRow "OK" (Join-Lines $output)
    }
    else {
        Add-Result $castRow "FAIL" "exit=$exitCode $(Join-Lines $output)"
    }
}

function Invoke-ParallelRunsCheck {
    # Start-Job, not Start-Process: the job's own `&` call keeps the brief's spaces intact without
    # hand-quoting an -ArgumentList string, and each run's stdout goes straight to its own file.
    $jobs = @(1, 2 | ForEach-Object {
        Start-Job -ScriptBlock {
            param($Exe, $N, $Cwd, $OutFile)
            & $Exe run builder --cast smoke --brief "create parallel-$N.txt containing $N" --json --cwd $Cwd --budget 0.5 2>$null |
                Out-File -FilePath $OutFile -Encoding utf8
        } -ArgumentList $claustrum, $_, $tmp, (Join-Path $outputs "parallel-$_.json")
    })
    $jobs | Wait-Job | Out-Null
    $jobs | Remove-Job

    $statuses = @()
    $worktrees = @()
    $files = @()
    $script:parallelBranches = @()
    foreach ($n in 1, 2) {
        $result = $null
        try { $result = (Get-Content -Raw (Join-Path $outputs "parallel-$n.json")) | ConvertFrom-Json } catch { $result = $null }
        if ($null -ne $result) {
            $statuses += [string]$result.status
            $worktrees += [string]$result.worktree
            $script:parallelBranches += [string]$result.branch
            $files += (@($result.changed_files | Where-Object { $_.path -eq "parallel-$n.txt" }).Count -gt 0)
        }
        else {
            $statuses += ""
            $worktrees += ""
            $script:parallelBranches += ""
            $files += $false
        }
    }

    $detail = "status=$($statuses -join ',') branch=$($script:parallelBranches -join ',') files=$($files -join ',')"
    $ok = (
        $statuses[0] -eq "success" -and $statuses[1] -eq "success" -and
        $worktrees[0] -and $worktrees[1] -and $worktrees[0] -ne $worktrees[1] -and
        $script:parallelBranches[0] -and $script:parallelBranches[1] -and
        $script:parallelBranches[0] -ne $script:parallelBranches[1] -and
        $files[0] -and $files[1]
    )
    Add-Result $parallelRow $(if ($ok) { "OK" } else { "FAIL" }) $detail
}

function Invoke-JobsCleanCheck {
    $output = [string](& $claustrum jobs clean --cwd $tmp 2>&1 | Out-String)
    $exitCode = $LASTEXITCODE

    $worktreesRoot = Join-Path $tmp ".claustrum/worktrees"
    $left = 0
    if (Test-Path $worktreesRoot) { $left = @(Get-ChildItem -Path $worktreesRoot -Force).Count }

    # `jobs clean` removes the working directory only — the branch survives for the architect to
    # rebase from (Cli/JobsCommands.cs, NOTES.md "Worktree isolation for max_parallel builders").
    $branchList = [string](& git -C $tmp branch --list "claustrum/*" | Out-String)
    $branchesKept = $script:parallelBranches.Count -eq 2
    foreach ($branch in $script:parallelBranches) {
        if (-not $branch -or -not $branchList.Contains($branch)) { $branchesKept = $false }
    }

    $detail = "exit=$exitCode worktrees_left=$left branches_kept=$branchesKept ($(Join-Lines $output))"
    if ($exitCode -eq 0 -and $left -eq 0 -and $branchesKept) {
        Add-Result $cleanRow "OK" $detail
    }
    else {
        Add-Result $cleanRow "FAIL" $detail
    }
}

function Invoke-MaxParallelChecks {
    if (-not $foundBackends.ContainsKey("claude")) {
        Add-Result $castRow "SKIP" "claude not installed"
        Add-Result $parallelRow "SKIP" "claude not installed"
        Add-Result $cleanRow "SKIP" "claude not installed"
        return
    }

    Invoke-CastCreateCheck
    if (-not $script:smokeCastCreated) {
        Add-Result $parallelRow "SKIP" "cast smoke was not created"
        Add-Result $cleanRow "SKIP" "cast smoke was not created"
        return
    }

    Invoke-ParallelRunsCheck
    Invoke-JobsCleanCheck
}

try {
    Push-Location $tmp
    try {
        git init -q
        git config user.email "smoke@claustrum.local"
        git config user.name "claustrum-smoke"
        "smoke" | Out-File -Encoding utf8 README.md
        git add -A
        git commit -q -m init
    }
    finally {
        Pop-Location
    }

    Invoke-AllDoctorChecks
    Invoke-InitCheck
    Invoke-InitRerunCheck
    Invoke-AllRunChecks
    Invoke-MaxParallelChecks
}
finally {
    # -Force also clears the read-only attribute git-for-windows puts on loose objects, which a plain
    # recursive delete refuses there (NOTES.md-era finding, tests/Claustrum.Tests/Testing/TempTree.cs).
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

$results | Format-Table -AutoSize -Wrap

$failed = @($results | Where-Object { $_.Status -eq "FAIL" })
if ($failed.Count -gt 0) {
    Write-Error "$($failed.Count) smoke check(s) failed."
    exit 1
}

Write-Host "All smoke checks passed."
exit 0
