'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');
const config = path.resolve(__dirname, '../../Jellyfin.Plugin.MediaDash/Configuration');
function environment(api) {
    const nodes = new Map();
    const node = key => {
        if (!nodes.has(key)) nodes.set(key, { innerHTML: '', style: {}, classList: { contains: () => false } });
        return nodes.get(key);
    };
    const context = { window: { MediaDash: { esc: value => String(value ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'), bytes: value => value + ' B' } } };
    vm.runInNewContext(fs.readFileSync(path.join(config, 'scripts/tabs/diagnostics-tab.js'), 'utf8'), context);
    const selectedTabs = [];
    const tab = context.window.MediaDash.createDiagnosticsTab({ root: { querySelector: node }, api, strings: {},
        lifetime: { current: () => 1, accepts: () => true }, showTab(name) { selectedTabs.push(name); } });
    return { tab, node, selectedTabs };
}
test('Diagnostics stays hidden and makes no requests until enabled', async () => {
    let calls = 0;
    const e = environment(() => { calls++; return Promise.resolve({ Scan: null }); });
    e.tab.setEnabled(false);
    await e.tab.activate();
    assert.equal(calls, 0);
    assert.equal(e.node('.mdTab[data-tab="diagnostics"]').style.display, 'none');
    e.tab.setEnabled(true);
    await e.tab.activate();
    assert.equal(calls, 1);
    assert.equal(e.node('.mdTab[data-tab="diagnostics"]').style.display, '');
    assert.match(e.node('#mdDiagnosticsData').innerHTML, /Run a scan/);
});
test('Diagnostics shows separate scanner and fixer tables and escapes scanner names', async () => {
    const e = environment(() => Promise.resolve({ Scan: { Outcome: 'Completed', StartedAtUtc: '2026-11-20T00:00:00Z',
        Records: [null, 3, { Component: 'ScanTask', Phase: 'finish' }, { Component: '<img src=x onerror=alert(1)>', Phase: 'finish', ElapsedSeconds: 12.5, Findings: 7,
            HostOrContainerCpuPeakPercent: 40, HostDiskPeakBytesPerSecond: 100, NetworkInterfacePeakBytesPerSecond: 200,
            HostOrContainerMemoryPeakBytes: 300, HostGpuPeakPercent: 25 }],
        Errors: [{ Message: '<script>bad</script>' }] } }));
    e.tab.setEnabled(true);
    await e.tab.activate();
    const html = e.node('#mdDiagnosticsData').innerHTML;
    assert.equal((html.match(/<table\b/g) || []).length, 2);
    for (const column of ['Scanner', 'Time', 'Items found', 'Max CPU', 'Max I/O', 'Max network', 'Max RAM', 'Max GPU']) assert.ok(html.includes(column));
    for (const value of ['12.5 s', '>7<', '40.0%', '100 B/s', '200 B/s', '300 B', '25.0%']) assert.ok(html.includes(value), value);
    assert.match(html, /&lt;img/);
    assert.doesNotMatch(html, /<img|<script>|<details|<pre|ScanTask|Complete scan data/);
});
test('Each scanner has one row using its finish record and missing peaks remain unavailable', async () => {
    const records = ['start', 'heartbeat', 'finish', 'heartbeat'].map((Phase, index) => ({ Id: 'one-stage', Component: 'Scanner', Phase, ElapsedSeconds: index * 10 }));
    const e = environment(() => Promise.resolve({ Scan: { Outcome: 'Completed', StartedAtUtc: '2026-11-20T00:00:00Z', Records: records } }));
    e.tab.setEnabled(true);
    await e.tab.activate();
    const markup = e.node('#mdDiagnosticsData').innerHTML;
    assert.equal((markup.match(/<tr\b/g) || []).length, 3); // two headings and scanner
    assert.match(markup, /20.0 s/);
    assert.match(markup, /—/);
    assert.doesNotMatch(markup, /heartbeat/);
});

test('Fixer diagnostics work without a scan and combine calls using totals and maximum peaks', async () => {
    const e = environment(() => Promise.resolve({ Scan: null, Fix: {
        Outcome: 'Completed', StartedAtUtc: '2026-11-21T00:00:00Z', Records: [
            { Id: 'task', Kind: 'fix', Component: 'FixTask', Phase: 'finish', ElapsedSeconds: 999 },
            { Id: 'a', Kind: 'fix', Component: '<Test>Fixer', Phase: 'start', ElapsedSeconds: 0 },
            { Id: 'a', Kind: 'fix', Component: '<Test>Fixer', Phase: 'finish', ElapsedSeconds: 2, FixTargets: 2, ResolvedIssues: 2, HostOrContainerCpuPeakPercent: 20 },
            { Id: 'a', Kind: 'fix', Component: '<Test>Fixer', Phase: 'heartbeat', ElapsedSeconds: 99 },
            { Id: 'b', Kind: 'fix', Component: '<Test>Fixer', Phase: 'finish', ElapsedSeconds: 3, FixTargets: 1, ResolvedIssues: 0, HostOrContainerCpuPeakPercent: 40 },
            null, 3
        ]
    } }));
    e.tab.setEnabled(true);
    await e.tab.activate();
    const html = e.node('#mdDiagnosticsData').innerHTML;
    assert.equal((html.match(/<table\b/g) || []).length, 2);
    assert.equal((html.match(/&lt;Test&gt;/g) || []).length, 1);
    for (const value of ['Fixer', 'Targets', 'Issues resolved', '5.0 s', '>3<', '>2<', '40.0%']) assert.ok(html.includes(value), value);
    assert.doesNotMatch(html, /FixTask|999|99.0 s|<Test>/);
});
test('Disabling diagnostics discards a pending response and clears its data', async () => {
    let resolve;
    const e = environment(() => new Promise(done => { resolve = done; }));
    e.tab.setEnabled(true);
    const request = e.tab.activate();
    e.tab.setEnabled(false);
    resolve({ Scan: { Outcome: 'Completed', Records: [{ Component: 'private data' }] } });
    await request;
    assert.equal(e.node('#mdDiagnosticsData').innerHTML, '');
});
test('Disabling an active Diagnostics tab returns to Settings', () => {
    const e = environment(() => Promise.resolve({ Scan: null }));
    e.tab.setEnabled(true);
    e.node('.mdTab[data-tab="diagnostics"]').classList.contains = () => true;
    e.tab.setEnabled(false);
    assert.deepEqual(e.selectedTabs, ['settings']);
    assert.equal(e.node('#mdTab-diagnostics').style.display, 'none');
});
test('Diagnostics is placed next to Errors with its toggle inside Maintenance', () => {
    const html = fs.readFileSync(path.join(config, 'configPage.html'), 'utf8');
    assert.match(html, /data-tab="errors"[^]*?<\/button>\s*<button[^>]*data-tab="diagnostics"/);
    const maintenance = html.slice(html.indexOf('id="mdSet-maintenance"'), html.indexOf('<div class="mdSetSave">'));
    assert.match(maintenance, /id="mdDiagnosticsEnabled"/);
});
