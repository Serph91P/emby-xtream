using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin;
using Emby.M3uEditor.Plugin.Service;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class M3uEditorLiveStreamConsumerTests
    {
        [Fact]
        public void ConsumerCallbacks_ArePublicVoidMethodsWithSingleStringParameter()
        {
            AssertConsumerMethod("AddConsumer");
            AssertConsumerMethod("RemoveConsumer");
        }

        [Fact]
        public void ConsumerCallbacks_CountEveryCall_AndNeverUnderflow()
        {
            using (var stream = CreateLiveStream(new TrackingHttpHandler()))
            {
                var addConsumer = GetConsumerMethod("AddConsumer");
                var removeConsumer = GetConsumerMethod("RemoveConsumer");

                Assert.Equal(0, stream.ConsumerCount);
                addConsumer.Invoke(stream, new object[] { "same-consumer" });
                addConsumer.Invoke(stream, new object[] { "same-consumer" });
                addConsumer.Invoke(stream, new object[] { "another-consumer" });
                Assert.Equal(3, stream.ConsumerCount);

                removeConsumer.Invoke(stream, new object[] { "same-consumer" });
                removeConsumer.Invoke(stream, new object[] { "missing-consumer" });
                removeConsumer.Invoke(stream, new object[] { "another-consumer" });
                removeConsumer.Invoke(stream, new object[] { "another-consumer" });
                Assert.Equal(0, stream.ConsumerCount);
            }
        }

        [Fact]
        public void ConsumerCallbacks_PreserveLegacyCounterAndDoNotOwnTransport()
        {
            var handler = new TrackingHttpHandler();
            using (var stream = CreateLiveStream(handler))
            {
                stream.ConsumerCount = 7;
                Assert.False(stream.EnableStreamSharing);

                GetConsumerMethod("AddConsumer").Invoke(stream, new object[] { "consumer" });
                GetConsumerMethod("RemoveConsumer").Invoke(stream, new object[] { "consumer" });

                Assert.Equal(7, stream.ConsumerCount);
                Assert.Equal(0, handler.RequestCount);
                Assert.Equal(0, handler.DisposeCount);
            }
        }

        [Fact]
        public async Task Open_DefersConnection_AndCloseOwnsDisposal()
        {
            GetConsumerMethod("AddConsumer");
            var handler = new TrackingHttpHandler();
            var stream = CreateLiveStream(handler);
            var output = new MemoryStream();
            var previousPlugin = SetTestPluginInstance();
            try
            {
                await stream.Open(CancellationToken.None);
                Assert.Equal(0, handler.RequestCount);

                await stream.CopyToAsync(output, null, null, CancellationToken.None);
                Assert.Equal(1, handler.RequestCount);
                Assert.NotNull(handler.ResponseStream);
                Assert.Equal(0, handler.ResponseStream.DisposeCount);

                await stream.Close();
                Assert.True(handler.ResponseStream.DisposeCount > 0);
                Assert.Equal(1, handler.DisposeCount);
            }
            finally
            {
                SetPluginInstance(previousPlugin);
                output.Dispose();
                stream.Dispose();
            }
        }

        private static M3uEditorLiveStream CreateLiveStream(HttpMessageHandler handler)
        {
            return new M3uEditorLiveStream(
                new MediaSourceInfo { Id = "stream", Path = "https://example.test/live" },
                "tuner",
                new HttpClient(handler));
        }

        private static void AssertConsumerMethod(string name)
        {
            var method = GetConsumerMethod(name);
            Assert.True(method.IsPublic);
            Assert.True(method.IsVirtual);
            Assert.Equal(typeof(void), method.ReturnType);
            var parameters = method.GetParameters();
            Assert.Single(parameters);
            Assert.Equal(typeof(string), parameters[0].ParameterType);
            Assert.Equal("consumerId", parameters[0].Name);
        }

        private static MethodInfo GetConsumerMethod(string name)
        {
            var method = typeof(M3uEditorLiveStream).GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(string) },
                null);
            Assert.NotNull(method);
            return method;
        }

        private static Plugin SetTestPluginInstance()
        {
            var previous = Plugin.InstanceOrNull;
            var instance = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
            for (var type = typeof(Plugin); type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (field.FieldType == typeof(object) && field.GetValue(instance) == null)
                        field.SetValue(instance, new object());
                    if (field.FieldType == typeof(PluginConfiguration) && field.GetValue(instance) == null)
                        field.SetValue(instance, new PluginConfiguration());
                }
            }

            SetPluginInstance(instance);
            return previous;
        }

        private static void SetPluginInstance(Plugin instance)
        {
            typeof(Plugin).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, instance);
        }

        private sealed class TrackingHttpHandler : HttpMessageHandler
        {
            public int RequestCount { get; private set; }
            public int DisposeCount { get; private set; }
            public TrackingStream ResponseStream { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                ResponseStream = new TrackingStream();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(ResponseStream),
                });
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    DisposeCount++;
                base.Dispose(disposing);
            }
        }

        private sealed class TrackingStream : MemoryStream
        {
            public TrackingStream() : base(new byte[] { 1, 2, 3 }) { }

            public int DisposeCount { get; private set; }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    DisposeCount++;
                base.Dispose(disposing);
            }
        }
    }
}
