// settings behavior and state. Dependencies are supplied by the page controller.
(function () {
    'use strict';
    window.MediaDash.createSettingsTab = function (deps) {


            // Race gate for the Settings Save path. loadSettings' `.then(function (c) { ... })`
            // hydrates every checkbox / select from the server-side config. If the user hits
            // Save before that callback runs — Jellyfin's Settings render is instant but the
            // config fetch is a real HTTP round-trip — every checkbox whose HTML default is
            // unchecked overwrites the server-side value as `false`. For DryRun (C# default
            // `true`), silent-off means the NEXT fix run really deletes instead of dry-logging.
            // Same shape as the 1.0.7.6 Repair checkboxes bug; the audit-round-3 findings named
            // 5 additional checkboxes with the identical race. Root-cause fix here is a single
            // load-completion gate — belt-and-braces `checked` attributes on the specific
            // inputs are added below but don't scale to future settings; this gate does.
            var mdConfigLoaded = false;


            // Field report D2 default order — mirrors FixTask.DefaultFixerRank on the server so
            // the "Reset to default" button in the Fix-order dialog restores exactly what the server
            // would use with FixerOrder = []. Update both when a new IssueType is added.
            var DEFAULT_FIXER_ORDER = [
                'MalwareRisk', 'Playability', 'Duplicate', 'OrphanedDebris', 'Misplaced', 'Ungrouped',
                'MissingSubtitles', 'SubtitleLanguage', 'AudioLanguage', 'Quality',
                'HeavyTranscode', 'FailedTranscode', 'SubtitleFonts', 'EmbeddedCoverArt',
                'CorruptNfo', 'CorruptArtwork', 'Stale', 'LargeTrickplay', 'FormatCompatibility'
            ];


            // Hard constraints the D2 default preserves. If a user reorders past one of these we
            // soft-warn but don't block the save — spec explicitly says "keeps user control".
            var FIXER_HARD_CONSTRAINTS = [
                ['Duplicate', 'Quality', "Don't rebuild a file that's about to be deleted."],
                ['Duplicate', 'SubtitleLanguage', "Don't rebuild a file that's about to be deleted."],
                ['Duplicate', 'AudioLanguage', "Don't rebuild a file that's about to be deleted."],
                ['Duplicate', 'HeavyTranscode', "Don't rebuild a file that's about to be deleted."],
                ['Playability', 'Quality', "Don't re-encode a file we're about to remove as broken."],
                ['MissingSubtitles', 'Quality', "Subtitles must land before the encode or they're orphaned by the rename."],
                ['MissingSubtitles', 'HeavyTranscode', "Subtitles must land before the encode or they're orphaned by the rename."],
                ['OrphanedDebris', 'Misplaced', "Sweep orphans before moving files or the mover creates new ones."],
                ['Quality', 'LargeTrickplay', "Trickplay BIF must match the final video, so it goes last."],
                ['HeavyTranscode', 'LargeTrickplay', "Trickplay BIF must match the final video, so it goes last."]
            ];


            var FIX_TYPES = [
                { key: 'Duplicate', mode: 'DuplicateFixMode', disposal: 'DuplicateDisposal' },
                { key: 'Playability', mode: 'PlayabilityFixMode', disposal: 'PlayabilityDisposal' },
                { key: 'Quality', mode: 'TranscodeFixMode', disposal: 'TranscodeDisposal' },
                { key: 'SubtitleLanguage', mode: 'SubtitleFixMode', disposal: 'SubtitleDisposal' },
                { key: 'AudioLanguage', mode: 'AudioFixMode', disposal: 'AudioDisposal' },
                // Misplaced fixes move files rather than delete them, so there's no disposal choice.
                { key: 'Misplaced', mode: 'MisplacedFixMode' },
                // Missing-subtitle fixes add a file rather than remove one; no disposal choice.
                { key: 'MissingSubtitles', mode: 'MissingSubtitlesFixMode' },
                // Stale content: fixer deletes the unwatched file per-row. Automatic is intentionally
                // suppressed (modeSegments autoSuppressed flag; NormaliseFixModes clamps server-side)
                // so a mis-set threshold can't batch-delete someone's library. Off + Ask me first only.
                { key: 'Stale', mode: 'StaleFixMode', disposal: 'StaleDisposal' },
                { key: 'MalwareRisk', mode: 'SuspiciousFileFixMode', disposal: 'SuspiciousFileDisposal' },
                // Trickplay conversion rewrites in place — no disposal choice, and no separate disposal
                // config on the C# side. Just a fix mode + a quality slider.
                { key: 'LargeTrickplay', mode: 'TrickplayFixMode' },
                // Subtitle font optimizer rewrites .ass sidecars in place; no disposal choice.
                { key: 'SubtitleFonts', mode: 'SubtitleFontFixMode' },
                // Orphan cleanup deletes debris; respects recycle-vs-permanent choice like Duplicates.
                { key: 'OrphanedDebris', mode: 'OrphanCleanupFixMode', disposal: 'OrphanCleanupDisposal' },
                // NFO integrity: delete corrupt sidecars, respect recycle vs. permanent choice.
                { key: 'CorruptNfo', mode: 'NfoFixMode', disposal: 'NfoDisposal' },
                // Both transcode-log tasks fix by re-encoding — same disposal semantics as Quality.
                { key: 'HeavyTranscode', mode: 'HeavyTranscodeFixMode', disposal: 'HeavyTranscodeDisposal' },
                { key: 'FailedTranscode', mode: 'FailedTranscodeFixMode', disposal: 'FailedTranscodeDisposal' },
                // Embedded cover art: disposal only kicks in when the strip-toggle is on (originals recycled first).
                { key: 'EmbeddedCoverArt', mode: 'EmbeddedCoverFixMode', disposal: 'EmbeddedCoverDisposal' },
                // Media grouper moves files into per-title folders — no disposal, only a mode.
                { key: 'Ungrouped', mode: 'UngroupedFixMode' },
                // Corrupt artwork just deletes broken files so Jellyfin re-fetches on next scan; no
                // disposal choice — the file is safe to delete outright because it lives under Jellyfin's
                // own metadata folder, not user-placed media.
                { key: 'CorruptArtwork', mode: 'CorruptArtworkFixMode' },
                // Format conversion: converts ebooks/comics to Jellyfin-compatible formats; originals
                // go to the recycle bin so the conversion is reversible.
                { key: 'FormatCompatibility', mode: 'FormatCompatibilityFixMode', disposal: 'FormatCompatibilityDisposal' }
            ];


            // A curated dropdown of families we can safely offer for "force one font on every subtitle".
            // Web-safe + widely-shipped Google fonts. Empty value = "keep original per-style fonts".
            // `stack` is the CSS font-family stack rendered inline on the <option> so each label previews
            // in its own face (Firefox honours <option style>; recent Chromium honours it in the popup —
            // fine even when it doesn't since users typically read the label AND pick).
            var SUBTITLE_FONTS = [
                { v: '',                 l: 'Keep original fonts', stack: 'inherit' },
                { v: 'Arial',            l: 'Arial',               stack: "Arial, sans-serif" },
                { v: 'Verdana',          l: 'Verdana',             stack: "Verdana, sans-serif" },
                { v: 'Tahoma',           l: 'Tahoma',              stack: "Tahoma, sans-serif" },
                { v: 'Trebuchet MS',     l: 'Trebuchet MS',        stack: "'Trebuchet MS', sans-serif" },
                { v: 'Georgia',          l: 'Georgia',             stack: "Georgia, serif" },
                { v: 'Times New Roman',  l: 'Times New Roman',     stack: "'Times New Roman', serif" },
                { v: 'Courier New',      l: 'Courier New',         stack: "'Courier New', monospace" },
                { v: 'Roboto',           l: 'Roboto',              stack: "'Roboto', sans-serif" },
                { v: 'Open Sans',        l: 'Open Sans',           stack: "'Open Sans', sans-serif" },
                { v: 'Noto Sans',        l: 'Noto Sans',           stack: "'Noto Sans', sans-serif" }
            ];


            var KEEPER_PRESETS = {
                quality: ['Resolution', 'Codec', 'Bitrate', 'Size'],
                codec: ['Codec', 'Resolution', 'Bitrate', 'Size'],
                size: ['Size']
            };


            // Bitrate is stored as its 1080p equivalent; the slider displays it scaled to the selected max resolution.
            var RES_PIXEL_FACTOR = { 360: 0.1111, 480: 0.1975, 720: 0.4444, 1080: 1, 1440: 1.7778, 2160: 4, 4320: 16 };


            var currentBitrateFactor = 1;
            var hydratedBitrateBase = null;
            var bitrateSliderEdited = false;


            function bitrateFactor() {
                return RES_PIXEL_FACTOR[parseInt(deps.view.querySelector('#mdMaxRes').value, 10)] || 1;
            }


            function setBitrateSliderForResolution(base1080) {
                hydratedBitrateBase = base1080;
                bitrateSliderEdited = false;
                currentBitrateFactor = bitrateFactor();
                var slider = deps.view.querySelector('#mdMaxBitrate');
                slider.max = Math.ceil(40 * currentBitrateFactor);
                slider.value = Math.round(base1080 * currentBitrateFactor * 2) / 2;
                var res = deps.view.querySelector('#mdMaxRes').value;
                var resLabel = res === '4320' ? '8K' : res === '2160' ? '4K' : res + 'p';
                // Target the inner .mdBitrateLabelText span, not the whole <label>, so we don't
                // wipe out the sibling #mdMaxBitrateVal span when updating the resolution label.
                var labelText = deps.view.querySelector('#mdMaxBitrateLabel .mdBitrateLabelText');
                if (labelText) labelText.textContent = 'Maximum video bitrate at ' + resLabel;
                deps.view.querySelector('#mdMaxBitrateVal').textContent = deps.fmt(deps.STR.unitMbps, slider.value);
            }


            function bitrateBaseFromSlider() {
                if (!bitrateSliderEdited && hydratedBitrateBase != null) return hydratedBitrateBase;
                // Divide by the factor the slider was DISPLAYED with, not the freshly selected resolution's factor.
                return Math.round((parseFloat(deps.view.querySelector('#mdMaxBitrate').value) / currentBitrateFactor) * 10) / 10 || 8;
            }


            var SLIDERS = [
                { id: 'mdMaxBitrate', val: 'mdMaxBitrateVal', unit: 'unitMbps' },
                { id: 'mdTolerance', val: 'mdToleranceVal', unit: 'unitPercent' },
                { id: 'mdRetention', val: 'mdRetentionVal', unit: 'unitDays' },
                { id: 'mdTrickplayQuality', val: 'mdTrickplayQualityVal', unit: 'unitPercent' },
                { id: 'mdTrickplayMinSize', val: 'mdTrickplayMinSizeVal', format: function (v) { return v === '0' || v === 0 ? ' (flag every folder)' : ' ' + v + ' MB'; } },
                { id: 'mdBinWarnThreshold', val: 'mdBinWarnThresholdVal', format: function (v) { return v === '0' || v === 0 ? ' (off)' : ' ' + v + ' GB'; } },
                { id: 'mdBinPauseAt', val: 'mdBinPauseAtVal', format: function (v) { return v === '0' || v === 0 ? ' (off)' : ' ' + v + ' GB'; } },
                { id: 'mdLowSpaceThreshold', val: 'mdLowSpaceThresholdVal', format: function (v) { return v === '0' || v === 0 ? ' (off)' : ' ' + v + ' % used'; } }
            ];


            // ---------- Settings ----------

            function modeOptions() {
                return '<option value="Off">' + deps.esc(deps.STR.modeOff) + '</option>' +
                    '<option value="ManualApprove">' + deps.esc(deps.STR.modeManual) + '</option>' +
                    '<option value="Automatic">' + deps.esc(deps.STR.modeAuto) + '</option>';
            }


            function disposalOptions(typeKey) {
                var t = deps.STR.types[typeKey] || {};
                return '<option value="RecycleBin">' + deps.esc(t.doRecycle || deps.STR.disposalRecycle) + '</option>' +
                    '<option value="Permanent">' + deps.esc(t.doPermanent || deps.STR.disposalPermanent) + '</option>';
            }


            // ─── Task-card system (spec 1a) ────────────────────────────────
            // Every scanner renders as a self-contained task card. Card body holds the settings that
            // only affect that scanner. The mode picker is a segmented control writing to a hidden
            // input #mdMode<Key>; the disposal select stays as #mdDisposal<Key>. All other field ids
            // are preserved so the existing loadSettings/saveSettings paths keep working unchanged.

            // SubtitleFonts is intentionally excluded — its controls are folded into the SubtitleLanguage
            // card's body (font optimizer mode + force-font dropdown at lines ~5294-5315). Putting
            // SubtitleFonts here would render a second empty "Subtitles" card since no key === 'SubtitleFonts'
            // branch exists in buildTaskCard.
            var TASK_ORDER = ['Duplicate', 'Playability', 'Quality', 'HeavyTranscode', 'FailedTranscode', 'SubtitleLanguage', 'AudioLanguage', 'Stale', 'Misplaced', 'Ungrouped', 'MissingSubtitles', 'MalwareRisk', 'LargeTrickplay', 'EmbeddedCoverArt', 'OrphanedDebris', 'CorruptNfo', 'CorruptArtwork', 'FormatCompatibility'];


            // Ink used for the selected-accent segment. Derived by hand from the mock — near-black
            // shifted toward the accent's hue so the label reads without vibrating.
            var TASK_INKS = {
                Duplicate: '#1a1204',
                Playability: '#1f0505',
                Quality: '#04101f',
                SubtitleLanguage: '#100521',
                AudioLanguage: '#04150f',
                Misplaced: '#1a0d02',
                MissingSubtitles: '#0e0521',
                Stale: '#0b0f16',
                MalwareRisk: '#1f0303',
                LargeTrickplay: '#03151a',
                OrphanedDebris: '#141311',
                CorruptNfo: '#1a0410',
                HeavyTranscode: '#1a0d02',
                FailedTranscode: '#1f0303',
                EmbeddedCoverArt: '#150520',
                FormatCompatibility: '#120421'
            };


            // Border tint applied when the task is On. The CSS also tries a relative-color-syntax
            // fallback for browsers that support it; this is the belt-and-braces value used everywhere.
            var TASK_ON_BORDER = {
                Duplicate: 'rgba(245,158,11,.28)',
                Playability: 'rgba(239,68,68,.28)',
                Quality: 'rgba(59,130,246,.3)',
                SubtitleLanguage: 'rgba(139,92,246,.28)',
                AudioLanguage: 'rgba(16,185,129,.28)',
                Misplaced: 'rgba(249,115,22,.28)',
                MissingSubtitles: 'rgba(167,139,250,.28)',
                Stale: 'rgba(100,116,139,.28)',
                MalwareRisk: 'rgba(220,38,38,.32)',
                LargeTrickplay: 'rgba(20,184,166,.28)',
                OrphanedDebris: 'rgba(120,113,108,.32)',
                CorruptNfo: 'rgba(225,29,72,.28)',
                HeavyTranscode: 'rgba(249,115,22,.28)',
                FailedTranscode: 'rgba(220,38,38,.32)',
                EmbeddedCoverArt: 'rgba(168,85,247,.28)',
                FormatCompatibility: 'rgba(124,58,237,.28)'
            };


            // Explanation string shown as a hover tooltip on each scanner-card title. The one-line
            // meta.hint is the *summary* under the title; this is the "what does this scanner
            // actually do?" tooltip for users who need more context before picking a mode
            // (Automatic / Ask me first / Off). Keep each entry to 2-3 sentences: what the scanner
            // detects, then what the fixer does with matches. English-only for now — i18n can catch
            // up when the strings settle. Roadmap item 42e1a420.
            var TASK_TOOLTIPS = {
                Duplicate: 'Finds movies, episodes or tracks that exist as more than one file (byte-identical, provider-ID match, or heuristic title match). The fixer keeps the highest-quality copy and moves the rest to the recycle bin so restore is always possible.',
                Playability: 'Files ffprobe / ffmpeg cannot decode end-to-end — broken container, truncated download, corrupt codec, DRM. The fixer runs a 4-step repair ladder (quick remux → drop broken streams → coerce container → re-encode); if every rung fails the file is recycled.',
                Quality: 'Files bigger than your chosen quality ceiling (resolution + bitrate). The fixer re-encodes to the target profile using your GPU when available; the pre-encode original goes to the recycle bin as a safety net.',
                HeavyTranscode: 'Files that keep transcoding on-the-fly on your clients instead of direct-playing. The fixer re-encodes to a more client-compatible profile so future playbacks skip the transcode.',
                FailedTranscode: 'Recent Jellyfin transcode attempts that exited with an error (non-zero ffmpeg exit or a "Conversion failed" tail in the log). The fixer re-encodes with a stricter / safer profile.',
                SubtitleLanguage: 'Embedded subtitle tracks whose language is NOT in your allowed-languages list. The fixer remuxes the file without them (one ffmpeg pass, original recycled) and can also trim unused embedded fonts from .ass sidecars.',
                AudioLanguage: 'Embedded audio tracks whose language is NOT in your allowed-languages list. The fixer remuxes the file without them (one ffmpeg pass, original recycled).',
                Stale: 'Content nobody has played in your configured threshold days (default 365). Detect-only by default; no automatic deletion — this scanner is here to surface what you might want to prune, not to prune it.',
                Misplaced: 'A movie sitting in a TV library folder, or a TV episode sitting in a Movies library folder. The fixer moves the file to the correct library, preserving timestamps so Recently Added stays honest.',
                Ungrouped: 'Loose media files or oddly-named folders that should sit under a per-title (or per-franchise) parent folder. The fixer creates the correct folder and moves files in — handles year-suffixed reboots and franchise disambiguation.',
                MissingSubtitles: 'Files with no subtitle track in any of your kept languages. The fixer downloads a matching subtitle via the subtitle providers configured in Jellyfin.',
                MalwareRisk: 'Executable files (.exe / .bat / .sh / .cmd / .ps1 / .scr / .msi) sitting in a media folder — almost always malware from a pirated release. The fixer quarantines them to the recycle bin.',
                LargeTrickplay: 'Scrub-bar preview thumbnails Jellyfin generated as raw JPG. The fixer re-encodes to WebP, typically halving the disk footprint per title with no visible quality loss.',
                EmbeddedCoverArt: 'Music or audiobook folders where every track has embedded cover art but no folder-level cover.jpg. The fixer extracts one shared cover.jpg (and optionally strips the redundant per-file copies to save space).',
                OrphanedDebris: 'Empty folders, orphan subtitle sidecars, orphan trickplay folders, orphan metadata whose parent media file no longer exists. The fixer recycles them.',
                CorruptNfo: 'Zero-byte, malformed, or unrecognised-root .nfo metadata sidecars that stop Jellyfin from reading item info. The fixer recycles them so Jellyfin regenerates fresh metadata on the next refresh.',
                CorruptArtwork: 'Poster / backdrop / thumb / logo files inside the metadata folder Jellyfin manages that are zero-byte, truncated, or fail to decode. The fixer removes them so Jellyfin re-fetches on the next metadata pass. User-placed artwork alongside your media is never touched.',
                FormatCompatibility: 'Ebooks and comics in formats Jellyfin\'s browser reader cannot display (MOBI, AZW3, DjVu, CBR, CB7). The fixer converts them to EPUB or PDF (via Calibre) and CBZ respectively; originals go to the recycle bin so conversions are reversible.'
            };


            // Accent-tint background for the icon tile.
            var TASK_TINT = {
                Duplicate: 'rgba(245,158,11,.14)',
                Playability: 'rgba(239,68,68,.14)',
                Quality: 'rgba(59,130,246,.14)',
                SubtitleLanguage: 'rgba(139,92,246,.14)',
                AudioLanguage: 'rgba(16,185,129,.14)',
                Misplaced: 'rgba(249,115,22,.14)',
                MissingSubtitles: 'rgba(167,139,250,.14)',
                Stale: 'rgba(100,116,139,.18)',
                MalwareRisk: 'rgba(220,38,38,.18)',
                LargeTrickplay: 'rgba(20,184,166,.14)',
                OrphanedDebris: 'rgba(120,113,108,.16)',
                CorruptNfo: 'rgba(225,29,72,.14)',
                HeavyTranscode: 'rgba(249,115,22,.14)',
                FailedTranscode: 'rgba(220,38,38,.18)',
                EmbeddedCoverArt: 'rgba(168,85,247,.14)',
                FormatCompatibility: 'rgba(124,58,237,.14)'
            };


            function findFixType(key) {
                for (var i = 0; i < FIX_TYPES.length; i++) if (FIX_TYPES[i].key === key) return FIX_TYPES[i];
                return { key: key, mode: key + 'FixMode' };
            }


            function shortTaskLabel(key) {
                if (key === 'SubtitleLanguage') return 'Subtitles';
                if (key === 'SubtitleFonts') return 'Subtitles';
                if (key === 'AudioLanguage') return 'Unwanted audio';
                if (key === 'Playability') return "Won't play";
                if (key === 'Quality') return 'Wasting space';
                return deps.STR.types[key].title;
            }


            function modeSegments(key) {
                // Stale is approval-only — age-based auto-delete can clear a library overnight,
                // so Automatic is hidden here and clamped server-side by NormaliseFixModes.
                // Ask me first stays available so the scanner can actually be turned on; the earlier
                // "canAuto = false" shortcut suppressed both Automatic AND Ask me first and left
                // Off as the sole option, hiding any way to enable the scanner.
                var autoSuppressed = key === 'Stale';
                // Duplicate: Automatic disabled while the new confidence-ladder detection stabilises
                // (rework landed in 1.0.6, still tuning against real reporter data). Ask-me-first is
                // the safe posture for now; a stray auto-delete on a false-positive pair is
                // irreversible where a false-negative leaves a duplicate for next scan.
                var duplicateAutoDisabled = key === 'Duplicate';
                var opts = [
                    { v: 'Off', l: deps.STR.modeOff || 'Off' },
                    { v: 'ManualApprove', l: deps.STR.modeManual || 'Ask me first' }
                ];
                if (!autoSuppressed && !duplicateAutoDisabled) {
                    opts.push({ v: 'Automatic', l: deps.STR.modeAuto || 'Automatic' });
                }
                var tooltip = duplicateAutoDisabled
                    ? ' title="Automatic mode is temporarily disabled while duplicate detection is being reworked and isn\'t reliable enough for hands-off auto-delete. Use Ask me first."'
                    : (autoSuppressed
                        ? ' title="Stale is approval-only — each row needs an explicit Approve on the Issues tab before the file is deleted. Automatic is deliberately not offered."'
                        : '');
                return '<div class="mdTaskModes" role="radiogroup"' + tooltip + '>' + opts.map(function (o) {
                    var i = o.l.indexOf(' — ');
                    var inner = i < 0 ? deps.esc(o.l) : deps.esc(o.l.slice(0, i)) + '<span class="mdModeHint"> — ' + deps.esc(o.l.slice(i + 3)) + '</span>';
                    return '<button type="button" data-mode="' + o.v + '">' + inner + '</button>';
                }).join('') + '</div>' +
                    '<input type="hidden" id="mdMode' + key + '" value="ManualApprove" />';
                // The "Automatic disabled" explanation lives in the Duplicate task-card BODY
                // (see buildTaskCard's `key === 'Duplicate'` branch) — putting it here made the
                // header flex row wrap the amber banner alongside the mode segments, squashing
                // the card title. The segments-container tooltip is the compact hover cue.
            }


            function disposalField(key, label) {
                return '<div class="mdField">' +
                    '<label for="mdDisposal' + key + '">' + deps.esc(label) + '</label>' +
                    '<select is="emby-select" id="mdDisposal' + key + '" class="emby-select-withcolor emby-select">' + disposalOptions(key) + '</select>' +
                    '</div>';
            }


            // Renders a placeholder row that populateLibShortcuts() fills after /Libraries returns.
            // Each rendered chip fills the paired input on click; users can still type freeform.
            function libShortcutRowHtml(targetId, types) {
                return '<div class="mdLibShortcuts" data-target="' + targetId + '" data-types="' + types + '"></div>';
            }


            function langPresetsHtml(id) {
                return '<div class="mdLangPresets" data-target="' + id + '">' +
                    '<button type="button" class="mdJumpCollapse mdLangPresetBtn" data-preset="clear" title="Remove every language chip from this input.">Clear</button>' +
                '</div>';
            }


            function langChipInputHtml(id) {
                // list="mdLangList" wires the input to the shared <datalist> populated from
                // /Localization/Cultures at settings-load — turns the freeform typebox into a native
                // combobox showing every language Jellyfin supports as the user types.
                return '<div class="mdChipInput" data-chip-input="' + id + '">' +
                    '<span class="mdChipInputRender"></span>' +
                    '<input type="text" class="mdChipInputText" list="mdLangList" placeholder="Type or pick a language…" />' +
                    '<input type="hidden" id="' + id + '" value="" />' +
                    '</div>' +
                    langPresetsHtml(id);
            }


            function taskBody(key) {
                if (key === 'Duplicate') {
                    // 1.0.6 rework in flight — surface why Automatic isn't offered before the user
                    // scrolls through the confidence-gate knobs. Rendered inside the card BODY
                    // (not the header flex row) so it doesn't squash the title.
                    return '<div class="fieldDescription" style="margin:0 0 .8em;padding:.6em .8em;border-radius:6px;background:rgba(245,158,11,.10);border:1px solid rgba(245,158,11,.35);font-size:.9em;"><span class="material-icons" style="font-size:1em;vertical-align:-3px;color:#f59e0b;">construction</span> <b>Automatic mode is temporarily disabled for duplicates.</b> The detection pipeline is being reworked (see 1.0.6 changelog); it isn\'t reliable enough for hands-off auto-delete yet. <b>Ask me first</b> is the recommended setting until this notice is removed.</div>' +
                        '<div class="mdFieldGrid mdFieldGrid-2">' +
                        '<div class="mdField">' +
                            '<label for="mdKeeperPolicy" title="How the duplicate scanner picks which copy stays. Best-quality is the safe default; codec-first favours modern re-encodes; size-first ruthlessly picks the smallest file even if it\'s lower quality.">When duplicates are found, keep</label>' +
                            '<select is="emby-select" id="mdKeeperPolicy" class="emby-select-withcolor emby-select">' +
                                '<option value="quality">The best-quality copy (recommended)</option>' +
                                '<option value="codec">The most modern format (codec first)</option>' +
                                '<option value="size">The smallest file</option>' +
                            '</select>' +
                            '<span class="mdFieldHint">Compares resolution, then codec, then bitrate; ties go to the smaller file.</span>' +
                        '</div>' +
                        disposalField('Duplicate', 'The worse copy goes to') +
                        '<div class="mdField" style="grid-column:1/-1;">' +
                            '<label for="mdDuplicateMinAge" title="Freshly-imported items can transiently self-duplicate while Jellyfin\'s metadata scrape catches up. This grace period skips items younger than N days.">Wait N days after import</label>' +
                            '<input id="mdDuplicateMinAge" type="number" min="0" max="365" is="emby-input" />' +
                            '<span class="mdFieldHint">Freshly-imported items can transiently look like duplicates of themselves while Jellyfin\'s metadata scrape catches up. Default 0 (no wait — flag duplicates immediately). Set a positive value to defer flagging until every copy is that many days old.</span>' +
                        '</div>' +
                        '<label class="mdInsetRow" style="grid-column:1/-1;cursor:pointer;">' +
                            '<input id="mdEditions" type="checkbox" class="mdInsetCheck" checked />' +
                            '<span style="font-size:13px;font-weight:600;flex:0 0 auto;">Treat different editions as duplicates</span>' +
                            '<span class="mdFieldHint" style="margin:0;flex:1;">Off: a Director\'s Cut and the theatrical cut stay as separate movies.</span>' +
                        '</label>' +
                        // Confidence ladder controls (2026-08-22 rework §7). Kept collapsed under one
                        // grid so they don't overwhelm the "just tell me if I have duplicates" user.
                        '<div style="grid-column:1/-1;margin-top:.6em;padding:.7em .9em;border-radius:8px;background:rgba(255,255,255,.03);border:1px solid rgba(255,255,255,.08);">' +
                            '<div style="font-size:12px;font-weight:600;letter-spacing:.02em;text-transform:uppercase;color:var(--md-dim);margin-bottom:.5em;">Confidence gate</div>' +
                            '<div class="mdFieldGrid mdFieldGrid-2">' +
                                '<div class="mdField">' +
                                    '<label for="mdDupAutoFixConfidence" title="When a fix type runs in Automatic mode, only pairs at or above this per-pair confidence are auto-queued. Manual approval from the Issues tab always overrides.">Auto-fix confidence threshold</label>' +
                                    '<input id="mdDupAutoFixConfidence" type="number" min="0" max="1" step="0.05" is="emby-input" />' +
                                    '<span class="mdFieldHint">0.80 default. Byte-identical (1.00) always auto-fixes; provider-ID matches (0.90) do; filename/episode-only matches below the gate wait for manual approval.</span>' +
                                '</div>' +
                                '<div class="mdField">' +
                                    '<label for="mdDupTitleVeto" title="Below this filename-token Jaccard, the pair is not flagged as a duplicate at all (protects against two different specials sharing an episode slot).">Title-token veto (Jaccard)</label>' +
                                    '<input id="mdDupTitleVeto" type="number" min="0" max="1" step="0.05" is="emby-input" />' +
                                    '<span class="mdFieldHint">0.40 default. Filenames whose title tokens overlap below this are treated as different content.</span>' +
                                '</div>' +
                                '<div class="mdField">' +
                                    '<label for="mdDupRuntimeVeto" title="If both runtimes are known and differ by more than this percent, the pair is not flagged as a duplicate.">Runtime veto (%)</label>' +
                                    '<input id="mdDupRuntimeVeto" type="number" min="0" max="100" step="1" is="emby-input" />' +
                                    '<span class="mdFieldHint">15 default. Two files claiming to be the same content but with wildly different runtimes probably aren\'t.</span>' +
                                '</div>' +
                                '<label class="mdInsetRow--stack" style="cursor:pointer;" title="Within an already-formed group of same-size candidates, SHA-256 the files to confirm byte-identical matches. Bounded cost, on by default.">' +
                                    '<span class="mdInsetRowMain">' +
                                        '<input id="mdDupExactHash" type="checkbox" class="mdInsetCheck" />' +
                                        '<span style="font-size:13px;font-weight:600;">Confirm byte-identical with SHA-256</span>' +
                                    '</span>' +
                                    '<span class="mdFieldHint mdInsetRowHint">Only invoked within a metadata group of same-size files, cached by (path, size, mtime). Byte-identical pairs bypass every veto and auto-fix under any confidence gate.</span>' +
                                '</label>' +
                            '</div>' +
                        '</div>' +
                        '</div>' +
                        '<span class="mdFieldHint">Retention and bin location live in the <a href="#" class="mdGoRecycleTab" style="color:rgba(230,237,243,.85);text-decoration:underline;">Recycle bin tab</a>.</span>';
                }
                if (key === 'Playability') {
                    return '<div class="mdFieldGrid mdFieldGrid-2">' +
                        disposalField('Playability', 'Broken files go to') +
                        '<label class="mdInsetRow--stack" style="cursor:pointer;">' +
                            '<span class="mdInsetRowMain">' +
                                '<input id="mdThorough" type="checkbox" class="mdInsetCheck" checked />' +
                                '<span style="font-size:13px;font-weight:600;">Thorough playability check</span>' +
                            '</span>' +
                            '<span class="mdFieldHint mdInsetRowHint">On adds full multi-track audio and interleave checks. Off samples video and all audio tracks in short windows. Sampled checks can miss damage between windows.</span>' +
                        '</label>' +
                        '</div>' +
                        '<div class="mdFieldGrid mdFieldGrid-1" style="margin-top:12px;">' +
                            '<div class="mdField">' +
                                '<label style="font-size:13px;font-weight:600;">Repair broken files before deleting</label>' +
                                '<span class="mdFieldHint">Steps run in order and stop at the first one that produces a working file. Untick a step to skip it.</span>' +
                                '<label class="mdInsetRow--stack" style="cursor:pointer;margin-top:8px;">' +
                                    '<span class="mdInsetRowMain">' +
                                        '<input id="mdRepairRemux" type="checkbox" class="mdInsetCheck" />' +
                                        '<span style="font-size:13px;font-weight:600;">1. Quick remux</span>' +
                                    '</span>' +
                                    '<span class="mdFieldHint mdInsetRowHint">Fix container damage without re-encoding.</span>' +
                                '</label>' +
                                '<label class="mdInsetRow--stack" style="cursor:pointer;">' +
                                    '<span class="mdInsetRowMain">' +
                                        '<input id="mdRepairDropStreams" type="checkbox" class="mdInsetCheck" />' +
                                        '<span style="font-size:13px;font-weight:600;">2. Drop broken streams</span>' +
                                    '</span>' +
                                    '<span class="mdFieldHint mdInsetRowHint">Remove tracks that fail to decode. Keeps at least one video and one audio track.</span>' +
                                '</label>' +
                                '<label class="mdInsetRow--stack" style="cursor:pointer;">' +
                                    '<span class="mdInsetRowMain">' +
                                        '<input id="mdRepairContainerCoerce" type="checkbox" class="mdInsetCheck" />' +
                                        '<span style="font-size:13px;font-weight:600;">3. Change container to MKV</span>' +
                                    '</span>' +
                                    '<span class="mdFieldHint mdInsetRowHint">Repack into MKV. <strong>Jellyfin will treat the file as new and watch history for the title will reset.</strong></span>' +
                                '</label>' +
                                '<label class="mdInsetRow--stack" style="cursor:pointer;">' +
                                    '<span class="mdInsetRowMain">' +
                                        '<input id="mdRepairAudioReencode" type="checkbox" class="mdInsetCheck" />' +
                                        '<span style="font-size:13px;font-weight:600;">4. Re-encode audio only</span>' +
                                    '</span>' +
                                    '<span class="mdFieldHint mdInsetRowHint">When the file\'s only audio track is broken. Copies video, minutes not hours.</span>' +
                                '</label>' +
                                '<label class="mdInsetRow--stack" style="cursor:pointer;">' +
                                    '<span class="mdInsetRowMain">' +
                                        '<input id="mdRepairReencode" type="checkbox" class="mdInsetCheck" />' +
                                        '<span style="font-size:13px;font-weight:600;">5. Re-encode video</span>' +
                                    '</span>' +
                                    '<span class="mdFieldHint mdInsetRowHint">Last resort. Can take hours per file, runs in the background.</span>' +
                                '</label>' +
                            '</div>' +
                        '</div>' +
                        '<span class="mdFieldHint">Broken-file removal always waits for approval, even in Automatic.</span>';
                }
                if (key === 'Quality') {
                    return '<div id="mdQualityAutoStrip" class="mdInfoStrip" style="display:none;">' +
                            '<span class="material-icons">bolt</span>' +
                            '<span id="mdQualityAutoStripText"></span>' +
                        '</div>' +
                        '<div class="mdFieldGrid mdFieldGrid-3">' +
                            '<div class="mdField">' +
                                '<label for="mdMaxRes">Maximum resolution</label>' +
                                '<select is="emby-select" id="mdMaxRes" class="emby-select-withcolor emby-select">' +
                                    '<option value="4320">8K (4320p)</option>' +
                                    '<option value="2160">4K (2160p)</option>' +
                                    '<option value="1440">1440p</option>' +
                                    '<option value="1080">1080p</option>' +
                                    '<option value="720">720p</option>' +
                                    '<option value="480">480p</option>' +
                                    '<option value="360">360p</option>' +
                                '</select>' +
                            '</div>' +
                            '<div class="mdField">' +
                                '<label for="mdCodec">Re-encode to</label>' +
                                '<select is="emby-select" id="mdCodec" class="emby-select-withcolor emby-select">' +
                                    '<option value="hevc">H.265 / HEVC (recommended)</option>' +
                                    '<option value="h264">H.264 (best compatibility)</option>' +
                                    '<option value="av1">AV1 (smallest files, slow)</option>' +
                                    '<option value="vp9">VP9 (WebM native, needs MKV)</option>' +
                                '</select>' +
                            '</div>' +
                            '<div class="mdField">' +
                                '<label for="mdContainer">Save re-encoded files as</label>' +
                                '<select is="emby-select" id="mdContainer" class="emby-select-withcolor emby-select">' +
                                    '<option value="mkv">MKV — keeps subtitles</option>' +
                                    '<option value="mp4">MP4 — no subtitle tracks</option>' +
                                '</select>' +
                            '</div>' +
                            '<div class="mdField mdBitrateField" style="grid-column:1/-1;">' +
                                '<label for="mdMaxBitrate" id="mdMaxBitrateLabel" title="The ceiling above which the Quality scanner flags files. Any file whose bitrate exceeds this at 1080p (scaled for other resolutions) is queued for re-encode.">' +
                                    '<span class="mdBitrateLabelText">Maximum video bitrate at 1080p</span>' +
                                    '<span class="mdSliderVal mdBitrateValue" id="mdMaxBitrateVal"></span>' +
                                '</label>' +
                                '<input id="mdMaxBitrate" type="range" min="1" max="40" step="0.5" class="mdControl mdControl--slider" />' +
                                '<span class="mdFieldHint">Scaled automatically for every other resolution.</span>' +
                            '</div>' +
                            disposalField('Quality', 'The original file goes to') +
                        '</div>' +
                        '<div class="mdInsetRow" style="gap:14px;flex-wrap:wrap;">' +
                            '<label for="mdEncoder" title="CPU (software) produces the best quality per bit but is much slower. GPUs are much faster but the file is slightly larger for the same quality. Auto lets Jellyfin pick the first available GPU." style="font-size:12.5px;font-weight:600;color:rgba(230,237,243,.85);">Encoder</label>' +
                            '<select is="emby-select" id="mdEncoder" class="emby-select-withcolor emby-select" style="max-width:26em;flex:1 1 12em;min-width:0;"></select>' +
                            '<span class="mdFieldHint" style="margin:0;flex:1 1 100%;">Falls back to software automatically if a hardware encode fails.</span>' +
                        '</div>' +
                        '<details class="mdAdvanced">' +
                            '<summary class="mdAdvancedToggle" style="list-style:none;">' +
                                '<span class="material-icons">expand_more</span>' +
                                '<span class="mdAdvancedTitle">Advanced</span>' +
                                '<span class="mdAdvancedTeaser">Encode preset · tolerance · ignore under 100 MB · skip HDR · file types · rename</span>' +
                            '</summary>' +
                            '<div class="mdAdvancedBody">' +
                                '<div class="mdFieldGrid mdFieldGrid-2">' +
                                    '<div class="mdField">' +
                                        '<label for="mdEncodePreset" title="Trade-off between encode speed and final file size for software encoders (x264/x265). Hardware encoders ignore this and use the plugin\'s bitrate ceiling instead.">Encode preset (software encoders)</label>' +
                                        '<select is="emby-select" id="mdEncodePreset" class="emby-select-withcolor emby-select">' +
                                            '<option value="Faster">Faster — half the time, slightly bigger files</option>' +
                                            '<option value="Balanced">Balanced — recommended</option>' +
                                            '<option value="Best">Best — best quality/size, twice the time</option>' +
                                        '</select>' +
                                    '</div>' +
                                    '<div class="mdField">' +
                                        '<div style="display:flex;justify-content:space-between;align-items:baseline;">' +
                                            '<label for="mdTolerance" title="Skip files whose bitrate is only slightly over the ceiling. Avoids churning on borderline files. 15% is a good default.">Tolerance</label>' +
                                            '<span class="mdSliderVal" id="mdToleranceVal"></span>' +
                                        '</div>' +
                                        '<input id="mdTolerance" type="range" min="0" max="50" step="5" />' +
                                        '<span class="mdFieldHint">Files only slightly over are left alone.</span>' +
                                    '</div>' +
                                    '<div class="mdField">' +
                                        '<label for="mdMinSize">Ignore files smaller than (MB)</label>' +
                                        '<input id="mdMinSize" type="number" is="emby-input" min="0" max="10000" step="10" />' +
                                        '<span class="mdFieldHint">Filters samples and trailers. Default 100 MB.</span>' +
                                    '</div>' +
                                    '<div class="mdField">' +
                                        '<label for="mdFileTypes">Only re-encode these file types</label>' +
                                        '<input id="mdFileTypes" type="text" is="emby-input" placeholder="all file types" />' +
                                        '<span class="mdFieldHint">Comma-separated: "avi, wmv, mkv". Empty = all.</span>' +
                                    '</div>' +
                                    '<div class="mdField">' +
                                        '<label for="mdMinSavingsPct" title="Skip the swap when a Quality re-encode saves less than this. 0 = off (only larger-than-original is rejected).">Minimum savings (%)</label>' +
                                        '<input id="mdMinSavingsPct" type="number" is="emby-input" min="0" max="95" step="1" />' +
                                        '<span class="mdFieldHint">0 = off. Applies to Quality fixes only. Skipped files are not retried.</span>' +
                                    '</div>' +
                                    '<div class="mdField">' +
                                        '<label for="mdMinSavingsMb" title="Skip the swap when a Quality re-encode saves fewer than this many megabytes. 0 = off. Combined with the percent rule via AND — both must be met.">Minimum savings (MB)</label>' +
                                        '<input id="mdMinSavingsMb" type="number" is="emby-input" min="0" max="1048576" step="10" />' +
                                        '<span class="mdFieldHint">0 = off. Combined with the % rule: both must be met.</span>' +
                                    '</div>' +
                                '</div>' +
                                '<label class="mdInsetRow" style="cursor:pointer;" title="HDR (BT.2020 + PQ/HLG) files need color-space plumbing MediaDash doesn\'t handle. Skipping them prevents accidentally destroying HDR metadata.">' +
                                    '<input id="mdSkipHdr" type="checkbox" class="mdInsetCheck" />' +
                                    '<span style="font-size:13px;font-weight:600;flex:0 0 auto;">Skip HDR content</span>' +
                                    '<span class="mdFieldHint" style="margin:0;flex:1;">Re-encoding HDR without color-space plumbing destroys metadata.</span>' +
                                '</label>' +
                                '<label class="mdInsetRow" style="cursor:pointer;">' +
                                    '<input id="mdQualityScanAudiobooks" type="checkbox" class="mdInsetCheck" />' +
                                    '<span style="font-size:13px;font-weight:600;flex:0 0 auto;">Also scan audiobooks for oversized audio</span>' +
                                    '<span class="mdFieldHint" style="margin:0;flex:1;">Audiobooks are usually already lean — enable only if you have unusually large audiobook files.</span>' +
                                '</label>' +
                                '<label class="mdInsetRow" style="cursor:pointer;" title="After a re-encode succeeds, rename the output to a canonical form: Movies get \'Name (Year) - {height}p.{ext}\', TV gets \'SeriesName - S{ss:00}E{ee:00} - {height}p.{ext}\'.">' +
                                    '<input id="mdRenameAfterTranscode" type="checkbox" class="mdInsetCheck" />' +
                                    '<span style="font-size:13px;font-weight:600;flex:0 0 auto;">Rename re-encoded files to a canonical name</span>' +
                                    '<span class="mdFieldHint" style="margin:0;flex:1;">Movie (Year) - 1080p.mkv style.</span>' +
                                '</label>' +
                                '<label class="mdInsetRow" style="cursor:pointer;" title="When renaming, keep an existing [tmdbid-NNNN] (movies) or [tvdbid-NNNN] (episodes) tag from the source filename so Sonarr / Radarr-style filename metadata survives the re-encode. Falls back to Jellyfin\'s provider IDs when the source has no tag.">' +
                                    '<input id="mdPreserveExternalIdInFilename" type="checkbox" class="mdInsetCheck" />' +
                                    '<span style="font-size:13px;font-weight:600;flex:0 0 auto;">Keep TMDB / TVDB ID in the canonical name</span>' +
                                    '<span class="mdFieldHint" style="margin:0;flex:1;">Movie (Year) - 1080p [tmdbid-12345].mkv; only takes effect when the rename option above is on.</span>' +
                                '</label>' +
                            '</div>' +
                        '</details>';
                }
                if (key === 'SubtitleLanguage') {
                    var fontOpts = SUBTITLE_FONTS.map(function (f) {
                        return '<option value="' + deps.esc(f.v) + '" style="font-family:' + deps.esc(f.stack) + ';font-size:14px;">' + deps.esc(f.l) + '</option>';
                    }).join('');
                    return '<div class="mdFieldGrid" style="grid-template-columns:repeat(auto-fit,minmax(min(20em,100%),1fr));">' +
                        '<div class="mdField">' +
                            '<label>Subtitle languages to keep</label>' +
                            langChipInputHtml('mdSubLangs') +
                            '<span class="mdFieldHint">Removal is a lossless remux — no quality loss. Original language uses item metadata or original audio flags; cleanup is skipped if neither identifies it. Empty list = task is skipped entirely (no subs are touched).</span>' +
                        '</div>' +
                        disposalField('SubtitleLanguage', 'Cleaned-up originals go to') +
                        '</div>' +
                        '<label class="mdInsetRow--stack" style="cursor:pointer;margin-top:.8em;" title="Turn on if you (or a viewer) rely on SDH / closed captions. When on: subtitle tracks flagged hearing-impaired are never removed even if their language is outside the keep list, and the missing-subtitle fixer prefers HI/SDH variants when downloading.">' +
                            '<span class="mdInsetRowMain">' +
                                '<input id="mdSubHearingImpaired" type="checkbox" class="mdInsetCheck" />' +
                                '<span style="font-size:13px;font-weight:600;">Hearing-impaired mode (protect &amp; prefer SDH / CC subtitles)</span>' +
                            '</span>' +
                            '<span class="mdFieldHint mdInsetRowHint">Applies to both this task and the Missing Subtitles downloader.</span>' +
                        '</label>' +
                        '<div class="mdSecHead" style="border-color:#8b5cf6;background:rgba(139,92,246,.08);margin-top:1.4em;">' +
                            '<span class="material-icons">font_download</span><span>Also optimize .ass sidecar subtitles</span>' +
                        '</div>' +
                        '<div class="mdFieldGrid mdFieldGrid-2">' +
                            '<div class="mdField">' +
                                '<label for="mdModeSubtitleFonts" title="Ask me first queues the fix for review; Automatic runs it in the next fix window.">Font optimizer mode</label>' +
                                '<select is="emby-select" id="mdModeSubtitleFonts" class="emby-select-withcolor emby-select">' +
                                    '<option value="Off">Off</option>' +
                                    '<option value="ManualApprove">Ask me first</option>' +
                                    '<option value="Automatic">Automatic</option>' +
                                '</select>' +
                                '<span class="mdFieldHint">Strips embedded fonts that no style references (common bloat in fansubs). Contents-only edit.</span>' +
                            '</div>' +
                            '<div class="mdField">' +
                                '<label for="mdSubForceFont" title="Rewrites every Style Fontname and inline \\fn override to this family, then drops the whole [Fonts] block from each .ass file. Pick a font your clients are guaranteed to have; empty keeps the sidecar\'s original per-style fonts.">Force every subtitle to use</label>' +
                                '<select is="emby-select" id="mdSubForceFont" class="emby-select-withcolor emby-select">' +
                                    fontOpts +
                                '</select>' +
                                '<span class="mdFieldHint">Warning: overrides fansub art direction. Keep on "Keep original fonts" unless you want a single look everywhere.</span>' +
                            '</div>' +
                        '</div>';
                }
                if (key === 'AudioLanguage') {
                    return '<div class="mdFieldGrid" style="grid-template-columns:repeat(auto-fit,minmax(min(20em,100%),1fr));">' +
                        '<div class="mdField">' +
                            '<label>Audio languages to keep</label>' +
                            langChipInputHtml('mdAudioLangs') +
                            '<span class="mdFieldHint">A file\'s only audio track is never removed. Original language uses item metadata or original audio flags; cleanup is skipped if neither identifies it. Empty list = task is skipped entirely (no audio is touched).</span>' +
                        '</div>' +
                        disposalField('AudioLanguage', 'Cleaned-up originals go to') +
                        '</div>';
                }
                if (key === 'Misplaced') {
                    return '<div class="mdWarnStrip" id="mdMisplacedWarn" style="display:none;">' +
                            '<span class="material-icons">warning</span>' +
                            '<span>Set both the Movies and TV folders below to turn this task on.</span>' +
                        '</div>' +
                        '<div class="mdFieldGrid mdFieldGrid-2">' +
                            '<div class="mdField">' +
                                '<label for="mdMoviesTarget">Movies destination folder</label>' +
                                '<input id="mdMoviesTarget" type="text" is="emby-input" placeholder="/media/movies" />' +
                                libShortcutRowHtml('mdMoviesTarget', 'movies') +
                            '</div>' +
                            '<div class="mdField">' +
                                '<label for="mdTvTarget">TV destination folder</label>' +
                                '<input id="mdTvTarget" type="text" is="emby-input" placeholder="/media/tv" />' +
                                libShortcutRowHtml('mdTvTarget', 'tvshows') +
                            '</div>' +
                            '<div class="mdField">' +
                                '<label for="mdAnimeTarget" title="Optional. Files whose Jellyfin genre tag includes \'Anime\' are routed here instead of the Movies or TV pile. Empty = keep anime in Movies/TV.">Anime destination (optional)</label>' +
                                '<input id="mdAnimeTarget" type="text" is="emby-input" placeholder="empty leaves anime in Movies/TV" />' +
                                libShortcutRowHtml('mdAnimeTarget', 'movies,tvshows') +
                            '</div>' +
                            '<div class="mdField">' +
                                '<label for="mdMusicTarget" title="Optional. Audio / MusicAlbum items found under a non-music library get moved here. Empty = ignore music misplacement.">Music destination (optional)</label>' +
                                '<input id="mdMusicTarget" type="text" is="emby-input" placeholder="empty leaves music where it sits" />' +
                                libShortcutRowHtml('mdMusicTarget', 'music') +
                            '</div>' +
                            '<div class="mdField">' +
                                '<label for="mdBooksTarget" title="Optional. Book items (EPUB / PDF / MOBI / AZW3 / and AudioBook items) found outside a books library get moved here. Empty = ignore book misplacement.">Books destination (optional)</label>' +
                                '<input id="mdBooksTarget" type="text" is="emby-input" placeholder="empty leaves books where they sit" />' +
                                libShortcutRowHtml('mdBooksTarget', 'books') +
                            '</div>' +
                            '<div class="mdField">' +
                                '<label for="mdComicsTarget" title="Optional. Comic archive files (CBZ / CBR / CB7 — currently classified as Book by Jellyfin) found outside a comics library get moved here. Empty = ignore comic misplacement.">Comics destination (optional)</label>' +
                                '<input id="mdComicsTarget" type="text" is="emby-input" placeholder="empty leaves comics where they sit" />' +
                                libShortcutRowHtml('mdComicsTarget', 'books') +
                            '</div>' +
                            '<div class="mdField">' +
                                '<label for="mdPicturesTarget" title="Optional. Photo / Picture items found outside a photo library get moved here. Empty = ignore picture misplacement.">Pictures destination (optional)</label>' +
                                '<input id="mdPicturesTarget" type="text" is="emby-input" placeholder="empty leaves pictures where they sit" />' +
                                libShortcutRowHtml('mdPicturesTarget', 'homevideos,photos') +
                            '</div>' +
                            '<div class="mdField">' +
                                '<label for="mdMediaSortSource" title="Jellyfin metadata is authoritative when items are recognised; the filename fallback catches unidentified files by looking for SxxExx / NxN patterns.">Detect movie / TV from</label>' +
                                '<select is="emby-select" id="mdMediaSortSource" class="emby-select-withcolor emby-select">' +
                                    '<option value="JellyfinMetadata">Jellyfin metadata (recommended)</option>' +
                                    '<option value="FilenameHeuristic">Filename pattern (fallback)</option>' +
                                '</select>' +
                            '</div>' +
                        '</div>';
                }
                if (key === 'MissingSubtitles') {
                    return '<div class="mdFieldHint">Uses the subtitle languages set in <a href="#mdSet-task-SubtitleLanguage" style="color:rgba(230,237,243,.85);text-decoration:underline;">Subtitles</a>. Downloads go through Jellyfin\'s configured subtitle providers.</div>' +
                        '<div class="mdFieldHint" id="mdProviderHint" style="display:none;"></div>' +
                        '<label class="mdInsetRow--stack" style="cursor:pointer;" title="Applies to any subtitle provider that respects Jellyfin\'s automated-search hint (currently OpenSubtitles.com; most other providers ignore it and behave the same either way). When on, MediaDash searches as if a human clicked \"Search subtitles\" in Jellyfin\'s UI, unlocking the full result set. Turn off to respect provider throttling and accept fewer hits.">' +
                            '<span class="mdInsetRowMain">' +
                                '<input id="mdSubIgnoreRate" type="checkbox" class="mdInsetCheck" />' +
                                '<span style="font-size:13px;font-weight:600;">Ignore rate limiting</span>' +
                            '</span>' +
                            '<span class="mdFieldHint mdInsetRowHint">Applies to any provider that honours Jellyfin\'s automated-search flag — today that\'s effectively OpenSubtitles.com; others (Addic7ed, Podnapisi, legacy OpenSubtitles.org, etc.) ignore the flag. Recommended on: without it OpenSubtitles.com trims about two-thirds of episode hits to preserve free-tier quota.</span>' +
                        '</label>';
                }
                if (key === 'MalwareRisk') {
                    return '<div class="mdFieldGrid mdFieldGrid-2">' +
                        disposalField('MalwareRisk', 'Suspicious files go to') +
                        '</div>' +
                        '<span class="mdFieldHint">Flagged files always require approval, even in Automatic — bundled malware in pirated releases can flag on filenames alone, so the fixer never runs on its own.</span>';
                }
                if (key === 'CorruptNfo') {
                    return '<div class="mdFieldGrid mdFieldGrid-2">' +
                        disposalField('CorruptNfo', 'Deleted NFOs go to') +
                        '</div>' +
                        '<span class="mdFieldHint">Deletion is safe by default: Jellyfin re-fetches metadata from your configured providers on the next library scan. Only turn on Permanent if you never rely on the recycle bin — hand-curated NFOs live here.</span>';
                }
                if (key === 'HeavyTranscode') {
                    return '<div class="mdFieldGrid mdFieldGrid-2">' +
                        '<div class="mdField">' +
                            '<label for="mdHeavyTranscodeDays" title="Reads Jellyfin\'s ffmpeg transcode logs. Files touched by any live transcode within this many days will be flagged.">Lookback window</label>' +
                            '<input id="mdHeavyTranscodeDays" type="number" min="1" max="365" step="1" is="emby-input" />' +
                            '<span class="mdFieldHint">Number of days of Jellyfin transcode logs to read. 30 by default. Higher = more history but slower scans on servers with heavy log churn.</span>' +
                        '</div>' +
                        disposalField('HeavyTranscode', 'Original files go to') +
                        '</div>' +
                        '<span class="mdFieldHint">Reads <code>%datadir%/log/FFmpeg.Transcode-*.log</code>. The fix reuses the Quality re-encoder using your existing codec / bitrate / encoder settings — set those in the Quality task above.</span>';
                }
                if (key === 'FailedTranscode') {
                    return '<div class="mdFieldGrid mdFieldGrid-2">' +
                        disposalField('FailedTranscode', 'Original files go to') +
                        '</div>' +
                        '<span class="mdFieldHint">Same log source as the heavy-transcode task. This one only flags files whose most recent transcode ended with a failure marker (Conversion failed, decoder open error, invalid input). A targeted re-encode with MediaDash\'s own settings usually succeeds where the live attempt didn\'t.</span>';
                }
                if (key === 'EmbeddedCoverArt') {
                    return '<div class="mdFieldGrid mdFieldGrid-2">' +
                        '<div class="mdField">' +
                            '<label for="mdCoverFilename" title="Jellyfin recognises both cover.jpg and folder.jpg as album artwork. cover.jpg is the modern convention; older setups sometimes use folder.jpg.">Folder cover filename</label>' +
                            '<input id="mdCoverFilename" type="text" is="emby-input" placeholder="cover.jpg" />' +
                            '<span class="mdFieldHint">Written once per affected folder.</span>' +
                        '</div>' +
                        disposalField('EmbeddedCoverArt', 'Stripped-audio originals go to') +
                        '</div>' +
                        '<label class="mdInsetRow" style="cursor:pointer;" title="If off, the fixer only extracts the shared cover.jpg and leaves the audio files untouched. If on, it also rewrites each audio file to remove the embedded cover — much bigger space win but every audio file gets a lossless remux.">' +
                            '<input id="mdCoverStripAudio" type="checkbox" class="mdInsetCheck" />' +
                            '<span style="font-size:13px;font-weight:600;flex:0 0 auto;">Also strip the embedded cover from each audio file</span>' +
                            '<span class="mdFieldHint" style="margin:0;flex:1;">Reclaims ~500 KB per track. Originals are recycled by default so anything can be rolled back.</span>' +
                        '</label>' +
                        '<span class="mdFieldHint">Extraction pipeline: raw → libwebp compression → back to jpg for maximum client compatibility. Runs across music and audiobook libraries only.</span>';
                }
                if (key === 'OrphanedDebris') {
                    return '<div class="mdFieldGrid mdFieldGrid-2">' +
                        '<div class="mdField" style="grid-column:1/-1;">' +
                            '<label style="font-weight:600;">Detect which orphan kinds</label>' +
                            '<label class="emby-checkbox-label" style="display:flex;align-items:center;gap:.6em;margin:.35em 0;">' +
                                '<input id="mdOrphanEmpty" type="checkbox" is="emby-checkbox" />' +
                                '<span>Empty folders (no video files anywhere in the subtree — the library root itself is always kept)</span>' +
                            '</label>' +
                            '<label class="emby-checkbox-label" style="display:flex;align-items:center;gap:.6em;margin:.35em 0;">' +
                                '<input id="mdOrphanSubs" type="checkbox" is="emby-checkbox" />' +
                                '<span>Subtitle sidecars (.srt / .ass / .ssa / .vtt / .sub / .idx / .sup / .smi / .sami / .mks) whose companion video is missing</span>' +
                            '</label>' +
                            '<label class="emby-checkbox-label" style="display:flex;align-items:center;gap:.6em;margin:.35em 0;">' +
                                '<input id="mdOrphanTrickplay" type="checkbox" is="emby-checkbox" />' +
                                '<span>Trickplay folders (<code>*.trickplay/</code>) whose companion video is missing</span>' +
                            '</label>' +
                            '<label class="emby-checkbox-label" style="display:flex;align-items:center;gap:.6em;margin:.35em 0;">' +
                                '<input id="mdOrphanMetadata" type="checkbox" is="emby-checkbox" />' +
                                '<span>Jellyfin metadata folders whose item no longer exists (global cleanup; missing owners cannot be assigned to a selected library)</span>' +
                            '</label>' +
                            '<span class="mdFieldHint">Every fix is re-verified at delete time — if a video reappeared between scan and fix, the orphan is left alone.</span>' +
                        '</div>' +
                        disposalField('OrphanedDebris', 'Deleted orphans go to') +
                        '</div>';
                }
                if (key === 'LargeTrickplay') {
                    return '<div class="mdFieldGrid mdFieldGrid-2">' +
                        '<div class="mdField" style="grid-column:1/-1;">' +
                            '<label for="mdTrickplayMinSize" title="Trickplay folders smaller than this threshold aren\'t flagged. Prevents libraries with many short clips from producing thousands of low-savings issues. 0 = flag every convertible folder.">Only flag trickplay folders above <span class="mdSliderVal" id="mdTrickplayMinSizeVal"></span></label>' +
                            '<input id="mdTrickplayMinSize" type="range" min="0" max="500" step="1" />' +
                            '<span class="mdFieldHint">Filters out tiny per-clip trickplay folders. Default 10 MB catches movies and series while ignoring short-video libraries. Set to 0 to flag every convertible folder.</span>' +
                        '</div>' +
                        '<div class="mdField" style="grid-column:1/-1;">' +
                            '<label for="mdTrickplayQuality" title="libwebp quality 40-95. 80 lands where compression artefacts stop mattering on scrub-bar thumbnails; 75 gives more space but occasional soft edges on high-contrast text; 85+ trades size back for pixel-perfect zoom.">WebP quality <span class="mdSliderVal" id="mdTrickplayQualityVal">80%</span></label>' +
                            '<input id="mdTrickplayQuality" type="range" min="40" max="95" step="1" />' +
                            '<span class="mdFieldHint">Quality 80 halves the trickplay folder without visible change on scrub thumbnails. Lower saves more disk at the cost of soft edges; higher gives up space for pixel-perfect zoom nobody will do.</span>' +
                        '</div>' +
                        '</div>' +
                        '<span class="mdFieldHint">Only touches images inside Jellyfin\'s own trickplay data folder — your library files are never modified. The .jpg extension is preserved so clients keep serving them unchanged.</span>';
                }
                if (key === 'Stale') {
                    return '<div class="mdFieldGrid mdFieldGrid-2">' +
                        '<div class="mdField">' +
                            '<label for="mdStaleThresholdDays" title="Both conditions must hold before something is flagged as stale: the item has been on the server this long AND nobody has played it in the same window. Fresh imports never look stale on their first scan.">Flag media nobody has played in (days)</label>' +
                            '<input id="mdStaleThresholdDays" type="number" min="1" max="3650" is="emby-input" />' +
                            '<span class="mdFieldHint">Item must also have been on the server this long, so fresh imports don\'t immediately flag.</span>' +
                        '</div>' +
                        disposalField('Stale', 'Deleted stale files go to') +
                        '<div class="mdField">' +
                            '<label for="mdStaleExcludedGenres" title="Media tagged with any of these genres is skipped by the Stale scanner. Draws from the genres actually present in your libraries — start typing to see suggestions.">Skip these genres</label>' +
                            '<input id="mdStaleExcludedGenres" type="text" is="emby-input" list="mdGenreList" placeholder="Documentary, Christmas" />' +
                            '<span class="mdFieldHint">Case-insensitive match against Jellyfin genres.</span>' +
                        '</div>' +
                        '<div class="mdField" style="grid-column:1/-1;">' +
                            '<label>Skip these libraries entirely</label>' +
                            '<div id="mdStaleExcludedLibs" class="mdFieldHint">Loading libraries…</div>' +
                        '</div>' +
                        '</div>' +
                        '<span class="mdFieldHint">Approval-only — Automatic is deliberately not offered. Each stale item needs an explicit Approve on the Issues tab before the file is deleted.</span>';
                }
                if (key === 'FormatCompatibility') {
                    return '<div class="mdFieldGrid mdFieldGrid-2">' +
                        disposalField('FormatCompatibility', 'Converted originals go to') +
                        '<div class="mdField">' +
                            '<label for="mdEbookPreferredTarget" title="Target format for ebook conversion. EPUB is Jellyfin\'s native format; PDF preserves complex layouts at the cost of reflow.">Preferred ebook target</label>' +
                            '<select is="emby-select" id="mdEbookPreferredTarget" class="emby-select-withcolor emby-select">' +
                                '<option value="">Let Calibre decide</option>' +
                                '<option value="epub">EPUB (recommended)</option>' +
                                '<option value="pdf">PDF</option>' +
                            '</select>' +
                        '</div>' +
                        '</div>' +
                        '<label class="mdInsetRow" style="cursor:pointer;" title="Convert MOBI, AZW3 and DjVu files to EPUB or PDF using Calibre. Originals go to the recycle bin.">' +
                            '<input id="mdConvertEbooks" type="checkbox" class="mdInsetCheck" />' +
                            '<span style="font-size:13px;font-weight:600;flex:0 0 auto;">Convert ebooks (MOBI, AZW3, DjVu → EPUB/PDF)</span>' +
                            '<span class="mdFieldHint" style="margin:0;flex:1;">Requires Calibre (ebook-convert). Leave the path below empty to use the system PATH or let MediaDash auto-download on first use.</span>' +
                        '</label>' +
                        '<label class="mdInsetRow" style="cursor:pointer;" title="Convert CBR and CB7 comic archives to CBZ. Originals go to the recycle bin.">' +
                            '<input id="mdConvertComics" type="checkbox" class="mdInsetCheck" />' +
                            '<span style="font-size:13px;font-weight:600;flex:0 0 auto;">Convert comics (CBR, CB7 → CBZ)</span>' +
                            '<span class="mdFieldHint" style="margin:0;flex:1;">No extra tool needed — repacked in-process.</span>' +
                        '</label>' +
                        '<label class="mdInsetRow" style="cursor:pointer;" title="Convert AAX/AA audiobooks to M4B; detect-only for DRM-protected AAX. Numbered folders of MP3s can be merged into a single M4B. Originals go to the recycle bin.">' +
                            '<input id="mdConvertAudiobooks" type="checkbox" class="mdInsetCheck" />' +
                            '<span style="font-size:13px;font-weight:600;flex:0 0 auto;">Convert audiobooks (AAX/AA detect-only; multi-file folders can merge)</span>' +
                            '<span class="mdFieldHint" style="margin:0;flex:1;">AAX/AA files are detected but not converted (DRM-protected). Folder-of-MP3s audiobooks can be merged into a single M4B if enabled below.</span>' +
                        '</label>' +
                        '<label class="mdInsetRow" style="cursor:pointer;margin-left:1.5em;" title="When Convert audiobooks is enabled, automatically merge numbered folders of MP3s into a single M4B file.">' +
                            '<input id="mdAudiobookMergeMultiFile" type="checkbox" class="mdInsetCheck" />' +
                            '<span style="font-size:13px;font-weight:600;flex:0 0 auto;">Merge numbered folder-of-MP3s audiobooks into a single M4B</span>' +
                            '<span class="mdFieldHint" style="margin:0;flex:1;">Consolidates 01.mp3, 02.mp3, etc. into one audiobook. Requires Convert audiobooks to be on.</span>' +
                        '</label>' +
                        '<label class="mdInsetRow" style="cursor:pointer;" title="Convert HEIC, HEIF, TIFF, BMP, and camera RAW images to JPG via ffmpeg. Originals go to the recycle bin.">' +
                            '<input id="mdConvertImages" type="checkbox" class="mdInsetCheck" />' +
                            '<span style="font-size:13px;font-weight:600;flex:0 0 auto;">Convert images (HEIC, HEIF, TIFF, RAW → JPG)</span>' +
                            '<span class="mdFieldHint" style="margin:0;flex:1;">Requires ffmpeg with image support. RAW formats require a build with libraw.</span>' +
                        '</label>' +
                        '<label class="mdInsetRow" style="cursor:pointer;" title="Convert tracker and legacy lossless formats to FLAC or MP3 via ffmpeg. Originals go to the recycle bin.">' +
                            '<input id="mdConvertMusic" type="checkbox" class="mdInsetCheck" />' +
                            '<span style="font-size:13px;font-weight:600;flex:0 0 auto;">Convert music (tracker + legacy lossless → FLAC/MP3)</span>' +
                            '<span class="mdFieldHint" style="margin:0;flex:1;">Trackers and lossless (.ape, .wv, .tak, .tta) → FLAC. WMA → MP3.</span>' +
                        '</label>' +
                        '<label class="mdInsetRow" style="cursor:pointer;" title="Convert RMVB and other legacy video formats to MKV via ffmpeg. Originals go to the recycle bin.">' +
                            '<input id="mdConvertVideos" type="checkbox" class="mdInsetCheck" />' +
                            '<span style="font-size:13px;font-weight:600;flex:0 0 auto;">Convert videos (RMVB → MKV)</span>' +
                            '<span class="mdFieldHint" style="margin:0;flex:1;">Requires ffmpeg.</span>' +
                        '</label>' +
                        '<div class="mdField" style="margin-top:.6em;">' +
                            '<label for="mdCalibreOverridePath" title="Full path to the ebook-convert binary. Leave empty to use the system PATH or let MediaDash auto-download Calibre on first fix.">Calibre override path</label>' +
                            '<input id="mdCalibreOverridePath" type="text" is="emby-input" placeholder="/usr/bin/ebook-convert or C:\\Program Files\\Calibre2\\ebook-convert.exe" />' +
                            '<span class="mdFieldHint">Leave empty to use system PATH or auto-download on first fix.</span>' +
                        '</div>';
                }
                return '';
            }


            function buildTaskCard(t) {
                var meta = deps.STR.types[t.key];
                var tooltip = TASK_TOOLTIPS[t.key] || '';
                return '<section class="mdTask" id="mdSet-task-' + t.key + '" data-key="' + t.key + '" ' +
                    'style="--md-accent:' + meta.color + ';--md-accent-tint:' + TASK_TINT[t.key] + ';--md-accent-ink:' + TASK_INKS[t.key] + ';--md-accent-border:' + TASK_ON_BORDER[t.key] + ';">' +
                    '<div class="mdTaskAccent"></div>' +
                    '<header class="mdTaskHead" data-role="head"' + (tooltip ? ' title="' + deps.esc(tooltip) + '"' : '') + '>' +
                        '<span class="mdTaskIcon"><span class="material-icons">' + meta.icon + '</span></span>' +
                        '<span class="mdTaskLabel">' +
                            '<span class="mdTaskTitle">' + deps.esc(meta.title) + '</span>' +
                            '<span class="mdTaskSub" data-role="summary">' + deps.esc(meta.hint || '') + '</span>' +
                        '</span>' +
                        modeSegments(t.key) +
                        '<span class="material-icons mdTaskChevron">expand_less</span>' +
                    '</header>' +
                    '<div class="mdTaskBody">' + taskBody(t.key) + '</div>' +
                    '</section>';
            }


            // Named `buildFixTypeRows` to preserve the existing init-time call site. Renders the
            // task list into the container, then wires up per-card interactions and the chip rail.
            function buildFixTypeRows() {
                var html = TASK_ORDER.map(function (key) { return buildTaskCard(findFixType(key)); }).join('');
                deps.view.querySelector('#mdFixTypeRows').innerHTML = html;
                // Nav chips first so #mdTaskCollapseAll exists before wireTaskCards binds its click.
                buildNavChips();
                wireTaskCards();
            }


            function wireTaskCards() {
                deps.view.querySelectorAll('.mdTask').forEach(function (card) {
                    var key = card.getAttribute('data-key');
                    var head = card.querySelector('[data-role=head]');
                    head.addEventListener('click', function (e) {
                        if (e.target.closest('.mdTaskModes')) return;
                        if (e.target.closest('button')) return;
                        toggleTaskCollapsed(key);
                    });
                    card.querySelectorAll('.mdTaskModes button').forEach(function (btn) {
                        btn.addEventListener('click', function (e) {
                            e.stopPropagation();
                            setTaskMode(key, btn.getAttribute('data-mode'));
                        });
                    });
                });
                deps.view.querySelectorAll('.mdChipInput').forEach(wireChipInput);
                wireLangPresets();
                wireSetCardCollapse();
                var collapseAll = deps.view.querySelector('#mdTaskCollapseAll');
                if (collapseAll) collapseAll.addEventListener('click', function () {
                    // Count only cards that CAN be toggled — Off-mode tasks are force-collapsed by
                    // applyCollapse regardless of setCollapseState, so they don't participate in the
                    // Collapse-all / Expand-all state machine. This keeps the button truthful when a
                    // user has some tasks set to Off (previously the label could read "Expand all"
                    // even though clicking it wouldn't visibly expand anything).
                    var togglableTasks = TASK_ORDER.filter(function (k) {
                        var card = deps.view.querySelector('.mdTask[data-key="' + k + '"]');
                        return card && !card.classList.contains('mdTaskOff');
                    });
                    var anyExpanded = false;
                    togglableTasks.forEach(function (k) {
                        var card = deps.view.querySelector('.mdTask[data-key="' + k + '"]');
                        if (card && !card.classList.contains('mdTaskCollapsed')) anyExpanded = true;
                    });
                    deps.view.querySelectorAll('.mdSetCard--collapsible').forEach(function (card) {
                        if (!card.classList.contains('mdSetCardCollapsed')) anyExpanded = true;
                    });
                    togglableTasks.forEach(function (k) { setCollapseState(k, anyExpanded); });
                    TASK_ORDER.forEach(applyCollapse);
                    deps.view.querySelectorAll('.mdSetCard--collapsible').forEach(function (card) {
                        setSetCardCollapsed(card, anyExpanded);
                    });
                    updateCollapseAllLabel();
                });
                updateCollapseAllLabel();
            }


            function setSetCardCollapsed(card, collapsed) {
                card.classList.toggle('mdSetCardCollapsed', collapsed);
                try { localStorage.setItem('mdSetCollapse:' + card.id, collapsed ? '1' : '0'); } catch (e) {}
            }


            function wireSetCardCollapse() {
                deps.view.querySelectorAll('.mdSetCard--collapsible').forEach(function (card) {
                    var stored = '0';
                    try { stored = localStorage.getItem('mdSetCollapse:' + card.id) || '0'; } catch (e) {}
                    card.classList.toggle('mdSetCardCollapsed', stored === '1');
                    var head = card.querySelector('.mdSetHead');
                    if (head) {
                        head.addEventListener('click', function () {
                            setSetCardCollapsed(card, !card.classList.contains('mdSetCardCollapsed'));
                            updateCollapseAllLabel();
                        });
                    }
                });
            }


            function updateCollapseAllLabel() {
                var btn = deps.view.querySelector('#mdTaskCollapseAll');
                if (!btn) return;
                // Match the click-handler's togglable-only set so the label reflects what a click will
                // actually do. Off-mode tasks are force-collapsed and can't be expanded — excluding them
                // avoids the state where the label promises "Expand all" but nothing visible changes.
                var anyExpanded = false;
                var anyTogglable = false;
                TASK_ORDER.forEach(function (k) {
                    var card = deps.view.querySelector('.mdTask[data-key="' + k + '"]');
                    if (!card || card.classList.contains('mdTaskOff')) return;
                    anyTogglable = true;
                    if (!card.classList.contains('mdTaskCollapsed')) anyExpanded = true;
                });
                deps.view.querySelectorAll('.mdSetCard--collapsible').forEach(function (card) {
                    anyTogglable = true;
                    if (!card.classList.contains('mdSetCardCollapsed')) anyExpanded = true;
                });
                if (!anyTogglable) {
                    btn.style.display = 'none';
                    return;
                }

                btn.style.display = '';
                btn.textContent = anyExpanded ? 'Collapse all' : 'Expand all';
            }


            function setTaskMode(key, newMode) {
                var hidden = deps.view.querySelector('#mdMode' + key);
                if (!hidden) return;
                var oldMode = hidden.value;
                if (newMode === oldMode) return;
                if (newMode === 'Automatic' && !window.confirm(deps.STR.confirmAuto)) return;
                hidden.value = newMode;
                markSettingsDirty();
                syncModeSegments(key);
                applyTaskState(key);
                updateActiveCount();
                updateNavChipStates();
                updateSummary(key);
                if (newMode === 'Off') setCollapseState(key, true);
                else if (oldMode === 'Off') setCollapseState(key, false);
                applyCollapse(key);
                updateCollapseAllLabel();
            }


            function syncModeSegments(key) {
                var el = deps.view.querySelector('#mdMode' + key);
                if (!el) return;
                var mode = el.value;
                deps.view.querySelectorAll('.mdTask[data-key="' + key + '"] .mdTaskModes button').forEach(function (btn) {
                    btn.classList.toggle('mdTaskModeActive', btn.getAttribute('data-mode') === mode);
                });
            }


            function applyTaskState(key) {
                var card = deps.view.querySelector('.mdTask[data-key="' + key + '"]');
                if (!card) return;
                var el = deps.view.querySelector('#mdMode' + key);
                var mode = el ? el.value : 'Off';
                card.classList.toggle('mdTaskOff', mode === 'Off');
                card.classList.toggle('mdTaskOn', mode !== 'Off');
                // Disposal only takes effect when fixes actually run. Off leaves the file alone
                // entirely, so grey the disposal select to reflect that it's not doing anything.
                var disp = deps.view.querySelector('#mdDisposal' + key);
                if (disp) {
                    var inert = mode === 'Off';
                    disp.disabled = inert;
                    disp.style.opacity = inert ? '.45' : '';
                    disp.title = inert ? 'This only applies once fixes run — switch to Ask me first or Automatic.' : '';
                }
            }


            function toggleTaskCollapsed(key) {
                var card = deps.view.querySelector('.mdTask[data-key="' + key + '"]');
                if (!card || card.classList.contains('mdTaskOff')) return;
                setCollapseState(key, !card.classList.contains('mdTaskCollapsed'));
                applyCollapse(key);
                updateCollapseAllLabel();
            }


            function applyCollapse(key) {
                var card = deps.view.querySelector('.mdTask[data-key="' + key + '"]');
                if (!card) return;
                var el = deps.view.querySelector('#mdMode' + key);
                var isOff = el && el.value === 'Off';
                card.classList.toggle('mdTaskCollapsed', isOff || getCollapseState(key));
            }


            function applyAllCollapse() { TASK_ORDER.forEach(applyCollapse); }


            function getCollapseState(key) {
                try { return localStorage.getItem('mdCollapse:' + key) === '1'; } catch (e) { return false; }
            }

            function setCollapseState(key, collapsed) {
                try { localStorage.setItem('mdCollapse:' + key, collapsed ? '1' : '0'); } catch (e) {}
            }


            function updateActiveCount() {
                var active = 0;
                TASK_ORDER.forEach(function (k) {
                    var el = deps.view.querySelector('#mdMode' + k);
                    if (el && el.value !== 'Off') active++;
                });
                var el = deps.view.querySelector('#mdTaskActiveCount');
                // Total is derived from TASK_ORDER length so the pill stays truthful as new task types
                // are added (previously read a hardcoded "of 8" then "of 15" which drifted from reality).
                if (el) el.textContent = active + ' of ' + TASK_ORDER.length + ' active';
            }


            var _langNames = null;

            var _langNamesCulturesLen = 0;

            function langNameMap() {
                // Rebuild if cultures arrived after the first call — STR.languages alone only covers
                // ~27 codes, but the datalist offers every Jellyfin culture. Chip commit compares
                // against this map, so anything outside it (e.g. "afr") gets silently rejected.
                var culturesLen = (mdCulturesCache || []).length;
                if (_langNames && culturesLen === _langNamesCulturesLen) return _langNames;
                _langNames = {};
                (deps.STR.languages || []).forEach(function (p) { _langNames[p[0]] = p[1]; });
                (mdCulturesCache || []).forEach(function (c) {
                    var code3 = c.ThreeLetterISOLanguageName || '';
                    var code2 = c.TwoLetterISOLanguageName || '';
                    var name = c.DisplayName || c.Name || code3 || code2;
                    if (code3 && !_langNames[code3]) _langNames[code3] = name;
                    if (code2 && !_langNames[code2]) _langNames[code2] = name;
                });
                _langNamesCulturesLen = culturesLen;
                return _langNames;
            }


            function updateSummary(key) {
                var card = deps.view.querySelector('.mdTask[data-key="' + key + '"]');
                if (!card) return;
                var sub = card.querySelector('[data-role=summary]');
                if (!sub) return;
                var el = deps.view.querySelector('#mdMode' + key);
                var mode = el ? el.value : 'Off';
                if (mode === 'Off') {
                    sub.textContent = offSummaryFor(key);
                    return;
                }
                sub.textContent = activeSummaryFor(key, mode) || (deps.STR.types[key].hint || '');
            }


            function updateAllSummaries() { TASK_ORDER.forEach(updateSummary); }


            function offSummaryFor(key) {
                if (key === 'Misplaced') return 'Off — turn on to set the Movies, TV and Anime target folders.';
                if (key === 'MissingSubtitles') return 'Off — downloads use Jellyfin\'s own subtitle providers.';
                if (key === 'Stale') return 'Off — turn on to flag anything unplayed for a while.';
                return 'Off — this task is disabled.';
            }


            function modeLabelFor(mode) {
                if (mode === 'ManualApprove') return 'Ask me first';
                if (mode === 'Automatic') return 'Automatic';
                return 'On';
            }


            function langSummary(codes) {
                if (!codes.length) return 'no languages set';
                var names = codes.map(function (c) { return langNameMap()[c] || c; });
                if (names.length === 1) return names[0];
                if (names.length === 2) return names.join(' and ');
                return names.slice(0, 2).join(', ') + ' + ' + (names.length - 2) + ' more';
            }


            function activeSummaryFor(key, mode) {
                var m = modeLabelFor(mode);
                if (key === 'Duplicate') {
                    var kp = deps.view.querySelector('#mdKeeperPolicy');
                    var kpLabel = kp && kp.value === 'codec' ? 'newest codec' : kp && kp.value === 'size' ? 'smallest file' : 'best-quality copy';
                    return m + ' — keep the ' + kpLabel + '.';
                }
                if (key === 'Playability') {
                    var thorough = deps.view.querySelector('#mdThorough');
                    return (thorough && thorough.checked ? 'Thorough check · ' : 'Sampled check · ') + 'broken files go to the recycle bin';
                }
                if (key === 'Quality') {
                    var res = deps.view.querySelector('#mdMaxRes');
                    var codec = deps.view.querySelector('#mdCodec');
                    return m + ' — re-encode to ' + (codec ? codec.value.toUpperCase() : 'HEVC') + ' at ' + (res ? res.value + 'p' : 'ceiling');
                }
                if (key === 'SubtitleLanguage' || key === 'AudioLanguage' || key === 'MissingSubtitles') {
                    var id = key === 'AudioLanguage' ? '#mdAudioLangs' : '#mdSubLangs';
                    var el = deps.view.querySelector(id);
                    var codes = ((el && el.value) || '').split(',').map(function (s) { return s.trim(); }).filter(Boolean);
                    if (codes.length === 0) return m + ' — no languages picked, so nothing will be touched.';
                    return 'Keeping ' + langSummary(codes) + ' · ' + m.toLowerCase();
                }
                if (key === 'Stale') {
                    var d = deps.view.querySelector('#mdStaleThresholdDays');
                    return 'Flagging anything unplayed for ' + (d ? d.value : '365') + ' days · never deleted automatically';
                }
                if (key === 'Misplaced') {
                    var mv = deps.view.querySelector('#mdMoviesTarget');
                    var tv = deps.view.querySelector('#mdTvTarget');
                    var hasBoth = mv && mv.value && tv && tv.value;
                    return m + (hasBoth ? ' — sorting into your Movies / TV folders' : ' — folders not set yet');
                }
                return m;
            }


            // Jump-control replaces the wrapping chip row. One 46px control with the current
            // section on the left + "Jump to a section…" hint + chevron; a mono count pill; a
            // Collapse-all button. Popover has a filter field and 19 rows in a two-column grid
            // grouped CLEANUP TASKS + GLOBAL + STORAGE. Selecting a row scrolls the target into
            // view and sets it as the control's left segment.
            var jumpCurrentSection = null;
 // href of the currently-anchored section, e.g. '#mdSet-safety'

            var JUMP_SECTIONS = [
                { group: 'GLOBAL',        label: 'Safety',       icon: 'shield',           color: '#22d3ee', href: '#mdSet-safety' },
                { group: 'STORAGE',       label: 'Libraries',    icon: 'video_library',    color: '#06b6d4', href: '#mdSet-libraries' },
                { group: 'STORAGE',       label: 'Recycle bin',  icon: 'delete',           color: '#ec4899', href: '#mdSet-recycle' },
                { group: 'GLOBAL',        label: 'Protected',    icon: 'block',            color: '#f97316', href: '#mdSet-protection' },
                { group: 'GLOBAL',        label: 'Maintenance',  icon: 'handyman',         color: '#94a3b8', href: '#mdSet-maintenance' }
            ];


            function buildNavChips() {
                var bar = deps.view.querySelector('#mdJumpBar');
                if (!bar) return;

                // Compose task rows in TASK_ORDER, then the four non-task sections.
                var taskRows = TASK_ORDER.map(function (key) {
                    var meta = deps.STR.types[key] || { title: key, color: '#888', icon: 'help' };
                    return { group: 'CLEANUP TASKS', taskKey: key, label: shortTaskLabel(key), icon: meta.icon, color: meta.color, href: '#mdSet-task-' + key };
                });
                var rows = taskRows.concat(JUMP_SECTIONS);

                // Default the current-section anchor to the first task on first render.
                if (!jumpCurrentSection) jumpCurrentSection = rows[0].href;
                var current = rows.filter(function (r) { return r.href === jumpCurrentSection; })[0] || rows[0];

                bar.innerHTML =
                    '<div class="mdJumpControl" id="mdJumpControl" tabindex="0" style="--md-current-accent:' + current.color + ';">' +
                        '<div class="mdJumpCurrent"><span class="material-icons">' + current.icon + '</span><span>' + deps.esc(current.label) + '</span></div>' +
                        '<div class="mdJumpHint">Jump to a section…</div>' +
                        '<div class="mdJumpChevron"><span class="material-icons">expand_more</span></div>' +
                        '<div class="mdJumpPopover" id="mdJumpPopover" role="dialog" aria-label="Jump to section">' +
                            '<div class="mdJumpFilterWrap">' +
                                '<input type="search" class="mdJumpFilter" id="mdJumpFilter" placeholder="Filter sections…" autocomplete="off" />' +
                                '<span class="mdJumpFilterEsc">esc</span>' +
                            '</div>' +
                            '<div class="mdJumpGroups" id="mdJumpGroups"></div>' +
                        '</div>' +
                    '</div>' +
                    '<span class="mdTaskPill" id="mdTaskActiveCount" style="cursor:default;">— of ' + TASK_ORDER.length + ' active</span>' +
                    '<button type="button" class="mdJumpCollapse" id="mdTaskCollapseAll">Collapse all</button>';

                renderJumpRows(rows, '');
                wireJumpControl(rows);
                updateNavChipStates();
            }


            function renderJumpRows(rows, filterText) {
                var groups = deps.view.querySelector('#mdJumpGroups');
                if (!groups) return;
                var q = (filterText || '').trim().toLowerCase();
                var visible = q ? rows.filter(function (r) { return r.label.toLowerCase().indexOf(q) >= 0; }) : rows;
                // Split into two columns (task list is split 8/7).
                var tasks = visible.filter(function (r) { return r.group === 'CLEANUP TASKS'; });
                var global = visible.filter(function (r) { return r.group === 'GLOBAL'; });
                var storage = visible.filter(function (r) { return r.group === 'STORAGE'; });

                var html = '';
                if (tasks.length) html += '<div class="mdJumpGroupLabel">Cleanup tasks</div>';
                tasks.forEach(function (r) { html += jumpRowHtml(r); });
                if (global.length || storage.length) html += '<div class="mdJumpDivider"></div>';
                if (global.length)  html += '<div class="mdJumpGroupLabel">Global</div>';
                global.forEach(function (r) { html += jumpRowHtml(r); });
                if (storage.length) html += '<div class="mdJumpGroupLabel">Storage</div>';
                storage.forEach(function (r) { html += jumpRowHtml(r); });
                if (!visible.length) html = '<div class="mdJumpRow" style="cursor:default;color:var(--md-meta);"><span>No sections match.</span></div>';
                groups.innerHTML = html;
            }


            function jumpRowHtml(r) {
                var isCurrent = r.href === jumpCurrentSection;
                var attr = r.taskKey ? ' data-task-chip="' + r.taskKey + '"' : '';
                // The right-aligned mode tag is only meaningful for task rows.
                var modeTag = '';
                if (r.taskKey) {
                    var mode = (deps.view.querySelector('#mdMode' + r.taskKey) || {}).value || 'Off';
                    var short = { Off: 'off', ManualApprove: 'ask', Automatic: 'auto' }[mode] || mode.toLowerCase();
                    modeTag = '<span class="mdJumpRowMode">' + short + '</span>';
                }
                return '<a class="mdJumpRow mdSetChip' + (isCurrent ? ' mdJumpCurrentRow' : '') + '" href="' + r.href + '"' + attr +
                    ' style="--md-accent:' + r.color + ';">' +
                        '<span class="material-icons">' + r.icon + '</span>' +
                        '<span class="mdJumpRowLabel">' + deps.esc(r.label) + '</span>' +
                        modeTag +
                    '</a>';
            }


            var jumpDismiss = null;

            function wireJumpControl(rows) {
                var control = deps.view.querySelector('#mdJumpControl');
                var popover = deps.view.querySelector('#mdJumpPopover');
                var filter = deps.view.querySelector('#mdJumpFilter');
                if (!control || !popover || !filter) return;

                function open() {
                    popover.classList.add('mdJumpOpen');
                    deps.lifetime.timeout(function () { filter.focus(); filter.select(); }, 0);
                }
                function close() {
                    popover.classList.remove('mdJumpOpen');
                    filter.value = ''; renderJumpRows(rows, '');
                }
                control.addEventListener('click', function (e) {
                    if (e.target.closest('.mdJumpPopover')) return; // clicks inside popover handled below
                    if (popover.classList.contains('mdJumpOpen')) close(); else open();
                });
                filter.addEventListener('input', function () { renderJumpRows(rows, filter.value); });
                filter.addEventListener('keydown', function (e) {
                    if (e.key === 'Escape') { e.preventDefault(); close(); }
                });
                popover.addEventListener('click', function (e) {
                    var row = e.target.closest('.mdJumpRow');
                    if (!row || !row.getAttribute('href')) return;
                    e.preventDefault();
                    var href = row.getAttribute('href');
                    var target = deps.view.querySelector(href);
                    if (!target) return;
                    // Expand collapsed cards so clicking a row never lands the user on a hidden card.
                    if (target.classList.contains('mdSetCard--collapsible') && target.classList.contains('mdSetCardCollapsed')) {
                        setSetCardCollapsed(target, false);
                        updateCollapseAllLabel();
                    } else if (target.classList.contains('mdTask') && !target.classList.contains('mdTaskOff')) {
                        var k = target.getAttribute('data-key');
                        if (k) { setCollapseState(k, false); applyCollapse(k); updateCollapseAllLabel(); }
                    }
                    jumpCurrentSection = href;
                    target.scrollIntoView({ behavior: 'smooth', block: 'start' });
                    // Refresh the control's left segment + rows so the new current is reflected.
                    buildNavChips();
                    close();
                });
                jumpDismiss = function (e) {
                    if (!popover.classList.contains('mdJumpOpen') || control.contains(e.target)) return;
                    close();
                };
            }


            function updateNavChipStates() {
                // Refresh the per-task mode tags in the popover when a task's mode changes.
                deps.view.querySelectorAll('.mdJumpRow[data-task-chip]').forEach(function (row) {
                    var key = row.getAttribute('data-task-chip');
                    var el = deps.view.querySelector('#mdMode' + key);
                    var mode = (el && el.value) || 'Off';
                    var short = { Off: 'off', ManualApprove: 'ask', Automatic: 'auto' }[mode] || mode.toLowerCase();
                    var tag = row.querySelector('.mdJumpRowMode');
                    if (tag) tag.textContent = short;
                });
            }


            function wireChipInput(chipEl) {
                if (chipEl._wired) return; chipEl._wired = true;
                var hidden = chipEl.querySelector('input[type=hidden]');
                var render = chipEl.querySelector('.mdChipInputRender');
                var input = chipEl.querySelector('.mdChipInputText');
                function currentCodes() { return (hidden.value || '').split(',').map(function (s) { return s.trim(); }).filter(Boolean); }
                function redraw() {
                    // Render chips sorted alphabetically by display name (falling back to raw code
                    // for unknown entries) — insertion order is meaningless to the user and made a
                    // list of 40+ languages hard to scan for a specific one.
                    var names = langNameMap();
                    var codes = currentCodes().slice().sort(function (a, b) {
                        return (names[a] || a).localeCompare(names[b] || b, undefined, { sensitivity: 'base' });
                    });
                    render.innerHTML = codes.map(function (c) {
                        return '<span class="mdChip" data-code="' + deps.esc(c) + '">' + deps.esc(names[c] || c) +
                            '<button type="button" tabindex="-1" aria-label="Remove"><span class="material-icons">close</span></button></span>';
                    }).join('');
                    render.querySelectorAll('.mdChip button').forEach(function (b) {
                        b.addEventListener('click', function (e) {
                            e.stopPropagation();
                            var code = b.parentElement.getAttribute('data-code');
                            hidden.value = currentCodes().filter(function (c) { return c !== code; }).join(', ');
                            redraw();
                            notifyLangChange(hidden);
                        });
                    });
                }
                chipEl.addEventListener('click', function (e) {
                    if (!e.target.closest('.mdChip')) input.focus();
                });
                function commitCurrent() {
                    var v = input.value.trim().toLowerCase();
                    if (!v) return false;
                    var byCode = langNameMap();
                    var byName = {}; Object.keys(byCode).forEach(function (c) {
                        if (c.length === 3 || c === 'orig') byName[byCode[c].toLowerCase()] = c;
                    });
                    if (byName[v]) v = byName[v];
                    var culture = (mdCulturesCache || []).find(function (c) { return c.TwoLetterISOLanguageName === v; });
                    if (culture) v = culture.ThreeLetterISOLanguageName;
                    if (!byCode[v]) return false;
                    var codes = currentCodes();
                    if (codes.indexOf(v) === -1) codes.push(v);
                    hidden.value = codes.join(', ');
                    input.value = '';
                    redraw();
                    notifyLangChange(hidden);
                    return true;
                }
                input.addEventListener('keydown', function (e) {
                    if (e.key === 'Enter' || e.key === ',') {
                        e.preventDefault();
                        commitCurrent();
                    } else if (e.key === 'Backspace' && !input.value) {
                        var codes2 = currentCodes(); codes2.pop();
                        hidden.value = codes2.join(', ');
                        redraw();
                        notifyLangChange(hidden);
                    }
                });
                // A typed prefix can itself be another language: fi (Finnish) starts
                // Filipino and sl (Slovenian) starts Slovak. Wait for confirmation while
                // typing; dropdown replacements and pastes can commit the whole value.
                input.addEventListener('input', function (e) {
                    if (e && (e.isComposing || (e.inputType && e.inputType !== 'insertReplacementText' && e.inputType !== 'insertFromPaste'))) return;
                    commitCurrent();
                });
                input.addEventListener('change', function () { commitCurrent(); });
                input.addEventListener('blur', function () { commitCurrent(); });
                chipEl._redraw = redraw;
                redraw();
            }


            // Idempotent: safe to re-run when the settings tab re-renders. Uses a page-scoped delegated
            // listener so wizard buttons rendered before any task-card init still work, and dynamically
            // rendered task cards pick it up automatically.
            var _langPresetsBound = false;

            function wireLangPresets() {
                if (_langPresetsBound) return;
                _langPresetsBound = true;
                deps.view.addEventListener('click', function (e) {
                    var btn = e.target.closest && e.target.closest('.mdLangPresetBtn');
                    if (!btn) return;
                    e.preventDefault();
                    e.stopPropagation();
                    var row = btn.closest('.mdLangPresets');
                    if (!row) return;
                    var targetId = row.getAttribute('data-target');
                    var chip = deps.view.querySelector('.mdChipInput[data-chip-input="' + targetId + '"]');
                    var hidden = deps.view.querySelector('#' + targetId);
                    if (!chip || !hidden) return;
                    if (btn.getAttribute('data-preset') !== 'clear') return;
                    hidden.value = '';
                    if (chip._redraw) chip._redraw();
                    notifyLangChange(hidden);
                });
            }


            function notifyLangChange(hidden) {
                if (hidden && hidden.closest('#mdSettingsForm')) markSettingsDirty();
                updateSummary('SubtitleLanguage');
                updateSummary('AudioLanguage');
                updateSummary('MissingSubtitles');
            }


            function initTaskCardsAfterLoad() {
                TASK_ORDER.forEach(function (k) {
                    syncModeSegments(k);
                    applyTaskState(k);
                    updateSummary(k);
                    applyCollapse(k);
                });
                deps.view.querySelectorAll('.mdChipInput').forEach(function (c) { if (c._redraw) c._redraw(); });
                updateActiveCount();
                updateNavChipStates();
                updateQualityAutoStrip();
                var qualityFieldWatch = ['#mdKeeperPolicy', '#mdMaxRes', '#mdCodec', '#mdStaleThresholdDays', '#mdMoviesTarget', '#mdTvTarget', '#mdThorough'];
                qualityFieldWatch.forEach(function (sel) {
                    var el = deps.view.querySelector(sel);
                    if (el) el.addEventListener('change', function () { updateAllSummaries(); updateQualityAutoStrip(); });
                });
            }


            function updateQualityAutoStrip() {
                var strip = deps.view.querySelector('#mdQualityAutoStrip');
                var text = deps.view.querySelector('#mdQualityAutoStripText');
                if (!strip || !text) return;
                var mode = deps.view.querySelector('#mdModeQuality');
                var dry = deps.view.querySelector('#mdDryRun');
                if (mode && mode.value === 'Automatic') {
                    text.textContent = 'Automatic re-encodes run whenever the server has been idle for 15 minutes.' + (dry && dry.checked ? ' Dry-run is still on, so nothing is written yet.' : '');
                    strip.style.display = '';
                } else {
                    strip.style.display = 'none';
                }
            }


            function initSetNav() {
                var chips = Array.prototype.slice.call(deps.view.querySelectorAll('.mdSetChip'));
                chips.forEach(function (a) {
                    a.addEventListener('click', function (e) {
                        e.preventDefault();
                        var target = deps.view.querySelector(a.getAttribute('href'));
                        if (target) target.scrollIntoView({ behavior: 'smooth', block: 'start' });
                    });
                });
                if (!('IntersectionObserver' in window)) return;
                var sections = chips.map(function (c) { return deps.view.querySelector(c.getAttribute('href')); }).filter(Boolean);
                // ponytail: only-first-visible wins; per-chip toggle inside the callback
                var io = new IntersectionObserver(function (entries) {
                    entries.forEach(function (en) {
                        if (!en.isIntersecting) return;
                        var id = '#' + en.target.id;
                        chips.forEach(function (c) {
                            c.classList.toggle('mdSetChipActive', c.getAttribute('href') === id);
                        });
                    });
                }, { rootMargin: '-25% 0px -65% 0px', threshold: 0 });
                sections.forEach(function (s) { io.observe(s); });
            }


            function sliderLabelText(s, value) {
                return s.format ? s.format(value) : deps.fmt(deps.STR[s.unit], value);
            }


            function initSliders() {
                SLIDERS.forEach(function (s) {
                    var input = deps.view.querySelector('#' + s.id);
                    var label = deps.view.querySelector('#' + s.val);
                    input.addEventListener('input', function () {
                        if (s.id === 'mdMaxBitrate') bitrateSliderEdited = true;
                        label.textContent = sliderLabelText(s, input.value);
                    });
                });
            }


            function syncSliderLabels() {
                SLIDERS.forEach(function (s) {
                    var input = deps.view.querySelector('#' + s.id);
                    deps.view.querySelector('#' + s.val).textContent = sliderLabelText(s, input.value);
                });
            }


            // ---- Unsaved-settings guard --------------------------------------------------
            // Simple dirty-flag guard: any input/change inside the Settings form flips a boolean,
            // Save clears it, and `beforeunload` warns the user before a hard navigation (browser
            // close / refresh / URL-bar change). Doesn't try to intercept Jellyfin's SPA sidebar
            // routing — the plugin page's internal tabs share DOM state so intra-page nav preserves
            // pending changes anyway.
            var mdSettingsDirty = false;
            // UI-12: revision counter bumped on every user edit so a save completion only clears
            // the dirty flag if no further edits have arrived during the round-trip. Without this,
            // a save that lands after a mid-save edit would mark the form clean and the user's
            // newer change would be silently overwritten on the next hydration.
            var mdEditRevision = 0;


            function markSettingsDirty() {
                mdSettingsDirty = true;
                mdEditRevision++;
            }


            function markSettingsClean(revisionAtSaveStart) {
                // Only clear dirty if nothing else was edited while the save was in flight. When
                // called without a captured revision (loadSettings' own post-hydration cleanup),
                // treat as unconditional — hydration is already dirty-aware.
                if (revisionAtSaveStart == null || revisionAtSaveStart === mdEditRevision) {
                    mdSettingsDirty = false;
                }
            }


            function installUnsavedGuardOnce() {
                if (installUnsavedGuardOnce._installed) return;
                installUnsavedGuardOnce._installed = true;
                var form = deps.view.querySelector('#mdSettingsForm');
                if (form) {
                    form.addEventListener('input', markSettingsDirty);
                    form.addEventListener('change', markSettingsDirty);
                }

            }


            function activateSettings() {
                deps.ownGlobal(document, 'click', function (e) { if (jumpDismiss) jumpDismiss(e); });
                deps.ownGlobal(window, 'beforeunload', function (e) {
                    if (!mdSettingsDirty) return;
                    // Modern browsers ignore custom text and render their own generic prompt.
                    e.preventDefault();
                    e.returnValue = '';
                    return '';
                });
            }

            // The <option> font-family styles preview each label in its own face, but the collapsed
            // <select> value inherits the page font unless we push the stack up. Called on load and
            // on every change so the closed dropdown always previews the picked family.
            function syncSubForceFontFace() {
                var sel = deps.view.querySelector('#mdSubForceFont');
                if (!sel) return;
                var entry = SUBTITLE_FONTS.find(function (f) { return f.v === sel.value; });
                sel.style.fontFamily = entry ? entry.stack : 'inherit';
            }


            function loadLibraries(config) {
                return deps.api('Libraries').then(function (folders) {
                    var el = deps.view.querySelector('#mdLibraries');
                    var html = '';
                    var firstMovies = null, firstTv = null;
                    // Show every library type the plugin can scan (movies, TV, music, audiobooks, books,
                    // comics, mixed content). The old movies-and-TV-only filter dated from before v0.9
                    // when scanners only understood video items — every scanner has since been widened.
                    folders.forEach(function (f) {
                        if (f.CollectionType === 'movies' && !firstMovies) firstMovies = (f.Locations || [])[0] || null;
                        if (f.CollectionType === 'tvshows' && !firstTv) firstTv = (f.Locations || [])[0] || null;
                        var checked = (config.EnabledLibraries || []).indexOf(f.ItemId) >= 0 ? ' checked' : '';
                        var typeLabel = f.CollectionType ? ' <span style="opacity:.55;font-size:.85em;">(' + deps.esc(f.CollectionType) + ')</span>' : '';
                        // Inline access-result slot populated by the "Test read / write access" button.
                        // Matched by library name so aggregate-by-name grouping stays trivial on the client.
                        html += '<div class="checkboxContainer" style="display:flex;align-items:center;gap:.6em;"><label class="emby-checkbox-label" style="flex:1;">' +
                            '<input type="checkbox" is="emby-checkbox" class="mdLib" data-id="' + deps.esc(f.ItemId) + '"' + checked + ' />' +
                            '<span>' + deps.esc(f.Name) + typeLabel + '</span></label>' +
                            '<span class="mdLibAccessSlot" data-lib="' + deps.esc(f.Name) + '" style="font-size:.85em;flex:none;"></span>' +
                            '</div>';
                    });
                    el.innerHTML = html;
                    var mv = deps.view.querySelector('#mdMoviesTarget');
                    var tv = deps.view.querySelector('#mdTvTarget');
                    if (mv && !mv.value && firstMovies) mv.placeholder = firstMovies;
                    if (tv && !tv.value && firstTv) tv.placeholder = firstTv;
                    populateLibShortcuts(folders);
                });
            }


            // Fill every <div class="mdLibShortcuts"> with clickable path chips for libraries whose
            // CollectionType matches data-types. Clicking a chip sets the paired input's value.
            function populateLibShortcuts(folders) {
                deps.view.querySelectorAll('.mdLibShortcuts').forEach(function (row) {
                    var targetId = row.getAttribute('data-target');
                    var input = deps.view.querySelector('#' + targetId);
                    if (!input) return;
                    var wanted = (row.getAttribute('data-types') || '').split(',').filter(Boolean);
                    var entries = [];
                    folders.forEach(function (f) {
                        if (wanted.length && wanted.indexOf(f.CollectionType) === -1) return;
                        (f.Locations || []).forEach(function (p) { entries.push({ name: f.Name, path: p }); });
                    });
                    if (!entries.length) { row.innerHTML = ''; return; }
                    row.innerHTML = '<span class="mdLibShortcutsLabel">Or fill from a library:</span>' +
                        entries.map(function (e) {
                            return '<button type="button" class="mdLibShortcut" title="' + deps.esc(e.path) + '" data-path="' + deps.esc(e.path) + '">' + deps.esc(e.name) + '</button>';
                        }).join('');
                    row.querySelectorAll('.mdLibShortcut').forEach(function (btn) {
                        btn.addEventListener('click', function () {
                            input.value = btn.getAttribute('data-path');
                            input.dispatchEvent(new Event('change', { bubbles: true }));
                        });
                    });
                });
            }


            // Build the 48 half-hour slots (00:00, 00:30, …, 23:30) plus a leading "Any time" option
            // whose value is empty — the backend treats empty start OR end as "no window". Idempotent:
            // populates once per select, further calls no-op.
            function populateFixWindowSelects() {
                ['#mdFixWindowStart', '#mdFixWindowEnd'].forEach(function (sel) {
                    var el = deps.view.querySelector(sel);
                    if (!el || el.options.length > 0) return;
                    var html = '<option value="">Any time</option>';
                    for (var h = 0; h < 24; h++) {
                        for (var m = 0; m < 60; m += 30) {
                            var v = (h < 10 ? '0' : '') + h + ':' + (m === 0 ? '00' : '30');
                            html += '<option value="' + v + '">' + v + '</option>';
                        }
                    }
                    el.innerHTML = html;
                });
            }


            function keeperPresetFromConfig(order) {
                var joined = (order || []).join(',');
                var match = Object.keys(KEEPER_PRESETS).find(function (k) { return KEEPER_PRESETS[k].join(',') === joined; });
                return match || 'quality';
            }


            // Populates the language datalist from the curated list shared with the chip picker.
            var mdLangCache = null;

            var mdCulturesCache = null;

            // ISO 639-1 codes are not prefixes of ISO 639-2: Swedish swe is sv,
            // while sw means Swahili. Filipino has no ISO 639-1 code.
            var languageIso2 = {
                sqi: 'sq', ara: 'ar', ben: 'bn', bul: 'bg', zho: 'zh', hrv: 'hr',
                ces: 'cs', dan: 'da', nld: 'nl', eng: 'en', est: 'et', fin: 'fi',
                fra: 'fr', deu: 'de', ell: 'el', heb: 'he', hin: 'hi', hun: 'hu',
                isl: 'is', ind: 'id', ita: 'it', jpn: 'ja', kor: 'ko', lav: 'lv',
                lit: 'lt', msa: 'ms', nor: 'no', fas: 'fa', pol: 'pl', por: 'pt',
                pan: 'pa', ron: 'ro', rus: 'ru', srp: 'sr', slk: 'sk', slv: 'sl',
                spa: 'es', swe: 'sv', tam: 'ta', tha: 'th', tur: 'tr', ukr: 'uk',
                urd: 'ur', vie: 'vi', cym: 'cy'
            };

            function populateLanguageDatalist() {
                // Core language shortlist only — the picker offers the same codes that STR.languages
                // knows how to commit as chips. Larger lists (Jellyfin's full culture set) let users
                // pick codes the chip input silently rejects.
                var list = deps.view.querySelector('#mdLangList');
                mdCulturesCache = (deps.STR.languages || []).map(function (p) {
                    return { ThreeLetterISOLanguageName: p[0], TwoLetterISOLanguageName: languageIso2[p[0]] || '', DisplayName: p[1], Name: p[1] };
                });
                if (list) {
                    list.innerHTML = (deps.STR.languages || []).map(function (p) {
                        var label = p[1] + ' (' + p[0] + ')';
                        return '<option value="' + p[0] + '" label="' + label + '">' + label + '</option>';
                    }).join('');
                }
                return Promise.resolve(mdCulturesCache);
            }


            // Maps the browser/UI locale (e.g. "en-US" → "en") to the 3-letter ISO code Jellyfin stores
            // in AllowedAudioLanguages / AllowedSubtitleLanguages (e.g. "eng"). Returns 'eng' if we can't map.
            function localeToIso3() {
                var loc = (deps.currentLocale() || 'en').toLowerCase().split(/[-_]/)[0];
                if (loc === 'nb' || loc === 'nn' || loc === 'nob' || loc === 'nno') loc = 'nor';
                var cultures = mdCulturesCache || [];
                var hit = cultures.find(function (c) {
                    return (c.TwoLetterISOLanguageName || '').toLowerCase() === loc || (c.ThreeLetterISOLanguageName || '').toLowerCase() === loc;
                });
                return (hit && (hit.ThreeLetterISOLanguageName || hit.TwoLetterISOLanguageName)) || 'eng';
            }


            // Populates <datalist id="mdGenreList"> with the genres actually present in the user's
            // libraries via GET /MediaDash/Genres. Refreshed on every settings-load because library
            // state changes as the user adds/removes media.
            function populateGenreDatalist() {
                var list = deps.view.querySelector('#mdGenreList');
                if (!list) return;
                deps.api('Genres').then(function (genres) {
                    list.innerHTML = (genres || []).map(function (g) {
                        return '<option value="' + deps.esc(g) + '"></option>';
                    }).join('');
                }, function () { /* Endpoint failure — datalist stays empty; freeform still works. */ });
            }


            // Pure-ish config → form hydration. Every field the Settings form owns is set
            // from `c` here; readFormIntoConfig(view, c) is the inverse. Non-hydration side
            // effects (network fetches, one-shot listener installs, dirty-flag reset, load
            // gate flip) live in the calling loadSettings so multiple loadSettings passes
            // (wizard finish, tab re-activation) don't multiply them.
            function applyConfigToForm(view, c) {
                view.querySelector('#mdKeeperPolicy').value = keeperPresetFromConfig(c.KeeperPolicyOrder);
                view.querySelector('#mdDryRun').checked = c.DryRun;
                view.querySelector('#mdPausePlayback').checked = c.PauseDuringPlayback;
                view.querySelector('#mdLowImpact').checked = !!c.LowSystemImpactMode;
                view.querySelector('#mdAnalytics').checked = !!c.AnalyticsEnabled;
                populateFixWindowSelects();
                view.querySelector('#mdFixWindowStart').value = c.FixWindowStart || '';
                view.querySelector('#mdFixWindowEnd').value = c.FixWindowEnd || '';
                FIX_TYPES.forEach(function (t) {
                    var mode = t.key === 'Duplicate' ? deps.sanitizeDuplicateModeOnLoad(c[t.mode]) : c[t.mode];
                    view.querySelector('#mdMode' + t.key).value = mode;
                    if (t.disposal) view.querySelector('#mdDisposal' + t.key).value = c[t.disposal];
                });
                view.querySelector('#mdStaleThresholdDays').value = c.StaleThresholdDays > 0 ? c.StaleThresholdDays : 365;
                view.querySelector('#mdHeavyTranscodeDays').value = c.HeavyTranscodeLookbackDays > 0 ? c.HeavyTranscodeLookbackDays : 30;
                view.querySelector('#mdCoverFilename').value = c.EmbeddedCoverFilename || 'cover.jpg';
                view.querySelector('#mdCoverStripAudio').checked = c.EmbeddedCoverStripFromAudio !== false;
                view.querySelector('#mdMaxRes').value = String(c.MaxResolutionHeight);
                setBitrateSliderForResolution(c.MaxBitrateMbpsAt1080p);
                view.querySelector('#mdCodec').value = c.PreferredCodec;
                view.querySelector('#mdContainer').value = c.TargetContainer;
                view.querySelector('#mdEncodePreset').value = c.SoftwareEncodePreset || 'Balanced';
                view.querySelector('#mdMinSize').value = c.MinScanFileSizeMb;
                view.querySelector('#mdSkipHdr').checked = c.SkipHdrContent;
                view.querySelector('#mdQualityScanAudiobooks').checked = !!c.QualityScanAudiobooks;
                populateEncoderOptions(c.UseHardwareEncoder, c.PreferredGpuIndex);
                view.querySelector('#mdFileTypes').value = (c.ReencodeFileTypes || []).join(', ');
                view.querySelector('#mdTolerance').value = c.QualityTolerancePercent;
                view.querySelector('#mdMinSavingsPct').value = c.QualityMinSavingsPercent || 0;
                view.querySelector('#mdMinSavingsMb').value = Math.round((c.QualityMinSavingsBytes || 0) / (1024 * 1024));
                view.querySelector('#mdAudioLangs').value = (c.AllowedAudioLanguages || []).join(', ');
                view.querySelector('#mdSubLangs').value = (c.AllowedSubtitleLanguages || []).join(', ');
                view.querySelector('#mdMoviesTarget').value = c.MoviesTargetPath || '';
                view.querySelector('#mdTvTarget').value = c.TvTargetPath || '';
                view.querySelector('#mdAnimeTarget').value = c.AnimeTargetPath || '';
                view.querySelector('#mdMusicTarget').value = c.MusicTargetPath || '';
                view.querySelector('#mdBooksTarget').value = c.BooksTargetPath || '';
                view.querySelector('#mdComicsTarget').value = c.ComicsTargetPath || '';
                view.querySelector('#mdPicturesTarget').value = c.PicturesTargetPath || '';
                view.querySelector('#mdMediaSortSource').value = c.MediaSortSource || 'JellyfinMetadata';
                view.querySelector('#mdRenameAfterTranscode').checked = !!c.RenameAfterTranscode;
                view.querySelector('#mdPreserveExternalIdInFilename').checked = !!c.PreserveExternalIdInFilename;
                view.querySelector('#mdBinPath').value = c.RecycleBinPath || '';
                view.querySelector('#mdRetention').value = c.RecycleBinRetentionDays;
                view.querySelector('#mdBinWarnThreshold').value = (c.RecycleBinWarnThresholdGb != null ? c.RecycleBinWarnThresholdGb : 10);
                view.querySelector('#mdBinPauseAt').value = (c.RecycleBinPauseFixesAtGb != null ? c.RecycleBinPauseFixesAtGb : 0);
                // 0 == disabled — even when the master toggle is on, threshold 0 short-circuits the compute.
                var lowSpaceOn = c.LibraryDriveLowSpaceWarningEnabled !== false;
                var lowSpaceThreshold = (c.LibraryDriveWarnThresholdPercent != null ? c.LibraryDriveWarnThresholdPercent : 90);
                view.querySelector('#mdLowSpaceThreshold').value = lowSpaceOn ? lowSpaceThreshold : 0;
                view.querySelector('#mdThorough').checked = c.ThoroughPlayabilityCheck;
                view.querySelector('#mdRepairRemux').checked = c.RepairAttemptRemux !== false;
                view.querySelector('#mdRepairDropStreams').checked = c.RepairAttemptDropStreams !== false;
                view.querySelector('#mdRepairContainerCoerce').checked = c.RepairAttemptContainerCoerce !== false;
                view.querySelector('#mdRepairAudioReencode').checked = c.RepairAttemptAudioReencode !== false;
                view.querySelector('#mdRepairReencode').checked = c.RepairAttemptReencode !== false;
                view.querySelector('#mdSubIgnoreRate').checked = c.SubtitleIgnoreRateLimit !== false;
                view.querySelector('#mdSubHearingImpaired').checked = !!c.SubtitleHearingImpairedMode;
                view.querySelector('#mdEditions').checked = c.TreatEditionsAsDuplicates;
                view.querySelector('#mdDuplicateMinAge').value = c.DuplicateMinAgeDays == null ? 0 : c.DuplicateMinAgeDays;
                // Confidence ladder controls (§7). Defaults mirror PluginConfiguration ctor.
                view.querySelector('#mdDupAutoFixConfidence').value = c.DuplicateAutoFixConfidence != null ? c.DuplicateAutoFixConfidence : 0.80;
                view.querySelector('#mdDupTitleVeto').value = c.DuplicateTitleJaccardVeto != null ? c.DuplicateTitleJaccardVeto : 0.40;
                view.querySelector('#mdDupRuntimeVeto').value = c.DuplicateRuntimeVetoPct != null ? c.DuplicateRuntimeVetoPct : 15;
                view.querySelector('#mdDupExactHash').checked = c.DuplicateExactHashEnabled !== false;
                view.querySelector('#mdStaleExcludedGenres').value = (c.StaleExcludedGenres || []).join(', ');
                view.querySelector('#mdTrickplayQuality').value = c.TrickplayWebPQuality > 0 ? c.TrickplayWebPQuality : 80;
                view.querySelector('#mdTrickplayMinSize').value = (c.TrickplayMinSizeMb != null ? c.TrickplayMinSizeMb : 10);
                view.querySelector('#mdSubForceFont').value = c.SubtitleForceFont || '';
                syncSubForceFontFace();
                view.querySelector('#mdOrphanEmpty').checked = c.OrphanScanEmptyFolders !== false;
                view.querySelector('#mdOrphanSubs').checked = c.OrphanScanSubtitles !== false;
                view.querySelector('#mdOrphanTrickplay').checked = c.OrphanScanTrickplay !== false;
                view.querySelector('#mdOrphanMetadata').checked = c.OrphanScanMetadata !== false;
                view.querySelector('#mdConvertEbooks').checked = !!c.ConvertEbooks;
                view.querySelector('#mdConvertComics').checked = !!c.ConvertComics;
                view.querySelector('#mdConvertAudiobooks').checked = !!c.ConvertAudiobooks;
                view.querySelector('#mdConvertImages').checked = !!c.ConvertImages;
                view.querySelector('#mdConvertMusic').checked = !!c.ConvertMusic;
                view.querySelector('#mdConvertVideos').checked = !!c.ConvertVideos;
                view.querySelector('#mdAudiobookMergeMultiFile').checked = !!c.AudiobookMergeMultiFile;
                view.querySelector('#mdCalibreOverridePath').value = c.CalibreOverridePath || '';
                view.querySelector('#mdEbookPreferredTarget').value = c.EbookPreferredTarget || '';
            }


            function loadSettings() {
                if (!mdSettingsDirty) mdConfigLoaded = false;
                if (deps.recycleTab) deps.recycleTab.refreshInfo();
                deps.loadEnv().then(function (env) {
                    deps.applySubtitleProviderHint(deps.view.querySelector('#mdProviderHint'), env);
                });
                populateLanguageDatalist();
                populateGenreDatalist();
                deps.api.configuration(deps.PLUGIN_ID).then(function (c) {
                    // Invariant: server config apply must not clobber local unsaved edits.
                    // Reproduced live in the 06-C.3 QA sweep (2026-09-23) — toggling a
                    // checkbox then Overview → Settings silently reverted the toggle
                    // because loadSettings unconditionally re-hydrated form state from
                    // the server. UI-11: on dirty bail we still need to finish the
                    // non-form bits (libraries, stale-exclusion checkboxes) AND flip
                    // mdConfigLoaded = true, otherwise an edit that lands before the
                    // very first hydration completes locks Save forever.
                    var dirtyBeforeApply = mdSettingsDirty;
                    if (!dirtyBeforeApply) {
                        applyConfigToForm(deps.view, c);
                        // One-shot subForce change listener install: loadSettings runs on wizard
                        // finish AND every Settings tab activation. ponytail: current addEventListener
                        // multiplies with each re-run; syncSubForceFontFace is idempotent so it's
                        // cosmetic churn only. Guarded here so we don't stack listeners; a proper
                        // ownGlobal/dispose lifecycle lands in 06-D.
                        var subForce = deps.view.querySelector('#mdSubForceFont');
                        if (subForce && !subForce.dataset.mdFontFaceBound) {
                            subForce.addEventListener('change', syncSubForceFontFace);
                            subForce.dataset.mdFontFaceBound = '1';
                        }
                        // Population above fires change/input events — reset the dirty flag so a fresh
                        // load doesn't immediately trip the unsaved-settings guard on the next unload.
                        deps.lifetime.timeout(function () { markSettingsClean(); }, 0);
                        syncSliderLabels();
                        initTaskCardsAfterLoad();
                    }
                    // Empty library selections mean all libraries. Save must wait for both lists.
                    return Promise.all([
                        dirtyBeforeApply && deps.view.querySelector('#mdLibraries .mdLib') ? Promise.resolve() : loadLibraries(c),
                        dirtyBeforeApply && deps.view.querySelector('#mdStaleExcludedLibs .mdStaleExcludeLib') ? Promise.resolve() : loadStaleExcludedLibraries(c)
                    ]).then(function () {
                        mdConfigLoaded = true;
                    });
                }).catch(function () {
                    mdConfigLoaded = false;
                    var message = deps.view.querySelector('#mdSaveMsg');
                    if (message) message.textContent = 'Settings could not finish loading. Reopen Settings to retry.';
                });
            }


            // Renders one checkbox per Jellyfin library into the Stale card so the user can carve out
            // libraries that stay unwatched by design (Christmas, reference material) without turning
            // the whole stale scanner off.
            function loadStaleExcludedLibraries(config) {
                var excluded = new Set(config.StaleExcludedLibraryIds || []);
                return deps.api('Libraries').then(function (folders) {
                    var el = deps.view.querySelector('#mdStaleExcludedLibs');
                    if (!el) return;
                    var rows = folders.filter(function (f) {
                        return !f.CollectionType || f.CollectionType === 'movies' || f.CollectionType === 'tvshows';
                    });
                    if (!rows.length) {
                        el.textContent = 'No movie or TV libraries configured yet.';
                        return;
                    }
                    el.innerHTML = rows.map(function (f) {
                        var checked = excluded.has(f.ItemId) ? ' checked' : '';
                        return '<div class="checkboxContainer" style="margin:.15em 0;"><label class="emby-checkbox-label">' +
                            '<input type="checkbox" is="emby-checkbox" class="mdStaleExcludeLib" data-id="' + deps.esc(f.ItemId) + '"' + checked + ' />' +
                            '<span>' + deps.esc(f.Name) + '</span></label></div>';
                    }).join('');
                });
            }


            // Single "Encoder" select consolidates the old HW-encoder toggle + GPU picker.
            function populateEncoderOptions(useHw, currentIndex) {
                var sel = deps.view.querySelector('#mdEncoder');
                if (!sel) return;
                var gpus = (deps.lastStatus && deps.lastStatus.System && deps.lastStatus.System.Gpus) || [];
                var html = '<option value="cpu">CPU (software) — best quality, slowest</option>' +
                    '<option value="auto">Auto GPU (Jellyfin default) — much faster</option>';
                gpus.forEach(function (g) {
                    var label = 'GPU ' + g.Index + ' — ' + g.Name + ' (' + (g.Source || '') + ')';
                    html += '<option value="' + g.Index + '">' + deps.esc(label) + '</option>';
                });
                if (useHw && currentIndex != null && !gpus.some(function (g) { return String(g.Index) === String(currentIndex); })) {
                    html += '<option value="' + deps.esc(String(currentIndex)) + '">GPU ' + deps.esc(String(currentIndex)) + ' (not currently detected)</option>';
                }
                sel.innerHTML = html;
                sel.value = deps.encoderConfigToValue(useHw, currentIndex);
            }


            // Inverse of applyConfigToForm. Mutates `c` in place with every field the Settings
            // form owns; callers keep the "getPluginConfiguration → mutate → update" pattern so
            // fields not owned by the form (unknown/future config keys, wizard-only fields)
            // survive round-trips untouched.
            function readFormIntoConfig(view, c) {
                c.DryRun = view.querySelector('#mdDryRun').checked;
                c.PauseDuringPlayback = view.querySelector('#mdPausePlayback').checked;
                c.LowSystemImpactMode = view.querySelector('#mdLowImpact').checked;
                c.FixWindowStart = view.querySelector('#mdFixWindowStart').value || '';
                c.FixWindowEnd = view.querySelector('#mdFixWindowEnd').value || '';
                deps.applyAnalyticsToggle(c, view.querySelector('#mdAnalytics').checked);
                FIX_TYPES.forEach(function (t) {
                    c[t.mode] = view.querySelector('#mdMode' + t.key).value;
                    if (t.disposal) c[t.disposal] = view.querySelector('#mdDisposal' + t.key).value;
                });
                var staleDays = parseInt(view.querySelector('#mdStaleThresholdDays').value, 10);
                if (!isNaN(staleDays) && staleDays > 0) c.StaleThresholdDays = staleDays;
                var heavyDays = parseInt(view.querySelector('#mdHeavyTranscodeDays').value, 10);
                if (!isNaN(heavyDays) && heavyDays > 0) c.HeavyTranscodeLookbackDays = heavyDays;
                var coverName = (view.querySelector('#mdCoverFilename').value || '').trim();
                c.EmbeddedCoverFilename = coverName.length > 0 ? coverName : 'cover.jpg';
                c.EmbeddedCoverStripFromAudio = view.querySelector('#mdCoverStripAudio').checked;
                c.MaxResolutionHeight = parseInt(view.querySelector('#mdMaxRes').value, 10);
                c.MaxBitrateMbpsAt1080p = bitrateBaseFromSlider();
                c.PreferredCodec = view.querySelector('#mdCodec').value;
                c.TargetContainer = view.querySelector('#mdContainer').value;
                var encConf = deps.encoderValueToConfig(view.querySelector('#mdEncoder').value);
                c.UseHardwareEncoder = encConf.useHw;
                c.PreferredGpuIndex = encConf.gpuIndex;
                c.SoftwareEncodePreset = view.querySelector('#mdEncodePreset').value;
                c.MinScanFileSizeMb = parseInt(view.querySelector('#mdMinSize').value, 10) || 0;
                c.SkipHdrContent = view.querySelector('#mdSkipHdr').checked;
                c.QualityScanAudiobooks = view.querySelector('#mdQualityScanAudiobooks').checked;
                c.ReencodeFileTypes = deps.csv(view.querySelector('#mdFileTypes').value);
                // UI-13: `|| 15` coerces a valid 0 to 15; the slider lets you drop to zero (strict
                // ceiling) and the user's selection must survive. Only an unparseable value falls
                // back to the default.
                var tol = parseInt(view.querySelector('#mdTolerance').value, 10);
                c.QualityTolerancePercent = isNaN(tol) ? 15 : Math.max(0, Math.min(50, tol));
                c.QualityMinSavingsPercent = Math.max(0, Math.min(95, parseInt(view.querySelector('#mdMinSavingsPct').value, 10) || 0));
                c.QualityMinSavingsBytes = Math.max(0, parseInt(view.querySelector('#mdMinSavingsMb').value, 10) || 0) * 1024 * 1024;
                c.AllowedAudioLanguages = deps.csv(view.querySelector('#mdAudioLangs').value);
                c.AllowedSubtitleLanguages = deps.csv(view.querySelector('#mdSubLangs').value);
                c.MoviesTargetPath = view.querySelector('#mdMoviesTarget').value.trim();
                c.TvTargetPath = view.querySelector('#mdTvTarget').value.trim();
                c.AnimeTargetPath = view.querySelector('#mdAnimeTarget').value.trim();
                c.MusicTargetPath = view.querySelector('#mdMusicTarget').value.trim();
                c.BooksTargetPath = view.querySelector('#mdBooksTarget').value.trim();
                c.ComicsTargetPath = view.querySelector('#mdComicsTarget').value.trim();
                c.PicturesTargetPath = view.querySelector('#mdPicturesTarget').value.trim();
                c.MediaSortSource = view.querySelector('#mdMediaSortSource').value;
                c.RenameAfterTranscode = view.querySelector('#mdRenameAfterTranscode').checked;
                c.PreserveExternalIdInFilename = view.querySelector('#mdPreserveExternalIdInFilename').checked;
                c.RecycleBinPath = view.querySelector('#mdBinPath').value.trim();
                c.RecycleBinRetentionDays = Math.max(1, parseInt(view.querySelector('#mdRetention').value, 10) || 30);
                var warnGb = parseInt(view.querySelector('#mdBinWarnThreshold').value, 10);
                c.RecycleBinWarnThresholdGb = isNaN(warnGb) ? 10 : Math.max(0, warnGb);
                var pauseGb = parseInt(view.querySelector('#mdBinPauseAt').value, 10);
                c.RecycleBinPauseFixesAtGb = isNaN(pauseGb) ? 0 : Math.max(0, pauseGb);
                var lowSpacePct = parseInt(view.querySelector('#mdLowSpaceThreshold').value, 10);
                // 0 disables via the compute path (thresholdPercent <= 0 returns null). The enable
                // flag stays true so re-enabling is one slider drag, not a hidden second toggle.
                c.LibraryDriveLowSpaceWarningEnabled = true;
                c.LibraryDriveWarnThresholdPercent = isNaN(lowSpacePct) ? 90 : Math.max(0, Math.min(99, lowSpacePct));
                c.ThoroughPlayabilityCheck = view.querySelector('#mdThorough').checked;
                c.RepairAttemptRemux = view.querySelector('#mdRepairRemux').checked;
                c.RepairAttemptDropStreams = view.querySelector('#mdRepairDropStreams').checked;
                c.RepairAttemptContainerCoerce = view.querySelector('#mdRepairContainerCoerce').checked;
                c.RepairAttemptAudioReencode = view.querySelector('#mdRepairAudioReencode').checked;
                c.RepairAttemptReencode = view.querySelector('#mdRepairReencode').checked;
                c.SubtitleIgnoreRateLimit = view.querySelector('#mdSubIgnoreRate').checked;
                c.SubtitleHearingImpairedMode = view.querySelector('#mdSubHearingImpaired').checked;
                c.TreatEditionsAsDuplicates = view.querySelector('#mdEditions').checked;
                c.EnabledLibraries = Array.prototype.map.call(view.querySelectorAll('.mdLib:checked'), function (cb) {
                    return cb.getAttribute('data-id');
                });
                c.KeeperPolicyOrder = KEEPER_PRESETS[view.querySelector('#mdKeeperPolicy').value] || KEEPER_PRESETS.quality;
                var minAge = parseInt(view.querySelector('#mdDuplicateMinAge').value, 10);
                c.DuplicateMinAgeDays = isNaN(minAge) || minAge < 0 ? 0 : minAge;
                // Confidence ladder — clamp to their spec ranges; empty/invalid → default.
                var autoConf = parseFloat(view.querySelector('#mdDupAutoFixConfidence').value);
                c.DuplicateAutoFixConfidence = isNaN(autoConf) ? 0.80 : Math.max(0, Math.min(1, autoConf));
                var titleVeto = parseFloat(view.querySelector('#mdDupTitleVeto').value);
                c.DuplicateTitleJaccardVeto = isNaN(titleVeto) ? 0.40 : Math.max(0, Math.min(1, titleVeto));
                var rtVeto = parseInt(view.querySelector('#mdDupRuntimeVeto').value, 10);
                c.DuplicateRuntimeVetoPct = isNaN(rtVeto) ? 15 : Math.max(0, Math.min(100, rtVeto));
                c.DuplicateExactHashEnabled = view.querySelector('#mdDupExactHash').checked;
                c.StaleExcludedLibraryIds = Array.prototype.map.call(view.querySelectorAll('.mdStaleExcludeLib:checked'), function (cb) {
                    return cb.getAttribute('data-id');
                });
                // Stale-genre input keeps user casing so "Documentary" isn't lowercased to "documentary"
                // — the scanner does its own case-insensitive compare against BaseItem.Genres.
                c.StaleExcludedGenres = view.querySelector('#mdStaleExcludedGenres').value.split(',')
                    .map(function (s) { return s.trim(); })
                    .filter(function (s) { return s.length > 0; });
                var trickQ = parseInt(view.querySelector('#mdTrickplayQuality').value, 10);
                c.TrickplayWebPQuality = isNaN(trickQ) ? 80 : Math.max(40, Math.min(95, trickQ));
                var trickMin = parseInt(view.querySelector('#mdTrickplayMinSize').value, 10);
                c.TrickplayMinSizeMb = isNaN(trickMin) ? 10 : Math.max(0, trickMin);
                c.SubtitleForceFont = (view.querySelector('#mdSubForceFont').value || '').trim();
                c.OrphanScanEmptyFolders = view.querySelector('#mdOrphanEmpty').checked;
                c.OrphanScanSubtitles = view.querySelector('#mdOrphanSubs').checked;
                c.OrphanScanTrickplay = view.querySelector('#mdOrphanTrickplay').checked;
                c.OrphanScanMetadata = view.querySelector('#mdOrphanMetadata').checked;
                c.ConvertEbooks = view.querySelector('#mdConvertEbooks').checked;
                c.ConvertComics = view.querySelector('#mdConvertComics').checked;
                c.ConvertAudiobooks = view.querySelector('#mdConvertAudiobooks').checked;
                c.ConvertImages = view.querySelector('#mdConvertImages').checked;
                c.ConvertMusic = view.querySelector('#mdConvertMusic').checked;
                c.ConvertVideos = view.querySelector('#mdConvertVideos').checked;
                c.AudiobookMergeMultiFile = view.querySelector('#mdAudiobookMergeMultiFile').checked;
                c.CalibreOverridePath = (view.querySelector('#mdCalibreOverridePath').value || '').trim();
                c.EbookPreferredTarget = view.querySelector('#mdEbookPreferredTarget').value || '';
            }


            // UI-08: the Scan button needs to know whether Save actually completed before firing.
            // saveSettings is called from the form submit event (which lets the browser's native
            // Enter-to-submit still work) and returns no promise. Internal helper saveSettingsAsync
            // returns a promise so external callers (Scan handler) can await success / failure /
            // user-cancellation without polling the dirty flag.
            function saveSettings(e) {
                if (e) e.preventDefault();
                saveSettingsAsync().catch(showSaveError);
                return false;
            }

            function showSaveError(error) {
                if (error && ['cancelled', 'low-space-warn', 'not-loaded'].indexOf(error.message) >= 0) return;
                deps.view.querySelector('#mdSaveMsg').textContent = 'Could not save settings — ' + ((error && (error.responseText || error.message || error.statusText)) || 'unknown error');
            }

            function saveSettingsAsync(forceSaveDespiteLowSpace) {
                deps.view.querySelector('#mdSaveMsg').textContent = '';
                // Race gate — refuse to save until the load pass has hydrated every checkbox
                // from the server-side config. Without this, an early click of Save persists
                // every un-hydrated checkbox as `false` (DryRun silently off → real deletes,
                // PauseDuringPlayback silently off → fixes fire during playback, etc.).
                if (!mdConfigLoaded) {
                    if (window.Dashboard && Dashboard.alert) {
                        Dashboard.alert({ title: 'Still loading', message: 'Settings are still loading from the server — wait a moment and try Save again.' });
                    } else {
                        window.alert('Settings are still loading from the server — wait a moment and try Save again.');
                    }
                    return Promise.reject(new Error('not-loaded'));
                }
                return deps.api.configuration(deps.PLUGIN_ID).then(function (c) {
                    var turnedAuto = false, turnedPermanent = false;
                    FIX_TYPES.forEach(function (t) {
                        var newMode = deps.view.querySelector('#mdMode' + t.key).value;
                        if (newMode === 'Automatic' && c[t.mode] !== 'Automatic') turnedAuto = true;
                        if (t.disposal) {
                            var newDisposal = deps.view.querySelector('#mdDisposal' + t.key).value;
                            if (newDisposal === 'Permanent' && c[t.disposal] !== 'Permanent') turnedPermanent = true;
                        }
                    });
                    if (turnedAuto && !window.confirm(deps.STR.confirmAuto)) {
                        return Promise.reject(new Error('cancelled'));
                    }
                    if (turnedPermanent && !window.confirm(deps.STR.confirmPermanent)) {
                        return Promise.reject(new Error('cancelled'));
                    }

                    readFormIntoConfig(deps.view, c);
                    return validateAndSaveConfig(c, deps.view.querySelector('#mdBinPath').value.trim(), forceSaveDespiteLowSpace === true);
                });
            }


            // Pre-flight the recycle bin volume before saving. Soft-warns on
            // insufficient space with a jump-to-card button and a "Save anyway" override — GitHub
            // #58: users on smaller volumes were completely blocked from saving unrelated settings
            // because the refusal was hard. forceSaveDespiteLowSpace=true skips the check and
            // proceeds to the update call; the user's explicit click on "Save anyway" is a
            // stronger signal than the 5 GB floor.
            function validateAndSaveConfig(c, binPath, forceSaveDespiteLowSpace) {
                var warnEl = deps.view.querySelector('#mdBinDiskWarn');
                if (warnEl) warnEl.style.display = 'none';
                var url = 'RecycleBin/DiskInfo';
                if (binPath) url += '?path=' + encodeURIComponent(binPath);
                // UI-12: capture the edit revision at the moment we commit to persist. If any
                // further edit happens before the API round-trip completes, markSettingsClean no
                // longer clears the dirty flag and those edits remain protected from the next
                // hydration overwrite.
                var revAtStart = mdEditRevision;
                var persist = function () {
                    return ApiClient.updatePluginConfiguration(deps.PLUGIN_ID, c).then(function (result) {
                        deps.view.querySelector('#mdSaveMsg').textContent = deps.STR.saved;
                        Dashboard.processPluginConfigurationUpdateResult(result);
                        deps.refresh();
                        markSettingsClean(revAtStart);
                    });
                };
                return deps.api(url).then(function (info) {
                    if (!forceSaveDespiteLowSpace && info && info.MeetsFiveGbMinimum === false) {
                        showBinDiskWarning(info, c, binPath);
                        // showBinDiskWarning stashes a "Save anyway" closure — treat as cancelled
                        // for the UI-08 scan-after-save flow; the user hasn't actually committed.
                        return Promise.reject(new Error('low-space-warn'));
                    }
                    return persist();
                }, function () {
                    // Endpoint failed (network, permission) — fall through and save. Missing pre-flight
                    // isn't worth blocking a settings save; the FixTask floor still enforces safety.
                    return persist();
                });
            }


            function showBinDiskWarning(info, pendingConfig, pendingBinPath) {
                var warnEl = deps.view.querySelector('#mdBinDiskWarn');
                if (!warnEl) return;
                var freeGb = Math.round((info.FreeBytes || 0) / (1024 * 1024 * 1024));
                warnEl.innerHTML =
                    '<div style="margin-bottom:.5em;"><strong>Warning:</strong> ' +
                    deps.esc(info.PathProbed || '(default location)') + ' has only ' + freeGb + ' GB free.</div>' +
                    '<div style="margin-bottom:.7em;">MediaDash recommends at least 5 GB free before it uses a folder for the recycle bin (Jellyfin itself needs about 2 GB to keep running). You can pick a location with more room, or save these settings anyway if you know what you\'re doing — GitHub #58.</div>' +
                    '<button is="emby-button" type="button" id="mdBinDiskWarnJump" class="raised" style="background:#dc2626;color:#fff;font-size:80%;margin-right:.5em;"><span class="material-icons" style="font-size:1em;vertical-align:-2px;margin-right:.3em;">arrow_downward</span><span>Go to Recycle bin settings</span></button>' +
                    '<button is="emby-button" type="button" id="mdBinDiskWarnForce" class="raised" style="font-size:80%;"><span class="material-icons" style="font-size:1em;vertical-align:-2px;margin-right:.3em;">save</span><span>Save anyway</span></button>';
                warnEl.style.display = '';
                var jumpBtn = warnEl.querySelector('#mdBinDiskWarnJump');
                if (jumpBtn) {
                    jumpBtn.addEventListener('click', function () {
                        var card = deps.view.querySelector('#mdSet-recycle');
                        if (!card) return;
                        // Uncollapse the card if it's collapsed (mdSetHead click toggles collapsed state).
                        setSetCardCollapsed(card, false);
                        card.scrollIntoView({ behavior: 'smooth', block: 'start' });
                        var pathInput = deps.view.querySelector('#mdBinPath');
                        if (pathInput) {
                            deps.lifetime.timeout(function () { pathInput.focus(); }, 350);
                        }
                    });
                }

                var forceBtn = warnEl.querySelector('#mdBinDiskWarnForce');
                if (forceBtn && pendingConfig) {
                    forceBtn.addEventListener('click', function () {
                        saveSettingsAsync(true).catch(showSaveError);
                    });
                }
            }


            function bindSettings() {
                    buildFixTypeRows();
                    initSliders();
                    initSetNav();
                    deps.view.querySelector('#mdMaxRes').addEventListener('change', function () {
                        // Re-express the same underlying ceiling at the newly selected resolution.
                        setBitrateSliderForResolution(bitrateBaseFromSlider());
                    });
                    deps.view.querySelector('#mdSettingsForm').addEventListener('submit', saveSettings);
                    installUnsavedGuardOnce();
                    initProtectionSection();
            }

            // Roadmap i-110-protected-media: Protected paths section. Fully server-backed; nothing
            // here touches PluginConfiguration — rules live in the `protection_rules` DB table and
            // apply immediately after Add / Remove (ProtectionService.Invalidate on the API side).
            // ponytail: one mask, written to BOTH scanners_mask and fixers_mask on the wire — the
            // schema keeps the columns separate for future granularity, but the UI never asks users
            // to distinguish "don't scan" from "don't fix" (they always want both).
            function initProtectionSection() {
                var view = deps.view;
                var addBtn = view.querySelector('#mdProtAdd');
                if (!addBtn) return;
                populateProtectionLibraries();
                populateProtectionBlockGrid();
                reloadProtectionRows();
                var kindSel = view.querySelector('#mdProtKind');
                kindSel.addEventListener('change', refreshProtectionTargetVisibility);
                refreshProtectionTargetVisibility();
                addBtn.addEventListener('click', function () {
                    var kind = kindSel.value;
                    var target;
                    if (kind === 'library') {
                        target = view.querySelector('#mdProtTargetLibrary').value || '';
                    } else {
                        target = (view.querySelector('#mdProtTargetFolder').value || '').trim();
                    }
                    var mask = readProtectionBlockMask();
                    if (!target) { Dashboard.alert({ message: 'Target is required.', title: 'Protected paths' }); return; }
                    if (!mask) { Dashboard.alert({ message: 'Pick at least one issue type to block (or tick All).', title: 'Protected paths' }); return; }
                    var body = { Kind: kind, Target: target, ScannersMask: mask, FixersMask: mask, Note: (view.querySelector('#mdProtNote').value || '').trim() };
                    deps.api.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('MediaDash/Protection'),
                        contentType: 'application/json',
                        data: JSON.stringify(body)
                    }).then(function () {
                        view.querySelector('#mdProtTargetFolder').value = '';
                        view.querySelector('#mdProtNote').value = '';
                        resetProtectionBlockMask();
                        reloadProtectionRows();
                    }, function (xhr) {
                        Dashboard.alert({ message: (xhr && xhr.responseText) || 'Failed to add rule.', title: 'Protected paths' });
                    });
                });
            }

            function refreshProtectionTargetVisibility() {
                var view = deps.view;
                var kind = view.querySelector('#mdProtKind').value;
                view.querySelector('#mdProtTargetFolderWrap').style.display = kind === 'library' ? 'none' : '';
                view.querySelector('#mdProtTargetLibraryWrap').style.display = kind === 'library' ? '' : 'none';
            }

            function populateProtectionLibraries() {
                var view = deps.view;
                var sel = view.querySelector('#mdProtTargetLibrary');
                if (!sel) return;
                ApiClient.getJSON(ApiClient.getUrl('MediaDash/Libraries')).then(function (libs) {
                    sel.innerHTML = (libs || []).map(function (l) {
                        return '<option value="' + deps.esc(l.ItemId) + '">' + deps.esc(l.Name) + '</option>';
                    }).join('') || '<option value="" disabled>No libraries</option>';
                }, function () {
                    sel.innerHTML = '<option value="" disabled>Failed to load libraries</option>';
                });
            }

            function populateProtectionBlockGrid() {
                var view = deps.view;
                var host = view.querySelector('#mdProtBlockGrid');
                if (!host) return;
                ApiClient.getJSON(ApiClient.getUrl('MediaDash/Protection/IssueTypes')).then(function (types) {
                    var chipStyle = 'display:inline-flex;align-items:center;gap:.3em;padding:.25em .6em;border:1px solid rgba(255,255,255,.15);border-radius:999px;font-size:12px;cursor:pointer;user-select:none;';
                    var allChip = '<label style="' + chipStyle + 'background:rgba(249,115,22,.15);border-color:#f97316;"><input type="checkbox" id="mdProtBlockAll" style="margin:0;" /><span>All</span></label>';
                    var typeChips = (types || []).map(function (t) {
                        return '<label class="mdProtBlockType" style="' + chipStyle + '"><input type="checkbox" value="' + deps.esc(t) + '" style="margin:0;" /><span>' + deps.esc(t) + '</span></label>';
                    }).join('');
                    host.innerHTML = allChip + typeChips;
                    var allBox = host.querySelector('#mdProtBlockAll');
                    var typeBoxes = host.querySelectorAll('.mdProtBlockType input');
                    allBox.addEventListener('change', function () {
                        typeBoxes.forEach(function (cb) { cb.checked = false; cb.disabled = allBox.checked; });
                    });
                    typeBoxes.forEach(function (cb) {
                        cb.addEventListener('change', function () {
                            if (cb.checked) { allBox.checked = false; }
                        });
                    });
                }, function () {
                    host.innerHTML = '<span style="opacity:.7;color:#fca5a5;">Failed to load issue types.</span>';
                });
            }

            function readProtectionBlockMask() {
                var view = deps.view;
                var host = view.querySelector('#mdProtBlockGrid');
                if (!host) return '';
                if (host.querySelector('#mdProtBlockAll') && host.querySelector('#mdProtBlockAll').checked) return '*';
                var picks = Array.prototype.slice.call(host.querySelectorAll('.mdProtBlockType input:checked')).map(function (cb) { return cb.value; });
                return picks.join(',');
            }

            function resetProtectionBlockMask() {
                var host = deps.view.querySelector('#mdProtBlockGrid');
                if (!host) return;
                var allBox = host.querySelector('#mdProtBlockAll');
                if (allBox) allBox.checked = false;
                host.querySelectorAll('.mdProtBlockType input').forEach(function (cb) { cb.checked = false; cb.disabled = false; });
            }

            function formatProtectionMask(m) {
                if (!m) return '<span style="opacity:.5;">—</span>';
                if (m === '*') return '<b>All</b>';
                return deps.esc(m);
            }

            function reloadProtectionRows() {
                var view = deps.view;
                var host = view.querySelector('#mdProtectionRows');
                if (!host) return;
                host.textContent = 'Loading…';
                // Fetch libraries too so we can render library-kind rules as friendly names.
                Promise.all([
                    ApiClient.getJSON(ApiClient.getUrl('MediaDash/Protection')),
                    ApiClient.getJSON(ApiClient.getUrl('MediaDash/Libraries')).catch(function () { return []; })
                ]).then(function (results) {
                    var rules = results[0] || [];
                    var libNameById = {};
                    (results[1] || []).forEach(function (l) { libNameById[l.ItemId] = l.Name; });
                    if (!rules.length) {
                        host.innerHTML = '<span style="opacity:.7;">No protected paths yet. Add one below.</span>';
                        return;
                    }
                    var esc = deps.esc;
                    var orphanCount = 0;
                    var rows = rules.map(function (r) {
                        // Display: if scanners and fixers agree (the norm from the new UI), show one
                        // "Blocks" column. Legacy rows with divergent masks fall back to two cells.
                        var mergedMask = r.ScannersMask === r.FixersMask ? formatProtectionMask(r.ScannersMask) : (formatProtectionMask(r.ScannersMask) + ' <span style="opacity:.5;">scan</span> · ' + formatProtectionMask(r.FixersMask) + ' <span style="opacity:.5;">fix</span>');
                        var displayTarget;
                        var orphanBadge = '';
                        var rowClass = '';
                        if (r.Kind === 'library') {
                            var known = libNameById[r.Target];
                            if (known) {
                                displayTarget = esc(known);
                            } else {
                                // Library was removed / renamed / re-added under a new ItemId. Rule
                                // will silently match nothing. Flag it loudly so the user removes
                                // it or re-adds the library.
                                orphanCount++;
                                rowClass = ' class="mdProtOrphanRow"';
                                displayTarget = '<span style="opacity:.7;font-family:\'IBM Plex Mono\',monospace;">' + esc(r.Target) + '</span>';
                                orphanBadge = ' <span class="mdProtOrphanBadge" title="This library is no longer in Jellyfin (removed, renamed, or re-added with a new id). The rule blocks nothing. Remove it or add a fresh library rule."><span class="material-icons">error</span>Missing library</span>';
                            }
                        } else {
                            displayTarget = esc(r.Target);
                        }
                        return '<tr' + rowClass + '>' +
                            '<td>' + esc(r.Kind) + '</td>' +
                            '<td style="word-break:break-all;">' + displayTarget + orphanBadge + '</td>' +
                            '<td>' + mergedMask + '</td>' +
                            '<td style="opacity:.8;">' + esc(r.Note || '') + '</td>' +
                            '<td><button is="emby-button" type="button" class="mdProtRemove" data-id="' + r.Id + '" title="Remove this protected-path rule. Applies immediately."><span class="material-icons">delete</span><span>Remove</span></button></td>' +
                            '</tr>';
                    }).join('');
                    // Global-banner treatment matches the plugin's dry-run / recycle-bin-full pattern
                    // (see #mdDryRunNote in configPage.html): big amber icon, bold title, secondary
                    // hint, styled container. Keeps the visual language consistent so users read it
                    // as the same class of "you should look at this" signal.
                    var banner = orphanCount > 0
                        ? '<div class="mdProtOrphanBanner">' +
                            '<span class="material-icons mdProtOrphanBannerIcon">warning</span>' +
                            '<div class="mdProtOrphanBannerBody">' +
                                '<div class="mdProtOrphanBannerTitle">' + orphanCount + ' protected-path rule' + (orphanCount === 1 ? '' : 's') + ' point at a library Jellyfin no longer has</div>' +
                                '<div class="fieldDescription">These rules block nothing. Remove them below, or add a new rule against a current library. Folder-kind rules are unaffected.</div>' +
                            '</div>' +
                          '</div>'
                        : '';
                    host.innerHTML = banner + '<table style="width:100%;border-collapse:collapse;font-size:13px;">' +
                        '<thead><tr style="text-align:left;opacity:.7;"><th>Kind</th><th>Target</th><th>Blocks</th><th>Note</th><th></th></tr></thead>' +
                        '<tbody>' + rows + '</tbody></table>';
                    host.querySelectorAll('.mdProtRemove').forEach(function (btn) {
                        btn.addEventListener('click', function () {
                            var id = btn.getAttribute('data-id');
                            deps.api.ajax({ type: 'DELETE', url: ApiClient.getUrl('MediaDash/Protection/' + id) })
                                .then(reloadProtectionRows, function (xhr) {
                                    Dashboard.alert({ message: (xhr && xhr.responseText) || 'Failed to remove rule.', title: 'Protected paths' });
                                });
                        });
                    });
                }, function () {
                    host.innerHTML = '<span style="opacity:.7;color:#fca5a5;">Failed to load protected paths.</span>';
                });
            }
        return {
            activateSettings: activateSettings,
            get DEFAULT_FIXER_ORDER() { return DEFAULT_FIXER_ORDER; },
            get FIXER_HARD_CONSTRAINTS() { return FIXER_HARD_CONSTRAINTS; },
            get KEEPER_PRESETS() { return KEEPER_PRESETS; },
            get langPresetsHtml() { return langPresetsHtml; },
            get wireChipInput() { return wireChipInput; },
            get wireLangPresets() { return wireLangPresets; },
            get mdSettingsDirty() { return mdSettingsDirty; },
            get keeperPresetFromConfig() { return keeperPresetFromConfig; },
            get populateLanguageDatalist() { return populateLanguageDatalist; },
            get localeToIso3() { return localeToIso3; },
            get populateGenreDatalist() { return populateGenreDatalist; },
            get loadSettings() { return loadSettings; },
            get bindSettings() { return bindSettings; },
            // UI-08: Scan handler awaits this instead of polling the dirty flag; promise resolves
            // when the server confirms persistence and rejects on cancel / low-space-warn / failure.
            get saveSettingsAsync() { return saveSettingsAsync; }
        };
    };
})();
