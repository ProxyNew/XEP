using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;

namespace XEnchantmentPatcher;

// A new instance belongs to each LoadedGame. Only discovery listings are cached;
// file contents, existence checks, archive precedence and readers stay in Mutagen.
internal sealed class DiscoveryFileSystem : FileSystem
{
    public override IDirectory Directory { get; }
    public override IFile File { get; }
    private readonly HashSet<string> observedArchives = new(StringComparer.OrdinalIgnoreCase);
    private readonly object archivesGate = new();

    public DiscoveryFileSystem(string dataFolder) : this(dataFolder, TimeSpan.FromSeconds(2)) { }
    internal DiscoveryFileSystem(string dataFolder, TimeSpan retryWindow)
    {
        Directory = new DiscoveryDirectory(this, dataFolder);
        File = new ObservedInputFile(this, retryWindow);
    }

    private sealed class ObservedInputFile(DiscoveryFileSystem owner, TimeSpan retryWindow) : FileWrapper(owner)
    {
        public override FileSystemStream Open(string path, FileMode mode, FileAccess access, FileShare share)
        {
            bool observed;
            lock (owner.archivesGate) observed = mode == FileMode.Open && access == FileAccess.Read && owner.observedArchives.Contains(path);
            if (!observed) return base.Open(path, mode, access, share);
            var opening = Stopwatch.StartNew();
            while (true)
            {
                try { return base.Open(path, mode, access, share); }
                catch (FileNotFoundException ex)
                {
                    if (opening.Elapsed >= retryWindow)
                        throw new IOException($"Previously observed input archive is no longer available: {path}. Keep MO2 running with the unchanged profile and restart this session. No reduced input universe was accepted.", ex);
                    Thread.Sleep(25);
                }
            }
        }
    }

    private sealed class DiscoveryDirectory : DirectoryWrapper
    {
        private readonly DiscoveryFileSystem owner;
        private readonly string dataFolder;
        private readonly string stringsFolder;
        private readonly object gate = new();
        private readonly Dictionary<(string Path, string Pattern), ReadOnlyCollection<string>> listings = new();

        public DiscoveryDirectory(DiscoveryFileSystem fileSystem, string dataFolder) : base(fileSystem)
        {
            owner = fileSystem;
            this.dataFolder = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dataFolder));
            stringsFolder = System.IO.Path.Combine(this.dataFolder, "Strings");
        }

        // These are the exact two-argument calls made by Mutagen 0.54.4 through
        // Noggog. Other overloads/options and directories deliberately pass through.
        public override IEnumerable<string> EnumerateFiles(string path, string searchPattern)
        {
            if (!Cacheable(path, searchPattern)) return base.EnumerateFiles(path, searchPattern);
            lock (gate)
            {
                var key = (path, searchPattern); // Preserve original spelling and output paths.
                if (listings.TryGetValue(key, out var files)) return files;
                // Only completed, successful enumeration is retained. No exception or
                // partial listing is cached, and callers cannot mutate the stored array.
                files = Array.AsReadOnly(base.EnumerateFiles(path, searchPattern).ToArray());
                if (System.IO.Path.TrimEndingDirectorySeparator(path).Equals(dataFolder, StringComparison.OrdinalIgnoreCase) && searchPattern.Equals("*.bsa", StringComparison.OrdinalIgnoreCase))
                    lock (owner.archivesGate) foreach (var archive in files) owner.observedArchives.Add(archive);
                listings.Add(key, files);
                return files;
            }
        }

        private bool Cacheable(string path, string pattern)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(pattern) || !System.IO.Path.IsPathFullyQualified(path)) return false;
            var folder = System.IO.Path.TrimEndingDirectorySeparator(path);
            return folder.Equals(dataFolder, StringComparison.OrdinalIgnoreCase) && pattern.Equals("*.bsa", StringComparison.OrdinalIgnoreCase)
                || folder.Equals(stringsFolder, StringComparison.OrdinalIgnoreCase) && pattern.EndsWith("*STRINGS", StringComparison.Ordinal)
                    && pattern.IndexOfAny(['/', '\\', ':']) < 0;
        }
    }
}
