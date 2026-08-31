# Wire the 5 fixture libraries into a fresh Jellyfin instance via the API.
# Run this AFTER completing the Startup Wizard (create user test/test).

$ErrorActionPreference = "Stop"

# Auth
$body = @{ Username = "test"; Pw = "test" } | ConvertTo-Json -Compress
$r = Invoke-RestMethod -Method Post -Uri http://localhost:8099/Users/AuthenticateByName `
    -ContentType application/json -Body $body `
    -Headers @{ Authorization = 'MediaBrowser Client="mediadash-setup", Device="ps", DeviceId="setup", Version="1"' }
$env:JFAUTH = "MediaBrowser Token=`"$($r.AccessToken)`", Client=`"mediadash-setup`", Device=`"ps`", DeviceId=`"setup`", Version=`"1`""
Write-Host "Auth OK." -ForegroundColor Green

# Add the 5 libraries
$libs = @(
    @{ name = "MediaDash Test";   type = "movies";     path = "C:\dev\mediadash-fixtures\movies" },
    @{ name = "Test Audiobooks";  type = "";           path = "C:\dev\mediadash-fixtures\audiobooks" },
    @{ name = "Test Books";       type = "books";      path = "C:\dev\mediadash-fixtures\books" },
    @{ name = "Test Comics";      type = "books";      path = "C:\dev\mediadash-fixtures\comics" },
    @{ name = "Test Music";       type = "music";      path = "C:\dev\mediadash-fixtures\music" }
)
foreach ($l in $libs) {
    $q = "name=$([Uri]::EscapeDataString($l.name))&collectionType=$($l.type)&paths=$([Uri]::EscapeDataString($l.path))&refreshLibrary=false"
    $code = curl.exe -s -o NUL -w "%{http_code}" -X POST -H "Authorization: $env:JFAUTH" `
        "http://localhost:8099/Library/VirtualFolders?$q"
    Write-Host "  add lib $($l.name) -> $code"
}

Write-Host ""
Write-Host "Triggering initial library scan ..." -ForegroundColor Cyan
$tasks = curl.exe -s -H "Authorization: $env:JFAUTH" "http://localhost:8099/ScheduledTasks" | ConvertFrom-Json
$scan = $tasks | Where-Object Key -eq "RefreshLibrary"
curl.exe -s -o NUL -X POST -H "Authorization: $env:JFAUTH" "http://localhost:8099/ScheduledTasks/Running/$($scan.Id)"

# Poll
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep 3
    $t = curl.exe -s -H "Authorization: $env:JFAUTH" "http://localhost:8099/ScheduledTasks/$($scan.Id)" | ConvertFrom-Json
    if ($t.State -eq "Idle") { break }
    Write-Host "  scan $($t.State) $($t.CurrentProgressPercentage)%"
}
Write-Host "Scan done." -ForegroundColor Green

# Verify
$items = curl.exe -s -H "Authorization: $env:JFAUTH" "http://localhost:8099/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path&Limit=500" | ConvertFrom-Json
$fix = $items.Items | Where-Object { $_.Path -like "C:\dev\mediadash-fixtures\*" }
Write-Host ""
Write-Host "Movie items indexed from fixture library: $(@($fix).Count)" -ForegroundColor Green
$fix | Select-Object Name, Path | Sort-Object Name | Format-Table -AutoSize

Write-Host ""
Write-Host "If you see >= 5 items (Big Buck, Clean, Multi Audio, Sub Heavy, Truncated Movie)," -ForegroundColor Yellow
Write-Host "F-019 is fixed. If 0 or partial — the DB corruption is deeper and needs a bug report to Jellyfin." -ForegroundColor Yellow
