# ADR-004: StrmNamingVersion - Force Re-Sync After Naming Bug Fixes

**Date**: 2026-03-07
**Status**: Accepted
**Affects**: `PluginConfiguration`, `StrmSyncService.SyncMoviesAsync`, `StrmSyncService.SyncSeriesAsync`

---

## Context

The STRM sync pipeline uses delta sync: `LastMovieSyncTimestamp` and `LastSeriesSyncTimestamp`
store the provider-side timestamp of the most-recently-seen item. On subsequent runs,
`SmartSkipExisting` skips any series whose directory already exists and whose provider timestamp
has not advanced. This is correct behaviour for content changes, but it also means that **a fix
to the filename generation logic does not automatically propagate to existing files** - the
corrected code path is never reached for series that the smart-skip considers unchanged.

## Problem

GitHub issue #9 (reported by `ullms1`): after the v1.4.31 fix to `StripEpisodeTitleDuplicate`,
series files on existing installs still had duplicated names:

```
EN - Barbie It Takes Two - S01E01 - EN - Barbie It Takes Two - S01E01.strm
4K-NF - Arcane (2021) (4K-NF) - S01E01 - EN - Arcane - S01E01.strm
```

Root cause: the naming fix is correct, but `SmartSkipExisting` caused the episode-writing loop
to be bypassed entirely for series that had not changed on the provider side. Old files with
duplicated names were never overwritten.

## Alternatives Considered

### Option A: Clear timestamps on plugin update (rejected)

Detecting "plugin updated" reliably at runtime is not straightforward in Emby's plugin model.
The plugin version is available, but storing `LastKnownPluginVersion` and comparing on startup
adds coupling between the release process and the sync state. Any version bump - even one
unrelated to naming - would trigger a full re-sync.

### Option B: User-initiated "Force Full Re-Sync" button (rejected)

Requires user action. Users who upgrade without reading release notes will not press the button
and will continue seeing duplicated filenames. The problem requires no user involvement to solve.

### Option C: Versioned naming constant (chosen)

Add `StrmNamingVersion` to `PluginConfiguration` (persisted as XML, default 0). Add a compile-
time constant `CurrentStrmNamingVersion` in `StrmSyncService`. At the start of each sync run,
compare the two. If the stored value is behind the current constant, reset both sync timestamps
to 0 and save the config. The next run then treats every item as new, regenerates all STRM files
with the corrected names, and updates the stored version so the re-sync happens only once.

## Decision

Implement Option C: `StrmNamingVersion`.

- `PluginConfiguration.StrmNamingVersion` - persisted integer, default 0
- `StrmSyncService.CurrentStrmNamingVersion = 1` - bumped for the v1.4.31 naming fix
- `CheckAndUpgradeNamingVersion(config)` - called at the top of both `SyncMoviesAsync` and
  `SyncSeriesAsync`; idempotent (whichever sync runs first applies the upgrade, subsequent
  calls are no-ops within the same session)

When to bump `CurrentStrmNamingVersion` in future:
- Any change to `StripEpisodeTitleDuplicate` that alters output for previously-synced files
- Any change to the STRM filename template (series directory name, episode filename structure)
- Do NOT bump for changes that only affect new content (new categories, new providers)

## Consequences

**Positive:**
- Users upgrading from a version with the naming bug automatically get a full re-sync on first
  run after update, with no manual intervention required.
- The mechanism is self-contained and requires no changes to the plugin update or release process.
- Adding future naming fixes only requires incrementing one integer constant.

**Negative / caveats:**
- A full re-sync rewrites all episode files, which is slower than a delta sync. For large
  libraries this may take several minutes. This is a one-time cost per naming version bump.
- Old files with incorrect names are only removed if `CleanupOrphans` is enabled (disabled by
  default). Users with cleanup disabled will have both old and new files until they enable
  cleanup or delete old files manually. This should be called out in release notes.
- Both `SyncMoviesAsync` and `SyncSeriesAsync` receive the call even though the v1.4.31 fix
  only affected series. Resetting the movie timestamp is harmless (movie naming was unaffected),
  and it keeps the implementation simple - one upgrade path covers both sync types.
