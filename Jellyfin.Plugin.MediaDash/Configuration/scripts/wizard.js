// wizard behavior and state. Dependencies are supplied by the page controller.
(function () {
    'use strict';
    window.MediaDash.createWizard = function (deps) {


            // ---------- First run wizard ----------

            var WIZ_STEP_LABELS = [
                'Step 1 of 18 — Welcome',
                'Step 2 of 18 — Libraries',
                'Step 3 of 18 — Duplicates',
                'Step 4 of 18 — Broken files',
                'Step 5 of 18 — Oversized files',
                'Step 6 of 18 — Transcode-heavy files',
                'Step 7 of 18 — Trickplay thumbnails',
                'Step 8 of 18 — Languages',
                'Step 9 of 18 — Music & audiobook covers',
                'Step 10 of 18 — Media sorter',
                'Step 11 of 18 — Broken metadata sidecars',
                'Step 12 of 18 — Suspicious executables',
                'Step 13 of 18 — Housekeeping',
                'Step 14 of 18 — Safety',
                'Step 15 of 18 — Stale content',
                'Step 16 of 18 — Format compatibility',
                'Step 17 of 18 — Community stats',
                'Step 18 of 18 — All done'
            ];

            var WIZ_MODE_OPTIONS = [
                ['Off', 'modeOff'],
                ['ManualApprove', 'modeManual'],
                ['Automatic', 'modeAuto']
            ];


            function wizFillSelect(el, options) {
                if (!el || el.options.length) return;
                el.innerHTML = options.map(function (o) {
                    return '<option value="' + o[0] + '">' + deps.esc(o[1]) + '</option>';
                }).join('');
            }


            function wizRenderDots(active, total) {
                var el = deps.view.querySelector('#mdWizDots');
                var html = '';
                for (var i = 0; i < total; i++) {
                    var cls = 'mdWizDot' + (i === active ? ' mdWizDotActive' : (i < active ? ' mdWizDotDone' : ''));
                    html += '<div class="' + cls + '" title="' + deps.esc(WIZ_STEP_LABELS[i]) + '"></div>';
                }
                el.innerHTML = html;
            }


            function wizShow(step) {
                var steps = deps.view.querySelectorAll('#mdWizSteps .mdWizStep');
                var activeStepEl = null;
                for (var i = 0; i < steps.length; i++) {
                    var isActive = parseInt(steps[i].getAttribute('data-step'), 10) === step;
                    steps[i].classList.toggle('mdWizStepActive', isActive);
                    if (isActive) activeStepEl = steps[i];
                }
                var total = WIZ_STEP_LABELS.length;
                deps.view.querySelector('#mdWizStepLabel').textContent = WIZ_STEP_LABELS[step] || '';
                wizRenderDots(step, total);
                // Set the wizard's accent-bar colour from the active step's h3 icon color.
                var wiz = deps.view.querySelector('#mdFirstRun');
                var icon = activeStepEl && activeStepEl.querySelector('h3 .material-icons');
                if (wiz && icon) wiz.style.setProperty('--md-wiz-accent', icon.style.color || '');
                deps.view.querySelector('#mdWizBack').style.visibility = step === 0 ? 'hidden' : '';
                deps.view.querySelector('#mdWizSkip').style.display = step === total - 1 ? 'none' : '';
                var lastStep = step === total - 1;
                var nextBtn = deps.view.querySelector('#mdWizNext');
                nextBtn.classList.toggle('mdWizFinish', lastStep);
                deps.view.querySelector('#mdWizNextLabel').textContent = lastStep ? 'Finish setup' : 'Continue';
                deps.view.querySelector('#mdWizMsg').textContent = '';
                deps.view.querySelector('#mdWizMsg').classList.remove('mdWizErr');
                if (lastStep) {
                    wizRenderSummary();
                }
                if (step === 0) {
                    wizCheckV12Offer();
                }
                if (step === 9) {
                    wizPrefillSortTargets();
                }
            }


            // Ask the server whether the post-Jellyfin-12 cleanup should be offered right now,
            // and toggle the Welcome-step banner accordingly. Runs on every arrival at step 0
            // so completing the sweep during the wizard collapses the notice on next re-entry.
            function wizCheckV12Offer() {
                var note = deps.view.querySelector('#mdWizV12Note');
                if (!note) return;
                deps.api('PostUpgradeCleanup/Status').then(function (r) {
                    note.style.display = (r && r.Available) ? '' : 'none';
                }, function () { note.style.display = 'none'; });
            }


            function wizRenderSummary() {
                var libEls = deps.view.querySelectorAll('#mdWizLibraries .mdWizLib:checked');
                var libCount = libEls.length;
                // UI-09: empty EnabledLibraries is the "all libraries" sentinel at the scanner — the
                // old copy ("scan no libraries") disagreed with execution. Reflect the actual
                // behaviour so the summary never promises something the save won't deliver. The
                // step-1 advance guard separately refuses to proceed when zero libraries are
                // ticked, so this phrasing is only visible on the summary step for a user who
                // ticked every library and reached the summary via back-navigation after unticking.
                var summary = libCount === 0
                    ? 'MediaDash will scan <b>every configured movie / TV library</b>.'
                    : 'MediaDash will scan <b>' + libCount + ' librar' + (libCount === 1 ? 'y' : 'ies') + '</b>.';
                var modes = [
                    ['Duplicates', deps.view.querySelector('#mdWizDupMode').value],
                    ['Broken files', deps.view.querySelector('#mdWizPlayMode').value],
                    ['Oversized', deps.view.querySelector('#mdWizQMode').value],
                    ['Heavy transcodes', deps.view.querySelector('#mdWizHeavyMode').value],
                    ['Failed transcodes', deps.view.querySelector('#mdWizFailedMode').value],
                    ['Trickplay compression', deps.view.querySelector('#mdWizTrickplayMode').value],
                    ['Audio', deps.view.querySelector('#mdWizAudioMode').value],
                    ['Subtitles', deps.view.querySelector('#mdWizSubMode').value],
                    ['Missing subtitles', deps.view.querySelector('#mdWizMissingSubMode').value],
                    ['Duplicated cover art', deps.view.querySelector('#mdWizCoverMode').value],
                    ['Media sorter', deps.view.querySelector('#mdWizSortMode').value],
                    ['Corrupt NFOs', deps.view.querySelector('#mdWizNfoMode').value],
                    ['Suspicious executables', deps.view.querySelector('#mdWizVirusMode').value],
                    ['Orphaned debris', deps.view.querySelector('#mdWizOrphanMode').value],
                    ['Unused subtitle fonts', deps.view.querySelector('#mdWizSubFontMode').value],
                    ['Stale content', deps.view.querySelector('#mdWizStaleEnabled').checked ? 'ManualApprove' : 'Off'],
                    ['Format compatibility', (function () { var r = deps.view.querySelector('input[name="mdWizFormatMode"]:checked'); return r && r.value !== 'Skip' ? 'ManualApprove' : 'Off'; }())]
                ];
                var enabled = modes.filter(function (m) { return m[1] !== 'Off'; }).map(function (m) { return m[0]; });
                var off = modes.filter(function (m) { return m[1] === 'Off'; }).map(function (m) { return m[0]; });
                summary += ' Fix types enabled: <b>' + (enabled.length ? enabled.join(', ') : 'none') + '</b>.';
                if (off.length) summary += ' Off: ' + off.join(', ') + '.';
                if (deps.view.querySelector('#mdWizDryRun').checked) {
                    summary += ' Dry-run is <b>on</b> — nothing on disk changes until you turn it off in Settings → Safety.';
                } else {
                    summary += ' Dry-run is <b>off</b> — approved fixes actually run.';
                }
                deps.view.querySelector('#mdWizSummary').innerHTML = summary;
            }


            // Prefill the media-sorter targets from Jellyfin's own library paths so first-run users
            // don't have to type them. Only touches empty inputs — never overwrites a config-loaded value
            // or something the user typed. Prefers a library the user ticked in step 1 (movies / TV);
            // books and music aren't offered in step 1, so first available library of that type wins.
            function wizPrefillSortTargets() {
                var targets = [
                    { el: deps.view.querySelector('#mdWizMoviesTarget'), types: ['movies'], preferTicked: true },
                    { el: deps.view.querySelector('#mdWizTvTarget'), types: ['tvshows'], preferTicked: true },
                    { el: deps.view.querySelector('#mdWizBooksTarget'), types: ['books'], preferTicked: false },
                    { el: deps.view.querySelector('#mdWizMusicTarget'), types: ['music', 'audiobooks'], preferTicked: false }
                ];
                if (targets.every(function (t) { return !t.el || t.el.value; })) return;
                var tickedIds = Array.prototype.map.call(
                    deps.view.querySelectorAll('#mdWizLibraries .mdWizLib:checked'),
                    function (cb) { return cb.getAttribute('data-id'); });
                deps.api('Libraries').then(function (folders) {
                    var pick = function (types, requireTicked) {
                        for (var i = 0; i < folders.length; i++) {
                            var f = folders[i];
                            if (types.indexOf(f.CollectionType || '') === -1) continue;
                            if (requireTicked && tickedIds.indexOf(f.ItemId) === -1) continue;
                            var loc = (f.Locations || [])[0];
                            if (loc) return loc;
                        }
                        return null;
                    };
                    targets.forEach(function (t) {
                        if (!t.el || t.el.value) return;
                        var v = t.preferTicked ? (pick(t.types, true) || pick(t.types, false)) : pick(t.types, false);
                        if (v) t.el.value = v;
                    });
                });
            }


            function wizLoadLibraries(config) {
                return deps.api('Libraries').then(function (folders) {
                    var el = deps.view.querySelector('#mdWizLibraries');
                    var enabled = config.EnabledLibraries || [];
                    var eligible = folders.filter(function (f) {
                        return !f.CollectionType || f.CollectionType === 'movies' || f.CollectionType === 'tvshows';
                    });
                    if (!eligible.length) {
                        el.innerHTML = '<div class="fieldDescription" style="color:#f59e0b;">No movie or TV libraries are configured yet. Add one in Dashboard → Libraries, then re-open this wizard from Settings → Maintenance.</div>';
                        return;
                    }
                    // Empty enabled = "all" by convention elsewhere; pre-tick everything so the checkboxes reflect that.
                    var effective = enabled.length ? enabled : eligible.map(function (f) { return f.ItemId; });
                    el.innerHTML = eligible.map(function (f) {
                        var checked = effective.indexOf(f.ItemId) >= 0 ? ' checked' : '';
                        var kind = f.CollectionType === 'tvshows' ? 'TV' : (f.CollectionType === 'movies' ? 'Movies' : 'Mixed');
                        return '<div class="checkboxContainer"><label class="emby-checkbox-label">' +
                            '<input type="checkbox" is="emby-checkbox" class="mdWizLib" data-id="' + deps.esc(f.ItemId) + '"' + checked + ' />' +
                            '<span>' + deps.esc(f.Name) + ' <span class="mdBadge" style="background:rgba(0,164,220,.35);font-size:.7em;vertical-align:middle;">' + kind + '</span></span>' +
                            '</label></div>';
                    }).join('');
                });
            }


            function wizPopulateFromConfig(c) {
                // Mode + disposal selects. Populate options once, then set current value.
                deps.view.querySelectorAll('#mdFirstRun [data-mode-for]').forEach(function (sel) {
                    var modeFor = sel.getAttribute('data-mode-for');
                    // Duplicate: Automatic disabled while the confidence-ladder rework stabilises.
                    // Filter the option list AND coerce a legacy Automatic value to ManualApprove.
                    var isDuplicate = modeFor === 'DuplicateFixMode';
                    var opts = WIZ_MODE_OPTIONS.filter(function (o) {
                        return !(isDuplicate && o[0] === 'Automatic');
                    }).map(function (o) { return [o[0], deps.STR[o[1]]]; });
                    wizFillSelect(sel, opts);
                    var current = c[modeFor] || 'ManualApprove';
                    if (current === 'DetectOnly') {
                        // pre-1.0.9 tombstone — see Plugin.NormaliseFixModes
                        current = 'ManualApprove';
                    }

                    if (isDuplicate && current === 'Automatic') {
                        current = 'ManualApprove';
                    }

                    sel.value = current;
                    if (isDuplicate) {
                        sel.title = 'Automatic mode is temporarily disabled while duplicate detection is being reworked and isn\'t reliable enough for hands-off auto-delete.';
                    }
                });
                deps.view.querySelectorAll('#mdFirstRun [data-disp-for]').forEach(function (sel) {
                    wizFillSelect(sel, [['RecycleBin', deps.STR.disposalRecycle], ['Permanent', deps.STR.disposalPermanent]]);
                    sel.value = c[sel.getAttribute('data-disp-for')] || 'RecycleBin';
                });

                deps.view.querySelector('#mdWizDupKeeper').value = deps.keeperPresetFromConfig(c.KeeperPolicyOrder);
                deps.view.querySelector('#mdWizThorough').checked = c.ThoroughPlayabilityCheck !== false;
                deps.view.querySelector('#mdWizMaxRes').value = String(c.MaxResolutionHeight || 1080);
                deps.view.querySelector('#mdWizCodec').value = c.PreferredCodec || 'hevc';
                deps.view.querySelector('#mdWizHwEncode').checked = !!c.UseHardwareEncoder;
                deps.view.querySelector('#mdWizSkipHdr').checked = c.SkipHdrContent !== false;
                wizPopulateGpuOptions(c.PreferredGpuIndex);
                // Default audio-languages-to-keep to the dashboard's own render language so first-run users
                // don't have to know their ISO code. Users can still add more or clear it before saving.
                var audioLangs = (c.AllowedAudioLanguages || []).slice();
                if (audioLangs.length === 0) audioLangs = [deps.localeToIso3()];
                deps.view.querySelector('#mdWizAudioLangs').value = audioLangs.join(', ');
                deps.view.querySelector('#mdWizSubLangs').value = (c.AllowedSubtitleLanguages || []).join(', ');
                // Wire (idempotent) + repaint each wizard chip input so the datalist picker sits between
                // chips instead of the user having to type comma-separated codes into one flat text field.
                // Also inject the +European / +Asian preset row on demand — the wizard's inline HTML
                // doesn't get the row from langChipInputHtml, and the delegated click listener still
                // handles it once the DOM node exists.
                ['mdWizAudioLangs', 'mdWizSubLangs'].forEach(function (id) {
                    var chip = deps.view.querySelector('.mdChipInput[data-chip-input="' + id + '"]');
                    if (chip) {
                        deps.wireChipInput(chip);
                        if (chip._redraw) chip._redraw();
                        var next = chip.nextElementSibling;
                        var alreadyHasPresets = next && next.classList && next.classList.contains('mdLangPresets');
                        if (!alreadyHasPresets) chip.insertAdjacentHTML('afterend', deps.langPresetsHtml(id));
                    }
                });
                deps.wireLangPresets();
                deps.view.querySelector('#mdWizMoviesTarget').value = c.MoviesTargetPath || '';
                deps.view.querySelector('#mdWizTvTarget').value = c.TvTargetPath || '';
                deps.view.querySelector('#mdWizAnimeTarget').value = c.AnimeTargetPath || '';
                deps.view.querySelector('#mdWizMusicTarget').value = c.MusicTargetPath || '';
                deps.view.querySelector('#mdWizBooksTarget').value = c.BooksTargetPath || '';
                deps.view.querySelector('#mdWizSortSource').value = c.MediaSortSource || 'JellyfinMetadata';
                // Format compatibility: pre-select radio from existing config. First run defaults to ManualApprove.
                (function () {
                    var mode = 'ManualApprove';
                    if (c.ConvertEbooks || c.ConvertComics || c.ConvertImages || c.ConvertMusic || c.ConvertVideos) {
                        mode = 'Automatic';
                    }
                    var radio = deps.view.querySelector('input[name="mdWizFormatMode"][value="' + mode + '"]');
                    if (radio) radio.checked = true;
                }());
                deps.view.querySelector('#mdWizDryRun').checked = c.DryRun !== false;
                // Idle-guard checkbox defaults to ON when entering the wizard, regardless of what the
                // user may have saved previously — the walkthrough is the recommended-defaults moment.
                deps.view.querySelector('#mdWizPausePlayback').checked = true;
                // Off by default in the wizard — most Jellyfin installs run headless, so we don't
                // want to slow encodes 10× for users who never had a problem. The label asks
                // "is this machine also used for daily work" so a Yes flips it explicitly.
                deps.view.querySelector('#mdWizLowImpact').checked = !!c.LowSystemImpactMode;
                deps.view.querySelector('#mdWizRetention').value = c.RecycleBinRetentionDays || 30;
                deps.view.querySelector('#mdWizBinPath').value = c.RecycleBinPath || '';
                var staleOn = c.StaleFixMode && c.StaleFixMode !== 'Off';
                deps.view.querySelector('#mdWizStaleEnabled').checked = staleOn;
                // Only surface a persisted threshold when the user has actually opted in. A first-run wizard
                // should present the recommended 365 even if a previous session or test run wrote something
                // shorter — the wizard is where users get the "sane defaults" pitch.
                deps.view.querySelector('#mdWizStaleThreshold').value = (staleOn && c.StaleThresholdDays > 0) ? c.StaleThresholdDays : 365;
                // Pre-tick on first run so the community counts include this install by default;
                // once FirstRunDone we respect whatever the user last saved.
                deps.view.querySelector('#mdWizAnalytics').checked = c.FirstRunDone ? !!c.AnalyticsEnabled : true;
                deps.view.querySelector('#mdWizCoverStrip').checked = c.EmbeddedCoverStripFromAudio !== false;
            }


            function wizPopulateGpuOptions(currentIndex) {
                var sel = deps.view.querySelector('#mdWizGpuIndex');
                // Prefer live status if the dashboard has already polled; otherwise fetch it once here so the
                // dropdown isn't empty when the wizard opens before the first refresh() completes.
                function render(gpus) {
                    var html = '<option value="">Auto (Jellyfin default)</option>';
                    (gpus || []).forEach(function (g) {
                        var label = 'GPU ' + g.Index + ' — ' + g.Name + ' (' + (g.Source || '') + ')';
                        html += '<option value="' + g.Index + '">' + deps.esc(label) + '</option>';
                    });
                    if (currentIndex != null && !(gpus || []).some(function (g) { return String(g.Index) === String(currentIndex); })) {
                        html += '<option value="' + deps.esc(String(currentIndex)) + '">GPU ' + deps.esc(String(currentIndex)) + ' (not currently detected)</option>';
                    }
                    sel.innerHTML = html;
                    sel.value = (currentIndex == null) ? '' : String(currentIndex);
                    deps.view.querySelector('#mdWizGpuIndexRow').style.display = (currentIndex != null || (gpus && gpus.length > 0)) ? '' : 'none';
                }
                var cached = deps.lastStatus && deps.lastStatus.System && deps.lastStatus.System.Gpus;
                if (cached) {
                    render(cached);
                } else {
                    deps.api('Status').then(function (s) {
                        render(s && s.System ? s.System.Gpus : []);
                    }, function () { render([]); });
                }
            }


            function wizApplyStepToConfig(step, c) {
                switch (step) {
                    case 1:
                        c.EnabledLibraries = Array.prototype.map.call(
                            deps.view.querySelectorAll('#mdWizLibraries .mdWizLib:checked'),
                            function (cb) { return cb.getAttribute('data-id'); });
                        break;
                    case 2:
                        c.DuplicateFixMode = deps.view.querySelector('#mdWizDupMode').value;
                        c.DuplicateDisposal = deps.view.querySelector('#mdWizDupDisp').value;
                        c.KeeperPolicyOrder = deps.KEEPER_PRESETS[deps.view.querySelector('#mdWizDupKeeper').value] || deps.KEEPER_PRESETS.quality;
                        break;
                    case 3:
                        c.PlayabilityFixMode = deps.view.querySelector('#mdWizPlayMode').value;
                        c.PlayabilityDisposal = deps.view.querySelector('#mdWizPlayDisp').value;
                        c.ThoroughPlayabilityCheck = deps.view.querySelector('#mdWizThorough').checked;
                        break;
                    case 4:
                        c.TranscodeFixMode = deps.view.querySelector('#mdWizQMode').value;
                        c.TranscodeDisposal = deps.view.querySelector('#mdWizQDisp').value;
                        c.MaxResolutionHeight = parseInt(deps.view.querySelector('#mdWizMaxRes').value, 10);
                        c.PreferredCodec = deps.view.querySelector('#mdWizCodec').value;
                        // Route the wizard's two-widget encoder flow through the shared helper so
                        // unchecking HW encoder clears any stale GPU index — matches the main
                        // Settings dropdown's semantics (see settings-mapper.js encoderValueToConfig).
                        var wizUseHw = deps.view.querySelector('#mdWizHwEncode').checked;
                        var wizGpuVal = deps.view.querySelector('#mdWizGpuIndex').value;
                        var wizEncConf = deps.encoderValueToConfig(wizUseHw ? (wizGpuVal === '' ? 'auto' : wizGpuVal) : 'cpu');
                        c.UseHardwareEncoder = wizEncConf.useHw;
                        c.PreferredGpuIndex = wizEncConf.gpuIndex;
                        c.SkipHdrContent = deps.view.querySelector('#mdWizSkipHdr').checked;
                        break;
                    case 5:
                        c.HeavyTranscodeFixMode = deps.view.querySelector('#mdWizHeavyMode').value;
                        c.HeavyTranscodeDisposal = deps.view.querySelector('#mdWizHeavyDisp').value;
                        c.FailedTranscodeFixMode = deps.view.querySelector('#mdWizFailedMode').value;
                        c.FailedTranscodeDisposal = deps.view.querySelector('#mdWizFailedDisp').value;
                        break;
                    case 6:
                        c.TrickplayFixMode = deps.view.querySelector('#mdWizTrickplayMode').value;
                        break;
                    case 7:
                        c.AllowedAudioLanguages = deps.csv(deps.view.querySelector('#mdWizAudioLangs').value);
                        c.AudioFixMode = deps.view.querySelector('#mdWizAudioMode').value;
                        c.AllowedSubtitleLanguages = deps.csv(deps.view.querySelector('#mdWizSubLangs').value);
                        c.SubtitleFixMode = deps.view.querySelector('#mdWizSubMode').value;
                        c.MissingSubtitlesFixMode = deps.view.querySelector('#mdWizMissingSubMode').value;
                        break;
                    case 8:
                        c.EmbeddedCoverFixMode = deps.view.querySelector('#mdWizCoverMode').value;
                        c.EmbeddedCoverDisposal = deps.view.querySelector('#mdWizCoverDisp').value;
                        c.EmbeddedCoverStripFromAudio = deps.view.querySelector('#mdWizCoverStrip').checked;
                        break;
                    case 9:
                        c.MoviesTargetPath = deps.view.querySelector('#mdWizMoviesTarget').value.trim();
                        c.TvTargetPath = deps.view.querySelector('#mdWizTvTarget').value.trim();
                        c.AnimeTargetPath = deps.view.querySelector('#mdWizAnimeTarget').value.trim();
                        c.MusicTargetPath = deps.view.querySelector('#mdWizMusicTarget').value.trim();
                        c.BooksTargetPath = deps.view.querySelector('#mdWizBooksTarget').value.trim();
                        c.MediaSortSource = deps.view.querySelector('#mdWizSortSource').value;
                        c.MisplacedFixMode = deps.view.querySelector('#mdWizSortMode').value;
                        break;
                    case 10:
                        c.NfoFixMode = deps.view.querySelector('#mdWizNfoMode').value;
                        c.NfoDisposal = deps.view.querySelector('#mdWizNfoDisp').value;
                        break;
                    case 11:
                        c.SuspiciousFileFixMode = deps.view.querySelector('#mdWizVirusMode').value;
                        c.SuspiciousFileDisposal = deps.view.querySelector('#mdWizVirusDisp').value;
                        break;
                    case 12:
                        c.OrphanCleanupFixMode = deps.view.querySelector('#mdWizOrphanMode').value;
                        c.OrphanCleanupDisposal = deps.view.querySelector('#mdWizOrphanDisp').value;
                        c.SubtitleFontFixMode = deps.view.querySelector('#mdWizSubFontMode').value;
                        break;
                    case 13:
                        c.DryRun = deps.view.querySelector('#mdWizDryRun').checked;
                        c.PauseDuringPlayback = deps.view.querySelector('#mdWizPausePlayback').checked;
                        c.LowSystemImpactMode = deps.view.querySelector('#mdWizLowImpact').checked;
                        var days = parseInt(deps.view.querySelector('#mdWizRetention').value, 10);
                        if (!isNaN(days) && days > 0) c.RecycleBinRetentionDays = days;
                        c.RecycleBinPath = deps.view.querySelector('#mdWizBinPath').value.trim();
                        break;
                    case 14:
                        c.StaleFixMode = deps.view.querySelector('#mdWizStaleEnabled').checked ? 'ManualApprove' : 'Off';
                        var staleDays = parseInt(deps.view.querySelector('#mdWizStaleThreshold').value, 10);
                        if (!isNaN(staleDays) && staleDays > 0) c.StaleThresholdDays = staleDays;
                        break;
                    case 15:
                        var formatMode = deps.view.querySelector('input[name="mdWizFormatMode"]:checked');
                        var formatModeVal = formatMode ? formatMode.value : 'ManualApprove';
                        if (formatModeVal === 'Skip') {
                            // Leave FormatCompatibilityFixMode at its current/default value (ManualApprove), all converters off.
                            c.FormatCompatibilityFixMode = 'ManualApprove';
                            c.ConvertEbooks = false;
                            c.ConvertComics = false;
                            c.ConvertImages = false;
                            c.ConvertMusic = false;
                            c.ConvertVideos = false;
                        } else if (formatModeVal === 'Automatic') {
                            c.FormatCompatibilityFixMode = 'Automatic';
                            c.ConvertEbooks = true;
                            c.ConvertComics = true;
                            c.ConvertImages = true;
                            c.ConvertMusic = true;
                            c.ConvertVideos = true;
                            // ponytail: ConvertAudiobooks deliberately left off — audiobook merge is opt-in only (bigger UX surprise)
                        } else {
                            c.FormatCompatibilityFixMode = 'ManualApprove';
                            c.ConvertEbooks = false;
                            c.ConvertComics = false;
                            c.ConvertImages = false;
                            c.ConvertMusic = false;
                            c.ConvertVideos = false;
                        }
                        break;
                    case 16:
                        applyAnalyticsToggle(c, deps.view.querySelector('#mdWizAnalytics').checked);
                        break;
                    default:
                        break;
                }
            }


            // Applies the analytics opt-in toggle to the config object. The install identity is no
            // longer stored — the server derives a month-rotated hash of Jellyfin's SystemId at
            // report time (see AnalyticsReporter.ComputeMonthlyInstallId). We still clear any
            // legacy AnalyticsInstallId that pre-1.0.6 configs might carry so opt-out really
            // removes every trace from disk.
            function applyAnalyticsToggle(c, enabled) {
                c.AnalyticsEnabled = !!enabled;
                if (c.AnalyticsInstallId) {
                    c.AnalyticsInstallId = '';
                }
            }



            var current = 0;
            var saving = false;

            function reopen() {
                saving = false;
                return Promise.all([deps.api.configuration(deps.PLUGIN_ID), deps.populateLanguageDatalist()]).then(function (r) {
                    wizPopulateFromConfig(r[0]);
                    return wizLoadLibraries(r[0]).then(function () {
                        current = 0;
                        wizShow(0);
                    });
                });
            }

            function setupFirstRun() {
                var total = WIZ_STEP_LABELS.length;

                function saveThen(step, next) {
                    if (saving) return;
                    var msg = deps.view.querySelector('#mdWizMsg');
                    msg.classList.remove('mdWizErr');
                    // UI-09: refuse to advance past the library step when the user has deselected
                    // every library. Empty EnabledLibraries is the "all libraries" sentinel at the
                    // scanner — a user who explicitly unticked everything expected the opposite.
                    // Make them pick at least one, or re-tick the ones they want to opt out of.
                    if (step === 1) {
                        var ticked = deps.view.querySelectorAll('#mdWizLibraries .mdWizLib:checked').length;
                        var available = deps.view.querySelectorAll('#mdWizLibraries .mdWizLib').length;
                        if (available > 0 && ticked === 0) {
                            msg.textContent = 'Pick at least one library — leaving every box unticked would otherwise scan everything.';
                            msg.classList.add('mdWizErr');
                            return;
                        }
                    }
                    saving = true;
                    msg.textContent = 'Saving…';
                    deps.api.configuration(deps.PLUGIN_ID).then(function (c) {
                        var before = Object.assign({}, c);
                        wizApplyStepToConfig(step, c);
                        var keys = Object.keys(c);
                        if (keys.some(function (key) { return /FixMode$/.test(key) && c[key] === 'Automatic' && before[key] !== 'Automatic'; }) && !window.confirm(deps.STR.confirmAuto)) { msg.textContent = ''; return; }
                        if (keys.some(function (key) { return /Disposal$/.test(key) && c[key] === 'Permanent' && before[key] !== 'Permanent'; }) && !window.confirm(deps.STR.confirmPermanent)) { msg.textContent = ''; return; }
                        if (step === total - 1) {
                            c.FirstRunDone = true;
                        }

                        // Recycle bin free-space gate on the Safety-net step: won't advance until the
                        // chosen (or default) bin volume has >=5 GB free. Same rule as the Settings-tab
                        // save, minus the "Go to Recycle bin card" button — the wizard's own input is
                        // right there on the step.
                        var proceed = (step === 13) ? gateOnBinDiskInfo(c) : Promise.resolve(true);
                        return proceed.then(function (ok) {
                            if (!ok) {
                                msg.textContent = '';
                                return;
                            }

                            return ApiClient.updatePluginConfiguration(deps.PLUGIN_ID, c).then(function (result) {
                                Dashboard.processPluginConfigurationUpdateResult(result);
                                msg.textContent = '';
                                next(c);
                            }, function () {
                                msg.textContent = 'Could not save — try again.';
                                msg.classList.add('mdWizErr');
                            });
                        });
                    }).catch(function () {
                        msg.textContent = 'Could not save — try again.';
                        msg.classList.add('mdWizErr');
                    }).finally(function () { saving = false; });
                }

                function gateOnBinDiskInfo(c) {
                    var warnEl = deps.view.querySelector('#mdWizBinDiskWarn');
                    if (warnEl) warnEl.style.display = 'none';
                    var binPath = (c.RecycleBinPath || '').trim();
                    var url = 'RecycleBin/DiskInfo';
                    if (binPath) url += '?path=' + encodeURIComponent(binPath);
                    return deps.api(url).then(function (info) {
                        if (info && info.MeetsFiveGbMinimum === false) {
                            renderWizBinWarn(info);
                            return false;
                        }

                        return true;
                    }, function () {
                        // Endpoint failure = allow through; the FixTask floor still enforces safety.
                        return true;
                    });
                }

                function renderWizBinWarn(info) {
                    var warnEl = deps.view.querySelector('#mdWizBinDiskWarn');
                    if (!warnEl) return;
                    var freeGb = Math.round((info.FreeBytes || 0) / (1024 * 1024 * 1024));
                    warnEl.innerHTML =
                        '<div style="margin-bottom:.5em;"><strong>This location isn’t safe:</strong> ' +
                        deps.esc(info.PathProbed || '(default location)') + ' has only ' + freeGb + ' GB free.</div>' +
                        '<div>Jellyfin needs 2 GB of free space to run so MediaDash requires at least 5 GB before it will use a folder for the recycle bin. Enter a different path above (a folder on your media drive works best) and try again.</div>';
                    warnEl.style.display = '';
                    var pathInput = deps.view.querySelector('#mdWizBinPath');
                    if (pathInput) {
                        deps.lifetime.timeout(function () { pathInput.focus(); }, 100);
                    }
                }

                // Await the cultures list so localeToIso3() has real data when picking the default audio language.
                reopen().catch(function () { deps.view.querySelector('#mdWizMsg').textContent = 'Could not load setup — reopen it to retry.'; });

                deps.loadEnv().then(function (env) {
                    deps.applySubtitleProviderHint(deps.view.querySelector('#mdWizProviderHint'), env);
                });

                deps.view.querySelector('#mdWizNext').addEventListener('click', function () {
                    saveThen(current, function (c) {
                        if (current >= total - 1) {
                            // Finished. Hide wizard, refresh dashboards, optionally kick off scan.
                            deps.view.querySelector('#mdFirstRun').style.display = 'none';
                            deps.view.classList.remove('mdWizardActive');
                            deps.loadSettings();
                            deps.refresh();
                            if (deps.view.querySelector('#mdWizScanNow').checked) {
                                deps.post('Scan').then(function () { deps.refresh(); });
                            }
                        } else {
                            current++;
                            wizShow(current);
                        }
                    });
                });

                deps.view.querySelector('#mdWizBack').addEventListener('click', function () {
                    if (saving || current === 0) return;
                    current--;
                    wizShow(current);
                });

                deps.view.querySelector('#mdWizV12Run').addEventListener('click', function () {
                    var btn = this;
                    var resultEl = deps.view.querySelector('#mdWizV12Result');
                    btn.disabled = true;
                    resultEl.style.display = '';
                    resultEl.textContent = 'Running sweep…';
                    deps.postJson('PostUpgradeCleanup/Run').then(function (r) {
                        var freed = (r && r.BytesFreed) || 0;
                        var count = (r && r.OrphanedFoldersDeleted) || 0;
                        resultEl.textContent = (r && r.WasDryRun) ? 'Dry-run preview: ' + count + ' orphaned folders could be removed. No files were deleted.' : 'Cleanup done. Reclaimed ' + deps.bytes(freed) + ' from ' + count + ' orphaned trickplay folder' + (count === 1 ? '' : 's') + '.';
                        deps.lifetime.timeout(function () {
                            deps.view.querySelector('#mdWizV12Note').style.display = 'none';
                        }, 3500);
                    }, function (err) {
                        resultEl.style.color = '#ef4444';
                        resultEl.textContent = 'Cleanup failed: ' + ((err && err.statusText) || 'unknown error');
                        btn.disabled = false;
                    });
                });

                deps.view.querySelector('#mdWizSkip').addEventListener('click', function () {
                    if (saving) return;
                    // "Skip" leaves everything else alone but marks the wizard done so it stops nagging.
                    // Everything is reachable from Settings — this is the "I know what I'm doing" exit.
                    deps.api.configuration(deps.PLUGIN_ID).then(function (c) {
                        c.FirstRunDone = true;
                        // Analytics defaults on. No install ID to mint any more — the server hashes
                        // Jellyfin's SystemId with the current year-month at report time.
                        applyAnalyticsToggle(c, !!c.AnalyticsEnabled);
                        ApiClient.updatePluginConfiguration(deps.PLUGIN_ID, c).then(function (result) {
                            Dashboard.processPluginConfigurationUpdateResult(result);
                            deps.view.querySelector('#mdFirstRun').style.display = 'none';
                            deps.view.classList.remove('mdWizardActive');
                            deps.loadSettings();
                            deps.refresh();
                        });
                    });
                });

                deps.view.querySelector('#mdWizCheckAccess').addEventListener('click', function () {
                    var btn = this;
                    var resultsEl = deps.view.querySelector('#mdWizAccessResults');
                    btn.disabled = true;
                    resultsEl.style.display = 'block';
                    resultsEl.innerHTML = '<div class="fieldDescription">Probing each library folder…</div>';
                    deps.api('LibraryAccessCheck').then(function (results) {
                        btn.disabled = false;
                        if (!results.length) {
                            resultsEl.innerHTML = '<div class="fieldDescription" style="color:#f59e0b;">No libraries are configured yet. Add a movie or TV library in Dashboard → Libraries first.</div>';
                            return;
                        }
                        var html = '';
                        results.forEach(function (r) {
                            var ok = r.CanRead && r.CanWrite;
                            var icon = ok ? '<span class="material-icons" style="color:#22c55e;font-size:1.1em;vertical-align:-3px;">check_circle</span>'
                                          : '<span class="material-icons" style="color:#ef4444;font-size:1.1em;vertical-align:-3px;">error</span>';
                            html += '<div style="padding:.35em .5em;border-left:3px solid ' + (ok ? '#22c55e' : '#ef4444') + ';background:rgba(255,255,255,.03);margin-bottom:.3em;border-radius:4px;">' +
                                icon + ' <strong>' + deps.esc(r.Name) + '</strong> ' +
                                '<span style="font-family:monospace;color:rgba(255,255,255,.6);">' + deps.esc(r.Path) + '</span>' +
                                (ok ? '' : '<div class="fieldDescription" style="margin:.25em 0 0;color:#fca5a5;">' + deps.esc(r.Error || 'Access denied') + '</div>') +
                                '</div>';
                        });
                        var failed = results.filter(function (r) { return !r.CanRead || !r.CanWrite; });
                        if (failed.length > 0) {
                            html = '<div style="color:#ef4444;font-weight:600;margin-bottom:.4em;">' + failed.length + ' of ' + results.length + ' libraries are not fully accessible — fixes for files in those libraries will fail.</div>' + html;
                        } else {
                            html = '<div style="color:#22c55e;font-weight:600;margin-bottom:.4em;">All ' + results.length + ' libraries are readable and writable. You\'re good to go.</div>' + html;
                        }
                        resultsEl.innerHTML = html;
                    }, function () {
                        btn.disabled = false;
                        resultsEl.innerHTML = '<div class="fieldDescription" style="color:#ef4444;">Access check failed to run. Check the Errors tab.</div>';
                    });
                });

                // Same probe as the wizard button but on the Settings → Libraries card so users can
                // re-check without walking back through the wizard. Results render inline next to each
                // library name (grouped by name — a library with multiple Locations aggregates to the
                // worst per-Location outcome).
                var libAccessBtn = deps.view.querySelector('#mdLibAccessTest');
                if (libAccessBtn) {
                    libAccessBtn.addEventListener('click', function () {
                        var btn = this;
                        var summaryEl = deps.view.querySelector('#mdLibAccessSummary');
                        btn.disabled = true;
                        summaryEl.innerHTML = '<span style="opacity:.7;">Probing each library folder…</span>';
                        deps.view.querySelectorAll('.mdLibAccessSlot').forEach(function (slot) {
                            slot.innerHTML = '<span style="opacity:.55;">testing…</span>';
                        });
                        deps.api('LibraryAccessCheck').then(function (results) {
                            btn.disabled = false;
                            if (!results.length) {
                                summaryEl.innerHTML = '<span style="color:#f59e0b;">No libraries are configured yet — add one in Dashboard → Libraries first.</span>';
                                return;
                            }

                            // Group by library name; a library with N Locations gets N result rows.
                            var byLib = {};
                            results.forEach(function (r) {
                                if (!byLib[r.Name]) byLib[r.Name] = [];
                                byLib[r.Name].push(r);
                            });

                            deps.view.querySelectorAll('.mdLibAccessSlot').forEach(function (slot) {
                                var libName = slot.getAttribute('data-lib');
                                var group = byLib[libName];
                                if (!group) { slot.innerHTML = ''; return; }
                                var failures = group.filter(function (r) { return !r.CanRead || !r.CanWrite; });
                                if (failures.length === 0) {
                                    slot.innerHTML = '<span style="color:#22c55e;display:inline-flex;align-items:center;gap:.25em;" title="Jellyfin can read and write this library">' +
                                        '<span class="material-icons" style="font-size:1.05em;">check_circle</span>readable + writable</span>';
                                } else {
                                    var first = failures[0];
                                    var tipText = (first.Error || 'Access denied') + (failures.length > 1 ? ' (+' + (failures.length - 1) + ' more location' + (failures.length > 2 ? 's' : '') + ')' : '');
                                    slot.innerHTML = '<span style="color:#ef4444;display:inline-flex;align-items:center;gap:.25em;" title="' + deps.esc(tipText) + '">' +
                                        '<span class="material-icons" style="font-size:1.05em;">error</span>' +
                                        (failures.length === group.length ? 'no access' : failures.length + ' of ' + group.length + ' failed') +
                                        '</span>';
                                }
                            });

                            var totalFailed = results.filter(function (r) { return !r.CanRead || !r.CanWrite; }).length;
                            summaryEl.innerHTML = totalFailed > 0
                                ? '<span style="color:#ef4444;">' + totalFailed + ' of ' + results.length + ' locations not fully accessible — hover an ✗ for details.</span>'
                                : '<span style="color:#22c55e;">All ' + results.length + ' locations readable and writable.</span>';
                        }, function () {
                            btn.disabled = false;
                            summaryEl.innerHTML = '<span style="color:#ef4444;">Access check failed to run.</span>';
                            deps.view.querySelectorAll('.mdLibAccessSlot').forEach(function (slot) { slot.innerHTML = ''; });
                        });
                    });
                }

                // Recycle-bin probe — creates the folder if missing so a user who just changed the path
                // can verify Jellyfin can read/write before their first fix run tries to move a file into it.
                var binAccessBtn = deps.view.querySelector('#mdBinAccessTest');
                if (binAccessBtn) {
                    binAccessBtn.addEventListener('click', function () {
                        var btn = this;
                        var resultsEl = deps.view.querySelector('#mdBinAccessResults');
                        btn.disabled = true;
                        resultsEl.innerHTML = '<span style="opacity:.7;">Probing recycle bin path…</span>';
                        deps.api('RecycleBinAccessCheck').then(function (r) {
                            btn.disabled = false;
                            var ok = r.CanRead && r.CanWrite;
                            var color = ok ? '#22c55e' : '#ef4444';
                            var icon = ok ? 'check_circle' : 'error';
                            resultsEl.innerHTML = '<div style="padding:.3em .5em;border-left:3px solid ' + color + ';background:rgba(255,255,255,.03);border-radius:4px;">' +
                                '<span class="material-icons" style="color:' + color + ';font-size:1.05em;vertical-align:-3px;">' + icon + '</span> ' +
                                '<strong>' + (ok ? 'Recycle bin is readable and writable' : 'Recycle bin access problem') + '</strong> ' +
                                '<span style="font-family:monospace;opacity:.6;">' + deps.esc(r.Path || '') + '</span>' +
                                (ok ? '' : '<div class="fieldDescription" style="margin:.2em 0 0;color:#fca5a5;">' + deps.esc(r.Error || 'Access denied') + '</div>') +
                                '</div>';
                        }, function () {
                            btn.disabled = false;
                            resultsEl.innerHTML = '<span style="color:#ef4444;">Access check failed to run.</span>';
                        });
                    });
                }
            }
        return {
            get WIZ_STEP_LABELS() { return WIZ_STEP_LABELS; },
            get applyAnalyticsToggle() { return applyAnalyticsToggle; },
            get setupFirstRun() { return setupFirstRun; },
            reopen: reopen
        };
    };
})();
