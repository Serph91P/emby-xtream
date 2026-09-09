using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;

namespace Emby.RuntimeCompatibility
{
    internal static class Program
    {
        private const string PluginTypeName = "Emby.M3uEditor.Plugin.Service.M3uEditorLiveStream";
        private const string LiveStreamTypeName = "MediaBrowser.Controller.Library.ILiveStream";

        private static int Main(string[] args)
        {
            if (args.Length != 5)
            {
                Console.Error.WriteLine("Usage: RuntimeAbiHarness <candidate> <released> <sdk> <server-4.9> <server-4.10>");
                return 2;
            }

            try
            {
                VerifyCandidate(args[0], "SDK 4.8.0.80", args[2]);
                VerifyCandidate(args[0], "Emby Server 4.9.5.0", args[3]);
                VerifyCandidate(args[0], "Emby Server 4.10.0.40", args[4]);
                VerifyHistoricalFailure(args[1], args[4]);
                Console.WriteLine("Runtime ABI compatibility checks passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void VerifyCandidate(string pluginPath, string runtimeName, string runtimeDirectory)
        {
            using (var context = new ServerAssemblyLoadContext(runtimeDirectory, Path.GetDirectoryName(pluginPath)))
            {
                var controller = context.LoadFromAssemblyPath(Path.Combine(runtimeDirectory, "MediaBrowser.Controller.dll"));
                var liveStreamInterface = controller.GetType(LiveStreamTypeName, true);
                var plugin = context.LoadFromAssemblyPath(pluginPath);
                var liveStreamType = plugin.GetType(PluginTypeName, true);
                var map = liveStreamType.GetInterfaceMap(liveStreamInterface);

                if (map.InterfaceMethods.Length != liveStreamInterface.GetMethods().Length ||
                    map.TargetMethods.Length != map.InterfaceMethods.Length ||
                    map.TargetMethods.Any(method => method == null))
                    throw new InvalidOperationException(runtimeName + " did not map every ILiveStream member.");

                var instance = CreateLiveStream(context, liveStreamType);
                try
                {
                    InvokeConsumer(map, liveStreamType, instance, "AddConsumer");
                    AssertConsumerCount(liveStreamType, instance, 1, runtimeName + " after AddConsumer");
                    InvokeConsumer(map, liveStreamType, instance, "RemoveConsumer");
                    AssertConsumerCount(liveStreamType, instance, 0, runtimeName + " after RemoveConsumer");
                    Console.WriteLine(runtimeName + ": candidate mapped " + map.InterfaceMethods.Length + " ILiveStream members.");
                }
                finally
                {
                    (instance as IDisposable)?.Dispose();
                }
            }
        }

        private static void VerifyHistoricalFailure(string releasedPluginPath, string runtimeDirectory)
        {
            try
            {
                using (var context = new ServerAssemblyLoadContext(runtimeDirectory, Path.GetDirectoryName(releasedPluginPath)))
                {
                    var controller = context.LoadFromAssemblyPath(Path.Combine(runtimeDirectory, "MediaBrowser.Controller.dll"));
                    var liveStreamInterface = controller.GetType(LiveStreamTypeName, true);
                    var plugin = context.LoadFromAssemblyPath(releasedPluginPath);
                    var liveStreamType = plugin.GetType(PluginTypeName, true);
                    liveStreamType.GetInterfaceMap(liveStreamInterface);
                }
            }
            catch (TypeLoadException exception) when (exception.Message.IndexOf("AddConsumer", StringComparison.Ordinal) >= 0)
            {
                Console.WriteLine("Emby Server 4.10.0.40: released v1.5.0 failed as expected: " + exception.Message);
                return;
            }

            throw new InvalidOperationException("Released v1.5.0 unexpectedly mapped to Emby Server 4.10.0.40.");
        }

        private static object CreateLiveStream(ServerAssemblyLoadContext context, Type liveStreamType)
        {
            var mediaSourceType = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "MediaBrowser.Model.dll"))
                .GetType("MediaBrowser.Model.Dto.MediaSourceInfo", true);
            var mediaSource = Activator.CreateInstance(mediaSourceType);
            mediaSourceType.GetProperty("Id").SetValue(mediaSource, "runtime-abi");
            mediaSourceType.GetProperty("Path").SetValue(mediaSource, "http://127.0.0.1:9/deferred");

            var constructor = liveStreamType.GetConstructors().Single(ctor => ctor.GetParameters().Length == 4);
            return constructor.Invoke(new object[] { mediaSource, "runtime-abi", new HttpClient(), null });
        }

        private static void InvokeConsumer(InterfaceMapping map, Type liveStreamType, object instance, string name)
        {
            var index = Array.FindIndex(map.InterfaceMethods, method => method.Name == name);
            if (index >= 0)
            {
                map.TargetMethods[index].Invoke(instance, new object[] { "runtime-abi" });
                return;
            }

            // 4.8 and 4.9 predate the interface callbacks; the candidate method
            // still executes in their isolated runtime contexts.
            liveStreamType.GetMethod(name, new[] { typeof(string) }).Invoke(instance, new object[] { "runtime-abi" });
        }

        private static void AssertConsumerCount(Type liveStreamType, object instance, int expected, string message)
        {
            var actual = (int)liveStreamType.GetProperty("ConsumerCount").GetValue(instance);
            if (actual != expected)
                throw new InvalidOperationException(message + ": expected " + expected + ", actual " + actual + ".");
        }
    }

    internal sealed class ServerAssemblyLoadContext : AssemblyLoadContext, IDisposable
    {
        private readonly string _pluginDirectory;

        public ServerAssemblyLoadContext(string runtimeDirectory, string pluginDirectory)
            : base("emby-runtime-" + Path.GetFileName(runtimeDirectory), true)
        {
            RuntimeDirectory = runtimeDirectory;
            _pluginDirectory = pluginDirectory;
        }

        public string RuntimeDirectory { get; }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == "netstandard" || assemblyName.Name.StartsWith("System", StringComparison.Ordinal) || assemblyName.Name.StartsWith("Microsoft", StringComparison.Ordinal))
                return null;

            var runtimeAssembly = Path.Combine(RuntimeDirectory, assemblyName.Name + ".dll");
            if (File.Exists(runtimeAssembly))
                return LoadFromAssemblyPath(runtimeAssembly);

            var pluginAssembly = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
            if (File.Exists(pluginAssembly))
                return LoadFromAssemblyPath(pluginAssembly);

            return null;
        }

        public void Dispose()
        {
            Unload();
        }
    }
}
