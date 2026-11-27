// Latest scan and fix diagnostics. Data is fetched only while this opt-in tab is enabled.
(function () {
    'use strict';
    window.MediaDash.createDiagnosticsTab = function (deps) {
        var enabled = false;
        var sequence = 0;
        var root = deps.root;
        var strings = deps.strings;
        var esc = window.MediaDash.esc;
        var bytes = window.MediaDash.bytes;
        function percent(value) { return typeof value === 'number' && isFinite(value) ? value.toFixed(1) + '%' : '—'; }
        function amount(value, rate) { return typeof value === 'number' && isFinite(value) ? bytes(value) + (rate ? '/s' : '') : '—'; }
        function setEnabled(value) {
            enabled = value === true;
            var button = root.querySelector('.mdTab[data-tab="diagnostics"]');
            button.style.display = enabled ? '' : 'none';
            if (!enabled) {
                sequence++;
                root.querySelector('#mdDiagnosticsData').innerHTML = '';
                root.querySelector('#mdTab-diagnostics').style.display = 'none';
                if (button.classList.contains('mdTabActive')) deps.showTab('settings');
            }
        }
        function renderTable(run, kind) {
            var fixing = kind === 'fix';
            var records = run && Array.isArray(run.Records) ? run.Records : [];
            var columns = [
                fixing ? (strings.diagnosticsFixer || 'Fixer') : (strings.diagnosticsScanner || 'Scanner'),
                strings.diagnosticsTime || 'Time'
            ];
            if (fixing) columns.push(strings.diagnosticsTargets || 'Targets', strings.diagnosticsResolved || 'Issues resolved');
            else columns.push(strings.diagnosticsItemsFound || 'Items found');
            columns.push(strings.diagnosticsMaxCpu || 'Max CPU', strings.diagnosticsMaxIo || 'Max I/O', strings.diagnosticsMaxNetwork || 'Max network',
                strings.diagnosticsMaxRam || 'Max RAM', strings.diagnosticsMaxGpu || 'Max GPU');
            var html = '<div class="mdCard mdDiagnosticsTableWrap"><table class="mdDiagnosticsTable"><caption>' +
                esc(fixing ? (strings.diagnosticsFixers || 'Fixers') : (strings.diagnosticsScanners || 'Scanners')) + '<span>' +
                (run ? esc(new Date(run.StartedAtUtc).toLocaleString()) + ' · ' + esc(run.Outcome || '') :
                    esc(fixing ? (strings.diagnosticsNoFix || 'Run fixes with diagnostics enabled to record their details.') :
                        (strings.diagnosticsNoScan || 'Run a scan with diagnostics enabled to record its details.'))) + '</span>' +
                '<span>' + esc(strings.diagnosticsPeaksHint || 'Sampled host/container peaks. I/O combines reads and writes; network combines receive and send.') + '</span>' +
                (fixing ? '<span>' + esc(strings.diagnosticsFixersHint || 'Totals combine calls to each fixer. Targets include dry-run previews; resolved issues count applied fixes only.') + '</span>' : '') + '</caption>' +
                '<thead><tr>' + columns.map(function (column) { return '<th scope="col">' + esc(column) + '</th>'; }).join('') + '</tr></thead><tbody>';
            var stages = new Map();
            records.forEach(function (record) {
                if (!record || typeof record !== 'object' || Array.isArray(record) || !record.Component ||
                    record.Component === 'ScanTask' || record.Component === 'FixTask' || (record.Kind && record.Kind !== kind)) return;
                var key = record.Id || record.Component;
                if (!stages.has(key)) stages.set(key, []);
                stages.get(key).push(record);
            });
            var rows = new Map();
            var totals = ['ElapsedSeconds', 'FixTargets', 'ResolvedIssues'];
            var peaks = ['HostOrContainerCpuPeakPercent', 'HostDiskPeakBytesPerSecond', 'NetworkInterfacePeakBytesPerSecond', 'HostOrContainerMemoryPeakBytes', 'HostGpuPeakPercent'];
            stages.forEach(function (stage, id) {
                var record = stage.filter(function (entry) { return entry.Phase === 'finish'; }).pop() || stage[stage.length - 1];
                var key = fixing ? record.Component : id;
                var row = rows.get(key);
                if (!row) {
                    row = { Component: record.Component, Outcomes: new Set() };
                    rows.set(key, row);
                }
                row.Outcomes.add(record.Outcome || record.Phase || '');
                totals.forEach(function (field) {
                    if (record.Phase === 'finish' && typeof record[field] === 'number' && isFinite(record[field]))
                        row[field] = (row[field] || 0) + record[field];
                });
                peaks.forEach(function (field) {
                    if (typeof record[field] === 'number' && isFinite(record[field]))
                        row[field] = row[field] == null ? record[field] : Math.max(row[field], record[field]);
                });
                if (!fixing) row.Findings = record.Findings;
            });
            rows.forEach(function (row) {
                var name = String(row.Component).replace(fixing ? /Fixer$/ : /Scanner$/, '') || row.Component;
                name = name.replace(/([a-z])([A-Z])/g, '$1 $2');
                var values = [name, row.ElapsedSeconds == null ? '—' : row.ElapsedSeconds.toFixed(1) + ' s'];
                if (fixing) values.push(row.FixTargets == null ? '—' : row.FixTargets, row.ResolvedIssues == null ? '—' : row.ResolvedIssues);
                else values.push(row.Findings == null ? '—' : row.Findings);
                values.push(percent(row.HostOrContainerCpuPeakPercent), amount(row.HostDiskPeakBytesPerSecond, true),
                    amount(row.NetworkInterfacePeakBytesPerSecond, true), amount(row.HostOrContainerMemoryPeakBytes, false), percent(row.HostGpuPeakPercent));
                html += '<tr title="' + esc(Array.from(row.Outcomes).join(', ')) + '">' + values.map(function (value) { return '<td>' + esc(value) + '</td>'; }).join('') + '</tr>';
            });
            return html + '</tbody></table></div>';
        }
        function activate() {
            if (!enabled) return Promise.resolve();
            var request = ++sequence;
            var token = deps.lifetime.current();
            return deps.api('Diagnostics/LastScan').then(function (data) {
                if (!enabled || request !== sequence || !deps.lifetime.accepts(token)) return;
                root.querySelector('#mdDiagnosticsData').innerHTML = renderTable(data && data.Scan, 'scan') + renderTable(data && data.Fix, 'fix');
            }, function () {
                if (enabled && request === sequence && deps.lifetime.accepts(token)) {
                    root.querySelector('#mdDiagnosticsData').innerHTML = '<div class="mdCard fieldDescription">' + esc(strings.diagnosticsLoadFailed || 'Diagnostics could not be loaded. Refresh to try again.') + '</div>';
                }
            });
        }
        return { setEnabled: setEnabled, activate: activate, get enabled() { return enabled; } };
    };
})();
