# Local scanner resource measurement — 30 October 2026

## Collect diagnostics from Jellyfin

Enable **Settings → Maintenance → Diagnostics mode**, save changes, and run a scan. The **Diagnostics** tab appears next to Errors and retains the latest diagnostic scan across restarts. Turning the mode off hides the tab and blocks its data endpoint. This setting replaces the former `MEDIADASH_RUN_DIAGNOSTICS` environment switch.

The Diagnostics tab shows one table row per scanner: completion time, items found, and maximum sampled host/container CPU, disk I/O, network, RAM and GPU usage. I/O combines read/write throughput; network combines receive/send throughput. A dash means no valid measurement was available. Run a new diagnostic scan to populate the combined throughput peaks in older recordings.

The full recording remains available through the administrator-only `GET /MediaDash/Diagnostics/LastScan` endpoint while diagnostics is enabled. Stage records include periodic heartbeats, active operations and time since progress, and remain in the Jellyfin log under `MediaDash run diagnostics` when information logging is enabled. Error messages may include media paths.

CPU and memory identify their Jellyfin-process or host/container scope. GPU, disk, and network samples describe activity visible to the host or container. Other workloads can contribute, and local disk counters do not measure a remote NAS disk. The disk rate covers Windows physical disks in aggregate or Linux `sda`, including Docker when that counter is visible. Missing counters and phases too short to establish a rate are reported as unavailable.

The Overview displays each Windows physical disk separately, using the disk number and any drive letters exposed by Windows. On Linux and Docker it labels the `sda` tile directly.

## Baseline benchmark

The scanner did not saturate this machine on the controlled 1,000-file audio fixture. It still has **no dynamic CPU, RAM or disk-pressure backoff**. These measurements do not certify a 20 TB library, a remote NAS or behavior while another workload pins the system.

| Measurement | Plugin cache empty | Warm plugin cache |
| --- | ---: | ---: |
| Elapsed scan | 31.194 s | 0.556 s |
| Exact unwanted-audio findings | 1,000 | 1,000 |
| Whole-machine CPU, mean | 12.57% | 7.45% |
| Whole-machine CPU, sampled peak | 32.8% | 15.9% |
| Jellyfin plus observed descendants, peak resident memory | 423.22 MiB | 399.03 MiB |
| Whole-machine disk reads, mean | 0.30 MiB/s | 0.28 MiB/s |
| Whole-machine disk writes, mean | 3.30 MiB/s | 2.10 MiB/s |

The system had 16 logical CPUs and 32,419.7 MiB RAM. Available host memory averaged 17,349 MiB during the uncached scan. Whole-machine measurements include other machine activity. Process-tree sampling observes FFprobe descendants, but short-lived processes can exit between 100 ms samples; its separate CPU number is a lower bound and is not used to claim complete scanner CPU attribution. The warm run has only four samples, so its percentile estimates are weak. Disk counters cover the machine, not just Jellyfin, and do not measure NAS latency or queue saturation.

“Cache empty” means the MediaDash probe cache was verified empty. The operating-system file cache was not flushed. Jellyfin had already indexed the generated files. Native indexing was measured separately at 42.38 seconds with explicit empty metadata/image fetcher lists and generated NFO files. That phase overlapped unrelated .NET tests, so its host resource readings cannot be attributed solely to Jellyfin or compared directly with earlier fixture/provider configurations.

The first benchmark attempt configured the audio scanner before sampling. The existing configuration helper waited through an automatic targeted scan, making both subsequent explicit runs warm. Those raw logs remain available; the corrected run used the owned host's Reset API after configuration settled, proved empty probe cache, and verified that History and the move journal were unchanged. No queued work existed. No .NET test or build overlapped the corrected plugin measurements.

All 1,000 generated media files and 1,000 NFO files retained their original SHA-256 hashes after both scans. Both test hosts' settings, libraries and queues were restored and their owned processes stopped. The generated resource corpus was removed only after its resolved path, exact file allowlist and every hash were verified. No real-library share was accessed.

The installed candidate was Jellyfin 12.0.0, source fingerprint `882855c6a9ee54acdd704fa9fb7f7f5c9762cd9ce5a452ea36b982e489a45a8f`, DLL SHA-256 `6ab6886b2bfea252cbc67dd0b3762e603ee58bc5863f4c6ca05da2e5bf4ab210`. Later rename repairs are separate candidates.

Evidence root: [owned benchmark artifacts](../../artifacts/remaining-issue-repairs-worktree/artifacts/mover-owned-host-watch-baseline-20261030/). Corrected summaries and cleanup proof are in `v12/evidence/resources-plugin-uncached.json`, `resources-plugin-warm-corrected.json`, `resources-idle-corrected.json` and `resource-cleanup.json`; execution is recorded in `v12-resource-scan-corrected.log`.

Existing controls remain static: probe threads are capped at half the logical cores, subprocesses use below-normal priority, low-impact Linux scans can use `ionice`, and scheduled scans can defer for playback/native indexing. Manual Scan now bypasses idle/index deferral. The new per-root outstanding-stat budget prevents repeated unavailable-root probes from accumulating; it is not general resource-pressure throttling. Pinned-system acceptance and a measured large-library bottleneck remain open.
