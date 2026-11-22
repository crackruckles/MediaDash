// MediaDash shared DOM/text helpers. Extracted from configPage.html on 2026-09-23
// (post-06-D follow-up 1a) to unblock full-DOM tab factory extraction
// (createHistoryTab / createRecycleTab / createIssuesTab / createErrorsTab).
// The tab factories close over `esc()` and `STR` today — once both live on
// window.MediaDash.*, each factory can move into its own file.
//
// Same 06-D pattern as page-lifetime.js / api-client.js: served by
// PluginAssetController, consumed via <script src>, loaded by
// tools/comprehensive-test/ui-lifecycle-self-tests.js via fs+vm.
(function () {
    'use strict';
    var ns = (typeof window !== 'undefined') ? window : globalThis;
    ns.MediaDash = ns.MediaDash || {};

    ns.MediaDash.fixerOrderPhase = function (type) {
        if (type === 'Misplaced' || type === 'Ungrouped' || type === 'ManualMove') return 2;
        if (type === 'Quality' || type === 'HeavyTranscode' || type === 'FailedTranscode' || type === 'FormatCompatibility') return 1;
        return 0;
    };

    ns.MediaDash.normalizeFixerOrder = function (order) {
        return [0, 1, 2].reduce(function (result, phase) {
            return result.concat(order.filter(function (type) { return ns.MediaDash.fixerOrderPhase(type) === phase; }));
        }, []);
    };

    // HTML-escape a string so it's safe in either text content OR an HTML attribute.
    // textContent → innerHTML escapes <, >, and & correctly but leaves " unescaped.
    // Every callsite that dumps this into an HTML attribute (title="…", data-…="…")
    // then breaks when the payload contains a quote — the attribute closes early and
    // everything after is misparsed. Escape " (and ' for good measure) here so any
    // reachable use in either HTML text OR an attribute is safe.
    ns.MediaDash.esc = function esc(s) {
        var d = document.createElement('div');
        d.textContent = s == null ? '' : s;
        return d.innerHTML.replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    };

    // Substitute {0}, {1}, ... in a template with positional args. Used every place
    // STR keys need runtime values interpolated in (last-scan time, drive %, banner
    // labels, fix counts). Pure — no context — so it lives next to esc + STR.
    ns.MediaDash.fmt = function fmt(str) {
        var args = Array.prototype.slice.call(arguments, 1);
        return str.replace(/\{(\d+)\}/g, function (m, i) { return args[i]; });
    };

    // Format a byte count as "1.2 GB" / "42 MB" / "0 B". Same rounding rule as the
    // original inline copy: whole-number units at KB or larger when >= 10, otherwise
    // one decimal place; bytes always integer.
    ns.MediaDash.bytes = function bytes(n) {
        if (!n || n <= 0) return '0 B';
        var units = ['B', 'KB', 'MB', 'GB', 'TB'], i = 0;
        while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
        return n.toFixed(n >= 10 || i === 0 ? 0 : 1) + ' ' + units[i];
    };

    // All user-facing strings live here for future localization. Object literal —
    // pure data, no closures. Every configPage.html reference is `STR.foo`; the
    // inline `var STR = window.MediaDash.STR;` alias in configPage.html keeps every
    // call site unchanged.
    ns.MediaDash.STR = {
        tagline: 'Keeps your library lean and playable — finds duplicate copies, broken files, oversized encodes and unwanted language tracks, then fixes them safely.',
        scanIdle: 'Last scan: {0}',
        scanNever: 'No scan has run yet. Click "Scan now" to check your libraries.',
        scanRunning: 'Scanning your libraries… {0}%',
        scanStarting: 'Starting scan…',
        scanDone: 'Scan finished — {0} open issue(s) found.',
        scanNowLabel: 'Scan now',
        moveAnyway: 'Move anyway',
        issueReplaced: 'A newer scan replaced this finding. Reloading the list — try again on the updated row.',
        moveAnywayHint: 'Finish or release the earlier move that holds this item, then plan it again.',
        enterSeason: 'Enter season number',
        enterSeasonHint: 'Jellyfin could not tell which season this episode belongs to. Enter it and MediaDash moves it.',
        enterSeasonPrompt: 'Which season is this episode in? Enter 0 for specials.',
        enterSeasonInvalid: 'Enter a season number such as 4, or 0 for specials.',
        stopScanLabel: 'Stop scanning',
        fixRunning: 'Applying fixes… {0}%',
        fixStarting: 'Starting fix run…',
        fixStartFailed: 'Could not start fixes.',
        issueActionFailed: 'Could not update this issue.',
        fixSettings: 'Fix settings',
        approvePartial: 'Queued {0} of {1} issues. Settings or issue status changed; review the refreshed list.',
        fixNowLabel: 'Run fixes now',
        stopFixLabel: 'Stop running fixes',
        fixDone: 'Fix run finished — see History for what was done.',
        fixNothingToDo: 'Nothing to fix yet — approve issues on the Issues tab, or switch a fix type to "Fix automatically" in Settings, then click again.',
        dryRunOn: 'Approved fixes are only rehearsed and logged to History. When the plan looks right, turn off dry-run to let MediaDash make real changes.',
        binFullTitle: 'Recycle bin is holding {0} across {1} file{2}',
        binFullBody: 'Files are auto-purged after the retention window in Settings → Recycle bin. Restore anything you still want, or empty the bin to reclaim the space now.',
        diskContext: '{0} free of {1} on your library drives',
        driveStorage: 'Library drive storage',
        driveUsed: '{0} used of {1}',
        driveFree: '{0} free',
        systemStats: 'System performance',
        statCpu: 'CPU',
        statRam: 'Memory',
        statGpu: 'GPU',
        openItemInJellyfin: 'Open item in Jellyfin',
        missingArtwork: 'Missing artwork',
        corruptArtwork: 'Corrupt artwork',
        missingArtworkHint: 'Jellyfin has a path recorded for this image, but the file was missing when MediaDash checked it.',
        clearMissingArtwork: 'Clear missing image reference',
        clearMissingArtworkHint: 'Clears the missing image reference so Jellyfin can fetch artwork again.',
        statDiskIo: 'Disk I/O',
        statDiskIoTooltip: 'Shows local disk activity only. Network drive and NAS disk performance is unavailable.',
        statDiskRead: 'Read',
        statDiskWrite: 'Write',
        statCpuHint: 'System-wide across {0} cores',
        statRamHint: '{0} used of {1}',
        statGpuHint: 'via {0}',
        statGpuMissing: 'No GPU counter available on this host',
        statUnavailable: 'N/A on {0}',
        statUnknown: '—',
        statPlatform: 'Host: {0}',
        totalSavings: 'Space you could reclaim',
        lifetimeSaved: 'Space trimmed since install',
        lifetimeSavedHint: 'Sum of per-file shrinkage from remuxes and re-encodes plus outright deletions. Originals wait in the recycle bin until purged (see below), so free disk space drops during a fix run and only fully recovers once the bin empties.',
        binHeldNow: 'Sitting in recycle bin now',
        binHeldNowHint: 'Originals moved aside so fixes stay reversible. Auto-purges after {0} day(s). Once purged, this space is truly reclaimed.',
        binHeldEmpty: 'Nothing in the recycle bin.',
        whereSavings: 'Where the savings are',
        issuesByType: 'Issues found',
        noIssues: 'No issues found — your library is in good shape. New scans run automatically on schedule.',
        issuesLoadFailed: "Couldn't load issues. Previously loaded results may be out of date. Retry, or check Jellyfin's logs if this continues.",
        retry: 'Retry',
        noHistory: 'Nothing has been fixed yet. Fixes appear here after a fix run, with a Restore button for anything recoverable.',
        recordedMoves: 'Recorded moves',
        diagnosticsNoScan: 'Run a scan with diagnostics enabled to record its details.',
        diagnosticsNoFix: 'Run fixes with diagnostics enabled to record their details.',
        diagnosticsScanners: 'Scanners',
        diagnosticsFixers: 'Fixers',
        diagnosticsFixer: 'Fixer',
        diagnosticsTargets: 'Targets',
        diagnosticsResolved: 'Issues resolved',
        diagnosticsFixersHint: 'Totals combine calls to each fixer. Targets include dry-run previews; resolved issues count applied fixes only.',
        diagnosticsScanner: 'Scanner',
        diagnosticsTime: 'Time',
        diagnosticsItemsFound: 'Items found',
        diagnosticsMaxCpu: 'Max CPU',
        diagnosticsMaxIo: 'Max I/O',
        diagnosticsMaxNetwork: 'Max network',
        diagnosticsMaxRam: 'Max RAM',
        diagnosticsMaxGpu: 'Max GPU',
        diagnosticsPeaksHint: 'Sampled host/container peaks. I/O combines reads and writes; network combines receive and send.',
        diagnosticsLoadFailed: 'Diagnostics could not be loaded. Refresh to try again.',
        undoCompleteMove: 'Undo complete move',
        moveNeedsReview: 'Needs review',
        moveUndone: 'Undone',
        moveCompleted: 'Completed',
        moveUndoCompleted: 'Undo completed',
        moveInProgress: 'Move in progress',
        moveRecordedPaths: 'Recorded paths',
        moveOriginal: 'Original',
        moveDestination: 'Destination',
        moveRecovery: 'Recovery locations — preserve these while reviewing',
        moveUserStatePending: "Run Jellyfin's library scan to finish indexing and restore watched state.",
        moveReviewRequired: 'Review the recorded move before continuing',
        moveUndoChecking: 'Checking the complete move and original locations…',
        moveUndoFailed: 'Undo could not complete. Review the recorded move and its current files.',
        moveLoadFailed: 'Move recovery records could not be loaded. Refresh this tab to try again.',
        moveRecoveryHelp: 'These records remain available if a move stops before History is saved. Undo restores the complete recorded item only when its files are unchanged and the original locations are free. Dry run previews Undo without changing files.',
        movePreviewLabel: 'Review complete move',
        moveSourceCleanup: 'After the last episode moves, recycle unchanged source metadata that already exists at the destination and remove the empty source folder.',
        moveFolder: 'Folder',
        moveFile: 'File',
        moveShowMore: 'Show more paths',
        fileMovePaths: "paths in this complete move",
        fileRenameTitle: 'Rename item',
        fileRenameName: 'New name',
        fileRenameReview: 'Review related paths',
        fileMoveDryRunHelp: "Dry run is on. Files will stay in place.",
        fileMoveHelp: "Move all the paths below together. The complete recorded move can be undone from History when its files are unchanged.",
        fileMoveCheckDryRun: "Check dry run",
        fileMoveAccept: "Move these paths",
        fileMovePreviewFailed: "The complete move could not be previewed.",
        fileMoveDone: "The complete move finished. See History for Undo.",
        fileMoveChanged: "The preview changed. Review the current paths before approving again.",
        fileMoveFailed: "The move stopped. Review its recorded paths in History.",
        allTypes: 'All issue types',
        showingOf: 'Showing the first {0} of {1} issues. Use the filter above to narrow the list.',
        approve: 'Approve',
        approveHint: 'Queues this fix for the next fix run.',
        dismiss: 'Ignore this',
        dismissHint: 'Won’t report this file for this issue again.',
        approveAll: 'Approve all {0} shown',
        approveAllTypes: 'Approve all {0} issues (all types)',
        rescanApproveAll: 'Scan & approve all ({0})',
        rescanApproveScanning: 'Scanning again for the issues that need a fresh scan… keep this page open.',
        rescanApproveApproving: 'Scan finished. Approving every issue that is now ready…',
        rescanApproveDone: 'Approved {0} issue(s) after the fresh scan. Anything still blocked is listed with its reason.',
        approveAllNote: 'Queues eligible issues in this filtered list. Blocked, already queued, and needs-review issues are excluded.',
        clearAll: 'Clear all {0} shown',
        clearAllConfirm: 'Ignore all {0} issue(s) currently listed? They won’t be reported again by future scans. This does not touch files on disk.',
        clearAllNothing: 'No issues to clear.',
        approved: 'Approved — will be handled by the next fix run.',
        dismissed: 'Ignored — this file won’t be reported again.',
        undoAction: 'Undo',
        undoActionHint: 'Change your mind? Puts the issue back on the decision list so you can pick again.',
        undoneMsg: 'Undone — this issue is back on your decision list.',
        undoFailedMsg: 'Could not undo (the fix may already have run).',
        langFilterLabel: 'Filter by audio language:',
        langFilterAll: 'All languages',
        ctxIgnoreFolder: 'Ignore all issues in this folder',
        ctxIgnoreType: 'Ignore all "{0}" issues',
        ctxIgnoreExt: 'Ignore all {0} files',
        ctxIgnoreMedia: 'Ignore all {0} issues',
        ctxConfirm: 'Ignore {0} issue(s)? They won’t be reported again by future scans.',
        ctxNothing: 'No matching open issues.',
        showIgnored: 'Show ignored',
        showOpen: 'Show open issues',
        noIgnored: 'No ignored issues. Anything you dismiss on this tab shows up here so you can bring it back.',
        unignore: 'Unignore',
        unignoreHint: 'Move this issue back to the open list. Future scans will report it again.',
        unignored: 'Moved back to the open list.',
        unignoreAll: 'Unignore all {0} shown',
        unignoreAllConfirm: 'Move {0} ignored issue(s) back to the open list? Future scans will report them again.',
        page: 'Page {0} of {1}',
        restore: 'Restore',
        restoredAs: 'Restored as {0} (a file already existed at the original path — both copies are kept).',
        restoreHint: 'Puts the file back exactly where it was.',
        restored: 'Restored.',
        restoreFailed: 'Could not restore: {0}',
        restoring: 'Restoring…',
        spaceSavedToDate: 'Space saved to date: {0}',
        savedPerLibrary: 'Space trimmed per library',
        unassignedLibrary: 'Unassigned',
        dryRunBadge: 'dry run',
        saved: 'Saved.',
        confirmAuto: 'Automatic mode will change files whenever the server is idle, without asking. Removed files go to the recycle bin (unless set to permanent delete). Continue?',
        confirmPermanent: 'Permanent delete means removed files CANNOT be recovered. Are you sure?',
        confirmEmptyBin: 'Permanently delete everything in the recycle bin? Restore will no longer be possible for those files.',
        confirmReset: 'Clear all detected issues and probe/decode caches? Fix history and the recycle bin are kept. No media files will be touched.',
        resetDone: 'Scan data cleared — click "Scan now" to start fresh.',
        resetBusy: 'Cannot reset right now: a scan or fix is running. Wait for it to finish, then try again.',
        binHolding: 'Currently holding {0} file(s) ({1}).',
        binEmpty: 'The recycle bin is empty.',
        emptyAllTitle: 'Empty recycle bins',
        emptyAllIntro: 'Some recycled files sit outside what a normal empty clears: bins from an earlier bin path, or batches from an older MediaDash version. Emptying permanently deletes them; restore will no longer be possible.',
        emptyAllButton: 'Empty all locations ({0})',
        emptyCurrentButton: 'Current bin only ({0})',
        emptyCurrentTag: 'current bin',
        emptyLocationLine: '{0} batch(es), {1}',
        emptyCancel: 'Cancel',
        binViewHide: 'Hide contents',
        binViewShow: 'View contents',
        binRestoreHint: 'To put a file back, use the Restore button on the History tab.',
        binPerItemRestoreHint: 'Click Restore on any row to put that file back at its original location. Files without a "no history" tag can also be restored from the History tab.',
        modeOff: "Off — don't even scan",
        modeManual: 'Ask me first',
        modeAuto: 'Fix automatically',
        disposalRecycle: 'Recycle bin (recoverable)',
        disposalPermanent: 'Delete permanently',
        unitMbps: '{0} Mbps',
        unitPercent: '{0}%',
        unitDays: '{0} days',
        types: {
            ManualMove: {
                title: 'Moves and renames', hint: 'Complete moves approved in Files, including optional renames after re-encoding.', color: '#64748b', icon: 'drive_file_move'
            },
            Duplicate: {
                title: 'Duplicate copies', hint: 'The same movie or episode exists more than once.', color: '#d946ef', icon: 'content_copy',
                action: 'Delete this copy', actionHint: 'Queues deletion of this copy for the next fix run; the better copy stays.', approveAll: 'Delete all {0} duplicate copies shown',
                doRecycle: 'Delete the worse copy — recoverable from the recycle bin', doPermanent: 'Delete the worse copy — permanently'
            },
            Playability: {
                title: "Files that won't play", hint: 'These files are broken or unreadable.', color: '#eab308', icon: 'play_disabled',
                action: 'Repair file', actionHint: 'Try to repair. If repair fails, keep the file and ask you to approve deletion.', approveAll: 'Repair all {0} broken files shown',
                doRecycle: 'Try to repair; failed repairs are kept for you to review or delete', doPermanent: 'Try to repair; failed repairs are kept for you to review or delete'
            },
            Quality: {
                title: 'Files wasting space', hint: 'Bigger than your chosen quality ceiling.', color: '#3b82f6', icon: 'compress',
                action: 'Re-encode', actionHint: 'Queues a re-encode down to your quality ceiling for the next fix run; the original goes to the recycle bin.', approveAll: 'Re-encode all {0} files shown',
                doRecycle: 'Re-encode to the quality ceiling — original kept in recycle bin', doPermanent: 'Re-encode to the quality ceiling — original deleted permanently'
            },
            SubtitleLanguage: {
                title: 'Subtitles', hint: 'Strip unwanted-language subtitle tracks and (optionally) trim embedded fonts from .ass sidecars. Rebuilds the video file at the same path; the pre-edit copy goes to the recycle bin as a safety net.', color: '#8b5cf6', icon: 'subtitles',
                action: 'Clean up subtitles', actionHint: 'Queues removal of unwanted subtitle tracks (and font-optimize passes when enabled) for the next fix run. The file is rebuilt at the same path — the pre-edit copy goes to the recycle bin so you can revert.', approveAll: 'Clean up all {0} files shown',
                doRecycle: 'Rebuild file without unwanted subtitle tracks — pre-edit copy kept in recycle bin', doPermanent: 'Rebuild file without unwanted subtitle tracks — pre-edit copy deleted permanently'
            },
            SubtitleFonts: {
                title: 'Subtitles', hint: 'Embedded fonts in .ass sidecar subtitles that no style actually uses.', color: '#8b5cf6', icon: 'subtitles',
                action: 'Strip unused fonts', actionHint: 'Rewrites the .ass file with only the fonts your styles reference. Contents-only edit — the file stays a valid subtitle.', approveAll: 'Strip fonts from all {0} shown'
            },
            OrphanedDebris: {
                title: 'Orphaned debris', hint: 'Video-free folders, subtitle sidecars, trickplay folders and Jellyfin metadata folders whose anchor is gone.', color: '#78716c', icon: 'folder_delete',
                action: 'Delete orphan', actionHint: 'Removes the orphan (into the recycle bin unless you switch to permanent). Empty folders are hard-deleted regardless — nothing to recover.', approveAll: 'Clean up all {0} orphan(s) shown',
                doRecycle: 'Delete the orphan — recoverable from the recycle bin', doPermanent: 'Delete the orphan — permanently'
            },
            CorruptNfo: {
                title: 'Corrupt NFO files', hint: 'Zero-byte, malformed, or unrecognised-root .nfo sidecars that stop Jellyfin from reading metadata.', color: '#e11d48', icon: 'description',
                action: 'Delete corrupt NFO', actionHint: 'Removes the broken .nfo file. Jellyfin\'s next library scan tries to build metadata from providers or embedded tags.', approveAll: 'Delete all {0} corrupt NFOs shown',
                doRecycle: 'Delete the NFO — recoverable from the recycle bin', doPermanent: 'Delete the NFO — permanently'
            },
            HeavyTranscode: {
                title: 'Files that transcode a lot', hint: 'Source files Jellyfin has had to transcode on the fly recently. Re-encoding once turns future plays into direct-play, saving CPU on every play.', color: '#f97316', icon: 'local_fire_department',
                action: 'Re-encode this file', actionHint: 'Queues a one-off re-encode to a client-compatible profile so future plays don\'t transcode.', approveAll: 'Re-encode all {0} heavy-transcode files shown',
                doRecycle: 'Re-encode — original goes to the recycle bin', doPermanent: 'Re-encode — delete original permanently'
            },
            FailedTranscode: {
                title: 'Files that failed to transcode', hint: 'Source files whose most recent live transcode attempt failed. A targeted re-encode with MediaDash\'s settings usually succeeds where the on-the-fly attempt didn\'t.', color: '#dc2626', icon: 'sync_problem',
                action: 'Re-encode this file', actionHint: 'Queues a full re-encode using MediaDash\'s settings; more likely to succeed than the live attempt.', approveAll: 'Re-encode all {0} failed files shown',
                doRecycle: 'Re-encode — original goes to the recycle bin', doPermanent: 'Re-encode — delete original permanently'
            },
            EmbeddedCoverArt: {
                title: 'Duplicated embedded cover art', hint: 'Music / audiobook folders where each track carries its own copy of the same cover image but the folder has no shared cover.jpg. Extracting once (and optionally stripping the duplicates) saves ~500 KB per track.', color: '#a855f7', icon: 'album',
                action: 'Extract shared cover', actionHint: 'Writes a single cover.jpg for the folder and (if enabled) removes the duplicate copies embedded in each audio file.', approveAll: 'Extract covers for all {0} folders shown',
                doRecycle: 'Extract cover — originals go to the recycle bin before stripping', doPermanent: 'Extract cover — replace stripped audio files in place permanently'
            },
            AudioLanguage: {
                title: 'Unwanted audio', hint: 'Audio tracks in languages you don’t keep. Rebuilds the video file at the same path; the pre-edit copy goes to the recycle bin as a safety net.', color: '#10b981', icon: 'audiotrack',
                action: 'Remove audio tracks', actionHint: 'Queues removal of the unwanted audio tracks for the next fix run. The file is rebuilt at the same path — the pre-edit copy goes to the recycle bin so you can revert.', approveAll: 'Clean up all {0} files shown',
                doRecycle: 'Rebuild file without unwanted audio — pre-edit copy kept in recycle bin', doPermanent: 'Rebuild file without unwanted audio — pre-edit copy deleted permanently'
            },
            Misplaced: {
                title: 'Misplaced files', hint: 'A movie in the TV folder, or a TV episode in the Movies folder.', color: '#f97316', icon: 'drive_file_move',
                action: 'Move to correct folder', actionHint: 'Moves the file into the target folder configured in Settings → Libraries.', approveAll: 'Move all {0} misplaced files shown'
            },
            MissingSubtitles: {
                title: 'Missing subtitles', hint: 'No subtitle in any of your kept languages.', color: '#a78bfa', icon: 'closed_caption',
                action: 'Download subtitles', actionHint: 'Downloads a subtitle from your configured provider (OpenSubtitles etc.).', approveAll: 'Download subtitles for all {0} shown'
            },
            Stale: {
                title: 'Stale content', hint: 'Nobody has played it in a long time.', color: '#64748b', icon: 'hourglass_bottom',
                action: 'Review', actionHint: 'Detect-only for now — MediaDash surfaces the list, you decide whether to delete or keep.', approveAll: 'Review all {0} stale items shown'
            },
            MalwareRisk: {
                title: 'Suspicious executables', hint: 'Programs or scripts sitting in a media folder — almost always malware from a pirated release.', color: '#dc2626', icon: 'gpp_bad',
                action: 'Delete this file', actionHint: 'Queues deletion for the next fix run; the file goes to the recycle bin unless you switch it to permanent.', approveAll: 'Delete all {0} suspicious files shown',
                doRecycle: 'Delete the executable — recoverable from the recycle bin', doPermanent: 'Delete the executable — permanently'
            },
            LargeTrickplay: {
                title: 'Trickplay space savings', hint: 'Scrub-bar preview thumbnails are still raw JPG — re-encoding to WebP typically halves the disk footprint.', color: '#14b8a6', icon: 'compress',
                action: 'Re-encode as WebP', actionHint: 'Runs the bundled jellyfin-ffmpeg with libwebp. The .jpg extension is kept so Jellyfin clients continue serving them exactly the same way.', approveAll: 'Re-encode all {0} trickplay folders shown'
            },
            Ungrouped: {
                title: 'Ungrouped media', hint: 'Loose files or oddly-named folders that should sit under a per-title (or per-franchise) parent folder.', color: '#f59e0b', icon: 'create_new_folder',
                action: 'Group under title folder', actionHint: 'Moves the file or folder into a folder named after the identified title (franchise stem when siblings share it).', approveAll: 'Group all {0} items shown'
            },
            CorruptArtwork: {
                title: 'Missing or corrupt artwork', hint: 'Jellyfin-managed artwork with a missing file or a file that fails validation.', color: '#e11d48', icon: 'broken_image',
                action: 'Delete corrupt artwork', actionHint: 'Removes the broken image; Jellyfin\'s next library scan re-fetches from providers.', approveAll: 'Approve all {0} artwork fixes shown'
            },
            FormatCompatibility: {
                title: 'Unsupported format', hint: 'Ebooks and comics in formats Jellyfin cannot display — converted to EPUB/PDF or CBZ so the browser reader works.', color: '#7c3aed', icon: 'inventory_2',
                action: 'Convert', actionHint: 'Converts to the target format. The original moves to the recycle bin unless you switched disposal to permanent.',
                doRecycle: 'Convert — original kept in recycle bin', doPermanent: 'Convert — original deleted permanently',
                approveAll: 'Convert all {0} incompatible files shown'
            }
        },
        // Sorted alphabetically by display name so both the <datalist> picker and the chip
        // renderer offer languages in a predictable order — appending new codes at the end
        // used to leave them hidden below the "P" fold in the popup. Locked to a curated
        // major-European + major-Asian set (29 + 15 = 44 codes); ISO 639-2/T terminological
        // form (deu / fra / zho / …), which LanguageHelper.Normalize also maps from the
        // bibliographic aliases (ger / fre / chi) and 2-letter tags on the C# side.
        languages: [
            ['sqi', 'Albanian'],
            ['ara', 'Arabic'],
            ['ben', 'Bengali'],
            ['bul', 'Bulgarian'],
            ['zho', 'Chinese (Mandarin)'],
            ['hrv', 'Croatian'],
            ['ces', 'Czech'],
            ['dan', 'Danish'],
            ['nld', 'Dutch'],
            ['eng', 'English'],
            ['est', 'Estonian'],
            ['fil', 'Filipino'],
            ['fin', 'Finnish'],
            ['fra', 'French'],
            ['deu', 'German'],
            ['ell', 'Greek'],
            ['heb', 'Hebrew'],
            ['hin', 'Hindi'],
            ['hun', 'Hungarian'],
            ['isl', 'Icelandic'],
            ['ind', 'Indonesian'],
            ['ita', 'Italian'],
            ['jpn', 'Japanese'],
            ['kor', 'Korean'],
            ['lav', 'Latvian'],
            ['lit', 'Lithuanian'],
            ['msa', 'Malay'],
            ['nor', 'Norwegian'],
            ['orig', 'Original language'],
            ['fas', 'Persian (Farsi)'],
            ['pol', 'Polish'],
            ['por', 'Portuguese'],
            ['pan', 'Punjabi'],
            ['ron', 'Romanian'],
            ['rus', 'Russian'],
            ['srp', 'Serbian'],
            ['slk', 'Slovak'],
            ['slv', 'Slovenian'],
            ['spa', 'Spanish'],
            ['swe', 'Swedish'],
            ['tam', 'Tamil'],
            ['tha', 'Thai'],
            ['tur', 'Turkish'],
            ['ukr', 'Ukrainian'],
            ['urd', 'Urdu'],
            ['vie', 'Vietnamese'],
            ['cym', 'Welsh']
        ]
    };
})();
