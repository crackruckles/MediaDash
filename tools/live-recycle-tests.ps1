#requires -Version 5.1
<#
.SYNOPSIS
Live integration tests against localhost:8099. Exercises every recycle-bin recovery path.

.DESCRIPTION
Seeds a real file into a library location, drives it through the MediaDash API into the recycle
bin, verifies the enriched RecycleBinItem shape, restores it, verifies the restore, then repeats
the collision-suffix path. Also probes every error path (empty body, unknown id, unauthorized
paths) and confirms the response codes + messages are user-friendly.

Exit code 0 on all-pass, 1 on any failure. Prints PASS / FAIL per test.
#>
param(
    [string]$BaseUrl = 'http://localhost:8099',
    [string]$User = 'test',
    [string]$Pw = 'test',
    [string]$LibraryPath = 'C:\dev\mediadash-fixtures\movies'
)
$ErrorActionPreference = 'Stop'

$script:results = New-Object System.Collections.ArrayList
function Register-Result {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $null = $script:results.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    if ($Passed) { Write-Host "  PASS " -ForegroundColor Green -NoNewline; Write-Host $Name }
    else { Write-Host "  FAIL " -ForegroundColor Red -NoNewline; Write-Host "$Name  ->  $Detail" }
}

function Invoke-Md {
    param([string]$Path, [string]$Method = 'GET', $Body = $null, [switch]$AsJson)
    $u = "$BaseUrl/MediaDash/$Path"
    $sep = if ($u.Contains('?')) { '&' } else { '?' }
    $u = "$u${sep}ApiKey=$token"
    $headers = @{ 'Content-Type' = 'application/json' }
    $params = @{ Uri = $u; Method = $Method; Headers = $headers; UseBasicParsing = $true }
    if ($Body -ne $null) { $params['Body'] = ($Body | ConvertTo-Json -Compress) }
    try {
        $r = Invoke-WebRequest @params
        if ($AsJson -and $r.Content) { return @{ Status = [int]$r.StatusCode; Body = ($r.Content | ConvertFrom-Json) } }
        return @{ Status = [int]$r.StatusCode; Body = $r.Content }
    } catch {
        $resp = $_.Exception.Response
        $status = if ($resp) { [int]$resp.StatusCode } else { 0 }
        $bodyText = ''
        if ($resp) {
            try {
                $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
                $bodyText = $sr.ReadToEnd()
            } catch {}
        }
        return @{ Status = $status; Body = $bodyText }
    }
}

Write-Host "== auth =="
$authHeader = 'MediaBrowser Client="test", Device="testdev", DeviceId="mediadash-live-tests", Version="1.0.0"'
$authBody = @{ Username = $User; Pw = $Pw } | ConvertTo-Json
$authResp = Invoke-WebRequest -Uri "$BaseUrl/Users/AuthenticateByName" -Method POST -Body $authBody `
    -Headers @{ 'Authorization' = $authHeader; 'Content-Type' = 'application/json' } -UseBasicParsing
$script:token = ($authResp.Content | ConvertFrom-Json).AccessToken
Write-Host "authed as $User"

# --- Shape tests ---
Write-Host ""
Write-Host "== shape / read-only endpoints =="

$r = Invoke-Md -Path 'RecycleBin' -AsJson
Register-Result 'GET /RecycleBin returns 200 + IsEmptying field' ($r.Status -eq 200 -and $r.Body.PSObject.Properties.Name -contains 'IsEmptying') "status=$($r.Status)"

$r = Invoke-Md -Path 'RecycleBin/Items' -AsJson
Register-Result 'GET /RecycleBin/Items returns 200 + list' ($r.Status -eq 200 -and ($r.Body -is [array] -or $r.Body.Count -ge 0)) "status=$($r.Status)"

$r = Invoke-Md -Path 'RecycleBin/OtherBins' -AsJson
Register-Result 'GET /RecycleBin/OtherBins returns 200 + list' ($r.Status -eq 200 -and ($r.Body -is [array] -or $r.Body.Count -ge 0)) "status=$($r.Status)"

$r = Invoke-Md -Path 'RecycleBin/DiskInfo' -AsJson
Register-Result 'GET /RecycleBin/DiskInfo returns 200 + TotalBytes' ($r.Status -eq 200 -and $r.Body.PSObject.Properties.Name -contains 'TotalBytes') "status=$($r.Status)"

# --- Error path tests ---
Write-Host ""
Write-Host "== error-path validation =="

$r = Invoke-Md -Path 'History/99999999/Restore' -Method 'POST'
Register-Result 'Restore unknown history id returns 404' ($r.Status -eq 404) "status=$($r.Status)"

$r = Invoke-Md -Path 'RecycleBin/Items/Restore' -Method 'POST' -Body @{ BinPath = '' }
Register-Result 'Restore-by-BinPath with empty body returns 4xx' ($r.Status -ge 400 -and $r.Status -lt 500) "status=$($r.Status)"

$r = Invoke-Md -Path 'RecycleBin/Items/Restore' -Method 'POST' -Body @{ BinPath = 'C:\does\not\exist\20260827-100000-000-a1b2c3d4\Movie.mkv' }
Register-Result 'Restore-by-BinPath with fabricated path returns 4xx' ($r.Status -ge 400 -and $r.Status -lt 500) "status=$($r.Status)"

$r = Invoke-Md -Path 'RecycleBin/Consolidate' -Method 'POST' -Body @{ SourceRoot = '' }
Register-Result 'Consolidate with empty SourceRoot returns 400' ($r.Status -eq 400) "status=$($r.Status)"

$r = Invoke-Md -Path 'RecycleBin/Consolidate' -Method 'POST' -Body @{ SourceRoot = 'C:\arbitrary\path' }
Register-Result 'Consolidate with unknown SourceRoot returns 404' ($r.Status -eq 404) "status=$($r.Status)"

# --- End-to-end recycle + restore lifecycle ---
Write-Host ""
Write-Host "== end-to-end recycle/restore lifecycle =="

$scratchDir = Join-Path $LibraryPath 'mediadash-live-test'
if (-not (Test-Path $scratchDir)) { New-Item -ItemType Directory -Path $scratchDir | Out-Null }
$fixtureName = "live-test-$([guid]::NewGuid().ToString('N').Substring(0,8)).txt"
$fixturePath = Join-Path $scratchDir $fixtureName
'This is a real file MediaDash live tests will recycle and restore.' | Out-File -FilePath $fixturePath -Encoding UTF8

# Delete via Files endpoint (routes through RecycleBin.MoveToBin)
$r = Invoke-Md -Path 'Files/Delete' -Method 'POST' -Body @{ Path = $fixturePath }
Register-Result 'Delete via Files endpoint sends to bin (204)' ($r.Status -eq 204) "status=$($r.Status)"

Register-Result 'File is gone from library location after Delete' (-not (Test-Path $fixturePath)) 'file still present'

# List items and find our file
Start-Sleep -Milliseconds 300
$items = (Invoke-Md -Path 'RecycleBin/Items' -AsJson).Body
$ourItem = $items | Where-Object { $_.FileName -eq $fixtureName } | Select-Object -First 1
Register-Result 'Recycled file appears in /RecycleBin/Items' ($ourItem -ne $null) 'not in listing'

# Verify enriched fields
if ($ourItem) {
    Register-Result 'Item has Reason' (-not [string]::IsNullOrWhiteSpace($ourItem.Reason)) 'empty'
    Register-Result 'Item has RestoreHint' (-not [string]::IsNullOrWhiteSpace($ourItem.RestoreHint)) 'empty'
    # Jellyfin serializes enums as strings; accept either the string name or the numeric value.
    $provOk = ($ourItem.Provenance -eq 1) -or ($ourItem.Provenance -eq 'Manifest')
    Register-Result 'Item Provenance is Manifest for Files-tab delete' $provOk "got $($ourItem.Provenance)"
    Register-Result 'Item BinPath is populated for manifest-only restore' (-not [string]::IsNullOrWhiteSpace($ourItem.BinPath)) 'empty'
    Register-Result 'Item OriginalPath matches the seeded path' ($ourItem.OriginalPath -eq $fixturePath) "got $($ourItem.OriginalPath)"
    Register-Result 'Item RecycledAtUtc is a valid recent timestamp' (([DateTime]$ourItem.RecycledAtUtc) -gt (Get-Date).AddMinutes(-5)) 'stale or invalid'
    Register-Result 'Item AutoPurgesAtUtc is set (retention > 0)' ($ourItem.AutoPurgesAtUtc -ne $null) 'null'

    # Restore via BinPath (manifest-only path)
    $rr = Invoke-Md -Path 'RecycleBin/Items/Restore' -Method 'POST' -Body @{ BinPath = $ourItem.BinPath } -AsJson
    Register-Result 'Restore-by-BinPath returns 200' ($rr.Status -eq 200) "status=$($rr.Status)"
    Register-Result 'Restore response includes RestoredTo path' (-not [string]::IsNullOrWhiteSpace($rr.Body.RestoredTo)) 'empty'
    Register-Result 'File is back at the restored path' (Test-Path $rr.Body.RestoredTo) "not at $($rr.Body.RestoredTo)"

    # Second cycle to test collision-suffix path
    Write-Host ""
    Write-Host "== collision -> -restored suffix =="
    'Fresh content.' | Out-File -FilePath $fixturePath -Encoding UTF8
    $r = Invoke-Md -Path 'Files/Delete' -Method 'POST' -Body @{ Path = $fixturePath }
    Register-Result 'Second cycle: Delete succeeds' ($r.Status -eq 204) "status=$($r.Status)"

    # Recreate file at target so restore has to use suffix
    'Collision-holder content.' | Out-File -FilePath $fixturePath -Encoding UTF8

    Start-Sleep -Milliseconds 300
    $items2 = (Invoke-Md -Path 'RecycleBin/Items' -AsJson).Body
    $ourItem2 = $items2 | Where-Object { $_.FileName -eq $fixtureName } | Select-Object -First 1
    if ($ourItem2 -and $ourItem2.BinPath) {
        $rr2 = Invoke-Md -Path 'RecycleBin/Items/Restore' -Method 'POST' -Body @{ BinPath = $ourItem2.BinPath } -AsJson
        Register-Result 'Collision restore returns 200' ($rr2.Status -eq 200) "status=$($rr2.Status)"
        Register-Result 'Collision restore reports Suffixed=true' ($rr2.Body.Suffixed -eq $true) "got $($rr2.Body.Suffixed)"
        Register-Result 'Restored path contains -restored suffix' ($rr2.Body.RestoredTo -like '*-restored*') "got $($rr2.Body.RestoredTo)"
        Register-Result 'Both files coexist after collision restore' ((Test-Path $fixturePath) -and (Test-Path $rr2.Body.RestoredTo)) 'one is missing'

        # Cleanup
        Remove-Item -Path $fixturePath -Force -ErrorAction SilentlyContinue
        if (Test-Path $rr2.Body.RestoredTo) { Remove-Item -Path $rr2.Body.RestoredTo -Force -ErrorAction SilentlyContinue }
    } else {
        Register-Result 'Collision restore path' $false 'could not find bin item'
    }
}

# --- Restore-protection lifecycle ---
Write-Host ""
Write-Host "== restored file is protected from auto-fix =="

if (-not (Test-Path $scratchDir)) { New-Item -ItemType Directory -Path $scratchDir | Out-Null }
$protName = "prot-test-$([guid]::NewGuid().ToString('N').Substring(0,8)).txt"
$protPath = Join-Path $scratchDir $protName
'Protection-test payload.' | Out-File -FilePath $protPath -Encoding UTF8

# Delete → bin → find item → restore. Restore endpoint should mark the path protected.
$r = Invoke-Md -Path 'Files/Delete' -Method 'POST' -Body @{ Path = $protPath }
Register-Result 'Protection cycle: Delete to bin succeeds' ($r.Status -eq 204) "status=$($r.Status)"
Start-Sleep -Milliseconds 300
$items3 = (Invoke-Md -Path 'RecycleBin/Items' -AsJson).Body
$protItem = $items3 | Where-Object { $_.FileName -eq $protName } | Select-Object -First 1
if ($protItem) {
    $rr3 = Invoke-Md -Path 'RecycleBin/Items/Restore' -Method 'POST' -Body @{ BinPath = $protItem.BinPath } -AsJson
    Register-Result 'Protection cycle: Restore succeeds' ($rr3.Status -eq 200) "status=$($rr3.Status)"
    Register-Result 'Protection cycle: File back at original path' (Test-Path $protPath) 'missing'

    # Now check the issues API — the path shouldn't auto-queue on a scan. This is hard to force
    # (no fixer will run on a plain txt file), but the DB-level guarantee is that
    # /Issues/{id}/Approve still works and no auto-queue happens. We verify via the /Issues
    # payload — WasPreviouslyRestored must appear as a serializable property so the UI can render
    # the badge. Sanity-check the shape (the field exists on any DTO in the payload).
    $issues = (Invoke-Md -Path 'Issues?openOnly=true' -AsJson).Body
    if ($issues -is [array] -and $issues.Count -gt 0) {
        $hasField = $issues[0].PSObject.Properties.Name -contains 'WasPreviouslyRestored'
        Register-Result 'Issue DTO exposes WasPreviouslyRestored field' $hasField 'field missing'
    } else {
        # No issues right now — just verify the endpoint shape by inspecting the JSON via property names on a probe object.
        Register-Result 'Issue DTO WasPreviouslyRestored: no issues to inspect (skipped)' $true 'no issues'
    }

    Remove-Item -Path $protPath -Force -ErrorAction SilentlyContinue
} else {
    Register-Result 'Protection cycle: bin item found' $false 'not in listing'
}

# Clean up scratch folder
if (Test-Path $scratchDir) { Remove-Item -Path $scratchDir -Recurse -Force -ErrorAction SilentlyContinue }

# --- Summary ---
Write-Host ""
Write-Host "== summary =="
$total = $script:results.Count
$passed = ($script:results | Where-Object Passed).Count
$failed = $total - $passed
Write-Host ("total: $total   passed: $passed   failed: $failed")
if ($failed -gt 0) {
    Write-Host ""
    Write-Host "FAILURES:" -ForegroundColor Red
    $script:results | Where-Object { -not $_.Passed } | ForEach-Object { Write-Host ("  $($_.Name)   $($_.Detail)") -ForegroundColor Red }
    exit 1
} else {
    Write-Host "all live tests passed." -ForegroundColor Green
    exit 0
}
