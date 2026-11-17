# Issue #87: original-audio safety investigation

28 October 2026. A reproducible metadata-failure safety gap is repaired locally. **The reporter's exact Rafa case remains unconfirmed, and #87 is not complete.** No server was started or restarted; no plugin was deployed, no production media was touched, and no commit, push or GitHub comment was made.

[GitHub issue #87](https://github.com/crackruckles/MediaDash/issues/87) still contains two screenshots and no comments. The settings show Original language only, Ask me first, and permanent disposal. The episode cards propose removing Spanish and keeping English. Neither screenshot supplies stream dispositions, the server version, or item/series original-language metadata. The user confirmed that the GitHub issue is the only available reporter evidence.

## Reproduction and repair

`LanguageHelper.ResolveLanguages` catches `TargetInvocationException` from reflected original-language metadata access. Previously it ignored that failure and continued with container original flags. Controlled input with English flagged original, Spanish unflagged, and a throwing inherited-metadata lookup therefore selected Spanish for removal. Both audio and subtitle removal regressions failed before the repair.

The resolver now throws an explanatory exception with the underlying failure attached. The existing scanner and fixer error handling stops cleanup and reports the metadata problem. An approved stale finding stays queued instead of being treated as a successful cleanup or a terminal no-change result. Explicit language selection still works without requesting original-language metadata.

The shared resolver reaches audio/subtitle scanning, single and combined track cleanup, external subtitle selection, retained tracks during re-encoding, and missing-subtitle scanning/downloading. Its callers were traced before editing. No caller-specific selection workaround was added.

The file/queue regression uses a disposable file, cached controlled probe JSON, a faulting metadata item, and a real SQLite database. It exercises `AudioLanguageScanner`, `TrackFixer`, `FixExecutor`, and `FixResultRecorder` with Original only and permanent disposal. It verifies a failed history row, zero savings, preserved queued approval and identical source bytes. **This is a fault-injection test; its four-byte source is not a playable Rafa reproduction or a real FFmpeg cleanup workflow.**

## Validation

- Initial selection regressions: two failures, ten passes. Expanded scanner reproduction: three failures, thirteen passes.
- Queue-preservation regression and selection checks failed against the intermediate null-return approach; that approach was replaced because a no-change outcome could hide the issue.
- Final focused selection/track/subtitle checks: **157 passed, zero failed, zero skipped**.
- Final full offline .NET run: **1,748 passed, three existing failures, zero skipped** (1,751 tests). The external UNC category is explicitly excluded; it is unrelated to this change and was not rerun.

Evidence, relative to the repository root:

- [Reporter settings screenshot](../../artifacts/issue-87-20261028/reporter-settings.png)
- [Reporter issue cards](../../artifacts/issue-87-20261028/reporter-issues.png)
- [Failing selection/scanner regressions](../../artifacts/issue-87-20261028/red/flow-red.trx)
- [Failing queue-preservation regression](../../artifacts/issue-87-20261028/red/queue-red.trx)
- [Passing focused run](../../artifacts/issue-87-20261028/green/focused-final.trx)
- [Full offline run](../../artifacts/issue-87-20261028/offline/offline-final.trx)

Raw artifacts are local ignored evidence. No reporter-acceptance record was added to the push gate.

## Remaining limits

This repair covers a **failed** metadata lookup. A missing optional metadata API still permits the existing container-flag fallback. Incorrect English metadata or an English-only original flag can still cause Spanish to be selected. There is no safe basis in these screenshots to infer Spanish from track order or to distinguish a wrong flag from a legitimately English original.

Closing #87 still needs the episode's ffprobe stream languages/dispositions, Jellyfin version, and episode/series original-language values. Those inputs must reproduce the reported decision before claiming its cause or correction. Supported-host scan, approval, real cleanup, retained-stream hashes, rescan and restoration remain unvalidated for this report.

The tree remains **DO NOT PUSH/PUBLISH**. The existing full-suite failures are:

- `ReportedIssueRegressionTests.Issue92_OutputHeaderFailureMustNotBeClassifiedAsSourceCorruption`
- `ReportedIssueRegressionTests.Issue43_CompanionCollisionMustNotReportACompletedMove`
- `ReportedIssueRegressionTests.Reddit_RefusedMoveStaysVisibleAndDoesNotClaimACompletedFix`
