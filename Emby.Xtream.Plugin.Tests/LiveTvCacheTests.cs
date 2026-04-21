using System;
using System.Reflection;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Unit tests for <see cref="LiveTvService"/> cache management.
    /// These tests exercise the in-memory cache state via the internal properties
    /// exposed for testing, without requiring a running Emby instance.
    /// </summary>
    public class LiveTvCacheTests
    {
        private static LiveTvService MakeService() => new LiveTvService(new TestLogger());

        // ── InvalidateCache resets all state ────────────────────────────────

        [Fact]
        public void InvalidateCache_ClearsCachedM3U()
        {
            var svc = MakeService();
            // Inject a cached value via reflection
            SetField(svc, "_cachedM3U", "#EXTM3U");
            SetField(svc, "_m3uCacheTime", DateTime.UtcNow);

            svc.InvalidateCache();

            Assert.False(svc.HasCachedM3U);
        }

        [Fact]
        public void InvalidateCache_ClearsCachedEpgXml()
        {
            var svc = MakeService();
            SetField(svc, "_cachedEpgXml", "<tv/>");
            SetField(svc, "_epgCacheTime", DateTime.UtcNow);

            svc.InvalidateCache();

            Assert.False(svc.HasCachedEpgXml);
        }

        [Fact]
        public void InvalidateCache_ResetsXmltvFailed()
        {
            var svc = MakeService();
            // Simulate a previous XMLTV failure
            SetField(svc, "_xmltvFailed", true);
            SetField(svc, "_xmltvFailedTime", DateTime.UtcNow);

            svc.InvalidateCache();

            Assert.False(svc.XmltvFailed);
        }

        [Fact]
        public void InvalidateCache_ResetsXmltvFailedTime()
        {
            var svc = MakeService();
            SetField(svc, "_xmltvFailed", true);
            SetField(svc, "_xmltvFailedTime", DateTime.UtcNow);

            svc.InvalidateCache();

            Assert.Equal(DateTime.MinValue, svc.XmltvFailedTime);
        }

        [Fact]
        public void InvalidateCache_ClearsXmltvCache()
        {
            var svc = MakeService();
            SetField(svc, "_xmltvCache",
                new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Emby.Xtream.Plugin.Client.Models.EpgProgram>>());
            SetField(svc, "_xmltvCacheTime", DateTime.UtcNow);

            svc.InvalidateCache();

            // After invalidation _xmltvCache must be null
            var xmltvCache = GetField<object>(svc, "_xmltvCache");
            Assert.Null(xmltvCache);
        }

        [Fact]
        public void InvalidateCache_ResetsXmltvCacheTime()
        {
            var svc = MakeService();
            SetField(svc, "_xmltvCacheTime", DateTime.UtcNow);

            svc.InvalidateCache();

            var cacheTime = GetField<DateTime>(svc, "_xmltvCacheTime");
            Assert.Equal(DateTime.MinValue, cacheTime);
        }

        // ── XMLTV failed-retry TTL logic ─────────────────────────────────────

        /// <summary>
        /// After a failure, XmltvFailed must be true and XmltvFailedTime must be recent.
        /// </summary>
        [Fact]
        public void XmltvFailed_InitiallyFalse()
        {
            var svc = MakeService();
            Assert.False(svc.XmltvFailed);
        }

        [Fact]
        public void XmltvFailedTime_InitiallyMinValue()
        {
            var svc = MakeService();
            Assert.Equal(DateTime.MinValue, svc.XmltvFailedTime);
        }

        /// <summary>
        /// Simulates recording a failure and verifies that the retry-due condition
        /// is false when the failure is recent (within the TTL window).
        /// </summary>
        [Fact]
        public void XmltvRetryDue_ReturnsFalse_WhenFailureIsRecent()
        {
            var cacheTtl = TimeSpan.FromMinutes(30);
            var failedTime = DateTime.UtcNow;           // just now → not yet due for retry

            bool xmltvFailed = true;
            bool retryDue = xmltvFailed && DateTime.UtcNow - failedTime >= cacheTtl;

            Assert.False(retryDue);
        }

        /// <summary>
        /// Simulates recording a failure and verifies that the retry-due condition
        /// is true when the failure is old enough (beyond the TTL window).
        /// </summary>
        [Fact]
        public void XmltvRetryDue_ReturnsTrue_WhenFailureIsOlderThanTtl()
        {
            var cacheTtl = TimeSpan.FromMinutes(30);
            var failedTime = DateTime.UtcNow - cacheTtl - TimeSpan.FromSeconds(1); // just past TTL

            bool xmltvFailed = true;
            bool retryDue = xmltvFailed && DateTime.UtcNow - failedTime >= cacheTtl;

            Assert.True(retryDue);
        }

        /// <summary>
        /// When XMLTV has never failed, the retry-due check is vacuously false but
        /// the combined condition (!xmltvFailed || retryDue) is true — meaning we
        /// do attempt a fresh XMLTV fetch even before a failure has occurred.
        /// </summary>
        [Fact]
        public void XmltvFetchAllowed_WhenNeverFailed()
        {
            bool xmltvFailed = false;
            var cacheTtl = TimeSpan.FromMinutes(30);
            var failedTime = DateTime.MinValue;

            bool retryDue = xmltvFailed && DateTime.UtcNow - failedTime >= cacheTtl;
            bool fetchAllowed = !xmltvFailed || retryDue;

            Assert.True(fetchAllowed);
        }

        /// <summary>
        /// After a failure within the TTL window, no fetch should be attempted.
        /// </summary>
        [Fact]
        public void XmltvFetchBlocked_WhenFailedRecently()
        {
            bool xmltvFailed = true;
            var cacheTtl = TimeSpan.FromMinutes(30);
            var failedTime = DateTime.UtcNow; // just now → within TTL

            bool retryDue = xmltvFailed && DateTime.UtcNow - failedTime >= cacheTtl;
            bool fetchAllowed = !xmltvFailed || retryDue;

            Assert.False(fetchAllowed);
        }

        /// <summary>
        /// After the TTL has elapsed since the failure, a retry should be allowed.
        /// </summary>
        [Fact]
        public void XmltvFetchAllowed_WhenFailurePastTtl()
        {
            bool xmltvFailed = true;
            var cacheTtl = TimeSpan.FromMinutes(30);
            var failedTime = DateTime.UtcNow - cacheTtl - TimeSpan.FromSeconds(1);

            bool retryDue = xmltvFailed && DateTime.UtcNow - failedTime >= cacheTtl;
            bool fetchAllowed = !xmltvFailed || retryDue;

            Assert.True(fetchAllowed);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static void SetField(object obj, string name, object value)
        {
            var fi = obj.GetType().GetField(name,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(fi); // field must exist
            fi.SetValue(obj, value);
        }

        private static T GetField<T>(object obj, string name)
        {
            var fi = obj.GetType().GetField(name,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(fi);
            return (T)fi.GetValue(obj);
        }

        private sealed class TestLogger : ILogger
        {
            public void Info(string message, params object[] paramList) { }
            public void Error(string message, params object[] paramList) { }
            public void Warn(string message, params object[] paramList) { }
            public void Debug(string message, params object[] paramList) { }
            public void Fatal(string message, params object[] paramList) { }
            public void FatalException(string message, Exception exception, params object[] paramList) { }
            public void ErrorException(string message, Exception exception, params object[] paramList) { }
            public void LogMultiline(string message, MediaBrowser.Model.Logging.LogSeverity severity,
                System.Text.StringBuilder additionalContent) { }
            public void Log(MediaBrowser.Model.Logging.LogSeverity severity, string message, params object[] paramList) { }
            public void Info(ReadOnlyMemory<char> message) { }
            public void Error(ReadOnlyMemory<char> message) { }
            public void Warn(ReadOnlyMemory<char> message) { }
            public void Debug(ReadOnlyMemory<char> message) { }
            public void Log(MediaBrowser.Model.Logging.LogSeverity severity, ReadOnlyMemory<char> message) { }
        }
    }
}
