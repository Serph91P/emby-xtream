# Test Expansion - Integration Harness Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add ~50 integration tests covering `SyncMoviesAsync`, `SyncSeriesAsync`, `XtreamTunerHost`, `NfoWriter`, folder naming, and pure-logic gaps via a shared `FakeHttpHandler` + `TempDirectory` harness.

**Architecture:** A `FakeHttpHandler` intercepts all `HttpClient` calls with canned JSON responses. A `TempDirectory` fixture owns the filesystem root. `SyncTestBase` and `TunerTestBase` wire both together. Production code is refactored to accept `PluginConfiguration` and `Action saveConfig` as parameters instead of reading from `Plugin.Instance`.

**Tech Stack:** xUnit, C# 7.3 (netstandard2.0 plugin, net10.0 tests), no new NuGet dependencies.

**Run tests with:** `dotnet test Emby.Xtream.Plugin.Tests/`

---

## Task 1: Create `TempDirectory` fixture

**Files:**
- Create: `Emby.Xtream.Plugin.Tests/Fakes/TempDirectory.cs`

**Step 1: Create the file**

```csharp
using System;
using System.IO;

namespace Emby.Xtream.Plugin.Tests.Fakes
{
    /// <summary>
    /// Creates a unique temp directory for a test and deletes it on Dispose.
    /// Use as a field in test classes - dispose in constructor via IDisposable or in each test.
    /// </summary>
    public sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
```

**Step 2: Build**

```bash
dotnet build Emby.Xtream.Plugin.Tests/
```
Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add Emby.Xtream.Plugin.Tests/Fakes/TempDirectory.cs
git commit -m "test: add TempDirectory fixture"
```

---

## Task 2: Create `FakeHttpHandler`

**Files:**
- Create: `Emby.Xtream.Plugin.Tests/Fakes/FakeHttpHandler.cs`

**Step 1: Create the file**

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Plugin.Tests.Fakes
{
    /// <summary>
    /// Intercepts HttpClient calls and returns pre-registered responses.
    /// Register responses with RespondWith() before the code under test runs.
    /// Requests with no matching registration throw InvalidOperationException.
    /// </summary>
    public sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly List<(string UrlSubstring, Queue<(string Body, HttpStatusCode Status)> Responses)> _rules
            = new List<(string, Queue<(string, HttpStatusCode)>)>();

        public List<string> ReceivedUrls { get; } = new List<string>();

        /// <summary>Register a single response for URLs containing <paramref name="urlSubstring"/>.</summary>
        public void RespondWith(string urlSubstring, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            var q = new Queue<(string, HttpStatusCode)>();
            q.Enqueue((body, status));
            _rules.Add((urlSubstring, q));
        }

        /// <summary>Register multiple ordered responses for the same URL pattern.</summary>
        public void RespondWithSequence(string urlSubstring, IEnumerable<string> bodies, HttpStatusCode status = HttpStatusCode.OK)
        {
            var q = new Queue<(string, HttpStatusCode)>();
            foreach (var b in bodies)
                q.Enqueue((b, status));
            _rules.Add((urlSubstring, q));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            ReceivedUrls.Add(url);

            foreach (var (urlSubstring, queue) in _rules)
            {
                if (url.Contains(urlSubstring) && queue.Count > 0)
                {
                    var (body, status) = queue.Dequeue();
                    var response = new HttpResponseMessage(status)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    };
                    return Task.FromResult(response);
                }
            }

            throw new InvalidOperationException(
                $"FakeHttpHandler: no registered response for URL: {url}\n" +
                $"Register one with handler.RespondWith(\"{url}\", json)");
        }

        protected override void Dispose(bool disposing) { }
    }
}
```

**Step 2: Build**

```bash
dotnet build Emby.Xtream.Plugin.Tests/
```
Expected: 0 errors.

**Step 3: Commit**

```bash
git add Emby.Xtream.Plugin.Tests/Fakes/FakeHttpHandler.cs
git commit -m "test: add FakeHttpHandler for intercepting HTTP in tests"
```

---

## Task 3: Refactor `StrmSyncService` - `HttpClient` injection + `saveConfig` parameter

This is the only substantial production code change. All `Plugin.Instance.*` calls are extracted from `StrmSyncService`. No behaviour changes.

**Files:**
- Modify: `Emby.Xtream.Plugin/Service/StrmSyncService.cs`

**Step 1: Add `_httpClient` instance field + constructor overload**

Find the constructor at line ~111:
```csharp
public StrmSyncService(ILogger logger)
{
    _logger = logger;
    _tmdbLookupService = new TmdbLookupService(logger);
```

Add `_httpClient` field after line 100 (`private readonly ILogger _logger;`):
```csharp
private readonly HttpClient _httpClient;
```

Change the constructor:
```csharp
public StrmSyncService(ILogger logger, HttpClient httpClient = null)
{
    _logger = logger;
    _tmdbLookupService = new TmdbLookupService(logger);
    _httpClient = httpClient ?? SharedHttpClient;
}
```

**Step 2: Replace all `SharedHttpClient.GetStringAsync` with `_httpClient.GetStringAsync`**

There are 6 call sites at lines ~1257, ~1300, ~1317, ~1368, ~1385, ~1429. Use a global replace - search for `SharedHttpClient.GetStringAsync` and replace with `_httpClient.GetStringAsync` in the file. Do NOT replace the field declaration at line 87.

Also update `ApplyUserAgentToSharedClient` (line ~92) - keep it pointing at `SharedHttpClient` (the shared static), since production always uses that. The injected `_httpClient` in tests has no user-agent header requirement.

**Step 3: Add `saveConfig` parameter to `CheckAndUpgradeNamingVersion`**

Current signature (line ~188):
```csharp
private bool CheckAndUpgradeNamingVersion(PluginConfiguration config)
```

New signature:
```csharp
private bool CheckAndUpgradeNamingVersion(PluginConfiguration config, Action saveConfig)
```

Replace `Plugin.Instance.SaveConfiguration();` inside this method with `saveConfig?.Invoke();`.

**Step 4: Add `saveConfig` parameter to `SyncMoviesAsync`**

Current signature (line ~203):
```csharp
public async Task SyncMoviesAsync(CancellationToken cancellationToken)
```

New signature:
```csharp
public async Task SyncMoviesAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig = null)
```

Remove the line `var config = Plugin.Instance.Configuration;` (it's the first line of the method body - now received as a parameter).

Update the call to `CheckAndUpgradeNamingVersion` to pass `saveConfig`:
```csharp
CheckAndUpgradeNamingVersion(config, saveConfig);
```

Replace the two remaining `Plugin.Instance.SaveConfiguration();` calls in this method body (at line ~486) with `saveConfig?.Invoke();`.

**Step 5: Add `saveConfig` parameter to `SyncSeriesAsync`**

Same pattern as Step 4. Current signature (line ~522):
```csharp
public async Task SyncSeriesAsync(CancellationToken cancellationToken)
```

New signature:
```csharp
public async Task SyncSeriesAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig = null)
```

Remove `var config = Plugin.Instance.Configuration;` from method body.

Update `CheckAndUpgradeNamingVersion(config, saveConfig)` call.

Replace `Plugin.Instance.SaveConfiguration();` at line ~832 with `saveConfig?.Invoke();`.

**Note:** `FetchCategoriesAsync` at line ~1249 also reads `Plugin.Instance.Configuration` directly to build its URL. Change it to accept config as a parameter:
```csharp
private async Task<List<Category>> FetchCategoriesAsync(string action, PluginConfiguration config, CancellationToken cancellationToken)
```
Update the two call sites inside `SyncMoviesAsync` to pass `config`.

**Step 6: Build**

```bash
dotnet build Emby.Xtream.Plugin/
```
Expected: 0 errors.

**Step 7: Update callers in `SyncMoviesTask` and `SyncSeriesTask`**

`SyncMoviesTask.cs` line 47:
```csharp
// Before:
await svc.SyncMoviesAsync(cancellationToken).ConfigureAwait(false);

// After:
await svc.SyncMoviesAsync(
    config,
    cancellationToken,
    () => Plugin.Instance.SaveConfiguration()).ConfigureAwait(false);
```

`SyncSeriesTask.cs` line 47:
```csharp
// Before:
await svc.SyncSeriesAsync(cancellationToken).ConfigureAwait(false);

// After:
await svc.SyncSeriesAsync(
    config,
    cancellationToken,
    () => Plugin.Instance.SaveConfiguration()).ConfigureAwait(false);
```

**Step 8: Build and test**

```bash
dotnet test Emby.Xtream.Plugin.Tests/
```
Expected: all 192 existing tests still pass, 0 failures.

**Step 9: Commit**

```bash
git add Emby.Xtream.Plugin/Service/StrmSyncService.cs \
        Emby.Xtream.Plugin/Service/SyncMoviesTask.cs \
        Emby.Xtream.Plugin/Service/SyncSeriesTask.cs
git commit -m "refactor: inject HttpClient and saveConfig into StrmSyncService for testability"
```

---

## Task 4: Create `SyncTestBase`

**Files:**
- Create: `Emby.Xtream.Plugin.Tests/SyncTestBase.cs`

**Step 1: Create the file**

```csharp
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Tests
{
    public abstract class SyncTestBase : IDisposable
    {
        protected readonly FakeHttpHandler Handler;
        protected readonly HttpClient HttpClient;
        protected readonly TempDirectory TempDir;
        protected int SaveConfigCallCount;

        protected SyncTestBase()
        {
            Handler = new FakeHttpHandler();
            HttpClient = new HttpClient(Handler);
            TempDir = new TempDirectory();
            SaveConfigCallCount = 0;
        }

        protected Action SaveConfig => () => SaveConfigCallCount++;

        protected PluginConfiguration DefaultConfig() => new PluginConfiguration
        {
            BaseUrl      = "http://fake-xtream",
            Username     = "user",
            Password     = "pass",
            StrmLibraryPath   = TempDir.Path,
            SmartSkipExisting = false,
            CleanupOrphans    = false,
            OrphanSafetyThreshold = 0.0,
            StrmNamingVersion     = StrmSyncService.CurrentStrmNamingVersion,
            SyncParallelism       = 1,
            MovieFolderMode  = "single",
            SeriesFolderMode = "single",
            EnableNfoFiles   = false,
            EnableTmdbFolderNaming  = false,
            EnableContentNameCleaning = false,
        };

        protected StrmSyncService MakeService() =>
            new StrmSyncService(new NullLogger(), HttpClient);

        // ----- JSON factory helpers -----

        protected static string VodStreamsJson(params object[] streams) =>
            JsonSerializer.Serialize(streams);

        protected static object VodStream(int streamId = 1, string name = "Test Movie",
            long added = 1000, string tmdbId = "", string ext = "mkv") =>
            new
            {
                stream_id = streamId,
                name,
                added,
                tmdb_id = tmdbId,
                container_extension = ext,
                category_id = (int?)null
            };

        protected static string SeriesListJson(params object[] series) =>
            JsonSerializer.Serialize(series);

        protected static object Series(int seriesId = 1, string name = "Test Show",
            string lastModified = "2000", string tmdbId = "") =>
            new
            {
                series_id = seriesId,
                name,
                last_modified = lastModified,
                tmdb = tmdbId,
                category_id = (int?)null
            };

        protected static string SeriesDetailJson(int seriesId = 1, int seasonNum = 1,
            int episodeNum = 1, string title = "Episode Title", string ext = "mp4") =>
            JsonSerializer.Serialize(new
            {
                info = new { series_id = seriesId, name = "Test Show", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>
                {
                    [seasonNum.ToString()] = new object[]
                    {
                        new { id = 101, episode_num = episodeNum, title, container_extension = ext, season = seasonNum }
                    }
                }
            });

        protected static readonly CancellationToken None = CancellationToken.None;

        public void Dispose()
        {
            HttpClient.Dispose();
            TempDir.Dispose();
        }
    }
}
```

**Step 2: Build**

```bash
dotnet build Emby.Xtream.Plugin.Tests/
```
Expected: 0 errors. Note: `NullLogger` is already used in `DispatcharrClientTests` - it lives in the test project.

**Step 3: Commit**

```bash
git add Emby.Xtream.Plugin.Tests/SyncTestBase.cs
git commit -m "test: add SyncTestBase with FakeHttpHandler + TempDirectory wiring"
```

---

## Task 5: Pure-logic tests - folder naming, `ParseTvdbOverrides`, `ComputeChannelListHash`, naming version, `NfoWriter`

**Files:**
- Modify: `Emby.Xtream.Plugin.Tests/StrmSyncServiceTests.cs`

Add the following test methods after the existing `StripEpisodeTitleDuplicate_ReturnsExpected` theory. Each group is a separate `[Theory]` or `[Fact]`.

**Step 1: Add folder naming tests**

```csharp
// ----- BuildMovieFolderName -----

[Theory]
[InlineData("The Matrix", "603", "The Matrix [tmdbid=603]")]
[InlineData("The Matrix", "0",   "The Matrix")]
[InlineData("The Matrix", "",    "The Matrix")]
[InlineData("The Matrix", null,  "The Matrix")]
[InlineData("The Matrix", "abc", "The Matrix")]
public void BuildMovieFolderName_TmdbHandling(string name, string tmdbId, string expected)
{
    var result = StrmSyncService.BuildMovieFolderName(name, tmdbId);
    Assert.Equal(expected, result);
}

// ----- BuildSeriesFolderName -----

[Fact]
public void BuildSeriesFolderName_TvdbOverride_WinsOverTmdb()
{
    var overrides = new System.Collections.Generic.Dictionary<string, int>(
        StringComparer.OrdinalIgnoreCase) { ["Breaking Bad"] = 81189 };
    var result = StrmSyncService.BuildSeriesFolderName("Breaking Bad", "1396", null, overrides);
    Assert.Equal("Breaking Bad [tvdbid=81189]", result);
}

[Fact]
public void BuildSeriesFolderName_AutoTvdb_UsedWhenNoOverrideAndNoTmdb()
{
    var result = StrmSyncService.BuildSeriesFolderName("Breaking Bad", null, 81189, null);
    Assert.Equal("Breaking Bad [tvdbid=81189]", result);
}

[Fact]
public void BuildSeriesFolderName_Tmdb_UsedWhenNoTvdb()
{
    var result = StrmSyncService.BuildSeriesFolderName("Breaking Bad", "1396", null, null);
    Assert.Equal("Breaking Bad [tmdbid=1396]", result);
}

[Fact]
public void BuildSeriesFolderName_BareNameWhenNoIds()
{
    var result = StrmSyncService.BuildSeriesFolderName("Breaking Bad", null, null, null);
    Assert.Equal("Breaking Bad", result);
}
```

**Step 2: Add `ParseTvdbOverrides` tests**

```csharp
// ----- ParseTvdbOverrides -----

[Fact]
public void ParseTvdbOverrides_ParsesBasicMapping()
{
    var result = StrmSyncService.ParseTvdbOverrides("Breaking Bad=81189");
    Assert.Equal(81189, result["Breaking Bad"]);
}

[Fact]
public void ParseTvdbOverrides_IgnoresCommentLines()
{
    var result = StrmSyncService.ParseTvdbOverrides("# comment\nBreaking Bad=81189");
    Assert.False(result.ContainsKey("# comment"));
    Assert.Equal(81189, result["Breaking Bad"]);
}

[Fact]
public void ParseTvdbOverrides_IgnoresMalformedLines()
{
    var result = StrmSyncService.ParseTvdbOverrides("NoEqualsSign\nBreaking Bad=81189");
    Assert.Single(result);
}

[Fact]
public void ParseTvdbOverrides_DuplicateKey_LastWins()
{
    var result = StrmSyncService.ParseTvdbOverrides("Breaking Bad=111\nBreaking Bad=81189");
    Assert.Equal(81189, result["Breaking Bad"]);
}

[Fact]
public void ParseTvdbOverrides_NonNumericId_Skipped()
{
    var result = StrmSyncService.ParseTvdbOverrides("Breaking Bad=abc");
    Assert.Empty(result);
}
```

**Step 3: Add `ComputeChannelListHash` tests**

```csharp
// ----- ComputeChannelListHash -----

[Fact]
public void ComputeChannelListHash_SameChannelsDifferentOrder_SameHash()
{
    var a = new System.Collections.Generic.List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>
    {
        new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 1, Name = "A" },
        new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 2, Name = "B" },
    };
    var b = new System.Collections.Generic.List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>
    {
        new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 2, Name = "B" },
        new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 1, Name = "A" },
    };
    Assert.Equal(StrmSyncService.ComputeChannelListHash(a), StrmSyncService.ComputeChannelListHash(b));
}

[Fact]
public void ComputeChannelListHash_AddingChannel_ChangeHash()
{
    var a = new System.Collections.Generic.List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>
    {
        new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 1, Name = "A" },
    };
    var b = new System.Collections.Generic.List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>
    {
        new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 1, Name = "A" },
        new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 2, Name = "B" },
    };
    Assert.NotEqual(StrmSyncService.ComputeChannelListHash(a), StrmSyncService.ComputeChannelListHash(b));
}

[Fact]
public void ComputeChannelListHash_NameChanged_ChangeHash()
{
    var a = new System.Collections.Generic.List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>
    {
        new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 1, Name = "BBC One" },
    };
    var b = new System.Collections.Generic.List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>
    {
        new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 1, Name = "BBC Two" },
    };
    Assert.NotEqual(StrmSyncService.ComputeChannelListHash(a), StrmSyncService.ComputeChannelListHash(b));
}
```

**Step 4: Add naming version upgrade tests**

These require the refactored `CheckAndUpgradeNamingVersion(config, saveConfig)` - make it `internal` (it already is `private`; change to `internal` so tests can call it directly). Add `[assembly: InternalsVisibleTo("Emby.Xtream.Plugin.Tests")]` to `StrmSyncService.cs` if not already present - check first.

Actually, simpler: test `CheckAndUpgradeNamingVersion` indirectly through the integration tests in Task 6/7 (the "naming version upgrade bypasses smart-skip" tests). The unit-level assertions (saveConfig called once, returns true) can be tested by making it `internal` and using `InternalsVisibleTo`.

Check whether `InternalsVisibleTo` is already configured:

```bash
grep -r "InternalsVisibleTo" "/Users/rolandbo@backbase.com/Documents/Coding Projects/Emby/Emby.Xtream.Plugin/"
```

If not present, add to the plugin's `.csproj`:
```xml
<ItemGroup>
  <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
    <_Parameter1>Emby.Xtream.Plugin.Tests</_Parameter1>
  </AssemblyAttribute>
</ItemGroup>
```

Then change `CheckAndUpgradeNamingVersion` from `private` to `internal`.

Add these tests:

```csharp
// ----- CheckAndUpgradeNamingVersion -----

[Fact]
public void CheckAndUpgradeNamingVersion_OldVersion_ResetsTimestamps()
{
    var config = new PluginConfiguration
    {
        StrmNamingVersion     = 0,
        LastMovieSyncTimestamp  = 999,
        LastSeriesSyncTimestamp = 888,
    };
    var saveCount = 0;
    var svc = new StrmSyncService(new NullLogger());

    var upgraded = svc.CheckAndUpgradeNamingVersion(config, () => saveCount++);

    Assert.True(upgraded);
    Assert.Equal(0, config.LastMovieSyncTimestamp);
    Assert.Equal(0, config.LastSeriesSyncTimestamp);
    Assert.Equal(StrmSyncService.CurrentStrmNamingVersion, config.StrmNamingVersion);
    Assert.Equal(1, saveCount);
}

[Fact]
public void CheckAndUpgradeNamingVersion_CurrentVersion_NoChange()
{
    var config = new PluginConfiguration
    {
        StrmNamingVersion     = StrmSyncService.CurrentStrmNamingVersion,
        LastMovieSyncTimestamp  = 999,
        LastSeriesSyncTimestamp = 888,
    };
    var saveCount = 0;
    var svc = new StrmSyncService(new NullLogger());

    var upgraded = svc.CheckAndUpgradeNamingVersion(config, () => saveCount++);

    Assert.False(upgraded);
    Assert.Equal(999, config.LastMovieSyncTimestamp);
    Assert.Equal(0, saveCount);
}

[Fact]
public void CheckAndUpgradeNamingVersion_CalledTwice_SecondIsNoOp()
{
    var config = new PluginConfiguration { StrmNamingVersion = 0 };
    var saveCount = 0;
    var svc = new StrmSyncService(new NullLogger());

    svc.CheckAndUpgradeNamingVersion(config, () => saveCount++);
    svc.CheckAndUpgradeNamingVersion(config, () => saveCount++);

    Assert.Equal(1, saveCount);
}
```

**Step 5: Add `NfoWriter` tests**

```csharp
// ----- NfoWriter -----

[Fact]
public void NfoWriter_Movie_WritesFileWithTmdbId()
{
    using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
    {
        var path = System.IO.Path.Combine(tmp.Path, "movie.nfo");
        Emby.Xtream.Plugin.Service.NfoWriter.WriteMovieNfo(path, "The Matrix", "603", 1999);
        Assert.True(System.IO.File.Exists(path));
        var content = System.IO.File.ReadAllText(path);
        Assert.Contains("<uniqueid type=\"tmdb\" default=\"true\">603</uniqueid>", content);
    }
}

[Fact]
public void NfoWriter_Movie_NoTmdbId_FileNotCreated()
{
    using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
    {
        var path = System.IO.Path.Combine(tmp.Path, "movie.nfo");
        Emby.Xtream.Plugin.Service.NfoWriter.WriteMovieNfo(path, "The Matrix", null, 1999);
        Assert.False(System.IO.File.Exists(path));
    }
}

[Fact]
public void NfoWriter_Movie_FileAlreadyExists_NotOverwritten()
{
    using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
    {
        var path = System.IO.Path.Combine(tmp.Path, "movie.nfo");
        System.IO.File.WriteAllText(path, "SENTINEL");
        Emby.Xtream.Plugin.Service.NfoWriter.WriteMovieNfo(path, "The Matrix", "603", 1999);
        Assert.Equal("SENTINEL", System.IO.File.ReadAllText(path));
    }
}

[Fact]
public void NfoWriter_Movie_EscapesXmlSpecialChars()
{
    using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
    {
        var path = System.IO.Path.Combine(tmp.Path, "movie.nfo");
        Emby.Xtream.Plugin.Service.NfoWriter.WriteMovieNfo(path, "Tom & Jerry <1940>", "12345", null);
        var content = System.IO.File.ReadAllText(path);
        Assert.Contains("Tom &amp; Jerry &lt;1940&gt;", content);
    }
}

[Fact]
public void NfoWriter_Show_TvdbOnly_IsDefault()
{
    using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
    {
        var path = System.IO.Path.Combine(tmp.Path, "tvshow.nfo");
        Emby.Xtream.Plugin.Service.NfoWriter.WriteShowNfo(path, "Breaking Bad", "81189", null);
        var content = System.IO.File.ReadAllText(path);
        Assert.Contains("<uniqueid type=\"tvdb\" default=\"true\">81189</uniqueid>", content);
    }
}

[Fact]
public void NfoWriter_Show_TvdbAndTmdb_TvdbIsDefault()
{
    using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
    {
        var path = System.IO.Path.Combine(tmp.Path, "tvshow.nfo");
        Emby.Xtream.Plugin.Service.NfoWriter.WriteShowNfo(path, "Breaking Bad", "81189", "1396");
        var content = System.IO.File.ReadAllText(path);
        Assert.Contains("type=\"tvdb\" default=\"true\"", content);
        Assert.Contains("type=\"tmdb\">1396", content);
        Assert.DoesNotContain("type=\"tmdb\" default=\"true\"", content);
    }
}

[Fact]
public void NfoWriter_Show_TmdbOnly_IsDefault()
{
    using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
    {
        var path = System.IO.Path.Combine(tmp.Path, "tvshow.nfo");
        Emby.Xtream.Plugin.Service.NfoWriter.WriteShowNfo(path, "Breaking Bad", null, "1396");
        var content = System.IO.File.ReadAllText(path);
        Assert.Contains("<uniqueid type=\"tmdb\" default=\"true\">1396</uniqueid>", content);
    }
}

[Fact]
public void NfoWriter_Show_NoIds_FileNotCreated()
{
    using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
    {
        var path = System.IO.Path.Combine(tmp.Path, "tvshow.nfo");
        Emby.Xtream.Plugin.Service.NfoWriter.WriteShowNfo(path, "Breaking Bad", null, null);
        Assert.False(System.IO.File.Exists(path));
    }
}
```

**Step 6: Run tests**

```bash
dotnet test Emby.Xtream.Plugin.Tests/
```
Expected: all previous tests pass + all new tests pass. Zero failures.

**Step 7: Commit**

```bash
git add Emby.Xtream.Plugin.Tests/StrmSyncServiceTests.cs \
        Emby.Xtream.Plugin/Emby.Xtream.Plugin.csproj \
        Emby.Xtream.Plugin/Service/StrmSyncService.cs
git commit -m "test: add folder naming, NfoWriter, hash, and naming-version unit tests"
```

---

## Task 6: `SyncMoviesIntegrationTests`

**Files:**
- Create: `Emby.Xtream.Plugin.Tests/SyncMoviesIntegrationTests.cs`

**Step 1: Create the file with all movie sync tests**

```csharp
using System.IO;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class SyncMoviesIntegrationTests : SyncTestBase
    {
        [Fact]
        public async Task HappyPath_WritesStrmFile()
        {
            var config = DefaultConfig();
            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 1, name: "Inception", added: 5000, ext: "mkv")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var strmPath = Path.Combine(TempDir.Path, "Movies", "Inception.strm");
            Assert.True(File.Exists(strmPath), "STRM file should be written");
            Assert.Contains("get_vod_stream", File.ReadAllText(strmPath));
            Assert.Equal(1, SaveConfigCallCount); // timestamp persisted
        }

        [Fact]
        public async Task SmartSkip_ExistingFile_NotRewritten()
        {
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.LastMovieSyncTimestamp = 9999; // movie.Added (5000) <= lastTs

            var strmPath = Path.Combine(TempDir.Path, "Movies", "Inception.strm");
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath));
            File.WriteAllText(strmPath, "SENTINEL");

            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 1, name: "Inception", added: 5000, ext: "mkv")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal("SENTINEL", File.ReadAllText(strmPath));
        }

        [Fact]
        public async Task NamingVersionUpgrade_BypassesSmartSkip_OverwritesSentinel()
        {
            var config = DefaultConfig();
            config.SmartSkipExisting    = true;
            config.LastMovieSyncTimestamp = 9999;
            config.StrmNamingVersion    = 0; // triggers upgrade

            var strmPath = Path.Combine(TempDir.Path, "Movies", "Inception.strm");
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath));
            File.WriteAllText(strmPath, "SENTINEL");

            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 1, name: "Inception", added: 5000, ext: "mkv")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.NotEqual("SENTINEL", File.ReadAllText(strmPath)); // file was rewritten
            Assert.True(SaveConfigCallCount >= 2); // once for version upgrade, once for timestamp
        }

        [Fact]
        public async Task AddedZero_AllStreams_FileStillWrittenWhenNoSmartSkip()
        {
            var config = DefaultConfig();
            config.LastMovieSyncTimestamp = 100; // all Added=0 <= 100, treated as existing

            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 1, name: "Inception", added: 0, ext: "mkv")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            // SmartSkipExisting=false so file is written even though Added=0
            var strmPath = Path.Combine(TempDir.Path, "Movies", "Inception.strm");
            Assert.True(File.Exists(strmPath));
            // Timestamp NOT updated: Max(Added)=0 is not > LastMovieSyncTimestamp=100
            Assert.Equal(0, SaveConfigCallCount);
        }

        [Fact]
        public async Task OrphanCleanup_RemovesStaleFile()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;

            // Pre-write an orphan
            var orphan = Path.Combine(TempDir.Path, "Movies", "OldMovie.strm");
            Directory.CreateDirectory(Path.GetDirectoryName(orphan));
            File.WriteAllText(orphan, "http://old");

            // Provider no longer has OldMovie
            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 1, name: "Inception", added: 5000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(orphan), "Orphan file should be deleted");
        }

        [Fact]
        public async Task OrphanThreshold_AboveThreshold_CleanupSkipped()
        {
            // Safety threshold only fires when existingStrms.Length > 10
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.OrphanSafetyThreshold = 0.5; // 50%

            // Write 12 orphan files so threshold check activates
            var moviesDir = Path.Combine(TempDir.Path, "Movies");
            Directory.CreateDirectory(moviesDir);
            for (int i = 1; i <= 12; i++)
                File.WriteAllText(Path.Combine(moviesDir, $"Movie{i}.strm"), "http://x");

            // Provider returns only 1 movie → 11/12 (91%) would be orphaned, exceeds 50%
            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 1, name: "Movie1", added: 5000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            // All 12 files should survive (cleanup aborted)
            Assert.Equal(12, Directory.GetFiles(moviesDir, "*.strm").Length);
        }

        [Fact]
        public async Task OrphanThreshold_BelowThreshold_CleanupProceeds()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.OrphanSafetyThreshold = 0.5; // 50%

            var moviesDir = Path.Combine(TempDir.Path, "Movies");
            Directory.CreateDirectory(moviesDir);
            for (int i = 1; i <= 12; i++)
                File.WriteAllText(Path.Combine(moviesDir, $"Movie{i}.strm"), "http://x");

            // Provider returns 10 movies → 2/12 (17%) orphaned, within 50%
            var streams = new object[10];
            for (int i = 0; i < 10; i++)
                streams[i] = VodStream(streamId: i + 1, name: $"Movie{i + 1}", added: 5000);
            Handler.RespondWith("get_vod_streams", VodStreamsJson(streams));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(10, Directory.GetFiles(moviesDir, "*.strm").Length);
        }

        [Fact]
        public async Task HttpError_SyncThrows()
        {
            var config = DefaultConfig();
            Handler.RespondWith("get_vod_streams", "Service Unavailable",
                System.Net.HttpStatusCode.ServiceUnavailable);

            await Assert.ThrowsAnyAsync<System.Exception>(
                () => MakeService().SyncMoviesAsync(config, None, SaveConfig));
        }

        [Fact]
        public async Task EmptyResponse_NoFilesWritten()
        {
            var config = DefaultConfig();
            Handler.RespondWith("get_vod_streams", "[]");

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var moviesDir = Path.Combine(TempDir.Path, "Movies");
            var files = Directory.Exists(moviesDir)
                ? Directory.GetFiles(moviesDir, "*.strm", SearchOption.AllDirectories)
                : new string[0];
            Assert.Empty(files);
            Assert.Equal(0, SaveConfigCallCount); // no timestamp to save
        }
    }
}
```

**Step 2: Run tests**

```bash
dotnet test Emby.Xtream.Plugin.Tests/ --filter "SyncMoviesIntegrationTests"
```
Expected: all 9 tests pass.

**Step 3: Run full suite**

```bash
dotnet test Emby.Xtream.Plugin.Tests/
```
Expected: 0 failures.

**Step 4: Commit**

```bash
git add Emby.Xtream.Plugin.Tests/SyncMoviesIntegrationTests.cs
git commit -m "test: add SyncMovies integration tests (happy path, smart-skip, orphans, threshold)"
```

---

## Task 7: `SyncSeriesIntegrationTests`

**Files:**
- Create: `Emby.Xtream.Plugin.Tests/SyncSeriesIntegrationTests.cs`

**Step 1: Create the file**

```csharp
using System.IO;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class SyncSeriesIntegrationTests : SyncTestBase
    {
        private void RegisterSeriesResponses(string seriesListJson, string seriesDetailJson, int seriesId = 1)
        {
            Handler.RespondWith("get_series", seriesListJson);
            Handler.RespondWith($"get_series_info&series_id={seriesId}", seriesDetailJson);
        }

        [Fact]
        public async Task HappyPath_WritesEpisodeFile()
        {
            var config = DefaultConfig();
            RegisterSeriesResponses(
                SeriesListJson(Series(seriesId: 1, name: "Breaking Bad", lastModified: "2000")),
                SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1, title: "Pilot"));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var ep = Path.Combine(TempDir.Path, "Shows", "Breaking Bad", "Season 01", "S01E01.strm");
            Assert.True(File.Exists(ep));
        }

        [Fact]
        public async Task EpisodeTitleDeduplication_TitleNotDuplicatedInFilename()
        {
            // Provider sends title "Breaking Bad - S01E01" - should strip to ""
            var config = DefaultConfig();
            RegisterSeriesResponses(
                SeriesListJson(Series(seriesId: 1, name: "Breaking Bad", lastModified: "2000")),
                SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1, title: "Breaking Bad - S01E01"));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var seasonDir = Path.Combine(TempDir.Path, "Shows", "Breaking Bad", "Season 01");
            var files = Directory.GetFiles(seasonDir, "*.strm");
            // Filename should be just S01E01.strm, not "S01E01 - Breaking Bad - S01E01.strm"
            Assert.Single(files);
            Assert.Equal("S01E01.strm", Path.GetFileName(files[0]));
        }

        [Fact]
        public async Task SmartSkip_ExistingEpisode_NotRewritten()
        {
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.LastSeriesSyncTimestamp = 9999; // series.LastModified (2000) <= lastTs

            var ep = Path.Combine(TempDir.Path, "Shows", "Breaking Bad", "Season 01", "S01E01.strm");
            Directory.CreateDirectory(Path.GetDirectoryName(ep));
            File.WriteAllText(ep, "SENTINEL");

            RegisterSeriesResponses(
                SeriesListJson(Series(seriesId: 1, name: "Breaking Bad", lastModified: "2000")),
                SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.Equal("SENTINEL", File.ReadAllText(ep));
        }

        [Fact]
        public async Task NamingVersionUpgrade_ResetsTimestamp_EpisodeRewritten()
        {
            var config = DefaultConfig();
            config.SmartSkipExisting    = true;
            config.LastSeriesSyncTimestamp = 9999;
            config.StrmNamingVersion    = 0; // triggers upgrade → resets timestamp to 0

            var ep = Path.Combine(TempDir.Path, "Shows", "Breaking Bad", "Season 01", "S01E01.strm");
            Directory.CreateDirectory(Path.GetDirectoryName(ep));
            File.WriteAllText(ep, "SENTINEL");

            RegisterSeriesResponses(
                SeriesListJson(Series(seriesId: 1, name: "Breaking Bad", lastModified: "2000")),
                SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            // Timestamp reset to 0 → isChangedSeries = (lastSeriesTs==0) = true → loop runs
            Assert.NotEqual("SENTINEL", File.ReadAllText(ep));
        }

        [Fact]
        public async Task OrphanInSeasonSubdir_FileAndEmptyDirsDeleted()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;

            // Pre-write orphan in a season subdir
            var orphanEp = Path.Combine(TempDir.Path, "Shows", "OldShow", "Season 01", "S01E01.strm");
            Directory.CreateDirectory(Path.GetDirectoryName(orphanEp));
            File.WriteAllText(orphanEp, "http://old");

            // Provider has a different show
            RegisterSeriesResponses(
                SeriesListJson(Series(seriesId: 2, name: "New Show", lastModified: "2000")),
                SeriesDetailJson(seriesId: 2, seasonNum: 1, episodeNum: 1));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(orphanEp));
            Assert.False(Directory.Exists(Path.GetDirectoryName(orphanEp))); // Season 01 removed
            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "OldShow"))); // OldShow removed
        }

        [Fact]
        public async Task AddedZeroProvider_SeriesNotUpdated_FileStillWrittenNoSmartSkip()
        {
            var config = DefaultConfig();
            config.LastSeriesSyncTimestamp = 5000;
            // Series.LastModified = "0" → isChangedSeries = (0 > 5000) = false
            // SmartSkipExisting = false → episode loop still runs

            RegisterSeriesResponses(
                SeriesListJson(Series(seriesId: 1, name: "Breaking Bad", lastModified: "0")),
                SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var ep = Path.Combine(TempDir.Path, "Shows", "Breaking Bad", "Season 01", "S01E01.strm");
            Assert.True(File.Exists(ep));
            // Timestamp NOT updated (maxSeriesTs stays 0, not > 5000)
            Assert.Equal(0, SaveConfigCallCount);
        }

        [Fact]
        public async Task SeriesWithNoEpisodes_NoCrashNoDirRequired()
        {
            var config = DefaultConfig();
            // Return empty episodes dictionary
            var emptyDetail = System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Empty Show", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>()
            });
            Handler.RespondWith("get_series", SeriesListJson(Series(seriesId: 1, name: "Empty Show")));
            Handler.RespondWith("get_series_info&series_id=1", emptyDetail);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var showDir = Path.Combine(TempDir.Path, "Shows", "Empty Show");
            var strms = Directory.Exists(showDir)
                ? Directory.GetFiles(showDir, "*.strm", SearchOption.AllDirectories)
                : new string[0];
            Assert.Empty(strms);
        }

        [Fact]
        public async Task MultiSeason_EpisodesWrittenToCorrectSeasonDirs()
        {
            var config = DefaultConfig();
            var detail = System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Breaking Bad", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>
                {
                    ["1"] = new object[] { new { id = 1, episode_num = 1, title = "Pilot", container_extension = "mp4", season = 1 } },
                    ["2"] = new object[] { new { id = 2, episode_num = 1, title = "Season 2 Ep1", container_extension = "mp4", season = 2 } }
                }
            });
            Handler.RespondWith("get_series", SeriesListJson(Series(seriesId: 1, name: "Breaking Bad")));
            Handler.RespondWith("get_series_info&series_id=1", detail);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(Path.Combine(TempDir.Path, "Shows", "Breaking Bad", "Season 01", "S01E01.strm")));
            Assert.True(File.Exists(Path.Combine(TempDir.Path, "Shows", "Breaking Bad", "Season 02", "S02E01.strm")));
        }
    }
}
```

**Step 2: Run tests**

```bash
dotnet test Emby.Xtream.Plugin.Tests/ --filter "SyncSeriesIntegrationTests"
```
Expected: all 8 tests pass.

**Step 3: Commit**

```bash
git add Emby.Xtream.Plugin.Tests/SyncSeriesIntegrationTests.cs
git commit -m "test: add SyncSeries integration tests (smart-skip, orphan cleanup, multi-season)"
```

---

## Task 8: `XtreamTunerHostTests` - skip for now, mark as future work

`XtreamTunerHost` has a deeper dependency on the Emby `ITunerHost` interface and static `_stats` cache that requires more significant refactoring to make testable (the stats are stored in a static `ConcurrentDictionary` keyed by tuner ID). This is better addressed as a separate design effort once the sync tests are bedded in.

Document this as a follow-up:

**Files:**
- Create: `Emby.Xtream.Plugin.Tests/XtreamTunerHostTests.cs`

```csharp
// TODO: XtreamTunerHost integration tests - tracked as follow-up.
// The stream stats cache (_streamStats) is static and shared across test runs,
// making isolation difficult without refactoring the stats lifecycle.
// See docs/plans/2026-03-07-test-expansion-design.md Section 3 for scenario list.

namespace Emby.Xtream.Plugin.Tests
{
    // placeholder
}
```

**Step 1: Commit placeholder**

```bash
git add Emby.Xtream.Plugin.Tests/XtreamTunerHostTests.cs
git commit -m "test: placeholder for XtreamTunerHost tests (follow-up)"
```

---

## Task 9: Final verification

**Step 1: Run the full test suite**

```bash
dotnet test Emby.Xtream.Plugin.Tests/
```
Expected: ≥ 240 tests, 0 failures.

**Step 2: Push**

```bash
git push origin main
```

---

## Summary of Files Changed

| File | Change |
|------|--------|
| `Emby.Xtream.Plugin/Service/StrmSyncService.cs` | Add `_httpClient` field + ctor overload; replace all `SharedHttpClient` uses; add `saveConfig` param to 3 methods; make `CheckAndUpgradeNamingVersion` internal |
| `Emby.Xtream.Plugin/Service/SyncMoviesTask.cs` | Update call to `SyncMoviesAsync` |
| `Emby.Xtream.Plugin/Service/SyncSeriesTask.cs` | Update call to `SyncSeriesAsync` |
| `Emby.Xtream.Plugin/Emby.Xtream.Plugin.csproj` | Add `InternalsVisibleTo` |
| `Emby.Xtream.Plugin.Tests/Fakes/TempDirectory.cs` | New |
| `Emby.Xtream.Plugin.Tests/Fakes/FakeHttpHandler.cs` | New |
| `Emby.Xtream.Plugin.Tests/SyncTestBase.cs` | New |
| `Emby.Xtream.Plugin.Tests/StrmSyncServiceTests.cs` | Add ~25 tests |
| `Emby.Xtream.Plugin.Tests/SyncMoviesIntegrationTests.cs` | New - 9 tests |
| `Emby.Xtream.Plugin.Tests/SyncSeriesIntegrationTests.cs` | New - 8 tests |
| `Emby.Xtream.Plugin.Tests/XtreamTunerHostTests.cs` | New - placeholder |
