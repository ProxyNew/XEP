using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public sealed record SkyPatcherReference(string Field, string Identifier, string Status, string? FormKey);
public sealed record SkyPatcherRule(int Line, string Text, string Status,
    IReadOnlyList<string> Fields, IReadOnlyList<SkyPatcherReference> References);
public sealed record SkyPatcherFile(string Path, string Sha256, string Status, IReadOnlyList<SkyPatcherRule> Rules);
public sealed record SkyPatcherInputReport(string Status, string Version, string DllSha256, string Fingerprint,
    IReadOnlyList<SkyPatcherFile> Files, IReadOnlyList<string> Diagnostics)
{
    internal IReadOnlyList<string> Information { get; init; } = [];
    internal bool EditorIdCacheAvailable { get; init; }
    // Retain exact bytes (including switches in other categories) for later
    // interpretation of the same snapshot. They are not duplicated in JSON.
    internal IReadOnlyDictionary<string, ReadOnlyMemory<byte>> CapturedBytes { get; init; }
        = new Dictionary<string, ReadOnlyMemory<byte>>();
    internal IReadOnlyDictionary<FormKey, string> NativeListEditorIds { get; init; }
        = new Dictionary<FormKey, string>();
    internal IReadOnlyDictionary<FormKey, IReadOnlySet<FormKey>> KeywordMembers { get; init; }
        = new Dictionary<FormKey, IReadOnlySet<FormKey>>();
    internal IReadOnlySet<string> IrrelevantFurnitureFiles { get; init; } = new HashSet<string>();
    internal string? FurnitureProofBaseFingerprint { get; init; }
    internal IReadOnlyList<string> FurnitureProofInformation { get; init; } = [];
}

// Input evidence only. No rule is a placement witness until its release semantics
// and composition with normal plugin output have been established.
internal static class SkyPatcherInputs
{
    private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "filterByLLs", "filterByLLNPCs", "noFilterLL", "noFilterLLNPC", "filterByEditorIdContains",
        "filterByEditorIdContainsOr", "filterByEditorIdContainsExcluded", "addToLLs", "addOnceToLLs",
        "removeFromLLs", "formsToReplace", "clear", "calcForLevel", "calcEachItem", "calcLevelAndEachItem",
        "calcUseAll", "clearFlags", "addTemplateKeywords", "removeObjectsByKeyword", "chanceGlobal",
        "chanceNone", "objectMultCount"
    };

    public static SkyPatcherInputReport Capture(string? dataFolder, IReadOnlyList<ISkyrimModGetter> mods,
        Func<IReadOnlyDictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)>>? winners = null)
        => CaptureWith(dataFolder, mods, new NativeFormResolver(mods, winners ?? (() => PatcherEngine.LoadOrderWinners(mods))));

    // Scan shares one resolver (winners and EditorID index) with LLOS capture.
    internal static SkyPatcherInputReport CaptureWith(string? dataFolder, IReadOnlyList<ISkyrimModGetter> mods, NativeFormResolver resolver)
    {
        using var timing = PhaseTiming.Measure("skypatcher.capture");
        if (string.IsNullOrWhiteSpace(dataFolder)) return Empty();
        var plugins = Path.Combine(dataFolder, "SKSE", "Plugins");
        var root = Path.Combine(plugins, "SkyPatcher");
        var dll = Path.Combine(plugins, "SkyPatcher.dll");
        var settings = Path.Combine(plugins, "SkyPatcher.ini");
        if (!File.Exists(dll) && !File.Exists(settings) && !Directory.Exists(root)) return Empty();

        var diagnostics = new DiagnosticMessages();
        var files = new List<SkyPatcherFile>();
        var capturedBytes = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        var version = "";
        var dllHash = "";
        var editorIdDllHash = "";
        var editorIdVersion = "";
        string? editorIdSettings = null;
        var enabled = false;
        var uncertain = false;
        try
        {
            if (File.Exists(dll))
            {
                using var stream = File.OpenRead(dll);
                dllHash = Convert.ToHexString(SHA256.HashData(stream));
                version = FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "unknown";
            }
            if (File.Exists(settings))
            {
                var bytes = File.ReadAllBytes(settings);
                capturedBytes.Add("SkyPatcher.ini", bytes);
                var text = Decode(bytes);
                enabled = CategoryEnabled(text, diagnostics);
                files.Add(new("SkyPatcher.ini", Hash(bytes), "settings", Array.Empty<SkyPatcherRule>()));
            }
            else diagnostics.Add("SkyPatcher settings are missing; no enabled leveled-list category is established.");
            var editorIdDll = Path.Combine(plugins, "po3_Tweaks.dll");
            if (File.Exists(editorIdDll))
            {
                using var stream = File.OpenRead(editorIdDll);
                editorIdDllHash = Convert.ToHexString(SHA256.HashData(stream));
                editorIdVersion = FileVersionInfo.GetVersionInfo(editorIdDll).FileVersion ?? "unknown";
                files.Add(new("po3_Tweaks.dll", editorIdDllHash, "editorid-dependency", Array.Empty<SkyPatcherRule>()));
            }
            var editorIdIni = Path.Combine(plugins, "po3_Tweaks.ini");
            if (File.Exists(editorIdIni))
            {
                var bytes = File.ReadAllBytes(editorIdIni);
                capturedBytes.Add("po3_Tweaks.ini", bytes);
                editorIdSettings = Decode(bytes);
                files.Add(new("po3_Tweaks.ini", Hash(bytes), "editorid-dependency", Array.Empty<SkyPatcherRule>()));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            uncertain = true;
            diagnostics.Add("Could not capture SkyPatcher DLL/settings: " + ex.Message);
        }

        // Keep discovery order as evidence. It is NOT certified execution order.
        // Capture other categories too: later route proofs must account for their
        // equipment/consumer mutations without reopening files after Scan.
        if (Directory.Exists(root))
        {
            var directories = new Queue<string>();
            directories.Enqueue(root);
            while (directories.TryDequeue(out var directory))
            {
                try
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if (Directory.Exists(path))
                        {
                            // Avoid following directory links outside the projected tree or cycles.
                            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            {
                                uncertain = true;
                                diagnostics.Add("SkyPatcher directory link was not followed: " + Path.GetRelativePath(root, path));
                            }
                            else directories.Enqueue(path);
                            continue;
                        }
                        var fileName = Path.GetFileName(path);
                        var iniAt = fileName.IndexOf(".ini", StringComparison.Ordinal);
                        if (iniAt < 0) continue;
                        var relative = Path.GetRelativePath(plugins, path).Replace('\\', '/');
                        try
                        {
                            var bytes = File.ReadAllBytes(path);
                            capturedBytes.Add(relative, bytes);
                            var text = Decode(bytes);
                            var itemCategory = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)[0]
                                .Equals("leveledList", StringComparison.OrdinalIgnoreCase);
                            var condition = fileName[..iniAt];
                            var conditional = new[] { ".esp", ".esm", ".esl" }.Any(ext => condition.Contains(ext, StringComparison.Ordinal));
                            var status = dllHash.Length == 0 ? "missing-dll"
                                : conditional && !resolver.HasPlugin(condition) ? "missing-plugin"
                                : !itemCategory ? "other-category-unanalysed"
                                : !enabled ? "disabled" : "active";
                            var rules = ReadRules(text, itemCategory && status == "active", resolver);
                            files.Add(new(relative, Hash(bytes), status, rules));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
                        {
                            uncertain = true;
                            files.Add(new(relative, "", "unreadable", Array.Empty<SkyPatcherRule>()));
                            diagnostics.Add($"Could not capture {relative}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    uncertain = true;
                    diagnostics.Add("Could not enumerate SkyPatcher inputs: " + ex.Message);
                }
            }
        }
        // 8.0 is the supported messaging baseline; the captured parser contract
        // remains input-contract-7.0 and other majors remain unverified.
        var supportedMajor = Major(version) == 8;
        var needsEditorIds = files.Where(f => f.Status == "active").SelectMany(f => f.Rules).Any(r =>
            r.Fields.Any(f => f.StartsWith("filterByEditorId", StringComparison.OrdinalIgnoreCase))
            || r.References.Any(reference => !reference.Identifier.Contains('|')));
        var editorIdCacheAvailable = EditorIdDependencySupported(editorIdDllHash, editorIdSettings);
        var editorIdsSupported = !needsEditorIds || editorIdCacheAvailable;
        var statusOverall = uncertain ? "incomplete-capture" : dllHash.Length == 0 ? "missing-dll"
            : !enabled ? "disabled" : !editorIdsSupported ? "unsupported-editorid-dependency" : "input-contract-7.0";
        if (enabled && !editorIdsSupported)
            diagnostics.Add("SkyPatcher EditorID inputs require po3_Tweaks with [Fixes] Load EditorIDs=true; a missing DLL or disabled cache prevents distribution import.");
        if (enabled && needsEditorIds && editorIdsSupported && Major(editorIdVersion) != 1)
            diagnostics.Add($"po3_Tweaks {editorIdVersion} differs in major version from the observed 1.16 release (DLL {editorIdDllHash}); its EditorID cache behavior is applied unverified.");
        if (enabled && dllHash.Length > 0)
            diagnostics.AddDiagnostic(supportedMajor
                ? $"SkyPatcher {version}: supported 8.0 release; operation support is assessed during distribution analysis."
                : $"SkyPatcher {version} differs in major version from the supported 8.0 release (DLL {dllHash}); its current input contract is applied unverified.", informational: supportedMajor);
        foreach (var file in files.Where(f => f.Status == "active"))
        foreach (var rule in file.Rules.Where(r => r.Status == "ignored-no-op"))
            diagnostics.AddInformation($"SkyPatcher {file.Path}:{rule.Line}: ignored selector-only native no-op: {rule.Text}");
        var missing = files.SelectMany(f => f.Rules).SelectMany(r => r.References).Count(r => r.Status is "missing-plugin" or "missing-record" or "deleted-record");
        if (missing > 0) diagnostics.AddInformation($"SkyPatcher: {missing} references could not resolve against the active load order; they contribute no imported items or distribution.");
        var identity = $"skypatcher-inputs-v1\n{statusOverall}\n{version}\n{dllHash}\n" +
            string.Join('\n', files.Select(f => $"{f.Path}|{f.Sha256}|{f.Status}"));
        return new(statusOverall, version, dllHash, Hash(Encoding.UTF8.GetBytes(identity)),
            files.AsReadOnly(), diagnostics.AsReadOnly())
        {
            Information = diagnostics.Information,
            EditorIdCacheAvailable = editorIdCacheAvailable,
            CapturedBytes = capturedBytes.AsReadOnly(),
            KeywordMembers = resolver.KeywordMembers(files.Where(f => f.Status == "active").SelectMany(f => f.Rules)
                .SelectMany(r => r.References).Where(r => r.Field.Equals("removeObjectsByKeyword", StringComparison.OrdinalIgnoreCase) && r.Status == "resolved")
                .Select(r => FormKey.Factory(r.FormKey!)).ToHashSet()),
            // po3's cache uses try_emplace: overrides retain the first nonempty
            // EditorID, even when the winning record has a different name.
            NativeListEditorIds = needsEditorIds ? mods.SelectMany(m => m.LeveledItems)
                .Where(l => !string.IsNullOrEmpty(l.EditorID)).GroupBy(l => l.FormKey)
                .ToDictionary(g => g.Key, g => g.First().EditorID!).AsReadOnly()
                : new Dictionary<FormKey, string>()
        };
    }

    private static SkyPatcherInputReport Empty() => new("absent", "", "", "", Array.Empty<SkyPatcherFile>(), Array.Empty<string>());
    // FileVersion strings vary in part count (po3_Tweaks reports 1.16.0.1.0);
    // only the leading component decides the major version. Shared with CDF capture.
    internal static int? Major(string version)
        => int.TryParse(version.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : null;

    // Shared with LLOS capture: both read the same native EditorID cache.
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
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool CategoryEnabled(string text, List<string> diagnostics)
    {
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(';') || line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var split = line.IndexOf('=');
            if (!section.Equals("Patcher", StringComparison.OrdinalIgnoreCase) || split < 0 ||
                !line[..split].Trim().Equals("iEnableLeveledListPatching", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(line[(split + 1)..].Trim(), out var enabled)) return enabled != 0;
            diagnostics.Add("Malformed SkyPatcher iEnableLeveledListPatching; category activation is unverified.");
            return false;
        }
        return false;
    }

    private static IReadOnlyList<SkyPatcherRule> ReadRules(string text, bool resolve, NativeFormResolver resolver)
    {
        var result = new List<SkyPatcherRule>();
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(';')) continue;
            var fields = new List<string>();
            var refs = new List<SkyPatcherReference>();
            var malformed = false;
            var unknown = false;
            var npc = false;
            var clauses = line.Split(':');
            foreach (var clause in clauses)
            {
                var split = clause.IndexOf('=');
                if (split < 1) { malformed = true; continue; }
                var field = clause[..split].Trim();
                var value = clause[(split + 1)..].Trim();
                if (fields.Contains(field, StringComparer.OrdinalIgnoreCase) || value.Length == 0) malformed = true;
                fields.Add(field);
                unknown |= !KnownFields.Contains(field);
                npc |= field.Equals("filterByLLNPCs", StringComparison.OrdinalIgnoreCase) || field.Equals("noFilterLLNPC", StringComparison.OrdinalIgnoreCase);
                if (!resolve) continue;
                var kind = field.ToLowerInvariant();
                var expected = kind switch
                {
                    "filterbylls" => typeof(ILeveledItemGetter),
                    "filterbyllnpcs" => typeof(ILeveledNpcGetter),
                    "addtolls" or "addoncetolls" or "removefromlls" or "formstoreplace" or "objectmultcount" => typeof(IItemGetter),
                    "chanceglobal" => typeof(IGlobalGetter),
                    "removeobjectsbykeyword" => typeof(IKeywordGetter),
                    _ => null
                };
                if (expected is null) continue;
                foreach (var entry in value.Split(','))
                {
                    var parts = entry.Split('~');
                    refs.Add(Resolve(resolver, field, kind == "removeobjectsbykeyword" ? entry.Trim() : parts[0].Trim(), expected));
                    if (kind == "formstoreplace")
                    {
                        if (parts.Length < 2) malformed = true;
                        else refs.Add(Resolve(resolver, field, parts[1].Trim(), expected));
                    }
                }
            }
            // Author's General Information: operations require key=value and
            // unknown keys are ignored. The actual add key is addOnceToLLs.
            // Exempt only the proven selector-only shape, with a unique LVLI binding.
            var noOp = resolve && clauses.Length == 2
                && clauses[0].StartsWith("filterByLLs=", StringComparison.Ordinal)
                && clauses[0].IndexOf('=', "filterByLLs=".Length) < 0
                && clauses[1].Trim() == "addOnceToLL"
                && fields.Count == 1 && refs.Count == 1 && refs[0].Status == "resolved"
                && clauses[0]["filterByLLs=".Length..].Trim() == refs[0].Identifier;
            result.Add(new(index + 1, line, !resolve ? "not-analysed" : noOp ? "ignored-no-op" : malformed ? "malformed"
                : unknown ? "unsupported-field" : npc ? "npc-selector-out-of-scope" : "recognized",
                fields.AsReadOnly(), refs.AsReadOnly()));
        }
        return result.AsReadOnly();
    }

    internal static SkyPatcherReference Resolve(NativeFormResolver resolver, string field, string identifier, Type expected)
    {
        SkyPatcherReference Result(string status, IMajorRecordGetter? record = null) => new(field, identifier, status, record?.FormKey.ToString());
        IMajorRecordGetter? record;
        var separator = identifier.IndexOf('|');
        if (separator >= 0)
        {
            if (!resolver.TryGetPlugin(identifier[..separator].Trim(), out var plugin)) return Result("missing-plugin");
            var number = identifier[(separator + 1)..].Trim();
            if (number.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) number = number[2..];
            if (!uint.TryParse(number, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id)) return Result("malformed-identifier");
            // Full load-order IDs are normalized using the named loaded plugin,
            // never inferred from string length or a physically installed file.
            id &= plugin.IsSmallMaster ? 0xFFFu : 0xFFFFFFu;
            record = resolver.Winner(new FormKey(plugin.ModKey, id));
            if (record is null) return Result("missing-record");
        }
        else
        {
            var (status, found) = resolver.EditorId(identifier);
            if (found is null) return Result(status);
            record = found;
        }
        if (record.IsDeleted) return Result("deleted-record");
        return expected.IsInstanceOfType(record) ? Result("resolved", record) : Result("wrong-type");
    }
}
