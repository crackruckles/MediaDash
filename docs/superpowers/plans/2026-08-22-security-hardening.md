# Security Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent a misconfigured recycle-bin root from deleting unrelated directories, make analytics affirmative opt-in, and reduce path-substitution exposure without overstating portable guarantees.

**Architecture:** Treat only MediaDash-created timestamp/GUID batch directories as purgeable recycle-bin content. Keep the default safe operational modes, change telemetry defaults and wizard behavior to opt-in, and add a final containment recheck at mutation boundaries as defense in depth while documenting that untrusted local writers must not share library roots.

**Tech Stack:** C# 13, .NET 9, xUnit, embedded HTML/JavaScript Jellyfin configuration page.

**Spec:** Codex Security scan `scan_mediadash_20260822` and repository safety invariants in `CONTRIBUTING.md`.

## Global Constraints

- Preserve Jellyfin 10.11 and 12.0 compatibility.
- Keep valid custom recycle-bin folders supported.
- Never recursively delete a directory that MediaDash did not create as a recycle batch.
- Do not claim a portable pathname recheck fully eliminates CWE-367.
- Do not add dependencies.

---

### Task 1: Recycle-bin batch ownership

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Fixers/RecycleBin.cs`
- Create: `Jellyfin.Plugin.MediaDash.Tests/RecycleBinSafetyTests.cs`

**Interfaces:**
- Produces: `internal static bool IsMediaDashBatchDirectory(string path)` used by `EmptyAll`, `Purge`, and `ListContents`.

- [ ] Write xUnit cases proving arbitrary child folders are rejected and `yyyyMMdd-HHmmss-fff-<8 hex>` batches are accepted.
- [ ] Run the focused tests and verify they fail because the classifier does not exist.
- [ ] Implement the strict classifier and filter every destructive/listing enumeration through it.
- [ ] Run the focused tests and verify they pass.

### Task 2: Affirmative analytics opt-in

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs`
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/configPage.html`
- Modify: `docs/PRIVACY.md`
- Create: `Jellyfin.Plugin.MediaDash.Tests/PluginConfigurationPrivacyTests.cs`

**Interfaces:**
- Produces: new installations with `AnalyticsEnabled == false` and no UUID unless the user checks the opt-in control.

- [ ] Write an xUnit test proving a new configuration disables analytics and has no install UUID.
- [ ] Run it and verify the current default makes it fail.
- [ ] Change the configuration default, unchecked wizard state, first-run initialization, and skip behavior.
- [ ] Update privacy wording from opt-out/on-by-default to opt-in/off-by-default.
- [ ] Run the focused test and verify it passes.

### Task 3: Path mutation defense in depth

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Api/FileBrowserController.cs`
- Modify: `README.md`

**Interfaces:**
- Consumes: existing `TryResolveInsideLibrary(string?, out string, out ActionResult)`.
- Produces: a second containment/reparse-point check immediately before file-browser mutation sinks.

- [ ] Add a shared pre-mutation revalidation helper and apply it immediately before rename, move, copy, delete, upload finalization, and download open.
- [ ] Document that library roots must not be writable by untrusted local/container principals because portable path checks cannot make pathname operations race-free.
- [ ] Build and run file-browser/library-guard tests.

### Task 4: Verification and PR

**Files:**
- Review: all files changed by Tasks 1–3.

- [ ] Inspect the final diff for unrelated changes and challenge alternate destructive paths.
- [ ] Run `dotnet build Jellyfin.Plugin.MediaDash.sln --configuration Release`.
- [ ] Run `dotnet test Jellyfin.Plugin.MediaDash.sln --configuration Release --no-build`.
- [ ] Request independent code review and resolve Critical or Important findings.
- [ ] Commit, push the branch, and create a PR using `.github/pull_request_template.md`.
