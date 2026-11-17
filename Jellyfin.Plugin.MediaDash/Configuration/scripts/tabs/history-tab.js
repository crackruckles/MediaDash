// MediaDash History tab. Pure helpers were extracted in 06-D.7; the full-DOM
// factory createHistoryTab followed on 2026-09-23 (06-FOLLOWUP-1d). Same
// 06-D pattern as recycle-tab.js / errors-tab.js: served by
// PluginAssetController, consumed via <script src>, self-tests load it via
// fs+vm.
//
// The factory body is byte-identical to the previously inline copy apart from:
//   - `esc`, `historyFailReasonOf`, `historyMatches` resolved through the shared
//     window.MediaDash namespace via local aliases.
//   - `bytes`, `tryRestore`, `PLUGIN_ID` are taken from deps at the call site.
//   - `ApiClient` (Jellyfin's global) is used unchanged.
//   - `lifetime` is genuinely used here (unlike recycle/errors) — it's already
//     in the standard deps bag.
(function () {
    'use strict';
    var ns = (typeof window !== 'undefined') ? window : globalThis;
    ns.MediaDash = ns.MediaDash || {};

    // Extract a short human-readable reason from a failed fix row's Action text. Successful
    // rows and nulls return empty string; unknown reasons get sentence-cased.
    function historyFailReasonOf(h) {
        if (!h || h.Success) return '';
        var s = (h.Action || '').toString();
        s = s.replace(/^Fix failed\s*[—-]\s*/i, '').trim();
        var cut = s.search(/[.:—]/);
        if (cut > 0) s = s.substring(0, cut).trim();
        if (!s) return 'Unspecified';
        var lower = s.toLowerCase();
        if (lower.indexOf('permission denied') === 0) return 'Permission denied';
        if (lower.indexOf('i/o error') === 0 || lower.indexOf('io error') === 0) return 'I/O error';
        if (lower.indexOf('unexpected error') === 0) return 'Unexpected error';
        if (lower.indexOf('cross-device') >= 0) return 'Cross-device link';
        if (lower.indexOf('no longer exists') >= 0) return 'File no longer exists';
        if (lower.indexOf('outside your library') >= 0 || lower.indexOf('not inside a configured library') >= 0) return 'Outside library folders';
        if (lower.indexOf('not enough free space') >= 0) return 'Not enough free space';
        if (lower.indexOf('nothing to remove any more') >= 0) return 'Refused (no longer applicable)';
        if (lower.indexOf('refused') === 0) return 'Refused by safety guard';
        return s.charAt(0).toUpperCase() + s.slice(1);
    }

    // History row filter predicate. filter ∈ {'all','succeeded','failed','dryrun'}; failReason
    // is only consulted when filter is 'failed'.
    function historyMatches(h, filter, failReason) {
        if (filter === 'succeeded') return h.Success && !h.WasDryRun;
        if (filter === 'failed') {
            if (h.Success) return false;
            if (failReason && historyFailReasonOf(h) !== failReason) return false;
            return true;
        }
        if (filter === 'dryrun') return h.WasDryRun;
        return true;
    }

    ns.MediaDash.historyFailReasonOf = historyFailReasonOf;
    ns.MediaDash.historyMatches = historyMatches;

    ns.MediaDash.createHistoryTab = function createHistoryTab(deps) {
        var root = deps.root, api = deps.api, strings = deps.strings, lifetime = deps.lifetime;
        var bytes = deps.bytes, tryRestore = deps.tryRestore, PLUGIN_ID = deps.PLUGIN_ID;
        var esc = ns.MediaDash.esc;

        var cache = [];
        var statsCache = null;
        var filter = 'all';
        var failReason = '';
        var hiddenBeforeMs = 0;
        var loadSeq = 0;
        var movesCache = [];
        var moveLoadFailed = false;

        function bindMoveUndo(container) {
            container.querySelectorAll('.mdMoveUndo').forEach(function (button) {
                button.addEventListener('click', function () {
                    if (button.disabled) return;
                    var id = button.getAttribute('data-move');
                    var row = button.closest('.mdMoveRow') || button.closest('[data-history]');
                    var message = row.querySelector('.mdRowMsg');
                    var pageTok = lifetime.current();
                    button.disabled = true;
                    message.textContent = strings.moveUndoChecking || 'Checking the complete move and original locations…';
                    return api.ajax({ type: 'POST', url: ApiClient.getUrl('MediaDash/Moves/' + encodeURIComponent(id) + '/Undo'), dataType: 'json' }).then(function (result) {
                        if (!lifetime.accepts(pageTok)) return;
                        message.textContent = result.Message || '';
                        button.disabled = false;
                        if (!result.WasDryRun) activate();
                    }, function (error) {
                        if (!lifetime.accepts(pageTok)) return;
                        var detail = error && error.responseText;
                        try {
                            var parsed = JSON.parse(detail);
                            detail = parsed.Message || parsed.message || parsed.detail || parsed.Detail;
                        } catch (_) { /* Plain-text errors remain readable. */ }
                        message.textContent = detail || strings.moveUndoFailed || 'Undo could not complete. Review the recorded move and its current files.';
                        button.disabled = false;
                    });
                });
            });
        }

        function moveUndoButton(id) {
            return '<button is="emby-button" type="button" class="raised mdMoveUndo" data-move="' + esc(id) + '"><span class="material-icons">undo</span><span>' + esc(strings.undoCompleteMove || 'Undo complete move') + '</span></button>';
        }

        function renderMoves() {
            var element = root.querySelector('#mdMoveOperations');
            if (!element) return;
            var html = moveLoadFailed ? '<p class="fieldDescription">' + esc(strings.moveLoadFailed || 'Move recovery records could not be loaded. Refresh this tab to try again.') + '</p>' : '';
            if (movesCache.length) {
                html += '<div class="mdCard mdFade"><div class="mdSecHead">' + esc(strings.recordedMoves || 'Recorded moves') + '</div><p class="fieldDescription">' +
                    esc(strings.moveRecoveryHelp || 'These records remain available if a move stops before History is saved. Undo restores the complete recorded item only when its files are unchanged and the original locations are free. Dry run previews Undo without changing files.') + '</p>';
                movesCache.forEach(function (move) {
                    var label = move.State === 'NeedsReview' ? (strings.moveNeedsReview || 'Needs review')
                        : move.State === 'Undone' ? (strings.moveUndone || 'Undone')
                        : move.State === 'Completed' ? (move.IsUndo ? (strings.moveUndoCompleted || 'Undo completed') : (strings.moveCompleted || 'Completed'))
                        : (strings.moveInProgress || 'Move in progress');
                    html += '<div class="mdMoveRow"><p><strong>' + esc(label) + '</strong></p>' +
                        (move.Detail ? '<p class="fieldDescription">' + esc(move.Detail) + '</p>' : '') +
                        (move.UserStatePending ? '<p class="fieldDescription">' + esc(strings.moveUserStatePending || "Run Jellyfin's library scan to finish indexing and restore watched state.") + '</p>' : '') +
                        '<details><summary>' + esc(strings.moveRecordedPaths || 'Recorded paths') + '</summary>' +
                        '<div class="mdHistPath">' + esc(strings.moveOriginal || 'Original') + ':<br>' + (move.Sources || []).map(esc).join('<br>') + '</div>' +
                        '<div class="mdHistPath">' + esc(strings.moveDestination || 'Destination') + ':<br>' + (move.Destinations || []).map(esc).join('<br>') + '</div>' +
                        ((move.RecoveryPaths || []).length ? '<div class="mdHistPath">' + esc(strings.moveRecovery || 'Recovery locations — preserve these while reviewing') + ':<br>' + move.RecoveryPaths.map(esc).join('<br>') + '</div>' : '') + '</details>' +
                        '<div class="fieldDescription mdRowMsg"></div>' +
                        (move.CanUndo && move.State === 'Completed' ? moveUndoButton(move.Id) : '') + '</div>';
                });
                html += '</div>';
            }
            element.innerHTML = html;
            bindMoveUndo(element);
        }

        function render(list) {
            if (list) cache = list;
            renderMoves();
            var full = cache;
            var visible = full.filter(function (h) {
                return !hiddenBeforeMs || new Date(h.FixedAtUtc).getTime() > hiddenBeforeMs;
            });

            root.querySelectorAll('.mdHistFilter').forEach(function (btn) {
                var f = btn.getAttribute('data-filter');
                var n;
                if (f === 'failed') {
                    n = visible.filter(function (h) { return !h.Success; }).length;
                } else if (f === 'succeeded') {
                    n = visible.filter(function (h) { return h.Success && !h.WasDryRun; }).length;
                } else if (f === 'dryrun') {
                    n = visible.filter(function (h) { return h.WasDryRun; }).length;
                } else {
                    n = visible.length;
                }
                btn.querySelector('.mdHistCount').textContent = n;
                btn.classList.toggle('mdHistFilterActive', f === filter);
            });

            var reasonSel = root.querySelector('#mdHistoryFailReason');
            if (reasonSel) {
                if (filter === 'failed') {
                    var failedRows = visible.filter(function (h) { return !h.Success; });
                    if (failedRows.length > 0) {
                        var reasonCounts = {};
                        failedRows.forEach(function (h) {
                            var r = historyFailReasonOf(h);
                            reasonCounts[r] = (reasonCounts[r] || 0) + 1;
                        });
                        var reasons = Object.keys(reasonCounts).sort(function (a, b) {
                            return reasonCounts[b] - reasonCounts[a] || a.localeCompare(b);
                        });
                        if (failReason && reasons.indexOf(failReason) < 0) failReason = '';

                        var opts = '<option value="">All failure reasons (' + failedRows.length + ')</option>';
                        reasons.forEach(function (r) {
                            opts += '<option value="' + esc(r) + '"' + (r === failReason ? ' selected' : '') + '>' + esc(r) + ' (' + reasonCounts[r] + ')</option>';
                        });
                        reasonSel.innerHTML = opts;
                        reasonSel.style.display = '';
                    } else {
                        reasonSel.style.display = 'none';
                        failReason = '';
                    }
                } else {
                    reasonSel.style.display = 'none';
                    failReason = '';
                }
            }

            var filtered = visible.filter(function (h) { return historyMatches(h, filter, failReason); });
            var el = root.querySelector('#mdHistoryList');
            var stats = statsCache;
            var summaryEl = root.querySelector('#mdHistorySummary');
            var saved = stats ? (stats.TotalBytesFreed || 0) : 0;
            if (saved > 0) {
                summaryEl.innerHTML = '<div class="mdHistSummaryLabel">Space trimmed since install</div>' +
                    '<div class="mdHistSummaryValue">' + esc(bytes(saved)) + '</div>';
            } else {
                summaryEl.innerHTML = '';
            }

            var chartEl = root.querySelector('#mdHistoryChart');
            var libRows = stats && stats.ByLibrary
                ? stats.ByLibrary.map(function (r) { return { name: r.Library || strings.unassignedLibrary, saved: r.BytesFreed }; })
                : [];
            if (libRows.length) {
                var maxSaved = libRows[0].saved || 1;
                chartEl.innerHTML = '<div class="mdCard mdFade">' +
                    '<div class="mdSecHead"><span class="material-icons">bar_chart</span>' + esc(strings.savedPerLibrary) + '</div>' +
                    libRows.map(function (r) {
                        var pct = Math.max(3, Math.round(r.saved / maxSaved * 100));
                        return '<div class="mdBarRow">' +
                            '<div class="mdBarLabel">' + esc(r.name) + '</div>' +
                            '<div class="mdBarTrack"><div class="mdBarFill" style="width:' + pct + '%;background:#10b981;"></div></div>' +
                            '<div class="mdBarValue">' + esc(bytes(r.saved)) + '</div>' +
                            '</div>';
                    }).join('') + '</div>';
            } else {
                chartEl.innerHTML = '';
            }

            if (!filtered.length) {
                var empty = filter === 'all'
                    ? esc(strings.noHistory)
                    : 'No entries match this filter. Try "All" to see everything.';
                el.innerHTML = '<div class="fieldDescription" style="margin-top:1em;">' + empty + '</div>';
                return;
            }

            var html = '';
            filtered.forEach(function (h) {
                var t = strings.types[h.Type] || { color: '#888', title: h.Type };
                var kind = h.WasDryRun ? 'dryrun' : (h.Success ? 'succeeded' : 'failed');
                var statusIcon = kind === 'succeeded' ? 'check_circle' : kind === 'failed' ? 'error' : 'science';
                var typeChip = '<span class="mdHistTypeChip">' + esc(t.title) + '</span>';
                var dryChip = h.WasDryRun ? '<span class="mdHistDryChip">dry run</span>' : '';
                var bytesRight = h.BytesFreed > 0
                    ? '<div class="mdHistBytes">' + esc(bytes(h.BytesFreed)) + '</div>'
                    : '';
                var restoreAction;
                if (kind === 'dryrun') {
                    restoreAction = '<span class="mdHistNoOp">nothing changed</span>';
                } else if (h.CanUndoMove && h.MoveState === 'Completed') {
                    restoreAction = moveUndoButton(h.MoveOperationId);
                } else if (h.MoveOperationId && h.MoveState && h.MoveState !== 'Completed' && h.MoveState !== 'Undone') {
                    restoreAction = '<span class="mdHistNoOp">' + esc(strings.moveReviewRequired || 'Review the recorded move before continuing') + '</span>';
                } else if (kind === 'failed') {
                    // UI-10: the previous Retry button carried the mdRestore class and routed
                    // through tryRestore / History/{id}/Restore, which only succeeds for rows that
                    // have a recycled original — nearly none for failed fixes, since the fix never
                    // ran. The failed issue stays Queued with LastAttemptSuccess=false and is
                    // retried on the next scheduled fix cycle (or by clicking "Run fixes now").
                    // Point users at that flow instead of a button that reliably reports "not in
                    // the recycle bin".
                    restoreAction = '<span class="mdHistNoOp" title="This issue is still queued on the Issues tab and will retry on the next fix run. Dismiss it there to stop retrying.">will retry</span>';
                } else if (h.CanRestore) {
                    restoreAction = '<button is="emby-button" type="button" class="raised mdRestore" data-id="' + h.Id + '"><span class="material-icons">restore</span><span>' + esc(strings.restore) + '</span></button>';
                } else {
                    restoreAction = '';
                }
                html += '<div class="mdCard mdHistRow mdFade" data-kind="' + kind + '" data-history="' + h.Id + '">' +
                    '<div class="mdHistLayout">' +
                        '<div class="mdHistIcon mdHistIcon--' + kind + '"><span class="material-icons">' + statusIcon + '</span></div>' +
                        '<div class="mdHistBody">' +
                            '<div class="mdHistTitleRow">' +
                                '<span class="mdHistAction">' + esc(h.Action) + '</span>' +
                                typeChip + dryChip +
                            '</div>' +
                            '<div class="mdHistPath">' + esc(h.FileName) + '</div>' +
                            '<div class="fieldDescription mdRowMsg mdHistRowMsg"></div>' +
                        '</div>' +
                        '<div class="mdHistMeta">' +
                            '<div class="mdHistMetaTop">' +
                                (bytesRight || '<div class="mdHistBytes mdHistBytesDim">—</div>') +
                                (restoreAction ? '<div class="mdHistAct">' + restoreAction + '</div>' : '') +
                            '</div>' +
                            '<div class="mdHistTime">' + esc(new Date(h.FixedAtUtc).toLocaleString()) + '</div>' +
                        '</div>' +
                    '</div>' +
                '</div>';
            });
            el.innerHTML = html;
            bindMoveUndo(el);

            el.querySelectorAll('.mdRestore').forEach(function (b) {
                b.addEventListener('click', function () { tryRestore(b.getAttribute('data-id'), b, b.closest('[data-history]').querySelector('.mdRowMsg')); });
            });
        }

        function activate() {
            var mySeq = ++loadSeq;
            var pageTok = lifetime.current();
            Promise.all([
                api('History'),
                ApiClient.getPluginConfiguration(PLUGIN_ID),
                api('History/Stats').catch(function () { return null; }),
                api('Moves').catch(function () { return null; })
            ]).then(function (results) {
                // Two guards: outer page lifetime (already enforced by api() for
                // /History and /History/Stats, but the plugin-config leg goes
                // straight through ApiClient), and inner per-tab sequence for
                // stacked activate() calls.
                if (!lifetime.accepts(pageTok)) return;
                if (mySeq !== loadSeq) return;
                var ticks = results[1].HistoryHiddenBeforeUtcTicks || 0;
                hiddenBeforeMs = ticks > 0 ? (ticks - 621355968000000000) / 10000 : 0;
                statsCache = results[2] || null;
                moveLoadFailed = !Array.isArray(results[3]);
                if (!moveLoadFailed) movesCache = results[3];
                render(results[0]);
            });
        }

        function setFilter(f) { filter = f; failReason = ''; render(); }
        function setFailReason(r) { failReason = r || ''; render(); }

        // dispose() intentionally does not tear down the filename search
        // wiring: `wireSearchFilter('#mdHistorySearch', '#mdHistoryList',
        // '.mdHistPath')` in initAfterI18n runs once per lifetime (guarded
        // by dataset.init) and its input listener + MutationObserver are
        // both scoped to the persistent view DOM, so pagehide does not need
        // to remove them. Reserved for future per-tab cleanups when 06-D
        // pulls the search helper into its own owned lifecycle.
        function dispose() { loadSeq++; }

        return {
            activate: activate,
            render: render,
            dispose: dispose,
            setFilter: setFilter,
            setFailReason: setFailReason
        };
    };
})();
