using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Integration tests for <see cref="StrmSyncService.SyncSeriesAsync"/>.
    ///
    /// Path structure (SeriesFolderMode = "single"):
    ///   {StrmLibraryPath}/Shows/{seriesName}/Season {N:D2}/{seriesName} - S{N:D2}E{N:D2} - {title}.strm
    ///
    /// URL patterns (no selected categories):
    ///   ...player_api.php?...&amp;action=get_series
    ///   ...player_api.php?...&amp;action=get_series_info&amp;series_id={id}
    ///
    /// Note: get_series_categories is NOT called when SeriesFolderMode = "single".
    /// </summary>
    public class SyncSeriesIntegrationTests : SyncTestBase
    {
        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Compute the expected STRM path for a plain-ASCII episode
        /// (no TMDB/TVDb IDs in folder name).
        /// </summary>
        private string EpisodeStrmPath(
            string seriesName, int season, int episode, string title)
        {
            var seasonFolder = $"Season {season:D2}";
            var sanitizedTitle = string.IsNullOrWhiteSpace(title) ? string.Empty : $" - {title}";
            var fileName = $"{seriesName} - S{season:D2}E{episode:D2}{sanitizedTitle}.strm";
            return Path.Combine(TempDir.Path, "Shows", seriesName, seasonFolder, fileName);
        }

        /// <summary>
        /// Register both the series list and detail responses needed for a single series.
        /// </summary>
        private void RegisterSeriesResponses(string seriesListJson, string seriesDetailJson, int seriesId = 1)
        {
            Handler.RespondWith("action=get_series", seriesListJson);
            Handler.RespondWith($"action=get_series_info&series_id={seriesId}", seriesDetailJson);
        }

        // -----------------------------------------------------------------
        // Test 1: HappyPath_WritesEpisodeFile
        // -----------------------------------------------------------------

        [Fact]
        public async Task HappyPath_WritesEpisodeFile()
        {
            var config = DefaultConfig();
            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));
            var detail = SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1,
                title: "Episode Title", ext: "mp4");
            RegisterSeriesResponses(list, detail, seriesId: 1);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var strmPath = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Episode Title");
            Assert.True(File.Exists(strmPath), $"Expected STRM file at: {strmPath}");

            var content = File.ReadAllText(strmPath);
            // URL format: {BaseUrl}/series/{Username}/{Password}/{episodeId}.{ext}
            Assert.Equal("http://fake-xtream/series/user/pass/101.mp4", content);

            // Timestamp (2000) > 0 → saved
            Assert.Equal(1, SaveConfigCallCount);
        }

        // -----------------------------------------------------------------
        // Test 2: SmartSkip_ExistingEpisode_NotRewritten
        // -----------------------------------------------------------------

        [Fact]
        public async Task SmartSkip_ExistingEpisode_NotRewritten()
        {
            // lastSeriesTs = 9999, series.lastModified = "2000" → 2000 < 9999 → isChangedSeries = false
            // SmartSkipExisting = true AND directory with .strm exists → skip
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.LastSeriesSyncTimestamp = 9999;

            // Pre-write a sentinel episode to trigger smart-skip
            var strmPath = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Episode Title");
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath));
            File.WriteAllText(strmPath, "SENTINEL");

            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));
            // Detail is still needed because SmartSkip check requires existing .strm on disk;
            // but since the directory already exists and has a .strm, the code returns early
            // before making the series_info request. Register it anyway (won't be called,
            // but FakeHttpHandler only throws on unmatched URLs that ARE called).
            Handler.RespondWith("action=get_series", list);
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson());

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.Equal("SENTINEL", File.ReadAllText(strmPath));
        }

        // -----------------------------------------------------------------
        // Test 3: SmartSkip_ChangedSeries_EpisodeRewritten
        // -----------------------------------------------------------------

        [Fact]
        public async Task SmartSkip_ChangedSeries_EpisodeRewritten()
        {
            // lastSeriesTs = 1000, series.lastModified = "5000" → 5000 > 1000 → isChangedSeries = true
            // Even with SmartSkipExisting = true, changed series are always written
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.LastSeriesSyncTimestamp = 1000;

            // Pre-write a sentinel — it must be overwritten because series has changed
            var strmPath = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Episode Title");
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath));
            File.WriteAllText(strmPath, "SENTINEL");

            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "5000"));
            var detail = SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1,
                title: "Episode Title", ext: "mp4");
            RegisterSeriesResponses(list, detail, seriesId: 1);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var content = File.ReadAllText(strmPath);
            Assert.NotEqual("SENTINEL", content);
            Assert.Contains("http://fake-xtream/series/user/pass/101.mp4", content);
        }

        // -----------------------------------------------------------------
        // Test 4: NamingVersionUpgrade_ResetsTimestamp_EpisodeRewritten
        // -----------------------------------------------------------------

        [Fact]
        public async Task NamingVersionUpgrade_ResetsTimestamp_EpisodeRewritten()
        {
            // StrmNamingVersion = 0 → upgrade resets LastSeriesSyncTimestamp to 0
            // With lastSeriesTs = 0 → isChangedSeries = true → episode is always written
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.LastSeriesSyncTimestamp = 9999; // Would normally cause smart-skip
            config.StrmNamingVersion = 0;          // Stale version → triggers upgrade → resets to 0

            var strmPath = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Episode Title");
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath));
            File.WriteAllText(strmPath, "SENTINEL");

            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));
            var detail = SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1,
                title: "Episode Title", ext: "mp4");
            RegisterSeriesResponses(list, detail, seriesId: 1);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var content = File.ReadAllText(strmPath);
            Assert.NotEqual("SENTINEL", content);
            // At least 2 saves: naming-version upgrade + timestamp update
            Assert.True(SaveConfigCallCount >= 2, $"Expected >= 2 saves, got {SaveConfigCallCount}");
        }

        // -----------------------------------------------------------------
        // Test 5: OrphanInSeasonSubdir_FileAndEmptyDirsDeleted
        // -----------------------------------------------------------------

        [Fact]
        public async Task OrphanInSeasonSubdir_FileAndEmptyDirsDeleted()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;

            // Pre-write an orphan episode for "Old Show"
            var orphanStrm = EpisodeStrmPath("Old Show", season: 1, episode: 1, title: "Gone");
            Directory.CreateDirectory(Path.GetDirectoryName(orphanStrm));
            File.WriteAllText(orphanStrm, "orphan");

            // Provider returns only "New Show"
            var list = SeriesListJson(Series(seriesId: 2, name: "New Show", lastModified: "3000"));
            var detail = SeriesDetailJson(seriesId: 2, seasonNum: 1, episodeNum: 1,
                title: "Ep One", ext: "mp4");
            Handler.RespondWith("action=get_series", list);
            Handler.RespondWith("action=get_series_info&series_id=2", detail);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            // Orphan STRM must be deleted
            Assert.False(File.Exists(orphanStrm), "Orphan STRM file should have been deleted");

            // Empty Season 01 dir must also be removed (CleanupOrphans walks up)
            var orphanSeasonDir = Path.GetDirectoryName(orphanStrm);
            Assert.False(Directory.Exists(orphanSeasonDir),
                "Empty season subdirectory should have been removed");

            // New show episode must exist
            var newEpisode = EpisodeStrmPath("New Show", season: 1, episode: 1, title: "Ep One");
            Assert.True(File.Exists(newEpisode), $"Expected new episode at: {newEpisode}");
        }

        // -----------------------------------------------------------------
        // Test 6: AddedZeroProvider_SeriesNotUpdated_FileStillWrittenNoSmartSkip
        // -----------------------------------------------------------------

        [Fact]
        public async Task AddedZeroProvider_SeriesNotUpdated_FileStillWrittenNoSmartSkip()
        {
            // lastModified = "0" → seriesLm = 0 → maxSeriesTs stays at lastSeriesTs (100)
            // SmartSkipExisting = false → always write
            var config = DefaultConfig();
            config.LastSeriesSyncTimestamp = 100;
            config.SmartSkipExisting = false;

            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "0"));
            var detail = SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1,
                title: "Ep One", ext: "mp4");
            RegisterSeriesResponses(list, detail, seriesId: 1);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var strmPath = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Ep One");
            Assert.True(File.Exists(strmPath), $"Expected STRM file at: {strmPath}");
            // maxSeriesTs (0) is NOT > LastSeriesSyncTimestamp (100) → saveConfig not called for timestamp
            Assert.Equal(0, SaveConfigCallCount);
        }

        // -----------------------------------------------------------------
        // Test 7: SeriesWithNoEpisodes_NoCrashNoDirRequired
        // -----------------------------------------------------------------

        [Fact]
        public async Task SeriesWithNoEpisodes_NoCrashNoDirRequired()
        {
            var config = DefaultConfig();
            var list = SeriesListJson(Series(seriesId: 1, name: "Empty Show", lastModified: "1000"));
            // Detail returns no episodes
            var emptyDetail = System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Empty Show", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>()
            });
            Handler.RespondWith("action=get_series", list);
            Handler.RespondWith("action=get_series_info&series_id=1", emptyDetail);

            // Must not throw
            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var showsRoot = Path.Combine(TempDir.Path, "Shows");
            var files = Directory.Exists(showsRoot)
                ? Directory.GetFiles(showsRoot, "*.strm", SearchOption.AllDirectories)
                : Array.Empty<string>();
            Assert.Empty(files);
        }

        // -----------------------------------------------------------------
        // Test 8: EpisodeTitleDeduplication_TitleNotDuplicatedInFilename
        // -----------------------------------------------------------------

        [Fact]
        public async Task EpisodeTitleDeduplication_TitleNotDuplicatedInFilename()
        {
            // Provider embeds series name + episode code in the episode title:
            // title = "Breaking Bad - S01E01"
            // Without deduplication the filename would be:
            //   Breaking Bad - S01E01 - Breaking Bad - S01E01.strm
            // With StripEpisodeTitleDuplicate the title becomes empty, giving:
            //   Breaking Bad - S01E01.strm
            var config = DefaultConfig();
            var list = SeriesListJson(Series(seriesId: 1, name: "Breaking Bad", lastModified: "2000"));
            var detail = System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Breaking Bad", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>
                {
                    ["1"] = new object[]
                    {
                        new { id = 101, episode_num = 1, title = "Breaking Bad - S01E01",
                              container_extension = "mp4", season = 1 }
                    }
                }
            });
            RegisterSeriesResponses(list, detail, seriesId: 1);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            // The clean filename (title stripped to empty string)
            var expectedPath = EpisodeStrmPath("Breaking Bad", season: 1, episode: 1, title: "");
            Assert.True(File.Exists(expectedPath), $"Expected STRM at: {expectedPath}");

            // The duplicated filename must NOT exist
            var duplicatedPath = EpisodeStrmPath("Breaking Bad", season: 1, episode: 1,
                title: "Breaking Bad - S01E01");
            Assert.False(File.Exists(duplicatedPath),
                "Duplicated series name in filename — StripEpisodeTitleDuplicate should have removed it");
        }

        // -----------------------------------------------------------------
        // Test 9: MultiSeason_WritesFilesInCorrectSubdirs
        // -----------------------------------------------------------------

        [Fact]
        public async Task MultiSeason_WritesFilesInCorrectSubdirs()
        {
            var config = DefaultConfig();
            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));

            // Build a detail with episodes in two seasons
            var detail = System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Test Show", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>
                {
                    ["1"] = new object[]
                    {
                        new { id = 101, episode_num = 1, title = "Pilot",    container_extension = "mp4", season = 1 }
                    },
                    ["2"] = new object[]
                    {
                        new { id = 201, episode_num = 1, title = "Premiere", container_extension = "mp4", season = 2 }
                    }
                }
            });

            Handler.RespondWith("action=get_series", list);
            Handler.RespondWith("action=get_series_info&series_id=1", detail);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var s1e1 = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Pilot");
            var s2e1 = EpisodeStrmPath("Test Show", season: 2, episode: 1, title: "Premiere");

            Assert.True(File.Exists(s1e1), $"Expected Season 01 episode at: {s1e1}");
            Assert.True(File.Exists(s2e1), $"Expected Season 02 episode at: {s2e1}");

            Assert.Equal("http://fake-xtream/series/user/pass/101.mp4", File.ReadAllText(s1e1));
            Assert.Equal("http://fake-xtream/series/user/pass/201.mp4", File.ReadAllText(s2e1));
        }
    }
}
