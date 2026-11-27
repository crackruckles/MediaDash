# Compatibility entry point. The former script injected into the running DB and
# invoked the global Fix task. Use the production parsers against isolated test
# databases instead; this does not claim live HTTP endpoint coverage.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../../Jellyfin.Plugin.MediaDash.Tests/Jellyfin.Plugin.MediaDash.Tests.csproj'
Write-Host 'Running isolated malformed-row regression tests (no live server mutation).'
& dotnet test $project --configuration Release --filter 'FullyQualifiedName~MalformedDbRowsDoNotCrashTests|FullyQualifiedName~MalformedTimestampTests|FullyQualifiedName~IssueDetailsReaderTests' --verbosity minimal
exit $LASTEXITCODE
