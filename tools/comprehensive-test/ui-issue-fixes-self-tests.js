'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');

const wizard = fs.readFileSync(path.resolve(__dirname, '../../Jellyfin.Plugin.MediaDash/Configuration/scripts/wizard.js'), 'utf8');
const start = wizard.indexOf('function gateOnBinDiskInfo(c)');
const end = wizard.indexOf('function renderWizBinWarn(info)', start);
assert(start >= 0 && end > start);

function environment(api) {
    const context = { deps: { view: { querySelector: () => null }, api }, renderWizBinWarn() { context.warned = true; } };
    vm.createContext(context);
    vm.runInContext(wizard.slice(start, end), context);
    return context;
}

for (const cap of [0, 25]) {
    test(`Setup preserves recycle-bin cap ${cap} despite a suggested cap`, async () => {
        const env = environment(() => Promise.resolve({ MeetsFiveGbMinimum: true, SuggestedPauseCapGb: 162 }));
        const config = { RecycleBinPauseFixesAtGb: cap };
        assert.equal(await env.gateOnBinDiskInfo(config), true);
        assert.equal(config.RecycleBinPauseFixesAtGb, cap);
    });
}

test('Setup still blocks a low-space bin without changing the cap', async () => {
    const env = environment(() => Promise.resolve({ MeetsFiveGbMinimum: false, SuggestedPauseCapGb: 162 }));
    const config = { RecycleBinPauseFixesAtGb: 0 };
    assert.equal(await env.gateOnBinDiskInfo(config), false);
    assert.equal(env.warned, true);
    assert.equal(config.RecycleBinPauseFixesAtGb, 0);
});

test('An unavailable disk probe preserves the unlimited cap', async () => {
    const env = environment(() => Promise.reject(new Error('unavailable')));
    const config = { RecycleBinPauseFixesAtGb: 0 };
    assert.equal(await env.gateOnBinDiskInfo(config), true);
    assert.equal(config.RecycleBinPauseFixesAtGb, 0);
});

const historySource = fs.readFileSync(path.resolve(__dirname, '../../Jellyfin.Plugin.MediaDash/Configuration/scripts/tabs/history-tab.js'), 'utf8');
function moveHistoryEnvironment(moves, history = [], undo = () => Promise.resolve({ Success: true })) {
    function node() {
        return {
            innerHTML: '', textContent: '', style: {}, disabled: false,
            querySelector: () => message,
            querySelectorAll(selector) {
                if (selector !== '.mdMoveUndo') return [];
                this.buttons = [...this.innerHTML.matchAll(/class="[^"]*mdMoveUndo[^"]*"[^>]*data-move="([^"]+)"/g)].map(match => ({
                    disabled: false, getAttribute: () => match[1], closest: () => this,
                    addEventListener(_, callback) { this.click = callback; }
                }));
                return this.buttons;
            }
        };
    }
    const message = { textContent: '' };
    const elements = Object.fromEntries(['#mdHistorySummary', '#mdHistoryChart', '#mdHistoryList', '#mdMoveOperations'].map(key => [key, node()]));
    const calls = [];
    const api = route => Promise.resolve(route === 'Moves' ? moves : route === 'History' ? history : { TotalBytesFreed: 0, ByLibrary: [] });
    api.ajax = options => { calls.push(options); return undo(options); };
    const context = { window: { MediaDash: { esc: value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])) } },
        ApiClient: { getPluginConfiguration: () => Promise.resolve({}), getUrl: value => value } };
    vm.createContext(context);
    vm.runInContext(historySource, context);
    const tab = context.window.MediaDash.createHistoryTab({ root: { querySelector: key => elements[key] || null, querySelectorAll: () => [] }, api,
        strings: { types: {}, noHistory: 'No history' }, lifetime: { current: () => 1, accepts: () => true }, bytes: String, tryRestore: () => assert.fail('Move Undo must not use recycle Restore'), PLUGIN_ID: 'owned-test' });
    return { tab, elements, calls, message };
}

test('Move recovery stays visible without a History row and escapes recorded paths', async () => {
    const env = moveHistoryEnvironment([{ Id: 'a'.repeat(32), State: 'NeedsReview', Sources: ['<img src=x onerror=alert(1)>'], Destinations: ['/owned/film'], RecoveryPaths: ['/owned/recovery'] }]);
    env.tab.activate();
    await new Promise(setImmediate);
    const html = env.elements['#mdMoveOperations'].innerHTML;
    assert.match(html, /Needs review/i);
    assert.match(html, /&lt;img/);
    assert.doesNotMatch(html, /<img/);
    assert.match(html, /\/owned\/recovery/);
});

test('A partial move is shown for review rather than promising automatic retry', async () => {
    const env = moveHistoryEnvironment([], [{ Id: 1, Type: 'Ungrouped', Success: false, WasDryRun: false, Action: 'Move stopped', FileName: 'Film.mkv', FixedAtUtc: '2026-10-30T00:00:00Z', MoveOperationId: 'b'.repeat(32), MoveState: 'NeedsReview' }]);
    env.tab.activate();
    await new Promise(setImmediate);
    assert.doesNotMatch(env.elements['#mdHistoryList'].innerHTML, /will retry/i);
    assert.match(env.elements['#mdHistoryList'].innerHTML, /review/i);
});

test('An old unsuccessful cleanup does not promise automatic retry without queue state', async () => {
    const env = moveHistoryEnvironment([], [{ Id: 2, Type: 'OrphanedDebris', Success: false, WasDryRun: false,
        Action: 'The folder was left in place', FileName: 'Show release', FixedAtUtc: '2026-11-21T12:23:16Z' }]);
    env.tab.activate();
    await new Promise(setImmediate);
    const html = env.elements['#mdHistoryList'].innerHTML;
    assert.doesNotMatch(html, /will retry|still queued/i);
    assert.match(html, /not applied/i);
});

test('Complete move Undo uses its protected operation route and preserves refusal text', async () => {
    const id = 'c'.repeat(32);
    const env = moveHistoryEnvironment([{ Id: id, State: 'Completed', CanUndo: true, Sources: ['/owned/original'], Destinations: ['/owned/destination'], RecoveryPaths: [] }], [],
        () => Promise.reject({ responseText: JSON.stringify({ Message: 'A new import occupies the original path.' }) }));
    env.tab.activate();
    await new Promise(setImmediate);
    const button = env.elements['#mdMoveOperations'].buttons?.[0];
    assert.ok(button, 'A complete move must offer complete Undo.');
    await button.click();
    assert.equal(env.calls.length, 1);
    assert.equal(env.calls[0].type, 'POST');
    assert.equal(env.calls[0].url, 'MediaDash/Moves/' + id + '/Undo');
    assert.match(env.message.textContent, /new import/);
    assert.equal(button.disabled, false);
});

test('Completed move explains pending user state until the native library scan finishes', async () => {
    const env = moveHistoryEnvironment([{ Id: 'e'.repeat(32), State: 'Completed', CanUndo: true, UserStatePending: true,
        Sources: ['/owned/original'], Destinations: ['/owned/destination'], RecoveryPaths: [] }]);
    env.tab.activate();
    await new Promise(setImmediate);
    assert.match(env.elements['#mdMoveOperations'].innerHTML, /Jellyfin.*library scan/i);
    assert.match(env.elements['#mdMoveOperations'].innerHTML, /watched state/i);
});

test('Recorded moves uses History styling and starts collapsed with only its heading visible', async () => {
    const env = moveHistoryEnvironment([{ Id: 'a'.repeat(32), State: 'Completed', CanUndo: true,
        Sources: ['/owned/original'], Destinations: ['/owned/destination'], RecoveryPaths: [] }]);
    env.tab.activate();
    await new Promise(setImmediate);
    const markup = env.elements['#mdMoveOperations'].innerHTML;
    assert.match(markup, /^<details\b[^>]*class="[^"]*mdRecordedMoves[^"]*"[^>]*><summary\b[^>]*>Recorded moves<\/summary>/);
    assert.doesNotMatch(markup.match(/^<details[^>]*>/)[0], /\bopen\b/);
    assert.match(markup, /class="[^"]*mdMoveRow[^"]*mdHistRow[^"]*"/);
    assert.match(markup, /class="mdHistAction">Completed/);
    assert.equal(env.elements['#mdMoveOperations'].buttons.length, 1);
});

test('Partial operations never offer a force Undo or recycle Restore action', async () => {
    const env = moveHistoryEnvironment([{ Id: 'd'.repeat(32), State: 'NeedsReview', CanUndo: false, Sources: [], Destinations: [], RecoveryPaths: ['/owned/recovery'] }]);
    env.tab.activate();
    await new Promise(setImmediate);
    assert.match(env.elements['#mdMoveOperations'].innerHTML, /Needs review/i);
    assert.doesNotMatch(env.elements['#mdMoveOperations'].innerHTML, /mdMoveUndo|mdRestore|force=/);
});

test('Move approval preview shows every file, directory and target without metadata internals', () => {
    const context = { window: { MediaDash: { esc: value => String(value ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;') } } };
    vm.createContext(context);
    vm.runInContext(fs.readFileSync(path.resolve(__dirname, '../../Jellyfin.Plugin.MediaDash/Configuration/scripts/tabs/issues-tab.js'), 'utf8'), context);
    const render = context.window.MediaDash.movePreviewHtml;
    assert.equal(typeof render, 'function');
    const issue = { Type: 'Ungrouped', DetailsJson: JSON.stringify({ movePlan: { version: 1, entries: [{ source: '/owned/<film>', destination: '/owned/new/film', isDirectory: true,
        inventory: [{ relativePath: '', isDirectory: true }, { relativePath: 'Film.mkv', isDirectory: false, sha256: 'private-hash', metadata: 'private-permissions' }, { relativePath: 'Film.en.srt', isDirectory: false }, { relativePath: 'empty', isDirectory: true }] }] } }) };
    const html = render(issue, {});
    assert.match(html, /Film\.mkv/);
    assert.match(html, /Film\.en\.srt/);
    assert.match(html, /empty/);
    assert.match(html, /\/owned\/new\/film/);
    assert.match(html, /&lt;film&gt;/);
    assert.doesNotMatch(html, /private-hash|private-permissions|<film>/);
    const cleanupPlan = JSON.parse(issue.DetailsJson);
    cleanupPlan.movePlan.sourceCleanup = { source: '/owned/<old-series>', inventory: [{ metadata: 'private-permissions' }] };
    const cleanupHtml = render({ ...issue, DetailsJson: JSON.stringify(cleanupPlan) }, {}, true);
    assert.match(cleanupHtml, /recycle unchanged source metadata/);
    assert.match(cleanupHtml, /&lt;old-series&gt;/);
    assert.doesNotMatch(cleanupHtml, /private-permissions|<old-series>/);
    assert.equal(render({ Type: 'AudioLanguage', DetailsJson: '{}' }, {}), '');
    assert.equal(render({ Type: 'Ungrouped', DetailsJson: 'broken JSON' }, {}), '');
});

const filesMoveSource = fs.readFileSync(path.resolve(__dirname, '../../Jellyfin.Plugin.MediaDash/Configuration/scripts/tabs/files-tab.js'), 'utf8');
function fileMoveEnvironment() {
    const nodes = new Map(), calls = [], pending = [], cleanups = [];
    function node(key) {
        if (!nodes.has(key)) nodes.set(key, { style: {}, textContent: '', innerHTML: '', disabled: false,
            focus() { context.document.activeElement = this; }, addEventListener() {}, querySelectorAll: () => [] });
        return nodes.get(key);
    }
    const api = () => new Promise(() => {});
    api.ajax = options => { calls.push({ ...options, body: JSON.parse(options.data) }); return new Promise((resolve, reject) => pending.push({ resolve, reject })); };
    const context = { window: { MediaDash: {}, prompt: () => 'Renamed.mkv', alert() {} }, document: { body: { style: {} } }, ApiClient: { getUrl: p => p } };
    vm.runInNewContext(filesMoveSource.replace('get currentFilePath()', 'doFileAction: doFileAction, picker: picker, get currentFilePath()'), context);
    const tab = context.window.MediaDash.createFilesTab({ view: { querySelector: node }, api,
        lifetime: { current: () => 1, accepts: epoch => epoch === context.epoch, own: cleanup => cleanups.push(cleanup) },
        esc: value => String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])) });
    context.epoch = 1;
    function start(action, full, name, directory) {
        tab.doFileAction(action, full, name, directory);
        if (action === 'rename') {
            node('#mdFileRenameName').value = 'Renamed.mkv';
            node('#mdFileRenameName').oninput();
            node('#mdFileRenameAccept').onclick();
        }
    }
    return { tab, start, node, calls, pending, context, cleanups };
}

test('Files Rename uses an in-page name dialog and performs no request before Review', () => {
    const env = fileMoveEnvironment();
    env.context.window.prompt = () => assert.fail('Embedded clients cannot provide native prompt()');
    env.tab.doFileAction('rename', '/owned/Film.mkv', 'Film.mkv', false);
    assert.equal(env.node('#mdFileRenameDialog').style.display, 'flex');
    assert.equal(env.node('#mdFileRenameName').value, 'Film.mkv');
    assert.equal(env.calls.length, 0);
    env.node('#mdFileRenameName').value = 'Renamed.mkv';
    env.node('#mdFileRenameName').oninput();
    env.node('#mdFileRenameAccept').onclick();
    assert.equal(env.calls.length, 1);
    assert.equal(env.calls[0].body.PreviewOnly, true);
    assert.equal(env.calls[0].body.NewName, 'Renamed.mkv');
});

test('Rename cancellation and unchanged or blank names never request a preview', () => {
    const env = fileMoveEnvironment();
    env.tab.doFileAction('rename', '/owned/Film.mkv', 'Film.mkv', false);
    assert.equal(env.node('#mdFileRenameAccept').disabled, true);
    env.node('#mdFileRenameName').value = '   ';
    env.node('#mdFileRenameName').oninput();
    assert.equal(env.node('#mdFileRenameAccept').disabled, true);
    env.node('#mdFileRenameCancel').onclick();
    assert.equal(env.node('#mdFileRenameDialog').style.display, 'none');
    assert.equal(env.calls.length, 0);
});

test('Rename Escape restores focus and Enter requests one complete preview', () => {
    const env = fileMoveEnvironment();
    let restored = false;
    env.context.document.activeElement = { focus() { restored = true; } };
    env.tab.doFileAction('rename', '/owned/Film.mkv', 'Film.mkv', false);
    env.node('#mdFileRenameDialog').onkeydown({ key: 'Escape', preventDefault() {} });
    assert.equal(restored, true);
    assert.equal(env.calls.length, 0);
    env.tab.doFileAction('rename', '/owned/Film.mkv', 'Film.mkv', false);
    const input = env.node('#mdFileRenameName');
    input.value = 'Renamed.mkv'; input.oninput();
    env.node('#mdFileRenameDialog').onkeydown({ key: 'Enter', target: input, preventDefault() {} });
    assert.equal(env.calls.length, 1);
    assert.equal(env.calls[0].body.PreviewOnly, true);
    assert.equal(env.node('#mdFileRenameAccept').onclick, null);
});

test('Rename refuses a late Review and closes when the page lifetime stops', () => {
    const env = fileMoveEnvironment();
    env.tab.doFileAction('rename', '/owned/Film.mkv', 'Film.mkv', false);
    env.node('#mdFileRenameName').value = 'Renamed.mkv'; env.node('#mdFileRenameName').oninput();
    const lateReview = env.node('#mdFileRenameAccept').onclick;
    env.context.epoch = 2;
    lateReview();
    assert.equal(env.calls.length, 0);
    env.cleanups.forEach(cleanup => cleanup());
    assert.equal(env.node('#mdFileRenameDialog').style.display, 'none');
    lateReview();
    assert.equal(env.calls.length, 0);
});

for (const action of ['move', 'rename']) {
    test(`Files ${action} previews complete related paths before explicit approval`, async () => {
        const env = fileMoveEnvironment();
        env.tab.currentFilePath = '/owned';
        env.start(action, '/owned/Film.mkv', 'Film.mkv', false);
        if (action === 'move') env.tab.picker.onPick('/target');
        assert.equal(env.calls.length, 1);
        assert.equal(env.calls[0].body.PreviewOnly, true);
        assert.equal(env.calls[0].dataType, 'json', 'Jellyfin ajax requires an explicit JSON response type');
        env.pending[0].resolve({ Fingerprint: 'f'.repeat(64), Paths: [{ From: '<script>bad</script>', To: '/target/Film.mkv' }], BlockingReasons: [] });
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(env.calls.length, 1);
        assert(!env.node('#mdFileMovePaths').innerHTML.includes('<script>'));
        assert(env.node('#mdFileMovePaths').innerHTML.includes('&lt;script&gt;'));
        env.node('#mdFileMoveAccept').onclick();
        assert.equal(env.calls.length, 2);
        assert.equal(env.calls[1].body.PlanFingerprint, 'f'.repeat(64));
        assert.equal(env.calls[1].body.PreviewOnly, false);
        assert.equal(env.calls[1].dataType, 'json');
    });
}

test('Files ignores a preview response after the page lifetime expires', async () => {
    const env = fileMoveEnvironment();
    env.start('rename', '/owned/Film.mkv', 'Film.mkv', false);
    env.context.epoch = 2;
    env.pending[0].resolve({ Fingerprint: 'f'.repeat(64), Paths: [], BlockingReasons: [] });
    await new Promise(resolve => setImmediate(resolve));
    assert.notEqual(env.node('#mdFileMoveDialog').style.display, 'flex');
    assert.equal(env.calls.length, 1);
});

test('Files move preview reveals its full inventory in bounded groups and prevents double submission', async () => {
    const env = fileMoveEnvironment();
    env.start('rename', '/owned/Film.mkv', 'Film.mkv', false);
    env.pending[0].resolve({ Fingerprint: 'a'.repeat(64), Paths: Array.from({ length: 250 }, (_, i) => ({ From: '/owned/' + i, To: '/target/' + i })), BlockingReasons: [] });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal((env.node('#mdFileMovePaths').innerHTML.match(/mdHistPath/g) || []).length, 100);
    env.node('#mdFileMoveMore').onclick();
    assert.equal((env.node('#mdFileMovePaths').innerHTML.match(/mdHistPath/g) || []).length, 200);
    env.node('#mdFileMoveMore').onclick();
    assert.equal((env.node('#mdFileMovePaths').innerHTML.match(/mdHistPath/g) || []).length, 250);
    env.node('#mdFileMoveAccept').onclick();
    env.node('#mdFileMoveAccept').onclick();
    assert.equal(env.calls.length, 2);
});

test('A changed blocked Files preview retains the reason and requires new consent', async () => {
    const env = fileMoveEnvironment();
    env.start('rename', '/owned/Film.mkv', 'Film.mkv', false);
    env.pending[0].resolve({ Fingerprint: 'a'.repeat(64), Paths: [{ From: '/owned/Film.mkv', To: '/owned/Renamed.mkv' }], BlockingReasons: [] });
    await new Promise(resolve => setImmediate(resolve));
    env.node('#mdFileMoveAccept').onclick();
    env.pending[1].reject({ status: 409, responseJSON: { Fingerprint: 'b'.repeat(64), Paths: [], BlockingReasons: ['Destination now occupied.'] } });
    await new Promise(resolve => setImmediate(resolve));
    assert(env.node('#mdFileMoveMessage').textContent.includes('Destination now occupied.'));
    assert.equal(env.node('#mdFileMoveAccept').disabled, true);
    env.node('#mdFileMoveAccept').onclick();
    assert.equal(env.calls.length, 2);
});
