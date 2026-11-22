'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');
const config = path.resolve(__dirname, '../../Jellyfin.Plugin.MediaDash/Configuration');
const html = fs.readFileSync(path.join(config, 'configPage.html'), 'utf8');
function source(name) {
    const file = path.join(config, 'scripts', name);
    return fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : html;
}
function element() {
    const handlers = new Map();
    return {
        handlers,
        addEventListener(name, fn) { if (!handlers.has(name)) handlers.set(name, new Set()); handlers.get(name).add(fn); },
        removeEventListener(name, fn) { if (handlers.has(name)) handlers.get(name).delete(fn); },
        fire(name, ev) { for (const fn of handlers.get(name) || []) fn(ev); }
    };
}

test('Fix order dialog keeps repairs before filename changes and folder moves, including saved orders', () => {
    const list = { innerHTML: '', attributes: {}, querySelectorAll: () => [], setAttribute(key, value) { this.attributes[key] = value; } };
    const warning = { innerHTML: '', style: {} };
    const ctx = { window: {}, deps: { STR: { types: {} }, FIXER_HARD_CONSTRAINTS: [],
        view: { querySelector: selector => selector === '#mdFixOrderList' ? list : warning } } };
    vm.runInNewContext(source('dom-helpers.js'), ctx);
    const overview = source('tabs/overview-tab.js');
    const start = overview.indexOf('function renderFixOrderList(');
    const end = overview.indexOf('function saveFixOrder()', start);
    vm.runInNewContext(overview.slice(start, end), ctx);
    ctx.renderFixOrderList(['Misplaced', 'FormatCompatibility', 'SubtitleFonts', 'Ungrouped', 'Quality'], []);
    assert.equal(list.attributes['data-order'], 'SubtitleFonts,FormatCompatibility,Quality,Misplaced,Ungrouped');
    const rows = list.innerHTML.split('<div class="mdOrderRow"').slice(1);
    assert.match(rows[0], /mdFixOrderDown" disabled/);
    assert.match(rows[1], /mdFixOrderUp" disabled/);
    assert.match(rows[2], /mdFixOrderDown" disabled/);
    assert.match(rows[3], /mdFixOrderUp" disabled/);
    assert.doesNotMatch(rows[3], /mdFixOrderDown" disabled/);
});

function issuesEnvironment(rescan = () => {}) {
    const nodes = new Map(), pending = [];
    const node = selector => {
        if (!nodes.has(selector)) nodes.set(selector, Object.assign(element(), {
            style: {}, dataset: {}, value: '', textContent: '', innerHTML: '', disabled: false,
            classList: { add() {}, remove() {} }, querySelector: node,
            querySelectorAll: selector => selector === '.mdIssueRescan' ? [node(selector)] : [],
            getAttribute(key) { return this[key]; }, setAttribute(key, value) { this[key] = value; }, removeAttribute(key) { delete this[key]; }
        }));
        return nodes.get(selector);
    };
    const ctx = { window: { MediaDash: { esc: String } } };
    vm.runInNewContext(source('dom-helpers.js'), ctx);
    ctx.window.MediaDash.esc = String;
    vm.runInNewContext(source('tabs/issues-tab.js'), ctx);
    const tab = ctx.window.MediaDash.createIssuesTab({
        root: { querySelector: node, querySelectorAll: () => [] },
        api: url => new Promise((resolve, reject) => pending.push({ url, resolve, reject })),
        strings: ctx.window.MediaDash.STR,
        lifetime: { current: () => 1 }, fmt: ctx.window.MediaDash.fmt, bytes: ctx.window.MediaDash.bytes,
        fileExtOf: () => 'mkv', mediaCategoryOf: () => 'video', updateTabBadge() {}, rescan
    });
    return { tab, node, pending };
}

test('Active scan fast polling avoids database and recycle queries between cold refreshes', () => {
    const code = source('page-controller.js');
    const start = code.indexOf('function refresh(silent)');
    const end = code.indexOf('// Shared config cache', start);
    assert(start >= 0 && end > start);
    const calls = [];
    const ctx = {
        _refreshTick: 0, _postUpgradeChecked: true, _postUpgradeAvailable: false,
        _backgroundStatus: null, _statusRefreshPending: false,
        overviewTab: { lastStatus: { IsScanning: true, IsFixing: false } },
        api: url => { calls.push(url); return new Promise(() => {}); },
        view: { querySelector: () => ({ style: {}, getAttribute: () => 'overview' }) }
    };
    vm.runInNewContext(code.slice(start, end), ctx);
    for (let tick = 0; tick < 4; tick++) ctx.refresh(true);
    assert.deepEqual(calls, Array(4).fill('Status/Progress'));
    ctx.refresh(true);
    assert.deepEqual(calls.slice(4), ['Status', 'Status/Progress', 'Errors/Count', 'RecycleBin']);
    ctx.refresh(true);
    assert.equal(calls.at(-1), 'Status/Progress');
});

test('Fix resource readings stay live while a slow full status finishes in the background', async () => {
    const code = source('page-controller.js');
    const start = code.indexOf('function refresh(silent)');
    const end = code.indexOf('// Shared config cache', start);
    const pending = [];
    const ctx = {
        _refreshTick: 4, _postUpgradeChecked: true, _postUpgradeAvailable: false,
        _backgroundStatus: null, _statusRefreshPending: false,
        overviewTab: { lastStatus: { IsFixing: true, OpenIssueTotal: 21, System: { CpuPercent: 1 } },
            renderStatus(status) { this.lastStatus = status; } },
        api: url => new Promise(resolve => pending.push({ url, resolve })),
        view: { querySelector: () => ({ style: {}, getAttribute: () => 'overview' }) }
    };
    vm.runInNewContext(code.slice(start, end), ctx);
    ctx.refresh(true);
    assert.equal(pending[0].url, 'Status');
    assert.equal(pending[1].url, 'Status/Progress');
    pending[1].resolve({ IsFixing: true, CurrentActivity: 'second episode', System: { CpuPercent: 55 } });
    await new Promise(setImmediate);
    assert.equal(ctx.overviewTab.lastStatus.System.CpuPercent, 55);
    pending[0].resolve({ IsFixing: false, CurrentActivity: 'old episode', OpenIssueTotal: 20, System: { CpuPercent: 2 } });
    await new Promise(setImmediate);
    ctx.refresh(true);
    pending.at(-1).resolve({ IsFixing: true, CurrentActivity: 'third episode', System: { CpuPercent: 70 } });
    await new Promise(setImmediate);
    assert.equal(ctx.overviewTab.lastStatus.OpenIssueTotal, 20);
    assert.equal(ctx.overviewTab.lastStatus.IsFixing, true);
    assert.equal(ctx.overviewTab.lastStatus.CurrentActivity, 'third episode');
    assert.equal(ctx.overviewTab.lastStatus.System.CpuPercent, 70);
});

test('Blocked detected and queued issues explain settings instead of promising a fix', async () => {
    const e = issuesEnvironment();
    const issue = { Id: 1, Type: 'Quality', Status: 'Detected', Path: '/movie.mkv', FileName: 'movie.mkv', DetailsJson: '{}', FixBlockedReason: 'Detect only is read-only. Choose Ask me first.', Warnings: [] };
    e.tab.activate(); e.pending[0].resolve([issue]); await new Promise(setImmediate);
    assert.match(e.node('#mdIssueList').innerHTML, /Detect only is read-only/);
    assert.match(e.node('#mdIssueList').innerHTML, /mdIssueSettings/);
    assert.doesNotMatch(e.node('#mdIssueList').innerHTML, /class="[^"]*mdApprove/);
    assert.equal(e.node('#mdApproveAll').style.display, 'none');
    assert.equal(e.tab.filteredIds(true).length, 0);
    e.tab.activate(); e.pending[1].resolve([{ ...issue, Status: 'Queued' }]); await new Promise(setImmediate);
    assert.doesNotMatch(e.node('#mdIssueList').innerHTML, /Queued for next fix run/);
    assert.match(e.node('#mdIssueList').innerHTML, /Detect only is read-only/);
    e.tab.activate(); e.pending[2].resolve([{ ...issue, FixBlockedReason: null }]); await new Promise(setImmediate);
    assert.match(e.node('#mdIssueList').innerHTML, /class="[^"]*mdApprove/);
    assert.deepEqual(Array.from(e.tab.filteredIds(true)), [1]);
});

test('Legacy move findings offer a scan instead of settings or approval', async () => {
    const scans = [];
    const e = issuesEnvironment(type => scans.push(type));
    e.tab.activate();
    e.pending[0].resolve([{ Id: 1, Type: 'Ungrouped', Status: 'Detected', Path: '/tv/show',
        FileName: 'show', DetailsJson: '{}', FixRequiresRescan: true,
        FixBlockedReason: 'Re-scan this item before approval so you can review its complete, current move preview.' }]);
    await new Promise(setImmediate);
    const markup = e.node('#mdIssueList').innerHTML;
    assert.match(markup, /mdIssueRescan/);
    assert.match(markup, /Scan now/);
    assert.doesNotMatch(markup, /mdIssueSettings|class="[^"]*mdApprove/);
    assert.equal(e.tab.filteredIds(true).length, 0);
    e.node('.mdIssueRescan').setAttribute('data-type', 'Ungrouped');
    e.node('.mdIssueRescan').fire('click');
    assert.deepEqual(scans, ['Ungrouped']);
});

test('Issue rescan uses the existing scan flow and never cancels an active scan', () => {
    const code = source('page-controller.js');
    const start = code.indexOf('rescan: function (type) {');
    const end = code.indexOf('\n                        },', start);
    const calls = [];
    const ctx = { showTab: tab => calls.push(tab), requestScan: targeted => calls.push(targeted), overviewTab: { busy: {}, lastStatus: {} },
        view: { querySelector: () => ({ disabled: false }) } };
    const rescan = vm.runInNewContext('({' + code.slice(start, end) + '\n}}).rescan', ctx);
    rescan();
    assert.deepEqual(calls, ['overview', false]);
    calls.length = 0;
    rescan('Ungrouped');
    assert.deepEqual(calls, ['overview', true]);
    calls.length = 0;
    ctx.overviewTab.lastStatus.IsScanning = true;
    rescan();
    assert.deepEqual(calls, ['overview']);
    calls.length = 0;
    ctx.overviewTab.lastStatus.IsScanning = false;
    ctx.overviewTab.busy.scan = true;
    rescan();
    assert.deepEqual(calls, ['overview']);
});

test('Ungrouped rescans save dirty settings and use only the ungrouped endpoint', async () => {
    const code = source('page-controller.js');
    const start = code.indexOf('function requestScan(scope)');
    const end = code.indexOf('function schedulePoll()', start);
    assert(start >= 0 && end > start);
    const calls = [];
    let save;
    const ctx = { overviewTab: { busy: {}, setProgress() {} }, STR: { scanStarting: 'Scanning' },
        view: { querySelector: () => ({ textContent: '' }) },
        settingsTab: { mdSettingsDirty: true, saveSettingsAsync: () => new Promise(resolve => { save = resolve; }) },
        post: route => { calls.push(route); return Promise.resolve(); }, lifetime: { timeout() {} }, refresh() {} };
    vm.runInNewContext(code.slice(start, end), ctx);
    ctx.requestScan(true);
    assert.deepEqual(calls, []);
    save(); await new Promise(setImmediate);
    assert.deepEqual(calls, ['Scan/Ungrouped']);
    ctx.settingsTab.mdSettingsDirty = false;
    ctx.requestScan(false); await new Promise(setImmediate);
    assert.deepEqual(calls, ['Scan/Ungrouped', 'Scan']);
    ctx.settingsTab.mdSettingsDirty = true;
    ctx.settingsTab.saveSettingsAsync = () => Promise.reject(new Error('cancelled'));
    ctx.requestScan(true); await new Promise(setImmediate);
    assert.deepEqual(calls, ['Scan/Ungrouped', 'Scan']);
});

test('Scan picker runs only the ticked scans and reports a refused start', async () => {
    const code = source('page-controller.js');
    const start = code.indexOf('function requestScan(scope)');
    const end = code.indexOf('function schedulePoll()', start);
    assert(start >= 0 && end > start);
    const requests = [];
    let reply = Promise.resolve();
    const state = { textContent: '' };
    const ctx = { overviewTab: { busy: {}, setProgress() {} }, STR: { scanStarting: 'Scanning' },
        view: { querySelector: sel => sel === '#mdScanState' ? state : { textContent: '' } },
        settingsTab: { mdSettingsDirty: false }, post: () => Promise.resolve(),
        api: { ajax: req => { requests.push(req); return reply; } }, ApiClient: { getUrl: p => '/' + p },
        errorMessage: () => 'A scan is already running.', lifetime: { timeout() {} }, refresh() {} };
    vm.runInNewContext(code.slice(start, end), ctx);
    ctx.requestScan(['Ungrouped', 'Duplicate']); await new Promise(setImmediate);
    assert.equal(requests.length, 1);
    assert.equal(requests[0].url, '/MediaDash/Scan/Selected');
    assert.deepEqual(JSON.parse(requests[0].data), { Types: ['Ungrouped', 'Duplicate'] });
    reply = Promise.reject(new Error('409'));
    ctx.requestScan(['Ungrouped']); await new Promise(setImmediate);
    assert.equal(ctx.overviewTab.busy.scan, false);
    assert.match(state.textContent, /Scan not started.*already running/);
});

test('Scan picker greys out scans switched off in Settings and remembers the last selection', () => {
    const code = source('page-controller.js');
    const start = code.indexOf('var scanPickSelection');
    const end = code.indexOf('function schedulePoll()', start);
    assert(start >= 0 && end > start);
    const nodes = {};
    const node = sel => nodes[sel] || (nodes[sel] = { style: {}, textContent: '', innerHTML: '', disabled: false, value: '',
        focus() {}, querySelector: () => null });
    let checked = [];
    const scans = [];
    const ctx = { esc: s => String(s), PLUGIN_ID: 'x', STR: { types: { Ungrouped: { title: 'Ungrouped media' }, Duplicate: { title: 'Duplicates' } } },
        settingsTab: { FIX_TYPES: [{ key: 'Duplicate', mode: 'DuplicateFixMode' }, { key: 'Ungrouped', mode: 'UngroupedFixMode' }, { key: 'Stale', mode: 'StaleFixMode' }] },
        overviewTab: { busy: {}, lastStatus: {} }, requestScan: types => scans.push(types),
        api: { configuration: () => ({ then: ok => ok({ DuplicateFixMode: 'ManualApprove', UngroupedFixMode: 'ManualApprove', StaleFixMode: 'Off' }) }) },
        view: { querySelector: node, querySelectorAll: () => checked.map(value => ({ value })) } };
    vm.runInNewContext(code.slice(start, end) + ';this.api_ = { openScanPicker, runPickedScans };', ctx);
    ctx.api_.openScanPicker();
    const markup = node('#mdScanPickList').innerHTML;
    assert.equal(node('#mdScanPickDialog').style.display, 'flex');
    assert.doesNotMatch(markup, /checked/);
    assert.match(markup, /value="Stale" disabled/);
    assert.match(markup, /Off in Settings/);
    assert(markup.indexOf('Duplicates') < markup.indexOf('Ungrouped media') && markup.indexOf('Ungrouped media') < markup.indexOf('Stale'));
    assert.equal(node('#mdScanPickRun').disabled, true);
    checked = ['Ungrouped'];
    ctx.api_.runPickedScans();
    assert.equal(JSON.stringify(scans), '[["Ungrouped"]]');
    assert.equal(node('#mdScanPickDialog').style.display, '');
    ctx.api_.openScanPicker();
    assert.match(node('#mdScanPickList').innerHTML, /value="Ungrouped" checked/);
    ctx.overviewTab.lastStatus.IsScanning = true;
    node('#mdScanPickDialog').style.display = '';
    ctx.api_.openScanPicker();
    assert.equal(node('#mdScanPickDialog').style.display, '');
});

test('Managed artwork shows the metadata path and opens its Jellyfin item instead of Files', async () => {
    const e = issuesEnvironment();
    e.tab.activate();
    e.pending[0].resolve([{ Id: 1, Type: 'CorruptArtwork', Status: 'Detected',
        Path: '/metadata/library/poster.jpg', FileName: 'poster.jpg', DetailsJson: '{"reason":"missing"}',
        SuggestedFix: 'Artwork is unreadable. Approve to delete this file.',
        ItemId: '3f382d12-db31-3a8f-a5c0-1c5236b31210', FixBlockedReason: 'Owner is disabled.' }]);
    await new Promise(setImmediate);
    const markup = e.node('#mdIssueList').innerHTML;
    assert.match(markup, /metadata\/library\/poster.jpg/);
    assert.doesNotMatch(markup, /class="fieldDescription mdIssuePath"/);
    assert.match(markup, /mdOpenInJellyfin.*Open item in Jellyfin/);
    assert.match(markup, /mdIssueTagChip">Missing artwork/);
    assert.match(markup, /the file was missing when MediaDash checked it/);
    assert.doesNotMatch(markup, /Approve to delete this file/);
    const row = e.node('#mdIssueList');
    e.tab.activate();
    e.pending[1].resolve([{ Id: 2, Type: 'CorruptArtwork', Status: 'Detected', FileName: 'poster.jpg',
        Path: '/metadata/library/poster.jpg', DetailsJson: '{"reason":"decode error"}' }]);
    await new Promise(setImmediate);
    assert.match(row.innerHTML, /mdIssueTagChip">Corrupt artwork/);
});

test('Bulk approval excludes blocked, queued and needs-review rows without changing dismissal selection', async () => {
    const e = issuesEnvironment();
    const issue = { Type: 'Quality', Status: 'Detected', Path: '/movie.mkv', FileName: 'movie.mkv', DetailsJson: '{}', Warnings: [] };
    e.tab.activate(); e.pending[0].resolve([
        { ...issue, Id: 1 }, { ...issue, Id: 2, FixBlockedReason: 'Disabled' },
        { ...issue, Id: 3, Status: 'Queued' }, { ...issue, Id: 4, Warnings: [{ blocking: true, message: 'Needs consent' }] }
    ]); await new Promise(setImmediate);
    assert.deepEqual(Array.from(e.tab.filteredIds(true)), [1]);
    assert.deepEqual(Array.from(e.tab.filteredIds()), [1, 2, 3, 4]);
    assert.match(e.node('.mdApproveAllLabel').textContent, /1/);
    assert.match(e.node('#mdIssueList').innerHTML, /Needs consent/);
});

test('Failed fix startup clears the busy state and displays the server explanation', async () => {
    const e = overviewEnvironment(), alerts = [];
    const ctx = { view: { querySelector: e.node }, overviewTab: e.tab, STR: { fixStartFailed: 'Could not start fixes.' },
        post: () => Promise.reject({ responseText: 'Task unavailable' }), errorMessage: err => err.responseText,
        Dashboard: { alert: item => alerts.push(item) }, lifetime: { timeout() {} }, refresh() {} };
    const code = source('page-controller.js');
    const start = code.indexOf("view.querySelector('#mdFixNow').addEventListener('click'");
    const end = code.indexOf('// Right-click (desktop)', start);
    assert(start >= 0 && end > start);
    vm.runInNewContext(code.slice(start, end), ctx);
    e.node('#mdFixNow').fire('click'); await new Promise(setImmediate);
    assert.equal(e.tab.busy.fix, false);
    assert.match(e.node('#mdScanState').textContent, /Task unavailable/);
    assert.equal(alerts[0].message, 'Task unavailable');
});

test('Rejected approval keeps the row detected and reports the error', async () => {
    const nodes = new Map();
    const node = selector => {
        if (!nodes.has(selector)) nodes.set(selector, { textContent: '', style: {}, getAttribute: () => '1', querySelector: node });
        return nodes.get(selector);
    };
    const ctx = { window: { MediaDash: {} } };
    vm.runInNewContext(source('tabs/issues-tab.js').replace('filteredIds: function', 'rowAction: rowAction, filteredIds: function'), ctx);
    const tab = ctx.window.MediaDash.createIssuesTab({
        root: { querySelector: node }, strings: { issueActionFailed: 'Could not update this issue.' },
        post: () => Promise.reject({ statusText: 'Settings changed' }), errorMessage: err => err.statusText
    });
    const button = { getAttribute: () => '1', closest: () => node('row') };
    tab.rowAction(button, 'Approve', 'Approved');
    assert.equal(button.disabled, true);
    await new Promise(setImmediate);
    assert.equal(button.disabled, false);
    assert.match(node('.mdRowMsg').textContent, /Settings changed/);
    assert.equal(node('row').style.opacity, undefined);
});

test('Fetch errors expose the server reason and suppress it if the page closes while reading', async () => {
    const ctx = { window: {} }; vm.createContext(ctx);
    vm.runInContext(source('page-lifetime.js'), ctx); vm.runInContext(source('api-client.js'), ctx);
    const life = ctx.window.MediaDash.createPageLifetime(); life.start();
    let finish;
    const response = { status: 409, statusText: 'Conflict', text() {}, clone() { return { text: () => new Promise(resolve => { finish = resolve; }) }; } };
    const api = ctx.window.MediaDash.createApi(life, { ajax: () => Promise.reject(response) });
    const request = api.ajax({}); await new Promise(setImmediate);
    finish('"Detect only is read-only"');
    await assert.rejects(request, error => error === response && error.responseText.includes('Detect only'));
    let rendered = false;
    api.ajax({}).catch(() => { rendered = true; }); await new Promise(setImmediate);
    life.stop(); finish('old failure'); await new Promise(setImmediate);
    assert.equal(rendered, false);
});

test('Fetch conflict bodies retain the structured replacement preview', async () => {
    const ctx = { window: {} }; vm.createContext(ctx);
    vm.runInContext(source('page-lifetime.js'), ctx); vm.runInContext(source('api-client.js'), ctx);
    const life = ctx.window.MediaDash.createPageLifetime(); life.start();
    const preview = { Fingerprint: 'b'.repeat(64), Paths: [], BlockingReasons: ['Changed destination'] };
    const response = { status: 409, text() {}, clone() { return { text: () => Promise.resolve(JSON.stringify(preview)) }; } };
    const api = ctx.window.MediaDash.createApi(life, { ajax: () => Promise.reject(response) });
    await assert.rejects(api.ajax({ dataType: 'json' }), error => error === response && error.responseJSON?.Fingerprint === preview.Fingerprint);
});

test('Failed playability repair offers Delete and Ignore with disposal explained', async () => {
    const e = issuesEnvironment();
    e.tab.activate();
    e.pending[0].resolve([{ Id: 7, Type: 'Playability', Status: 'Detected', Path: '/owned/movie.mkv', FileName: 'movie.mkv', DetailsJson: '{}', RepairFailed: true, SuggestedFix: 'Repair failed; deletion needs approval.', Warnings: [{ Blocking: true, Message: 'Repair failed.' }] }]);
    await new Promise(setImmediate);
    const rendered = e.node('#mdIssueList').innerHTML;
    assert.match(rendered, /mdApprove[^>]*><span>Delete<\/span>/);
    assert.match(rendered, /mdDismiss/);
    assert.match(rendered, /recycle bin after failed repair/);
});

test('Issues request failure is visible and Retry recovers without an empty-success message', async () => {
    const e = issuesEnvironment();
    e.tab.activate(); e.pending[0].reject(new Error('HTTP 500'));
    await new Promise(setImmediate);
    assert.equal(e.node('#mdIssueLoadError').style.display, '');
    assert.match(e.node('#mdIssueLoadErrorText').textContent, /load issues/i);
    assert.doesNotMatch(e.node('#mdIssueList').innerHTML, /No issues/);
    e.node('#mdIssueLoadRetry').onclick();
    assert.equal(e.pending.length, 2);
    e.pending[1].resolve([]); await new Promise(setImmediate);
    assert.equal(e.node('#mdIssueLoadError').style.display, 'none');
    assert.match(e.node('#mdIssueList').innerHTML, /No issues/);
});

test('A failed or malformed Issues refresh retains previously displayed rows', async () => {
    const e = issuesEnvironment();
    e.tab.activate();
    e.pending[0].resolve([{ Id: 7, Type: 'Quality', Status: 'Detected', Path: '/owned/movie.mkv', FileName: 'movie.mkv', DetailsJson: '{}', Warnings: [] }]);
    await new Promise(setImmediate);
    assert.match(e.node('#mdIssueList').innerHTML, /movie\.mkv/);
    const previous = e.node('#mdIssueList').innerHTML;
    e.tab.activate(); e.pending[1].reject(new Error('HTTP 500'));
    await new Promise(setImmediate);
    assert.equal(e.node('#mdIssueList').innerHTML, previous);
    assert.equal(e.tab.count(), 1);
    assert.equal(e.node('#mdIssueLoadError').style.display, '');
    e.node('#mdIssueLoadRetry').onclick(); e.pending[2].resolve({ wrong: 'shape' });
    await new Promise(setImmediate);
    assert.equal(e.tab.count(), 1);
    assert.equal(e.node('#mdIssueList').innerHTML, previous);
    assert.equal(e.node('#mdIssueLoadError').style.display, '');
});

test('A failure for the previous Issues view does not report failure in the new view', async () => {
    const e = issuesEnvironment();
    e.tab.activate(); e.tab.toggleShowIgnored();
    e.pending[0].reject(new Error('old open-view failure'));
    await new Promise(setImmediate);
    assert.equal(e.pending.length, 2);
    assert.equal(e.pending[1].url, 'Issues?status=Dismissed');
    assert.notEqual(e.node('#mdIssueLoadError').style.display, '');
    e.pending[1].resolve([]); await new Promise(setImmediate);
    assert.equal(e.node('#mdIssueLoadError').style.display, 'none');
});

test('A newer attempt message with the same timestamp replaces the displayed failure', async () => {
    const e = issuesEnvironment();
    const row = { Id: 7, Type: 'Quality', Status: 'Queued', Path: '/owned/movie.mkv', FileName: 'movie.mkv', DetailsJson: '{}', Warnings: [], LastAttemptSuccess: false, LastAttemptAtUtc: '2026-10-12T00:00:00Z', LastAttemptMessage: 'older attempt' };
    e.tab.activate(); e.pending[0].resolve([row]);
    await new Promise(setImmediate);
    assert.match(e.node('#mdIssueList').innerHTML, /older attempt/);
    e.tab.activate(); e.pending[1].resolve([Object.assign({}, row, { LastAttemptMessage: 'newer attempt' })]);
    await new Promise(setImmediate);
    assert.match(e.node('#mdIssueList').innerHTML, /newer attempt/);
});

function overviewEnvironment() {
    const nodes = new Map();
    const node = selector => {
        if (!nodes.has(selector)) nodes.set(selector, Object.assign(element(), {
            style: {}, textContent: '', innerHTML: '', classList: { toggle() {} },
            querySelector: node, querySelectorAll: () => []
        }));
        return nodes.get(selector);
    };
    const ctx = { window: { MediaDash: {} }, localStorage: { getItem: () => null } };
    vm.runInNewContext(source('dom-helpers.js'), ctx);
    vm.runInNewContext(source('tabs/overview-tab.js'), ctx);
    const tab = ctx.window.MediaDash.createOverviewTab({
        view: { querySelector: node, querySelectorAll: () => [] }, STR: ctx.window.MediaDash.STR,
        fmt: ctx.window.MediaDash.fmt, esc: String, bytes: String,
        updateTabBadge() {}, schedulePoll() {}, bannerDismissed: () => false
    });
    return { tab, node };
}

test('Scan progress advances within a long scanner while retaining the completed-stage count', () => {
    const e = overviewEnvironment();
    e.tab.renderStatus({ IsScanning: true, ScanProgress: 73.6, ScanScannersDone: 1, ScanScannersTotal: 2 }, true);
    assert.match(e.node('#mdScanState').textContent, /74%/);
    assert.match(e.node('#mdScanState').textContent, /1 \/ 2 scanners done/);
    assert.equal(e.node('#mdProgressFill').style.width, '74%');
    e.tab.renderStatus({ IsScanning: true, ScanProgress: null, ScanScannersDone: 1, ScanScannersTotal: 2 }, true);
    assert.equal(e.node('#mdProgressFill').style.width, '50%');
});

test('Grouping file activity stays visible while the overall scanner percentage is unchanged', () => {
    const e = overviewEnvironment();
    const status = { IsScanning: true, ScanProgress: 15, CurrentActivity: '/tv/Show/episode.mkv' };
    e.tab.renderStatus({ ...status, CurrentActivityLabel: 'Checking move files (1/10; 25% of file)' }, true);
    assert.match(e.node('#mdActivity').textContent, /25% of file.*episode.mkv/);
    e.tab.renderStatus({ ...status, CurrentActivityLabel: 'Checking move files (1/10; 50% of file)' }, true);
    assert.match(e.node('#mdActivity').textContent, /50% of file.*episode.mkv/);
    assert.equal(e.node('#mdProgressFill').style.width, '15%');
    e.tab.renderStatus({ IsScanning: false }, true);
    assert.equal(e.node('#mdActivity').textContent, '');
});

test('System performance displays disk rates next to GPU and distinguishes zero from unavailable', () => {
    const e = overviewEnvironment();
    e.tab.renderStatus({ System: { Platform: 'Linux', Disks: [{ Name: 'sda', ReadBytesPerSecond: 0, WriteBytesPerSecond: 1024 }] } }, true);
    assert.match(e.node('#mdSystem').innerHTML, /data-kind="gpu-0"[\s\S]*data-kind="disk-io-0"/);
    assert.match(e.node('[data-field="value"]').innerHTML, /Read 0\/s.*Write 1024\/s/);
    const title = index => e.node('.mdSysCell[data-kind="disk-io-' + index + '"] [data-field="title"]').textContent;
    assert.equal(title(0), 'Disk I/O · sda');
    e.tab.renderStatus({ System: { Platform: 'Linux', Disks: [{ Name: 'sda' }] } }, true);
    assert.match(e.node('[data-field="value"]').innerHTML, /Read .*N\/A.*Write .*N\/A/);
    e.tab.renderStatus({ System: { Platform: 'Windows', Disks: [{ Name: '0 C:' }, { Name: '1 D:' }] } }, true);
    assert.equal(title(0), 'Disk I/O · 0 C:');
    assert.equal(title(1), 'Disk I/O · 1 D:');
    const markup = e.node('#mdSystem').innerHTML;
    assert.match(markup, /aria-describedby="mdSysTip-disk-io-0"/);
    assert.match(markup, /aria-describedby="mdSysTip-disk-io-1"/);
    assert.equal((markup.match(/Network drive and NAS disk performance is unavailable/g) || []).length, 2);
});

test('Disk semicircle compares read and write on a shared scale that retains peaks and handles missing rates', () => {
    const e = overviewEnvironment();
    const render = (read, write) => {
        e.tab.renderStatus({ System: { Platform: 'Linux', Disks: [{ Name: 'sda', ReadBytesPerSecond: read, WriteBytesPerSecond: write }] } }, true);
        return e.node('[data-field="value"]').innerHTML;
    };
    const arcs = markup => Array.from(markup.matchAll(/stroke-dasharray="([\d.]+) 100"/g), match => Number(match[1]));
    let markup = render(2097152, 1048576);
    assert.match(markup, /<svg[^>]*class="mdDiskGauge"/);
    assert.deepEqual(arcs(markup), [100, 50]);
    assert.match(markup, /mdDiskRead/);
    assert.match(markup, /mdDiskWrite/);
    assert.deepEqual(arcs(render(524288, 0)), [25, 0]);
    markup = render(null, -1);
    assert.deepEqual(arcs(markup), [0, 0]);
    assert.match(markup, /Read .*N\/A.*Write .*N\/A/);
    assert.doesNotMatch(markup, /NaN|Infinity/);
    assert.deepEqual(arcs(render(Infinity, NaN)), [0, 0]);
});

test('A fix completing between polls keeps its no-work explanation visible', () => {
    const e = overviewEnvironment();
    e.tab.renderStatus({ IsFixing: false }, true);
    e.tab.busy.fix = true;
    const status = { IsFixing: false, LastFixRun: { FinishedAtUtc: '2026-10-13T01:00:00Z', NoWorkReason: 'No fixes ran. Re-enable the fixer in Settings → Fix order.' } };
    status.FixPauseReason = status.LastFixRun.NoWorkReason;
    e.tab.renderStatus(status, true);
    assert.equal(e.node('#mdScanState').textContent, status.LastFixRun.NoWorkReason);
    for (let poll = 0; poll < 3; poll++) e.tab.renderStatus(status, true);
    assert.equal(e.node('#mdFixPauseNote').style.display, 'flex');
    assert.equal(e.node('#mdFixPauseText').textContent, status.LastFixRun.NoWorkReason);
    assert.equal(e.node('#mdFixIgnoreActivity').style.display, 'none');
    e.tab.renderStatus({ IsFixing: true, LastFixRun: status.LastFixRun }, true);
    assert.equal(e.node('#mdFixPauseNote').style.display, 'none');
    // A cancellation can leave the older summary intact, but the new run resets its current note.
    e.tab.renderStatus({ IsFixing: false, LastFixRun: status.LastFixRun }, true);
    assert.equal(e.node('#mdFixPauseNote').style.display, 'none');
    e.tab.renderStatus({ IsFixing: false, LastFixRun: { FinishedAtUtc: '2026-10-13T01:01:00Z', Attempted: 1, Succeeded: 1 } }, true);
    assert.equal(e.node('#mdFixPauseNote').style.display, 'none');
});

test('An early safety pause remains visible after the task returns', () => {
    const e = overviewEnvironment();
    e.tab.renderStatus({ IsFixing: false, FixPauseReason: 'Recycle bin is full.' }, true);
    assert.equal(e.node('#mdFixPauseText').textContent, 'Recycle bin is full.');
    assert.equal(e.node('#mdFixPauseNote').style.display, 'flex');
    assert.equal(e.node('#mdFixIgnoreActivity').style.display, 'none');
    e.tab.renderStatus({ IsFixing: true, FixPauseReason: 'Playback is active.' }, true);
    assert.equal(e.node('#mdFixIgnoreActivity').style.display, '');
});

test('Run fixes now asks the server for an explanation even with no pending fixes', async () => {
    const e = overviewEnvironment(), calls = [];
    const ctx = { view: { querySelector: e.node }, overviewTab: e.tab, STR: {},
        post: path => { calls.push(path); return Promise.resolve(); }, lifetime: { timeout() {} }, refresh() {} };
    e.tab.renderStatus({ PendingFixCount: 0 }, true);
    const code = source('page-controller.js');
    const start = code.indexOf("view.querySelector('#mdFixNow').addEventListener('click'");
    const end = code.indexOf('// Right-click (desktop)', start);
    assert(start >= 0 && end > start);
    vm.runInNewContext(code.slice(start, end), ctx);
    e.node('#mdFixNow').fire('click', {});
    await new Promise(setImmediate);
    assert.deepEqual(calls, ['Fix']);
    assert.equal(e.tab.busy.fix, true);
});

function filesMenuEnvironment() {
    const nodes = new Map(), requests = [], alerts = [], cleanups = [], timers = new Map();
    let timerId = 0;
    const node = selector => {
        if (!nodes.has(selector)) nodes.set(selector, Object.assign(element(), {
            style: { display: 'none' }, offsetWidth: 230, offsetHeight: 80, textContent: '', value: '',
            focus() {}, contains: () => false, querySelector: node, querySelectorAll: () => [],
            click() { this.fire('click', { stopPropagation() {} }); }
        }));
        return nodes.get(selector);
    };
    const ctx = { window: { MediaDash: {}, innerWidth: 200, innerHeight: 150, alert: m => alerts.push(m) },
        document: element(), ApiClient: { getUrl: p => p },
        setTimeout(fn) { timers.set(++timerId, fn); return timerId; }, clearTimeout(id) { timers.delete(id); } };
    vm.runInNewContext(source('tabs/files-tab.js').replace('get currentFilePath()',
        'bindCtxMenu: bindCtxMenu, openCtxMenu: openCtxMenu, submitManualIssue: submitManualIssue, extractErrorMessage: extractErrorMessage, setListing: function(d) { currentFileListing = d; }, get currentFilePath()'), ctx);
    const api = () => new Promise(() => {});
    api.ajax = options => { requests.push(JSON.parse(options.data)); return Promise.resolve({ Id: 1 }); };
    const files = ctx.window.MediaDash.createFilesTab({ api, view: { querySelector: node },
        lifetime: { own: fn => cleanups.push(fn) }, ownGlobal(target, type, fn) {
            target.addEventListener(type, fn); cleanups.push(() => target.removeEventListener(type, fn));
        } });
    files.currentFilePath = 'C:\\A'; files.setListing({}); files.bindCtxMenu();
    return { files, node, ctx, requests, alerts, cleanups, timers };
}

test('Files menu captures the clicked path for manual add and protection', async () => {
    const e = filesMenuEnvironment();
    e.files.openCtxMenu(190, 140, 'one.mkv', false);
    assert.equal(e.node('#mdFileCtxMenu').style.left, '0px');
    e.files.currentFilePath = 'C:\\B'; e.files.submitManualIssue('Quality');
    assert.equal(e.requests[0].Path, 'C:\\A\\one.mkv');
    e.files.currentFilePath = 'C:\\A'; e.files.openCtxMenu(0, 0, 'folder', true);
    e.files.currentFilePath = 'C:\\B'; e.node('[data-ctx="protect"]').click();
    e.node('#mdProtectAccept').click(); e.node('#mdProtectAccept').click();
    assert.equal(e.requests.length, 2);
    assert.equal(e.requests[1].Target, 'C:\\A\\folder');
    assert.equal(e.requests[1].ScannersMask, '*'); assert.equal(e.requests[1].FixersMask, '*');
    await Promise.resolve(); assert.equal(e.node('#mdProtectAccept').disabled, false);
});

test('Files menu handles keyboard actions, touch clicks and page lifecycle', () => {
    const e = filesMenuEnvironment(), menu = e.node('#mdFileCtxMenu'), list = e.node('#mdFileList');
    let activated = 0;
    menu.fire('keydown', { key: 'Enter', target: { closest: () => ({ click() { activated++; } }) }, preventDefault() {} });
    assert.equal(activated, 1);
    const row = { getAttribute: key => key === 'data-name' ? 'one.mkv' : '0' };
    list.fire('touchstart', { target: { closest: () => row }, touches: [{ clientX: 1, clientY: 1 }] });
    for (const fn of e.timers.values()) fn();
    let prevented = false;
    list.fire('click', { preventDefault() { prevented = true; }, stopImmediatePropagation() {} });
    assert.equal(prevented, true);
    e.node('[data-ctx="protect"]').click();
    e.ctx.document.fire('keydown', { key: 'Escape' });
    assert.equal(e.node('#mdProtectDialog').style.display, 'none');
    e.files.openCtxMenu(0, 0, 'one.mkv', false);
    e.cleanups.splice(0).forEach(fn => fn());
    assert.equal(menu.style.display, 'none'); assert.equal(e.timers.size, 0);
    assert.equal(e.ctx.document.handlers.get('click').size, 0);
    e.files.activateFiles(); assert.equal(e.ctx.document.handlers.get('click').size, 1);
});

test('Files menu decodes fetch and XHR error messages', async () => {
    const { files } = filesMenuEnvironment();
    assert.equal(await files.extractErrorMessage({ text: () => Promise.resolve('{"Error":"Outside library"}') }), 'Outside library');
    assert.equal(await files.extractErrorMessage({ responseText: '"Run a Jellyfin scan first."' }), 'Run a Jellyfin scan first.');
    assert.equal(await files.extractErrorMessage({ text: () => Promise.reject(new Error()) }, 'Failed'), 'Failed');
});

test('Protect dialog keeps keyboard focus inside and restores the opener', () => {
    const e = filesMenuEnvironment();
    const opener = { focus() { e.ctx.document.activeElement = this; } };
    e.ctx.document.activeElement = opener;
    for (const id of ['#mdProtectNote', '#mdProtectCancel', '#mdProtectAccept']) {
        e.node(id).focus = function () { e.ctx.document.activeElement = this; };
    }
    e.files.openCtxMenu(0, 0, 'one.mkv', false);
    e.node('[data-ctx="protect"]').click();
    assert.equal(e.ctx.document.activeElement, e.node('#mdProtectNote'));
    e.node('#mdProtectDialog').fire('keydown', { key: 'Tab', shiftKey: true, preventDefault() {} });
    assert.equal(e.ctx.document.activeElement, e.node('#mdProtectAccept'));
    e.node('#mdProtectCancel').click();
    assert.equal(e.ctx.document.activeElement, opener);
});
function environment() {
    const system = element(), drives = element(), timers = new Map();
    let menus = 0, timerId = 0;
    const ctx = {
        view: { querySelector: s => s === '#mdSystem' ? system : drives },
        showSysPerfCtxMenu() { menus++; }, showSmartCtxMenu() { menus++; },
        setTimeout(fn) { timers.set(++timerId, fn); return timerId; },
        clearTimeout(id) { timers.delete(id); }
    };
    vm.createContext(ctx);
    vm.runInContext(source('page-lifetime.js'), ctx);
    ctx.lifetime = ctx.MediaDash.createPageLifetime();
    ctx.ownGlobal = (target, name, fn, options) => {
        target.addEventListener(name, fn, options);
        ctx.lifetime.own(() => target.removeEventListener(name, fn, options));
    };
    ctx.deps = ctx;
    return { ctx, system, drives, timers, menus: () => menus };
}
const event = { target: { closest: () => ({}) }, preventDefault() {}, clientX: 1, clientY: 1, touches: [{ clientX: 1, clientY: 1 }] };

function languagePicker(locale = 'sv-SE') {
    const ctx = { window: {} };
    vm.createContext(ctx);
    vm.runInContext(source('dom-helpers.js'), ctx);
    vm.runInContext(source('tabs/settings-tab.js'), ctx);
    const tab = ctx.window.MediaDash.createSettingsTab({
        STR: ctx.window.MediaDash.STR, view: { querySelector: () => null },
        esc: s => s, currentLocale: () => locale
    });
    tab.populateLanguageDatalist();
    const hidden = { value: 'eng', closest: () => null }, input = Object.assign(element(), { value: '' });
    const render = { innerHTML: '', querySelectorAll: () => [] };
    const chip = Object.assign(element(), {
        querySelector: s => s === 'input[type=hidden]' ? hidden : s === '.mdChipInputRender' ? render : input
    });
    tab.wireChipInput(chip);
    return { tab, hidden, input };
}

test('language picker saves Swedish as swe for names and real ISO codes', () => {
    for (const selected of ['Swedish', 'swe', 'sv']) {
        const { hidden, input } = languagePicker();
        input.value = selected; input.fire('input');
        assert.equal(hidden.value, 'eng, swe', selected);
    }
});

test('typing Swedish does not commit Swahili at the sw prefix', () => {
    const { hidden, input } = languagePicker();
    for (const partial of ['S', 'Sw']) {
        input.value = partial; input.fire('input');
        assert.equal(hidden.value, 'eng', partial);
    }
    input.value = 'Swedish'; input.fire('input');
    assert.equal(hidden.value, 'eng, swe');
});

test('language names save canonical codes and do not duplicate existing English', () => {
    for (const [selected, expected] of [['English', 'eng'], ['Spanish', 'eng, spa'], ['Filipino', 'eng, fil']]) {
        const { hidden, input } = languagePicker();
        input.value = selected; input.fire('change');
        assert.equal(hidden.value, expected);
    }
});

test('Original selection accepts its code and displayed name alongside English', () => {
    for (const value of ['orig', 'Original language']) {
        const { hidden, input } = languagePicker();
        input.value = value; input.fire('change');
        assert.equal(hidden.value, 'eng, orig');
        input.value = value; input.fire('blur');
        assert.equal(hidden.value, 'eng, orig');
    }
});

test('wizard locale mapping uses real language codes', () => {
    for (const [locale, expected] of [['sv-SE', 'swe'], ['es-ES', 'spa'], ['ja-JP', 'jpn'], ['sw', 'eng'], ['nb-NO', 'nor'], ['nn-NO', 'nor'], ['fil-PH', 'fil']]) {
        assert.equal(languagePicker(locale).tab.localeToIso3(), expected);
    }
});

test('typing complete language names and codes waits for confirmation', () => {
    for (const [text, code] of [['Filipino', 'fil'], ['fil', 'fil'], ['Slovak', 'slk'], ['slk', 'slk'], ['Swedish', 'swe']]) {
        const { hidden, input } = languagePicker();
        for (const char of text) {
            input.value += char;
            input.fire('input', { inputType: 'insertText' });
            assert.equal(hidden.value, 'eng', text);
        }
        input.fire('keydown', { key: 'Enter', preventDefault() {} });
        assert.equal(hidden.value, 'eng, ' + code, text);
        assert.equal(input.value, '');
    }
});

test('language picker supports dropdown replacement and blur without committing composition', () => {
    const { hidden, input } = languagePicker();
    input.value = 'fi'; input.fire('input', { inputType: 'insertCompositionText', isComposing: true });
    assert.equal(hidden.value, 'eng');
    input.value = 'fil'; input.fire('input', { inputType: 'insertReplacementText' });
    assert.equal(hidden.value, 'eng, fil');
    input.value = 'slk'; input.fire('blur');
    assert.equal(hidden.value, 'eng, fil, slk');
});

function bindMenus(ctx) {
    const code = source('tabs/overview-tab.js');
    const start = code.indexOf("var sysEl = ");
    const end = code.indexOf('// Global dismissal', start);
    assert(start >= 0 && end > start, 'context-menu wiring source found');
    vm.runInContext(code.slice(start, end), ctx);
}
test('base-path deployment does not request assets from origin root', () => {
    assert.equal([...html.matchAll(/(?:src|href)="(\/MediaDash\/Asset\/[^\"]+)"/g)].length, 0);
});
test('context-menu callbacks do not multiply on reopen', () => {
    const env = environment();
    for (let i = 0; i < 3; i++) { env.ctx.lifetime.start(); bindMenus(env.ctx); env.ctx.lifetime.stop(); }
    env.ctx.lifetime.start(); bindMenus(env.ctx);
    env.system.fire('contextmenu', event); env.drives.fire('contextmenu', event);
    assert.equal(env.menus(), 2);
});
test('pagehide cancels pending long presses', () => {
    const env = environment(); env.ctx.lifetime.start(); bindMenus(env.ctx);
    env.system.fire('touchstart', event); env.drives.fire('touchstart', event);
    env.ctx.lifetime.stop();
    assert.equal(env.timers.size, 0);
});
test('translation finishing after pagehide cannot initialize a hidden view', async () => {
    const env = environment(); let onShow, finish, initialized = 0;
    Object.assign(env.ctx, {
        document: { querySelector: () => ({ addEventListener: (_name, fn) => { onShow = fn; } }) },
        ApiClient: { getUrl: x => x }, populateLanguageDatalist() {}, populateGenreDatalist() {},
        settingsTab: { populateLanguageDatalist() {}, populateGenreDatalist() {} },
        loadI18n: () => new Promise(resolve => { finish = resolve; }),
        api: () => Promise.reject(new Error('probe unavailable')),
        STR: { tagline: 'test' }, initAfterI18n() { initialized++; }
    });
    const code = source('page-controller.js');
    const start = code.indexOf("document.querySelector('#MediaDashConfigPage').addEventListener('pageshow'");
    const end = code.indexOf('function initAfterI18n()', start);
    assert(start >= 0 && end > start);
    vm.runInContext(code.slice(start, end), env.ctx);
    onShow.call({ querySelector: () => ({ style: {} }), querySelectorAll: () => [] }); env.ctx.lifetime.stop(); finish();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(initialized, 0);
});

test('bootstrap uses server base path, orders scripts and mounts only after assets load', async () => {
    const loaded = []; let mounted = 0;
    const appendChild = el => { loaded.push(el.src || el.href); queueMicrotask(() => el.onload()); };
    const root = { appendChild };
    const ctx = { window: { MediaDash: { mountPage: actual => { assert.equal(actual, root); mounted++; } } },
        document: { createElement: () => ({ remove() {} }), head: { appendChild } } };
    vm.runInNewContext(source('bootstrap.js'), ctx);
    const pending = ctx.window.MediaDash.bootstrap(root, { getUrl: p => 'https://example.invalid/jellyfin/' + p });
    assert.equal(mounted, 0);
    await pending;
    assert.equal(mounted, 1);
    assert(loaded.every(url => url.startsWith('https://example.invalid/jellyfin/MediaDash/Asset/')));
    const scripts = loaded.filter(url => url.endsWith('.js')).map(url => url.split('/').pop());
    assert.equal(scripts[0], 'page-lifetime.js');
    assert.equal(scripts.at(-1), 'page-controller.js');
    for (const url of loaded) {
        const name = url.split('/').pop();
        const exists = fs.existsSync(path.join(config, name.endsWith('.css') ? 'styles' : 'scripts', name)) ||
            fs.existsSync(path.join(config, 'scripts/tabs', name));
        assert(exists, name + ' exists as an actual file');
    }
});

test('missing bootstrap asset rejects without mounting a partial interface', async () => {
    let mounted = false;
    const appendChild = el => queueMicrotask(() => el.onerror());
    const ctx = { window: { MediaDash: { mountPage() { mounted = true; } } },
        document: { createElement: () => ({ remove() {} }), head: { appendChild } } };
    vm.runInNewContext(source('bootstrap.js'), ctx);
    await assert.rejects(ctx.window.MediaDash.bootstrap({ appendChild }, { getUrl: p => '/' + p }), /Could not load MediaDash interface/);
    assert.equal(mounted, false);
});

test('Files ignores an older directory response arriving after newer navigation', async () => {
    const pending = [];
    const elements = new Map();
    const root = { querySelector(selector) {
        if (!elements.has(selector)) elements.set(selector, { style: {}, parentElement: { style: {} }, setAttribute() {} });
        return elements.get(selector);
    } };
    const ctx = { window: { MediaDash: {} }, Set };
    vm.runInNewContext(source('tabs/files-tab.js'), ctx);
    const files = ctx.window.MediaDash.createFilesTab({ view: root, api: () => new Promise(resolve => pending.push(resolve)) });
    files.loadFiles('/older'); files.loadFiles('/newer');
    pending[1]({ Path: '/newer', Entries: [] }); await Promise.resolve();
    pending[0]({ Path: '/older', Entries: [] }); await Promise.resolve();
    assert.equal(files.currentFilePath, '/newer');
    assert.equal(root.querySelector('#mdFileCrumbsText').textContent, '/newer');
});

test('API suppresses stale failures and configuration responses after hide', async () => {
    const ctx = { window: {} }; vm.createContext(ctx);
    vm.runInContext(source('page-lifetime.js'), ctx); vm.runInContext(source('api-client.js'), ctx);
    const life = ctx.window.MediaDash.createPageLifetime(); life.start();
    let reject, resolveConfig, callbacks = 0;
    const api = ctx.window.MediaDash.createApi(life, {
        getUrl: p => p, getJSON: () => new Promise((_resolve, fail) => { reject = fail; }),
        getPluginConfiguration: () => new Promise(resolve => { resolveConfig = resolve; })
    });
    api('Status').catch(() => callbacks++);
    life.stop(); reject(new Error('old failure'));
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(callbacks, 0, 'old failure cannot render into a reopened page');
    life.start(); api.configuration('plugin').then(() => callbacks++);
    life.stop(); resolveConfig({ DryRun: true });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(callbacks, 0);
});

test('settings global listeners detach on hide and reattach once per visit', () => {
    const ctx = { window: element(), document: element() }; ctx.window.MediaDash = {};
    vm.createContext(ctx); vm.runInContext(source('page-lifetime.js'), ctx);
    vm.runInContext(source('tabs/settings-tab.js'), ctx);
    const lifetime = ctx.window.MediaDash.createPageLifetime();
    const settings = ctx.window.MediaDash.createSettingsTab({ lifetime,
        ownGlobal(target, name, handler) { target.addEventListener(name, handler); lifetime.own(() => target.removeEventListener(name, handler)); }
    });
    for (let i = 0; i < 3; i++) {
        lifetime.start(); settings.activateSettings();
        assert.equal(ctx.window.handlers.get('beforeunload').size, 1);
        assert.equal(ctx.document.handlers.get('click').size, 1);
        lifetime.stop();
        assert.equal(ctx.window.handlers.get('beforeunload').size, 0);
        assert.equal(ctx.document.handlers.get('click').size, 0);
    }
});

test('owned timers cannot fire after hide or survive duplicate start', () => {
    const timers = new Map(); let id = 0, fired = 0;
    const ctx = { window: {}, setTimeout(fn) { timers.set(++id, fn); return id; }, clearTimeout(key) { timers.delete(key); } };
    vm.runInNewContext(source('page-lifetime.js'), ctx);
    const life = ctx.window.MediaDash.createPageLifetime(); life.start();
    life.timeout(() => fired++, 100); life.stop();
    assert.equal(timers.size, 0); assert.equal(fired, 0);
    life.start(); life.timeout(() => fired++, 100); life.start();
    assert.equal(timers.size, 0);
});

test('bulk deletion keeps its approved source paths when navigation changes', async () => {
    const requests = [], pending = [];
    const api = () => new Promise(() => {});
    api.ajax = options => { requests.push(JSON.parse(options.data)); return new Promise(resolve => pending.push(resolve)); };
    const ctx = { window: { MediaDash: {}, confirm: () => true, alert() {} }, ApiClient: { getUrl: p => p } };
    // Expose private operations only in the test VM, without adding production API surface.
    vm.runInNewContext(source('tabs/files-tab.js').replace('get currentFilePath()',
        'bulkDelete: bulkDelete, fileSelection: fileSelection, get currentFilePath()'), ctx);
    const files = ctx.window.MediaDash.createFilesTab({ api, view: { querySelector: () => ({ style: {} }) } });
    files.currentFilePath = 'C:\\A'; files.fileSelection.add('one.mkv'); files.fileSelection.add('two.mkv');
    files.bulkDelete(); files.currentFilePath = 'C:\\B'; pending[0]();
    await Promise.resolve();
    assert.deepEqual(requests.map(r => r.Path), ['C:\\A\\one.mkv', 'C:\\A\\two.mkv']);
});

test('settings save stays blocked until both library controls finish hydrating', async () => {
    const code = source('tabs/settings-tab.js');
    const start = code.indexOf('function loadSettings()');
    const end = code.indexOf('// Renders one checkbox', start);
    const pending = [];
    const ctx = { mdConfigLoaded: false, mdSettingsDirty: false,
        deps: { loadEnv: () => Promise.resolve({}), applySubtitleProviderHint() {},
            view: { querySelector: () => null }, api: { configuration: () => Promise.resolve({}) }, lifetime: { timeout() {} } },
        populateLanguageDatalist() {}, populateGenreDatalist() {}, applyConfigToForm() {},
        syncSliderLabels() {}, initTaskCardsAfterLoad() {}, markSettingsClean() {},
        loadLibraries: () => new Promise(resolve => pending.push(resolve)),
        loadStaleExcludedLibraries: () => new Promise(resolve => pending.push(resolve)) };
    vm.createContext(ctx); vm.runInContext(code.slice(start, end), ctx);
    ctx.loadSettings(); await new Promise(resolve => setImmediate(resolve));
    assert.equal(ctx.mdConfigLoaded, false);
    pending[0](); await new Promise(resolve => setImmediate(resolve));
    assert.equal(ctx.mdConfigLoaded, false);
    pending[1](); await new Promise(resolve => setImmediate(resolve));
    assert.equal(ctx.mdConfigLoaded, true);
});

test('bulk move captures selection before the picker and source before the batch', async () => {
    const requests = [], pending = [];
    const api = () => new Promise(() => {});
    api.ajax = options => { requests.push(JSON.parse(options.data)); return new Promise(resolve => pending.push(resolve)); };
    const ctx = { window: { MediaDash: {}, alert() {} }, document: { body: { style: {} } }, ApiClient: { getUrl: p => p } };
    vm.runInNewContext(source('tabs/files-tab.js').replace('get currentFilePath()',
        'bulkMove: bulkMove, picker: picker, fileSelection: fileSelection, get currentFilePath()'), ctx);
    const files = ctx.window.MediaDash.createFilesTab({ api, view: { querySelector: () => ({ style: {} }) } });
    files.currentFilePath = 'C:\\A'; files.fileSelection.add('one.mkv'); files.fileSelection.add('two.mkv');
    files.bulkMove(); files.fileSelection.clear(); files.currentFilePath = 'C:\\B';
    files.picker.onPick('C:\\target');
    assert.equal(requests.length, 1);
    assert.equal(requests[0].PreviewOnly, true);
    assert.deepEqual(requests[0].Selections.map(r => r.From), ['C:\\A\\one.mkv', 'C:\\A\\two.mkv']);
});

test('uploads retain the selected destination throughout the batch', () => {
    const requests = [];
    const ctx = { window: { MediaDash: {} }, ApiClient: { getUrl: p => p, accessToken: () => 'test-only' },
        XMLHttpRequest: function () { this.upload = {}; this.status = 200; this.open = (_method, url) => { this.url = url; };
            this.setRequestHeader = () => {}; this.send = () => requests.push(this); } };
    vm.runInNewContext(source('tabs/files-tab.js').replace('get currentFilePath()', 'uploadFiles: uploadFiles, get currentFilePath()'), ctx);
    const files = ctx.window.MediaDash.createFilesTab({ view: { querySelector: () => ({}) } });
    files.currentFilePath = 'C:\\A'; files.uploadFiles([{ name: 'one.mkv' }, { name: 'two.mkv' }]);
    files.currentFilePath = 'C:\\B'; requests[0].onload();
    assert.equal(requests.length, 2);
    assert(requests.every(r => new URL(r.url, 'http://test').searchParams.get('path') === 'C:\\A'));
});
