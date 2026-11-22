// MediaDash Issues tab. Pure helpers (`badgeText`, `issueMatchesFilters`) were
// extracted in 06-D.7; the full-DOM factory createIssuesTab followed on
// 2026-09-23 (06-FOLLOWUP-1e, the last of the four tab-factory extractions).
// Same 06-D pattern as the other tabs: served by PluginAssetController,
// consumed via <script src>, self-tests load it via fs+vm.
//
// The factory body is byte-identical to the previously inline copy apart from:
//   - `esc`, `issueMatchesFilters` resolved through the shared window.MediaDash
//     namespace via local aliases.
//   - Ten outer-IIFE closures (`fmt`, `bytes`, `fileExtOf`, `mediaCategoryOf`,
//     `refresh`, `post`, `bulkIssueAction`, `closeIssueContextMenu`,
//     `openInFileBrowser`, `updateTabBadge`) are taken from deps at the call
//     site.
//   - `deps.lifetime` is accepted for parity but not consumed here (see the
//     comment at the top of the factory).
//   - `encodeURIComponent`, `window.confirm`, `window.alert`, `window.open`,
//     `JSON`, `Object`, `Math` (JS globals) are used unchanged.
(function () {
    'use strict';
    var ns = (typeof window !== 'undefined') ? window : globalThis;
    ns.MediaDash = ns.MediaDash || {};

    // Tab badge cap. Anything > 999 shows as "999+" so the badge stays fixed-width.
    ns.MediaDash.badgeText = function badgeText(n) {
        return n > 999 ? '999+' : String(n);
    };

    // Combined issue-row filter: type ∈ enum, text substring match on FileName || Path,
    // AudioLanguage-specific language filter matched against removeLanguages inside
    // DetailsJson. Non-audio rows pass through the lang filter unaffected (they simply
    // don't participate). Malformed DetailsJson on an audio row → no match.
    ns.MediaDash.issueMatchesFilters = function issueMatchesFilters(issue, typeFilter, textFilter, langFilter) {
        if (typeFilter && issue.Type !== typeFilter) return false;
        if (textFilter) {
            var f = String(textFilter).toLowerCase();
            var fn = (issue.FileName || '').toLowerCase();
            var pth = (issue.Path || '').toLowerCase();
            if (fn.indexOf(f) < 0 && pth.indexOf(f) < 0) return false;
        }
        if (langFilter) {
            if (issue.Type !== 'AudioLanguage') return true;
            try {
                var d = JSON.parse(issue.DetailsJson || '{}');
                return (d.removeLanguages || []).indexOf(langFilter) >= 0;
            } catch (e) { return false; }
        }
        return true;
    };

    ns.MediaDash.movePreviewHtml = function movePreviewHtml(issue, strings, expanded, limit) {
        if (!issue || (issue.Type !== 'Ungrouped' && issue.Type !== 'Misplaced')) return '';
        var plan;
        try { plan = JSON.parse(issue.DetailsJson || '{}').movePlan; } catch (_) { return ''; }
        if (!plan || plan.version !== 1 || !Array.isArray(plan.entries)) return '';
        var esc = ns.MediaDash.esc;
        var paths = [];
        function join(base, relative) {
            if (!relative) return base;
            return String(base || '').replace(/[\\/]$/, '') + (String(base).indexOf('\\') >= 0 ? '\\' : '/') + relative;
        }
        plan.entries.forEach(function (entry) {
            if (!entry || typeof entry.source !== 'string' || typeof entry.destination !== 'string') return;
            var inventory = Array.isArray(entry.inventory) && entry.inventory.length ? entry.inventory : [{ relativePath: '', isDirectory: entry.isDirectory }];
            inventory.forEach(function (payload) {
                if (!payload || typeof payload.relativePath !== 'string') return;
                paths.push({ source: join(entry.source, payload.relativePath), target: join(entry.destination, payload.relativePath), directory: payload.isDirectory });
            });
        });
        if (expanded === false) {
            return '<details class="mdMovePreview" data-id="' + esc(issue.Id) + '"><summary>' + esc(strings.movePreviewLabel || 'Review complete move') + ' (' + paths.length + ')</summary><div class="mdMovePreviewContent"></div></details>';
        }
        var shown = typeof limit === 'number' ? limit : paths.length;
        var html = paths.slice(0, shown).map(function (entry) {
            return '<div class="fieldDescription"><strong>' + esc(entry.directory ? (strings.moveFolder || 'Folder') : (strings.moveFile || 'File')) + '</strong>' +
                '<div class="mdHistPath">' + esc(entry.source) + ' → ' + esc(entry.target) + '</div></div>';
        }).join('');
        if (plan.sourceCleanup) html += '<div class="fieldDescription">' + esc(strings.moveSourceCleanup || 'After the last episode moves, recycle unchanged source metadata that already exists at the destination and remove the empty source folder.') + '<div class="mdHistPath">' + esc(plan.sourceCleanup.source) + '</div></div>';
        if (paths.length > shown) html += '<button is="emby-button" type="button" class="raised mdMovePreviewMore">' + esc(strings.moveShowMore || 'Show more paths') + ' (' + shown + '/' + paths.length + ')</button>';
        return html;
    };

    ns.MediaDash.createIssuesTab = function createIssuesTab(deps) {
        var root = deps.root, api = deps.api, strings = deps.strings, lifetime = deps.lifetime;
        var fmt = deps.fmt, bytes = deps.bytes;
        var fileExtOf = deps.fileExtOf, mediaCategoryOf = deps.mediaCategoryOf;
        var refresh = deps.refresh, post = deps.post, bulkIssueAction = deps.bulkIssueAction;
        var closeIssueContextMenu = deps.closeIssueContextMenu;
        var openInFileBrowser = deps.openInFileBrowser;
        var updateTabBadge = deps.updateTabBadge;
        var getIssueDisposal = deps.getIssueDisposal || function () { return null; };
        var getDryRun = deps.getDryRun || function () { return false; };
        var esc = ns.MediaDash.esc;
        var issueMatchesFilters = ns.MediaDash.issueMatchesFilters;
        var ISSUE_PAGE_SIZE = 100;
        var lastIssueList = [];
        var lastFilteredIssues = [];
        var issuePage = 1;
        var issueLangFilter = '';
        var showingDismissed = false;
        var loading = false;
        // Track the lifetime generation and view mode the in-flight request was issued under.
        // UI-03: pagehide invalidates lifetime and api() returns a never-settling promise, so the
        // `.finally`-equivalents never fire and `loading` sticks true across pageshow. Comparing
        // against lifetime.current() on activate() detects the stale latch and clears it.
        // UI-04: showingDismissed can flip while the fetch is in flight; the response is then
        // rendered against the wrong view. Capture the requested view and re-fetch on mismatch.
        var loadingToken = null;
        var loadingView = false;
        // Separate open-issues count so the tab badge keeps showing open work after the user
        // opens the Ignored view (UI-17). Only an open-view fetch updates it.
        var openCount = 0;

        function sameFolder(a, b) {
            function dir(p) {
                var i = Math.max(String(p || '').lastIndexOf('/'), String(p || '').lastIndexOf('\\'));
                return i > 0 ? p.substring(0, i).toLowerCase() : '';
            }
            return dir(a) === dir(b);
        }

        function renderTotals() {
            var el = root.querySelector('#mdIssueTotals');
            if (!el) return;
            var counts = {};
            lastIssueList.forEach(function (i) { counts[i.Type] = (counts[i.Type] || 0) + 1; });
            var keys = Object.keys(counts).sort();
            if (!keys.length) { el.innerHTML = ''; return; }
            var current = root.querySelector('#mdTypeFilter').value;
            var total = lastIssueList.length;
            var html = '<button is="emby-button" type="button" class="raised mdTotalPill" data-type="" style="font-size:80%;background:' + (current === '' ? '#00a4dc' : 'rgba(255,255,255,.08)') + ';">All <b style="margin-left:.3em;">' + total + '</b></button>';
            keys.forEach(function (k) {
                var t = strings.types[k] || { title: k, color: '#888' };
                var active = current === k;
                html += '<button is="emby-button" type="button" class="raised mdTotalPill" data-type="' + esc(k) + '" style="font-size:80%;background:' + (active ? t.color : 'rgba(255,255,255,.08)') + ';border-left:3px solid ' + t.color + ';">' + esc(t.title) + ' <b style="margin-left:.3em;">' + counts[k] + '</b></button>';
            });
            el.innerHTML = html;
            el.querySelectorAll('.mdTotalPill').forEach(function (b) {
                b.addEventListener('click', function () {
                    root.querySelector('#mdTypeFilter').value = b.getAttribute('data-type');
                    issuePage = 1;
                    issueLangFilter = '';
                    renderTotals();
                    renderLangFilter();
                    renderFiltered();
                });
            });
        }

        function renderLangFilter() {
            var row = root.querySelector('#mdIssueLangRow');
            var sel = root.querySelector('#mdIssueLangFilter');
            if (!row || !sel) return;
            var typeFilter = root.querySelector('#mdTypeFilter').value;
            var candidates = lastIssueList.filter(function (i) {
                return i.Type === 'AudioLanguage' && (!typeFilter || typeFilter === 'AudioLanguage');
            });
            if (!candidates.length) { row.style.display = 'none'; return; }
            var langs = {};
            candidates.forEach(function (i) {
                try {
                    var d = JSON.parse(i.DetailsJson || '{}');
                    (d.removeLanguages || []).forEach(function (l) { if (l) langs[l] = true; });
                } catch (e) { /* malformed details — skip */ }
            });
            var opts = Object.keys(langs).sort();
            if (!opts.length) { row.style.display = 'none'; return; }
            row.style.display = 'flex';
            root.querySelector('#mdIssueLangLabel').textContent = strings.langFilterLabel || 'Filter by audio language:';
            var current = issueLangFilter && opts.indexOf(issueLangFilter) >= 0 ? issueLangFilter : '';
            issueLangFilter = current;
            var html = '<option value="">' + esc(strings.langFilterAll || 'All languages') + '</option>';
            opts.forEach(function (l) {
                html += '<option value="' + esc(l) + '"' + (l === current ? ' selected' : '') + '>' + esc(l) + '</option>';
            });
            sel.innerHTML = html;
        }

        function canBulkApprove(issue) {
            return issue.Status === 'Detected' && !issue.FixBlockedReason &&
                !(issue.Warnings || []).some(function (w) { return w.Blocking; });
        }

        function renderFiltered() {
            var el = root.querySelector('#mdIssueList');
            var approveAllBtn = root.querySelector('#mdApproveAll');
            var clearAllBtn = root.querySelector('#mdClearAllIssues');
            var pager = root.querySelector('#mdIssuePager');
            var typeFilter = root.querySelector('#mdTypeFilter').value;
            var textFilter = (root.querySelector('#mdIssueFilter').value || '').trim().toLowerCase();
            var list = lastIssueList.filter(function (i) {
                return issueMatchesFilters(i, typeFilter, textFilter, issueLangFilter);
            });
            lastFilteredIssues = list;
            var approvableCount = list.filter(canBulkApprove).length;

            // Pagination: clamp page after filter changes, then slice.
            var totalPages = Math.max(1, Math.ceil(list.length / ISSUE_PAGE_SIZE));
            if (issuePage > totalPages) issuePage = totalPages;
            if (issuePage < 1) issuePage = 1;
            var pageStart = (issuePage - 1) * ISSUE_PAGE_SIZE;
            var pageSlice = list.slice(pageStart, pageStart + ISSUE_PAGE_SIZE);

            // ponytail: skip re-render when the visible list is unchanged. Without this the 3s status poll
            // during a fix run replays mdFade on every card (visible flicker) and swaps click listeners mid-interaction.
            // UI-05: include failure state (success flag + attempt timestamp + list length) so a newly-arrived
            // LastAttemptMessage on an already-queued row forces a re-render, and so a filtered total-count
            // change (bulk ignore of a non-visible page) updates the Approve-all label.
            var fingerprint = typeFilter + '|' + textFilter + '|' + issueLangFilter + '|' + issuePage + '|' + list.length + '|' + approvableCount + '|' +
                pageSlice.map(function (i) {
                    var f = i.LastAttemptSuccess === false ? ('F:' + (i.LastAttemptAtUtc || '') + ':' + (i.LastAttemptMessage || '')) : 'O';
                    var moveFingerprint = '';
                    if (i.Type === 'Ungrouped' || i.Type === 'Misplaced') {
                        try { var movePlan = JSON.parse(i.DetailsJson || '{}').movePlan; moveFingerprint = movePlan && movePlan.fingerprint || ''; } catch (_) { /* Malformed plans remain blocked by the server. */ }
                    }
                    return i.Id + ':' + i.Status + ':' + i.RepairFailed + ':' + (i.FixBlockedReason || '') + ':' + JSON.stringify(i.Warnings) + ':' + moveFingerprint + ':' + f;
                }).join(',');
            if (el.getAttribute('data-fingerprint') === fingerprint) return;
            el.setAttribute('data-fingerprint', fingerprint);

            if (typeFilter && approvableCount > 1) {
                var ft = strings.types[typeFilter] || {};
                approveAllBtn.style.display = '';
                approveAllBtn.querySelector('.mdApproveAllLabel').textContent = fmt(ft.approveAll || strings.approveAll, approvableCount);
                root.querySelector('#mdApproveAllNote').textContent = strings.approveAllNote;
            } else if (!typeFilter && approvableCount > 0) {
                approveAllBtn.style.display = '';
                approveAllBtn.querySelector('.mdApproveAllLabel').textContent = fmt(strings.approveAllTypes, approvableCount);
                root.querySelector('#mdApproveAllNote').textContent = strings.approveAllNote;
            } else {
                approveAllBtn.style.display = 'none';
                root.querySelector('#mdApproveAllNote').textContent = '';
            }

            var rescanBtn = root.querySelector('#mdRescanApproveAll');
            var rescanCount = list.filter(needsRescan).length;
            if (rescanBtn) {
                rescanBtn.style.display = rescanCount > 0 ? '' : 'none';
                rescanBtn.querySelector('.mdRescanApproveAllLabel').textContent = fmt(strings.rescanApproveAll, rescanCount);
            }

            if (list.length > 0) {
                clearAllBtn.style.display = '';
                if (showingDismissed) {
                    clearAllBtn.classList.add('mdRevertMode');
                    root.querySelector('#mdClearAllIcon').textContent = 'restore_from_trash';
                    root.querySelector('#mdClearAllLabel').textContent = fmt(strings.unignoreAll || 'Unignore all {0} shown', list.length);
                    // Approve-all makes no sense on ignored rows — hide it in this view.
                    approveAllBtn.style.display = 'none';
                } else {
                    clearAllBtn.classList.remove('mdRevertMode');
                    root.querySelector('#mdClearAllIcon').textContent = 'delete_sweep';
                    root.querySelector('#mdClearAllLabel').textContent = fmt(strings.clearAll || 'Clear all {0} shown', list.length);
                }
            } else {
                clearAllBtn.style.display = 'none';
            }

            if (!list.length) {
                var empty = showingDismissed ? (strings.noIgnored || 'No ignored issues.') : strings.noIssues;
                el.innerHTML = '<div class="fieldDescription" style="margin-top:1em;">' + esc(empty) + '</div>';
                pager.style.display = 'none';
                return;
            }

            if (totalPages > 1) {
                pager.style.display = 'flex';
                root.querySelector('#mdIssuePageInfo').textContent = fmt(strings.page || 'Page {0} of {1}', issuePage, totalPages);
                root.querySelector('#mdIssuePagePrev').disabled = issuePage <= 1;
                root.querySelector('#mdIssuePageNext').disabled = issuePage >= totalPages;
            } else {
                pager.style.display = 'none';
            }

            var html = '';
            pageSlice.forEach(function (i) {
                var t = strings.types[i.Type] || { title: i.Type, color: '#888' };
                var isQueued = i.Status === 'Queued';
                var statusBadge = isQueued
                    ? '<span class="mdBadge" style="background:rgba(0,164,220,.35);color:#fff;">queued</span> '
                    : '';
                // "You restored this before" badge: rendered on any Detected row whose (path, type)
                // is in the restored_paths table. Tells the user why the row isn't auto-queuing
                // even though the type is set to Automatic — MediaDash won't override their restore.
                var restoredBadge = i.WasPreviouslyRestored
                    ? '<span class="mdBadge" style="background:rgba(245,158,11,.20);color:#fbbf24;border:1px solid rgba(245,158,11,.5);" title="MediaDash will not auto-fix this file again because you restored it before. Click Approve to fix it anyway, or Ignore this to stop seeing it.">↩ you restored this before</span> '
                    : '';
                // UI-01: issues can carry data-loss warnings (e.g. unwanted-subtitle fix will also drop
                // bitmap subs that the container can't hold under a copy-only remux). The scanner marks the
                // blocking ones; auto-queue holds them Detected and the user must consent via Approve. Hoist
                // a "⚠ needs review" chip so the warning isn't invisible under the generic Approve copy.
                var warnings = Array.isArray(i.Warnings) ? i.Warnings : [];
                var blockingWarnings = warnings.filter(function (w) { return w && w.Blocking; });
                var warningBadge = blockingWarnings.length
                    ? '<span class="mdBadge" style="background:rgba(239,68,68,.25);color:#fecaca;border:1px solid rgba(239,68,68,.6);" title="Approving this fix also removes something that cannot be restored by the normal recycle-bin round-trip. Read the warning below.">⚠ needs review</span> '
                    : '';
                var verifyBtn = '';
                var isManagedArtwork = i.Type === 'CorruptArtwork';
                var isMissingArtwork = false;
                if (isManagedArtwork) {
                    try { isMissingArtwork = JSON.parse(i.DetailsJson || '{}').reason === 'missing'; } catch (_) { /* Older findings may lack a reason. */ }
                }
                if (i.Type === 'Playability' && i.ItemId && i.ItemId !== '00000000-0000-0000-0000-000000000000') {
                    verifyBtn = '<button is="emby-button" type="button" class="raised mdOpenInJellyfin" data-item="' + esc(i.ItemId) + '" style="font-size:80%;"><span class="material-icons" style="font-size:1em;vertical-align:middle;margin-right:.2em;">play_arrow</span>Try to play</button>';
                }
                if (isManagedArtwork && i.ItemId && i.ItemId !== '00000000-0000-0000-0000-000000000000') {
                    verifyBtn = '<button is="emby-button" type="button" class="raised mdOpenInJellyfin" data-item="' + esc(i.ItemId) + '"><span>' + esc(strings.openItemInJellyfin) + '</span></button>';
                }
                // Split into (buttons-only) and (hint) so the mdIssueActions column stays narrow
                // and the hint wraps under the body — otherwise the flex column expands to fit
                // multi-line hint text and squeezes the body to 0px.
                var actionButtons, actionHint;
                var isDismissed = i.Status === 'Dismissed';
                if (isDismissed) {
                    actionButtons = verifyBtn +
                        '<button is="emby-button" type="button" class="raised mdUnignore" data-id="' + i.Id + '" style="font-size:80%;"><span class="material-icons" style="font-size:1em;vertical-align:-2px;margin-right:.25em;">restore_from_trash</span><span>' + esc(strings.unignore || 'Unignore') + '</span></button>';
                    actionHint = '<strong>' + esc(strings.unignore || 'Unignore') + ':</strong> ' + esc(strings.unignoreHint || 'Move this issue back to the open list.');
                } else if (isQueued) {
                    actionButtons = verifyBtn +
                        '<button is="emby-button" type="button" class="raised mdUndoAction" data-id="' + i.Id + '" style="font-size:80%;" title="' + esc(strings.undoActionHint) + '"><span class="material-icons" style="font-size:1em;vertical-align:-2px;margin-right:.25em;">undo</span><span>' + esc(strings.undoAction) + '</span></button>' +
                        '<button is="emby-button" type="button" class="raised mdDismiss" data-id="' + i.Id + '" style="font-size:80%;"><span>' + esc(strings.dismiss) + '</span></button>';
                    if (i.LastAttemptSuccess === false) {
                        actionHint = '<span class="material-icons" style="font-size:15px;vertical-align:middle;color:#ef4444;">error_outline</span> Retry on next fix run — the last attempt failed. Details below. Dismiss to stop retrying.';
                    } else {
                        actionHint = '<span class="material-icons" style="font-size:15px;vertical-align:middle;color:var(--md-primary);">schedule</span> Queued for next fix run — click "Run fixes now" on Overview. Change your mind? Click Undo.';
                    }
                } else {
                    var actionLabel = isMissingArtwork ? strings.clearMissingArtwork : (i.RepairFailed ? (strings.delete || 'Delete') : (t.action || strings.approve));
                    actionButtons = verifyBtn +
                        '<button is="emby-button" type="button" class="raised button-submit mdApprove" data-id="' + i.Id + '" style="font-size:80%;" title="' + esc(isMissingArtwork ? strings.clearMissingArtworkHint : (i.RepairFailed ? i.SuggestedFix : (t.actionHint || ''))) + '"><span>' + esc(actionLabel) + '</span></button>' +
                        '<button is="emby-button" type="button" class="raised mdDismiss" data-id="' + i.Id + '" style="font-size:80%;"><span>' + esc(strings.dismiss) + '</span></button>';
                    // UI-02: pick the hint variant that matches the effective disposal (Recycle vs Permanent)
                    // when the scanner provides doRecycle / doPermanent copy, so Approve describes what will
                    // actually happen under the user's current settings. Fallback to the generic actionHint for
                    // fix types with no disposal (e.g. MissingSubtitles — nothing is removed).
                    var disposal = getIssueDisposal(i.Type);
                    var effectiveHint;
                    if (disposal === 'Permanent' && t.doPermanent) effectiveHint = t.doPermanent;
                    else if (disposal === 'RecycleBin' && t.doRecycle) effectiveHint = t.doRecycle;
                    else effectiveHint = t.actionHint || strings.approveHint;
                    if (isMissingArtwork) effectiveHint = strings.clearMissingArtworkHint;
                    if (i.RepairFailed) effectiveHint = disposal === 'Permanent'
                        ? 'Permanently delete the file after failed repair.'
                        : 'Move the file to the recycle bin after failed repair.';
                    if (getDryRun()) {
                        effectiveHint = 'Dry-run: logged to History only — ' + effectiveHint;
                    }
                    var approveLabel = esc(actionLabel);
                    var dismissLabel = esc(strings.dismiss);
                    actionHint =
                        '<strong>' + approveLabel + ':</strong> ' + esc(effectiveHint) +
                        ' · <strong>' + dismissLabel + ':</strong> ' + esc(strings.dismissHint);
                }
                var keeperPath = '';
                if (!isDismissed && i.FixBlockedReason) {
                    actionHint = esc(i.FixBlockedReason);
                    if (!isQueued) {
                        actionButtons = verifyBtn +
                            (i.CanMoveAnyway
                                ? '<button is="emby-button" type="button" class="raised mdMoveAnyway" data-id="' + i.Id + '" title="' + esc(strings.moveAnywayHint) + '"><span>' + esc(strings.moveAnyway) + '</span></button>'
                            : i.NeedsSeason
                                ? '<button is="emby-button" type="button" class="raised mdEnterSeason" data-id="' + i.Id + '" data-path="' + esc(i.Path) + '" title="' + esc(strings.enterSeasonHint) + '"><span>' + esc(strings.enterSeason) + '</span></button>'
                            : i.FixRequiresRescan
                                ? '<button is="emby-button" type="button" class="raised mdIssueRescan" data-type="' + esc(i.Type) + '"><span>' + esc(strings.scanNowLabel) + '</span></button>'
                                : (i.Type === 'Stale' ? '' : '<button is="emby-button" type="button" class="raised mdIssueSettings"><span>' + esc(strings.fixSettings) + '</span></button>')) +
                            '<button is="emby-button" type="button" class="raised mdDismiss" data-id="' + i.Id + '"><span>' + esc(strings.dismiss) + '</span></button>';
                    }
                }
                if (i.Type === 'Duplicate' && i.DetailsJson) {
                    try { var d = JSON.parse(i.DetailsJson); keeperPath = d && d.keeperPath ? d.keeperPath : ''; } catch (e) { keeperPath = ''; }
                }
                var pathBlock;
                if (keeperPath) {
                    pathBlock = '<div class="fieldDescription" style="margin-top:.3em;"><b style="color:#ef4444;">Delete this copy:</b></div>' +
                        '<div class="fieldDescription mdIssuePath" data-path="' + esc(i.Path) + '" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;cursor:pointer;" title="Click to open this folder in the Files tab">' + esc(i.Path) + '</div>' +
                        '<div class="fieldDescription" style="margin-top:.3em;"><b style="color:#22c55e;">Better copy (kept):</b></div>' +
                        '<div class="fieldDescription mdIssuePath" data-path="' + esc(keeperPath) + '" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;cursor:pointer;" title="Click to open the keeper folder in the Files tab">' + esc(keeperPath) + '</div>';
                } else if (isManagedArtwork) {
                    pathBlock = '<div class="fieldDescription" style="overflow-wrap:anywhere;">' + esc(i.Path) + '</div>';
                } else {
                    pathBlock = '<div class="fieldDescription mdIssuePath" data-path="' + esc(i.Path) + '" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;cursor:pointer;" title="Click to open this folder in the Files tab">' + esc(i.Path) + '</div>';
                }
                html += '<div class="mdCard mdIssueCard mdFade" style="--md-issue-accent:' + t.color + ';' + ((isQueued || isDismissed) ? 'opacity:.72;' : '') + '" data-issue="' + i.Id + '">' +
                    '<div class="mdIssueLayout">' +
                        '<div class="mdIssueDisc"><span class="material-icons">' + esc(t.icon || 'info') + '</span></div>' +
                        '<div class="mdIssueBody">' +
                            '<div class="mdIssueTitleRow">' +
                                '<span class="mdIssueFilename">' + esc(i.FileName) + '</span>' +
                                '<span class="mdIssueTagChip">' + esc(isManagedArtwork ? (isMissingArtwork ? strings.missingArtwork : strings.corruptArtwork) : t.title) + '</span>' +
                                statusBadge +
                                restoredBadge +
                                warningBadge +
                            '</div>' +
                            pathBlock +
                            '<div class="mdIssueHint">' + esc(isMissingArtwork ? strings.missingArtworkHint : i.SuggestedFix) +
                                (i.SizeSavings > 0 ? ' <span class="mdIssueSavings">(' + esc(bytes(i.SizeSavings)) + ')</span>' : '') +
                            '</div>' +
                            ns.MediaDash.movePreviewHtml(i, strings, false) +
                        '</div>' +
                        '<div class="mdIssueActions">' + actionButtons + '</div>' +
                    '</div>' +
                    '<div class="mdIssueFooter">' +
                        (warnings.length ? warnings.map(function (w) {
                            var bg = w.Blocking ? 'rgba(239,68,68,.12)' : 'rgba(245,158,11,.10)';
                            var icon = w.Blocking ? 'error_outline' : 'warning_amber';
                            var color = w.Blocking ? '#fecaca' : '#fde68a';
                            return '<div class="fieldDescription" style="margin-top:.4em;padding:.5em .7em;background:' + bg + ';border-radius:4px;color:' + color + ';">' +
                                '<span class="material-icons" style="font-size:15px;vertical-align:middle;margin-right:.3em;">' + icon + '</span>' +
                                esc(w.Message) + '</div>';
                        }).join('') : '') +
                        '<div class="mdIssueActionHint">' + actionHint + '</div>' +
                        (i.LastAttemptSuccess === false && i.LastAttemptMessage
                            ? '<div class="fieldDescription" style="margin-top:.3em;color:#ef4444;"><span class="material-icons" style="font-size:15px;vertical-align:middle;">error_outline</span> Last attempt failed: ' + esc(i.LastAttemptMessage) + (i.LastAttemptAtUtc ? ' <span style="color:var(--md-ink-muted);">(' + esc(new Date(i.LastAttemptAtUtc).toLocaleString()) + ')</span>' : '') + '</div>'
                            : '') +
                        '<div class="fieldDescription mdRowMsg mdIssueRowMsg"></div>' +
                    '</div>' +
                '</div>';
            });
            el.innerHTML = html;
            wireRowHandlers(el);
        }

        // Row-button click handlers get re-attached on every renderFiltered
        // but attach to the fresh innerHTML nodes; the previous set dies with
        // the replaced DOM, so no lifetime.own bookkeeping is needed.
        function wireRowHandlers(el) {
            el.querySelectorAll('.mdMovePreview').forEach(function (preview) {
                var loaded = false;
                var shown = 100;
                function paint() {
                    var issue = lastIssueList.find(function (candidate) { return String(candidate.Id) === preview.getAttribute('data-id'); });
                    var content = preview.querySelector('.mdMovePreviewContent');
                    if (!issue || !content) return;
                    content.innerHTML = ns.MediaDash.movePreviewHtml(issue, strings, true, shown);
                    var more = content.querySelector('.mdMovePreviewMore');
                    if (more) more.addEventListener('click', function () { shown += 100; paint(); });
                }
                preview.addEventListener('toggle', function () {
                    if (!preview.open || loaded) return;
                    loaded = true;
                    paint();
                });
            });

            el.querySelectorAll('.mdApprove').forEach(function (b) {
                b.addEventListener('click', function () { rowAction(b, 'Approve', strings.approved); });
            });
            el.querySelectorAll('.mdIssueSettings').forEach(function (b) {
                b.addEventListener('click', function () { deps.showTab('settings'); });
            });
            el.querySelectorAll('.mdIssueRescan').forEach(function (b) {
                b.addEventListener('click', function () { deps.rescan(b.getAttribute('data-type')); });
            });
            el.querySelectorAll('.mdMoveAnyway').forEach(function (b) {
                b.addEventListener('click', function () { messageAction(b, 'Issues/' + b.getAttribute('data-id') + '/MoveAnyway'); });
            });
            el.querySelectorAll('.mdEnterSeason').forEach(function (b) {
                b.addEventListener('click', function () { openSeasonDialog(b); });
            });
            el.querySelectorAll('.mdDismiss').forEach(function (b) {
                b.addEventListener('click', function () { rowAction(b, 'Dismiss', strings.dismissed); });
            });
            el.querySelectorAll('.mdUndoAction').forEach(function (b) {
                b.addEventListener('click', function () { undoRowAction(b); });
            });
            // Un-ignore reuses the /Issues/{id}/Revert endpoint via undoRowAction — same guard,
            // same reload path, so a mis-click on a Dismissed row lands the same as the Queued undo.
            el.querySelectorAll('.mdUnignore').forEach(function (b) {
                b.addEventListener('click', function () { undoRowAction(b); });
            });
            el.querySelectorAll('.mdIssuePath').forEach(function (p) {
                p.addEventListener('click', function () { openInFileBrowser(p.getAttribute('data-path')); });
            });
            el.querySelectorAll('.mdOpenInJellyfin').forEach(function (b) {
                b.addEventListener('click', function () {
                    var id = b.getAttribute('data-item');
                    // UI-07: hard-coded '/web/' misses the base path when Jellyfin is served under
                    // /jellyfin (reverse-proxy deployments) or hosts the web client at a different
                    // origin. ApiClient.serverAddress() returns the full server base
                    // (http://host:port[/base]); appending the web client path from there preserves
                    // the deployment prefix. Fallback to the hard-coded path only if ApiClient is
                    // somehow unavailable at the moment of click.
                    var base = (window.ApiClient && ApiClient.serverAddress && ApiClient.serverAddress()) || '';
                    var url = (base.replace(/\/$/, '')) + '/web/#/details?id=' + encodeURIComponent(id);
                    window.open(url, '_blank', 'noopener');
                });
            });
        }

        // "Enter season number": the plugin's own modal (same look as the scan picker), not a browser prompt.
        function openSeasonDialog(button) {
            var dialog = root.querySelector('#mdSeasonDialog');
            if (!dialog) return;
            var input = dialog.querySelector('#mdSeasonInput');
            var error = dialog.querySelector('#mdSeasonError');
            var path = button.getAttribute('data-path') || '';
            dialog.querySelector('#mdSeasonFile').textContent = path.split(/[\\/]/).pop();
            input.value = '';
            error.style.display = 'none';
            dialog.style.display = 'flex';
            setTimeout(function () { input.focus(); }, 0);
            function close() {
                dialog.style.display = '';
                dialog.querySelector('#mdSeasonSave').onclick = null;
                dialog.querySelector('#mdSeasonCancel').onclick = null;
                input.onkeydown = null;
                dialog.onclick = null;
            }
            function save() {
                var entered = String(input.value || '').trim();
                if (!/^\d{1,3}$/.test(entered)) {
                    error.textContent = strings.enterSeasonInvalid;
                    error.style.display = '';
                    input.focus();
                    return;
                }
                close();
                messageAction(button, 'Issues/' + button.getAttribute('data-id') + '/Season?season=' + encodeURIComponent(String(parseInt(entered, 10))));
            }
            dialog.querySelector('#mdSeasonSave').onclick = save;
            dialog.querySelector('#mdSeasonCancel').onclick = close;
            input.onkeydown = function (e) {
                if (e.key === 'Enter') save();
                if (e.key === 'Escape') close();
            };
            dialog.onclick = function (e) { if (e.target === dialog) close(); };
        }

        // Open findings that only a fresh scan can unblock (the rows showing "Scan now").
        function needsRescan(issue) {
            return issue.Status === 'Detected' && issue.FixRequiresRescan === true;
        }

        // POSTs an action whose response is a sentence for the user, shows it on the row and refreshes.
        function messageAction(button, path) {
            var row = button.closest('[data-issue]');
            button.disabled = true;
            function show(text) {
                if (row && row.querySelector('.mdRowMsg')) row.querySelector('.mdRowMsg').textContent = text;
            }
            function readText(r) {
                if (!r || typeof r.text !== 'function') return Promise.resolve(typeof r === 'string' ? r : '');
                return r.text().then(function (t) {
                    try { var parsed = JSON.parse(t); return typeof parsed === 'string' ? parsed : t; } catch (e) { return t; }
                });
            }
            post(path).then(function (r) {
                return readText(r).then(function (text) {
                    show(text || strings.approved);
                    if (row) row.style.opacity = '.55';
                    refresh(true);
                });
            }, function (err) {
                button.disabled = false;
                if (err && err.status === 404) {
                    // A scan replaced this finding (Move anyway rescans Ungrouped media, for one):
                    // the row's id is gone. Say so and load the current list instead of a bare 404.
                    show(strings.issueReplaced);
                    setTimeout(activate, 1500);
                    return;
                }
                var response = err && typeof err.text === 'function' ? err : null;
                (response ? readText(response) : Promise.resolve(deps.errorMessage(err))).then(function (text) {
                    show(strings.issueActionFailed + ' ' + text);
                });
            });
        }

        function rowAction(button, action, doneText) {
            var id = button.getAttribute('data-id');
            button.disabled = true;
            post('Issues/' + id + '/' + action).then(function () {
                var row = button.closest('[data-issue]');
                // Keep the in-memory issue list in sync with the server. Without this the
                // renderFiltered fingerprint (Id + Status) stays stale, and a follow-up
                // activate() short-circuits its DOM re-render because "nothing changed" — that's
                // the class of bug that broke undo before we mutated Status here.
                var issue = lastIssueList.find(function (i) { return String(i.Id) === String(id); });
                if (issue) issue.Status = (action === 'Approve') ? 'Queued' : 'Dismissed';
                if (action === 'Dismiss') {
                    // Row disappears immediately without a re-fetch — a fresh /Issues call would
                    // unmask any items the user's current filter / pagination was hiding, so 2
                    // visible items could become 48. Instead: drop the row from DOM, drop it from
                    // the cached list, update the badge, and rebuild the fingerprint so a later
                    // renderFiltered doesn't accidentally re-add it. Undo is still available from
                    // the Dismissed filter tab.
                    var listEl = root.querySelector('#mdIssueList');
                    var idx = lastIssueList.findIndex(function (x) { return String(x.Id) === String(id); });
                    if (idx >= 0) lastIssueList.splice(idx, 1);
                    row.remove();
                    updateTabBadge('#mdIssueBadge', lastIssueList.length);
                    if (listEl) listEl.removeAttribute('data-fingerprint');
                    renderFiltered();
                    refresh(true);
                    return;
                }

                row.querySelector('.mdRowMsg').textContent = doneText;
                row.style.opacity = '.55';
                // Replace the action cluster with a single Undo button. Approve users often
                // mis-click; the Fix task runs on a schedule so there's a real window to change
                // your mind. Revert flips the issue back to Detected on the server.
                var actionsCell = row.querySelector('.mdIssueActions');
                if (actionsCell) {
                    actionsCell.innerHTML = '<button is="emby-button" type="button" class="raised mdUndoAction" data-id="' + id + '" style="font-size:80%;"><span class="material-icons" style="font-size:1em;vertical-align:-2px;margin-right:.25em;">undo</span><span>' + esc(strings.undoAction) + '</span></button>';
                    var undoBtn = actionsCell.querySelector('.mdUndoAction');
                    undoBtn.addEventListener('click', function () { undoRowAction(undoBtn); });
                }
                // Refresh in the background so PendingFixCount reflects the approval right away — otherwise
                // clicking "Run fixes now" immediately after approving reads the stale count and shows "nothing to fix".
                refresh(true);
            }, function (err) {
                button.disabled = false;
                button.closest('[data-issue]').querySelector('.mdRowMsg').textContent =
                    strings.issueActionFailed + ' ' + deps.errorMessage(err);
            });
        }

        function undoRowAction(button) {
            var id = button.getAttribute('data-id');
            button.disabled = true;
            post('Issues/' + id + '/Revert').then(function () {
                // Bust the fingerprint so renderFiltered doesn't short-circuit. The list
                // returned from activate() will report Status=Detected, which is what the row
                // ALREADY reads as in lastIssueList after a manual DOM-only Approve — leaving
                // the fingerprint identical and the render skipped. Explicitly clearing it here
                // forces the re-render every time.
                var listEl = root.querySelector('#mdIssueList');
                if (listEl) listEl.removeAttribute('data-fingerprint');
                activate();
                refresh(true);
            }, function () {
                button.disabled = false;
                var row = button.closest('[data-issue]');
                if (row) {
                    row.querySelector('.mdRowMsg').textContent = strings.undoFailedMsg;
                }
            });
        }

        // ---------- Issue context menu (per-list) ----------

        // Wires the right-click menu on the issue list. Uses event delegation
        // on the list container so cards added on later renders pick it up
        // automatically. Global dismissal listeners (click/keydown/scroll) are
        // owned by the top-level wireIssueMenuGlobals() via ownGlobal, so
        // pagehide disposes them and every pageshow re-attaches fresh.
        function installIssueContextMenu() {
            var list = root.querySelector('#mdIssueList');
            var menu = root.querySelector('#mdIssueCtxMenu');
            if (!list || !menu || list.dataset.ctxInit) return;
            list.dataset.ctxInit = '1';
            list.addEventListener('contextmenu', function (ev) {
                var card = ev.target && ev.target.closest ? ev.target.closest('.mdIssueCard') : null;
                if (!card) return;
                var issueId = card.getAttribute('data-issue');
                var issue = lastIssueList.find(function (i) { return String(i.Id) === String(issueId); });
                if (!issue) return;
                ev.preventDefault();
                showIssueContextMenu(ev.clientX, ev.clientY, issue);
            });
        }

        function showIssueContextMenu(x, y, issue) {
            var menu = root.querySelector('#mdIssueCtxMenu');
            var typeTitle = (strings.types[issue.Type] && strings.types[issue.Type].title) || issue.Type;
            var ext = fileExtOf(issue.Path) || '(no extension)';
            var cat = mediaCategoryOf(issue.Path);
            var folderMatches = lastIssueList.filter(function (i) { return sameFolder(i.Path, issue.Path); });
            var typeMatches = lastIssueList.filter(function (i) { return i.Type === issue.Type; });
            var extMatches = lastIssueList.filter(function (i) { return fileExtOf(i.Path) === ext.toLowerCase() || (ext === '(no extension)' && fileExtOf(i.Path) === ''); });
            var catMatches = lastIssueList.filter(function (i) { return mediaCategoryOf(i.Path) === cat; });

            var opts = [
                { label: fmt(strings.ctxIgnoreFolder || 'Ignore all issues in this folder', '') + ' (' + folderMatches.length + ')', ids: folderMatches.map(function (i) { return i.Id; }) },
                { label: fmt(strings.ctxIgnoreType || 'Ignore all "{0}" issues', typeTitle) + ' (' + typeMatches.length + ')', ids: typeMatches.map(function (i) { return i.Id; }) },
                { label: fmt(strings.ctxIgnoreExt || 'Ignore all {0} files', ext) + ' (' + extMatches.length + ')', ids: extMatches.map(function (i) { return i.Id; }) },
                { label: fmt(strings.ctxIgnoreMedia || 'Ignore all {0} issues', cat) + ' (' + catMatches.length + ')', ids: catMatches.map(function (i) { return i.Id; }) }
            ];

            var html = '';
            opts.forEach(function (o, idx) {
                html += '<div class="mdCtxItem" data-idx="' + idx + '" style="padding:.55em 1em;cursor:pointer;white-space:nowrap;" onmouseover="this.style.background=\'rgba(255,255,255,.08)\'" onmouseout="this.style.background=\'\'">' + esc(o.label) + '</div>';
            });
            menu.innerHTML = html;

            // Show first so we can measure, then clamp inside the viewport.
            menu.style.display = 'block';
            menu.style.left = '0px';
            menu.style.top = '0px';
            var rect = menu.getBoundingClientRect();
            var px = Math.min(x, window.innerWidth - rect.width - 8);
            var py = Math.min(y, window.innerHeight - rect.height - 8);
            menu.style.left = Math.max(4, px) + 'px';
            menu.style.top = Math.max(4, py) + 'px';

            menu.querySelectorAll('.mdCtxItem').forEach(function (it) {
                it.addEventListener('click', function (ev) {
                    ev.stopPropagation();
                    var idx = +it.getAttribute('data-idx');
                    var opt = opts[idx];
                    closeIssueContextMenu();
                    if (!opt.ids.length) {
                        window.alert(strings.ctxNothing || 'No matching open issues.');
                        return;
                    }
                    if (!window.confirm(fmt(strings.ctxConfirm || 'Ignore {0} issue(s)?', opt.ids.length))) return;
                    bulkIssueAction(opt.ids, 'Dismiss').then(function () { activate(); refresh(true); }).catch(function (err) { window.alert(errorMessage(err)); });
                });
            });
        }

        function renderIssues(list) {
            lastIssueList = list || [];
            // IssueWarning uses explicit lowercase JSON names even when the issue DTO uses PascalCase.
            lastIssueList.forEach(function (issue) {
                issue.Warnings = (Array.isArray(issue.Warnings) ? issue.Warnings : []).filter(Boolean).map(function (w) {
                    return { Blocking: w.blocking === undefined ? w.Blocking : w.blocking, Message: w.message === undefined ? w.Message : w.message };
                });
            });
            // UI-17: openCount tracks the latest open-view fetch and is what the Issues badge
            // reads. Without this, loading the Ignored view overwrites lastIssueList with the
            // ignored rows and the badge drops to that count (or zero if nothing is ignored),
            // under-reporting open work until a status poll re-renders.
            if (!showingDismissed) {
                openCount = lastIssueList.length;
                updateTabBadge('#mdIssueBadge', openCount);
            }
            renderTotals();
            renderLangFilter();
            renderFiltered();
        }

        function activate() {
            // UI-03: pagehide invalidates lifetime and the wrapped api() returns a never-settling
            // promise; the .then/.catch below never run and `loading` sticks true across pageshow,
            // silently blocking every subsequent load until a full page reload. If the recorded
            // lifetime generation differs from the current one, the previous load is lost — clear
            // the latch and start fresh.
            if (loading && loadingToken !== null && loadingToken !== lifetime.current()) {
                loading = false;
            }
            if (loading) return;
            // Capture the view mode so a late-arriving response for the previous view (open vs
            // ignored) can be discarded instead of rendered under the new UI (UI-04).
            var requestedView = showingDismissed;
            loading = true;
            loadingToken = lifetime.current();
            loadingView = requestedView;
            // ponytail: always fetch the full list for the current view — the type filter is applied
            // client-side by renderFiltered so the totals row can show counts for every type
            // simultaneously. The Dismissed view uses the same rendering path.
            var url = requestedView ? 'Issues?status=Dismissed' : 'Issues?openOnly=true';
            api(url)
                .then(function (list) {
                    loading = false;
                    if (requestedView !== showingDismissed) {
                        // The user toggled the view while this fetch was outstanding — the response
                        // is for the wrong list. Discard and kick a fresh request for the new view.
                        activate();
                        return;
                    }
                    if (!Array.isArray(list)) throw new Error('Invalid Issues response');
                    var errorBanner = root.querySelector('#mdIssueLoadError');
                    if (errorBanner) errorBanner.style.display = 'none';
                    renderIssues(list);
                })
                .catch(function () {
                    loading = false;
                    if (requestedView !== showingDismissed) {
                        activate();
                        return;
                    }
                    var errorBanner = root.querySelector('#mdIssueLoadError');
                    if (!errorBanner) return;
                    root.querySelector('#mdIssueLoadErrorText').textContent = strings.issuesLoadFailed || "Couldn't load issues. Previously loaded results may be out of date. Retry, or check Jellyfin's logs if this continues.";
                    var retry = root.querySelector('#mdIssueLoadRetry');
                    retry.textContent = strings.retry || 'Retry';
                    retry.onclick = activate;
                    errorBanner.style.display = '';
                });
        }

        function setLangFilter(l) {
            issueLangFilter = l || '';
            issuePage = 1;
            renderFiltered();
        }

        function nextPage() { issuePage++; renderFiltered(); }
        function prevPage() { if (issuePage > 1) { issuePage--; renderFiltered(); } }

        function toggleShowIgnored() {
            showingDismissed = !showingDismissed;
            // Reset paging + type filter — dismissed and open sets are disjoint so a stale
            // filter would just show "no results" for the other view.
            issuePage = 1;
            root.querySelector('#mdTypeFilter').value = '';
            // Force a fresh render — the fingerprint is content-based, not mode-based.
            var listEl = root.querySelector('#mdIssueList');
            if (listEl) listEl.removeAttribute('data-fingerprint');
            activate();
        }

        // dispose() intentionally no-op. Per-row handlers are re-attached on
        // every renderFiltered() to fresh innerHTML nodes; the old ones die
        // with their DOM. The `list.contextmenu` handler is DOM-scoped and
        // guarded by list.dataset.ctxInit so it wires once per persistent
        // view DOM. Global click/keydown/scroll dismissal listeners are owned
        // by the top-level wireIssueMenuGlobals() via ownGlobal (06-A.2), so
        // they round-trip cleanly on pagehide/pageshow. Reserved for future
        // teeth when 06-D pulls the context-menu into its own owned lifecycle.
        function dispose() {}

        return {
            activate: activate,
            render: renderFiltered,
            dispose: dispose,
            setLangFilter: setLangFilter,
            setPage: function (n) { issuePage = n | 0; renderFiltered(); },
            nextPage: nextPage,
            prevPage: prevPage,
            toggleShowIgnored: toggleShowIgnored,
            showingIgnored: function () { return showingDismissed; },
            // UI-17: badge-authoritative count = the last open-view fetch's size. When the user
            // opens the ignored view, lastIssueList holds ignored rows — using its length would
            // under-report open work. openCount is only touched by open-view fetches.
            count: function () { return showingDismissed ? openCount : lastIssueList.length; },
            rescanTypes: function () {
                return lastFilteredIssues.filter(needsRescan).map(function (i) { return i.Type; })
                    .filter(function (type, index, all) { return all.indexOf(type) === index; });
            },
            filteredIds: function (approvableOnly) { return lastFilteredIssues.filter(function (i) { return !approvableOnly || canBulkApprove(i); }).map(function (i) { return i.Id; }); },
            installContextMenu: installIssueContextMenu
        };
    };
})();
