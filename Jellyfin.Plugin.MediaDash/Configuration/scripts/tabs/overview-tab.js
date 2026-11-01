// overview behavior and state. Dependencies are supplied by the page controller.
(function () {
    'use strict';
    window.MediaDash.createOverviewTab = function (deps) {


            // ---------- Charts (inline SVG, no dependencies) ----------

            // ---------- Library-breakdown charts ----------------------------------------
            // Pure helpers extracted to scripts/library-breakdown.js (06-FOLLOWUP-2a).
            // renderLibraryBreakdown stays here — uses view.querySelector + api().
            // Aliases (LIB_RES_ORDER etc., libIconFor / libStackBar / libLegend /
            // libSegmentsFromMap / libCardHtml / donutSvg) live in the top-of-IIFE
            // alias block; call sites are unchanged.

            function renderLibraryBreakdown() {
                var host = deps.view.querySelector('#mdLibraryBreakdown');
                if (!host) return;
                deps.api('LibraryStats').then(function (stats) {
                    if (!stats || stats.length === 0) { host.innerHTML = ''; return; }
                    var head = '<div class="mdSecHead"><span class="material-icons">insights</span><span>Library breakdown</span></div>';
                    var body = '<div class="mdLibBreakdown">' + stats.map(deps.libCardHtml).join('') + '</div>';
                    host.innerHTML = '<div class="mdCard">' + head + body + '</div>';
                }, function () { host.innerHTML = ''; });
            }


            // Renders the Playability repair-ladder tally card. Fed by status.Repair (populated by
            // MediaDashDb.GetRepairSummary on the server). Card hides when no repair has ever run
            // (CountLifetime === 0) so a user who hasn't hit a broken file doesn't see an empty
            // panel. Bytes-rescued totals only cover repair rows written from schema v9 onwards;
            // pre-v9 rows carry 0 and the footer discloses this rather than silently understating.
            var REPAIR_RUNG_LABELS = {
                'remux': 'Quick remux',
                'drop': 'Dropped broken streams',
                'coerce': 'Container coerced to MKV',
                'audio-reencode': 'Audio re-encoded',
                'reencode': 'Video re-encoded',
                'other': 'Other'
            };
            function renderRepairSummary(s) {
                var host = deps.view.querySelector('#mdRepairSummary');
                if (!host) return;
                var r = s && s.Repair;
                if (!r || !r.CountLifetime) { host.innerHTML = ''; return; }
                var rungs = Array.isArray(r.Rungs30d) ? r.Rungs30d : [];
                var chipStyle = 'display:inline-flex;align-items:baseline;gap:.5em;padding:.35em .7em;margin:.15em;border-radius:8px;background:rgba(6,182,212,.12);border:1px solid rgba(6,182,212,.3);font-size:90%;';
                var chips = rungs.map(function (row) {
                    var label = REPAIR_RUNG_LABELS[row.Rung] || row.Rung;
                    var bytesPart = row.SavedBytes > 0 ? ' · ' + deps.bytes(row.SavedBytes) : '';
                    return '<span style="' + chipStyle + '">'
                        + '<span>' + deps.esc(label) + '</span>'
                        + '<span style="font-weight:600;color:#67e8f9;">' + row.Count + '</span>'
                        + '<span style="opacity:.75;">' + deps.esc(bytesPart) + '</span>'
                        + '</span>';
                }).join('');
                var bytes30d = r.SavedBytes30d > 0
                    ? '<div style="margin-top:.3em;opacity:.85;">≈ ' + deps.bytes(r.SavedBytes30d) + ' rescued from deletion</div>'
                    : '';
                var lifetimeBytes = r.SavedBytesLifetime > 0 ? ' · ' + deps.bytes(r.SavedBytesLifetime) + ' rescued' : '';
                var lifetime = '<div style="margin-top:.8em;opacity:.7;font-size:90%;">All time: ' + r.CountLifetime + ' repaired' + lifetimeBytes + '</div>';
                var footer = '<div style="margin-top:.4em;opacity:.55;font-size:80%;">Byte totals count from v1.0.8.0 onwards; earlier repair rows are counted but not sized.</div>';
                var body = '<div style="padding:.5em 0 .5em .2em;">'
                    + '<div style="display:flex;align-items:baseline;gap:.6em;">'
                    + '<span style="font:600 2.2em \'IBM Plex Mono\', monospace;color:#67e8f9;">' + r.Count30d + '</span>'
                    + '<span>broken files repaired in the last 30 days</span>'
                    + '</div>'
                    + bytes30d
                    + (chips ? '<div style="margin-top:.7em;">' + chips + '</div>' : '')
                    + lifetime
                    + footer
                    + '</div>';
                var head = '<div class="mdSecHead"><span class="material-icons">healing</span><span>Broken files repaired</span></div>';
                host.innerHTML = '<div class="mdCard">' + head + body + '</div>';
            }


            // libCardHtml + donutSvg moved to scripts/library-breakdown.js (06-FOLLOWUP-2a).


            // ---------- Overview ----------

            var busy = { scan: false, fix: false };

            var lastStatus = null;

            // Cumulative fix-run totals since this page load. Every completed run that the poll
            // observes rolls into these counters. The dashboard alert fires ONCE per page session
            // (fixAlertShown) with the running totals — subsequent runs update the counters silently
            // so the user isn't stacked with one modal per scheduled run. Page reload clears state.
            var fixRunsSincePageLoad = 0;

            var fixSucceededSincePageLoad = 0;

            var fixFailedSincePageLoad = 0;

            var fixSkippedSincePageLoad = 0;

            var fixAlertShown = false;

            // Progress-run start markers. `fixPendingBase` snapshots the fix queue depth at run
            // start so the fix status can render "5 / 20 items left" instead of an ETA — users
            // found the elapsed-time projection unhelpful when a single Blu-ray transcode
            // dominated the wall time. The scan run uses server-provided ScanScannersDone /
            // ScanScannersTotal directly (no client capture needed) since those numbers are
            // published from the scan-task main loop. Both fields cleared when the run ends.
            var progressStart = { fixPendingBase: 0 };


            function pctColor(p) {
                return p >= 90 ? '#ef4444' : p >= 70 ? '#f59e0b' : '#00a4dc';
            }


            // Renders one drive as a card: header (path + badges + SMART pill), a row of stat tiles for
            // whatever SMART values the server actually populated, and a usage bar with total/free.
            function driveCardHtml(dr) {
                var used = Math.max(0, dr.TotalBytes - dr.FreeBytes);
                var pct = dr.TotalBytes > 0 ? used / dr.TotalBytes * 100 : 0;
                var pctDisplay = Math.max(3, Math.round(pct));
                var fillColor = pct >= 90 ? '#ef4444' : pct >= 70 ? '#f59e0b' : '#06b6d4';
                var libraryBadge = dr.IsLibraryDrive
                    ? '<span class="mdBadge" style="background:rgba(6,182,212,.16);color:#67e8f9;font:600 11px \'IBM Plex Mono\', monospace;padding:4px 9px;border-radius:6px;">library</span>' : '';
                var recycleBadge = dr.IsRecycleBinDrive
                    ? '<span class="mdBadge" style="background:rgba(236,72,153,.16);color:#f9a8d4;font:600 11px \'IBM Plex Mono\', monospace;padding:4px 9px;border-radius:6px;">recycle bin</span>' : '';
                var shareBadge = dr.IsNetworkShare
                    ? '<span class="mdBadge" style="background:rgba(148,163,184,.16);color:#cbd5e1;font:600 11px \'IBM Plex Mono\', monospace;padding:4px 9px;border-radius:6px;">network share</span>' : '';
                var pill = smartHeaderPillHtml(dr);
                var tiles = smartTilesHtml(dr);
                var usageTitle = deps.fmt(deps.STR.driveUsed, deps.bytes(used), deps.bytes(dr.TotalBytes));

                // Design: one compact row per drive — mono root + tag chips, flex-1 segmented bar, mono free.
                // SMART tiles + model line sit below when the server actually populated them (library/recycle
                // drives only). Keep them because the plugin has more info than the reference prototype shows.
                return '<div class="mdDriveCard">' +
                    '<div class="mdDriveRow">' +
                        '<div class="mdDriveHeadMain">' +
                            '<span class="mdDriveRoot">' + deps.esc(dr.Root) + '</span>' +
                            libraryBadge + recycleBadge + shareBadge +
                        '</div>' +
                        '<div class="mdDriveUsage" style="flex:1 1 300px;">' +
                            '<div class="mdBarTrack" title="' + deps.esc(usageTitle) + '">' +
                                '<div class="mdBarFill" style="width:' + pctDisplay + '%;background:' + fillColor + ';"></div>' +
                            '</div>' +
                        '</div>' +
                        '<span class="mdDriveUsageFree">' + deps.esc(deps.bytes(dr.FreeBytes)) + ' <span style="color:var(--md-meta);font-weight:500;">free</span></span>' +
                        pill +
                    '</div>' +
                    (tiles ? '<div class="mdDriveExtras">' + tiles + '</div>' : '') +
                    '</div>';
            }


            // Big SMART verdict pill anchored top-right of each drive card.
            // Shown only for library/recycle drives (the server only populates SMART for those).
            // The server distinguishes the "async probe still running" state from a real Unknown by
            // returning the message "Checking SMART status..." — we surface that as a distinct
            // pending pill so a fresh boot doesn't briefly say "SMART unknown". The pill auto-heals
            // on the next /Status poll (~3 s during scan/fix, up to ~15 s idle) once the probe lands.
            function smartHeaderPillHtml(dr) {
                if (dr.IsNetworkShare) return ''; // SMART is a property of a physical disk, which a share doesn't expose.
                if (!dr.IsLibraryDrive && !dr.IsRecycleBinDrive) return '';
                if (!mdSmartMonitoringEnabled) {
                    return '<span class="mdSmartPill mdSmartPill--disabled" title="SMART monitoring is off. Right-click (or long-press) to re-enable.">' +
                        '<span class="material-icons">visibility_off</span>SMART disabled</span>';
                }
                var s = (dr.SmartHealth || 'unknown').toLowerCase();
                var msg = dr.SmartMessage || 'SMART status pending.';
                var pending = s === 'unknown' && /checking/i.test(msg);
                var bg, icon, label, cls;
                if (pending)              { bg = '#64748b'; icon = 'hourglass_empty'; label = 'Checking SMART status'; cls = 'pending'; }
                else if (s === 'healthy') { bg = '#22c55e'; icon = 'check_circle';    label = 'SMART OK';               cls = s; }
                else if (s === 'warning') { bg = '#f59e0b'; icon = 'warning';         label = 'SMART warning';          cls = s; }
                else if (s === 'failing') { bg = '#ef4444'; icon = 'error';           label = 'SMART failing';          cls = s; }
                else                      { bg = '#64748b'; icon = 'help_outline';    label = 'SMART unknown';          cls = s; }
                return '<span class="mdSmartPill mdSmartPill--' + cls + '" title="' + deps.esc(msg) + '" style="background:' + bg + ';">' +
                    '<span class="material-icons">' + icon + '</span>' + deps.esc(label) + '</span>';
            }


            // Stat tile row: temperature, wear, powered-on, uncorrected errors. Each tile only renders
            // when the server-side probe actually produced a value; skipping keeps the card honest.
            function smartTilesHtml(dr) {
                if (dr.IsNetworkShare) return '';
                if (!dr.IsLibraryDrive && !dr.IsRecycleBinDrive) return '';
                if (!mdSmartMonitoringEnabled) return '';
                var tiles = [];
                if (dr.SmartTemperatureCelsius != null && dr.SmartTemperatureCelsius > 0) {
                    var t = dr.SmartTemperatureCelsius;
                    var color = t >= 60 ? '#ef4444' : t >= 50 ? '#f59e0b' : '#38bdf8';
                    var sub = dr.SmartTemperatureMaxCelsius
                        ? 'lifetime max ' + dr.SmartTemperatureMaxCelsius + '°C'
                        : '';
                    tiles.push(driveTileHtml('device_thermostat', 'Temperature', t + '°C', sub, color));
                }
                if (dr.SmartWearPercent != null && dr.SmartWearPercent >= 0) {
                    var w = dr.SmartWearPercent;
                    var wc = w >= 80 ? '#ef4444' : w >= 50 ? '#f59e0b' : '#22c55e';
                    var wsub = w === 0 ? 'as new' : w >= 80 ? 'end-of-life soon' : w >= 50 ? 'over halfway worn' : 'well below wear budget';
                    tiles.push(driveTileHtml('battery_charging_full', 'SSD wear used', w + '%', wsub, wc));
                }
                if (dr.SmartPowerOnHours != null && dr.SmartPowerOnHours > 0) {
                    var h = dr.SmartPowerOnHours;
                    var years = h / 8760;
                    var value = years >= 1 ? years.toFixed(1) + ' yr' : h.toLocaleString() + ' h';
                    var sub = years >= 1 ? h.toLocaleString() + ' hours total' : 'since first power-on';
                    tiles.push(driveTileHtml('schedule', 'Powered on', value, sub, 'rgba(230,237,243,.85)'));
                }
                var errs = (dr.SmartReadErrorsUncorrected || 0) + (dr.SmartWriteErrorsUncorrected || 0);
                if (dr.SmartReadErrorsUncorrected != null || dr.SmartWriteErrorsUncorrected != null) {
                    var ec = errs > 0 ? '#ef4444' : '#22c55e';
                    var esub = errs > 0 ? 'data at risk — back up now' : 'no uncorrected I/O errors';
                    tiles.push(driveTileHtml('report_problem', 'I/O errors', errs.toLocaleString(), esub, ec));
                }
                if (!tiles.length) {
                    // No detailed stats available. During the initial async probe the server returns
                    // "Checking SMART status..." — swap the tile sub-text so a fresh boot doesn't
                    // read as "no attributes" (misleading — attributes are on their way).
                    var pending = ((dr.SmartHealth || '').toLowerCase() === 'unknown')
                        && /checking/i.test(dr.SmartMessage || '');
                    var sub = pending ? 'querying the drive - one moment' : 'no attributes reported for this drive';
                    var value = pending ? '...' : '-';
                    tiles.push(driveTileHtml('info', 'SMART detail', value, sub, 'rgba(230,237,243,.55)'));
                }
                return '<div class="mdDriveTiles">' + tiles.join('') + '</div>';
            }


            function driveTileHtml(icon, label, value, sub, color) {
                return '<div class="mdDriveTile" style="--md-tile-accent:' + color + ';">' +
                    '<span class="material-icons mdDriveTileIcon">' + icon + '</span>' +
                    '<div class="mdDriveTileBody">' +
                        '<div class="mdDriveTileValue">' + deps.esc(value) + '</div>' +
                        '<div class="mdDriveTileLabel">' + deps.esc(label) + '</div>' +
                        (sub ? '<div class="mdDriveTileSub">' + deps.esc(sub) + '</div>' : '') +
                    '</div>' +
                    '</div>';
            }


            // User preference — the System Performance card can be hidden via its right-click /
            // long-press menu. Cached so we survive the async config fetch that populates it, and
            // referenced by both the renderer and the toggle. Persisted to plugin config.
            var mdSysPerfHidden = false;

            var mdSysPerfLast = null;


            // Same idea for SMART monitoring — right-click / long-press on any drive-card SMART
            // pill toggles the whole subsystem off. When off: the server probe short-circuits
            // (no smartctl / WMI shell-out), the pill renders as a greyed "SMART disabled" chip,
            // the detail tiles disappear, and existing SmartHealth.* Errors-tab rows get purged.
            // Cached so the renderer (called every 3-15 s from /Status) sees the state without a
            // config round-trip; hydrated from Plugin.Configuration.SmartMonitoringEnabled on load.
            var mdSmartMonitoringEnabled = true;

            var mdDrivesLast = null;

            // UI-18: surgical drive refresh for silent polls. Keeps the mdCard wrapper + mdFade
            // animation state intact and swaps the inner grid HTML only, so free-space / SMART
            // samples stay live without visibly flashing the whole card every 3 seconds. First
            // call builds the wrapper; subsequent calls update #mdDriveGrid in place.
            function renderDrivesSurgical(drives) {
                mdDrivesLast = drives;
                var drivesEl = deps.view.querySelector('#mdDrives');
                if (!drivesEl) return;
                if (drives.length === 0) {
                    drivesEl.innerHTML = '';
                    return;
                }
                var grid = drivesEl.querySelector('.mdDriveGrid');
                if (!grid) {
                    drivesEl.innerHTML = '<div class="mdCard mdFade">' +
                        '<div class="mdSecHead"><span class="material-icons">storage</span>' + deps.esc(deps.STR.driveStorage) + '</div>' +
                        '<div class="mdDriveGrid">' + drives.map(driveCardHtml).join('') + '</div></div>';
                    return;
                }
                grid.innerHTML = drives.map(driveCardHtml).join('');
            }

            // Client-side 7-day dismissal for low-space warnings, per drive root. Deliberately not
            // wired through the server: this is advisory only, and the condition self-clears the
            // moment the user frees space or moves the library. Dismissal survives browser restarts.
            var LOW_SPACE_DISMISS_MS = 7 * 24 * 60 * 60 * 1000;
            function lowSpaceDismissedKey(root) { return 'mdLowSpaceDismissedUntil:' + root; }
            function lowSpaceIsDismissed(root) {
                try {
                    var raw = localStorage.getItem(lowSpaceDismissedKey(root));
                    return !!raw && parseInt(raw, 10) > Date.now();
                } catch (e) { return false; }
            }
            function lowSpaceDismiss(root) {
                try { localStorage.setItem(lowSpaceDismissedKey(root), String(Date.now() + LOW_SPACE_DISMISS_MS)); } catch (e) {}
            }

            function renderLowSpaceWarnings(drives) {
                var host = deps.view.querySelector('#mdLowSpaceWarnings');
                if (!host) return;
                var warned = drives.filter(function (d) { return d.LowSpaceWarning && !lowSpaceIsDismissed(d.Root); });
                if (warned.length === 0) { host.innerHTML = ''; return; }
                host.innerHTML = warned.map(function (d) {
                    var role = d.IsLibraryDrive ? 'Library drive' : (d.IsPluginDataDrive ? 'Plugin data drive' : (d.IsRecycleBinDrive ? 'Recycle bin drive' : 'Drive'));
                    return '<div class="mdLowSpaceBanner" data-root="' + deps.esc(d.Root) + '" style="margin-bottom:1em;padding:.8em 1.2em;border-radius:12px;background:rgba(245,158,11,.16);border:2px solid #f59e0b;">' +
                        '<div style="display:flex;gap:.8em;align-items:center;flex-wrap:wrap;">' +
                        '<span class="material-icons" style="color:#f59e0b;font-size:1.5em;">warning</span>' +
                        '<div style="flex:1;min-width:20em;">' +
                        '<div style="font-weight:600;color:#f59e0b;">' + deps.esc(role) + ' ' + deps.esc(d.Root) + '</div>' +
                        '<div class="fieldDescription" style="margin-top:.2em;">' + deps.esc(d.LowSpaceWarning) + '</div>' +
                        '</div>' +
                        '<button is="emby-button" type="button" class="mdLowSpaceDismiss" style="background:transparent;border:0;color:#f59e0b;padding:.3em;min-width:0;" title="Hide this warning for 7 days on this drive."><span class="material-icons" style="font-size:1.2em;">close</span></button>' +
                        '</div>' +
                        '</div>';
                }).join('');
                host.querySelectorAll('.mdLowSpaceDismiss').forEach(function (btn) {
                    btn.addEventListener('click', function () {
                        var banner = btn.closest('.mdLowSpaceBanner');
                        if (!banner) return;
                        lowSpaceDismiss(banner.getAttribute('data-root'));
                        banner.style.display = 'none';
                    });
                });
            }


            function renderSystemStats(sys) {
                var el = deps.view.querySelector('#mdSystem');
                if (!sys) { el.innerHTML = ''; mdSysPerfLast = null; return; }
                mdSysPerfLast = sys;

                if (mdSysPerfHidden) {
                    if (!el.querySelector('.mdSysHiddenChip')) {
                        el.innerHTML = '<div class="mdSysHiddenChip" title="Right-click (or long-press) to show system performance again.">' +
                            '<span class="material-icons">speed</span>' +
                            '<span class="mdSysHiddenLabel">' + deps.esc(deps.STR.systemStats) + '</span>' +
                            '<span class="mdSysHiddenHint">hidden</span>' +
                            '</div>';
                    }
                    return;
                }

                var gpus = sys.Gpus || [];
                var expectedGpuCells = Math.max(1, gpus.length);
                var existingGpuCells = el.querySelectorAll('.mdSysCell[data-kind^="gpu"]').length;

                // Only rebuild the DOM structure when the card first appears or when the GPU list changes shape
                // (adding/removing a card mid-session, or going from "no GPU detected" to detected).
                if (!el.querySelector('.mdSysCell') || existingGpuCells !== expectedGpuCells) {
                    el.innerHTML = buildSystemStatsHtml(sys, gpus);
                }

                updateSystemStatsInPlace(sys, gpus);
            }


            function showSysPerfCtxMenu(x, y) {
                var menu = deps.view.querySelector('#mdSysCtxMenu');
                var label = mdSysPerfHidden ? 'Show system performance' : 'Hide system performance';
                var icon = mdSysPerfHidden ? 'visibility' : 'visibility_off';
                menu.innerHTML = '<div class="mdCtxItem" data-act="toggle">' +
                    '<span class="material-icons">' + icon + '</span>' +
                    '<span>' + label + '</span></div>';
                menu.style.display = 'block';
                menu.style.left = '0px';
                menu.style.top = '0px';
                var rect = menu.getBoundingClientRect();
                var px = Math.min(x, window.innerWidth - rect.width - 8);
                var py = Math.min(y, window.innerHeight - rect.height - 8);
                menu.style.left = Math.max(4, px) + 'px';
                menu.style.top = Math.max(4, py) + 'px';

                menu.querySelector('[data-act="toggle"]').addEventListener('click', function (ev) {
                    ev.stopPropagation();
                    closeSysPerfCtxMenu();
                    toggleSysPerfHidden();
                });
            }

            function closeSysPerfCtxMenu() {
                var menu = deps.view.querySelector('#mdSysCtxMenu');
                if (menu) menu.style.display = 'none';
            }

            function toggleSysPerfHidden() {
                mdSysPerfHidden = !mdSysPerfHidden;
                // Clear the container so the renderer rebuilds fresh (chip → card or card → chip).
                var el = deps.view.querySelector('#mdSystem');
                if (el) el.innerHTML = '';
                if (mdSysPerfLast) renderSystemStats(mdSysPerfLast);
                // Persist. Read-modify-write matches the pattern used by the fix-order dialog so
                // we don't clobber unrelated fields.
                deps.api.configuration(deps.PLUGIN_ID).then(function (c) {
                    c.ShowSystemPerformance = !mdSysPerfHidden;
                    ApiClient.updatePluginConfiguration(deps.PLUGIN_ID, c);
                });
            }


            // SMART monitoring context menu — mirrors the System Performance one. Opens on right-
            // click / long-press of any drive-card SMART pill. Same visual shape and positioning
            // logic; different toggle target.
            function showSmartCtxMenu(x, y) {
                var menu = deps.view.querySelector('#mdSmartCtxMenu');
                var label = mdSmartMonitoringEnabled ? 'Disable SMART monitoring' : 'Enable SMART monitoring';
                var icon = mdSmartMonitoringEnabled ? 'visibility_off' : 'visibility';
                menu.innerHTML = '<div class="mdCtxItem" data-act="toggle">' +
                    '<span class="material-icons">' + icon + '</span>' +
                    '<span>' + label + '</span></div>';
                menu.style.display = 'block';
                menu.style.left = '0px';
                menu.style.top = '0px';
                var rect = menu.getBoundingClientRect();
                var px = Math.min(x, window.innerWidth - rect.width - 8);
                var py = Math.min(y, window.innerHeight - rect.height - 8);
                menu.style.left = Math.max(4, px) + 'px';
                menu.style.top = Math.max(4, py) + 'px';

                menu.querySelector('[data-act="toggle"]').addEventListener('click', function (ev) {
                    ev.stopPropagation();
                    closeSmartCtxMenu();
                    toggleSmartMonitoring();
                });
            }

            function closeSmartCtxMenu() {
                var menu = deps.view.querySelector('#mdSmartCtxMenu');
                if (menu) menu.style.display = 'none';
            }

            function toggleSmartMonitoring() {
                mdSmartMonitoringEnabled = !mdSmartMonitoringEnabled;
                // Force an immediate re-render of the drives card so the pill state flips without
                // waiting up to 15 s for the next /Status poll. Uses the cached last drives array.
                if (mdDrivesLast) {
                    var drivesEl = deps.view.querySelector('#mdDrives');
                    if (drivesEl && mdDrivesLast.length > 0) {
                        drivesEl.innerHTML = '<div class="mdCard mdFade">' +
                            '<div class="mdSecHead"><span class="material-icons">storage</span>' + deps.esc(deps.STR.driveStorage) + '</div>' +
                            '<div class="mdDriveGrid">' +
                            mdDrivesLast.map(driveCardHtml).join('') +
                            '</div></div>';
                    }
                }
                // Persist config + purge existing SmartHealth.* diagnostics from the Errors tab
                // when turning OFF (so the user isn't left staring at stale rows they can't clear).
                // Enabling does nothing extra — the server probe will start recording fresh entries
                // on the next scan tick if it hits something.
                deps.api.configuration(deps.PLUGIN_ID).then(function (c) {
                    c.SmartMonitoringEnabled = mdSmartMonitoringEnabled;
                    ApiClient.updatePluginConfiguration(deps.PLUGIN_ID, c).then(function () {
                        if (!mdSmartMonitoringEnabled) {
                            deps.api.ajax({
                                type: 'POST',
                                url: ApiClient.getUrl('MediaDash/Diagnostics/PurgeByPrefix?prefix=' + encodeURIComponent('SmartHealth'))
                            });
                        }
                    });
                });
            }


            function buildSystemStatsHtml(sys, gpus) {
                function cellHtml(icon, iconColor, title, kind) {
                    return '<div class="mdSysCell" data-kind="' + kind + '">' +
                        '<div class="mdSysHead"><span class="material-icons" style="color:' + iconColor + ';">' + icon + '</span>' + deps.esc(title) + '</div>' +
                        '<div class="mdSysValue" data-field="value"></div>' +
                        '<div class="mdBarTrack" data-field="track"><div class="mdBarFill" data-field="bar" style="width:3%;"></div></div>' +
                        '<div class="mdSysHint" data-field="hint"></div>' +
                        '</div>';
                }

                var gpuCellsHtml = '';
                if (gpus.length === 0) {
                    gpuCellsHtml = cellHtml('developer_board', '#10b981', deps.STR.statGpu, 'gpu-0');
                } else {
                    for (var i = 0; i < gpus.length; i++) {
                        var title = gpus.length === 1 ? deps.STR.statGpu : deps.STR.statGpu + ' ' + gpus[i].Index;
                        gpuCellsHtml += cellHtml('developer_board', '#10b981', title, 'gpu-' + i);
                    }
                }

                return '<div class="mdCard">' +
                    '<div style="display:flex;align-items:center;justify-content:space-between;gap:16px;margin:0 0 20px;flex-wrap:wrap;">' +
                        '<div class="mdSecHead" style="margin:0;">' +
                            '<span class="material-icons">speed</span>' + deps.esc(deps.STR.systemStats) +
                        '</div>' +
                        '<span data-field="platform" style="font:500 12.5px \'IBM Plex Mono\', monospace;color:var(--md-meta);"></span>' +
                    '</div>' +
                    '<div class="mdSysGrid">' +
                        cellHtml('memory', '#22d3ee', deps.STR.statCpu, 'cpu') +
                        cellHtml('memory_alt', '#8b5cf6', deps.STR.statRam, 'ram') +
                        gpuCellsHtml +
                    '</div></div>';
            }


            function updateSystemStatsInPlace(sys, gpus) {
                var el = deps.view.querySelector('#mdSystem');
                var platform = el.querySelector('[data-field="platform"]');
                if (platform) platform.textContent = deps.fmt(deps.STR.statPlatform, sys.Platform || 'Unknown');

                var naPlain = '<span style="opacity:.5;">N/A</span>';
                var platformHint = deps.fmt(deps.STR.statUnavailable, sys.Platform || 'this host');

                // CPU
                if (!sys.SystemStatsAvailable) {
                    setCellValues(el, 'cpu', naPlain, platformHint, null);
                } else if (sys.SystemCpuPercent == null) {
                    setCellValues(el, 'cpu', '<span style="opacity:.5;">' + deps.esc(deps.STR.statUnknown) + '</span>', deps.fmt(deps.STR.statCpuHint, sys.CpuCoreCount || 1), null);
                } else {
                    setCellValues(el, 'cpu', sys.SystemCpuPercent.toFixed(1) + '%', deps.fmt(deps.STR.statCpuHint, sys.CpuCoreCount || 1), sys.SystemCpuPercent);
                }

                // RAM
                if (!sys.SystemStatsAvailable || sys.SystemRamTotalBytes == null || sys.SystemRamTotalBytes <= 0) {
                    setCellValues(el, 'ram', naPlain, platformHint, null);
                } else {
                    var ramPct = sys.SystemRamUsedBytes / sys.SystemRamTotalBytes * 100;
                    setCellValues(el, 'ram', deps.bytes(sys.SystemRamUsedBytes), deps.fmt(deps.STR.statRamHint, deps.bytes(sys.SystemRamUsedBytes), deps.bytes(sys.SystemRamTotalBytes)), ramPct);
                }

                // GPU(s)
                if (gpus.length === 0) {
                    setCellValues(el, 'gpu-0', '<span style="opacity:.5;">' + deps.esc(deps.STR.statUnknown) + '</span>', deps.STR.statGpuMissing, null);
                } else {
                    for (var i = 0; i < gpus.length; i++) {
                        var g = gpus[i];
                        var hint = deps.esc(g.Name) + ' · ' + deps.fmt(deps.STR.statGpuHint, g.Source);
                        setCellValues(el, 'gpu-' + i, (g.Percent || 0).toFixed(1) + '%', hint, g.Percent, true);
                    }
                }
            }


            function setCellValues(rootEl, kind, valueHtml, hintHtml, barPct, hintIsHtml) {
                var cell = rootEl.querySelector('.mdSysCell[data-kind="' + kind + '"]');
                if (!cell) return;
                cell.querySelector('[data-field="value"]').innerHTML = valueHtml;
                var hintEl = cell.querySelector('[data-field="hint"]');
                if (hintIsHtml) hintEl.innerHTML = hintHtml; else hintEl.textContent = hintHtml;

                var track = cell.querySelector('[data-field="track"]');
                var bar = cell.querySelector('[data-field="bar"]');
                if (typeof barPct === 'number' && !isNaN(barPct)) {
                    track.style.display = '';
                    var color = pctColor(barPct);
                    bar.style.width = Math.max(3, Math.round(barPct)) + '%';
                    bar.style.background = color;
                } else {
                    // Keep the track visible as an empty 5px bar so cell heights stay uniform
                    // (design shows GPU-with-no-counter as an empty track, not a gap).
                    track.style.display = '';
                    bar.style.width = '0%';
                    bar.style.background = 'transparent';
                }
            }


            function setProgress(visible, percent) {
                deps.view.querySelector('#mdProgressWrap').style.display = visible ? '' : 'none';
                if (visible) {
                    deps.view.querySelector('#mdProgressFill').style.width = Math.max(3, percent || 0) + '%';
                }
            }


            // Fixer-order dialog (E1). openFixOrderDialog reads current order from plugin config
            // and paints the list; renderFixOrderList rebuilds it after each up/down move so the
            // hard-constraint warning stays in sync.
            //
            // Second axis: `disabled` is the set of types the user has muted from fix runs (via
            // the disable icon next to the up/down arrows). Persisted as config.FixerDisabled.
            // Independent of the Settings→What-to-fix FixMode: disabled fixers still get scanned
            // (issues appear in the Issues tab) but the fixer skips them until re-enabled or
            // Reset to default clears the whole list.
            function openFixOrderDialog() {
                deps.api.configuration(deps.PLUGIN_ID).then(function (c) {
                    var order = (c.FixerOrder && c.FixerOrder.length) ? c.FixerOrder.slice() : deps.DEFAULT_FIXER_ORDER.slice();
                    // Ensure every known type appears — a saved order from an older version might
                    // predate newer types. Append missing entries at the end so nothing silently drops.
                    deps.DEFAULT_FIXER_ORDER.forEach(function (t) {
                        if (order.indexOf(t) === -1) order.push(t);
                    });
                    var disabled = (c.FixerDisabled && c.FixerDisabled.length) ? c.FixerDisabled.slice() : [];
                    renderFixOrderList(order, disabled);
                    deps.view.querySelector('#mdFixOrderDialog').style.display = 'flex';
                });
            }


            function closeFixOrderDialog() {
                deps.view.querySelector('#mdFixOrderDialog').style.display = '';
            }


            function renderFixOrderList(order, disabled) {
                disabled = disabled || [];
                var listEl = deps.view.querySelector('#mdFixOrderList');
                var html = '';
                for (var i = 0; i < order.length; i++) {
                    var t = order[i];
                    var meta = deps.STR.types && deps.STR.types[t];
                    var title = meta && meta.title ? meta.title : t;
                    var icon = meta && meta.icon ? meta.icon : 'build';
                    var color = meta && meta.color ? meta.color : 'var(--md-meta)';
                    var upDisabled = i === 0 ? ' disabled' : '';
                    var downDisabled = i === order.length - 1 ? ' disabled' : '';
                    var isOff = disabled.indexOf(t) !== -1;
                    var rowStyle = isOff ? ' style="opacity:.5;"' : '';
                    var labelStyle = isOff ? ' style="text-decoration:line-through;"' : '';
                    var toggleIcon = isOff ? 'visibility_off' : 'visibility';
                    var toggleTitle = isOff ? 'Skipped in fix runs — click to re-enable' : 'Skip this fixer in every fix run until re-enabled';
                    html += '<div class="mdOrderRow" data-type="' + t + '"' + rowStyle + '>'
                        + '<span class="mdOrderNum">' + (i + 1) + '</span>'
                        + '<span class="material-icons mdOrderIcon" style="color:' + color + ';">' + icon + '</span>'
                        + '<span class="mdOrderLabel"' + labelStyle + '>' + title + '</span>'
                        + '<button type="button" class="mdIconBtn mdFixOrderUp"' + upDisabled + ' title="Move up"><span class="material-icons">arrow_upward</span></button>'
                        + '<button type="button" class="mdIconBtn mdFixOrderDown"' + downDisabled + ' title="Move down"><span class="material-icons">arrow_downward</span></button>'
                        + '<button type="button" class="mdIconBtn mdFixOrderToggle" title="' + toggleTitle + '"><span class="material-icons">' + toggleIcon + '</span></button>'
                        + '</div>';
                }
                listEl.innerHTML = html;
                listEl.querySelectorAll('.mdFixOrderUp').forEach(function (btn) {
                    btn.addEventListener('click', function () {
                        var row = btn.closest('.mdOrderRow');
                        var pos = order.indexOf(row.getAttribute('data-type'));
                        if (pos > 0) {
                            var tmp = order[pos - 1]; order[pos - 1] = order[pos]; order[pos] = tmp;
                            renderFixOrderList(order, disabled);
                        }
                    });
                });
                listEl.querySelectorAll('.mdFixOrderDown').forEach(function (btn) {
                    btn.addEventListener('click', function () {
                        var row = btn.closest('.mdOrderRow');
                        var pos = order.indexOf(row.getAttribute('data-type'));
                        if (pos > -1 && pos < order.length - 1) {
                            var tmp = order[pos + 1]; order[pos + 1] = order[pos]; order[pos] = tmp;
                            renderFixOrderList(order, disabled);
                        }
                    });
                });
                listEl.querySelectorAll('.mdFixOrderToggle').forEach(function (btn) {
                    btn.addEventListener('click', function () {
                        var row = btn.closest('.mdOrderRow');
                        var t = row.getAttribute('data-type');
                        var pos = disabled.indexOf(t);
                        if (pos === -1) { disabled.push(t); } else { disabled.splice(pos, 1); }
                        renderFixOrderList(order, disabled);
                    });
                });

                // Soft-warn on hard-constraint violations. Doesn't block save.
                var warnings = [];
                deps.FIXER_HARD_CONSTRAINTS.forEach(function (c) {
                    var before = order.indexOf(c[0]);
                    var after = order.indexOf(c[1]);
                    if (before > -1 && after > -1 && before > after) {
                        warnings.push(c[0] + ' should run before ' + c[1] + ': ' + c[2]);
                    }
                });
                var warnEl = deps.view.querySelector('#mdFixOrderWarn');
                if (warnings.length) {
                    warnEl.innerHTML = '<span class="material-icons" style="font-size:1em;vertical-align:-3px;">warning_amber</span> ' + warnings.map(function (w) { return '<div>' + w + '</div>'; }).join('');
                    warnEl.style.display = '';
                } else {
                    warnEl.style.display = 'none';
                }

                // Save the current order + disabled set to data attributes so saveFixOrder can
                // read them without reconstructing from DOM state.
                listEl.setAttribute('data-order', order.join(','));
                listEl.setAttribute('data-disabled', disabled.join(','));
            }


            function saveFixOrder() {
                var listEl = deps.view.querySelector('#mdFixOrderList');
                var order = (listEl.getAttribute('data-order') || '').split(',').filter(function (s) { return s.length > 0; });
                var disabled = (listEl.getAttribute('data-disabled') || '').split(',').filter(function (s) { return s.length > 0; });
                if (order.length === 0) return;
                var isDefault = order.length === deps.DEFAULT_FIXER_ORDER.length
                    && order.every(function (t, i) { return t === deps.DEFAULT_FIXER_ORDER[i]; });
                deps.api.configuration(deps.PLUGIN_ID).then(function (c) {
                    // Store [] when the user has reset to default — keeps the config JSON small and
                    // means future default-order changes automatically pick up.
                    c.FixerOrder = isDefault ? [] : order;
                    c.FixerDisabled = disabled;
                    ApiClient.updatePluginConfiguration(deps.PLUGIN_ID, c).then(function () {
                        closeFixOrderDialog();
                        if (window.Dashboard && Dashboard.processPluginConfigurationUpdateResult) {
                            Dashboard.processPluginConfigurationUpdateResult();
                        }
                    });
                });
            }


            function renderStatus(s, silent) {
                // UI-17: badge reflects open work only. Use the client's open-view count when the
                // Issues tab has fetched an open-view list (lastIssueList can diverge from the
                // server's summary total — bulk approve/dismiss races, mid-fix transitions). When
                // the user is in the Ignored view (lastIssueList is ignored rows), or the factory
                // hasn't fetched yet, fall back to the server's authoritative OpenIssueTotal.
                var openClient = deps.issuesTab && !deps.issuesTab.showingIgnored() ? deps.issuesTab.count() : null;
                var issueTotal = openClient != null ? openClient : (s.OpenIssueTotal || 0);
                deps.updateTabBadge('#mdIssueBadge', issueTotal);
                deps.updateTabBadge('#mdHistoryBadge', s.FailedHistoryTotal || 0);
                // Detect a just-finished fix run — the server sets s.LastFixRun.FinishedAtUtc on completion.
                // Every new completion rolls into the page-session accumulators; the alert only fires
                // ONCE per session so a user who leaves the page open across several scheduled runs
                // gets one cumulative summary instead of a modal per run. Fast all-fail runs (e.g.
                // permission denied on every file) finish in under a second, so the progress bar just
                // flashes; the (once-only) alert is still the reliable "the button did something" signal.
                // Guard on lastStatus itself — not on prevFinishedAt !== undefined. lastStatus starts as
                // null on page load, which short-circuited prevFinishedAt to null, and null !== undefined
                // is true — so the alert fired on every page open whenever the most-recent completed run
                // had failures, even mid-flight of a new run.
                var prevFinishedAt = lastStatus && lastStatus.LastFixRun && lastStatus.LastFixRun.FinishedAtUtc;
                var curr = s.LastFixRun;
                if (lastStatus !== null && curr && curr.FinishedAtUtc && curr.FinishedAtUtc !== prevFinishedAt) {
                    fixRunsSincePageLoad++;
                    fixSucceededSincePageLoad += (curr.Succeeded || 0);
                    fixFailedSincePageLoad += (curr.Failed || 0);
                    fixSkippedSincePageLoad += (curr.Skipped || 0);
                    if (!fixAlertShown && fixFailedSincePageLoad > 0) {
                        fixAlertShown = true;
                        var reason = curr.TopFailureReason || 'unknown';
                        var runsPhrase = fixRunsSincePageLoad === 1
                            ? 'Fix run finished: '
                            : 'Fix runs since you opened this page (' + fixRunsSincePageLoad + '): ';
                        var skippedPhrase = fixSkippedSincePageLoad > 0
                            ? ', ' + fixSkippedSincePageLoad + ' skipped (nothing to do or safe refusal)'
                            : '';
                        var msg = runsPhrase + fixSucceededSincePageLoad + ' succeeded, ' + fixFailedSincePageLoad + ' failed' + skippedPhrase + '.\n\n'
                            + 'Most common failure reason in the latest run: ' + reason + '.\n\n'
                            + 'See the Errors tab for the specific files. Reload the page to be alerted about future runs.';
                        if (reason === 'Permission denied') {
                            msg += '\n\nFix on the host: give the user Jellyfin runs as (typically "jellyfin" on Linux) read+write on your library folders. Example: sudo chown -R jellyfin:jellyfin /mnt/media && sudo find /mnt/media -type d -exec chmod g+rwx {} +';
                        } else if (reason === 'Not enough free disk space') {
                            msg += '\n\nFree some space on the target drive, then click "Retry all fixes" on the Errors tab.';
                        }
                        if (window.Dashboard && Dashboard.alert) {
                            Dashboard.alert({ title: 'Fix run finished with failures', message: msg });
                        } else {
                            alert(msg);
                        }
                    } else if (!fixAlertShown && fixSkippedSincePageLoad > 0 && fixFailedSincePageLoad === 0 && fixSucceededSincePageLoad === 0) {
                        // Every attempted item was a safe skip (e.g. all destination collisions, all
                        // already-healthy). No errors to alert about, but "0 fixed" needs to explain
                        // itself so the user doesn't think the plugin is broken.
                        fixAlertShown = true;
                        var info = 'Fix run finished — 0 succeeded, ' + fixSkippedSincePageLoad + ' skipped (nothing needed doing, or MediaDash safely refused to overwrite / touch DRM / touch outside your library). Open each row on the Issues tab for the specific reason.';
                        if (window.Dashboard && Dashboard.alert) {
                            Dashboard.alert({ title: 'Fix run finished — no changes', message: info });
                        }
                    }
                }
                lastStatus = s;
                var state = deps.view.querySelector('#mdScanState');
                var scanBtn = deps.view.querySelector('#mdScanNow');
                var fixBtn = deps.view.querySelector('#mdFixNow');
                // Button stays enabled while running so it can be clicked to cancel; label + colour swap to "Stop".
                scanBtn.classList.toggle('mdActionStop', s.IsScanning);
                scanBtn.classList.toggle('mdActionScan', !s.IsScanning);
                scanBtn.querySelector('.material-icons').textContent = s.IsScanning ? 'stop' : 'search';
                deps.view.querySelector('#mdScanNowLabel').textContent = s.IsScanning ? deps.STR.stopScanLabel : deps.STR.scanNowLabel;
                fixBtn.classList.toggle('mdActionStop', s.IsFixing);
                fixBtn.classList.toggle('mdActionFix', !s.IsFixing);
                fixBtn.querySelector('.material-icons').textContent = s.IsFixing ? 'stop' : 'build';
                deps.view.querySelector('#mdFixNowLabel').textContent = s.IsFixing ? deps.STR.stopFixLabel : deps.STR.fixNowLabel;

                var wasBusy = busy.scan || busy.fix;
                // Reveal the state row while scanning or fixing (CSS keeps it hidden otherwise). Also clear
                // the masthead "Last scan…" line when there's no prior scan so a first-time run doesn't keep
                // showing the stale "No scan has run yet. Click Scan now…" placeholder.
                state.classList.toggle('mdScanBusy', s.IsScanning || s.IsFixing);
                if ((s.IsScanning || s.IsFixing) && !s.LastScanCompletedUtc && !s.LastScanUtc) {
                    var mastheadEl = deps.view.querySelector('#mdLastScan');
                    if (mastheadEl) mastheadEl.textContent = '';
                }

                if (s.IsScanning) {
                    busy.scan = true;
                    var scanPct = s.ScanProgress || 0;
                    // Mirror the fix branch below: derive both the percent text and the progress bar
                    // fill from the server's scanner-count (N done / M total) so the whole progress
                    // cluster tells one story. Falls back to the server-side ScanProgress while
                    // ScanScannersTotal is null (very first poll before ScanTask has published).
                    var scannersDone = s.ScanScannersDone || 0;
                    var scannersTotal = s.ScanScannersTotal || 0;
                    var scanItemsPct = scannersTotal > 0
                        ? Math.round(scannersDone / scannersTotal * 100)
                        : Math.round(scanPct);
                    var scanItemsPart = scannersTotal > 0
                        ? ' · ' + scannersDone + ' / ' + scannersTotal + ' scanners done'
                        : '';
                    state.textContent = deps.fmt(deps.STR.scanRunning, scanItemsPct) + scanItemsPart;
                    setProgress(true, scanItemsPct);
                    deps.schedulePoll();
                } else if (s.IsFixing) {
                    busy.fix = true;
                    var fixPct = s.FixProgress || 0;
                    var remaining = s.PendingFixCount || 0;
                    // Snapshot the queue depth on the first poll we see a real remaining count so
                    // the ratio has a stable denominator. If new issues get auto-queued mid-run,
                    // bump the denominator so we never show "22 / 20 left".
                    if (!progressStart.fixPendingBase && remaining > 0) {
                        progressStart.fixPendingBase = remaining;
                    } else if (remaining > progressStart.fixPendingBase) {
                        progressStart.fixPendingBase = remaining;
                    }
                    // Both the percent text and the progress bar derive from the same items ratio
                    // as the "N / M items left" caption so the whole progress cluster tells one
                    // story. Falls back to the server's FixProgress on the very first poll before
                    // a denominator is captured, so the bar isn't stuck at 0% for the first tick.
                    var itemsDone = Math.max(0, progressStart.fixPendingBase - remaining);
                    var itemsPct = progressStart.fixPendingBase > 0
                        ? Math.round(itemsDone / progressStart.fixPendingBase * 100)
                        : Math.round(fixPct);
                    var itemsPart = progressStart.fixPendingBase > 0
                        ? ' · ' + remaining + ' / ' + progressStart.fixPendingBase + ' items left'
                        : (remaining > 0 ? ' · ' + remaining + ' item' + (remaining === 1 ? '' : 's') + ' left' : '');
                    // Per-file % (fraction 0..1) from ffmpeg -progress. Only shown when the current
                    // fixer is actually reporting it; cheap fixers (delete, rename, tag write) omit it.
                    var opPart = (typeof s.CurrentOperationProgress === 'number')
                        ? ' · this file ' + Math.round(s.CurrentOperationProgress * 100) + '%'
                        : '';
                    state.textContent = deps.fmt(deps.STR.fixRunning, itemsPct) + itemsPart + opPart;
                    setProgress(true, itemsPct);
                    deps.schedulePoll();
                } else if (busy.scan) {
                    busy.scan = false;
                    setProgress(false);
                    state.textContent = deps.fmt(deps.STR.scanDone, s.OpenIssueTotal);
                } else if (busy.fix) {
                    busy.fix = false;
                    progressStart.fixPendingBase = 0;
                    setProgress(false);
                    state.textContent = deps.STR.fixDone;
                    if (deps.view.querySelector('#mdTab-history').style.display !== 'none' && deps.historyTab) deps.historyTab.activate();
                } else if (s.LastScanCompletedUtc || s.LastScanUtc) {
                    setProgress(false);
                    // UI-15: prefer the scan-completion timestamp (ScanTask persists it on each
                    // successful finish). Fall back to LastScanUtc for installs that haven't
                    // completed a scan since the upgrade or when the state key is missing.
                    var scanAt = s.LastScanCompletedUtc || s.LastScanUtc;
                    state.textContent = deps.fmt(deps.STR.scanIdle, new Date(scanAt).toLocaleString());
                    var lastScanEl = deps.view.querySelector('#mdLastScan');
                    if (lastScanEl) lastScanEl.textContent = deps.fmt(deps.STR.scanIdle, new Date(scanAt).toLocaleString());
                } else {
                    setProgress(false);
                    state.textContent = deps.STR.scanNever;
                    var lastScanEl2 = deps.view.querySelector('#mdLastScan');
                    if (lastScanEl2) lastScanEl2.textContent = deps.STR.scanNever;
                }

                var activityEl = deps.view.querySelector('#mdActivity');
                if (s.IsScanning || s.IsFixing) {
                    // Show "Label — path" when both known, either alone when we only have one.
                    // Scanner boundaries set a label without a path (during initial item enumeration);
                    // ProbingScannerBase-derived scanners and FixTask add the path per file.
                    var lbl = s.CurrentActivityLabel || '';
                    var pth = s.CurrentActivity || '';
                    var text = lbl && pth ? (lbl + ' — ' + pth) : (lbl || pth);
                    activityEl.textContent = text;
                    activityEl.title = text;
                } else {
                    activityEl.textContent = '';
                    activityEl.title = '';
                }

                var pauseNote = deps.view.querySelector('#mdFixPauseNote');
                if (s.IsFixing && s.FixPauseReason) {
                    deps.view.querySelector('#mdFixPauseText').textContent = s.FixPauseReason;
                    pauseNote.style.display = 'flex';
                } else {
                    pauseNote.style.display = 'none';
                }

                // Keep polling while the Overview tab is visible so the system-performance card is actually live
                // (task-manager-ish). schedulePoll during scan/fix already handles those cases; this adds the idle
                // case where a user just wants to watch CPU / GPU / RAM tick.
                if (!s.IsScanning && !s.IsFixing && deps.view.querySelector('#mdTab-overview').style.display !== 'none') {
                    deps.schedulePoll();
                }

                // Refresh the Issues tab in the background during a fix run so items visibly disappear as they get
                // marked Fixed (or stay behind with the failure hint).
                if (s.IsFixing && deps.view.querySelector('#mdTab-issues').style.display !== 'none' && deps.issuesTab) {
                    deps.issuesTab.activate();
                }

                // System stats are live — update every poll (values change), regardless of busy state.
                renderSystemStats(s.System);

                var recycleWarn = deps.view.querySelector('#mdRecycleWarn');
                if (s.RecycleBinCrossVolume && !deps.bannerDismissed('mdRecycleWarn')) {
                    recycleWarn.style.display = '';
                    deps.view.querySelector('#mdRecycleWarnText').textContent =
                        'The recycle bin is at "' + (s.RecycleBinPath || '') + '", which is on a different volume than your library. ' +
                        'Recycling files requires copying them across — needs matching free space and fails when the bin volume fills up. ' +
                        'Move the recycle bin onto the same volume as your media (e.g. "/mnt/media/.mediadash-recycle") to make recycles instant.';
                } else {
                    recycleWarn.style.display = 'none';
                }

                renderLowSpaceWarnings(s.Drives || []);

                // Stale-queue banner: the last completed fix run recorded queued items outside
                // any enabled library. Session-dismissible via the standard dismissBanner path
                // (data-banner="mdStaleQueueWarn"). Refreshes whenever the LastFixRun timestamp
                // changes, so a re-run that still has skipped items resurfaces the banner.
                var staleWarn = deps.view.querySelector('#mdStaleQueueWarn');
                if (staleWarn) {
                    var skipCount = (curr && curr.SkippedLibraryDisabled) || 0;
                    if (skipCount > 0 && !deps.bannerDismissed('mdStaleQueueWarn')) {
                        deps.view.querySelector('#mdStaleQueueWarnText').textContent =
                            skipCount + ' queued issue' + (skipCount === 1 ? '' : 's') +
                            ' were skipped because their library is no longer enabled in Settings → Libraries. Re-enable the library to have them run, or clear them here (recoverable from the Issues tab via Revert).';
                        staleWarn.style.display = '';
                    } else {
                        staleWarn.style.display = 'none';
                    }
                }

                var redlBanner = deps.view.querySelector('#mdRedownloadWarn');
                var redl = s.RedownloadWarnings || [];
                if (redl.length > 0 && !deps.bannerDismissed('mdRedownloadWarn')) {
                    redlBanner.style.display = '';
                    var artifactCount = redl.filter(function (w) { return w.LikelySubtitleBugArtifact; }).length;
                    var leadText = redl.length + ' file' + (redl.length === 1 ? '' : 's') + ' flagged since the last scan.';
                    if (artifactCount > 0) {
                        leadText += ' ' + artifactCount + ' look like leftovers from a pre-0.9.9 subtitle-fixer bug that deleted the file (not a real redownload) — see the marked rows below.';
                    }
                    deps.view.querySelector('#mdRedownloadWarnLead').textContent = leadText;
                    var listEl = deps.view.querySelector('#mdRedownloadWarnList');
                    listEl.innerHTML = redl.map(function (w) {
                        var mb = function (b) { return (b / 1048576).toFixed(0) + ' MB'; };
                        var artifactTag = w.LikelySubtitleBugArtifact
                            ? ' <span style="color:#f59e0b;font-weight:600;" title="This flag is likely a downstream effect of the pre-0.9.9 subtitle-fixer path-collision bug — the video was deleted by MediaDash and later refilled by *arr or a manual restore. Not an actual redownload of a re-encoded file.">[likely subtitle-bug artefact]</span>'
                            : '';
                        var restoreBtn = w.LikelySubtitleBugArtifact
                            ? ' <button type="button" class="mdRedownloadRestoreOpt" data-history-id="' + w.HistoryId + '" style="background:transparent;border:1px solid #f59e0b;color:#f59e0b;border-radius:6px;padding:.05em .5em;margin-left:.4em;font-size:.85em;cursor:pointer;" title="Find the smaller remuxed copy in the recycle bin and restore it here. Acknowledges the row after.">Restore optimized</button>'
                            : '';
                        var ackBtn = ' <button type="button" class="mdRedownloadAck" data-history-id="' + w.HistoryId + '" style="background:transparent;border:1px solid #94a3b8;color:#94a3b8;border-radius:6px;padding:.05em .5em;margin-left:.4em;font-size:.85em;cursor:pointer;" title="Acknowledge this warning. The row stays in history; the banner stops flagging it on future scans.">Acknowledge</button>';
                        return '<li title="' + deps.esc(w.Path) + '" data-history-id="' + w.HistoryId + '">' + deps.esc(w.Path.split('/').pop()) +
                               ' — original ' + mb(w.OriginalBytes) + ' → shrunk → now back at ' + mb(w.CurrentBytes) +
                               ' (fixed ' + new Date(w.FixedAtUtc).toLocaleDateString() + ')' + artifactTag + restoreBtn + ackBtn + '</li>';
                    }).join('');
                } else {
                    redlBanner.style.display = 'none';
                }

                // Skip re-rendering the static content (hero, per-type cards, savings bars) on silent
                // polls — it only meaningfully changes when a scan/fix completes (nowBusy transitioning from
                // true → false) or when the user takes an action (which triggers a non-silent refresh). Skipping
                // avoids the full-tab flicker while just watching CPU/GPU tick.
                var nowBusy = s.IsScanning || s.IsFixing;
                var justFinished = wasBusy && !nowBusy;
                if (silent && !justFinished) {
                    // UI-18: drives + SMART are live values — bytes free changes with the user's own
                    // activity and SMART samples refresh on their own cadence. Static-content skip meant
                    // the card displayed stale free-space / stale SMART forever until a scan/fix completed.
                    // Render drives surgically here (keeps the mdFade wrapper intact so re-render doesn't
                    // re-trigger the fade-in animation).
                    renderDrivesSurgical(s.Drives || []);
                    return;
                }

                var counts = s.Counts || [];
                var lifetimeCounts = s.LifetimeCounts || [];
                var hero = deps.view.querySelector('#mdHero');
                var lifetime = s.LifetimeBytesReclaimed || 0;
                if (s.TotalPotentialSavings > 0 || counts.length > 0 || lifetime > 0 || (s.RecycleBinBytes || 0) > 0) {
                    // Design: two side-by-side panels, each with its own 4px accent top bar.
                    // Left panel (#3b82f6): "Space you could reclaim" + 104px conic-gradient donut
                    // sized by issue COUNT + count-badge legend.
                    // Right panel (#10b981): "Reclaimed since install" + donut sized by BYTES freed
                    // + mono-byte legend, negligible rows dimmed.
                    function conicDonut(items, sizeBy, panelBg) {
                        if (!items.length) return '';
                        var total = items.reduce(function (a, c) { return a + (sizeBy === 'bytes' ? Math.max(1, c.PotentialSavings || 0) : c.Count); }, 0) || 1;
                        var stops = '';
                        var running = 0;
                        items.forEach(function (c) {
                            var t = deps.STR.types[c.Type] || { color: '#888' };
                            var val = sizeBy === 'bytes' ? Math.max(1, c.PotentialSavings || 0) : c.Count;
                            var start = (running / total * 100).toFixed(2);
                            running += val;
                            var end = (running / total * 100).toFixed(2);
                            if (stops) stops += ', ';
                            stops += t.color + ' ' + start + '% ' + end + '%';
                        });
                        var centerVal = sizeBy === 'bytes' ? deps.bytes(running) : String(items.reduce(function (a, c) { return a + c.Count; }, 0));
                        return '<div style="width:104px;height:104px;border-radius:999px;flex:none;background:conic-gradient(' + stops + ');display:flex;align-items:center;justify-content:center;">' +
                                '<div style="width:66px;height:66px;border-radius:999px;background:' + panelBg + ';display:flex;align-items:center;justify-content:center;font:700 15px \'IBM Plex Mono\', monospace;">' + deps.esc(centerVal) + '</div>' +
                               '</div>';
                    }
                    function reclaimLegend(items) {
                        return items.map(function (c) {
                            var t = deps.STR.types[c.Type] || { title: c.Type, color: '#888' };
                            var val = c.PotentialSavings || 0;
                            // Show reclaimable bytes as the primary metric with the issue count as a
                            // muted suffix — the panel is about space, not issue-tab density.
                            var s = c.Count === 1 ? '' : 's';
                            return '<div style="display:flex;align-items:center;gap:10px;font-size:14px;color:var(--md-body);">' +
                                '<span style="min-width:60px;text-align:right;font:700 12px \'IBM Plex Mono\', monospace;color:' + t.color + ';">' + deps.esc(deps.bytes(val)) + '</span>' +
                                '<span>' + deps.esc(t.title) + ' <span style="opacity:.55;font-size:.85em;">(' + c.Count + ' file' + s + ')</span></span>' +
                            '</div>';
                        }).join('');
                    }
                    function binFooterHtml(s) {
                        var binBytes = s.RecycleBinBytes || 0;
                        var binCount = s.RecycleBinFileCount || 0;
                        var retention = s.RecycleBinRetentionDays || 30;
                        var body = binCount > 0
                            ? '<span style="font-family:\'IBM Plex Mono\', monospace;color:#facc15;">' + deps.esc(deps.bytes(binBytes)) + '</span> · ' +
                              deps.esc(deps.fmt(deps.STR.binHeldNowHint, retention))
                            : deps.esc(deps.STR.binHeldEmpty);
                        return '<div style="margin-top:22px;padding-top:14px;border-top:1px solid rgba(255,255,255,.08);font-size:12.5px;color:var(--md-meta);line-height:1.45;">' +
                            '<div style="font:700 11.5px Figtree,sans-serif;letter-spacing:.13em;text-transform:uppercase;margin-bottom:6px;">' + deps.esc(deps.STR.binHeldNow) + '</div>' +
                            '<div>' + body + '</div>' +
                        '</div>';
                    }
                    function lifetimeLegend(items) {
                        return items.map(function (c) {
                            var t = deps.STR.types[c.Type] || { title: c.Type, color: '#888' };
                            var val = c.PotentialSavings || 0;
                            var dim = val < 1024 * 1024 ? 'opacity:.55;' : '';
                            return '<div style="display:flex;align-items:center;gap:10px;font-size:14px;color:var(--md-body);' + dim + '">' +
                                '<span style="min-width:60px;text-align:right;font:700 12px \'IBM Plex Mono\', monospace;color:' + t.color + ';">' + deps.esc(deps.bytes(val)) + '</span>' +
                                '<span>' + deps.esc(t.title) + '</span>' +
                            '</div>';
                        }).join('');
                    }
                    var panelBg = getComputedStyle(deps.view).getPropertyValue('--md-panel').trim() || '#242b36';
                    // Left panel is exclusively about space reclaim. Filter out types that don't free
                    // any bytes (Ungrouped is a move, MissingSubtitles adds a file, Stale is detect-only,
                    // etc.) — they showed up here with a count but no savings, which was confusing.
                    var reclaimCounts = counts.filter(function (c) { return (c.PotentialSavings || 0) > 0; });
                    var reclaimHtml =
                        '<div class="mdHero mdFade">' +
                            '<div class="mdPanel" style="--md-accent:#3b82f6;">' +
                                '<div class="mdPanelAccent"></div>' +
                                '<div class="mdPanelBody">' +
                                    '<div style="font:700 11.5px Figtree,sans-serif;letter-spacing:.13em;text-transform:uppercase;color:var(--md-meta);">' + deps.esc(deps.STR.totalSavings) + '</div>' +
                                    '<div style="margin:10px 0 22px;font:700 40px \'IBM Plex Mono\', monospace;letter-spacing:-.03em;color:#60a5fa;">' + deps.esc(deps.bytes(s.TotalPotentialSavings)) + '</div>' +
                                    (reclaimCounts.length ? '<div style="display:flex;align-items:center;gap:24px;flex-wrap:wrap;">' + conicDonut(reclaimCounts, 'bytes', panelBg) + '<div style="display:flex;flex-direction:column;gap:11px;">' + reclaimLegend(reclaimCounts) + '</div></div>' : '') +
                                '</div>' +
                            '</div>' +
                            '<div class="mdPanel" style="--md-accent:#10b981;">' +
                                '<div class="mdPanelAccent"></div>' +
                                '<div class="mdPanelBody">' +
                                    '<div style="font:700 11.5px Figtree,sans-serif;letter-spacing:.13em;text-transform:uppercase;color:var(--md-meta);" title="' + deps.esc(deps.STR.lifetimeSavedHint) + '">' + deps.esc(deps.STR.lifetimeSaved) + '</div>' +
                                    '<div style="margin:10px 0 22px;font:700 40px \'IBM Plex Mono\', monospace;letter-spacing:-.03em;color:#34d399;">' + deps.esc(deps.bytes(lifetime)) + '</div>' +
                                    (lifetimeCounts.length ? '<div style="display:flex;align-items:center;gap:24px;flex-wrap:wrap;">' + conicDonut(lifetimeCounts, 'bytes', panelBg) + '<div style="display:flex;flex-direction:column;gap:11px;">' + lifetimeLegend(lifetimeCounts) + '</div></div>' : '') +
                                    binFooterHtml(s) +
                                '</div>' +
                            '</div>' +
                        '</div>';
                    hero.innerHTML = reclaimHtml;
                } else {
                    hero.innerHTML = '';
                }

                // Issue shortcut cards — whole-card link, 4px accent top bar, label + big mono count,
                // task hint, 40px accent-tinted icon disc on the right. Hover lifts the panel.
                var cards = deps.view.querySelector('#mdCards');
                cards.innerHTML = counts.map(function (c) {
                    var t = deps.STR.types[c.Type] || { title: c.Type, hint: '', color: '#888', icon: 'info' };
                    // Show reclaimable space as the big number so users see the "why" at a glance;
                    // keep the raw count as a small subtitle for context. Types without savings info
                    // (Stale, MissingSubtitles) fall back to just the count.
                    var savings = c.PotentialSavings || 0;
                    var bigNumber = savings > 0 ? deps.bytes(savings) : String(c.Count);
                    var subtitle = savings > 0
                        ? c.Count + (c.Count === 1 ? ' file' : ' files')
                        : (c.Count === 1 ? '1 item' : c.Count + ' items');
                    return '<div class="mdShortcut mdFade mdGoIssues" data-type="' + c.Type + '" style="--md-accent:' + t.color + ';" title="Show these issues">' +
                        '<div class="mdPanelAccent"></div>' +
                        '<div class="mdShortcutBody">' +
                            '<div class="mdShortcutText">' +
                                '<div class="mdShortcutLabel">' + deps.esc(t.title) + '</div>' +
                                '<div class="mdShortcutCount">' + deps.esc(bigNumber) + '</div>' +
                                '<div class="mdShortcutHint" style="opacity:.75;font-size:.85em;margin-bottom:.15em;">' + deps.esc(subtitle) + '</div>' +
                                '<div class="mdShortcutHint">' + deps.esc(t.hint || '') + '</div>' +
                            '</div>' +
                            '<div class="mdShortcutDisc"><span class="material-icons">' + deps.esc(t.icon) + '</span></div>' +
                        '</div>' +
                    '</div>';
                }).join('');

                renderDrivesSurgical(s.Drives || []);

                deps.view.querySelectorAll('.mdGoIssues').forEach(function (el) {
                    el.addEventListener('click', function () {
                        deps.view.querySelector('#mdTypeFilter').value = el.getAttribute('data-type');
                        deps.showTab('issues');
                    });
                });

                deps.view.querySelector('#mdEmpty').textContent = counts.length === 0 && !s.IsScanning ? deps.STR.noIssues : '';

                renderRepairSummary(s);
            }


            function activateOverview() {
                // Load the System Performance visibility preference once, in parallel with refresh().
                // If the user has it hidden, the first refresh tick will paint the full card, then
                // this resolves ~10-100ms later and re-renders as the compact chip. Acceptable flash;
                // avoids blocking Overview on a config round-trip.
                deps.api.configuration(deps.PLUGIN_ID).then(function (c) {
                    if (c && c.ShowSystemPerformance === false) {
                        mdSysPerfHidden = true;
                        var el = deps.view.querySelector('#mdSystem');
                        if (el) el.innerHTML = '';
                        if (mdSysPerfLast) renderSystemStats(mdSysPerfLast);
                    }
                    if (c && c.SmartMonitoringEnabled === false) {
                        mdSmartMonitoringEnabled = false;
                        // Force a redraw so the pills flip to the disabled state without waiting
                        // for the next /Status tick (same acceptable-flash trade-off as sysperf).
                        var drivesEl = deps.view.querySelector('#mdDrives');
                        if (drivesEl && mdDrivesLast && mdDrivesLast.length > 0) {
                            drivesEl.innerHTML = '<div class="mdCard mdFade">' +
                                '<div class="mdSecHead"><span class="material-icons">storage</span>' + deps.esc(deps.STR.driveStorage) + '</div>' +
                                '<div class="mdDriveGrid">' +
                                mdDrivesLast.map(driveCardHtml).join('') +
                                '</div></div>';
                        }
                    }
                });

                // Right-click / long-press on the System Performance container toggles visibility.
                // Container-level (not card-level) so the placeholder chip inherits the same menu —
                // otherwise a hidden card has no way to come back.
                var sysEl = deps.view.querySelector('#mdSystem');
                deps.ownGlobal(sysEl, 'contextmenu', function (e) {
                    // Only when the click is actually on our content (card or chip), not empty space
                    // above it if the container is stretched.
                    if (!e.target.closest('.mdCard, .mdSysHiddenChip')) return;
                    e.preventDefault();
                    showSysPerfCtxMenu(e.clientX, e.clientY);
                });
                (function () {
                    var timer = null;
                    var fired = false;
                    deps.ownGlobal(sysEl, 'touchstart', function (e) {
                        if (!e.target.closest('.mdCard, .mdSysHiddenChip')) return;
                        fired = false;
                        var touch = e.touches[0];
                        var x = touch ? touch.clientX : 0;
                        var y = touch ? touch.clientY : 0;
                        timer = setTimeout(function () {
                            fired = true;
                            showSysPerfCtxMenu(x, y);
                        }, 550);
                    }, { passive: true });
                    var cancel = function () { if (timer) { clearTimeout(timer); timer = null; } };
                    deps.lifetime.own(cancel);
                    deps.ownGlobal(sysEl, 'touchend', function (e) {
                        cancel();
                        if (fired) { e.preventDefault(); }
                    });
                    deps.ownGlobal(sysEl, 'touchmove', cancel, { passive: true });
                    deps.ownGlobal(sysEl, 'touchcancel', cancel);
                })();

                // Right-click / long-press on any drive-card SMART pill toggles the whole SMART
                // subsystem via ctx menu. Delegated on #mdDrives (which never leaves the DOM) so we
                // don't have to re-bind on every /Status re-render.
                var drivesRoot = deps.view.querySelector('#mdDrives');
                deps.ownGlobal(drivesRoot, 'contextmenu', function (e) {
                    if (!e.target.closest('.mdSmartPill, .mdDriveExtras')) return;
                    e.preventDefault();
                    showSmartCtxMenu(e.clientX, e.clientY);
                });
                (function () {
                    var timer = null;
                    var fired = false;
                    deps.ownGlobal(drivesRoot, 'touchstart', function (e) {
                        if (!e.target.closest('.mdSmartPill, .mdDriveExtras')) return;
                        fired = false;
                        var touch = e.touches[0];
                        var x = touch ? touch.clientX : 0;
                        var y = touch ? touch.clientY : 0;
                        timer = setTimeout(function () {
                            fired = true;
                            showSmartCtxMenu(x, y);
                        }, 550);
                    }, { passive: true });
                    var cancel = function () { if (timer) { clearTimeout(timer); timer = null; } };
                    deps.lifetime.own(cancel);
                    deps.ownGlobal(drivesRoot, 'touchend', function (e) {
                        cancel();
                        if (fired) { e.preventDefault(); }
                    });
                    deps.ownGlobal(drivesRoot, 'touchmove', cancel, { passive: true });
                    deps.ownGlobal(drivesRoot, 'touchcancel', cancel);
                })();

                // Global dismissal — click elsewhere, press Escape, or scroll to close.
                // Owned by page lifetime so pagehide disposes them and the enclosing
                // initAfterI18n path re-attaches on each pageshow.
                deps.ownGlobal(document, 'click', function () { closeSysPerfCtxMenu(); closeSmartCtxMenu(); });
                deps.ownGlobal(document, 'keydown', function (ev) { if (ev.key === 'Escape') { closeSysPerfCtxMenu(); closeSmartCtxMenu(); } });
                deps.ownGlobal(window, 'scroll', function () { closeSysPerfCtxMenu(); closeSmartCtxMenu(); }, true);
            }
        return {
            get renderLibraryBreakdown() { return renderLibraryBreakdown; },
            get busy() { return busy; },
            get lastStatus() { return lastStatus; },
            get setProgress() { return setProgress; },
            get openFixOrderDialog() { return openFixOrderDialog; },
            get closeFixOrderDialog() { return closeFixOrderDialog; },
            get renderFixOrderList() { return renderFixOrderList; },
            get saveFixOrder() { return saveFixOrder; },
            get renderStatus() { return renderStatus; },
            get activateOverview() { return activateOverview; }
        };
    };
})();
