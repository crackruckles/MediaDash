# Roadmap acceptance test matrix

Reviewed 2026-09-19 against `tools/roadmap/roadmap.json`: 70 cards, 29 unfinished,
including 2 superseded. Each unfinished card appears exactly once below.
These are planned acceptance tests, not completed tests or implementation claims.
See [current coverage review](REGRESSION-COVERAGE-REVIEW-2026-09-19.md) for RG cases,
existing test starting points, harness defects and the release evidence contract.

Before marking a card complete, link its implemented tests and current run evidence
from the card notes. Include a positive case, a refusal/failure case and a recovery
or persistence case. All mutating features inherit RG-01/02/04/07/11; media outputs
also inherit RG-05/06/12; UI surfaces inherit RG-10. Tests below supplement those
shared cases. Choose unresolved semantics in a repository spec before writing a
test that silently makes the product decision. Do not mark a research gate passed
because a helper exists.

## 1.0.8.0 — stabilisation

| Card ID / feature | Required acceptance cases and evidence |
|---|---|
| `i-176-pre-release-sweep` — release/docs reconciliation | Map each fixed finding to a failing-before/passing-after reproducer. Reconcile release number, package contents, manifest ABI/checksum and user-facing changelog against one built artifact. Fresh full run records failures/skips honestly; intentionally broken harness check must prevent a green release result. Documentation-only reconciliation must not mark runtime cases passed. |
| `i-108-cross-platform-verification` — OS/host matrix | Windows and Ubuntu Release unit jobs; install and upgrade the same package on both supported Jellyfin host lines. Scan/fix/restore a generated fixture; exercise case/drive/UNC and real cross-volume behavior. Interrupt a swap and restart. Missing runtime/native dependency or host load failure fails the job and retains logs. Track macOS/Docker/manual gaps explicitly. |

## 1.1.0.0 — control and safe repair

| Card ID / feature | Required acceptance cases and evidence |
|---|---|
| `2d2e24da-197a-4485-9045-68ce3152da85` — per-library policies | Two libraries with opposing policies plus overlapping roots: prove documented precedence and displayed effective policy match execution. Change policy after queueing; stale work uses the new policy. Old configs migrate to equivalent defaults; save/restart preserves overrides without changing other libraries. Preview and fixer resolve the same policy. |
| `i-110-protected-media` — Never modify | Protect title, folder and collection, then scan/queue each applicable fixer: no media mutation. Protection added after approval is rechecked. Refresh/move/item-ID change and restart preserve the documented identity rule. Removing protection deliberately permits work again; Ignore alone must not create protection. Cover manual and combined-fix entry points. |
| `i-bl-12-rung4a-audio-reencode` — audio-only repair | Damaged audio plus healthy video reaches audio repair before full re-encode; prove video packet preservation and clean audio decode. Failed audio repair leaves a recoverable source and escalates only under the selected policy. Multiple tracks, unknown language and last-audio guard preserve required content; history identifies the successful rung. |
| `i-bl-13-repair-only-row-action` — Repair versus Remove | Same irreparable fixture: Repair never deletes/recycles it merely because repair failed; Remove requires the separate authorised action and observes disposal policy. Reject forged/stale action values; dry-run leaves hashes unchanged. Verify button copy, restart-persisted action intent and history distinguish the operations. |
| `i-bl-14-repair-preview` — common preview | For every fixer, preview proposed paths, streams, bytes/space and disposal, then compare execution against it. Preview itself changes no file/queue state. Change source, policy, protection or destination after preview; refuse/recompute rather than execute stale consent. Missing probe/provider produces an honest unavailable preview, never fabricated certainty. Define the shared preview contract first. |
| `i-bl-15-salvage-report` — before/after history | Fixture loses only explicitly authorised damaged content: report exact stream/duration/size changes and retained recovery path. Failure, cancellation and combined passes produce consistent nonduplicated history. Upgrade/reopen DB and paginate/export history without losing technical details or reporting estimates as measured savings. |
| `i-110-minimum-savings` — encoding threshold | Output savings just below, exactly at and above byte/percentage thresholds, including both thresholds configured and zero/invalid values. Below-threshold output is discarded and source hash retained; no retry loop. Larger output still rejected. Repair/compatibility work uses its own acceptance rules. Pin percentage rounding and combined-threshold semantics in the spec. |

## 1.2.0.0 — reliable automation

| Card ID / feature | Required acceptance cases and evidence |
|---|---|
| `i-120-retry-policy` — bounded retry/Needs attention | Fake clock and persistent DB: transient failures back off to a ceiling; permanent failures stop; restart preserves attempts/deadline. Scheduling, playback, disabled mode and quota deferrals follow documented counting rules. Retry now after cause resolution succeeds. Two workers cannot consume one attempt twice; one blocked file does not starve healthy work. Test migration/API/UI for the new state. |
| `i-bl-16-plugin-data-growth-controls` — retention/cache/DB space | Advance clock around each retention/TTL boundary with live queue, history and cache rows. Unavailable mount must not be treated as deleted media. Dry-run maintenance reports only; real cleanup preserves protected/live data. Low DB-space and interrupted/locked VACUUM leave a reopenable DB. Upgrade populated historical schemas and validate retention after restart. |
| `i-bl-11-space-cliff-warning` — volume warnings | Source/output/plugin DB on same and different volumes; exact threshold and unknown/unmounted volume. Warn without declaring unknown space safe or healthy, deduplicate recurring alerts and recover after space returns. Save/restart preserves settings; disabling warnings does not bypass the destructive-operation space guard. |
| `3f821f32-bb56-4708-9ba9-fbb7e6906c35` — progress/wait reasons | Zero, one and combined tasks; queue changes mid-run; quota/lock/playback/space wait; cancel and failure. Progress never exceeds its documented denominator and waiting is not presented as failure. Reconnect/restart cannot show a stale running task forever. API and UI agree on totals and final outcome. |
| `i-bl-17-expanded-error-reporting` — diagnostic bundles | Default opt-out and explicit opt-in across upgrade/restart. Seed secrets/paths into nested tags, stderr and command arguments; verify capture/export preview redaction at every nesting level. Enforce time/size/count caps, valid truncated representation and deletion lifecycle. Failed/timed-out capture must not lose the original diagnostic or block repair. Sharing is user initiated; metadata cannot be claimed to reproduce arbitrary corruption. |
| `i-120-digest-alerts` — summaries/alerts | Opt-out causes no delivery; opt-in summary counts unique completed work correctly across retries/combined fixes. Clock/timezone and daily/weekly boundaries do not duplicate delivery after restart. Provider outage/retry cannot block fixes or disclose unredacted paths; disabling stops pending deliveries. Use a fake destination, never real recipients in automated tests. |

## 1.3.0.0 — audio and subtitles

| Card ID / feature | Required acceptance cases and evidence |
|---|---|
| `i-180-02` — audio conversion | Bring memory-only design into repository first. Fixture matrix covers immersive/channel/bitrate/sample-rate triggers and every preset, with negative controls that should not convert. Retain-original option adds the compatible track and preserves original packet/metadata identity; removal occurs only with explicit policy. Combined pass remains one encode; incompatible muxer, cancellation and insufficient space preserve source. |
| `bb59c42f-6e70-4d9e-8ef2-7d49fda0bbda` — track retention policy | Original language, commentary, audio description, forced/SDH, duplicate/unknown tags, multiple preferred languages and a single audio stream. Define a table of expected kept/removed indexes for each policy and compare preview, scanner and actual output. Ambiguous metadata preserves content or requests a decision; restart and old-config migration retain defaults. |
| `i-180-05` — subtitle extraction | Text and bitmap formats, forced/SDH language naming, font/style dependencies and multiple streams. Destination file/directory collision and failure after one extraction must not cause partial stripping of embedded subtitles. Stage/verify all required sidecars before media replacement; cancellation/restart remains recoverable. Compare subtitles and timings and confirm Jellyfin discovers/selects the sidecar. |

## 1.4.0.0 — organiser and duplicate automation

| Card ID / feature | Required acceptance cases and evidence |
|---|---|
| `i-180-03` — media organiser | Doctor Who 1963/2005/2024 and Silo (2023) fixtures reuse reboot naming semantics; preserve external IDs and timestamps. Move video plus subtitles/art/NFO with target collisions, genuine cross-volume copy and interruption. Preview matches destinations and protected titles never move. Verify watch state, item identity and Recently Added on both hosts; document host limitations before enabling automatic moves. |
| `d58754bb-ad8b-4e49-b99f-11869b6ae6fb` — duplicate auto-fix | Exact duplicate positive control versus different edition/cut, different language/HDR, different content with same name and unavailable winning copy. Only the defined confidence threshold can auto-fix, subject to protection and fix-time winner revalidation. Recycle/restore hash checks prove the losing file remains recoverable; library overlap cannot double-delete. Include threshold boundary and a protected best copy. |

## 1.5.0.0 and 1.6.0.0

| Card ID / feature | Required acceptance cases and evidence |
|---|---|
| `i-180-01` — Library/Overview redesign | Empty, one/many libraries, overlapping selections, stale maintenance data and unavailable drive. API totals match filtered underlying rows; cards show effective policy and blocked work. RG-10 includes all drill-downs, slow/error responses and large counts. Do not resurrect tests that require the previously reverted fields to remain absent once the new contract intentionally adds them. |
| `d04791b1-2625-4da9-bba7-dfa816ae71f5` — Leaving soon/Keep | Fake time at countdown boundaries; new watch activity, multiple users and series grouping. Keep persists across restart/rescan and cancels the review countdown under documented identity rules. Expiry remains review-only and does not delete media. Include deleted user, missing activity data and unavailable library; unknown activity must not be treated as confirmed unwatched. |

## Backlog research gates

| Card ID / feature | Required evidence before scheduling implementation |
|---|---|
| `i-bl-02` — legacy hardware decode | On each proposed GPU/backend, fixture for mpeg2/vc1 and an unsupported profile: demonstrate decode/scale/encode support, output integrity and bounded fallback. Compare throughput to software; mock argument tests alone cannot establish hardware support. Record driver/ffmpeg/device versions. |
| `i-bl-03` — AMF/VideoToolbox decode | Demonstrate GPU-resident scaling on actual supported hardware and current bundled ffmpeg. Unsupported filter/device fails safely to the documented fallback. Measure throughput and decode integrity; no whitelist expansion based only on backend name. |
| `i-bl-05` — configurable repair encoder | Define quality/HDR intent and allowed codecs first. Unsupported/invalid setting, unavailable hardware and old config select a safe documented outcome. Real fixtures verify stream retention, space, output validation and failure recovery for each offered encoder. |
| `i-bl-10-monthly-defrag` — drive maintenance | Product/OS-support decision first. Identify SSD/network/unknown drives and prove no unsupported maintenance launches. Explicit consent, least required permissions, cancellation and recovery must be specified. Research stays blocked without a safe platform-specific test environment; do not run experiments on user media drives. |
| `i-future-stale-auto-delete` — automatic stale deletion | Requires explicit change to PLAN.md's detect-only scope plus the Leaving soon feature. Define all-user veto/Keep, activity uncertainty, grouping and retention. Test final activity change immediately before execution, offline mounts and restore. Countdown expiry alone is never sufficient evidence to delete. |

## Superseded cards

| Card ID | Canonical coverage |
|---|---|
| `i-bl-06` | Audio-only repair cases belong to `i-bl-12-rung4a-audio-reencode`; keep this card superseded, not implemented. |
| `i-bl-09` | Library breakdown cases belong to `i-180-01`; keep this card superseded, not implemented. |

## 2026-10-13 scanner/fixer reliability fixes — 1.1.0.0

These 20 open bug cards map one-to-one to the numbered findings in the [scanner/fixer review](SCANNER-FIXER-RELIABILITY-REVIEW-2026-10-13.md). R01–R11 are must-fix safety defects; R12–R20 are detection, retry and history reliability work. They correct current behavior and are separate from later organiser, preview and retry features. The review reproductions demonstrate present defects; they do not satisfy these planned acceptance cases. All mutating cases also inherit the shared RG gates above; completion requires fresh passing evidence in card notes.

| Card ID / bug | Required acceptance cases and evidence |
|---|---|
| `i-110-scanner-fixer-r01` — R01: Sorter cleanup can delete the only remaining copy | Copy into staging, verify it and the source identity, promote without overwriting, then dispose of the source. Preserve a recovery file on any failure after source disposal; never clean up the sole survivor. A failed move can permanently lose media. Add an injected final-rename-failure test and a real cross-volume fixture. |
| `i-110-scanner-fixer-r02` — R02: Orphan-folder revalidation overlooks newly added music, books, comics, and pictures | Use the scanner's shared all-media set for the fix-time check. Keep the conservative refusal on read failure. A stale folder issue can remove newly imported non-video media. Test each supported media family appearing between scan and fix. |
| `i-110-scanner-fixer-r03` — R03: Folder operations can bypass protection on children | Reuse `IsAnyDescendantProtected` before moving/deleting a tree, and check each file before a folder-wide rewrite. Apply the checks at commit time too. Never-modify rules can still allow protected files to move, disappear, or change. Test a child rule with an unprotected queued parent. |
| `i-110-scanner-fixer-r04` — R04: Failed playability repair falls into deletion without a fresh safety gate | Capture identity before repair and require unchanged identity, current policy/protection, cancellation clearance, and a still-broken verdict before fallback disposal. Treat inability to attempt repair as deferred rather than failed repair. Infrastructure failure or a stale verdict can become removal of a recoverable/newly healthy file. Test source replacement, policy changes, and insufficient repair space. |
| `i-110-scanner-fixer-r05` — R05: Replacement staging checks only for nonzero size | Capture verified output length before staging and require the staged length to match. Reverify staged content when the move crosses volumes and stronger fidelity evidence is needed. Nonempty partial staging output can replace good media. Add a named-stage truncation fault test. |
| `i-110-scanner-fixer-r06` — R06: Source and policy checks precede potentially long staging | Repeat source identity and current-policy checks after staging, immediately before disposal; preserve the staged output on refusal. This narrows the race without claiming to make concurrent filesystem mutation fully atomic. Long staging can invalidate the safety decision. Test source and policy changes inside the staging seam. |
| `i-110-scanner-fixer-r07` — R07: NFO and artwork deletion trusts stale corruption findings | Reuse `NfoScanner.EvaluateFile` and `ArtworkScanner.EvaluateFile` before mutation. Return NoChange when healthy, and distinguish read/permission failure from confirmed corrupt content. Valid regenerated metadata or custom metadata can be removed. Test corrupt → valid at the same path between scan and fix. |
| `i-110-scanner-fixer-r08` — R08: Cover-art cleanup assumes different track images are duplicates | Compare each extracted image with the shared cover before stripping. Preserve differing artwork and require explicit consent for any broader removal. Unique artwork is lost, permanently when original disposal is permanent. Test two tracks with distinct embedded covers. |
| `i-110-scanner-fixer-r09` — R09: Cover stripping verifies stream count but not audio completeness | Compare source and output audio duration and retained stream identity, using frame/packet evidence when metadata is inconclusive. Refuse an unverifiable rewrite. Truncated audio can replace the source. Test a readable shortened output with unchanged stream count. |
| `i-110-scanner-fixer-r10` — R10: Subtitle-font and trickplay overwrites lack source and late-policy checks | Capture identity before reading, check identity/cancellation/current policy at commit, and use an owned unique temporary path. Verify output before promotion; preserve recovery where the original is valuable. Concurrent subtitle edits and regenerated images can be lost. Test source mutation and cancellation/policy changes before promotion. |
| `i-110-scanner-fixer-r11` — R11: Invalid UTF-8 subtitles are accepted and rewritten with damaged text | Decode with strict UTF-8 error handling and refuse unsupported encoding, preserving the existing UTF-16 refusal. Dialogue text can be permanently garbled. Add invalid UTF-8 and valid Unicode round-trip tests. |
| `i-110-scanner-fixer-r12` — R12: Detect-only duplicate findings suppress other scanners | Restrict the shortcut to actually authorised, validated removals, or remove it from detection. Apply protection filtering before deriving that set. Detection silently misses problems. Test detect-only and below-threshold duplicates alongside a real playability/language finding. |
| `i-110-scanner-fixer-r13` — R13: Incomplete scans can erase previously detected issues | Carry completed scope/inconclusive paths into persistence and replace only successfully examined paths. A dependency failure should preserve that scanner's old detections. Issues disappear during infrastructure trouble and reappear later. Test an unavailable root and stale-user-API failure with existing detected rows. |
| `i-110-scanner-fixer-r14` — R14: Some recursive walkers follow links and can revisit the same tree | Skip reparse points consistently with the existing NFO/suspicious-file enumeration pattern. Keep filesystem iteration exceptions inside the enumeration loop. One linked directory can disrupt a whole scan. Test a controlled link cycle on Windows and Linux. |
| `i-110-scanner-fixer-r15` — R15: Disk-walking scanners bypass the unreachable-root filter | Feed the task's usable root set into disk scanners, or apply the same shared root-health exclusion before their walks. Preserve findings for excluded roots as in #13. A NAS outage can still hang a scan. Validate with an isolated unavailable share, not production media. |
| `i-110-scanner-fixer-r16` — R16: Hearing-impaired subtitle scan and fix rules disagree | Agree on whether HI is required or preferred; enforce the same rule in both paths. Recheck current subtitle availability before search/download and late policy after the provider search. Users get repeat issues and wasted provider quota. Test a provider offering only non-HI hits and subtitles added after detection. |
| `i-110-scanner-fixer-r17` — R17: Font scanning matches subtitles belonging to other videos | Require the subtitle stem to equal the video stem or follow it with a dot, matching the existing orphan-sidecar pairing convention. Unrelated subtitles can be offered for changes. Test exact, dotted-language, sequel, and longer-title filenames. |
| `i-110-scanner-fixer-r18` — R18: Book/comic probes do not reliably distinguish corrupt payloads from protected content | Validate required EPUB structure and bounded payload reads; inspect real encryption metadata before labelling DRM. Check comic payloads with cancellation and decompression limits rather than trusting directory listings. Broken books can be declared healthy and corrupt comics get misleading advice. Retain these two probes as regression fixtures and add a damaged later-page case. |
| `i-110-scanner-fixer-r19` — R19: Partial cover/trickplay success hides files needing retry | Track failures separately from deliberate no-benefit skips. Let existing covers support retrying unfinished strips, and suppress only actually completed/no-benefit sprites rather than every file older than folder success. Temporary failures leave permanently unfinished optimisation. Test one success plus one failure, then rescan and retry. |
| `i-110-scanner-fixer-r20` — R20: Cancellation after a committed repair can lose its history record | Make optional post-commit measurement best-effort and ensure the committed result always reaches the recorder. Cancellation must stop new work without erasing a completed effect. Users can see queued/stale issues after successful repair and lose normal restore/history visibility. Inject cancellation immediately after commit and assert recorded paths/status. |

## Keeping this matrix complete

When adding or moving cards, update this file and the card's evidence links in the
same change. A planned row is not a pass. The following read-only check catches
unfinished cards with no matrix row, duplicate rows, and stale rows:

```powershell
$board = Get-Content tools/roadmap/roadmap.json -Raw | ConvertFrom-Json
$matrix = Get-Content docs/testing/ROADMAP-TEST-MATRIX.md -Raw
$rowIds = @([regex]::Matches($matrix, '(?m)^\| `([^`]+)`') |
    ForEach-Object { $_.Groups[1].Value })
$openIds = @($board.items | Where-Object { -not $_.completedAt } |
    ForEach-Object { $_.id })
$missing = @($openIds | Where-Object { $_ -notin $rowIds })
$stale = @($rowIds | Where-Object { $_ -notin $openIds })
$duplicates = @($rowIds | Group-Object | Where-Object Count -gt 1)
if ($missing.Count -or $stale.Count -or $duplicates.Count) {
    throw "Matrix mismatch: missing=$missing stale=$stale duplicate=$($duplicates.Name)"
}
"Covered $($openIds.Count) unfinished roadmap cards (specifications only)."
```
