[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$configured = & git -c "safe.directory=$repo" -C $repo config --get core.hooksPath
if ($LASTEXITCODE -gt 1) { throw 'Could not read Git hook configuration.' }
if ($configured) { throw "Existing core.hooksPath '$configured' was preserved. Integrate .githooks/pre-push into that hook explicitly." }
$hook = & git -c "safe.directory=$repo" -C $repo rev-parse --path-format=absolute --git-path hooks/pre-push
if ($LASTEXITCODE -ne 0 -or -not $hook) { throw 'Could not locate the local pre-push hook.' }
$source = [IO.File]::ReadAllText((Join-Path $repo '.githooks/pre-push')).Replace("`r`n", "`n")
if ((Test-Path -LiteralPath $hook) -and [IO.File]::ReadAllText($hook).Replace("`r`n", "`n") -cne $source) {
    throw "Existing pre-push hook '$hook' was preserved. Integrate .githooks/pre-push explicitly."
}
[IO.File]::WriteAllText($hook, $source, [Text.UTF8Encoding]::new($false))
if ($IsLinux -or $IsMacOS) {
    & chmod +x $hook
    if ($LASTEXITCODE -ne 0) { throw 'Could not make the hook executable.' }
}
Write-Host "Installed local reported-issue push gate: $hook"
