using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

// One entry of a directory listing with the attributes the captures test, taken from the same
// enumeration so no capture asks the file system again.
internal sealed record ListedEntry(string Path, string Name, bool IsDirectory, bool IsLink)
{
    public bool IsFile => !IsDirectory;
}

// A framework or po3_Tweaks DLL read once: its SHA-256 and the release number its reporting tool uses.
internal sealed record DllIdentity(string Sha256, string Version)
{
    // CommonLibSSE-NG plugins (LLOS, CID, SPID) carry their release in the SKSEPlugin_Version export.
    public static DllIdentity ReadSkse(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new(FrameworkFiles.Hash(bytes), SksePluginVersion.Read(bytes) ?? "unknown");
    }

    // SkyPatcher, CDF and po3_Tweaks report the file-version resource.
    public static DllIdentity ReadFileVersion(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new(FrameworkFiles.Hash(bytes), FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown");
    }
}

internal static class FrameworkFiles
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // Strict UTF-8 with BOM detection: invalid bytes throw DecoderFallbackException.
    public static string DecodeStrict(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // Replacement decoding with BOM detection (File.ReadAllText).
    public static string DecodeLenient(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // Enumerates like Directory.EnumerateFileSystemEntries did in each capture: entries read before an
    // IO error are kept, and the error is returned so the capture reports an incomplete discovery.
    public static IReadOnlyList<ListedEntry> List(string directory, out string? error)
    {
        var entries = new List<ListedEntry>();
        error = null;
        try
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                entries.Add(new(System.IO.Path.Combine(directory, info.Name), info.Name,
                    (info.Attributes & FileAttributes.Directory) != 0, (info.Attributes & FileAttributes.ReparsePoint) != 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; }
        return entries.AsReadOnly();
    }
}

// The input environment of one scan: every framework capture (SkyPatcher, LLOS, KID, SPID, CID, CDF)
// reads from it. The Data root and SKSE\Plugins are listed once, po3_Tweaks is read once and its
// EditorID-support decision is made once, and the record resolver carries the winners and EditorID index.
// Built per scan from the frozen load order; nothing is kept for a later scan.
internal sealed class FrameworkInputs
{
    public string DataFolder { get; }
    public bool Exists { get; }
    public string PluginsFolder { get; }
    public NativeFormResolver Resolver { get; }
    // Data root entries (files and directories, enumeration order); empty when the folder is absent or the listing failed.
    public IReadOnlyList<ListedEntry> Data { get; } = [];
    public string? DataError { get; }
    public IReadOnlyList<ListedEntry> Plugins { get; } = [];
    public string? PluginsError { get; }
    // po3_Tweaks.dll is installed (its identity is null when reading it failed).
    public bool Po3DllPresent { get; }
    public DllIdentity? Po3Dll { get; private set; }
    // SHA-256 of po3_Tweaks.ini; null when the file is absent, unreadable or not valid UTF-8.
    public string? Po3IniSha256 { get; private set; }
    // A read failure of either file: EditorIdSupport is then "unavailable" and SkyPatcher reports an incomplete capture.
    public Exception? Po3Failure { get; private set; }
    // The ini is not valid UTF-8: SkyPatcher reports an incomplete capture, the EditorID decision still reads it leniently.
    public DecoderFallbackException? Po3IniUndecodable { get; private set; }
    // "available" when po3_Tweaks loads EditorIDs, else "unavailable": decided once for every capture.
    public string EditorIdSupport { get; private set; } = "unavailable";

    private FrameworkInputs(string? dataFolder, IReadOnlyList<ISkyrimModGetter> mods,
        IReadOnlyDictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)> winners, IEnumerable<(string EditorId, FormKey Key)> editorIds, bool indexEditorIds)
    {
        using var timing = PhaseTiming.Measure("framework.inputs");
        DataFolder = dataFolder ?? "";
        Exists = !string.IsNullOrWhiteSpace(dataFolder) && Directory.Exists(dataFolder);
        PluginsFolder = Exists ? Path.Combine(DataFolder, "SKSE", "Plugins") : "";
        if (Exists)
        {
            Data = FrameworkFiles.List(DataFolder, out var dataError);
            DataError = dataError;
            if (Directory.Exists(PluginsFolder))
            {
                Plugins = FrameworkFiles.List(PluginsFolder, out var pluginsError);
                PluginsError = pluginsError;
            }
            Po3DllPresent = PluginFile("po3_Tweaks.dll") is not null;
            ReadPo3Tweaks();
        }
        // The EditorID index is an input structure of this scan: built here when any configuration can name a record.
        Resolver = new NativeFormResolver(mods, winners, indexEditorIds || HasConfigurationFiles() ? editorIds : null);
    }

    // editorIds are the names the winner read collected. indexEditorIds forces the EditorID index for a scan
    // whose captures were supplied frozen (fixtures): the consumers of those captures still resolve EditorIDs against it.
    public static FrameworkInputs Create(string? dataFolder, IReadOnlyList<ISkyrimModGetter> mods,
        IReadOnlyDictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)> winners, IEnumerable<(string EditorId, FormKey Key)> editorIds, bool indexEditorIds = false)
        => new(dataFolder, mods, winners, editorIds, indexEditorIds);

    // Fixtures: the environment of one Data folder over its own winner read.
    public static FrameworkInputs Create(string? dataFolder, IReadOnlyList<ISkyrimModGetter> mods)
    {
        var editorIds = new List<(string EditorId, FormKey Key)>();
        return new(dataFolder, mods, PatcherEngine.LoadOrderWinners(mods, editorIds), editorIds, false);
    }

    // Exact names are probed at their literal path, as the captures did before sharing a listing: the
    // listing's on-disk casing must not reach report paths or fingerprints, and a failed listing must
    // not turn a present file into an absent one.
    public ListedEntry? PluginFile(string name) => Probe(PluginsFolder, name, directory: false);
    public ListedEntry? PluginDirectory(string name) => Probe(PluginsFolder, name, directory: true);
    public ListedEntry? DataDirectory(string name) => Probe(DataFolder, name, directory: true);

    private ListedEntry? Probe(string folder, string name, bool directory)
    {
        if (!Exists) return null;
        var path = Path.Combine(folder, name);
        if (directory ? !Directory.Exists(path) : !File.Exists(path)) return null;
        // Unreadable attributes are treated as a link: the capture then reports it and does not follow it.
        bool link;
        try { link = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { link = true; }
        return new(path, name, directory, link);
    }

    // Files of SKSE\Plugins whose name ends with the suffix, in case-insensitive path order.
    public IEnumerable<ListedEntry> PluginFilesEndingWith(string suffix)
        => Plugins.Where(e => e.IsFile && e.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase);

    // Files of the Data root whose name ends with the suffix, in case-insensitive path order.
    public IEnumerable<ListedEntry> DataFilesEndingWith(string suffix)
        => Data.Where(e => e.IsFile && e.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase);

    // A file name pattern "*middle*.dll" of SKSE\Plugins.
    public bool AnyPluginDllContaining(string middle)
        => Plugins.Any(e => e.IsFile && e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && e.Name.IndexOf(middle, StringComparison.OrdinalIgnoreCase) is var at && at >= 0 && at + middle.Length <= e.Name.Length - 4);

    // Whether the scan has a configuration that can name a record: the file families SkyPatcher, LLOS, KID, SPID, CID and CDF read.
    private bool HasConfigurationFiles()
        => Data.Any(e => e.IsDirectory ? e.Name.Equals("LLOS", StringComparison.OrdinalIgnoreCase)
                : e.Name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                    && (e.Name.Contains("_DISTR", StringComparison.Ordinal) || e.Name.Contains("_LLOS", StringComparison.Ordinal)
                        || e.Name.EndsWith("_CID.ini", StringComparison.Ordinal) || e.Name.EndsWith("_KID.ini", StringComparison.OrdinalIgnoreCase)))
            || PluginDirectory("SkyPatcher") is not null || PluginDirectory("ContainerDistributionFramework") is not null;

    // po3_Tweaks is read once; SkyPatcher, LLOS, CID and SPID take its identity and EditorID decision from here.
    private void ReadPo3Tweaks()
    {
        var dll = PluginFile("po3_Tweaks.dll");
        var ini = PluginFile("po3_Tweaks.ini");
        try
        {
            if (dll is not null) Po3Dll = DllIdentity.ReadFileVersion(dll.Path);
            if (ini is null) return;
            var bytes = File.ReadAllBytes(ini.Path);
            try { FrameworkFiles.DecodeStrict(bytes); Po3IniSha256 = FrameworkFiles.Hash(bytes); }
            catch (DecoderFallbackException ex) { Po3IniUndecodable = ex; }
            if (Po3Dll is not null)
                EditorIdSupport = EditorIdDependencySupported(Po3Dll.Sha256, FrameworkFiles.DecodeLenient(bytes)) ? "available" : "unavailable";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Po3Failure = ex;
            EditorIdSupport = "unavailable";
        }
    }

    // po3_Tweaks' native EditorID cache: its DLL must be installed and [Fixes] Load EditorIDs=true set exactly once.
    internal static bool EditorIdDependencySupported(string dllHash, string? settings)
    {
        if (dllHash.Length == 0 || settings is null) return false;
        var fixes = false;
        bool? enabled = null;
        foreach (var raw in settings.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(';') || line.StartsWith('#') || line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { fixes = line[1..^1].Equals("Fixes", StringComparison.OrdinalIgnoreCase); continue; }
            var pair = line.Split('=', 2);
            if (!fixes || pair.Length != 2 || !pair[0].Trim().Equals("Load EditorIDs", StringComparison.OrdinalIgnoreCase)) continue;
            if (enabled.HasValue || !bool.TryParse(pair[1].Trim(), out var value)) return false;
            enabled = value;
        }
        return enabled == true;
    }
}

// How a captured reference's numeric part reaches a plugin record: the native difference of each tool.
internal enum FormIdRange
{
    // A local ID above the plugin's range names no record: CommonLib composes the runtime prefix with the raw value
    // (LLOS Utility::AcquireForm, CID Utility::GetBoundObject, SPID FormData).
    Refuse,
    // The ID is masked to the plugin's local range (SkyPatcher full load-order IDs, CDF).
    Mask,
}

internal enum MergeMapperHandling
{
    // MergeMapper is not consulted (CID, SkyPatcher, CDF).
    Ignore,
    // With MergeMapper installed an unknown plugin may be a merged one: "merge-mapping-unavailable" (LLOS).
    MissingPlugin,
    // MergeMapper also remaps every numeric reference before lookup, so even a known plugin is unverified (SPID FormData.h).
    AllReferences,
}

internal static class FormLocator
{
    // The common tail of the `hex~Plugin`, `Plugin|hex` and `0xhex|Plugin` lookups: named plugin, malformed number,
    // local range, winner. Each tool parses its own spelling first. A null id is an unparsable number, reported once the
    // plugin is known (SkyPatcher). Failure is null when the record was located.
    public static (string? Failure, ISkyrimMajorRecordGetter? Record) Locate(NativeFormResolver resolver, string pluginName, uint? id,
        FormIdRange range, MergeMapperHandling mapper = MergeMapperHandling.Ignore, bool mergeMapperPresent = false)
    {
        if (!resolver.TryGetPlugin(pluginName, out var plugin))
            return (mergeMapperPresent && mapper != MergeMapperHandling.Ignore ? "merge-mapping-unavailable" : "missing-plugin", null);
        if (mergeMapperPresent && mapper == MergeMapperHandling.AllReferences) return ("merge-mapping-unverified", null);
        if (id is not { } local) return ("malformed-identifier", null);
        var limit = plugin.IsSmallMaster ? 0xFFFu : 0xFFFFFFu;
        if (range == FormIdRange.Refuse)
        {
            if (local > limit) return ("outside-local-range", null);
        }
        else local &= limit;
        var record = resolver.Winner(new FormKey(plugin.ModKey, local));
        return record is null ? ("missing-record", null) : (null, record);
    }
}

// A framework release: the verified build is accepted silently, its major version with a label, any other
// or unreadable major with a warning.
internal readonly record struct FrameworkRelease(string Version, string DllSha256, int Major, string Label)
{
    public string Status(string version, string dllHash)
        => version == Version && dllHash == DllSha256 ? "source-matched-" + Label
            : MajorOf(version) == Major ? "accepted-major-" + Major : "accepted-other-major";

    // FileVersion strings vary in part count (po3_Tweaks reports 1.16.0.1.0); only the leading component decides.
    internal static int? MajorOf(string version)
        => int.TryParse(version.Split('.')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major) ? major : null;
}

// A C conversion the native code performs inside a noexcept function: the game terminates.
internal sealed class NativeConversionException(bool outOfRange, string text)
    : Exception(outOfRange ? "std::stoul: out of range" : "std::stoul: no conversion")
{
    public bool OutOfRange { get; } = outOfRange;
    public string Text { get; } = text;
}

// Native C conversions shared by the tools.
internal static class NativeNumbers
{
    // std::strtoul on a 32-bit unsigned long: leading space, optional sign (negation modulo 2^32), an optional
    // 0x prefix in base 16 when a hex digit follows, trailing text ignored. Never throws: no digits yields zero
    // with digits=false, and a magnitude above 2^32-1 saturates with overflow=true.
    public static uint UnsignedLong(string text, int radix, out bool digits, out bool overflow)
    {
        var i = 0;
        while (i < text.Length && IsSpace(text[i])) i++;
        var negative = i < text.Length && text[i] == '-';
        if (i < text.Length && text[i] is '+' or '-') i++;
        if (radix == 16 && i + 2 < text.Length && text[i] == '0' && text[i + 1] is 'x' or 'X' && Uri.IsHexDigit(text[i + 2])) i += 2;
        var start = i;
        ulong value = 0;
        overflow = false;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            int digit = c is >= '0' and <= '9' ? c - '0' : radix == 16 && Uri.IsHexDigit(c) ? char.ToUpperInvariant(c) - 'A' + 10 : -1;
            if (digit < 0) break;
            if (overflow) continue;
            value = value * (ulong)radix + (ulong)digit;
            if (value > uint.MaxValue) overflow = true;
        }
        digits = i > start;
        if (!digits) return 0;
        if (overflow) return uint.MaxValue;
        return negative ? unchecked(0u - (uint)value) : (uint)value;
    }

    // std::strtoul as LLOS uses it: failures yield zero or the saturated maximum.
    public static uint UnsignedLong(string text, int radix) => UnsignedLong(text, radix, out _, out _);

    // std::stoul as CID and SPID use it: no digits or a magnitude above 2^32-1 throws.
    public static uint Stoul(string text, int radix)
    {
        var value = UnsignedLong(text, radix, out var digits, out var overflow);
        if (overflow) throw new NativeConversionException(true, text);
        if (!digits) throw new NativeConversionException(false, text);
        return value;
    }

    public static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';
}
