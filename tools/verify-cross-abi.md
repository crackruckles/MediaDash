# Verify MediaDash Cross-ABI (Jellyfin 10.11 ↔ 12.0)

MediaDash ships a single .NET 9 binary that must load on both Jellyfin 10.11 and 12.0.
Run this checklist before every release. Manual by design — a Docker CI matrix would rot without active maintenance.

## 1. Static API audit

Run these greps from the repo root and confirm every hit is either a known-stable surface (see table below) or handled by an existing reflection bridge:

```
git grep -n "^using Jellyfin\|^using MediaBrowser" Jellyfin.Plugin.MediaDash/ | sort -u
git grep -n "IUserManager\|IUserDataManager\|ILibraryManager\|ISubtitleManager\|IApplicationPaths\|IServerApplicationPaths\|ILibraryMonitor\|MetadataRefresh\|GetBaseItemKind" -- Jellyfin.Plugin.MediaDash/
```

## 2. Smoke test — localhost v12

1. Build: `dotnet publish Jellyfin.Plugin.MediaDash/Jellyfin.Plugin.MediaDash.csproj -c Release`
2. Copy `bin/Release/net9.0/publish/*` to your Jellyfin 12.0 host's plugin folder (e.g., `%LOCALAPPDATA%\jellyfin\plugins\MediaDash_1.0.0.0\`).
3. Restart Jellyfin.
4. In Jellyfin dashboard → Scheduled Tasks, run **MediaDash: Scan libraries** in dry-run.
5. Tail Jellyfin's log folder:
   ```powershell
   Select-String -Path <log-dir>\*.log -Pattern "MissingMethodException|TypeLoadException|FileNotFoundException" | Where-Object { $_.Line -like "*MediaDash*" }
   ```
6. Expected: no matches. Any match → identify the failing hop, add a reflection bridge, iterate.

## 3. Smoke test — v10.11 regression

Same procedure against Jellyfin 10.11. If you don't have a native install, use Docker:

```
docker pull jellyfin/jellyfin:10.11
docker run -d --name jf-1011 -p 8098:8096 -v /path/to/fixtures:/media jellyfin/jellyfin:10.11
# Copy the plugin publish output into /config/plugins/MediaDash_1.0.0.0/ inside the container.
docker restart jf-1011
# ... run tasks + tail logs as above
docker stop jf-1011 && docker rm jf-1011
```

## 4. Known-changed API map

Each row is a Jellyfin API touch point in MediaDash and whether it needs a bridge for 10.11 ↔ 12.0.

| Symbol | 10.11 shape | 12.0 shape | Handled by |
|--------|-------------|------------|------------|
| `User` entity type | `Jellyfin.Data.Entities.User` | `Jellyfin.Database.Implementations.Entities.User` | `UserApiBridge` in `Scanners/StaleContentScanner.cs` (reflection) |
| `IUserManager.Users` | property | method `GetUsers()` | `UserApiBridge` in `Scanners/StaleContentScanner.cs` (reflection) |
| `IUserDataManager.GetUserData(User, BaseItem)` | via reflection | via reflection | `UserApiBridge` |
| `IServerApplicationPaths.InternalMetadataPath` | stable property | stable property | none needed |
| `ILibraryManager.GetItemById(Guid)` | stable | stable | none needed |
| `ILibraryManager.GetItemList(InternalItemsQuery)` | stable | stable | none needed |
| `ILibraryManager.GetVirtualFolders()` | returns items with populated `ItemId` (hex Guid string) | returns items with `ItemId == null`; `Id` (Guid) property carries the identity | `Scanners/VirtualFolderIdentity.GetId(f)` — reflection-cached fallback to `Id.ToString("N")` |
| `VirtualFolderInfo.ItemId` | populated | null | route through `VirtualFolderIdentity.GetId` (never touch `f.ItemId` directly in scoping filters) |
| `VirtualFolderInfo.CollectionType` | `CollectionTypeOptions?` enum (unchanged) | same | `.ToString()?.ToLowerInvariant()` when serializing to the frontend so `movies` / `tvshows` string matches Jellyfin's JSON convention |
| `ILibraryMonitor.ReportFileSystemChanged(string)` | stable | stable | none needed |
| `BaseItem.ImageInfos` | stable | stable | none needed |
| `BaseItem.ProviderIds` | stable | stable | none needed |
| `BaseItem.Genres` | stable | stable | none needed |
| `BaseItem.GetBaseItemKind()` | stable | stable | none needed |
| `Audio.Artists` / `.Album` / `.RunTimeTicks` | stable | stable | none needed |
| `ItemImageInfo.Path` / `.Type` | stable | stable | none needed |
| `Jellyfin.Data.Enums.BaseItemKind` values (`Audio`, `AudioBook`, `Book`, `Movie`, `Episode`, `MusicVideo`) | present | present | none needed |
| `MediaBrowser.Controller.Entities.Book` entity type | `MediaBrowser.Controller.Entities` | `MediaBrowser.Controller.Entities` | none needed |
| `MediaBrowser.Controller.Entities.Audio.Audio` entity type | `MediaBrowser.Controller.Entities.Audio` | `MediaBrowser.Controller.Entities.Audio` | none needed |
| `MediaBrowser.Controller.Entities.Movies.Movie` entity type | stable namespace | stable namespace | none needed |
| `MediaBrowser.Controller.Entities.TV.Episode` entity type | stable namespace | stable namespace | none needed |

Add rows for any newly-audited hop the maintainer verifies as either stable or requires a bridge.

## 4a. Scoped-scanner assertion (v12 regression guard)

On v12, `EnabledLibraries` and `StaleExcludedLibraryIds` filter by folder identity. If `VirtualFolderIdentity.GetId` regresses, both silently drop every library and scoped scanners report 0 issues across the board — a failure mode with no exception in the log.

1. Confirm the plugin config XML has at least one entry under `<EnabledLibraries>`; if not, pick a library and add its `ItemId` from `GET /MediaDash/Libraries`.
2. Ensure the scoped library has known issues (e.g., a fake `.exe` under one of its `Locations` so SuspiciousFileScanner has something to find).
3. Restart Jellyfin 12, trigger the MediaDash scan.
4. Expected in log:
   - `MediaDash scan starting: N items, 10 scanners` where N > 0 (the item filter must pass)
   - At least one `MediaDash scanner <Type> found > 0 issues` line
5. If every scanner reports `0 issues` and no unhandled exception exists, `VirtualFolderIdentity.GetId` is returning null again — check that both `f.ItemId` and the reflected `Id` property haven't both changed shape.

## 5. Release gate

Do not cut a release until:
- ✅ Static audit clean (no new API touch points without a row in section 4)
- ✅ Localhost v12 smoke test: 0 unhandled exception matches
- ✅ v12 scoped-scanner assertion (section 4a) passes
- ✅ v10.11 smoke test: 0 unhandled exception matches
- ✅ `dotnet test` green
