# Roadmap

Single-page kanban for planning MediaDash releases. Not a plugin feature — dev tool.

## Open it

Double-click `index.html`, or open the file path directly in Chrome/Edge. `file://` works — no server needed.

## First launch

The welcome card gives you two options:

- **New file** — pick a location for `roadmap.json`. The page seeds with the current MediaDash version buckets (Backlog, 1.0.7.5, 1.0.7.6, 1.0.8.0), empty. Every change autosaves to the file you picked.
- **Open existing** — load a previously-saved `roadmap.json`.

Recommended path: `C:\dev\mediadash\tools\roadmap\roadmap.json` (already gitignored — see the repo `.gitignore` entry `tools/roadmap/*.json`).

## Using it

| Action | How |
|---|---|
| Add item to a column | `+ Add item` at the bottom of the column, or `+ Version` in the header to add a new column first. |
| Move item between versions | Drag the card, drop on another column. |
| Edit item (title, notes, kind) | Click the card. `Save` commits, `Cancel` reverts. |
| Delete item | Hover the card, click the `×` in the corner. |
| Rename version | Click the column name. Enter commits, Escape cancels. |
| Delete version | Click the `⋯` next to the column name. Any items in it move to Backlog. |

Backlog is special — it can't be deleted; it's the fallback for orphaned items.

## What Claude sees

Same JSON file. Ask Claude to read `tools/roadmap/roadmap.json` when you want it to know what's queued for which version.

Schema:

```json
{
  "versions": [{ "id": "v-1-0-8-0", "name": "1.0.8.0", "order": 3 }],
  "items": [{
    "id": "01H7...", "title": "…", "notes": "…", "kind": "feature",
    "versionId": "v-1-0-8-0",
    "createdAt": "2026-09-05T…", "updatedAt": "2026-09-05T…"
  }]
}
```

## Browser support

Autosave uses the File System Access API — Chromium-based only (Chrome, Edge, Brave, Opera). Firefox and Safari get a graceful fallback: manual `Download` button and no autosave. If you're on those, you'll need to re-download after each session.

## Not doing (kept out on purpose)

- Multi-user sync, remote hosting, auth — offline personal tool.
- Filter / search — small dataset, scroll works.
- Undo history — the JSON file is the history; keep backups if you care.
- Import from CHANGELOG / spec files — seed manually.
- Subtasks — items are flat.

If any of these become painful, add them then. Not before.
