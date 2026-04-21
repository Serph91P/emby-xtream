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
