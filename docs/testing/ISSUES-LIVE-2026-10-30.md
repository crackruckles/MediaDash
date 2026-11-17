# Issue workflows on isolated Jellyfin hosts — 30 October 2026

The frozen local candidate completed the controlled issue workflows on Jellyfin **10.11.11** and the available **12.0.0** distribution. Both owned servers are stopped, their user settings and empty library/approval/protection state are restored, and the bulk fixture hashes were verified before removal. **This does not close every issue or authorize pushing/publishing.** Durable mover recovery/Undo and bounded folder indexing still require implementation; reporter media and remote environment acceptance remain unavailable.

No GitHub write, commit, push, production server update or production-media operation occurred. Every server request used `127.0.0.1:8099`; the two hosts ran sequentially in generated artifact directories. The UNC case used an owned `\\localhost\C$` directory on this computer. The user explicitly authorized local server testing.

## Exact candidate and evidence

- Source fingerprint: `56777e04af0538d17710e2af69ab57a7a4d3cdff0238f13d0f994ee256f2d0fd`.
- Installed Release DLL SHA-256 on **both** hosts: `4e1c3808574823e807c187f3eea6daf4f72b57ea719483327201a71bcd50681e`.
- [Source manifest](../../artifacts/issues-live-20261029-continuation/candidate-source.json), [frozen source archive](../../artifacts/issues-live-20261029-continuation/candidate-source.zip), [computed live summary](../../artifacts/issues-live-20261029-continuation/live-summary.json), [orchestration](../../artifacts/issues-live-20261029-continuation/run.py).
- Owned host roots: `artifacts/issues-live-20261029-continuation/v10/server` and `v12/server`. Active data directory and Active plugin status were checked at login, including after real restarts.
- The 12 distribution reports `12.0.0` through the API/executable; its web dashboard displays build `12.0-rc3`. It is **not** the reporter's Jellyfin 12.2 environment. The exact runtime limitation is retained rather than calling this universal 12.x certification.

The source archive belongs to the tested candidate. A later mover implementation is being developed in a separate detached worktree at `artifacts/remaining-issue-repairs-worktree`; its changes are **not** certified by this report and have not been copied into the original checkout or deployed to these hosts.

## Controlled workflow results

| Scope | Observed on both local hosts | Boundary |
| --- | --- | --- |
| Approval/migration (#83) | A real dry-run adds exact issue/path History and preserves source bytes and approval. An actual persisted `DetectOnly` XML value migrates to `ManualApprove` across restart without losing configuration or queued IDs/paths. Real cleanup, full decode, retained packet hashes, full HTTP playback-body hash, clean rescan, recycling and occupied-path restoration pass. | Generated media and isolated configuration. No universal deployment claim. |
| Artwork (#88) | Healthy image uploads and truncated-image detection; selected-library authorization; stale approval preserves a healthy replacement; preview; real recoverable cleanup; persisted ImageInfo removal; force restore; displaced healthy image recovery and image serving. Healthy SVG and exclusively locked artwork are preserved. Real recycle failure and a real write lock on the owned Jellyfin SQLite database preserve source, ImageInfo and queue with failed History. | Windows native image stack on these two distributions. Reporter 12.2/Linux/Skia environment is not reproduced. |
| Metadata disposal (#86) | Default and configured custom metadata roots; all-owned and selected-library scopes; persisted observation; exact-byte preview; approval remains queued until a later successful Jellyfin library scan; actual restart retains observation and approval; protected child refuses the complete cleanup; confirmed recycle/history/rescan/restore recovers every original byte. Same-size replacement with retained mtime invalidates consent. A deliberately unusable recycle destination fails without deleting metadata and remains queued. | Live disposal used recycling. Permanent disposal remains covered by offline regressions. Final checks are snapshots and cannot exclude every external-writer race. Default-root blocked-recycle coverage ran on v12; custom-root blocked-recycle coverage ran on both. |
| Original audio (#87) | A generated Spanish track carries the actual original disposition; English is default. Approval and real cleanup retain Spanish original audio, video and hearing-impaired Spanish subtitles with unchanged retained packet hashes. Full decode, full HTTP playback-body hash, recycle original, clean rescan and exact-byte restoration pass. | This is a known controlled flag/metadata case. Rafa's exact stream flags and Jellyfin item/series metadata remain unavailable. |
| MKV failures (#92) | Real Matroska output-header failure from a generated timecode/data-track control; separate stale cached subtitle-inventory injection exercises retained-track refusal. Both preserve the entire original hash and decode, retain queued approval, write exact-path failure History with zero savings, and keep diagnostics visible. | These demonstrate preservation and useful failure reporting. They do not prove the reporter's codec/container cause or repair the unavailable reporter MKVs. |
| UNC/space (#89) and changed-file refresh | Actual quality re-encode through loopback SMB; all retained stream kinds, full decode, exact recycled-original hash, smaller output, retained mtime, full HTTP body hash, clean quality rescan and exact-byte restoration. Jellyfin's user item metadata changes from 360p to 90p within the bounded wait without starting the full scheduled library scan. | Interactive local account and loopback administrative share. Remote NAS ACLs, quotas, service credentials and production LXC are not exercised. This proves individual-file refresh, not bounded folder indexing or unrelated-sibling behavior. |
| Moves (#43/Reddit) | A real loose movie with NFO and external subtitle is scanned and approved. A companion collision introduced after scanning preserves all sources/destinations, remains queued with zero savings, and survives a real rescan. Protected companion refuses the whole move. Removing the blockers moves the complete matching bundle with exact hashes and one zero-savings success; Jellyfin indexes the moved primary and serves its exact playback body. Explicit Files moves restore each recorded fixture member. | The final recovery is **manual fixture recovery**, not product Undo. Durable plans, claims, restart recovery and complete Undo were absent in this tested candidate. This is not the exact Linux Mint Reddit reproduction. |

Per-host artifacts contain `issue86-*.json`, `issue87-live.json`, `issue92-header.json`, `issue92-inventory.json`, `issue89-live.json`, `issue43-live.json`, and artwork/audio History and restoration JSON. Every attribution uses the new attempt's issue ID and complete fixture path, rather than accepting a generic History success or HTTP 200.

## Loaded history, scans and browser

Each host was seeded **while stopped** with one million synthetic History rows across 2,000 synthetic findings. Those findings were not approved. The lookup verified each requested issue's newest attempt across 20 HTTP requests. A separate owned library contained 1,000 generated dual-audio movies, about 164 MB total. Native Jellyfin indexing completed before measurement; only the owned plugin probe cache was cleared for the cold run.

| Measurement | 10.11.11 | Available 12.0.0 distribution |
| --- | --- | --- |
| Loaded Issues HTTP mean / maximum | 58.03 / 93.99 ms | 62.45 / 91.00 ms |
| Cold scan | 33.27 s | 35.22 s |
| Warm scan | 0.48 s | 0.71 s |
| Cold progress maximum latency | 30.11 ms | 36.00 ms |
| Warm progress maximum latency | 25.99 ms | 25.54 ms |
| Cold host-process CPU increment | 9.12 s | 10.86 s |
| Exact generated unwanted-audio findings | 1,000 unique paths | 1,000 unique paths |

The scan enabled AudioLanguage and Artwork only; other scanner modes were Off. CPU numbers cover the host process, not child FFmpeg/ffprobe processes. The native index took roughly 11–12 minutes per host and is excluded from the plugin scan durations. The light progress endpoint advanced during the audio phase; saved samples and INFO phase logs separate materialization, preparation, scanner work and persistence. These are **not** 20 TB results or a claimed speed improvement in full hashing/decode workloads.

Browser checks used the actual local Jellyfin web application and MediaDash UI. On 10.11, filename filtering found the Spanish fixture with its restore safeguard; History rendered 500 newest loaded rows and filtered to the requested filename. On 12, the 1,004-item Issues list paginated to 11 pages and filtering found a fixture beyond the initial page. UI approval followed by **Run fixes now** in dry-run mode produced exactly one new History row for that path, left the complete source hash unchanged, and retained approval. UI Undo returned the queue to empty; the History dry-run filter showed the single preview with “nothing changed.” Browser error checks were empty. [UI dry-run proof](../../artifacts/issues-live-20261029-continuation/v12/final-evidence/browser-dry-run-history.json), [10 browser observations](../../artifacts/issues-live-20261029-continuation/v10/final-evidence/browser-observations.json), [12 browser observations](../../artifacts/issues-live-20261029-continuation/v12/final-evidence/browser-observations.json).

## Accounting and cleanup

Raw results retain **462 PASS and 5 FAIL records on v10**, and **455 PASS / 0 FAIL records on v12**. These are check records, including repeated authentication, task completion and integrity assertions, not 917 distinct test scenarios. Do not present the raw run as zero failures.

The five v10 records came from four interrupted attempts, all retained in the logs:

1. Jellyfin's initial image-upload endpoint returned 500 while the old managed poster was briefly locked. The native log names a sharing violation. After verifying the lock was released, the complete artwork workflow passed.
2. The new UNC orchestration lacked its inherited `added` list before library creation. Initializing that harness member allowed the complete workflow to pass.
3. Custom-metadata configuration returned 404 because the new owned directory did not exist yet. Creating that generated directory before setting it allowed the full custom-root workflow to pass.
4. Cleanup's exact configuration assertion encountered the automatically seeded internal `FixTaskSeeded` flag; the assertion and the outer exception produced two FAIL records. Cleanup now checks all user settings, records any internal bookkeeping difference, waits for scanner completion, and disables test triggers. The final retry passed and retained the failure logs.

All intended workflows reached their passing endpoint after these documented retries/corrections. No product repair failure is hidden as a skipped case. The guarded-failure fixtures intentionally produce failed fix History while their preservation assertions pass.

[10 cleanup](../../artifacts/issues-live-20261029-continuation/v10/final-evidence/cleanup.json), [12 cleanup](../../artifacts/issues-live-20261029-continuation/v12/final-evidence/cleanup.json), and both `trim.json` files prove user-setting restoration, empty library/queue/protection state, original fixture hashes, removal of exactly the expected synthetic million History rows, and verification of all 1,000 movie hashes before removing each exact owned bulk directory. Both processes exited through the API shutdown path; no listener remained on port 8099. Other diagnostic/recovery evidence stays inside the owned host artifacts for review.

The original candidate's previous offline results remain applicable because its source fingerprint was rechecked unchanged: 1,835 Windows offline tests, 1,838 Linux tests including three actual cross-filesystem dispatch cases, and five generated-media/UNC service workflows with each FFmpeg bundle. Those results and this live report still leave **DO NOT PUSH / PUBLISH**: unfinished mover/refresh implementation, missing reporter inputs, remote NAS/service-account/quota and large-library acceptance, and the uncommitted acceptance gate are not waived.
