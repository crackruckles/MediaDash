<!-- Thanks for the PR! A few quick checks before submitting. -->

## Summary

<!-- 1-3 lines: what changes and why. -->

## Testing

<!-- What did you verify? Local build, unit tests, live Jellyfin install? -->

- [ ] `dotnet build` clean
- [ ] `dotnet test` passes
- [ ] Manual verification in a live Jellyfin server (if UI or fixer changes)

## Safety invariants

<!-- Only tick each box if you actually understood and preserved the invariant. Leave unchecked if not applicable. -->

- [ ] Does not touch files outside configured library paths.
- [ ] Does not remove the last audio track or the last video stream.
- [ ] Does not replace a file whose transcode/remux failed verification.
- [ ] Does not move a file to a target outside a Jellyfin library root.
- [ ] Free-space check present before any transcode.

## Notes for reviewers

<!-- Anything reviewers should know: intentional tradeoffs, follow-ups, screenshots for UI changes. -->
