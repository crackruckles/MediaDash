// MediaDash Recycle bin tab. Pure helpers were extracted in 06-D.7; the full-DOM
// factory createRecycleTab followed on 2026-09-23 (06-FOLLOWUP-1c). Same 06-D
// pattern: PluginAssetController serves it, configPage.html consumes via
// <script src>, self-tests load it via fs+vm.
//
// The factory body is byte-identical to the previously inline copy apart from:
//   - `esc`, `binProvenanceChipClass`, `binRestoreVariant` now resolved through
//     the shared window.MediaDash namespace via local aliases.
//   - Eight outer-IIFE closures (`fmt`, `bytes`, `describeRelativeAgo`,
//     `describeRetention`, `postJson`, `restoreMessage`, `errorMessage`,
//     `consolidateAllOtherBins`) are taken from deps at the call site.
//   - `ApiClient` (Jellyfin's global) is used unchanged.
(function () {
    'use strict';
    var ns = (typeof window !== 'undefined') ? window : globalThis;
    ns.MediaDash = ns.MediaDash || {};

    // Reason chip class for a recycle-bin row. Provenance is either the numeric enum
    // (0=Auto, 1=Manifest, 2=Orphan) or the string name (older payloads).
    ns.MediaDash.binProvenanceChipClass = function binProvenanceChipClass(f) {
        var p = f && f.Provenance;
        if (p === 1 || p === 'Manifest') return 'mdBinReasonChip mdBinReasonChip--manual';
        if (p === 2 || p === 'Orphan') return 'mdBinReasonChip mdBinReasonChip--orphan';
        return 'mdBinReasonChip';
    };

    // Pick the restore strategy for a recycle-bin row: HistoryId wins (a real history
    // record exists), else BinPath+OriginalPath means we can path-restore, else nothing.
    // HistoryId === 0 is treated as falsy — legacy rows without a real id used 0.
    ns.MediaDash.binRestoreVariant = function binRestoreVariant(f) {
        if (f && f.HistoryId) return { kind: 'history', historyId: f.HistoryId };
        if (f && f.BinPath && f.OriginalPath) return { kind: 'path', binPath: f.BinPath };
        return { kind: 'none' };
    };

    ns.MediaDash.createRecycleTab = function createRecycleTab(deps) {
        var root = deps.root, api = deps.api, strings = deps.strings;
        var fmt = deps.fmt, bytes = deps.bytes;
        var describeRelativeAgo = deps.describeRelativeAgo, describeRetention = deps.describeRetention;
        var postJson = deps.postJson, restoreMessage = deps.restoreMessage, errorMessage = deps.errorMessage;
        var consolidateAllOtherBins = deps.consolidateAllOtherBins;
        var esc = ns.MediaDash.esc;
        var binProvenanceChipClass = ns.MediaDash.binProvenanceChipClass;
        var binRestoreVariant = ns.MediaDash.binRestoreVariant;
        // deps.lifetime is accepted for API-shape parity with createHistoryTab but is not
        // consumed here: every fetch in this factory routes through the wrapped `api()`
        // seam, which already carries the page-lifetime token guard. Per-endpoint loadSeq
        // counters (infoSeq / listSeq / bannerSeq) layer latest-request-wins semantics on
        // top of that, so a slow older response cannot render over a fresher one within
        // the same page lifetime.
        var infoSeq = 0, listSeq = 0, bannerSeq = 0;

        function renderInfo(b) {
            var infoEl = root.querySelector('#mdBinInfo');
            var emptyBtn = root.querySelector('#mdBinEmpty');
            if (infoEl) {
                infoEl.textContent = b.FileCount > 0
                    ? fmt(strings.binHolding, b.FileCount, bytes(b.SizeBytes))
                    : strings.binEmpty;
            }
            // Track state on the button for the click handler; do NOT set `disabled`. A
            // silently-disabled button looks clickable but no-ops on click, which is exactly
            // what users report as "nothing happens".
            if (emptyBtn) emptyBtn.dataset.fileCount = String(b.FileCount || 0);
        }

        function renderList(items) {
            var listEl = root.querySelector('#mdBinList');
            if (!listEl) return;
            var html = '';
            if (items.length === 0) {
                html = '<div class="mdBinEmpty">' + esc(strings.binEmpty) + '</div>';
            } else {
                items.forEach(function (f) {
                    var hint = f.RestoreHint || 'Click Restore to put the file back.';
                    var variant = binRestoreVariant(f);
                    var restoreBtn;
                    if (variant.kind === 'history') {
                        restoreBtn = '<button is="emby-button" type="button" class="raised mdBinRestore" data-history-id="' + variant.historyId + '" title="' + esc(hint) + '"><span class="material-icons">restore</span><span>' + esc(strings.restore) + '</span></button>';
                    } else if (variant.kind === 'path') {
                        restoreBtn = '<button is="emby-button" type="button" class="raised mdBinRestore" data-bin-path="' + esc(variant.binPath) + '" title="' + esc(hint) + '"><span class="material-icons">restore</span><span>' + esc(strings.restore) + '</span></button>';
                    } else {
                        restoreBtn = '<span class="mdBinNoHistory" title="' + esc(hint) + '">no origin</span>';
                    }

                    var deleteBtn = f.BinPath
                        ? '<button is="emby-button" type="button" class="raised mdBinDelete" data-bin-path="' + esc(f.BinPath) + '" data-file-name="' + esc(f.FileName) + '" title="Permanently delete this file. Cannot be undone." style="margin-left:.4em;"><span class="material-icons">delete_forever</span><span>Delete</span></button>'
                        : '';

                    var fromPath = f.OriginalPath
                        ? '<div class="mdBinRowFrom" title="' + esc(f.OriginalPath) + '">from ' + esc(f.OriginalPath) + '</div>'
                        : '';

                    var chipClass = binProvenanceChipClass(f);
                    var reasonChip = f.Reason
                        ? '<span class="' + chipClass + '" title="' + esc(f.IssueType || '') + '">' + esc(f.Reason) + '</span>'
                        : '';

                    var recycledAt = new Date(f.RecycledAtUtc);
                    var recycledAgo = describeRelativeAgo(recycledAt);
                    var purge = describeRetention(f.AutoPurgesAtUtc);

                    var actionLine = f.ActionText
                        ? '<div class="mdBinRowAction2" title="' + esc(f.ActionText) + '">' + esc(f.ActionText) + '</div>'
                        : '';
                    var helpBubble = f.RestoreHint
                        ? '<div class="mdBinRowHelp" title="' + esc(f.RestoreHint) + '">' +
                                '<span class="material-icons">help_outline</span>' +
                                '<span>How to recover</span>' +
                            '</div>'
                        : '';

                    // E2E audit E2E-10: mdBinRowMsg used to live inside mdBinRowAction (narrow
                    // button column). A long "Restored as X" success message wrapped vertically
                    // inside that column and overlapped the Delete button. Moved to a full-width
                    // footer row via mdBinRowFooter so long messages always have horizontal room
                    // and never obscure the row's controls. Styled .mdBinRowMsg with
                    // display:block in configPage.css to pick up the new container width.
                    html += '<div class="mdBinRow" title="' + esc(f.OriginalPath || f.FileName) + '">' +
                        '<div class="mdBinRowFile">' +
                            '<div class="mdBinRowName">' + esc(f.FileName) + '</div>' +
                            fromPath +
                            '<div class="mdBinReasonRow">' +
                                reasonChip +
                                '<span>Recycled ' + esc(recycledAgo) + '</span>' +
                                (purge ? '<span>· ' + purge + '</span>' : '') +
                            '</div>' +
                            actionLine +
                            helpBubble +
                        '</div>' +
                        '<div class="mdBinRowSize">' + esc(bytes(f.SizeBytes)) + '</div>' +
                        '<div class="mdBinRowDate">' + esc(recycledAt.toLocaleDateString()) + '</div>' +
                        '<div class="mdBinRowAction">' + restoreBtn + deleteBtn + '</div>' +
                        '<div class="mdBinRowFooter"><span class="mdBinRowMsg"></span></div>' +
                        '</div>';
                });
            }
            listEl.innerHTML = html;
            listEl.style.display = '';
            wireRowHandlers(listEl);
        }

        // Row-button click handlers get re-attached on every renderList() but attach to the
        // fresh innerHTML nodes; the previous set dies with the replaced DOM, so no
        // lifetime.own bookkeeping is needed.
        function wireRowHandlers(listEl) {
            listEl.querySelectorAll('.mdBinRestore').forEach(function (rb) {
                rb.addEventListener('click', function () {
                    var id = rb.getAttribute('data-history-id');
                    var binPath = rb.getAttribute('data-bin-path');
                    var row = rb.closest('.mdBinRow');
                    var msg = row.querySelector('.mdBinRowMsg');
                    rb.disabled = true;
                    msg.style.color = '#22c55e';
                    msg.textContent = strings.restoring;
                    var request = id
                        ? postJson('History/' + id + '/Restore')
                        : api.ajax({
                            type: 'POST',
                            url: ApiClient.getUrl('MediaDash/RecycleBin/Items/Restore'),
                            dataType: 'json',
                            data: JSON.stringify({ BinPath: binPath }),
                            contentType: 'application/json'
                        });
                    request.then(function (r) {
                        msg.textContent = restoreMessage(r);
                        rb.style.display = 'none';
                        refreshInfo();
                    }, function (err) {
                        msg.style.color = '#ef4444';
                        msg.textContent = fmt(strings.restoreFailed, errorMessage(err));
                        rb.disabled = false;
                    });
                });
            });
            listEl.querySelectorAll('.mdBinDelete').forEach(function (db) {
                db.addEventListener('click', function () {
                    var binPath = db.getAttribute('data-bin-path');
                    var fileName = db.getAttribute('data-file-name') || 'this file';
                    if (!window.confirm('Permanently delete "' + fileName + '"?\n\nThis cannot be undone. The file will NOT be recoverable from the recycle bin or history.')) return;
                    var row = db.closest('.mdBinRow');
                    var rb = row.querySelector('.mdBinRestore');
                    var msg = row.querySelector('.mdBinRowMsg');
                    db.disabled = true;
                    if (rb) rb.disabled = true;
                    msg.style.color = '#ef4444';
                    msg.textContent = 'Deleting…';
                    api.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('MediaDash/RecycleBin/Items/Delete'),
                        data: JSON.stringify({ BinPath: binPath }),
                        contentType: 'application/json'
                    }).then(function () {
                        msg.textContent = 'Deleted.';
                        row.style.opacity = '0.5';
                        db.style.display = 'none';
                        if (rb) rb.style.display = 'none';
                        refreshInfo();
                    }, function (err) {
                        msg.textContent = 'Delete failed: ' + errorMessage(err);
                        db.disabled = false;
                        if (rb) rb.disabled = false;
                    });
                });
            });
        }

        function renderBanner(list) {
            var banner = root.querySelector('#mdBinConsolidateBanner');
            if (!banner) return;
            if (!Array.isArray(list) || list.length === 0) {
                banner.style.display = 'none';
                banner.innerHTML = '';
                return;
            }

            var totalBatches = list.reduce(function (a, o) { return a + (o.BatchCount || 0); }, 0);
            var totalBytes = list.reduce(function (a, o) { return a + (o.SizeBytes || 0); }, 0);
            var locationLines = list.map(function (o) {
                return '<div style="font:600 12.5px \'IBM Plex Mono\', monospace;margin-top:.35em;">' +
                        esc(o.RootPath) + ' <span style="opacity:.7;font-weight:400;">(' +
                        o.BatchCount + ' batch' + (o.BatchCount === 1 ? '' : 'es') + ', ' + esc(bytes(o.SizeBytes || 0)) + ')</span>' +
                        '</div>';
            }).join('');
            banner.innerHTML =
                '<div class="mdCard mdFade" style="border-left:4px solid #f59e0b;padding:14px 18px;margin-bottom:14px;">' +
                    '<div style="display:flex;align-items:center;gap:12px;flex-wrap:wrap;">' +
                        '<span class="material-icons" style="color:#f59e0b;font-size:1.6em;">move_to_inbox</span>' +
                        '<div style="flex:1;min-width:20em;">' +
                            '<div style="font-weight:700;font-size:14px;margin-bottom:.35em;">' +
                                totalBatches + ' recycle batch' + (totalBatches === 1 ? '' : 'es') +
                                ' at ' + list.length + ' other location' + (list.length === 1 ? '' : 's') +
                                ' (' + esc(bytes(totalBytes)) + ')' +
                            '</div>' +
                            '<div class="fieldDescription">These came from an earlier Settings → Recycle bin folder. Consolidate them into the current bin so restore, empty, and retention all work from one place.</div>' +
                            locationLines +
                        '</div>' +
                        '<button is="emby-button" type="button" id="mdBinConsolidateGo" class="raised" style="background:#f59e0b;color:#111;font-weight:700;">' +
                            '<span class="material-icons" style="font-size:1.1em;vertical-align:-3px;margin-right:.3em;">merge_type</span><span>Consolidate all</span>' +
                        '</button>' +
                    '</div>' +
                '</div>';
            banner.style.display = '';

            var goBtn = banner.querySelector('#mdBinConsolidateGo');
            if (!goBtn) return;
            goBtn.addEventListener('click', function () {
                var msg = 'Move ' + totalBatches + ' recycle batch(es) (' + bytes(totalBytes) + ') into the current bin?\n\n' +
                    'Files stay recyclable and history-restorable, they just live in one place. Cross-volume moves may take a while.';
                if (!window.confirm(msg)) return;
                goBtn.disabled = true;
                goBtn.innerHTML = '<span class="material-icons mdSpin" style="font-size:1.1em;vertical-align:-3px;margin-right:.3em;">progress_activity</span><span>Consolidating…</span>';
                consolidateAllOtherBins(list, goBtn);
            });
        }

        function refreshInfo() {
            var mySeq = ++infoSeq;
            api('RecycleBin').then(function (b) {
                if (mySeq !== infoSeq) return;
                renderInfo(b);
            });
        }

        function refreshList() {
            var mySeq = ++listSeq;
            api('RecycleBin/Items').then(function (items) {
                if (mySeq !== listSeq) return;
                renderList(items);
            });
        }

        function refreshBanner() {
            var mySeq = ++bannerSeq;
            api('RecycleBin/OtherBins').then(function (list) {
                if (mySeq !== bannerSeq) return;
                renderBanner(list);
            }, function () {
                if (mySeq !== bannerSeq) return;
                var banner = root.querySelector('#mdBinConsolidateBanner');
                if (banner) banner.style.display = 'none';
            });
        }

        function activate() {
            refreshInfo();
            refreshList();
            refreshBanner();
        }

        // dispose() intentionally does not tear down `wireSearchFilter('#mdBinSearch',
        // '#mdBinList', '.mdBinRowName')` — that shared search wiring is DOM-scoped and
        // survives pagehide correctly, same rationale as createHistoryTab. Reserved for
        // future per-tab cleanups when 06-D pulls the search helper into its own owned
        // lifecycle.
        function dispose() {}

        return {
            activate: activate,
            refreshInfo: refreshInfo,
            refreshList: refreshList,
            dispose: dispose
        };
    };
})();
