# Test Expansion Design - Integration + Harness

**Date**: 2026-03-07
**Status**: Approved
**Scope**: `Emby.Xtream.Plugin.Tests`

---

## Goal

Expand the test suite from pure-logic unit tests to full integration coverage of:
- `StrmSyncService.SyncMoviesAsync` / `SyncSeriesAsync` (file I/O + delta logic)
- `XtreamTunerHost` (stream URL selection, media source construction)
- `NfoWriter`, `ComputeChannelListHash`, `ParseTvdbOverrides`, folder name builders

Target: ~55 tests across 5 files, zero external test dependencies (xUnit only).

---

## Approach

**Shared base-class harness.** A `FakeHttpHandler` intercepts all HTTP calls; a `TempDirectory`
fixture owns the filesystem root. `SyncTestBase` and `TunerTestBase` wire both together and
expose factory helpers. All new integration test classes inherit from the appropriate base.

---

## Section 1 - Shared Harness

### `Emby.Xtream.Plugin.Tests/Fakes/FakeHttpHandler.cs`

`HttpMessageHandler` subclass. Tests register responses before the sync runs:

```csharp
handler.RespondWith("/get_vod_streams", jsonPayload);           // match by URL substring
handler.RespondWith("/get_series_info", jsonPayload, 500);      // with custom status code
handler.RespondWithSequence("/get_series", payloads);           // ordered responses
```

- First registered match wins on each request
- Unmatched requests throw `InvalidOperationException` so missing stubs are caught immediately
- Exposes `List<string> ReceivedUrls` for assertions

### `Emby.Xtream.Plugin.Tests/Fakes/TempDirectory.cs`

`IDisposable` wrapper around a unique subdirectory under `Path.GetTempPath()`. Deleted
recursively in `Dispose()`. Exposes `string Path` for use as `StrmLibraryPath`.

### `Emby.Xtream.Plugin.Tests/SyncTestBase.cs`

Base class for sync integration tests. Provides:

- `FakeHttpHandler Handler` + `HttpClient HttpClient` wired to it
- `TempDirectory TempDir` - disposed after each test
- `int SaveConfigCallCount` - incremented by the `saveConfig` delegate passed to sync methods
- `PluginConfiguration DefaultConfig` - pre-built with:
  - `StrmLibraryPath` = `TempDir.Path`
  - `SmartSkipExisting = false`
  - `CleanupOrphans = false`
  - `OrphanSafetyThreshold = 0.0` (disabled - avoids threshold interfering with cleanup tests)
  - `StrmNamingVersion = CurrentStrmNamingVersion` (current - avoids auto-upgrade noise)
  - `EnableNfoFiles = true`
- `StrmSyncService MakeService()` - returns service with fake `HttpClient` injected
- `Action SaveConfig` - delegate that increments `SaveConfigCallCount`; passed to sync calls
- `VodStream(...)` / `SeriesInfo(...)` / `EpisodeInfo(...)` factory methods for test data JSON

### `Emby.Xtream.Plugin.Tests/TunerTestBase.cs`

Same pattern for `XtreamTunerHost` tests. Pre-built config includes both Xtream and Dispatcharr
settings. Exposes a `BuildStats(streamId, audioCodec, ...)` factory for `StreamStatsInfo`.

### Sentinel content pattern

To assert "file was NOT re-written" without relying on `LastWriteTime` (fragile on fast
filesystems), tests write a known sentinel string directly to a `.strm` file before the second
sync, then assert the file content is still `"SENTINEL"` afterward.

---

## Section 2 - Production Code Changes

### `StrmSyncService` - extract all `Plugin.Instance.SaveConfiguration()` calls

Three call sites currently exist inside `StrmSyncService`:

| Location | Current code |
|----------|-------------|
| `CheckAndUpgradeNamingVersion` (line ~199) | `Plugin.Instance.SaveConfiguration()` |
| After `LastMovieSyncTimestamp` update (line ~486) | `Plugin.Instance.SaveConfiguration()` |
| After `LastSeriesSyncTimestamp` update (line ~832) | `Plugin.Instance.SaveConfiguration()` |

**Fix**: replace all three with `saveConfig?.Invoke()`. Add `Action saveConfig = null` parameter
to both sync methods and thread it through to `CheckAndUpgradeNamingVersion`.

New signatures:

```csharp
public async Task SyncMoviesAsync(
    PluginConfiguration config,
    CancellationToken cancellationToken,
    Action saveConfig = null)

public async Task SyncSeriesAsync(
    PluginConfiguration config,
    CancellationToken cancellationToken,
    Action saveConfig = null)

private bool CheckAndUpgradeNamingVersion(PluginConfiguration config, Action saveConfig)
```

### `SyncMoviesTask` / `SyncSeriesTask` - update call sites

```csharp
await svc.SyncMoviesAsync(
    Plugin.Instance.Configuration,
    cancellationToken,
    () => Plugin.Instance.SaveConfiguration());
```

Same for `SyncSeriesAsync`. Two files, one line change each.

### `StrmSyncService` - `HttpClient` injection

```csharp
private readonly HttpClient _httpClient;

public StrmSyncService(ILogger logger, HttpClient httpClient = null)
{
    _logger = logger;
    _tmdbLookupService = new TmdbLookupService(logger);
    _httpClient = httpClient ?? SharedHttpClient;
}
```

All internal HTTP calls that currently use `SharedHttpClient` switch to `_httpClient`.

### What is explicitly NOT changed

- No interface extraction, no DI container, no mocking framework
- `writtenPaths` comparer is already `StringComparer.OrdinalIgnoreCase` - correct, no change
- `NfoWriter` - writes to a real path; tested via `TempDirectory`
- No changes to `XtreamTunerHost` constructor or public API beyond `HttpClient` injection

---

## Section 3 - Test Scenarios

### `StrmSyncServiceTests` - additions to existing file (~20 new cases)

**Folder naming**
- Movie + valid TMDB ID → folder name contains `[tmdbid=12345]`
- Movie + zero/invalid TMDB ID → bare sanitized title
- Series + TVDb manual override → override wins over TMDB and auto TVDb
- Series + auto TVDb ID, no override → `[tvdbid=N]`
- Series + TMDB only → `[tmdbid=N]`
- Series + no IDs → bare name

**`ParseTvdbOverrides`**
- Basic `SeriesName=12345` → parsed
- Comment lines (`# ...`) → ignored
- Malformed line (no `=`) → skipped
- Duplicate keys → last wins
- Non-numeric ID → skipped

**`ComputeChannelListHash`**
- Same channels, different order → same hash
- One channel added → different hash
- Channel name changed (same stream ID) → different hash

**Naming version upgrade**
- Version 0, current 1 → timestamps reset to 0, returns `true`, `saveConfig` called once
- Version already current → no change, returns `false`, `saveConfig` not called
- Called twice → second call is no-op

**`NfoWriter`**
- Movie with TMDB → file created, `<uniqueid type="tmdb" default="true">` present
- Movie without TMDB → file not created
- Movie file already exists → sentinel content preserved (not overwritten)
- Show with TVDb only → `default="true"` on tvdb node
- Show with TVDb + TMDB → tvdb default, tmdb without default
- Show with TMDB only → tmdb has `default="true"`
- Show with no IDs → file not created
- Title containing `&`, `<`, `>` → XML-escaped in output file

---

### `SyncMoviesIntegrationTests` - new file (~10 tests)

- **Happy path** - provider returns one movie → `.strm` + `.nfo` written at correct relative
  path, `saveConfig` called to persist timestamp
- **Smart-skip** - sentinel written, `SmartSkipExisting = true`, same timestamp →
  sentinel content unchanged after sync
- **Naming version upgrade bypasses smart-skip** - sentinel written, `StrmNamingVersion = 0` →
  version upgrade fires, timestamp reset → sentinel overwritten; `saveConfig` called twice
  (once for upgrade, once for timestamp)
- **`Added = 0` provider** - all streams have `Added = 0`, `LastMovieSyncTimestamp = 100` →
  movies treated as unchanged (not new); `SmartSkipExisting = false` → files still written;
  `saveConfig` NOT called for timestamp update (0 not > 100)
- **Orphan flat** - `OldMovie.strm` pre-written, not in new response, `CleanupOrphans = true`
  → file deleted
- **Orphan threshold aborted** - 12 pre-written files, 1 in new response (91% ratio),
  `OrphanSafetyThreshold = 0.5` → cleanup skipped, all 12 preserved; requires >10 files
  because threshold check only fires when `existingStrms.Length > 10`
- **Orphan threshold allows** - 12 pre-written files, 10 in new response (17% ratio),
  threshold 0.5 → 2 deleted
- **HTTP 500** - fake returns 500 on stream fetch → sync throws, progress phase contains "Failed"
- **Empty response** - provider returns `[]` → no files written, no crash, `saveConfig` not
  called for timestamp

---

### `SyncSeriesIntegrationTests` - new file (~10 tests)

- **Happy path** - series + 2 episodes → `Shows/Series [tvdbid=N]/Season 01/S01E01.strm` and
  `S01E02.strm` written at correct paths
- **Episode title deduplication on disk** - provider embeds series name in episode title →
  actual filename on disk does not contain the duplicated series name segment
- **Smart-skip sentinel** - sentinel episode file, series timestamp unchanged,
  `SmartSkipExisting = true` → sentinel content preserved
- **Naming version forces re-sync** - sentinel written, version 0 < 1 → timestamp reset to 0 →
  `isChangedSeries` becomes true (because `lastSeriesTs == 0`) → episode loop runs → sentinel
  overwritten; note: mechanism is timestamp-based, not a skip override
- **Orphan in season subdir** - `Shows/OldShow/Season 01/S01E01.strm` pre-written, series not
  in new response, `CleanupOrphans = true` → strm file deleted, empty `Season 01/` dir deleted,
  empty `OldShow/` dir deleted
- **`Added = 0` provider** - same semantics as movie equivalent; series treated as unchanged
  when `LastSeriesSyncTimestamp` was previously set
- **Series with no episodes** - series in response, episode list returns `[]` → series dir
  created (NFO written), no crash, no `.strm` files
- **Multi-season** - episodes with `season_num = 1` and `season_num = 2` → written to
  `Season 01/` and `Season 02/` respectively

---

### `XtreamTunerHostTests` - new file (~10 tests)

- **Dispatcharr path** - Dispatcharr enabled → `MediaSourceInfo.Path` is
  `/proxy/ts/stream/{uuid}`, `SupportsProbing = false`, `AnalyzeDurationMs = 0`
- **Direct Xtream path** - Dispatcharr disabled → path is raw Xtream stream URL
- **AC3 → 6 channels** - `stream_stats.audio_codec = "ac3"` → `AudioChannels = 6`
- **EAC3 → 6 channels** - same
- **MP2 → 2 channels** - `audio_codec = "mp2"` → `AudioChannels = 2`
- **Unknown codec → no channel count forced**
- **Stats for wrong stream ID** - stats cache populated for ID 999, request for ID 42 → ID 42
  gets no-stats defaults, not ID 999's values
- **`SupportsDirectStream`** - stats present → `true`; no stats → `false`
- **`ForceAudioTranscode`** - config flag set → reflected on media source
- **UserAgent propagation** - `config.HttpUserAgent` set → present in `HttpUserAgent` field
  on the media source

---

## Test Count Summary

| File | New tests |
|------|-----------|
| `StrmSyncServiceTests` (additions) | ~20 |
| `SyncMoviesIntegrationTests` | ~10 |
| `SyncSeriesIntegrationTests` | ~10 |
| `XtreamTunerHostTests` | ~10 |
| **Total new** | **~50** |
| Existing | 192 |
| **Grand total** | **~242** |

---

## Out of Scope

- Concurrent sync runs (requires threading harness)
- `TmdbLookupService` HTTP tests (covered by `DispatcharrClientTests` pattern; add separately)
- `SyncHistoryJson` corruption recovery (low risk; serialisation is standard STJ)
- E2E browser tests (`tests/e2e/`) - separate Playwright suite
