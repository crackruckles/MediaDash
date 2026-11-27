#requires -Version 7.0
# Offline harness gate. No authentication, deployment, or live media changes.
[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "../artifacts/harness-tests/$([guid]::NewGuid().ToString('N'))"),
    [switch]$SelfTestsOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath $OutputDir) { throw 'OutputDir must be new so evidence cannot be reused.' }
$null = New-Item -ItemType Directory -Path $OutputDir
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path
$results = [Collections.Generic.List[object]]::new()
function Invoke-Suite {
    param([string]$SuiteName, [string]$Executable, [string[]]$Arguments)
    $log = Join-Path $OutputDir "$SuiteName.log"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $code = 1
    try {
        $command = (Get-Command $Executable -ErrorAction Stop).Source
        # Run each suite in a child process. A later success must not erase an
        # earlier native exit code, and a suite must not terminate this runner.
        & $command @Arguments *> $log
        $code = $LASTEXITCODE
    } catch {
        $_ | Out-String | Add-Content -LiteralPath $log
    }
    $results.Add([pscustomobject]@{ suite=$SuiteName; exitCode=$code; seconds=[Math]::Round($timer.Elapsed.TotalSeconds,2); log=$log })
    Write-Host "$SuiteName : exit $code ($log)"
}
Push-Location $repo
try {
    foreach ($suite in @('tools/comprehensive-test/self-tests.ps1','tools/comprehensive-test/task-wait-self-tests.ps1','tools/repair-test/validator-self-tests.ps1','tools/release-self-tests.ps1')) {
        $suiteName = ($suite -replace '^tools/','' -replace '/','-' -replace '\.ps1$','')
        Invoke-Suite $suiteName 'pwsh' @('-NoProfile','-NonInteractive','-File',$suite)
    }
    Invoke-Suite 'dashboard' 'node' @('--test','tools/comprehensive-test/ui-lifecycle-self-tests.js','tools/comprehensive-test/ui-regression-self-tests.js','tools/comprehensive-test/ui-issue-fixes-self-tests.js')
    Invoke-Suite 'python' 'python' @('-B','-m','unittest','discover','-s','tools/repair-test','-p','test_*.py')
    Invoke-Suite 'format-fixture-contracts' 'python' @('-B','tools/repair-test/run_formats.py','--self-test')
    Invoke-Suite 'powershell-syntax' 'pwsh' @('-NoProfile','-NonInteractive','-File','tools/check-harness-syntax.ps1')
    $bashExecutable = 'bash'
    if ([IO.Path]::DirectorySeparatorChar -eq '\') {
        $gitRoot = Split-Path -Parent (Split-Path -Parent (Get-Command git -ErrorAction Stop).Source)
        $gitBash = Join-Path $gitRoot 'bin/bash.exe'
        if (Test-Path -LiteralPath $gitBash) { $bashExecutable = $gitBash }
    }
    foreach ($shellScript in @('tools/make-fixtures.sh','tools/integration-test.sh','tools/audit-migration/migrate.sh')) {
        Invoke-Suite ([IO.Path]::GetFileNameWithoutExtension($shellScript) + '-syntax') $bashExecutable @('-n',$shellScript)
    }
    if (-not $SelfTestsOnly) {
        Invoke-Suite 'reported-issues' 'python' @('-B','tools/reported_issue_gate.py','--output',(Join-Path $OutputDir 'reported-issues'))
    }
} finally { Pop-Location }
$failed = @($results | Where-Object exitCode -NE 0)
$summary = [pscustomobject]@{ scope= $(if ($SelfTestsOnly) {'offline self-tests'} else {'offline self-tests and .NET'}); passed=$results.Count-$failed.Count; failed=$failed.Count; liveIntegration='NOT RUN'; suites=@($results) }
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDir 'summary.json') -Encoding utf8
if ($failed.Count) { exit 1 }
exit 0
