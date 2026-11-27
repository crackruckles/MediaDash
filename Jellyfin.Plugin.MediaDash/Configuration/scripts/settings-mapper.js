// MediaDash settings coercion helpers. Extracted from configPage.html in 06-D.7 as the
// third real asset. Same half-step pattern: self-tests exercise this file; configPage.html
// keeps its inline copies until live-QA proves the /MediaDash/Asset/ loading mechanism.
//
// All four helpers are pure functions with no DOM dependency, safe to load in a Node vm
// sandbox with a `window = {}` shim.
(function () {
    'use strict';
    var ns = (typeof window !== 'undefined') ? window : globalThis;
    ns.MediaDash = ns.MediaDash || {};

    // Duplicate.Automatic is disabled while the confidence-ladder rework stabilises. A
    // legacy config that saved "Automatic" would leave the mode segments with no active
    // button AND auto-queue server-side on the next fix run. Coerce to ManualApprove on
    // load; save writes the sanitised value back.
    // Also coerces pre-1.0.9 DetectOnly tombstones so the segmented control shows a live
    // selection — Plugin.NormaliseFixModes rewrites the server copy on next save.
    ns.MediaDash.sanitizeDuplicateModeOnLoad = function sanitizeDuplicateModeOnLoad(mode) {
        if (mode === 'Automatic' || mode === 'DetectOnly') return 'ManualApprove';
        return mode;
    };

    // Encoder select value ↔ config round-trip. Documented contract:
    //   (useHw=false, gpuIndex=X)   → 'cpu'  → (false, null)    // CPU forgets stale index
    //   (useHw=true,  gpuIndex=null)→ 'auto' → (true, null)
    //   (useHw=true,  gpuIndex=N)   → String(N) → (true, N)
    ns.MediaDash.encoderConfigToValue = function encoderConfigToValue(useHw, gpuIndex) {
        if (!useHw) return 'cpu';
        if (gpuIndex == null) return 'auto';
        return String(gpuIndex);
    };

    ns.MediaDash.encoderValueToConfig = function encoderValueToConfig(encVal) {
        if (encVal === 'cpu') return { useHw: false, gpuIndex: null };
        if (encVal === 'auto') return { useHw: true, gpuIndex: null };
        var index = parseInt(encVal, 10);
        return { useHw: true, gpuIndex: Number.isNaN(index) ? null : index };
    };

    // Parse a comma-separated string into a trimmed, lowercased, blank-stripped array.
    // Null/undefined → []. Used for both file-type lists (ReencodeFileTypes) and language
    // lists (AllowedAudioLanguages, AllowedSubtitleLanguages, wizard equivalents).
    ns.MediaDash.csv = function csv(value) {
        return (value || '').split(',').map(function (s) { return s.trim().toLowerCase(); }).filter(function (s) { return s.length > 0; });
    };
})();
