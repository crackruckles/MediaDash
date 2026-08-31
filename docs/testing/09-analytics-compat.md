# 09 · Analytics & Compat

`Analytics/AnalyticsReporter.cs` (relies on `AnalyticsInstallIdTests`) and
`Compat/SkiaSharpBridge.cs`.

Return to [INDEX](INDEX.md).

---

## Session prep

- [ ] **P.1** `$env:TOKEN` set.
- [ ] **P.2** Confirm analytics setting default: Settings → Advanced →
      Analytics. Record current state.
- [ ] **P.3** For network capture, run Fiddler or `Get-NetTCPConnection`
      snooping on outbound HTTPS traffic during analytics push windows.

---

## 09-A · AnalyticsReporter

Optional, opt-in telemetry. Reports aggregate issue counts and install
lifecycle.

### Opt-out (default)
- [ ] **A.1** With analytics **disabled** in settings, restart Jellyfin.
      Watch outbound traffic for 5 minutes after startup and after a scan.
      No outbound calls from Jellyfin to any analytics endpoint.
- [ ] **A.2** Log has no `AnalyticsReporter: sent` line.

### Opt-in
- [ ] **A.3** Enable analytics; save. Restart Jellyfin. Within initial
      report window (see reporter interval in code), one POST fires to
      the analytics endpoint. Confirm URL in log.
- [ ] **A.4** Payload keys match `p_duplicate`, `p_playability`,
      `p_quality`, `p_subtitle`, `p_audio`, `p_misplaced`, `p_missing_subs`,
      `p_stale`, `p_corrupt_artwork`, `p_suspicious`, `p_ungrouped`,
      `p_large_trickplay` (and any others in `AnalyticsReporter.cs`).
- [ ] **A.5** No PII in payload: no file paths, no library names, no
      usernames. Confirm by inspecting captured request body.

### Install ID (relies on `AnalyticsInstallIdTests`)
- [ ] **A.6** First-run install ID generated and persisted. Restart —
      same ID re-sent.
- [ ] **A.7** Delete the install ID file (or DB row). Restart — a new,
      different ID generated.
- [ ] **A.8** IDs are opaque UUIDs, not derived from any user/machine
      identifier (verify format matches `AnalyticsInstallIdTests`).

### Failure isolation
- [ ] **A.9** Block outbound HTTPS → reporter logs a warn, does not
      throw, plugin continues functioning.
- [ ] **A.10** Endpoint returns 500 → same behaviour.
- [ ] **A.11** Endpoint returns 200 but with garbage body → same
      behaviour.

### Aggregation window
- [ ] **A.12** Two scans in same window → single report sends latest
      aggregate (no duplicate sends).
- [ ] **A.13** Long-running install (leave server up 24 h) → daily send
      cadence honoured; no drift.

### Runtime toggling
- [ ] **A.14** Disable analytics mid-session → next scheduled send does
      not fire; buffer discarded (or held until re-enabled — verify
      documented behaviour).

---

## 09-B · SkiaSharpBridge (Compat/)

Wraps SkiaSharp for decoding artwork. Cross-platform compat shim.

### Positive
- [ ] **B.1** Feed a valid PNG through the bridge (via any scanner path
      that decodes artwork, e.g. 01-A). `SkiaSharpDecodeResult` populated
      with width, height, and no error.
- [ ] **B.2** JPG, WebP inputs also decode.
- [ ] **B.3** SVG rejected (not supported) with clear error string
      (should not throw).

### Failure
- [ ] **B.4** Zero-byte file → result with `error` populated, no throw.
- [ ] **B.5** Truncated file → result with `error`, no throw.
- [ ] **B.6** SkiaSharp native lib missing (rename its .dll temporarily):
      bridge falls back to error result with explanatory message; scanner
      logs it as one artwork error, NOT as a plugin crash.

### Cross-platform
- [ ] **B.7** On Windows, uses native `libSkiaSharp.dll` from plugin
      folder. Confirm via ProcessHacker / Handle.exe.
- [ ] **B.8** On Linux/macOS test host (if available), native `so`/`dylib`
      picked up.

### Memory
- [ ] **B.9** Decode 100 large images in a loop. No unbounded memory
      growth (monitor Jellyfin RSS — should return to baseline within one
      GC cycle after the batch).

---

## End-of-chapter cleanup

- [ ] **Z.1** Restore SkiaSharp native lib if you renamed it.
- [ ] **Z.2** Set analytics setting back to your normal state.
- [ ] **Z.3** `Reset`.
- [ ] **Z.4** Update INDEX progress.
