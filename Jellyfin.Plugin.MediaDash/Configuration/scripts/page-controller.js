// Owns page lifecycle, shared requests and coordination between tab components.
(function () {
    'use strict';
    window.MediaDash.mountPage = function (root) {
        if (root.dataset.mdMounted) return;
        root.dataset.mdMounted = '1';

            'use strict';


            var PLUGIN_ID = '38bdb090-b763-4294-934b-b54ade4d9d6d';


            // 06-D pure-helper aliases. Each name below is defined by one of the extracted
            // scripts loaded via the <script src> tags immediately above. Aliasing at the
            // top of the IIFE keeps every consumer's bare-identifier reference working
            // unchanged; the function bodies live in Configuration/scripts/*.
            var createPageLifetime = window.MediaDash.createPageLifetime;

            var esc = window.MediaDash.esc;

            var STR = window.MediaDash.STR;

            var fmt = window.MediaDash.fmt;

            var bytes = window.MediaDash.bytes;

            var LIB_RES_ORDER = window.MediaDash.LIB_RES_ORDER;

            var LIB_RES_COLORS = window.MediaDash.LIB_RES_COLORS;

            var LIB_CODEC_COLORS = window.MediaDash.LIB_CODEC_COLORS;

            var LIB_CONT_COLORS = window.MediaDash.LIB_CONT_COLORS;

            var libIconFor = window.MediaDash.libIconFor;

            var libStackBar = window.MediaDash.libStackBar;

            var libLegend = window.MediaDash.libLegend;

            var libSegmentsFromMap = window.MediaDash.libSegmentsFromMap;

            var libCardHtml = window.MediaDash.libCardHtml;

            var donutSvg = window.MediaDash.donutSvg;

            var historyFailReasonOf = window.MediaDash.historyFailReasonOf;

            var historyMatches = window.MediaDash.historyMatches;

            var binProvenanceChipClass = window.MediaDash.binProvenanceChipClass;

            var binRestoreVariant = window.MediaDash.binRestoreVariant;

            var badgeText = window.MediaDash.badgeText;

            var issueMatchesFilters = window.MediaDash.issueMatchesFilters;

            var csv = window.MediaDash.csv;

            var sanitizeDuplicateModeOnLoad = window.MediaDash.sanitizeDuplicateModeOnLoad;

            var encoderConfigToValue = window.MediaDash.encoderConfigToValue;

            var encoderValueToConfig = window.MediaDash.encoderValueToConfig;

            var createErrorsTab = window.MediaDash.createErrorsTab;

            var createRecycleTab = window.MediaDash.createRecycleTab;

            var createHistoryTab = window.MediaDash.createHistoryTab;

            var createIssuesTab = window.MediaDash.createIssuesTab;


            // fmt() + bytes() extracted to scripts/dom-helpers.js as ns.MediaDash.fmt / bytes.
            // Aliases in the top-of-IIFE alias block keep every call site unchanged.

            // Renders a past date as "X minute(s) ago", "X hour(s) ago", "X day(s) ago", or
            // "on YYYY-MM-DD" beyond 90 days. Used by the Recycle bin tab so users see time-in-bin
            // without having to compute it from a raw timestamp.
            function describeRelativeAgo(date) {
                var now = new Date();
                var secs = Math.max(0, Math.floor((now - date) / 1000));
                if (secs < 90) return secs + ' second' + (secs === 1 ? '' : 's') + ' ago';
                var mins = Math.floor(secs / 60);
                if (mins < 90) return mins + ' minute' + (mins === 1 ? '' : 's') + ' ago';
                var hrs = Math.floor(mins / 60);
                if (hrs < 36) return hrs + ' hour' + (hrs === 1 ? '' : 's') + ' ago';
                var days = Math.floor(hrs / 24);
                if (days <= 90) return days + ' day' + (days === 1 ? '' : 's') + ' ago';
                return 'on ' + date.toISOString().slice(0, 10);
            }


            // Renders the AutoPurgesAtUtc timestamp as "auto-deletes in N days" or a warning-tinted
            // "auto-deletes in N days" when we're inside 3 days. Returns HTML (span markup) so we
            // can colour the urgent case. Empty when purge is disabled (retention = 0) or unknown.
            function describeRetention(iso) {
                if (!iso) return '';
                var when = new Date(iso);
                if (isNaN(when.getTime())) return '';
                var days = Math.round((when - new Date()) / 86400000);
                var cls = '';
                if (days <= 0) cls = 'mdBinRetentionUrgent';
                else if (days <= 3) cls = 'mdBinRetentionSoon';
                var text;
                if (days <= 0) text = 'auto-deletes now';
                else if (days === 1) text = 'auto-deletes tomorrow';
                else text = 'auto-deletes in ' + days + ' days';
                return cls
                    ? '<span class="' + cls + '">' + text + '</span>'
                    : text;
            }


            var view, pollTimer;

            var lifetime = createPageLifetime();

            var api = window.MediaDash.createApi(lifetime, ApiClient);


            // Attaches a listener to a document/window target and registers its
            // removeEventListener for disposal at lifetime.stop() (page hide). Use for
            // GLOBAL listeners only — view-DOM listeners survive pagehide naturally
            // because Jellyfin reuses the same DOM instance across visits.
            function ownGlobal(target, type, handler, options) {
                target.addEventListener(type, handler, options);
                lifetime.own(function () { target.removeEventListener(type, handler, options); });
            }


            // ---------- i18n ----------
            // Resolves the current UI locale strictly from Jellyfin's own display-language setting.
            // Field report A11 (user directive): never fall back to navigator.language or the host
            // system locale — a user browsing from a German-language desktop with Jellyfin set to
            // English must get English strings. If Jellyfin's Globalize isn't ready yet or returns
            // nothing, use English rather than leaking the browser locale.
            function currentLocale() {
                try {
                    if (typeof Globalize !== 'undefined' && typeof Globalize.getCurrentLocale === 'function') {
                        var loc = Globalize.getCurrentLocale();
                        if (loc) return loc;
                    }
                } catch (e) { /* Globalize sometimes throws on first-load; ignore and fall through. */ }
                return 'en';
            }


            // Overlays translated strings on top of the English STR object. Deep merge for `types` so per-type
            // color/icon (not localised) survive; arrays and scalars overwrite wholesale.
            function mergeI18n(dict) {
                if (!dict) return;
                Object.keys(dict).forEach(function (k) {
                    if (k === 'html' || k === 'wizSteps') return;
                    var v = dict[k];
                    if (v && typeof v === 'object' && !Array.isArray(v) && STR[k] && typeof STR[k] === 'object') {
                        Object.keys(v).forEach(function (sub) {
                            var s = v[sub];
                            if (s && typeof s === 'object' && !Array.isArray(s) && STR[k][sub]) {
                                Object.keys(s).forEach(function (leaf) { STR[k][sub][leaf] = s[leaf]; });
                            } else {
                                STR[k][sub] = s;
                            }
                        });
                    } else {
                        STR[k] = v;
                    }
                });
                // Apply as many translated wizard step labels as the locale provides. Previously this
                // required an exact count match — every non-English locale has 9 keys, en.json has 16,
                // and WIZ_STEP_LABELS has 17, so the guard never passed and every non-English user got
                // the JS English fallback. Partial translations now override index-by-index.
                if (Array.isArray(dict.wizSteps)) {
                    var lim = Math.min(dict.wizSteps.length, wizardTab.WIZ_STEP_LABELS.length);
                    for (var i = 0; i < lim; i++) {
                        if (typeof dict.wizSteps[i] === 'string' && dict.wizSteps[i]) {
                            wizardTab.WIZ_STEP_LABELS[i] = dict.wizSteps[i];
                        }
                    }
                }
                if (dict.html) applyHtmlI18n(dict.html);
            }


            // Walks the page for elements with data-i18n / data-i18n-title / data-i18n-placeholder,
            // looks each key up in `html` (the extracted HTML strings) and swaps the visible text.
            // Silently skips unknown keys so a translation gap doesn't blank a label.
            function applyHtmlI18n(html) {
                view.querySelectorAll('[data-i18n]').forEach(function (el) {
                    var k = el.getAttribute('data-i18n');
                    if (html[k] != null) el.textContent = html[k];
                });
                view.querySelectorAll('[data-i18n-title]').forEach(function (el) {
                    var k = el.getAttribute('data-i18n-title');
                    if (html[k] != null) el.setAttribute('title', html[k]);
                });
            }


            // Fetches the locale JSON and merges it. Resolves either way — a missing locale falls back to English
            // on the server side (see I18nCatalog.ResolveLocale), so this promise never rejects the init flow.
            function loadI18n() {
                var loc = currentLocale();
                return ApiClient.getJSON(ApiClient.getUrl('MediaDash/I18n/' + encodeURIComponent(loc)))
                    .then(mergeI18n, function () { /* offline / 404 — keep English. */ });
            }


            function post(path) {
                return api.ajax({ type: 'POST', url: ApiClient.getUrl('MediaDash/' + path) });
            }

            // Same as post() but tells ApiClient to parse the JSON body. Use whenever the caller
            // reads properties off the resolved value. Plain post() returns the raw Response, so
            // `r.SomeField` silently comes back undefined without this.
            function postJson(path) {
                return api.ajax({ type: 'POST', url: ApiClient.getUrl('MediaDash/' + path), dataType: 'json' });
            }


            // Bulk issue status change — used by "approve all filtered", "clear all", and the
            // context menu bulk-ignore actions.
            function bulkIssueAction(ids, action) {
                if (!ids || !ids.length) return Promise.resolve(0);
                var count = 0;
                var request = Promise.resolve();
                for (var offset = 0; offset < ids.length; offset += 500) {
                    (function (batch) {
                        request = request.then(function () {
                            return api.ajax({ type: 'POST', url: ApiClient.getUrl('MediaDash/Issues/Bulk'),
                                data: JSON.stringify({ Ids: batch, Action: action }), contentType: 'application/json', dataType: 'json' });
                        }).then(function (n) { count += n || 0; });
                    })(ids.slice(offset, offset + 500));
                }
                return request.then(function () { return count; });
            }


            // Extract a user-readable message from an ApiClient error. ApiClient rejects with an
            // XHR-like object; statusText is just "Conflict" / "Not Found" — the reason lives in
            // the body (either plain text or a JSON envelope).
            function errorMessage(err) {
                if (!err) return 'unknown error';
                var body = err.responseText || err.response || '';
                if (body) {
                    try {
                        var j = JSON.parse(body);
                        if (j && typeof j === 'string') return j;
                        if (j && (j.title || j.detail || j.Message)) return j.detail || j.Message || j.title;
                    } catch (e) { /* not JSON */ }
                    // Trim ASP.NET's problem-details envelope quotes if present.
                    return String(body).replace(/^"|"$/g, '');
                }
                return err.statusText || 'unknown error';
            }


            // Coarse media-type category derived from extension. Used by the issues context menu
            // "ignore all <category> issues" option so the user can silence a whole medium without
            // needing per-file selection.
            // Mirrors the canonical server-side MediaFormats / SubtitleFormats sets so an "ignore all
            // <category>" click on the Issues tab classifies every Jellyfin-recognised extension into
            // the right bucket. If a server-side set grows, add the new codes here too.
            var MEDIA_EXT_CATS = {
                video: ['.001', '.3g2', '.3gp', '.amv', '.asf', '.asx', '.avi', '.bin', '.bivx', '.divx', '.dv', '.dvr-ms', '.f4v', '.fli', '.flv', '.ifo', '.img', '.iso', '.m2t', '.m2ts', '.m2v', '.m4v', '.mkv', '.mk3d', '.mov', '.mp4', '.mpe', '.mpeg', '.mpg', '.mts', '.mxf', '.nrg', '.nsv', '.nuv', '.ogm', '.ogv', '.pva', '.qt', '.rec', '.rm', '.rmvb', '.strm', '.svq3', '.tp', '.ts', '.ty', '.viv', '.vob', '.vp3', '.webm', '.wmv', '.wtv', '.xvid'],
                audio: ['.669', '.aa', '.aac', '.aax', '.ac3', '.act', '.adp', '.adplug', '.adx', '.afc', '.amf', '.aif', '.aifc', '.aiff', '.alac', '.amr', '.ape', '.ast', '.au', '.awb', '.cda', '.cue', '.dmf', '.dsf', '.dsm', '.dsp', '.dts', '.dvf', '.eac3', '.ec3', '.far', '.flac', '.gdm', '.gsm', '.gym', '.hps', '.imf', '.it', '.m15', '.m4a', '.m4b', '.mac', '.med', '.mka', '.mmf', '.mod', '.mogg', '.mp2', '.mp3', '.mpa', '.mpc', '.mpp', '.mp+', '.msv', '.nmf', '.nsf', '.oga', '.ogg', '.okt', '.opus', '.pls', '.ra', '.rf64', '.s3m', '.sfx', '.shn', '.sid', '.stm', '.ult', '.uni', '.vox', '.wav', '.wma', '.wv', '.xm', '.xsp', '.ymf'],
                image: ['.jpg', '.jpeg', '.png', '.webp', '.heic', '.heif', '.gif', '.bmp', '.tif', '.tiff', '.raw', '.nef', '.cr2', '.arw', '.dng'],
                subtitle: ['.srt', '.ass', '.ssa', '.vtt', '.sub', '.idx', '.sup', '.smi', '.sami', '.mks'],
                book: ['.epub', '.mobi', '.azw', '.azw3', '.pdf', '.djvu', '.cbz', '.cbr', '.cb7']
            };

            function fileExtOf(path) {
                var m = String(path || '').match(/\.[^.\\/]+$/);
                return m ? m[0].toLowerCase() : '';
            }

            function mediaCategoryOf(path) {
                var ext = fileExtOf(path);
                for (var k in MEDIA_EXT_CATS) {
                    if (MEDIA_EXT_CATS[k].indexOf(ext) >= 0) return k;
                }
                return 'other';
            }


            // Attempt a restore. Non-destructive by default: if a file already exists at the
            // original path, the backend lands the restore alongside it as <name>-restored<ext>
            // and returns Suffixed=true. UI shows either "Restored." or "Restored as <basename>".
            function tryRestore(id, btn, msgEl) {
                if (btn) btn.disabled = true;
                if (msgEl) { msgEl.style.color = ''; msgEl.textContent = STR.restoring; }
                postJson('History/' + id + '/Restore').then(function (r) {
                    if (msgEl) msgEl.textContent = restoreMessage(r);
                    // btn stays disabled — restored history rows don't restore twice
                }, function (err) {
                    if (msgEl) msgEl.textContent = fmt(STR.restoreFailed, errorMessage(err));
                    if (btn) btn.disabled = false;
                });
            }


            function restoreMessage(r) {
                if (r && r.Suffixed && r.RestoredTo) {
                    var base = String(r.RestoredTo).split(/[\\/]/).pop();
                    return fmt(STR.restoredAs, base);
                }
                return STR.restored;
            }


            // ---------- Issues ----------

            // Issues + Errors tabs live in createIssuesTab / createErrorsTab factories
            // (defined above near the History/Recycle factories). Top-level state
            // (lastIssueList, issuePage, showingDismissedIssues, etc.) moved into the
            // factory closure; call sites go through issuesTab.* / errorsTab.*.
            // openInFileBrowser stays at top level because the Files tab uses it too;
            // closeIssueContextMenu + wireIssueMenuGlobals stay because they own the
            // popover-DOM-outside-the-factory and the ownGlobal-wired dismissal listeners
            // (06-A.2), respectively.


            function openInFileBrowser(path) {
                if (!path) return;
                var lastSep = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
                filesTab.currentFilePath = lastSep > 0 ? path.substring(0, lastSep) : path;
                showTab('files');
            }


            // Closes the issue-list right-click popover. Kept at top level because
            // wireIssueMenuGlobals() and the factory's showIssueContextMenu both call
            // it, and the popover DOM (`#mdIssueCtxMenu`) lives outside any tab's
            // owned content column.
            function closeIssueContextMenu() {
                var m = view.querySelector('#mdIssueCtxMenu');
                if (m) m.style.display = 'none';
            }


            // Global dismissal listeners for the issue context menu. Wired via
            // ownGlobal so pagehide disposes them (lifetime.stop) and every pageshow
            // re-attaches. Called unconditionally from initAfterI18n before the
            // dataset.init guard (see 06-A.2 checkpoint).
            function wireIssueMenuGlobals() {
                ownGlobal(document, 'click', function () { closeIssueContextMenu(); });
                ownGlobal(document, 'keydown', function (ev) { if (ev.key === 'Escape') closeIssueContextMenu(); });
                ownGlobal(window, 'scroll', function () { closeIssueContextMenu(); }, true);
            }


            // History tab factory lives in Configuration/scripts/tabs/history-tab.js
            // (extracted 2026-09-23, follow-up 1d) and is registered on window.MediaDash.createHistoryTab.
            var historyTab = null;

            // Recycle bin tab factory lives in Configuration/scripts/tabs/recycle-tab.js
            // (extracted 2026-09-23, follow-up 1c) and is registered on window.MediaDash.createRecycleTab.
            var recycleTab = null;


            // Issues tab factory lives in Configuration/scripts/tabs/issues-tab.js
            // (extracted 2026-09-23, follow-up 1e) and is registered on window.MediaDash.createIssuesTab.
            var issuesTab = null;

            // Errors tab factory lives in Configuration/scripts/tabs/errors-tab.js
            // (extracted 2026-09-23, follow-up 1b) and is registered on window.MediaDash.createErrorsTab.
            var errorsTab = null;
            var diagnosticsTab = null;


            // ---------- Common ----------

            // Every refresh() fires Status (progress bar — cheap and required at 3s cadence). The
            // other three endpoints get throttled — they either NEVER change under normal
            // operation (PostUpgradeCleanup) or only change on user actions (RecycleBin) / scanner
            // recorded diagnostics (Errors). Full refresh (silent=false) always fetches everything;
            // silent polls fetch cold data every N ticks, or immediately when scan/fix is running.
            // Baseline before this: 4 endpoints × 3s = 80 req/min at idle. After: ~24 req/min idle,
            // full 80 during scan/fix.
            var _refreshTick = 0;

            var _postUpgradeChecked = false;
      // fetched once per session unless invalidated
            var _postUpgradeAvailable = false;
            // UI-14: count of consecutive /Status failures. Resets on first success. Drives the
            // backoff schedule and the "retrying…" state message.
            var _statusFailStreak = 0;
            var _backgroundStatus = null;
            var _statusRefreshPending = false;

            function refresh(silent) {
                if (!silent) Dashboard.showLoadingMsg();
                _refreshTick++;
                var running = overviewTab.lastStatus && (overviewTab.lastStatus.IsScanning || overviewTab.lastStatus.IsFixing);
                // Keep DB and recycle-folder reads on the 15-second cadence during scans too.
                var refreshCold = !silent || (_refreshTick % 5 === 0);
                // Fast progress poll during active scan/fix. The full /Status aggregates ~10 DB
                // queries + drive scan + SMART + system stats; polling that every 3s during a scan
                // competes with scanner writes and lets the Overview progress bar go stale or
                // disappear on loaded servers. Switch to /Status/Progress (no DB, no drives) on
                // every active tick. Refresh full status in the background so slow counts/drives
                // cannot stall live progress and resource readings.
                var useProgressEndpoint = silent && running && !!overviewTab.lastStatus;
                var statusPath = useProgressEndpoint ? 'Status/Progress' : 'Status';

                if (useProgressEndpoint && refreshCold && !_statusRefreshPending) {
                    _statusRefreshPending = true;
                    api('Status').then(function (s) {
                        if (overviewTab.lastStatus && (overviewTab.lastStatus.IsScanning || overviewTab.lastStatus.IsFixing)) {
                            _backgroundStatus = s;
                        }
                        _statusRefreshPending = false;
                    }, function () { _statusRefreshPending = false; });
                }

                api(statusPath).then(function (s) {
                    if (useProgressEndpoint) {
                        // Merge the small payload onto the last full status so the renderer still sees
                        // every field (OpenIssueTotal, drives, counts, ...). Only the live-progress
                        // fields change tick-to-tick.
                        s = Object.assign({}, overviewTab.lastStatus, _backgroundStatus, s);
                    }
                    _backgroundStatus = null;
                    overviewTab.renderStatus(s, !!silent);
                    if (!silent) Dashboard.hideLoadingMsg();
                    _statusFailStreak = 0;
                }, function () {
                    // UI-14: a single failed Status request used to leave the poll chain dead —
                    // schedulePoll was only called from renderStatus's success tail, so a transient
                    // network blip froze progress and state until another action triggered refresh.
                    // Backoff retries: 3s → 6s → 12s (capped), surface a stale indicator so users
                    // know the UI isn't up to date, give up after the cap continues.
                    _statusFailStreak = Math.min(_statusFailStreak + 1, 4);
                    if (!silent) Dashboard.hideLoadingMsg();
                    var stateEl = view.querySelector('#mdScanState');
                    if (stateEl && _statusFailStreak >= 2) {
                        stateEl.textContent = 'Status check failed — retrying…';
                    }
                    if (lifetime.accepts(lifetime.current())) {
                        var backoff = Math.min(3000 * Math.pow(2, _statusFailStreak - 1), 15000);
                        lifetime.clearTimer(pollTimer);
                        pollTimer = lifetime.timeout(function () { refresh(true); }, backoff);
                    }
                });
                // Piggyback the error count on every refresh so the tab badge stays live without opening the tab.
                // When the user is currently on the Errors tab, delegate to loadErrors so the badge + list
                // update from the same fetch — otherwise a race with a still-in-flight refresh() fetch could
                // set the badge to a stale count (e.g. 691) after the user just cleared errors and the list
                // reads "No errors recorded".
                var activeTab = view.querySelector('.mdTab.mdTabActive');
                if (activeTab && activeTab.getAttribute('data-tab') === 'errors' && errorsTab) {
                    errorsTab.activate();
                } else if (refreshCold) {
                    // Errors/Count returns the persisted row count — the same number loadErrors
                    // sets the badge to. Summing (e.Count || 1) over /Errors here counted dedup
                    // occurrences, which pushed the badge to "999+" for any single hot error
                    // (SMART noise, ffmpeg-timeout spam) while the Errors tab only listed one row.
                    api('Errors/Count').then(function (r) {
                        updateErrorBadge((r && r.Total) || 0);
                    }, function () { });
                }
                if (activeTab && activeTab.getAttribute('data-tab') === 'diagnostics' && diagnosticsTab && refreshCold) {
                    diagnosticsTab.activate();
                }
                // Recycle-bin size banner. Threshold is user-configurable (Settings → Recycle bin);
                // 0 disables the banner. The point is to nudge users before the bin silently eats a
                // chunk of their library drive. Auto-purge still runs after the retention window,
                // but a visible banner is a lot louder than a settings hint.
                if (refreshCold) {
                    api('RecycleBin').then(function (b) {
                        var banner = view.querySelector('#mdBinFullNote');
                        // Read the threshold from the settings slider (kept in sync with config on load /
                        // save). Fall back to 10 GB when the slider hasn't rendered yet — same default the
                        // constructor sets.
                        var thresholdInput = view.querySelector('#mdBinWarnThreshold');
                        var thresholdGb = thresholdInput ? parseInt(thresholdInput.value, 10) : 10;
                        if (isNaN(thresholdGb)) thresholdGb = 10;
                        if (thresholdGb <= 0) { banner.style.display = 'none'; return; }
                        var over = b && b.SizeBytes > thresholdGb * 1024 * 1024 * 1024;
                        var show = over && !bannerDismissed('mdBinFullNote');
                        banner.style.display = show ? '' : 'none';
                        if (show) {
                            var s = b.FileCount === 1 ? '' : 's';
                            view.querySelector('#mdBinFullTitle').textContent = fmt(STR.binFullTitle, bytes(b.SizeBytes), b.FileCount, s);
                            view.querySelector('#mdBinFullText').textContent = STR.binFullBody;
                        }
                    }, function () { });
                }
                // Post-Jellyfin-12 one-shot cleanup availability. Server tells us whether to show the banner
                // (host is on Jellyfin 12+ AND the user has not yet run or dismissed the offer). Once run or
                // dismissed the flag persists in plugin config, so the banner never comes back. Cache the
                // answer for the session — polling at 3s intervals was the single biggest source of idle
                // request chatter (see 2026-08-23 console-log audit). Invalidated by dismiss/run actions
                // via invalidatePostUpgradeCache().
                if (!_postUpgradeChecked || !silent) {
                    api('PostUpgradeCleanup/Status').then(function (r) {
                        _postUpgradeChecked = true;
                        _postUpgradeAvailable = !!(r && r.Available);
                        view.querySelector('#mdPostUpgradeNote').style.display = _postUpgradeAvailable ? '' : 'none';
                    }, function () { });
                } else {
                    view.querySelector('#mdPostUpgradeNote').style.display = _postUpgradeAvailable ? '' : 'none';
                }
                if (!silent) {
                    api.configuration(PLUGIN_ID).then(function (c) {
                        configCache = c;
                        if (diagnosticsTab) diagnosticsTab.setEnabled(c.DiagnosticsEnabled);
                        view.querySelector('#mdDryRunNote').style.display = c.DryRun ? '' : 'none';
                        view.querySelector('#mdDryRunText').textContent = STR.dryRunOn;
                        view.querySelector('#mdFirstRun').style.display = c.FirstRunDone ? 'none' : '';
                        view.classList.toggle('mdWizardActive', !c.FirstRunDone);
                    });
                }
            }

            // Shared config cache for cross-tab reads (Issues tab reads disposal + dry-run so each row's
            // Approve description matches what will actually happen under the current settings — UI-02).
            // Refreshed on every non-silent refresh(); null on first paint, in which case readers fall
            // back to the generic action copy.
            var configCache = null;
            // Fix-type → disposal-field map, mirrored from settings-tab.js FIX_TYPES. Kept inline so the
            // Issues tab doesn't need to reach into the settings factory before Settings has been opened.
            var ISSUE_DISPOSAL_KEY = {
                Duplicate: 'DuplicateDisposal',
                Playability: 'PlayabilityDisposal',
                Quality: 'TranscodeDisposal',
                SubtitleLanguage: 'SubtitleDisposal',
                AudioLanguage: 'AudioDisposal',
                MalwareRisk: 'SuspiciousFileDisposal',
                CorruptNfo: 'NfoDisposal',
                HeavyTranscode: 'HeavyTranscodeDisposal',
                FailedTranscode: 'FailedTranscodeDisposal',
                EmbeddedCoverArt: 'EmbeddedCoverDisposal',
                OrphanedDebris: 'OrphanCleanupDisposal',
                FormatCompatibility: 'FormatCompatibilityDisposal'
            };
            function getIssueDisposal(issueType) {
                if (!configCache) return null;
                var key = ISSUE_DISPOSAL_KEY[issueType];
                return key ? (configCache[key] || null) : null;
            }
            function getDryRun() { return !!(configCache && configCache.DryRun); }

            // `scope`: falsy = full scan, true = grouping-only (Issues → Ungrouped rescan), or an
            // array of issue type names from the Scan now picker (right-click / long-press).
            function requestScan(scope) {
                var startScan = function () {
                    overviewTab.busy.scan = true;
                    view.querySelector('#mdScanState').textContent = STR.scanStarting;
                    overviewTab.setProgress(true, 3);
                    var started = Array.isArray(scope)
                        ? api.ajax({ type: 'POST', url: ApiClient.getUrl('MediaDash/Scan/Selected'),
                            data: JSON.stringify({ Types: scope }), contentType: 'application/json' })
                        : post(scope ? 'Scan/Ungrouped' : 'Scan');
                    started.then(function () { lifetime.timeout(refresh, 700); }, function (err) {
                        overviewTab.busy.scan = false;
                        overviewTab.setProgress(false);
                        view.querySelector('#mdScanState').textContent = 'Scan not started — ' + errorMessage(err);
                    });
                };
                // Keep settings consent and save failures shared by full and grouping-only scans.
                if (settingsTab.mdSettingsDirty) {
                    view.querySelector('#mdScanState').textContent = 'Saving your settings before scanning…';
                    settingsTab.saveSettingsAsync().then(startScan, function (err) {
                        var reason = (err && err.message) || 'unknown';
                        var msg;
                        if (reason === 'cancelled') {
                            msg = 'Scan not started — you cancelled the save confirmation. Review Settings and click Scan again.';
                        } else if (reason === 'low-space-warn') {
                            msg = 'Scan not started — the recycle bin location is below the 5 GB floor. Resolve the warning or Save anyway, then Scan.';
                        } else if (reason === 'not-loaded') {
                            msg = 'Scan not started — Settings are still loading. Try again in a moment.';
                        } else {
                            msg = 'Scan not started — could not save your settings. Try Save from the Settings tab for the exact reason.';
                        }
                        view.querySelector('#mdScanState').textContent = msg;
                        overviewTab.setProgress(false);
                    });
                    return;
                }
                startScan();
            }

            // ---------- Scan picker (right-click / long-press on "Scan now") ----------
            // Lets the user tick which scans to run instead of the full sweep. Types switched Off
            // in Settings are listed greyed out: the server skips Off scanners regardless, so
            // offering them would start a run that does nothing. The last selection is remembered
            // for this page visit; the first open starts with nothing ticked so picking one scan
            // is a single click.
            var scanPickSelection = [];

            function scanTypeIsOff(entry, config) {
                // The Settings form reflects unsaved edits too, and requestScan saves those before
                // the scan starts — so prefer it over the last-saved config when it has a value.
                var el = view.querySelector('#mdMode' + entry.key);
                var mode = (el && el.value) ? el.value : (config ? config[entry.mode] : null);
                return mode === 'Off' || mode === 0;
            }

            function openScanPicker() {
                if (overviewTab.busy.scan || (overviewTab.lastStatus && overviewTab.lastStatus.IsScanning)) return;
                api.configuration(PLUGIN_ID).then(function (c) { renderScanPicker(c); }, function () { renderScanPicker(null); });
            }

            function renderScanPicker(config) {
                var entries = (settingsTab.FIX_TYPES || []).map(function (t) {
                    var meta = STR.types && STR.types[t.key];
                    return {
                        key: t.key,
                        title: (meta && meta.title) || t.key,
                        icon: (meta && meta.icon) || 'search',
                        color: (meta && meta.color) || 'var(--md-meta)',
                        off: scanTypeIsOff(t, config)
                    };
                });
                entries.sort(function (a, b) {
                    if (a.off !== b.off) return a.off ? 1 : -1;
                    return a.title.localeCompare(b.title);
                });
                var listEl = view.querySelector('#mdScanPickList');
                listEl.innerHTML = entries.map(function (e) {
                    var checked = !e.off && scanPickSelection.indexOf(e.key) !== -1 ? ' checked' : '';
                    return '<label class="mdOrderRow mdScanPickRow' + (e.off ? ' mdScanPickOff' : '') + '"'
                        + (e.off ? ' title="Turned off in Settings → What to fix. Turn it on there to scan for it."' : '') + '>'
                        + '<input type="checkbox" class="mdScanPickBox" value="' + esc(e.key) + '"' + checked + (e.off ? ' disabled' : '') + ' />'
                        + '<span class="material-icons mdOrderIcon" style="color:' + esc(e.color) + ';">' + esc(e.icon) + '</span>'
                        + '<span class="mdOrderLabel">' + esc(e.title) + '</span>'
                        + (e.off ? '<span class="mdScanPickNote">Off in Settings</span>' : '')
                        + '</label>';
                }).join('');
                updateScanPickRun();
                view.querySelector('#mdScanPickDialog').style.display = 'flex';
                var first = listEl.querySelector('.mdScanPickBox:not(:disabled)');
                (first || view.querySelector('#mdScanPickCancel')).focus();
            }

            function selectedScanTypes() {
                return Array.prototype.map.call(
                    view.querySelectorAll('#mdScanPickList .mdScanPickBox:checked:not(:disabled)'),
                    function (box) { return box.value; });
            }

            function updateScanPickRun() {
                var n = selectedScanTypes().length;
                view.querySelector('#mdScanPickRun').disabled = n === 0;
                view.querySelector('#mdScanPickRunLabel').textContent = n > 1 ? 'Run ' + n + ' scans' : 'Run scan';
            }

            function closeScanPicker() {
                view.querySelector('#mdScanPickDialog').style.display = '';
            }

            function runPickedScans() {
                var types = selectedScanTypes();
                if (!types.length) return;
                scanPickSelection = types;
                closeScanPicker();
                if (overviewTab.busy.scan || (overviewTab.lastStatus && overviewTab.lastStatus.IsScanning)) return;
                requestScan(types);
            }

            function schedulePoll() {
                if (!lifetime.accepts(lifetime.current())) return;
                // Silent polls during scan/fix so the whole page doesn't flash every 3s.
                lifetime.clearTimer(pollTimer);
                pollTimer = lifetime.timeout(function () { refresh(true); }, 3000);
            }


            function showTab(name) {
                if (name === 'diagnostics' && (!diagnosticsTab || !diagnosticsTab.enabled)) name = 'settings';
                ['overview', 'issues', 'history', 'files', 'recycle', 'errors', 'diagnostics', 'settings'].forEach(function (n) {
                    view.querySelector('#mdTab-' + n).style.display = n === name ? '' : 'none';
                });
                view.querySelectorAll('.mdTab').forEach(function (b) {
                    b.classList.toggle('mdTabActive', b.getAttribute('data-tab') === name);
                });
                if (name === 'issues' && issuesTab) issuesTab.activate();
                if (name === 'history') historyTab.activate();
                if (name === 'settings') settingsTab.loadSettings();
                if (name === 'files') filesTab.loadFiles(filesTab.currentFilePath);
                if (name === 'errors' && errorsTab) errorsTab.activate();
                if (name === 'diagnostics' && diagnosticsTab) diagnosticsTab.activate();
                if (name === 'recycle' && recycleTab) recycleTab.activate();
                if (name === 'overview') {
                    // Kick a fresh silent poll so live values (CPU / GPU / RAM) update as soon as the tab opens.
                    refresh(true);
                    // Library-breakdown is a heavier per-item aggregate; render it on tab entry rather
                    // than on the fast poll cadence so we don't hammer /LibraryStats every 5 seconds.
                    overviewTab.renderLibraryBreakdown();
                }
            }


            function consolidateAllOtherBins(list, btn) {
                // Sequential so we can report per-source outcomes cleanly; parallelism buys nothing
                // on a filesystem move-heavy workload.
                var remaining = list.slice();
                var total = list.length;
                var totalMoved = 0, totalSkipped = 0, totalBytes = 0;
                var warnings = [];
                function updateBtnLabel() {
                    if (!btn) return;
                    var done = total - remaining.length;
                    var label = total > 1
                        ? 'Consolidating ' + (done + 1) + ' of ' + total + '…'
                        : 'Consolidating…';
                    btn.innerHTML = '<span class="material-icons mdSpin" style="font-size:1.1em;vertical-align:-3px;margin-right:.3em;">progress_activity</span><span>' + label + '</span>';
                }
                function next() {
                    var src = remaining.shift();
                    if (!src) {
                        var line = totalMoved + ' batch(es) consolidated (' + bytes(totalBytes) + ')' +
                            (totalSkipped ? ', ' + totalSkipped + ' skipped' : '') +
                            (warnings.length ? '. Warnings: ' + warnings.join('; ') : '.');
                        Dashboard.alert({ title: 'Consolidate', message: line });
                        if (recycleTab) recycleTab.activate();
                        refresh(true);
                        return;
                    }

                    updateBtnLabel();
                    api.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('MediaDash/RecycleBin/Consolidate'),
                        dataType: 'json',
                        data: JSON.stringify({ SourceRoot: src.RootPath }),
                        contentType: 'application/json'
                    }).then(function (r) {
                        totalMoved += r.BatchesMoved || 0;
                        totalSkipped += r.BatchesSkipped || 0;
                        totalBytes += r.BytesMoved || 0;
                        if (r.Warning) warnings.push(r.Warning);
                        next();
                    }, function (err) {
                        warnings.push('Failed for ' + src.RootPath + ': ' + errorMessage(err));
                        next();
                    });
                }

                next();
            }


            // ---------- Dismissible banners ----------
            // Session-scoped: dismissal survives page reloads within the same tab, gets cleared when the tab
            // closes. Deliberate over localStorage because these are self-clearing conditions (bin gets emptied,
            // volume gets moved) - if the user hasn't fixed it by next session, seeing the reminder again is fine.
            function bannerDismissed(id) {
                try {
                    // Persistent flag wins over session — a banner marked data-persistent stays hidden
                    // even after the browser tab restarts, honouring the user's "don't show again" intent.
                    if (localStorage.getItem('mdBannerDismissed:' + id) === '1') return true;
                    return sessionStorage.getItem('mdBannerDismissed:' + id) === '1';
                } catch (e) { return false; }
            }

            function dismissBanner(id, persistent) {
                try {
                    var store = persistent ? localStorage : sessionStorage;
                    store.setItem('mdBannerDismissed:' + id, '1');
                } catch (e) { }
                var el = view.querySelector('#' + id);
                if (el) el.style.display = 'none';
            }


            // Version tuple compare: "0.9.10" > "0.9.9" (numeric), tolerant of a leading "v" and
            // trailing ".0" (Plugin.Version is 4-part, GitHub tag is usually 3-part).
            function parseVersion(v) {
                if (!v) return null;
                var s = String(v).replace(/^v/i, '').trim();
                var parts = s.split(/[.\-+]/).map(function (p) { return parseInt(p, 10); });
                if (parts.some(isNaN)) return null;
                while (parts.length < 4) parts.push(0);
                return parts;
            }

            function versionGreater(a, b) {
                for (var i = 0; i < Math.max(a.length, b.length); i++) {
                    var ai = a[i] || 0, bi = b[i] || 0;
                    if (ai !== bi) return ai > bi;
                }
                return false;
            }


            function checkForUpdate() {
                var banner = view.querySelector('#mdUpdateAvailable');
                if (!banner) return;
                loadEnv().then(function (env) {
                    if (!env || !env.PluginVersion) return;
                    var tag = view.querySelector('#mdVersionTag');
                    if (tag) tag.textContent = 'v' + env.PluginVersion.replace(/\.0$/, '');
                    // Skip the update check on dev builds (assembly version 0.0.0.0). Otherwise the banner
                    // fires on every load and reads "You're on 0.0.0.0, install X.Y.Z" — noise that
                    // damages first-impression trust.
                    if (env.PluginVersion === '0.0.0.0' || env.PluginVersion.startsWith('0.0.0')) return;
                    var current = parseVersion(env.PluginVersion);
                    if (!current) return;
                    // GitHub REST releases/latest — CORS-enabled, unauth 60/hr per IP, plenty for once-per-load.
                    fetch('https://api.github.com/repos/crackruckles/MediaDash/releases/latest', { headers: { 'Accept': 'application/vnd.github+json' } })
                        .then(function (r) { return r.ok ? r.json() : null; }, function () { return null; })
                        .then(function (rel) {
                            if (!rel || !rel.tag_name) return;
                            var latest = parseVersion(rel.tag_name);
                            if (!latest || !versionGreater(latest, current)) return;
                            var latestStr = rel.tag_name.replace(/^v/i, '');
                            var dismissed = '';
                            try { dismissed = localStorage.getItem('mdUpdateDismissed') || ''; } catch (e) {}
                            if (dismissed === latestStr) return;
                            banner.setAttribute('data-version', latestStr);
                            view.querySelector('#mdUpdateAvailableText').textContent =
                                'You’re on ' + env.PluginVersion + '. Version ' + latestStr + ' is out — install it from Dashboard → Catalog, or from the release page.';
                            view.querySelector('#mdUpdateAvailableLink').setAttribute('href', rel.html_url || 'https://github.com/crackruckles/MediaDash/releases');
                            banner.style.display = '';
                        });
                });
            }


            // ---------- Env / diagnostics ----------
            // Cache the env snapshot for the wizard + Settings hints so we don't hit /Environment on every
            // load. Refreshed on demand by copyDiagnostics().
            var envCache = null;


            function loadEnv(force) {
                if (envCache && !force) return Promise.resolve(envCache);
                return api('Environment').then(function (env) { envCache = env; return env; },
                    function () { return null; });
            }


            function applySubtitleProviderHint(el, env) {
                if (!el) return;
                if (!env) {
                    el.style.display = 'none';
                    return;
                }
                var providers = env.SubtitleProviders || [];
                if (providers.length > 0) {
                    el.style.display = '';
                    el.style.color = '#22c55e';
                    el.innerHTML = '<span class="material-icons" style="font-size:1em;vertical-align:-2px;">check_circle</span> Subtitle provider ready: ' + esc(providers.join(', ')) + '.';
                } else {
                    el.style.display = '';
                    el.style.color = '#f59e0b';
                    el.innerHTML = '<span class="material-icons" style="font-size:1em;vertical-align:-2px;">warning</span> No subtitle provider is installed. Install one from <b>Dashboard → Plugins → Catalog</b> (e.g. Open Subtitles) and configure its API key on that plugin’s page. Missing-subs fixes will fail with "no providers" until you do.';
                }
            }


            var ISSUE_URL = 'https://github.com/crackruckles/MediaDash/issues/new';


            function copyDiagnostics() {
                var msg = view.querySelector('#mdErrorsMsg');
                msg.textContent = 'Assembling diagnostics…';
                // Open the issue tracker synchronously from the click event so popup blockers stay off.
                // Doing the window.open inside the Promise.then below would be a non-gesture async open
                // and Chrome/Firefox would block it. On failure, issueWindow is null and we tell the user.
                var issueWindow = window.open(ISSUE_URL, '_blank', 'noopener');
                Promise.all([loadEnv(true), api('Errors')]).then(function (r) {
                    var env = r[0] || {};
                    var errs = r[1] || [];
                    var lines = [];
                    lines.push('**MediaDash diagnostics**');
                    lines.push('- Plugin: ' + (env.PluginVersion || 'unknown'));
                    lines.push('- Jellyfin: ' + (env.JellyfinVersion || 'unknown'));
                    lines.push('- OS: ' + (env.Os || 'unknown'));
                    lines.push('- Framework: ' + (env.Framework || 'unknown'));
                    lines.push('- Subtitle providers: ' + ((env.SubtitleProviders || []).join(', ') || 'none installed'));
                    lines.push('- Captured at: ' + new Date().toISOString());
                    lines.push('');
                    if (errs.length === 0) {
                        lines.push('_No errors recorded since server start._');
                    } else {
                        lines.push('```');
                        errs.forEach(function (e) {
                            var count = (e.Count || 1) > 1 ? ' (×' + e.Count + ')' : '';
                            lines.push('[' + e.AtUtc + '] ' + (e.Source || 'unknown') + count + ': ' + (e.Message || ''));
                        });
                        lines.push('```');
                    }
                    var blob = lines.join('\n');
                    var done = function () {
                        if (issueWindow) {
                            msg.textContent = 'Diagnostics copied — paste into the GitHub issue that just opened.';
                        } else {
                            msg.innerHTML = 'Diagnostics copied. Your browser blocked the popup, so <a href="' + ISSUE_URL + '" target="_blank" rel="noopener">open the issue tracker here</a> and paste the diagnostics into the issue body.';
                        }
                        lifetime.timeout(function () { if (msg.textContent.indexOf('Diagnostics copied') === 0) msg.textContent = ''; }, 8000);
                    };
                    if (navigator.clipboard && navigator.clipboard.writeText) {
                        navigator.clipboard.writeText(blob).then(done, function () {
                            if (execCommandCopy(blob)) { done(); } else { promptFallback(blob, msg); }
                        });
                    } else if (execCommandCopy(blob)) {
                        done();
                    } else {
                        promptFallback(blob, msg);
                    }
                });
            }


            // Legacy copy path used when navigator.clipboard is unavailable or blocked (HTTP origin,
            // permissions policy, etc.). execCommand('copy') is deprecated but still ubiquitous and
            // works on any origin as long as the call sits inside a user gesture — which it does here
            // (the click handler that triggered the report). Returns true on success.
            function execCommandCopy(text) {
                var ta = document.createElement('textarea');
                ta.value = text;
                ta.setAttribute('readonly', '');
                ta.style.cssText = 'position:fixed;top:0;left:0;width:1px;height:1px;opacity:0;';
                document.body.appendChild(ta);
                ta.select();
                var ok = false;
                try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
                document.body.removeChild(ta);
                return ok;
            }


            function promptFallback(text, msg) {
                // Last-resort fallback when both clipboard paths refuse. Dump into a prompt() so the
                // user can Ctrl+A / Ctrl+C by hand.
                msg.textContent = 'Clipboard blocked — the text is in the prompt below, copy it manually.';
                window.prompt('Copy this and paste into your GitHub issue:', text);
            }


            // ---------- Errors ----------
            // Map raw diagnostic source codes to user-facing titles + optional action hints.
            // Order matters: first matching prefix wins.
            var ERROR_META = [
                { prefix: 'ScanTask.UnreachableRoot', title: 'Scan skipped a library folder', icon: 'cloud_off', hint: 'A library folder did not respond within 5 s at scan start — usually a hard-mounted NFS / SMB share whose server is offline. MediaDash skipped everything under it so the scan could finish (otherwise Jellyfin can lock up). Fix: bring the share back online, or remount with soft,timeo=30,retrans=3 so a stalled server can\'t block calls.' },
                { prefix: 'FixTask.PermissionDenied', title: 'Fix run — permission denied', icon: 'block', hint: 'Jellyfin cannot write to a library folder or the recycle bin. Check the folder owner + ACLs match the user running Jellyfin.' },
                { prefix: 'FixTask.FileMissing', title: 'Fix skipped — file missing', icon: 'find_in_page', hint: 'The file (or its folder) disappeared between the scan and the fix — usually a Sonarr / Radarr rename, a manual move, or a sync tool. The issue has been cleared; the next scan will re-detect if still applicable.' },
                { prefix: 'FixTask.FileLocked', title: 'Fix run — file locked', icon: 'lock', hint: 'The file was still open after ~7.5s of retries. Long-lived holders: an active playback, Jellyfin trickplay / chapter-thumb / cover-art extraction, a sibling MediaDash scan, or an external tool (Sonarr, Radarr, sync app, antivirus).' },
                { prefix: 'FixTask.DiskFull', title: 'Fix run — drive full', icon: 'sd_card_alert', hint: 'The target or recycle-bin drive ran out of space mid-fix. Free some space and MediaDash will retry on the next run.' },
                { prefix: 'FixTask.IOError', title: 'Fix run — I/O error', icon: 'error_outline', hint: 'An I/O error MediaDash can\'t classify — usually a network-path glitch, an ACL edge case, or an antivirus briefly locking the file. The next scheduled fix run will retry.' },
                { prefix: 'FixTask.NoRunnable', title: 'Fix run — nothing to run', icon: 'info', hint: 'Approved issues were blocked by the current settings or library scope. The message explains what to check. The issues stay queued. Not a failure.' },
                { prefix: 'FixTask.SomeSkipped', title: 'Fix run — some issues skipped', icon: 'info', hint: 'The listed count of approved issues did not run because their type is set to \'Off\' in Settings → What to fix. The rest of the queue ran normally. Not a failure.' },
                { prefix: 'FixTask', title: 'Fix run failed', icon: 'build', hint: 'The fix pipeline threw an unexpected error. Retry from the Errors tab; if it happens again, use "Report an issue".' },
                { prefix: 'Ffprobe.Timeout', title: 'ffprobe timed out', icon: 'hourglass_empty', hint: 'A file took over 2 minutes to probe and was skipped. Usually a huge file on slow storage or a container ffprobe cannot parse.' },
                { prefix: 'Ffprobe.RunFailed', title: 'ffprobe could not run', icon: 'terminal', hint: 'The Jellyfin server has no working ffprobe. Every scan that inspects files is blocked. Check Dashboard > Playback > Transcoding.' },
                { prefix: 'Ffmpeg.Timeout', title: 'ffmpeg timed out', icon: 'hourglass_empty', hint: 'ffmpeg ran past its time budget and was killed. Blu-ray remuxes on slow disks / NAS storage are the usual cause — the track fixer already retries once with a 5-hour cap before this shows up. If the same file keeps hitting the wall, the container likely has a slow index (mkv with huge PGS subs) or the source disk is failing.' },
                { prefix: 'Ffmpeg.Error', title: 'ffmpeg failed', icon: 'error_outline', hint: 'ffmpeg exited with an error. The original file was left untouched. Common causes: bad codec parameters, corrupt input container, out-of-space on the working drive, or a mismatched hardware encoder (see the error tail for the exact reason).' },
                { prefix: 'Ffmpeg.RunFailed', title: 'ffmpeg could not run', icon: 'terminal', hint: 'The Jellyfin server has no working ffmpeg. Every re-encode is blocked. Check Dashboard > Playback > Transcoding.' },
                { prefix: 'Transcode.RenameFailed', title: 'Re-encode renamed step skipped', icon: 'drive_file_rename_outline', hint: 'Re-encode itself succeeded — only the "rename to canonical name" step failed. The file is safe under its old name.' },
                { prefix: 'FileBrowser.PermissionDenied', title: 'File browser — permission denied', icon: 'lock', hint: 'The Files tab tried to touch a path the Jellyfin user does not have permission for. Same fix as fix-run permission errors.' },
                { prefix: 'RecycleBin.PermissionDenied', title: 'Recycle bin — permission denied', icon: 'lock', hint: 'The recycle-bin location is not writable by the Jellyfin user, or a specific recycled file is locked.' },
                { prefix: 'RecycleBin.IOError', title: 'Recycle bin — disk error', icon: 'sd_card_alert', hint: 'A recycled file could not be purged. Usually a lock; sometimes a full or offline drive.' },
                { prefix: 'MediaSorter.BadTarget', title: 'Media sorter — bad target folder', icon: 'folder_off', hint: 'The Movies / TV / Anime target in Settings > Libraries is missing, or sits outside every Jellyfin library. Media sorter is a no-op until fixed.' },
                { prefix: 'StaleScanner.UserApi', title: 'Stale scanner — Jellyfin API mismatch', icon: 'error_outline', hint: 'MediaDash could not enumerate users on this Jellyfin build. The Stale scanner is skipped this run; other scanners are unaffected.' },
                { prefix: 'SystemStats.Linux', title: 'System monitoring (Linux)', icon: 'monitoring', hint: 'A one-off failure reading /proc or /sys. The Overview stats card may show N/A for one poll. Not user-facing.' },
                { prefix: 'SystemStats.WindowsGPU', title: 'System monitoring (Windows GPU)', icon: 'monitoring', hint: 'A GPU perf counter briefly failed. Overview GPU% may show N/A for one poll. Not user-facing.' },
                { prefix: 'SystemStats', title: 'System monitoring', icon: 'monitoring', hint: 'A one-off system-stats sample failed. The Overview card may skip one poll. Not user-facing.' },
                { prefix: 'MediaDashDb.MalformedItemId', title: 'Database — malformed issues row', icon: 'storage', hint: 'A row in MediaDash\'s issues table has an item_id that is not a valid 32-char hex GUID. The row is skipped on every read (safe) but it will keep re-appearing here until deleted. Copy the DELETE statement from the message and run it against mediadash.db to clean up.' },
                { prefix: 'LibraryOverlap.Detected', title: 'Overlapping library folders', icon: 'folder_copy', hint: 'One Jellyfin library sits inside another (e.g. a broad "Media" library covers /media plus separate "Movies" or "TV" libraries under /media/movies /media/tv). Jellyfin indexes each overlapped file as two separate library items — one per library. This is safe to ignore but has real costs: MediaDash scans each overlapped file twice per scan cycle (ffprobe caching softens the second pass but the walk still iterates duplicates), Overview stat cards double-count the affected items, Jellyfin\'s own metadata refresh doubles up on the same file (slower refreshes, more pressure on TMDb / TVDb rate limits), and every Jellyfin client shows the file twice under different libraries. No data-loss risk: the DuplicateScanner dedupes by path so overlapped files won\'t be falsely flagged as duplicates, fixers no-op on the second pass because File.Exists returns false after the first pass touches the file, and the plugin never compounds destructively across the two entries. Fix under Dashboard → Libraries: split the parent library, narrow it to a folder that doesn\'t contain the child libraries, or remove one of the two. Five minutes there buys back scan time and stops the stats from lying forever.' }
            ];


            function errorMeta(source) {
                for (var i = 0; i < ERROR_META.length; i++) {
                    if (source && source.indexOf(ERROR_META[i].prefix) === 0) return ERROR_META[i];
                }
                return { prefix: source, title: source || 'Unknown', icon: 'error', hint: null };
            }


            function updateErrorBadge(count) { updateTabBadge('#mdErrorBadge', count); }

            function updateTabBadge(selector, count) {
                var badge = view.querySelector(selector);
                if (!badge) return;
                if (count > 0) {
                    badge.textContent = count > 999 ? '999+' : String(count);
                    badge.style.display = '';
                } else {
                    badge.style.display = 'none';
                }
            }


            var overviewTab = window.MediaDash.createOverviewTab({
                get PLUGIN_ID() { return PLUGIN_ID; },
                get esc() { return esc; },
                get STR() { return STR; },
                get fmt() { return fmt; },
                get bytes() { return bytes; },
                get libCardHtml() { return libCardHtml; },
                get DEFAULT_FIXER_ORDER() { return settingsTab.DEFAULT_FIXER_ORDER; },
                get FIXER_HARD_CONSTRAINTS() { return settingsTab.FIXER_HARD_CONSTRAINTS; },
                get view() { return view; },
                get lifetime() { return lifetime; },
                get api() { return api; },
                get ownGlobal() { return ownGlobal; },
                get historyTab() { return historyTab; },
                get issuesTab() { return issuesTab; },
                get schedulePoll() { return schedulePoll; },
                get showTab() { return showTab; },
                get bannerDismissed() { return bannerDismissed; },
                get updateTabBadge() { return updateTabBadge; }
            });
            var settingsTab = window.MediaDash.createSettingsTab({
                get lifetime() { return lifetime; },
                get ownGlobal() { return ownGlobal; },
                get PLUGIN_ID() { return PLUGIN_ID; },
                get esc() { return esc; },
                get STR() { return STR; },
                get fmt() { return fmt; },
                get csv() { return csv; },
                get sanitizeDuplicateModeOnLoad() { return sanitizeDuplicateModeOnLoad; },
                get encoderConfigToValue() { return encoderConfigToValue; },
                get encoderValueToConfig() { return encoderValueToConfig; },
                get view() { return view; },
                get api() { return api; },
                get currentLocale() { return currentLocale; },
                get lastStatus() { return overviewTab.lastStatus; },
                get recycleTab() { return recycleTab; },
                get applyAnalyticsToggle() { return wizardTab.applyAnalyticsToggle; },
                get refresh() { return refresh; },
                get loadEnv() { return loadEnv; },
                get applySubtitleProviderHint() { return applySubtitleProviderHint; }
            });
            var wizardTab = window.MediaDash.createWizard({
                get lifetime() { return lifetime; },
                get PLUGIN_ID() { return PLUGIN_ID; },
                get esc() { return esc; },
                get STR() { return STR; },
                get bytes() { return bytes; },
                get csv() { return csv; },
                get encoderValueToConfig() { return encoderValueToConfig; },
                get KEEPER_PRESETS() { return settingsTab.KEEPER_PRESETS; },
                get view() { return view; },
                get api() { return api; },
                get post() { return post; },
                get postJson() { return postJson; },
                get lastStatus() { return overviewTab.lastStatus; },
                get langPresetsHtml() { return settingsTab.langPresetsHtml; },
                get wireChipInput() { return settingsTab.wireChipInput; },
                get wireLangPresets() { return settingsTab.wireLangPresets; },
                get keeperPresetFromConfig() { return settingsTab.keeperPresetFromConfig; },
                get populateLanguageDatalist() { return settingsTab.populateLanguageDatalist; },
                get localeToIso3() { return settingsTab.localeToIso3; },
                get loadSettings() { return settingsTab.loadSettings; },
                get refresh() { return refresh; },
                get loadEnv() { return loadEnv; },
                get applySubtitleProviderHint() { return applySubtitleProviderHint; }
            });
            var filesTab = window.MediaDash.createFilesTab({
                get strings() { return STR; },
                get lifetime() { return lifetime; },
                get ownGlobal() { return ownGlobal; },
                get esc() { return esc; },
                get bytes() { return bytes; },
                get view() { return view; },
                get api() { return api; }
            });

            document.querySelector('#MediaDashConfigPage').addEventListener('pageshow', function () {
                view = this;
                var showToken = lifetime.start();
                _backgroundStatus = null;
                _statusRefreshPending = false;
                // Page-scoped requests discard stale callbacks; restore their transient UI on return.
                ['mdPostUpgradeRun', 'mdPostUpgradeDismiss', 'mdBinEmpty', 'mdMaintVirusScan', 'mdHistoryClear', 'mdResetSchedule', 'mdWizV12Run', 'mdWizCheckAccess', 'mdLibAccessTest', 'mdBinAccessTest'].forEach(function (id) {
                    var button = view.querySelector('#' + id);
                    if (button) button.disabled = false;
                });
                    // Gate v12 migration button on host version — enabled only when Jellyfin ≥ 12.
                    api('PostUpgradeCleanup/Status').then(function (s) {
                        var btn = view.querySelector('#mdMaintV12Migration');
                        var msg = view.querySelector('#mdMaintV12MigrationMsg');
                        if (!btn) return;
                        if (s && s.JellyfinMajor >= 12) {
                            if (s.Completed) {
                                btn.disabled = true;
                                msg.textContent = 'Already run once on this server. History has the details.';
                            } else {
                                btn.disabled = false;
                                msg.textContent = 'Sweeps orphaned trickplay folders left behind by the 10.x → 12.x upgrade.';
                            }
                        } else {
                            btn.disabled = true;
                            btn.setAttribute('title', 'Not applicable — this server is running Jellyfin ' + (s && s.JellyfinMajor ? s.JellyfinMajor : '<12') + '. Nothing to migrate.');
                            msg.textContent = 'Locked — this cleanup only applies once you upgrade to Jellyfin 12+.';
                        }
                    }, function () { /* Status probe failed; leave button disabled with tooltip. */ });
                var emptyBar = view.querySelector('#mdBinEmptyBar');
                if (emptyBar) emptyBar.style.display = 'none';
                var binList = view.querySelector('#mdBinList');
                if (binList) binList.style.display = '';
                view.querySelectorAll('.mdRedownloadAck, .mdRedownloadRestoreOpt').forEach(function (button) { button.disabled = false; });

                view.querySelector('#mdLogo').src = ApiClient.getUrl('MediaDash/Logo');
                var wizLogo = view.querySelector('#mdWizLogo');
                if (wizLogo) wizLogo.src = ApiClient.getUrl('MediaDash/Logo');
                // Populate the shared datalists once up-front so any language / genre input in the
                // wizard OR settings has options available on first focus, regardless of which tab
                // the user opens first.
                settingsTab.populateLanguageDatalist();
                settingsTab.populateGenreDatalist();
                // i18n is loaded up front so every downstream call sees translated strings. Failure falls back to
                // the English defaults baked into STR — the page still works if the I18n endpoint 404s.
                loadI18n().then(function () {
                    if (!lifetime.accepts(showToken)) return;
                    view.querySelector('#mdTagline').textContent = STR.tagline;
                    initAfterI18n();
                });
            });


            function initAfterI18n() {

                // Global (document/window) listeners re-wire on every pageshow — the
                // init-guard block below only wires view-DOM once because Jellyfin
                // reuses the same DOM instance across visits, but doc/window listeners
                // are disposed on pagehide via lifetime.stop() so they need reattach.
                wireIssueMenuGlobals();
                settingsTab.activateSettings();
                filesTab.activateFiles();

                // Tab factories are created ONCE per page identity (Jellyfin reuses the
                // same DOM instance across pagehide/pageshow, so the same view root is
                // valid). Later pageshow cycles fall through this guard — the factory's
                // internal filter/failReason state deliberately persists so switching
                // away and back to History preserves the user's chosen chip.
                if (!historyTab) {
                    historyTab = createHistoryTab({
                        root: view, api: api, strings: STR, lifetime: lifetime,
                        bytes: bytes, tryRestore: tryRestore, PLUGIN_ID: PLUGIN_ID
                    });
                }
                if (!recycleTab) {
                    recycleTab = createRecycleTab({
                        root: view, api: api, strings: STR, lifetime: lifetime,
                        fmt: fmt, bytes: bytes,
                        describeRelativeAgo: describeRelativeAgo, describeRetention: describeRetention,
                        postJson: postJson, restoreMessage: restoreMessage, errorMessage: errorMessage,
                        consolidateAllOtherBins: consolidateAllOtherBins
                    });
                }
                if (!issuesTab) {
                    issuesTab = createIssuesTab({
                        root: view, api: api, strings: STR, lifetime: lifetime,
                        fmt: fmt, bytes: bytes,
                        fileExtOf: fileExtOf, mediaCategoryOf: mediaCategoryOf,
                        refresh: refresh, post: post, bulkIssueAction: bulkIssueAction,
                        showTab: showTab, errorMessage: errorMessage,
                        rescan: function (type) {
                            showTab('overview');
                            if (!overviewTab.busy.scan && !(overviewTab.lastStatus && overviewTab.lastStatus.IsScanning)) {
                                if (!view.querySelector('#mdScanNow').disabled) requestScan(type === 'Ungrouped');
                            }
                        },
                        closeIssueContextMenu: closeIssueContextMenu,
                        openInFileBrowser: openInFileBrowser,
                        updateTabBadge: updateTabBadge,
                        getIssueDisposal: getIssueDisposal,
                        getDryRun: getDryRun
                    });
                }
                if (!errorsTab) {
                    errorsTab = createErrorsTab({
                        root: view, api: api, strings: STR, lifetime: lifetime,
                        errorMeta: errorMeta, updateErrorBadge: updateErrorBadge, refresh: refresh
                    });
                }
                if (!diagnosticsTab) {
                    diagnosticsTab = window.MediaDash.createDiagnosticsTab({ root: view, api: api, strings: STR, lifetime: lifetime, showTab: showTab });
                }

                // Move the tab bar into the masthead slot so the active tab pill visually connects
                // to the content column below. The tab bar lives in its post-banner DOM position
                // in source, but is teleported here at init.
                var tabBar = view.querySelector('.mdTabBar');
                var slot = view.querySelector('#mdTabBarSlot');
                if (tabBar && slot && slot.childNodes.length === 0) slot.appendChild(tabBar);

                var filter = view.querySelector('#mdTypeFilter');
                if (!filter.dataset.init) {
                    filter.dataset.init = '1';
                    view.querySelector('#mdIssueFilter').addEventListener('input', function () {
                        issuesTab.setPage(1);
                        issuesTab.render();
                    });
                    view.querySelector('#mdIssueLangFilter').addEventListener('change', function () {
                        issuesTab.setLangFilter(this.value || '');
                    });
                    view.querySelector('#mdIssuePagePrev').addEventListener('click', function () { issuesTab.prevPage(); });
                    view.querySelector('#mdIssuePageNext').addEventListener('click', function () { issuesTab.nextPage(); });
                    view.querySelector('#mdClearAllIssues').addEventListener('click', function () {
                        var ids = issuesTab.filteredIds();
                        if (!ids.length) return;
                        if (issuesTab.showingIgnored()) {
                            if (!window.confirm(fmt(STR.unignoreAllConfirm || 'Unignore {0} issue(s)?', ids.length))) return;
                            bulkIssueAction(ids, 'Revert').then(function () { issuesTab.activate(); refresh(true); }).catch(function (err) { Dashboard.alert({ title: STR.issueActionFailed, message: errorMessage(err) }); });
                        } else {
                            if (!window.confirm(fmt(STR.clearAllConfirm || 'Ignore all {0} issue(s)?', ids.length))) return;
                            bulkIssueAction(ids, 'Dismiss').then(function () { issuesTab.activate(); refresh(true); }).catch(function (err) { Dashboard.alert({ title: STR.issueActionFailed, message: errorMessage(err) }); });
                        }
                    });
                    view.querySelector('#mdToggleIgnored').addEventListener('click', function () {
                        issuesTab.toggleShowIgnored();
                        var showing = issuesTab.showingIgnored();
                        var btn = view.querySelector('#mdToggleIgnored');
                        btn.classList.toggle('mdToggleActive', showing);
                        view.querySelector('#mdToggleIgnoredLabel').textContent = showing
                            ? (STR.showOpen || 'Show open issues')
                            : (STR.showIgnored || 'Show ignored');
                        view.querySelector('#mdToggleIgnoredIcon').textContent = showing
                            ? 'visibility'
                            : 'visibility_off';
                    });
                    issuesTab.installContextMenu();

                    // Bottom-of-Settings "Back to top" pill — jumps to the tab bar so the user can
                    // switch tabs or edit the save-button-adjacent knobs without a manual scroll.
                    // Prefers the masthead (which contains the tab bar) as the target; falls back
                    // to window if we can't find it (older layouts, DOM rearrangement).
                    var backTop = view.querySelector('#mdBackToTop');
                    if (backTop) {
                        backTop.addEventListener('click', function () {
                            var target = view.querySelector('#mdMasthead')
                                || view.querySelector('#mdTabBarSlot')
                                || view.querySelector('#MediaDashConfigPage');
                            if (target) {
                                target.scrollIntoView({ behavior: 'smooth', block: 'start' });
                            } else {
                                window.scrollTo({ top: 0, behavior: 'smooth' });
                            }
                        });
                    }

                    view.querySelectorAll('.mdTab').forEach(function (b) {
                        b.addEventListener('click', function () { showTab(b.getAttribute('data-tab')); });
                    });

                    view.querySelector('#mdScanNow').addEventListener('click', function () {
                        if (overviewTab.lastStatus && overviewTab.lastStatus.IsScanning) {
                            post('Scan/Cancel').then(function () { lifetime.timeout(refresh, 700); });
                            return;
                        }
                        requestScan(false);
                    });

                    view.querySelector('#mdFixNow').addEventListener('click', function () {
                        if (overviewTab.lastStatus && overviewTab.lastStatus.IsFixing) {
                            post('Fix/Cancel').then(function () { lifetime.timeout(refresh, 700); });
                            return;
                        }
                        overviewTab.busy.fix = true;
                        view.querySelector('#mdScanState').textContent = STR.fixStarting;
                        overviewTab.setProgress(true, 3);
                        post('Fix').then(function () { lifetime.timeout(refresh, 700); }, function (err) {
                            overviewTab.busy.fix = false;
                            overviewTab.setProgress(false);
                            view.querySelector('#mdScanState').textContent = STR.fixStartFailed + ' ' + errorMessage(err);
                            Dashboard.alert({ title: STR.fixStartFailed, message: errorMessage(err) });
                        });
                    });

                    // Right-click (desktop) / long-press (touch) on "Scan now" opens the scan picker
                    // so the user can run just the scans they tick. Same gesture model as "Run fixes
                    // now" below: 550 ms hold, cancelled by movement, trailing click swallowed.
                    var scanBtnEl = view.querySelector('#mdScanNow');
                    scanBtnEl.addEventListener('contextmenu', function (e) {
                        e.preventDefault();
                        openScanPicker();
                    });
                    (function () {
                        var timer = null;
                        var fired = false;
                        scanBtnEl.addEventListener('touchstart', function () {
                            fired = false;
                            timer = lifetime.timeout(function () {
                                fired = true;
                                openScanPicker();
                            }, 550);
                        }, { passive: true });
                        var cancel = function () { if (timer) { lifetime.clearTimer(timer); timer = null; } };
                        scanBtnEl.addEventListener('touchend', function (e) {
                            cancel();
                            if (fired) { e.preventDefault(); }
                        });
                        scanBtnEl.addEventListener('touchmove', cancel, { passive: true });
                        scanBtnEl.addEventListener('touchcancel', cancel);
                    })();
                    var scanPickDialog = view.querySelector('#mdScanPickDialog');
                    scanPickDialog.addEventListener('click', function (e) {
                        if (e.target === this) closeScanPicker();
                    });
                    scanPickDialog.addEventListener('keydown', function (e) {
                        if (e.key === 'Escape') { e.preventDefault(); closeScanPicker(); }
                    });
                    view.querySelector('#mdScanPickList').addEventListener('change', updateScanPickRun);
                    view.querySelector('#mdScanPickAll').addEventListener('click', function () {
                        view.querySelectorAll('#mdScanPickList .mdScanPickBox:not(:disabled)').forEach(function (box) { box.checked = true; });
                        updateScanPickRun();
                    });
                    view.querySelector('#mdScanPickNone').addEventListener('click', function () {
                        view.querySelectorAll('#mdScanPickList .mdScanPickBox').forEach(function (box) { box.checked = false; });
                        updateScanPickRun();
                    });
                    view.querySelector('#mdScanPickCancel').addEventListener('click', closeScanPicker);
                    view.querySelector('#mdScanPickRun').addEventListener('click', runPickedScans);

                    // Right-click (desktop) / long-press (touch) on "Run fixes now" opens the fixer
                    // reorder dialog. Field report E1 spec: reorder is a power-user knob, not
                    // primary UI, so it lives in the context menu rather than in the masthead.
                    var fixBtnEl = view.querySelector('#mdFixNow');
                    fixBtnEl.addEventListener('contextmenu', function (e) {
                        e.preventDefault();
                        overviewTab.openFixOrderDialog();
                    });
                    // Long-press: 550 ms threshold matches Android's default touch-hold. Cancel on
                    // move/up so the primary tap still runs a fix, and preventDefault on the
                    // synthetic contextmenu that some mobile browsers fire after long-press so we
                    // don't double-open the dialog.
                    (function () {
                        var timer = null;
                        var fired = false;
                        fixBtnEl.addEventListener('touchstart', function () {
                            fired = false;
                            timer = lifetime.timeout(function () {
                                fired = true;
                                overviewTab.openFixOrderDialog();
                            }, 550);
                        }, { passive: true });
                        var cancel = function () { if (timer) { lifetime.clearTimer(timer); timer = null; } };
                        fixBtnEl.addEventListener('touchend', function (e) {
                            cancel();
                            // If the long-press already opened the dialog, swallow the trailing
                            // synthetic click so it doesn't also start a fix run.
                            if (fired) { e.preventDefault(); }
                        });
                        fixBtnEl.addEventListener('touchmove', cancel, { passive: true });
                        fixBtnEl.addEventListener('touchcancel', cancel);
                    })();
                    view.querySelector('#mdFixOrderCancel').addEventListener('click', overviewTab.closeFixOrderDialog);
                    view.querySelector('#mdFixOrderDialog').addEventListener('click', function (e) {
                        // Click on the backdrop (but not the inner card) dismisses without saving.
                        if (e.target === this) overviewTab.closeFixOrderDialog();
                    });
                    view.querySelector('#mdFixOrderReset').addEventListener('click', function () {
                        // Reset clears BOTH the order and the per-fixer disable set — a single
                        // gesture returns everything on this dialog to its shipped defaults.
                        overviewTab.renderFixOrderList(settingsTab.DEFAULT_FIXER_ORDER.slice(), []);
                    });
                    view.querySelector('#mdFixOrderSave').addEventListener('click', overviewTab.saveFixOrder);

                    view.querySelector('#mdFixIgnoreActivity').addEventListener('click', function () {
                        // Hide the banner immediately so the click feels responsive; the next status poll (within
                        // 30 s, matching the server's re-check cadence) will confirm the run resumed.
                        view.querySelector('#mdFixPauseNote').style.display = 'none';
                        post('Fix/IgnoreActivity').then(function () { lifetime.timeout(refresh, 700); });
                    });

                    view.querySelector('#mdRecurringWarnShow').addEventListener('click', function () {
                        showTab('issues');
                    });

                    view.querySelector('#mdRecycleWarnFix').addEventListener('click', function () {
                        showTab('settings');
                        lifetime.timeout(function () {
                            var section = view.querySelector('#mdSet-recycle');
                            if (section) section.scrollIntoView({ block: 'start', behavior: 'smooth' });
                            var bin = view.querySelector('#mdBinPath');
                            if (bin) bin.focus();
                        }, 200);
                    });

                    view.querySelector('#mdStaleQueueClear').addEventListener('click', function () {
                        // Confirm because "clear queued items" is a queue-state change users will
                        // notice — even though Dismissed is recoverable via Revert.
                        var proceed = window.confirm(
                            'Dismiss every queued issue whose library is no longer enabled?\n\n' +
                            'These are the items the last fix run skipped. Dismissed rows still show on the Issues tab under "Dismissed" and can be brought back with Revert.'
                        );
                        if (!proceed) return;
                        view.querySelector('#mdStaleQueueWarn').style.display = 'none';
                        postJson('Issues/DismissStaleQueued').then(function (count) {
                            var n = (typeof count === 'number') ? count : 0;
                            if (window.Dashboard && Dashboard.alert) {
                                Dashboard.alert({ title: 'Stale queue cleared', message: 'Dismissed ' + n + ' queued item' + (n === 1 ? '' : 's') + '. Revert them from the Issues tab if needed.' });
                            }
                            lifetime.timeout(refresh, 500);
                        }).catch(function (err) { Dashboard.alert({ title: STR.issueActionFailed, message: errorMessage(err) }); });
                    });

                    // Delegated so future banners with class="mdDismissBanner" don't need separate wiring.
                    view.querySelectorAll('.mdDismissBanner').forEach(function (btn) {
                        btn.addEventListener('click', function () {
                            dismissBanner(btn.getAttribute('data-banner'), btn.getAttribute('data-persistent') === '1');
                        });
                    });

                    view.querySelector('#mdUpdateAvailableDismiss').addEventListener('click', function () {
                        var latest = view.querySelector('#mdUpdateAvailable').getAttribute('data-version') || '';
                        try { localStorage.setItem('mdUpdateDismissed', latest); } catch (e) {}
                        view.querySelector('#mdUpdateAvailable').style.display = 'none';
                    });

                    checkForUpdate();

                    // Delegated so links rendered inside task-card templates (created later) reach us.
                    view.addEventListener('click', function (e) {
                        var link = e.target.closest('.mdGoRecycleTab');
                        if (link) { e.preventDefault(); showTab('recycle'); }

                        var ackBtn = e.target.closest('.mdRedownloadAck');
                        if (ackBtn) {
                            e.preventDefault();
                            var id = ackBtn.getAttribute('data-history-id');
                            if (!id) return;
                            ackBtn.disabled = true;
                            post('RedownloadWarnings/' + encodeURIComponent(id) + '/Acknowledge').then(function () {
                                refresh(true);
                            }, function (err) {
                                ackBtn.disabled = false;
                                alert('Could not acknowledge: ' + errorMessage(err));
                            });
                        }

                        var restBtn = e.target.closest('.mdRedownloadRestoreOpt');
                        if (restBtn) {
                            e.preventDefault();
                            var rid = restBtn.getAttribute('data-history-id');
                            if (!rid) return;
                            restBtn.disabled = true;
                            var url = 'RedownloadWarnings/' + encodeURIComponent(rid) + '/RestoreOptimized';
                            post(url).then(function () {
                                refresh(true);
                            }, function (err) {
                                // 409 usually means "a file already exists at the target path".
                                // Offer the force=true flow so the current file goes to the bin first.
                                if (err && err.status === 409 && confirm(errorMessage(err) + '\n\nSend the current file at that path to the recycle bin and restore the optimized copy in its place?')) {
                                    post(url + '?force=true').then(function () {
                                        refresh(true);
                                    }, function (err2) {
                                        restBtn.disabled = false;
                                        alert('Restore failed: ' + errorMessage(err2));
                                    });
                                } else {
                                    restBtn.disabled = false;
                                    alert('Restore failed: ' + errorMessage(err));
                                }
                            });
                        }
                    });

                    view.querySelector('#mdBinRefresh').addEventListener('click', function () {
                        recycleTab.refreshInfo();
                        recycleTab.refreshList();
                    });

                    view.querySelector('#mdPostUpgradeRun').addEventListener('click', function () {
                        var runBtn = this;
                        var dismissBtn = view.querySelector('#mdPostUpgradeDismiss');
                        var resultEl = view.querySelector('#mdPostUpgradeResult');
                        runBtn.disabled = true;
                        dismissBtn.disabled = true;
                        resultEl.style.display = '';
                        resultEl.textContent = 'Running sweep — this can take a moment on large libraries…';
                        postJson('PostUpgradeCleanup/Run').then(function (r) {
                            var data = r; // Ajax helpers already return parsed JSON.
                            var freed = (data && data.BytesFreed) || 0;
                            var count = (data && data.OrphanedFoldersDeleted) || 0;
                            resultEl.textContent = (data && data.WasDryRun) ? 'Dry-run preview: ' + count + ' orphaned folders could be removed. No files were deleted.' : 'Cleanup done. Reclaimed ' + bytes(freed) + ' from ' + count + ' orphaned trickplay folder' + (count === 1 ? '' : 's') + '. Logged to History.';
                            // Server state just changed — invalidate the once-per-session cache so
                            // the next refresh re-reads (and the banner drops for good).
                            _postUpgradeChecked = false;
                            lifetime.timeout(function () {
                                view.querySelector('#mdPostUpgradeNote').style.display = 'none';
                                refresh(true);
                            }, 3500);
                        }, function (err) {
                            resultEl.style.color = '#ef4444';
                            resultEl.textContent = 'Cleanup failed: ' + ((err && err.statusText) || 'unknown error') + '. Errors tab has details.';
                            runBtn.disabled = false;
                            dismissBtn.disabled = false;
                        });
                    });

                    view.querySelector('#mdPostUpgradeDismiss').addEventListener('click', function () {
                        // Same permanent flag as Run — dismissal is also "handled forever".
                        post('PostUpgradeCleanup/Run?dismissOnly=true').then(function () {
                            _postUpgradeChecked = true;
                            _postUpgradeAvailable = false;
                            view.querySelector('#mdPostUpgradeNote').style.display = 'none';
                        });
                    });

                    view.querySelector('#mdGoDryRun').addEventListener('click', function () {
                        showTab('settings');
                        lifetime.timeout(function () {
                            // Expand the Safety card if the user previously collapsed it — otherwise
                            // the dry-run checkbox is display:none and scrollIntoView is a no-op, so
                            // the button appeared to do nothing. Mirrors the jump-bar handler's
                            // "never land on a hidden card" rule (settings-tab.js wireJumpControl).
                            var safetyCard = view.querySelector('#mdSet-safety');
                            if (safetyCard && safetyCard.classList.contains('mdSetCardCollapsed')) {
                                safetyCard.classList.remove('mdSetCardCollapsed');
                                try { localStorage.setItem('mdSetCollapse:mdSet-safety', '0'); } catch (e) {}
                            }
                            var dry = view.querySelector('#mdDryRun');
                            if (dry) dry.scrollIntoView({ block: 'center', behavior: 'smooth' });
                        }, 200);
                    });

                    view.querySelector('#mdGoBinReview').addEventListener('click', function () {
                        showTab('recycle');
                    });

                    // "Scan & approve all": rescan only the types whose findings need a fresh scan, wait for
                    // the scan to finish, then approve everything that came back unblocked. The server's
                    // ApproveAll still skips anything blocked, so nothing unsafe is queued.
                    view.querySelector('#mdRescanApproveAll').addEventListener('click', function () {
                        var btn = this;
                        var types = issuesTab.rescanTypes();
                        if (!types.length) return;
                        var note = view.querySelector('#mdApproveAllNote');
                        btn.disabled = true;
                        note.textContent = STR.rescanApproveScanning;
                        requestScan(types);
                        var startedAt = Date.now();
                        var sawScan = false;
                        function finish(text) {
                            btn.disabled = false;
                            note.textContent = text;
                            issuesTab.activate();
                            refresh(true);
                        }
                        function approve() {
                            note.textContent = STR.rescanApproveApproving;
                            var counts = [];
                            var chain = Promise.resolve();
                            types.forEach(function (type) {
                                chain = chain.then(function () {
                                    return postJson('Issues/ApproveAll?type=' + encodeURIComponent(type))
                                        .then(function (n) { counts.push(Number(n) || 0); }, function () { counts.push(0); });
                                });
                            });
                            chain.then(function () {
                                var total = counts.reduce(function (a, b) { return a + b; }, 0);
                                finish(fmt(STR.rescanApproveDone, total));
                            });
                        }
                        (function poll() {
                            api('Status/Progress').then(function (p) {
                                if (p && p.IsScanning) sawScan = true;
                                var settled = !(p && p.IsScanning) && (sawScan || Date.now() - startedAt > 15000);
                                if (settled) approve();
                                else lifetime.timeout(poll, 3000);
                            }, function () { lifetime.timeout(poll, 5000); });
                        })();
                    });

                    view.querySelector('#mdApproveAll').addEventListener('click', function () {
                        // Approve only the currently-filtered set (respects type + text + language),
                        // not the server-wide type totals — otherwise "Approve all 10 shown" queues
                        // hundreds of unshown rows when a text filter narrows the visible list.
                        var ids = issuesTab.filteredIds(true);
                        if (!ids.length) return;
                        bulkIssueAction(ids, 'Approve').then(function (count) {
                            if (count < ids.length) Dashboard.alert({ message: fmt(STR.approvePartial, count, ids.length) });
                            issuesTab.activate();
                            refresh(true);
                        }, function (err) {
                            Dashboard.alert({ title: STR.issueActionFailed, message: errorMessage(err) });
                            issuesTab.activate();
                        });
                    });

                    settingsTab.bindSettings();
                    wizardTab.setupFirstRun();

                    view.querySelector('#mdBinEmpty').addEventListener('click', function () {
                        var btn = this;
                        // Ask the server what every empty would delete before doing anything. Older bins
                        // (an earlier bin path, the default bin, batches from older MediaDash versions)
                        // are offered alongside the current bin so one click can clear all of them.
                        api('RecycleBin/EmptyPlan').then(function (plan) {
                            var total = (plan && plan.TotalBatches) || 0;
                            var current = (plan && plan.CurrentBinBatches) || 0;
                            if (total === 0) {
                                Dashboard.alert({ title: STR.emptyAllTitle, message: STR.binEmpty });
                                return;
                            }

                            if (total === current) {
                                if (!window.confirm(STR.confirmEmptyBin)) return;
                                runEmptyBin(btn, false);
                                return;
                            }

                            chooseEmptyScope(plan, function (everywhere) { runEmptyBin(btn, everywhere); });
                        }, function () {
                            // Plan fetch failed (older server, transient error): fall back to the current bin only.
                            if (!window.confirm(STR.confirmEmptyBin)) return;
                            runEmptyBin(btn, false);
                        });
                    });

                    // Three-way choice: every location, the current bin only, or cancel. A native confirm()
                    // can only say yes/no, so this is a small in-page dialog. onChoose(true|false) runs
                    // only when the user picks an empty; cancel, Escape and a backdrop click do nothing.
                    function chooseEmptyScope(plan, onChoose) {
                        var old = view.querySelector('#mdEmptyScope');
                        if (old) old.remove();
                        var lines = (plan.Locations || []).map(function (l) {
                            return '<li style="margin:.35em 0;"><code style="word-break:break-all;">' + esc(l.RootPath) + '</code>' +
                                (l.IsCurrent ? ' <b>(' + esc(STR.emptyCurrentTag) + ')</b>' : '') +
                                '<div style="opacity:.75;font-size:90%;">' + esc(fmt(STR.emptyLocationLine, l.BatchCount, bytes(l.SizeBytes))) + '</div></li>';
                        }).join('');
                        var overlay = document.createElement('div');
                        overlay.id = 'mdEmptyScope';
                        overlay.setAttribute('role', 'dialog');
                        overlay.setAttribute('aria-modal', 'true');
                        overlay.setAttribute('aria-labelledby', 'mdEmptyScopeTitle');
                        overlay.style.cssText = 'position:fixed;inset:0;z-index:1000;background:rgba(0,0,0,.55);display:flex;align-items:center;justify-content:center;padding:1em;';
                        overlay.innerHTML =
                            '<div style="background:var(--md-panel,#1b2230);color:var(--md-ink,#fff);border-radius:12px;max-width:640px;width:100%;max-height:85vh;overflow:auto;padding:1.4em 1.6em;box-shadow:0 10px 40px rgba(0,0,0,.5);">' +
                                '<h3 id="mdEmptyScopeTitle" style="margin:0 0 .6em;">' + esc(STR.emptyAllTitle) + '</h3>' +
                                '<p style="margin:0 0 .6em;">' + esc(STR.emptyAllIntro) + '</p>' +
                                '<ul style="margin:0 0 1.2em;padding-left:1.2em;">' + lines + '</ul>' +
                                '<div style="display:flex;gap:.6em;flex-wrap:wrap;justify-content:flex-end;">' +
                                    '<button is="emby-button" type="button" class="raised" data-choice="cancel">' + esc(STR.emptyCancel) + '</button>' +
                                    '<button is="emby-button" type="button" class="raised" data-choice="current"' + (plan.CurrentBinBatches ? '' : ' disabled') + '>' + esc(fmt(STR.emptyCurrentButton, plan.CurrentBinBatches || 0)) + '</button>' +
                                    '<button is="emby-button" type="button" class="raised" data-choice="all" style="background:#b91c1c;color:#fff;">' + esc(fmt(STR.emptyAllButton, bytes(plan.TotalBytes))) + '</button>' +
                                '</div>' +
                            '</div>';
                        function close() {
                            document.removeEventListener('keydown', onKey, true);
                            overlay.remove();
                        }
                        function onKey(e) { if (e.key === 'Escape') { e.preventDefault(); close(); } }
                        overlay.addEventListener('click', function (e) {
                            if (e.target === overlay) { close(); return; }
                            var b = e.target.closest && e.target.closest('[data-choice]');
                            if (!b || b.disabled) return;
                            var choice = b.getAttribute('data-choice');
                            close();
                            if (choice === 'all') onChoose(true);
                            else if (choice === 'current') onChoose(false);
                        });
                        document.addEventListener('keydown', onKey, true);
                        lifetime.own(close);
                        view.appendChild(overlay);
                        var cancelBtn = overlay.querySelector('[data-choice="cancel"]');
                        if (cancelBtn) cancelBtn.focus();
                    }

                    function runEmptyBin(btn, everywhere) {
                        var infoEl = view.querySelector('#mdBinInfo');
                        btn.disabled = true;
                        view.querySelector('#mdBinList').style.display = 'none';
                        // Ensure a progress bar container exists directly under the button row.
                        var bar = view.querySelector('#mdBinEmptyBar');
                        if (!bar) {
                            bar = document.createElement('div');
                            bar.id = 'mdBinEmptyBar';
                            bar.style.cssText = 'height:10px;background:var(--md-inner);border-radius:5px;overflow:hidden;margin:.6em 0;';
                            bar.innerHTML = '<div style="height:100%;width:0;background:linear-gradient(90deg,#ec4899,#f97316);transition:width .3s;"></div>';
                            infoEl.parentNode.insertBefore(bar, infoEl.nextSibling);
                        }
                        var fill = bar.firstChild;
                        bar.style.display = 'block';
                        fill.style.width = '2%';

                        // ponytail: 500ms polling on a user-initiated action; upgrade to server-push if bins routinely take minutes.
                        function poll() {
                            api('RecycleBin').then(function (b) {
                                var total = b.EmptyingTotal || 0;
                                var done = b.EmptyingDone || 0;
                                var pct = total > 0 ? Math.max(2, Math.round(done * 100 / total)) : (b.IsEmptying ? 5 : 100);
                                fill.style.width = pct + '%';
                                if (b.IsEmptying) {
                                    infoEl.textContent = 'Emptying… ' + done + ' of ' + total;
                                    lifetime.timeout(poll, 500);
                                    return;
                                }
                                fill.style.width = '100%';
                                lifetime.timeout(function () { bar.style.display = 'none'; }, 600);
                                btn.disabled = false;
                                if (b.EmptyingError) {
                                    Dashboard.alert({ title: 'Empty recycle bin', message: b.EmptyingError });
                                }
                                recycleTab.refreshInfo();
                                recycleTab.refreshList();
                            }, function () {
                                // Poll fetch failed (server restart, network blip). Give up cleanly.
                                fill.style.width = '0';
                                bar.style.display = 'none';
                                btn.disabled = false;
                                recycleTab.refreshInfo();
                                recycleTab.refreshList();
                            });
                        }

                        post('RecycleBin/Empty' + (everywhere ? '?everywhere=true' : '')).then(poll, function () {
                            btn.disabled = false;
                            bar.style.display = 'none';
                            Dashboard.alert({ title: 'Empty recycle bin', message: 'The server refused the request. Check the Errors tab for details.' });
                        });
                    }


                    view.querySelector('#mdRerunSetup').addEventListener('click', function () {
                        api.configuration(PLUGIN_ID).then(function (c) {
                            c.FirstRunDone = false;
                            ApiClient.updatePluginConfiguration(PLUGIN_ID, c).then(function (result) {
                                Dashboard.processPluginConfigurationUpdateResult(result);
                                wizardTab.reopen().then(function () { refresh(); }, function (err) { Dashboard.alert({ message: errorMessage(err) }); });
                                window.scrollTo(0, 0);
                            });
                        });
                    });

                    // Maintenance → Start virus scan. Runs just SuspiciousFileScanner in seconds.
                    view.querySelector('#mdMaintVirusScan').addEventListener('click', function () {
                        var btn = this;
                        var msg = view.querySelector('#mdMaintVirusScanMsg');
                        btn.disabled = true;
                        msg.textContent = 'Scanning every library folder for executables…';
                        postJson('Scan/Suspicious').then(function (r) {
                            var n = (r && r.Detected) || 0;
                            var ms = (r && r.ElapsedMs) || 0;
                            msg.textContent = n === 0
                                ? 'Scan complete in ' + ms + ' ms — nothing suspicious found.'
                                : 'Scan complete in ' + ms + ' ms — ' + n + ' suspicious file' + (n === 1 ? '' : 's') + ' flagged. Open the Issues tab.';
                            btn.disabled = false;
                            refresh(true);
                        }, function (err) {
                            msg.textContent = 'Virus scan failed: ' + ((err && err.statusText) || 'unknown error');
                            btn.disabled = false;
                        });
                    });

                    // Maintenance → Start v12 migration. Locked off unless server reports Jellyfin 12+.
                    view.querySelector('#mdMaintV12Migration').addEventListener('click', function () {
                        var btn = this;
                        var msg = view.querySelector('#mdMaintV12MigrationMsg');
                        if (btn.disabled) return;
                        btn.disabled = true;
                        msg.textContent = 'Sweeping orphaned trickplay folders — this can take a moment on large libraries…';
                        postJson('PostUpgradeCleanup/Run').then(function (r) {
                            var freed = (r && r.BytesFreed) || 0;
                            var count = (r && r.OrphanedFoldersDeleted) || 0;
                            msg.textContent = (r && r.WasDryRun) ? 'Dry-run preview: ' + count + ' orphaned folders could be removed. No files were deleted.' : 'Cleanup done. Reclaimed ' + bytes(freed) + ' from ' + count + ' orphaned folder' + (count === 1 ? '' : 's') + '. Logged to History.';
                            refresh(true);
                        }, function (err) {
                            msg.textContent = 'Migration failed: ' + ((err && err.statusText) || 'unknown error');
                            btn.disabled = false;
                        });
                    });



                    filesTab.bindFiles();

                    view.querySelectorAll('.mdHistFilter').forEach(function (btn) {
                        btn.addEventListener('click', function () {
                            // The factory resets its own failReason sub-filter internally
                            // whenever setFilter is called — the earlier top-level reset was
                            // just a mirror of the setter's contract.
                            historyTab.setFilter(btn.getAttribute('data-filter'));
                        });
                    });

                    var failReasonSel = view.querySelector('#mdHistoryFailReason');
                    if (failReasonSel) {
                        failReasonSel.addEventListener('change', function () {
                            historyTab.setFailReason(this.value || '');
                        });
                    }

                    view.querySelector('#mdHistoryClear').addEventListener('click', function () {
                        if (!window.confirm('Clear current history rows and recorded moves from this tab? Per-library totals and lifetime reclaim stay intact. Moves updated afterwards appear again.')) return;
                        var btn = this;
                        btn.disabled = true;
                        post('History/Clear').then(function () {
                            historyTab.activate();
                            btn.disabled = false;
                        }, function () {
                            btn.disabled = false;
                            Dashboard.alert({ title: 'Clear history log', message: 'The server refused the request. Check the Errors tab for details.' });
                        });
                    });

                    // Client-side text filter on the History + Recycle-bin tables. Matches only the
                    // media name element inside each row (`.mdHistPath` for history, `.mdBinRowName`
                    // for bin), not the whole row text — the row also carries chips, timestamps,
                    // action text etc. that would otherwise bleed noise into the results.
                    // Uses setProperty(..., 'important') because `.mdBinRow { display: grid !important }`
                    // outranks a plain `row.style.display = 'none'` — that ranking is why the recycle
                    // bin filter previously did nothing at all.
                    function wireSearchFilter(inputId, listId, nameSelector) {
                        var input = view.querySelector(inputId);
                        var list = view.querySelector(listId);
                        if (!input || !list) return;
                        function apply() {
                            var q = input.value.toLowerCase().trim();
                            var rows = list.children;
                            for (var i = 0; i < rows.length; i++) {
                                var row = rows[i];
                                var nameEl = row.querySelector(nameSelector);
                                var name = nameEl ? nameEl.textContent.toLowerCase() : row.textContent.toLowerCase();
                                if (q === '' || name.indexOf(q) !== -1) {
                                    row.style.removeProperty('display');
                                } else {
                                    row.style.setProperty('display', 'none', 'important');
                                }
                            }
                        }

                        input.addEventListener('input', apply);
                        // Re-apply whenever the list is re-populated so filter survives loadHistory /
                        // loadBinContents refreshes.
                        var observer = new MutationObserver(apply);
                        observer.observe(list, { childList: true });
                    }

                    wireSearchFilter('#mdHistorySearch', '#mdHistoryList', '.mdHistPath');
                    wireSearchFilter('#mdBinSearch', '#mdBinList', '.mdBinRowName');

                    view.querySelector('#mdErrorsRefresh').addEventListener('click', function () { errorsTab.activate(); });
                    view.querySelector('#mdDiagnosticsRefresh').addEventListener('click', function () { diagnosticsTab.activate(); });
                    view.querySelector('#mdErrorsClear').addEventListener('click', function () {
                        errorsTab.setShowFull(false);
                        post('Errors/Clear').then(function () { errorsTab.activate(); });
                    });
                    view.querySelector('#mdErrorsCopy').addEventListener('click', copyDiagnostics);
                    view.querySelector('#mdErrorsRetry').addEventListener('click', function () {
                        var msg = view.querySelector('#mdErrorsMsg');
                        msg.textContent = 'Retrying queued fixes…';
                        // Clear existing errors first — anything that fails again will re-populate the tab.
                        post('Errors/Clear').then(function () {
                            return post('Fix').then(function () {
                                lifetime.timeout(function () { errorsTab.activate(); refresh(true); }, 700);
                                msg.textContent = 'Fix run started — see Overview for progress, and this tab will fill up again if the same errors recur.';
                            });
                        }).catch(function (err) { msg.textContent = STR.fixStartFailed + ' ' + errorMessage(err); });
                    });

                    view.querySelector('#mdResetSchedule').addEventListener('click', function () {
                        var btn = this;
                        var msg = view.querySelector('#mdResetScheduleMsg');
                        btn.disabled = true;
                        var reenable = function () { btn.disabled = false; };
                        post('Schedule/Apply').then(function () {
                            msg.textContent = 'Scheduled task restored. It will next fire on the standard 15-minute interval.';
                            reenable();
                        }, function (xhr) {
                            msg.textContent = 'Could not restore the scheduled task (' + (xhr && xhr.status) + '). Check Dashboard → Scheduled Tasks for a MediaDash entry called "Apply approved fixes".';
                            reenable();
                        });
                    });

                    view.querySelector('#mdResetScan').addEventListener('click', function () {
                        if (!window.confirm(STR.confirmReset)) return;
                        var msg = view.querySelector('#mdSaveMsg');
                        post('Reset').then(function () {
                            msg.textContent = STR.resetDone;
                            refresh();
                            if (view.querySelector('#mdTab-issues').style.display !== 'none' && issuesTab) issuesTab.activate();
                        }, function (xhr) {
                            msg.textContent = (xhr && xhr.status === 409) ? STR.resetBusy : ('Reset failed (' + (xhr && xhr.status) + ')');
                        });
                    });


                }

                refresh();
                // Render the library breakdown once on init since Overview is the default tab. From
                // then on it only re-renders when the user re-opens the Overview tab (see showTab).
                overviewTab.renderLibraryBreakdown();

                overviewTab.activateOverview();
            }


            document.querySelector('#MediaDashConfigPage').addEventListener('pagehide', function () {
                lifetime.clearTimer(pollTimer);
                lifetime.stop();
            });
    };
})();
