# #89: free space on the output filesystem

27 October 2026. Local source repair; no production access, deployment, commit, push or release.

## Root cause and change

`FreeSpaceGuard` reduced every Unix source path to `/` before querying space. A rebuild on a separate media mount was therefore gated by the container root filesystem. The same logic passed Windows UNC roots to `DriveInfo`, which rejects them before measuring anything. Both faults were reproduced before the repair.

The guard now queries the existing source directory, where the fixers create their sibling output files. Unix uses `DriveInfo` with that directory, so filesystem queries follow mounts and directory links. Windows uses `GetDiskFreeSpaceExW` with the directory, which supports UNC paths, directory mount points and caller quotas. All callers use the same guard. The two-times-source requirement (three times for re-encode-enabled playability repair) and refusal on failed measurements remain intact.

Measured shortages report the output directory, source size, multiplier, required space and available space. Failed measurements say they could not determine free space rather than claiming the disk is full. Summary bucketing recognizes both the old wording and `Not enough free disk space`.

## Verification

- Release solution build: **zero warnings/errors**. Local Windows DLL SHA-256: `3A212C873F485259B93FA5BEE9F874F97655B591E5AAFAAB546FD6603E419E43`.
- Windows targeted tests: **26 passed**, zero failed/skipped. `Jellyfin.Plugin.MediaDash.Tests/TestResults/issue89-targeted.trx`.
- Actual writable UNC path via `\\localhost\C$`, restricted to the owned artifact directory: **1 failed before**, **1 passed after**; source bytes preserved. `Jellyfin.Plugin.MediaDash.Tests/TestResults/issue89-before.trx` and `issue89-unc-after.trx`. This is local SMB, not a remote NAS or service-account test.
- Ubuntu WSL, private mount namespace: the root filesystem had approximately 950 GiB available, a separate tmpfs 2 TiB, and another tmpfs 64 MiB. Sparse source lengths deliberately put the root filesystem and media filesystem on opposite sides of the required-space boundary. Both 2x and 3x checks: **4 failed before**, **4 passed after**, with source length and marker byte preserved. A further run through bind mounts of those filesystems: **4 passed**. The namespaces exited and their mounts disappeared. Evidence: `artifacts/issue89-local-smb/issue89-linux-before.trx`, `issue89-linux-after.trx`, `issue89-linux-bind-after.trx`; `run-linux-mount-check.sh` records the local setup. Sparse lengths test the gate; they are not large-media encodes.
- Whole offline .NET suite: **1,719 executed, 1,714 passed, 5 failed, zero skipped**, `Jellyfin.Plugin.MediaDash.Tests/TestResults/issue89-whole-tree.trx`. The five failures match the previously documented blockers: `Issue86_ApprovedManagedMetadataReachesItsSpecializedFixer` (OrphanedDebris, selected-library false/true), `Issue43_CompanionCollisionMustNotReportACompletedMove`, `Reddit_RefusedMoveStaysVisibleAndDoesNotClaimACompletedFix`, and `Issue92_OutputHeaderFailureMustNotBeClassifiedAsSourceCorruption`.

Commands for the ordinary local checks:

```powershell
dotnet test Jellyfin.Plugin.MediaDash.Tests -c Release --filter 'FullyQualifiedName~FreeSpaceGuardTests|FullyQualifiedName~BucketReasonContractTests|FullyQualifiedName~PlayabilityFixerRepairTests'
dotnet test Jellyfin.Plugin.MediaDash.Tests -c Release --filter 'Category!=ReportedIssueExternal'
```

`ReportedMountedSpaceAcceptanceTests` is explicitly external. On an owned Linux host or private mount namespace, set `MEDIADASH_TEST_ROOMY_MOUNT` to an existing writable filesystem with more free space than `/`, and `MEDIADASH_TEST_SMALL_MOUNT` to one with less. Run `dotnet test Jellyfin.Plugin.MediaDash.Tests -c Release --filter FullyQualifiedName~ReportedMountedSpaceAcceptanceTests`. Each case creates a unique sparse fixture, checks the real guard and removes that fixture. Missing prerequisites fail, never skip. Windows UNC acceptance retains its existing explicit environment setup in [the gate instructions](REPORTED-ISSUE-PUSH-GATES.md).

## Limits

The existing remote NAS library (`\\192.168.1.125\media`) is read-only for acceptance work. It can be queried for available space, probed, or copied to an owned local fixture directory. Use a separate owned writable target for `ReportedUncAcceptanceTests`, `ReportedMountedSpaceAcceptanceTests`, re-encode, recycle, and restore. See the [5 November read-only NAS results](PUSH-PREPARATION-2026-11-05.md#nas-read-only-checks).

No actual production LXC mount, production file, remote NAS, service-account quota, or Jellyfin encode/restore workflow was exercised for this change. The source defect and its repair are established by the real filesystem checks above; confirmation of this user's exact failure still requires a rebuilt plugin and the affected mount's measurements inside that LXC. Existing servers were not restarted. The earlier frozen candidate and its source-bound acceptance records do not certify this changed tree. **Do not push or publish: the existing five whole-tree failures and other acceptance gaps remain.**
