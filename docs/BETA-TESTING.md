# MediaDash beta channel

> [!IMPORTANT]
> **Only approved builds go to the beta channel.** The `beta` branch and the beta repository are
> **not** a mirror of day-to-day development. A build is promoted to beta only after the maintainer
> (crackruckles) has explicitly approved it for wider testing. Work-in-progress commits, experiments,
> agent/CI branches and "let's see if this works" builds never go to `beta`.
>
> Promotion is a deliberate, manual step: `tools/release-beta.ps1` refuses to run without
> `-Approved`, refuses to run from any branch other than `beta`, and refuses a dirty tree. Do not
> push to `beta` or edit `manifest-beta.json` by hand.

## For testers

1. In Jellyfin: **Dashboard → Plugins → Repositories → +** and add:

   ```
   https://raw.githubusercontent.com/crackruckles/MediaDash/beta/manifest-beta.json
   ```

   Name it something like `MediaDash (beta)`. You can keep the stable repository too — Jellyfin
   installs whichever version number is highest across all your repositories.

2. Jellyfin's **Update Plugins** scheduled task (on by default) downloads new beta builds
   automatically. To pick one up straight away: refresh the repository in **Catalog**, update
   MediaDash, then restart Jellyfin.

3. Report problems at <https://github.com/crackruckles/MediaDash/issues> and include the version
   shown under **My Plugins** (for example `1.0.8.2`). The **Errors** tab's bug-report button
   collects the diagnostics for you.

**Back to stable:** remove the beta repository. You'll move back to stable automatically the next
time a stable release with a higher version number ships. To downgrade immediately, uninstall
MediaDash, restart, and reinstall from the stable catalog (your scan data and settings are kept).

Beta builds keep the same safety defaults as stable (dry-run on, recycle bin on), but they have
had less real-world exposure. Test on a server you're comfortable with.

## For the maintainer — promoting an approved build

```powershell
git switch beta
git merge --ff-only <approved-commit-or-branch>   # bring beta up to the approved source
./tools/release-beta.ps1 -Version 1.0.8.3 -Changelog "What testers should try." -Approved
```

The script:

1. Checks you're on `beta`, the tree is clean, `-Approved` is present, and the version is higher
   than every version already in `manifest-beta.json` (Jellyfin only updates to a *higher*
   number, so every beta needs a new 4-part version — `1.0.8.2`, `1.0.8.3`, …).
2. Runs the full offline test suite, then pushes `beta` so the release tag points at a commit that
   exists on GitHub. Beta pushes use `--no-verify`: they deliberately skip the source-bound
   acceptance-evidence gate (`tools/reported_issue_gate.py`), because gathering that evidence is
   what beta testing is for. **Stable releases and normal pushes still require the full gate.**
3. Builds Release, checks the sidecar DLLs, zips the same way `release.ps1` does.
4. Creates a GitHub **pre-release** `v<Version>-beta` (never marked "latest"), re-downloads the
   asset and verifies its MD5.
5. Prepends the version to `manifest-beta.json` (both `targetAbi` entries), commits and pushes it.

Stable releases are unaffected: `manifest.json` on `main` and `tools/release.ps1` keep working
exactly as before. When a beta is ready for everyone, cut a normal stable release with `tools/release.ps1`.
Use a version number **higher** than the last beta if any code changed since it (Jellyfin won't
replace an installed `1.0.8.2` with another `1.0.8.2`), so beta testers are moved onto stable too.
