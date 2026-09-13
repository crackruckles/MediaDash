# Malformed-row torture: inject 5 rows into the issues table whose shape would tempt the
# plugin to crash (bad Guid item_id, non-object DetailsJson roots, non-string reason
# field), then verify /Status, /Issues, and /Fix all return 2xx and don't throw.
#
# Baseline behaviour (before Phase 1):
#   - GET /MediaDash/Status returns 500 because GetIssues Guid.ParseExact throws on the
#     'not-a-guid' item_id (F-014).
#   - Even after F-014, GET /MediaDash/Issues would surface the non-object DetailsJson
#     rows to HasBlockingWarnings and TryGetReason and throw InvalidOperationException
#     (F-015, F-016) on the /Fix path.
#
# Post-Phase-1 expected behaviour: all three endpoints return 2xx, the plugin logs each
# skipped malformed row with its id, and valid rows continue to be served / fixed.
#
# Prerequisites:
#   - Local Jellyfin v10 running at localhost:8099 with admin user test/test
#   - MediaDash plugin deployed and initialised (mediadash.db exists)
#   - sqlite3 on PATH
#
# Exit code: 0 = every endpoint returned 2xx, 1 = at least one endpoint threw (baseline).

param(
    [string]$JellyfinUrl = 'http://localhost:8099',
    [string]$AdminUser = 'test',
    [string]$AdminPass = 'test',
    [string]$DbPath = "$env:LOCALAPPDATA\jellyfin-v10\data\mediadash\mediadash.db"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $DbPath)) {
    Write-Host "MediaDash DB not found at $DbPath — is the plugin deployed and initialised?" -ForegroundColor Red
    exit 1
}

# ponytail: fixed marker string. Cleanup at start (in case a prior run failed) and end.
$marker = 'malformed-row-torture'

Write-Host "Clearing any leftover malformed-row rows..."
& sqlite3 $DbPath "DELETE FROM issues WHERE path LIKE '%$marker%';"

# --- Inject 5 malformed rows ------------------------------------------------
# Schema: (type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc, confidence)
# type = 1 (Playability), status = 0 (Detected), detected_at_utc = 0 (epoch, plugin tolerates).
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

$rows = @(
    # 1. Bad Guid item_id (32-char requirement violated). Trips F-014.
    @{ desc = 'bad-guid item_id';           itemId = 'not-a-guid';                       details = '{}'                     }
    # 2. Valid JSON, non-object root: null.
    @{ desc = 'DetailsJson = null';         itemId = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';  details = 'null'                   }
    # 3. Valid JSON, non-object root: array.
    @{ desc = 'DetailsJson = [1,2,3]';      itemId = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';  details = '[1,2,3]'                }
    # 4. Valid JSON, non-object root: number.
    @{ desc = 'DetailsJson = 42';           itemId = 'cccccccccccccccccccccccccccccccc';  details = '42'                     }
    # 5. Object root but reason field is not a string.
    @{ desc = 'DetailsJson reason non-str'; itemId = 'dddddddddddddddddddddddddddddddd';  details = '{"reason":[1,2]}'       }
)

Write-Host "`nInjecting $($rows.Count) malformed rows into issues table..."
foreach ($r in $rows) {
    $path = "$marker/$($r.itemId).mp4"
    $sql  = "INSERT INTO issues (type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc, confidence) VALUES (1, '$($r.itemId)', '$path', '$($r.details -replace "'","''")', '', 0, 0, $now, NULL);"
    & sqlite3 $DbPath $sql
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  FAILED to insert row: $($r.desc)" -ForegroundColor Red
        exit 1
    }
    Write-Host "  injected: $($r.desc)"
}

# --- Authenticate -----------------------------------------------------------
Write-Host "`nAuthenticating..."
$authBody = @{ Username = $AdminUser; Pw = $AdminPass } | ConvertTo-Json -Compress
$authH    = @{ Authorization = 'MediaBrowser Client="mrtorture", Device="ps", DeviceId="dtmr1", Version="1"' }
$auth = Invoke-RestMethod -Method Post -Uri "$JellyfinUrl/Users/AuthenticateByName" -Body $authBody -ContentType application/json -Headers $authH
$hdr  = @{ Authorization = "MediaBrowser Token=`"$($auth.AccessToken)`", Client=`"mrtorture`", Device=`"ps`", DeviceId=`"dtmr1`", Version=`"1`"" }

# --- Hit the three endpoints ------------------------------------------------
$results = @()

function Probe($label, [scriptblock]$call) {
    try {
        & $call | Out-Null
        Write-Host ("  [PASS] {0}" -f $label) -ForegroundColor Green
        return [pscustomobject]@{ endpoint = $label; status = 'PASS'; detail = '2xx' }
    } catch {
        $code = try { $_.Exception.Response.StatusCode.value__ } catch { 0 }
        $msg  = if ($code) { "HTTP $code" } else { $_.Exception.Message }
        Write-Host ("  [FAIL] {0} -> {1}" -f $label, $msg) -ForegroundColor Red
        return [pscustomobject]@{ endpoint = $label; status = 'FAIL'; detail = $msg }
    }
}

Write-Host "`nProbing endpoints..."
$results += Probe 'GET  /MediaDash/Status'  { Invoke-RestMethod -Uri "$JellyfinUrl/MediaDash/Status" -Headers $hdr }
$results += Probe 'GET  /MediaDash/Issues'  { Invoke-RestMethod -Uri "$JellyfinUrl/MediaDash/Issues" -Headers $hdr }
$results += Probe 'POST /MediaDash/Fix'     { Invoke-RestMethod -Method Post -Uri "$JellyfinUrl/MediaDash/Fix" -Headers $hdr }

# --- Cleanup + report -------------------------------------------------------
Write-Host "`nCleaning up injected rows..."
& sqlite3 $DbPath "DELETE FROM issues WHERE path LIKE '%$marker%';"

$results | Format-Table -AutoSize endpoint, status, detail
$failed = @($results | Where-Object status -eq 'FAIL')
Write-Host ("`n{0}/{1} endpoints survived malformed rows." -f ($results.Count - $failed.Count), $results.Count)
if ($failed.Count -gt 0) { exit 1 }
exit 0
