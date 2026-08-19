# MediaDash Docs + Migration Audit — 2026-08-19

Independent audit ahead of 1.0. Docs completeness + upgrade path + legacy compat surface.

Status legend: `[ ]` open, `[x]` fixed, `[!]` disputed / not-a-bug.

---

## Part 1 — Documentation

- [x] **D1. `Configuration/PluginConfiguration.cs:51` — `AnalyticsEnabled = true` default contradicts every user-facing string.**
  Constructor sets `AnalyticsEnabled = true`. XML doc at line 187, i18n string `settings.safety.analyticsHint` (en.json:331), and pre-wizard checkbox at `configPage.html:6666` all say "Off by default." In practice `AnalyticsInstallId` gates the actual POST so nothing leaks, but the toggle sits ON while every visible string says it doesn't. Single biggest launch-blocking mismatch.
  **Fix:** `AnalyticsEnabled = false;` in the constructor. Wizard opt-in path already mints the UUID via `applyAnalyticsToggle`.

- [x] **D2. `Configuration/i18n/{de,es,fr,it,nl,pt-BR,ru,zh-CN}.json` — 2 keys missing.**
  `html.settings.safety.analytics` and `html.settings.safety.analyticsHint` are absent from every non-English locale; `I18nCatalog.GetHtml` falls back to English inline with translated surrounding copy.
  **Fix:** add the keys with either translations or English fallback + `//` note. Machine-translated seeds are acceptable per project convention.

- [x] **D3. `README.md:92` — stale "(new in v0.9.1)" tag.**
  For a 1.0 release the tag is either wrong or redundant.
  **Fix:** drop the tag.

- [ ] **D4. `manifest.json` — no 1.0.0 entry yet.**
  Top entry is `0.9.9.4`. Run `tools/release.ps1 -Version 1.0.0 -Changelog "…"` — this IS the release action.

- [x] **D5. `CONTRIBUTING.md:6-12` — .NET SDK version pin missing.**
  Says ".NET 9 SDK"; csproj enforces `TreatWarningsAsErrors=true` + `AnalysisMode=AllEnabledByDefault`. Analyzer drift between old and new SDKs will fail CI locally.
  **Fix:** add a line noting .NET 9.0.11+ SDK.

- [x] **D6. `README.md:25-36` — install-from-catalog step missing "refresh" hint.**
  If a Jellyfin 12.0 user's catalog is cached, MediaDash may not appear; needs manual refresh.
  **Fix:** append "If MediaDash doesn't appear, refresh the MediaDash repository — Jellyfin caches catalog manifests for 6 hours." to step 2.

- [!] **D7. `configPage.html:4335, 4342` — mentions "pre-0.9.9 subtitle-fixer bug".**
  Legitimate context for existing redownload-warning rows. Fresh 1.0 installs won't see it. Leave for now.

- [!] **D8. Screenshots present and current** (`docs/overview.png` etc.).
- [!] **D9. CODE_OF_CONDUCT + LICENSE present and correct.**
- [!] **D10. No TODO/FIXME/placeholder text found in source or i18n.**

## Part 2 — Upgrade path

- [x] **U1. `Data/MediaDashDb.cs:103-184` — `MigrateSchema` not wrapped in transaction.**
  Each step is individually idempotent (invariant that makes crash-mid-migration safe), but a future refactor could break that guarantee silently. Migration is safe *in practice* today.
  **Fix (lazy):** add a top-of-method comment: `// Each step below is idempotent by design; PRAGMA user_version is only advanced after all steps succeed, so a crashed migration re-runs cleanly on next boot.` Alternative: wrap in BEGIN/COMMIT (bigger diff).

- [!] **U2. `format_probe_cache` created only in initial CREATE block, not versioned migration.**
  Works today because `CREATE TABLE IF NOT EXISTS` runs unconditionally at every connection. Add a warning comment so future contributors don't break the pattern.

- [!] **U3. Config schema — no removed settings still referenced.** Only `ScheduledFixTime` deprecated + documented as retained-for-deserialization. Clean.

- [x] **U4. `Api/MediaDashController.cs:1065` — `PostV12CleanupCompleted = true` set even on partial failure.**
  If `RunAsync()` returns with `result.Errors.Count > 0`, the completion flag still flips true → user can never retry the failed folders through the UI.
  **Fix:** only set the flag when `result.Errors.Count == 0`.

- [!] **U5. Migrations from 0.1 / 0.5 / 0.7 / 0.9 all trace cleanly.** Verified.

## Part 3 — Legacy compat

- [!] **L1. `Compat/SkiaSharpBridge.cs` + `SkiaSharpDecodeResult.cs` — KEEP.**
  Load-bearing for the "one binary, both Jellyfin 10.11 and 12.0" claim. Only inlineable when the plugin drops 10.11 support or SkiaSharp 4 becomes universal — neither in the 1.0 window.

- [!] **L2. No other legacy shims in `Compat/`.** Clean.

## 1.0 readiness call

**Not ready today.** Minimum bar to launch:

1. D1 (AnalyticsEnabled default) — 1 word, blocks launch
2. D2 (locale keys) — 2 keys × 8 locales, ~5 min
3. D3 (README tag) — 10 seconds
4. D4 (version bump) — the release action itself
5. U1 (migration comment) — 2 min
6. U4 (post-upgrade flag on error) — 3 min

Everything below can ship in 1.0.1.
