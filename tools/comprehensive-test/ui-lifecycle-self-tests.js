// Node-runnable self-tests for the MediaDash config page lifetime factory.
// Run:  node tools/comprehensive-test/ui-lifecycle-self-tests.js
// Exits non-zero on any assertion failure so CI can wire it up cheaply.
//
// Loads every helper from the real extracted asset (Configuration/scripts/*.js) so tests
// exercise the same source configPage.html loads via <script src>. There is no inline
// duplicate to keep in lockstep after the 06-D runtime collapse on 2026-09-23.

'use strict';

var fs = require('fs');
var path = require('path');
var vm = require('vm');

// Resolve relative to this file so the tests work from any cwd. Repo root is two levels up:
//   tools/comprehensive-test/ui-lifecycle-self-tests.js  →  <repo>/Jellyfin.Plugin.MediaDash/Configuration/scripts/*.js
var scriptsDir = path.resolve(
    __dirname,
    '..', '..',
    'Jellyfin.Plugin.MediaDash', 'Configuration', 'scripts');
var sandboxWindow = {};
var sandbox = { window: sandboxWindow, globalThis: sandboxWindow };
function loadAsset(relative) {
    var full = path.join(scriptsDir, relative);
    vm.runInNewContext(fs.readFileSync(full, 'utf8'), sandbox, { filename: full });
}
[
    'page-lifetime.js',
    'api-client.js',
    'dom-helpers.js',
    'library-breakdown.js',
    'settings-mapper.js',
    path.join('tabs', 'history-tab.js'),
    path.join('tabs', 'recycle-tab.js'),
    path.join('tabs', 'issues-tab.js'),
    path.join('tabs', 'errors-tab.js')
].forEach(loadAsset);

// Fatal check every registration up front so a broken loader fails the whole run before
// any test claims to have covered a stub instead of the real source.
var md = sandboxWindow.MediaDash;
var expected = [
    'createPageLifetime', 'createApi', 'esc',
    'sanitizeDuplicateModeOnLoad', 'encoderConfigToValue', 'encoderValueToConfig', 'csv',
    'historyFailReasonOf', 'historyMatches',
    'binProvenanceChipClass', 'binRestoreVariant',
    'badgeText', 'issueMatchesFilters',
    'createErrorsTab', 'createRecycleTab', 'createHistoryTab', 'createIssuesTab',
    'fmt', 'bytes',
    'libIconFor', 'libStackBar', 'libLegend', 'libSegmentsFromMap', 'libCardHtml', 'donutSvg'
];
expected.forEach(function (n) {
    if (!md || typeof md[n] !== 'function') {
        console.error('FATAL: window.MediaDash.' + n + ' was not registered — check the matching asset file.');
        process.exit(2);
    }
});
// STR is a pure data literal, so it's checked separately from the function registrations
// above. Sample four keys from four different regions of the object (top-level string,
// nested types map, per-type field, languages array shape) so a truncated / re-typed
// extraction fails loudly. Full-value coverage lives with browser QA — the browser is
// the only place esc() and every STR consumer actually run.
if (!md || typeof md.STR !== 'object' || md.STR === null) {
    console.error('FATAL: window.MediaDash.STR was not registered — check dom-helpers.js.');
    process.exit(2);
}
if (typeof md.STR.tagline !== 'string' ||
    typeof md.STR.types !== 'object' ||
    md.STR.types.Duplicate.title !== 'Duplicate copies' ||
    !Array.isArray(md.STR.languages) ||
    md.STR.languages.length !== 47 ||
    !md.STR.languages.some(function (language) { return language[0] === 'orig' && language[1] === 'Original language'; })) {
    console.error('FATAL: window.MediaDash.STR shape unexpected — did dom-helpers.js get truncated?');
    process.exit(2);
}
var createPageLifetime = md.createPageLifetime;
var createApi = md.createApi;
var sanitizeDuplicateModeOnLoad = md.sanitizeDuplicateModeOnLoad;
var encoderConfigToValue = md.encoderConfigToValue;
var encoderValueToConfig = md.encoderValueToConfig;
var csv = md.csv;
var historyFailReasonOf = md.historyFailReasonOf;
var historyMatches = md.historyMatches;
var binProvenanceChipClass = md.binProvenanceChipClass;
var binRestoreVariant = md.binRestoreVariant;
var badgeText = md.badgeText;
var issueMatchesFilters = md.issueMatchesFilters;

var failures = 0;
function check(label, ok) {
    if (ok) { console.log('  ok  ' + label); return; }
    console.log('  FAIL ' + label);
    failures++;
}

// --- lifecycle token generation ---
(function tokenGenerations() {
    console.log('# token generations');
    var life = createPageLifetime();
    var old = life.start();
    check('start returns a positive generation', old > 0);
    check('current token accepted while active', life.accepts(old));
    life.stop();
    check('stop invalidates previously accepted token', !life.accepts(old));
    var fresh = life.start();
    check('restart produces a new generation', fresh !== old);
    check('old token rejected after restart', !life.accepts(old));
    check('new token accepted after restart', life.accepts(fresh));
})();

// --- duplicate start ---
(function duplicateStart() {
    console.log('# duplicate start');
    var life = createPageLifetime();
    var a = life.start();
    var b = life.start();
    check('duplicate start advances the generation', a !== b);
    check('the earlier token no longer accepts', !life.accepts(a));
    check('the latest token accepts', life.accepts(b));
})();

// --- cleanup disposal ---
(function cleanupDisposal() {
    console.log('# cleanup disposal');
    var life = createPageLifetime();
    life.start();
    var calls = [];
    life.own(function () { calls.push('a'); });
    life.own(function () { throw new Error('b throws'); });
    life.own(function () { calls.push('c'); });
    life.stop();
    check('all cleanups ran despite one throwing', calls.join(',') === 'a,c');
    life.start();
    life.stop();
    check('second stop after empty cleanups is safe', true);
})();

// --- hide during request: stale response is dropped ---
// 06-D.5: uses the extracted createApi(lifetime, apiClient) from api-client.js. Fake
// apiClient records calls so URL construction and lifetime gating are both testable.
function makeFakeApiClient(fetcher) {
    var calls = { getUrl: [], getJSON: [] };
    return {
        calls: calls,
        getUrl: function (path) { calls.getUrl.push(path); return 'http://host/' + path; },
        getJSON: function (url) { calls.getJSON.push(url); return fetcher(url); }
    };
}

function slowOldRequestAfterHide() {
    console.log('# slow request completing after hide');
    var life = createPageLifetime();
    var resolveIt;
    var pending = new Promise(function (res) { resolveIt = res; });
    var fake = makeFakeApiClient(function () { return pending; });
    var api = createApi(life, fake);
    life.start();
    var rendered = false;
    api('Status').then(function () { rendered = true; });
    life.stop();
    resolveIt({ ok: true });
    return new Promise(function (r) { setImmediate(r); }).then(function () {
        check('render did NOT run after stop', rendered === false);
        check('MediaDash/ prefix applied to getUrl', fake.calls.getUrl[0] === 'MediaDash/Status');
        check('getJSON received the constructed URL', fake.calls.getJSON[0] === 'http://host/MediaDash/Status');
    });
}

function slowOldRequestAfterRestart() {
    console.log('# slow old request completing after a fresh start');
    var life = createPageLifetime();
    var resolveOld, resolveNew;
    var oldPending = new Promise(function (res) { resolveOld = res; });
    var newPending = new Promise(function (res) { resolveNew = res; });
    var which = 'old';
    var fake = makeFakeApiClient(function () {
        return which === 'old' ? oldPending : newPending;
    });
    var api = createApi(life, fake);
    life.start();
    var oldRendered = false, newRendered = false;
    api('Status').then(function () { oldRendered = true; });
    life.stop();
    life.start();
    which = 'new';
    api('Status').then(function () { newRendered = true; });
    resolveOld({ ok: 'stale' });
    resolveNew({ ok: 'fresh' });
    return new Promise(function (r) { setImmediate(r); }).then(function () {
        check('stale old response did not render', oldRendered === false);
        check('fresh new response did render', newRendered === true);
        check('both requests routed through getJSON', fake.calls.getJSON.length === 2);
    });
}

function reopenOwnedOnce() {
    console.log('# owned cleanups run once per stop cycle');
    var life = createPageLifetime();
    life.start();
    var count = 0;
    life.own(function () { count++; });
    life.stop();
    life.stop();
    check('second stop does not re-run disposed cleanups', count === 1);
    life.start();
    life.stop();
    check('third stop after fresh start with no cleanups also safe', count === 1);
    return Promise.resolve();
}

function ownGlobalRoundTrip() {
    console.log('# ownGlobal round-trip: attach, dispose, re-attach');
    // Fake EventTarget for headless Node — no jsdom dependency.
    function fakeTarget() {
        var subs = [];
        return {
            listeners: function () { return subs.length; },
            addEventListener: function (type, handler, options) { subs.push({ type: type, handler: handler, options: options }); },
            removeEventListener: function (type, handler, options) {
                for (var i = 0; i < subs.length; i++) {
                    if (subs[i].type === type && subs[i].handler === handler) { subs.splice(i, 1); return; }
                }
            }
        };
    }
    function ownGlobal(life, target, type, handler, options) {
        target.addEventListener(type, handler, options);
        life.own(function () { target.removeEventListener(type, handler, options); });
    }
    var life = createPageLifetime();
    var doc = fakeTarget();
    life.start();
    ownGlobal(life, doc, 'click', function () {});
    ownGlobal(life, doc, 'keydown', function () {});
    check('two listeners attached after ownGlobal', doc.listeners() === 2);
    life.stop();
    check('all listeners removed at lifetime.stop()', doc.listeners() === 0);
    life.start();
    ownGlobal(life, doc, 'click', function () {});
    check('fresh listener attached on second start()', doc.listeners() === 1);
    life.stop();
    check('fresh listener removed on second stop()', doc.listeners() === 0);
    return Promise.resolve();
}

// ---------------------------------------------------------------------------
// History tab: pure helpers loaded from scripts/tabs/history-tab.js above.
// ---------------------------------------------------------------------------

(function historyPureHelpers() {
    console.log('# history: historyFailReasonOf');
    check('successful row → empty reason', historyFailReasonOf({ Success: true, Action: 'anything' }) === '');
    check('null row → empty reason', historyFailReasonOf(null) === '');
    check('failed row: permission denied', historyFailReasonOf({ Success: false, Action: 'Fix failed — Permission denied: /media/foo.mkv' }) === 'Permission denied');
    check('failed row: i/o error', historyFailReasonOf({ Success: false, Action: 'Fix failed — I/O error while writing' }) === 'I/O error');
    check('failed row: cross-device', historyFailReasonOf({ Success: false, Action: 'Fix failed — cross-device link forbidden' }) === 'Cross-device link');
    check('failed row: file gone', historyFailReasonOf({ Success: false, Action: 'Fix failed — Source no longer exists on disk' }) === 'File no longer exists');
    check('failed row: refused', historyFailReasonOf({ Success: false, Action: 'Fix failed — Refused because library guard fired' }) === 'Refused by safety guard');
    check('failed row: unknown → sentence-cased first phrase', historyFailReasonOf({ Success: false, Action: 'Fix failed — bespoke reason with more words' }) === 'Bespoke reason with more words');
    check('failed row: empty Action → Unspecified', historyFailReasonOf({ Success: false, Action: 'Fix failed — ' }) === 'Unspecified');

    console.log('# history: historyMatches');
    var succRow = { Success: true, WasDryRun: false };
    var failRow = { Success: false, Action: 'Fix failed — Permission denied.', WasDryRun: false };
    var dryRow = { Success: true, WasDryRun: true };
    check('succeeded matches "all"', historyMatches(succRow, 'all', '') === true);
    check('succeeded matches "succeeded"', historyMatches(succRow, 'succeeded', '') === true);
    check('succeeded not "failed"', historyMatches(succRow, 'failed', '') === false);
    check('succeeded not "dryrun"', historyMatches(succRow, 'dryrun', '') === false);
    check('failed matches "failed" (no reason filter)', historyMatches(failRow, 'failed', '') === true);
    check('failed matches "failed" (matching reason)', historyMatches(failRow, 'failed', 'Permission denied') === true);
    check('failed rejected by non-matching reason', historyMatches(failRow, 'failed', 'I/O error') === false);
    check('dry-run matches "dryrun"', historyMatches(dryRow, 'dryrun', '') === true);
    check('dry-run not "succeeded" (WasDryRun disqualifies)', historyMatches(dryRow, 'succeeded', '') === false);
})();

// ---------------------------------------------------------------------------
// Recycle bin tab: pure helpers loaded from scripts/tabs/recycle-tab.js above.
// ---------------------------------------------------------------------------

(function recyclePureHelpers() {
    console.log('# recycle: binProvenanceChipClass');
    check('numeric 1 → manual chip', binProvenanceChipClass({ Provenance: 1 }) === 'mdBinReasonChip mdBinReasonChip--manual');
    check('string "Manifest" → manual chip', binProvenanceChipClass({ Provenance: 'Manifest' }) === 'mdBinReasonChip mdBinReasonChip--manual');
    check('numeric 2 → orphan chip', binProvenanceChipClass({ Provenance: 2 }) === 'mdBinReasonChip mdBinReasonChip--orphan');
    check('string "Orphan" → orphan chip', binProvenanceChipClass({ Provenance: 'Orphan' }) === 'mdBinReasonChip mdBinReasonChip--orphan');
    check('numeric 0 (Auto) → base chip', binProvenanceChipClass({ Provenance: 0 }) === 'mdBinReasonChip');
    check('string "Auto" → base chip', binProvenanceChipClass({ Provenance: 'Auto' }) === 'mdBinReasonChip');
    check('missing Provenance → base chip', binProvenanceChipClass({}) === 'mdBinReasonChip');
    check('null row → base chip', binProvenanceChipClass(null) === 'mdBinReasonChip');

    console.log('# recycle: binRestoreVariant');
    var vHistory = binRestoreVariant({ HistoryId: 42, BinPath: '/bin/foo', OriginalPath: '/media/foo' });
    check('HistoryId wins over path fallback', vHistory.kind === 'history' && vHistory.historyId === 42);
    var vPath = binRestoreVariant({ BinPath: '/bin/foo', OriginalPath: '/media/foo' });
    check('path variant when both BinPath + OriginalPath present', vPath.kind === 'path' && vPath.binPath === '/bin/foo');
    check('BinPath alone (no OriginalPath) → none', binRestoreVariant({ BinPath: '/bin/foo' }).kind === 'none');
    check('OriginalPath alone (no BinPath) → none', binRestoreVariant({ OriginalPath: '/media/foo' }).kind === 'none');
    check('HistoryId === 0 (falsy) falls through', binRestoreVariant({ HistoryId: 0, BinPath: '/bin/foo', OriginalPath: '/media/foo' }).kind === 'path');
    check('empty object → none', binRestoreVariant({}).kind === 'none');
    check('null row → none', binRestoreVariant(null).kind === 'none');
})();

// ---------------------------------------------------------------------------
// Issues tab: pure helpers loaded from scripts/tabs/issues-tab.js above.
// ---------------------------------------------------------------------------

(function issuesPureHelpers() {
    console.log('# issues: badgeText (999+ cap)');
    check('0 → "0"', badgeText(0) === '0');
    check('1 → "1"', badgeText(1) === '1');
    check('999 → "999"', badgeText(999) === '999');
    check('1000 → "999+"', badgeText(1000) === '999+');
    check('50000 → "999+"', badgeText(50000) === '999+');

    console.log('# issues: issueMatchesFilters');
    var row = { Type: 'Playability', FileName: 'movie.mkv', Path: '/media/movies/movie.mkv' };
    check('no filters → true', issueMatchesFilters(row, '', '', '') === true);
    check('matching type filter → true', issueMatchesFilters(row, 'Playability', '', '') === true);
    check('non-matching type filter → false', issueMatchesFilters(row, 'Duplicate', '', '') === false);
    check('text match on FileName → true', issueMatchesFilters(row, '', 'movie', '') === true);
    check('text match on Path → true', issueMatchesFilters(row, '', 'movies', '') === true);
    check('text mismatch → false', issueMatchesFilters(row, '', 'zzzzz', '') === false);
    check('text filter case-insensitive → true', issueMatchesFilters(row, '', 'MOVIE', '') === true);

    // Language filter only affects AudioLanguage rows; non-audio rows pass through
    var audioRow = { Type: 'AudioLanguage', FileName: 'a.mkv', Path: '/a.mkv', DetailsJson: JSON.stringify({ removeLanguages: ['fre', 'ita'] }) };
    check('lang filter matches AudioLanguage row → true', issueMatchesFilters(audioRow, '', '', 'fre') === true);
    check('lang filter does NOT match AudioLanguage row → false', issueMatchesFilters(audioRow, '', '', 'eng') === false);
    check('lang filter set but row is Playability → pass through (true)', issueMatchesFilters(row, '', '', 'fre') === true);
    check('malformed DetailsJson on AudioLanguage row → false', issueMatchesFilters({ Type: 'AudioLanguage', DetailsJson: '{not json' }, '', '', 'fre') === false);
    check('AudioLanguage row with no DetailsJson + lang filter → false', issueMatchesFilters({ Type: 'AudioLanguage' }, '', '', 'fre') === false);

    // Combined filters
    check('combined type + text (both match) → true', issueMatchesFilters(row, 'Playability', 'movie', '') === true);
    check('combined type + text (type mismatch) → false', issueMatchesFilters(row, 'Duplicate', 'movie', '') === false);
})();

// --- 06-C.1: settings-mapping coercion helpers loaded from scripts/settings-mapper.js ---

(function settingsMapperHelpers() {
    console.log('# settings: sanitizeDuplicateModeOnLoad');
    check('legacy Automatic → ManualApprove', sanitizeDuplicateModeOnLoad('Automatic') === 'ManualApprove');
    check('ManualApprove passes through', sanitizeDuplicateModeOnLoad('ManualApprove') === 'ManualApprove');
    check('legacy DetectOnly → ManualApprove', sanitizeDuplicateModeOnLoad('DetectOnly') === 'ManualApprove');
    check('Off passes through', sanitizeDuplicateModeOnLoad('Off') === 'Off');

    console.log('# settings: encoderConfigToValue');
    check('(false, null) → cpu', encoderConfigToValue(false, null) === 'cpu');
    check('(false, 3) → cpu (CPU forgets stale gpu index)', encoderConfigToValue(false, 3) === 'cpu');
    check('(true, null) → auto', encoderConfigToValue(true, null) === 'auto');
    check('(true, 0) → "0" (GPU 0 is not treated as null)', encoderConfigToValue(true, 0) === '0');
    check('(true, 2) → "2"', encoderConfigToValue(true, 2) === '2');

    console.log('# settings: encoderValueToConfig');
    var cpu = encoderValueToConfig('cpu');
    check('cpu → useHw false, gpuIndex null', cpu.useHw === false && cpu.gpuIndex === null);
    var auto = encoderValueToConfig('auto');
    check('auto → useHw true, gpuIndex null', auto.useHw === true && auto.gpuIndex === null);
    var gpu0 = encoderValueToConfig('0');
    check('"0" → useHw true, gpuIndex 0', gpu0.useHw === true && gpu0.gpuIndex === 0);
    var gpu2 = encoderValueToConfig('2');
    check('"2" → useHw true, gpuIndex 2', gpu2.useHw === true && gpu2.gpuIndex === 2);

    console.log('# settings: encoder round-trip');
    var cases = [
        [false, null, 'cpu'],
        [true,  null, 'auto'],
        [true,  0,    '0'],
        [true,  1,    '1'],
        [true,  7,    '7']
    ];
    cases.forEach(function (c) {
        var v = encoderConfigToValue(c[0], c[1]);
        var back = encoderValueToConfig(v);
        check('round-trip (' + c[0] + ',' + c[1] + ') via "' + v + '"',
            v === c[2] && back.useHw === c[0] && back.gpuIndex === c[1]);
    });
    // Lossy path: (false, N) round-trips to (false, null). That is the documented
    // contract — CPU mode discards a stale gpu index.
    var lossyValue = encoderConfigToValue(false, 3);
    var lossyBack = encoderValueToConfig(lossyValue);
    check('lossy (false, 3) → cpu → (false, null)',
        lossyValue === 'cpu' && lossyBack.useHw === false && lossyBack.gpuIndex === null);
})();

// --- 06-C.2: csv (replaces parseLangList) — loaded from scripts/settings-mapper.js ---

(function csvHelper() {
    console.log('# settings: csv');
    check('empty string → []', JSON.stringify(csv('')) === '[]');
    check('null → []', JSON.stringify(csv(null)) === '[]');
    check('undefined → []', JSON.stringify(csv(undefined)) === '[]');
    check('single value → [value]', JSON.stringify(csv('mkv')) === '["mkv"]');
    check('a,b,c → [a,b,c]', JSON.stringify(csv('a,b,c')) === '["a","b","c"]');
    check('trims whitespace', JSON.stringify(csv('  a , b  ,c ')) === '["a","b","c"]');
    check('lowercases', JSON.stringify(csv('ENG,FRE,Ita')) === '["eng","fre","ita"]');
    check('drops empty entries between commas', JSON.stringify(csv('a,,b,,,c')) === '["a","b","c"]');
    check('all-empty → []', JSON.stringify(csv(',, , ,')) === '[]');
})();

// Dirty tracking executes the actual Settings implementation, including its apply guard.
function createSettingsDirtyHarness() {
    var code = fs.readFileSync(path.join(scriptsDir, 'tabs', 'settings-tab.js'), 'utf8');
    var applied = false;
    var context = {
        deps: {
            loadEnv: function () { return { then: function () {} }; },
            view: { querySelector: function () { return null; } },
            api: { configuration: function () { return { then: function (callback) { callback({}); return { catch: function () {} }; } }; } },
            lifetime: { timeout: function () {} }
        },
        populateLanguageDatalist: function () {}, populateGenreDatalist: function () {},
        applyConfigToForm: function () { applied = true; }, syncSliderLabels: function () {},
        loadLibraries: function () { return Promise.resolve(); },
        loadStaleExcludedLibraries: function () { return Promise.resolve(); },
        initTaskCardsAfterLoad: function () {}, mdConfigLoaded: false
    };
    vm.createContext(context);
    vm.runInContext(code.slice(code.indexOf('var mdSettingsDirty ='), code.indexOf('function installUnsavedGuardOnce()')), context);
    vm.runInContext(code.slice(code.indexOf('function loadSettings()'), code.indexOf('// Renders one checkbox')), context);
    return {
        isDirty: function () { return context.mdSettingsDirty; },
        markDirty: context.markSettingsDirty, markClean: context.markSettingsClean,
        shouldApplyServerConfig: function () { applied = false; context.loadSettings(); return applied; }
    };
}
(function dirtyTracker() {
    console.log('# dirty tracking: contract');
    var d = createSettingsDirtyHarness();
    check('fresh tracker starts clean', d.isDirty() === false);
    check('fresh tracker permits server config apply', d.shouldApplyServerConfig() === true);
    d.markDirty();
    check('markDirty flips isDirty true', d.isDirty() === true);
    check('dirty tracker BLOCKS server config apply — the tab-back-clobber invariant', d.shouldApplyServerConfig() === false);
    d.markClean();
    check('markClean flips isDirty back to false', d.isDirty() === false);
    check('clean tracker permits server config apply again', d.shouldApplyServerConfig() === true);
    // A save that fails should NOT flip clean — the edits remain unsaved.
    d.markDirty();
    // (simulate a save-failed path where markClean is never called)
    check('failed save keeps dirty flag set (contract: only markClean clears it)', d.isDirty() === true);
})();

Promise.resolve()
    .then(slowOldRequestAfterHide)
    .then(slowOldRequestAfterRestart)
    .then(reopenOwnedOnce)
    .then(ownGlobalRoundTrip)
    .then(function () {
        if (failures > 0) {
            console.log('\n' + failures + ' assertion(s) failed.');
            process.exit(1);
        }
        console.log('\nAll assertions passed.');
    });
