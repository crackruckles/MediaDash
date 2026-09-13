# MediaDash Codebase Audit

Two persistent files coordinate a multi-session audit:

- **`findings.md`** — running list of issues discovered. Newest at top. Each entry has a stable
  ID (`F-NNN`), severity, area, repro steps, and status. Fix work reads from here.
- **`progress.md`** — checklist of what's been audited vs queued. Update as you go so a future
  session picks up where you stopped instead of re-checking done work.

Everything the audit produces lives under `docs/audit/`. Not shipped with the plugin — dev tool
only. `.gitignore` doesn't exclude it, so findings are versioned alongside the code they describe.

## For the audit agent (any session)

**Before you start any work:**

1. Read `progress.md` end-to-end. Anything marked `~ partial` is where you resume.
2. Skim the last 5-10 entries in `findings.md` so you don't re-file duplicates.

**As you work:**

3. Every substantive discovery gets an entry in `findings.md` using the template near the top.
4. Every completed audit target gets flipped `○ → ✓` (or `~` with a resume note) in
   `progress.md`.
5. Repro steps must be concrete enough that a fix-session agent can reproduce without you.

**Hard constraints:**

- Local Jellyfin at `localhost:8099` (test/test) is fair game — restart it, wipe its DB,
  poke its API, generate broken fixtures under `C:\dev\mediadash\tools\`.
- **Never** touch `192.168.1.117` (production server).
- **Never** post to GitHub — no `gh issue comment`, `gh pr create`, `gh pr review`, or any
  `gh api` call that mutates. Read-only (`gh issue view`, `gh pr list`, `gh api GET ...`) is OK.
- Don't commit or push. Findings + progress files are the deliverable; the user runs git.

**Stopping criteria:**

- Context budget approaching a soft limit — checkpoint into `progress.md` (mark in-flight
  work as `~ partial` with a specific resume hint) and hand off with a short summary.
- Twenty substantive findings without a break — checkpoint and hand off so triage doesn't
  get overwhelmed in one session.
- Genuinely nothing else queued — mark all done and say so.
