<#
.SYNOPSIS
    End-to-end smoke check for Claustrum (docs/PLAN.md "Verification" item 3): a fresh git repo,
    `backends doctor claude`, then a real `run builder` against the `claude` backend.
.PARAMETER Binary
    Path to an already-built claustrum executable. Falls back to $env:CLAUSTRUM, then to a Release
    build of src/Claustrum/Claustrum.csproj.
#>
[CmdletBinding()]
param(
    [string]$Binary
)

$ErrorActionPreference = "Stop"
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
function Add-Result([string]$Check, [bool]$Ok, [string]$Detail) {
    $results.Add([pscustomobject]@{ Check = $Check; Ok = $Ok; Detail = $Detail })
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("claustrum-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null

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

    $doctorOutput = (& $claustrum backends doctor claude 2>&1 | Out-String)
    $claudeFound = $doctorOutput -match "claude:\s*\r?\n\s*found:\s*True"
    Add-Result "backends doctor claude finds it" $claudeFound ($doctorOutput.Trim() -replace "\r?\n", " | ")

    $runOutput = (& $claustrum run builder --brief "create hello.txt containing hi" --json --cwd $tmp --budget 0.5 --model haiku 2>$null)
    $ok = $false
    $detail = "no output"
    try {
        $result = $runOutput | ConvertFrom-Json
        $statusOk = $result.status -eq "success"
        $hasHello = @($result.changed_files | Where-Object { $_.path -eq "hello.txt" }).Count -gt 0
        $reportOk = $null -ne $result.report
        $ok = $statusOk -and $hasHello -and $reportOk
        $detail = "status=$($result.status) changed_files=$(($result.changed_files | ForEach-Object { $_.path }) -join ',') report_status=$($result.report_status)"
    }
    catch {
        $detail = "could not parse JSON output: $runOutput"
    }
    Add-Result "run builder --json: success + hello.txt + report" $ok $detail
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

$results | Format-Table -AutoSize -Wrap

$failed = @($results | Where-Object { -not $_.Ok })
if ($failed.Count -gt 0) {
    Write-Error "$($failed.Count) smoke check(s) failed."
    exit 1
}

Write-Host "All smoke checks passed."
exit 0
