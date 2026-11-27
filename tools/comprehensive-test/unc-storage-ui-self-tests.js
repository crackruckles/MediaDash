'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');
const scripts = path.resolve(__dirname, '../../Jellyfin.Plugin.MediaDash/Configuration/scripts');
const overview = fs.readFileSync(path.join(scripts, 'tabs/overview-tab.js'), 'utf8');
function renderer() {
    const ctx = { window: {}, mdSmartMonitoringEnabled: true };
    vm.runInNewContext(fs.readFileSync(path.join(scripts, 'dom-helpers.js'), 'utf8'), ctx);
    ctx.deps = {
        STR: ctx.window.MediaDash.STR,
        esc: value => String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;'),
        bytes: String,
        fmt: (template, ...values) => template.replace(/\{(\d+)\}/g, (_, n) => values[n])
    };
    const start = overview.indexOf('function driveCardHtml(');
    const end = overview.indexOf('// User preference', start);
    vm.runInNewContext(overview.slice(start, end), ctx);
    return ctx;
}

test('UNC storage shows a network badge and stale reading, escapes paths, and hides SMART', () => {
    const ctx = renderer();
    const html = ctx.driveCardHtml({ Root: '\\\\nas\\<share>', IsLibraryDrive: true, IsNetworkShare: true,
        SpaceReadingStale: true, FreeBytes: 5, TotalBytes: 100, SmartHealth: 'healthy', SmartTemperatureCelsius: 45 });
    assert.match(html, /network share/);
    assert.match(html, /last known/i);
    assert.match(html, /&lt;share>/);
    assert.doesNotMatch(html, /SMART|Temperature|<share>/);
});

test('native disks keep SMART and multiple shares explain potentially overlapping totals', () => {
    const ctx = renderer();
    assert.match(ctx.driveCardHtml({ Root: 'C:', IsLibraryDrive: true, FreeBytes: 5, TotalBytes: 100,
        SmartHealth: 'healthy', SmartTemperatureCelsius: 45 }), /SMART OK/);
    const share = { Root: '\\\\nas\\a', IsLibraryDrive: true, IsNetworkShare: true, FreeBytes: 5, TotalBytes: 100 };
    assert.doesNotMatch(ctx.driveCardsHtml([share]), /totals may overlap/);
    assert.match(ctx.driveCardsHtml([share, { ...share, Root: '\\\\nas\\b' }]), /totals may overlap/);
    assert.doesNotMatch(ctx.driveCardsHtml([]), /totals may overlap/);
});
