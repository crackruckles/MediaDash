<#
.SYNOPSIS
  Builds MediaDash from this repo and deploys it to the MediaDash test hosts.
.DESCRIPTION
  Publishes the plugin, writes a Jellyfin meta.json (autoUpdate off, so Jellyfin's updater can
  never swap the dev build for the public release), copies it into each host's plugins folder over
  SSH, restarts Jellyfin, and waits until the server reports MediaDash as Active.
.EXAMPLE
  ./tools/testenv/deploy-plugin.ps1                  # all hosts
  ./tools/testenv/deploy-plugin.ps1 -Hosts linux     # one host
  ./tools/testenv/deploy-plugin.ps1 -SkipBuild       # redeploy the last build
#>
param(
    [ValidateSet('linux', 'docker', 'windows')]
    [string[]]$Hosts = @('linux', 'docker', 'windows'),
    [switch]$SkipBuild,
    [switch]$BuildOnly      # build + meta.json only, deploy nowhere
)
# Checked here rather than with #Requires: under Windows PowerShell 5.1 a #Requires failure still
# exits 0, which an agent would read as success.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    [Console]::Error.WriteLine("testenv: this script needs PowerShell 7. Run it with: pwsh -File $($MyInvocation.MyCommand.Path)")
    exit 2
}
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'testenv-common.ps1')

$repo    = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$publish = Join-Path $repo 'artifacts\testenv-publish'
$vm = Select-String -Path (Join-Path $repo 'build.yaml') -Pattern '^version:\s*"?([0-9.]+)' -ErrorAction SilentlyContinue
if (-not $vm) { throw "could not read the plugin version from $(Join-Path $repo 'build.yaml')" }
$version = $vm.Matches[0].Groups[1].Value
$folder  = "MediaDash_$version"

if (-not $SkipBuild) {
    Write-Step "building MediaDash $version"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw (New-EnvError 'dotnet is not on PATH') }
    $buildLog = & dotnet publish (Join-Path $repo 'Jellyfin.Plugin.MediaDash\Jellyfin.Plugin.MediaDash.csproj') `
        -c Release -o $publish --nologo -v q "-p:Version=$version" 2>&1
    if ($LASTEXITCODE -ne 0) {
        # A compile error in the working tree is a code problem, not an environment problem.
        $errs = @($buildLog | ForEach-Object { "$_" } | Where-Object { $_ -match ': error ' } | Select-Object -Unique -First 15)
        if (-not $errs) { $errs = @($buildLog | ForEach-Object { "$_" } | Select-Object -Last 15) }
        throw "BUILD FAILED (dotnet publish exit $LASTEXITCODE):`n  $($errs -join "`n  ")"
    }
    $meta = [ordered]@{
        category = 'General'; changelog = 'testenv dev build'; description = 'MediaDash (testenv dev build)'
        guid = $script:PluginGuid; name = 'MediaDash'; overview = ''; owner = 'crackruckles'
        targetAbi = '10.11.0.0'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); version = $version
        status = 'Active'; autoUpdate = $false; imagePath = ''; assemblies = @()
    }
    $meta | ConvertTo-Json | Set-Content -Encoding utf8NoBOM (Join-Path $publish 'meta.json')
}
if (-not (Test-Path (Join-Path $publish 'Jellyfin.Plugin.MediaDash.dll'))) { throw "no build at $publish - run without -SkipBuild" }
if ($BuildOnly) { Write-Host "  built $folder -> $publish"; return }

$tar = Join-Path $env:TEMP 'mediadash-testenv.tar'
if (Test-Path $tar) { Remove-Item $tar -Force }
tar.exe -cf $tar -C $publish . ; if ($LASTEXITCODE -ne 0) { throw (New-EnvError "tar could not package $publish") }

function Copy-ToHost([string]$h, [string]$dest) {
    for ($i = 0; $i -lt 3; $i++) {
        $out = & scp -q -o BatchMode=yes -o ConnectTimeout=10 $tar "$($script:Targets[$h].Ssh):$dest" 2>&1
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep (5 * ($i + 1))
    }
    throw (New-EnvError "could not copy the build to $h ($dest): $(Get-LastLines (($out | ForEach-Object { "$_" }) -join "`n") 2)")
}

foreach ($h in $Hosts) {
    $t = $script:Targets[$h]
    Write-Step "deploying $folder to $h"
    if ($h -eq 'windows') {
        Copy-ToHost $h 'C:/mdtest/mediadash.tar'
        Invoke-Remote $h @"
Stop-Service JellyfinServer -Force
Get-ChildItem '$($t.PluginDir)' -Directory -Filter 'MediaDash_*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
New-Item -ItemType Directory -Force '$($t.PluginDir)\$folder' | Out-Null
tar.exe -xf C:\mdtest\mediadash.tar -C '$($t.PluginDir)\$folder'
icacls '$($t.PluginDir)\$folder' /grant '*S-1-5-20:(OI)(CI)F' /T /Q | Out-Null   # NETWORK SERVICE: the installer's default service account
Remove-Item C:\mdtest\mediadash.tar
Start-Service JellyfinServer
"@
    } else {
        Copy-ToHost $h '/tmp/mediadash.tar'
        $restart = if ($h -eq 'docker') { 'docker restart jellyfin >/dev/null' } else { 'systemctl restart jellyfin' }
        $owner   = if ($h -eq 'linux') { "chown -R jellyfin:jellyfin '$($t.PluginDir)/$folder'" } else { 'true' }
        Invoke-Remote $h "set -e; rm -rf '$($t.PluginDir)'/MediaDash_*; mkdir -p '$($t.PluginDir)/$folder'; tar -xf /tmp/mediadash.tar -C '$($t.PluginDir)/$folder'; rm /tmp/mediadash.tar; $owner; $restart"
    }
}

foreach ($h in $Hosts) {
    $status = Wait-PluginActive $h
    Write-Host ("  {0,-8} MediaDash {1}" -f $h, $status)
    if ($status -notlike 'Active*') {
        # The plugin failing to load is a MediaDash problem; include what Jellyfin said about it.
        throw "MediaDash did not load on $h (status: $status). Jellyfin log:`n$(Get-JfPluginLogTail $h)"
    }
}
Remove-Item $tar -Force -ErrorAction SilentlyContinue
