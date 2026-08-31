# 00 · Setup — one-time per machine

Bring the localhost Jellyfin test bed up, seed a library, confirm the
plugin is loaded, and cache an auth token. Every other chapter assumes
this is done.

Re-do §3 (token) once per new session — tokens expire.

Return to [INDEX](INDEX.md) when done.

> **Corrected 2026-08-28** after QA session 1. Paths, log globs, auth
> headers and recycle-bin expectations in the original draft were wrong.
> See F-001…F-008 in [FINDINGS.md](FINDINGS.md) and the triage entries
> T-001…T-003.

---

## ⚠ Read before touching anything

- [x] **0.1** The test library contains **one very large real media file**
      (~26.9 GB, `movies\The.Devil.Wears.Prada.2.2026...mkv`). It is NOT a
      generated fixture. **Never approve a destructive fix against it.**
      Confirm you can see it and that you have noted its name:
  ```powershell
  Get-ChildItem C:\dev\mediadash-fixtures\movies -File |
    Where-Object Length -gt 1GB | Select-Object Name, @{n='GB';e={[math]::Round($_.Length/1GB,1)}}
  ```
- [x] **0.2** Destructive fixer tests are **out of scope for chapters 00
      and 01**. Scanners are read-only. Do not run `POST /MediaDash/Fix`
      during this chapter or chapter 01.

---

## §1 · Jellyfin server (localhost:8099)

- [x] **1.1** Jellyfin dev server responds:
  ```powershell
  Invoke-RestMethod http://localhost:8099/System/Info/Public
  ```
  Expect `{ ServerName, Version, Id, ... }`. Version must start `10.11` or `12.`.
- [x] **1.2** Data directory. Recent builds use a **version-suffixed**
      folder. Find the live one (the one whose `log/` has today's file):
  ```powershell
  Get-ChildItem "$env:LOCALAPPDATA" -Filter "jellyfin*" -Directory |
    ForEach-Object {
      $log = Join-Path $_.FullName "log"
      [pscustomobject]@{
        Dir      = $_.Name
        LastLog  = (Get-ChildItem $log -Filter "log_*.log" -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Desc | Select-Object -First 1).LastWriteTime
      }
    } | Sort-Object LastLog -Descending
  ```
      Record the live dir here: `jellyfin-v10`
      (on this machine it is `jellyfin-v10`, **not** `jellyfin`).
      Export it for the rest of the session:
  ```powershell
  $env:JFDATA = "$env:LOCALAPPDATA\jellyfin-v10"
  ```
- [x] **1.3** Log tail is live. Note the filename pattern is
      `log_YYYYMMDD.log`, **not** `jellyfin*.log`:
  ```powershell
  Get-ChildItem "$env:JFDATA\log\log_*.log" |
    Sort-Object LastWriteTime -Desc | Select-Object -First 1 |
    Get-Content -Tail 5
  ```
- [x] **1.4** Save a log-tail helper for later chapters:
  ```powershell
  function Get-JfLog([int]$Tail = 40) {
    Get-ChildItem "$env:JFDATA\log\log_*.log" |
      Sort-Object LastWriteTime -Desc | Select-Object -First 1 |
      Get-Content -Tail $Tail
  }
  function Find-JfLog([string]$Pattern) {
    Get-ChildItem "$env:JFDATA\log\log_*.log" |
      Sort-Object LastWriteTime -Desc | Select-Object -First 1 |
      Select-String -Pattern $Pattern -SimpleMatch
  }
  ```
      Verify: `Find-JfLog "MediaDash" | Select-Object -Last 5` returns lines.

## §2 · Test library on disk

The live test library is **`C:\dev\mediadash-fixtures`**. (An earlier draft
said `mediadash-testlib`; that path does not exist — F-003.)

- [x] **2.1** Set `$LIB` and confirm it exists:
  ```powershell
  $env:LIB = "C:\dev\mediadash-fixtures"
  Test-Path $env:LIB   # must be True
  Get-ChildItem $env:LIB -Directory | Select-Object Name
  ```
- [x] **2.2** Enumerate registered libraries (auth from §3 — do §3 first
      if this 401s):
  ```powershell
  curl.exe -s -H "Authorization: $env:JFAUTH" `
    http://localhost:8099/Library/VirtualFolders |
    ConvertFrom-Json | Select-Object Name, CollectionType, Locations
  ```
- [x] **2.3** Record what actually exists. Expected on this machine:
      `MediaDash Test` (movies), `Test Books` (books), `Test Comics`
      (books), `Test Music` (music), `Test Audiobooks` (blank type).
- [-] **2.4** **Known gap:** there is no `tvshows` library and no `shows`
      fixture tree. Any chapter step that seeds `$LIB\shows\...` cannot
      run. Mark those steps `[-]` with a pointer to F-005 rather than
      failing them. Do not create a TV library yourself — that changes the
      dev machine's setup.

### Fixture regeneration

Generated fixtures may have been consumed by an earlier destructive fix
run (the truncated-playability and 1080p-duplicate files are currently
missing). Regenerate them without touching anything else:

- [x] **2.5** Check which generated fixtures are present:
  ```powershell
  @(
    "movies\Big Buck Test (2020)\Big Buck Test (2020) - 2160p.mkv",
    "movies\Big Buck Test (2020)\Big Buck Test (2020) - 1080p.mkv",
    "movies\Truncated Movie (2021)\Truncated Movie (2021).mkv",
    "movies\Multi Audio (2022)\Multi Audio (2022).mkv",
    "movies\Sub Heavy (2023)\Sub Heavy (2023).mkv",
    "movies\Clean Movie (2024)\Clean Movie (2024).mkv"
  ) | ForEach-Object {
    [pscustomobject]@{ Present = (Test-Path (Join-Path $env:LIB $_)); Path = $_ }
  }
  ```
- [x] **2.6** If any are missing, regenerate into a **scratch dir** and
      copy only the missing ones across (the generator writes a whole
      tree; don't point it at `$LIB` directly or it overwrites live files):
  ```powershell
  $ff = (curl.exe -s -H "Authorization: $env:JFAUTH" http://localhost:8099/MediaDash/Environment | ConvertFrom-Json).ffmpegPath
  bash C:/dev/mediadash/tools/make-fixtures.sh /c/dev/mediadash-fixgen "$ff"
  ```
      Then copy individual missing files from `C:\dev\mediadash-fixgen\`
      into `$env:LIB\`.
- [x] **2.7** After copying, trigger a Jellyfin library scan so the new
      files are indexed (Dashboard → Scheduled Tasks → Scan Media Library,
      or the API), and confirm the item count rises.

## §3 · Auth token (redo per session)

Two things the original draft got wrong (F-008): the PowerShell JSON body
must be built with `ConvertTo-Json` and passed via `Invoke-RestMethod`
(curl `-d` mangles quotes under this harness), and `X-Emby-Token` alone is
**rejected** — you need the full `Authorization: MediaBrowser ...` header.

- [x] **3.1** Log in and capture the token:
  ```powershell
  $body = @{ Username = "test"; Pw = "test" } | ConvertTo-Json -Compress
  $r = Invoke-RestMethod -Method Post `
        -Uri http://localhost:8099/Users/AuthenticateByName `
        -ContentType "application/json" -Body $body `
        -Headers @{ "Authorization" = 'MediaBrowser Client="MediaDash-E2E", Device="ps", DeviceId="e2e", Version="1"' }
  $env:TOKEN = $r.AccessToken
  Write-Host "TOKEN=$env:TOKEN"
  ```
      Expect a non-empty token.
- [x] **3.2** Build the reusable auth header — **every** later chapter uses
      `$env:JFAUTH`, not `X-Emby-Token`:
  ```powershell
  $env:JFAUTH = "MediaBrowser Token=`"$env:TOKEN`", Client=`"MediaDash-E2E`", Device=`"ps`", DeviceId=`"e2e`", Version=`"1`""
  ```
- [x] **3.3** Verify it authenticates:
  ```powershell
  curl.exe -s -o NUL -w "%{http_code}`n" -H "Authorization: $env:JFAUTH" `
    http://localhost:8099/MediaDash/Status
  ```
      Expect `200`.
- [!] **3.4** Save a request helper for later chapters — **broken as
      written** (see F-009). PowerShell 5.1 has a built-in `md` alias
      pointing at `mkdir`, and `function Md ...` does not shadow it: every
      subsequent `Md <route>` call silently runs `mkdir <route>` and never
      reaches Jellyfin. Until this is fixed in the doc, use the shape
      below instead (kill the alias, then define the function):
  ```powershell
  Remove-Item Alias:md -Force -ErrorAction SilentlyContinue
  function Md([string]$Path, [string]$Method = "GET", $Body) {
    $u = "http://localhost:8099/MediaDash/$Path"
    $p = @{ Uri = $u; Method = $Method; Headers = @{ Authorization = $env:JFAUTH } }
    if ($Body) { $p.ContentType = "application/json"; $p.Body = ($Body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @p
  }
  ```
      Verify: `Md Status` returns the status object (not a `DirectoryInfo`).

## §4 · Plugin install (verify only)

A pure-QA session does **not** build or deploy — the plugin is already
installed and building would require touching source. Verify only.

- [!] **4.1** Plugin directory present and note the version:
  ```powershell
  Get-ChildItem "$env:JFDATA\plugins" -Directory -Filter "MediaDash*" | Select-Object Name
  ```
      Record version here: dir=`MediaDash_0.9.0.0` but `meta.json`
      version=`0.0.0.0` and Jellyfin logs `Loaded plugin: MediaDash
      0.0.0.0` — see F-010.
- [x] **4.2** Plugin loaded with no load failure in today's log:
  ```powershell
  Find-JfLog "MediaDash" | Select-Object -Last 10
  Find-JfLog "Failed to load plugin"
  ```
      First returns lines; second returns nothing for MediaDash.
- [x] **4.3** Plugin answers on its own route (proves DI + controller
      registration): `Md Status` returns JSON with a `dryRun` field.
      (Verified via curl bypassing the `md` alias — see F-009.)
- [x] **4.4** No `MissingMethodException` (cross-ABI regression canary):
  ```powershell
  Find-JfLog "MissingMethodException"
  ```
      Expect nothing.

## §5 · Recycle bin path

**Corrected (F-002 / T-002):** the bin's default location is the plugin's
own data folder — `<JellyfinDataPath>\mediadash\recycle` — and that is
**by design**, not a safety violation. The bin is where files go *after*
leaving the library; it is not required to live inside the library. A
custom path can be set in Settings → Recycle bin, and putting it on the
same volume as the media makes moves instant renames instead of copies.

- [x] **5.1** Read the effective bin path:
  ```powershell
  (Md Status) | Select-Object recycleBinPath, recycleBinFileCount, recycleBinCrossVolume
  ```
      Record here: `RecycleBinPath =
      C:\Users\crackruckles\AppData\Local\jellyfin-v10\data\mediadash\recycle`,
      `RecycleBinFileCount = 3`, `RecycleBinCrossVolume = False`.
- [x] **5.2** Bin path is **either** the configured `RecycleBinPath` **or**
      the default `<JFDATA>\mediadash\recycle`. Confirm which:
  ```powershell
  Select-String -Path "$env:JFDATA\plugins\configurations\Jellyfin.Plugin.MediaDash.xml" `
    -Pattern "RecycleBinPath"
  ```
      Empty element → default in use. That is a **pass**.
- [x] **5.3** Bin is NOT under an OS-reserved root (`C:\Windows`,
      `C:\Program Files`, `/etc`, `/usr`, `/bin`). This is the real
      invariant.
- [x] **5.4** `recycleBinCrossVolume` is `false`, or if `true`, a warning
      is surfaced in diagnostics (`Md Errors`).

---

## §6 · Safety posture for this session

Chapters 00 and 01 are read-only. You do not need dry-run ON to run them,
but you must confirm no fix run is triggered.

- [x] **6.1** Record the current safety posture (do NOT change it — this is
      the developer's machine, and changing it is itself a destructive act):
  ```powershell
  Select-String -Path "$env:JFDATA\plugins\configurations\Jellyfin.Plugin.MediaDash.xml" `
    -Pattern "DryRun|FixMode" | ForEach-Object { $_.Line.Trim() }
  ```
      Record `DryRun` value here: `false` (six modes are `Automatic`;
      one is `ManualApprove`; the rest are `DetectOnly`. See F-011).
- [x] **6.2** If any `*FixMode` is not `DetectOnly`, note **which** in
      FINDINGS.md as an `env` finding (informational, not critical) — a
      scan you trigger could auto-queue a fix. Shipped defaults are
      `DryRun=true` and all modes `DetectOnly`, so a machine that differs
      has been changed deliberately by its owner.
- [x] **6.3** Do not run `POST /MediaDash/Fix` in chapters 00–01. If a
      scan auto-queues fixes, cancel immediately:
      `Md Fix/Cancel POST`
- [x] **6.4** Before any scan, snapshot the library so you can prove
      nothing was lost:
  ```powershell
  Get-ChildItem $env:LIB -Recurse -File |
    Select-Object FullName, Length, LastWriteTime |
    Export-Csv "$env:TEMP\lib-before.csv" -NoTypeInformation
  ```
      Re-run as `lib-after.csv` at end of chapter and diff:
  ```powershell
  Compare-Object (Import-Csv "$env:TEMP\lib-before.csv") `
                 (Import-Csv "$env:TEMP\lib-after.csv") -Property FullName
  ```
      Expect no differences after a read-only chapter.

---

## Cleanup after chapter run

```powershell
# 1. Cancel anything running
Md Scan/Cancel POST
Md Fix/Cancel  POST
# 2. Clear plugin issue/history state (does NOT touch files)
Md Reset POST
```

`Reset` returns 409 if a scan or fix is active — cancel first.

**Do NOT** run the old draft's `Get-ChildItem $LIB -Recurse -File |
Remove-Item -Force` step. `$LIB` is the real fixture library on this
machine, and that command would delete the 26.9 GB media file along with
every fixture. It has been removed from this doc.
