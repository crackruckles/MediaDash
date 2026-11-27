# PR #91: UNC storage on Overview

Adapted from [Icabaad's PR #91](https://github.com/crackruckles/MediaDash/pull/91), head `3e37eb74cd53b656fed87d642c606cbbdd79bf61`, into the current dirty working tree. No commit, push, release or production deployment.

Configured Windows UNC libraries now contribute share rows, capacity totals and the existing configurable low-space warnings. Multiple library folders on one share are deduplicated case-insensitively, including extended UNC paths. Network rows have a badge and do not show SMART controls or attributes.

Differences from the proposed PR:

- Reuse the current #89 directory-based, quota-aware space measurement; expose total capacity without restoring root-filesystem fallback behavior.
- Never wait for network I/O in the status request. Cache readings for 30 seconds; at most one background measurement per share and four overall. Four stuck system calls occupy those four slots until they return; polls stay responsive, but additional shares cannot be measured in that interval.
- Retain and explicitly label stale readings. A share with no successful measurement remains absent until it answers, normally appearing on a later dashboard poll.
- Keep distinct shares separate. Capacity and free-space similarity do not establish disk identity. When multiple shares are displayed, explain that totals may overlap if they use the same disk.

## Verification

- Focused .NET checks: 39 passed (UNC extraction/cache/storage, existing free-space guards and warning thresholds).
- Ordinary .NET suite: 2,526 passed, zero failed/skipped.
- UI regression/lifecycle/issue/diagnostics checks plus new UNC renderer tests: 111 passed.
- Release solution build: zero warnings/errors.
- JavaScript syntax, English translation JSON parsing and scoped whitespace checks passed.
- Real local SMB acceptance: two tests passed against an owned temporary directory via `\\localhost\C$`. Verified share-root capacity reaches the Overview row helper and both existing mutation guards preserve source bytes. Temporary fixture directory removed. Evidence: `Jellyfin.Plugin.MediaDash.Tests/TestResults/pr91-local-unc.trx`.
- Separate code review: no important findings.

The first SMB attempt overlapped the ordinary test runner and failed to copy its locked test DLL. The serialized rerun above passed.

Disposable-host scan-only validation: `artifacts/testenv/runs/20261127-094105/summary.md`, overall environment exit 2.

- Linux and Docker loaded MediaDash 1.0.8.2 from the current tree. Each scan matched 40 fixture expectations, with zero missing, one unexpected `Ungrouped movies/Duplicates/Dupe Test (2021)`, three known gaps and zero recorded MediaDash diagnostics. The grouping finding is outside the storage changes; this run does not establish a fully green fixture suite.
- Windows could not be validated: Jellyfin refused connections on port 8096 throughout the harness's 300-second startup wait. Log capture also failed because the pinned Windows SSH host key no longer matches. Strict host-key checking was not bypassed or modified.
- All three hosts were restored to golden and independently confirmed stopped after the run. The harness lock was released.
- The versioned build is `artifacts/testenv-publish/Jellyfin.Plugin.MediaDash.dll`, SHA-256 `1A303293243640F6BDE83916EC5ACB458876BD9CF3C86D873D89C006F74F36A4`.

The SMB check runs as the interactive Windows account. It does not certify a remote NAS, service-account permissions or quotas, or a full Jellyfin UNC library workflow. No production media was changed.
