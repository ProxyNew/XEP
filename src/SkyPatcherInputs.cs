using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    // The category switches of SkyPatcher.ini, recorded at capture (including categories XEP does not
    // analyse): a category runs when its value reads nonzero as GetPrivateProfileIntA reads it. Null when the
    // file is absent or cannot be read as the native call reads it (the capture is then incomplete).
    internal IReadOnlyDictionary<string, bool>? CategorySwitches { get; init; }
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

    // The simple form for fixtures: builds the scan environment for one Data folder.
    public static SkyPatcherInputReport Capture(string? dataFolder, IReadOnlyList<ISkyrimModGetter> mods)
        => Capture(FrameworkInputs.Create(dataFolder, mods));

    internal static SkyPatcherInputReport Capture(FrameworkInputs inputs)
    {
        using var timing = PhaseTiming.Measure("skypatcher.capture");
        if (!inputs.Exists) return Empty();
        var resolver = inputs.Resolver;
        var plugins = inputs.PluginsFolder;
        var rootEntry = inputs.PluginDirectory("SkyPatcher");
        var dllEntry = inputs.PluginFile("SkyPatcher.dll");
        var settingsEntry = inputs.PluginFile("SkyPatcher.ini");
        if (dllEntry is null && settingsEntry is null && rootEntry is null) return Empty();
        var root = rootEntry?.Path ?? "";

        var diagnostics = new DiagnosticMessages();
        var files = new List<SkyPatcherFile>();
        var version = "";
        var dllHash = "";
        var editorIdDllHash = "";
        var editorIdVersion = "";
        var enabled = false;
        var uncertain = false;
        var po3Captured = false;
        IReadOnlyDictionary<string, bool>? categorySwitches = null;
        try
        {
            if (dllEntry is not null)
            {
                var dll = DllIdentity.ReadFileVersion(dllEntry.Path);
                dllHash = dll.Sha256;
                version = dll.Version;
            }
            if (settingsEntry is not null)
            {
                // One decode and one reader; leveled lists read the same map as every category. Switches the
                // reader cannot reproduce make the capture incomplete instead of reading as off.
                var bytes = File.ReadAllBytes(settingsEntry.Path);
                var unverified = new List<string>();
                categorySwitches = CategorySwitches(bytes, unverified);
                enabled = categorySwitches is not null && categorySwitches.TryGetValue("LeveledList", out var leveledLists) && leveledLists;
                if (unverified.Count > 0)
                {
                    uncertain = true;
                    diagnostics.Add($"SkyPatcher.ini category switches are unverified: {string.Join("; ", unverified)}. The capture is incomplete.");
                }
                files.Add(new("SkyPatcher.ini", FrameworkFiles.Hash(bytes), "settings", Array.Empty<SkyPatcherRule>()));
            }
            else diagnostics.Add("SkyPatcher settings are missing; no enabled leveled-list category is established.");
            // po3_Tweaks was read once for the scan.
            if (inputs.Po3Dll is { } po3)
            {
                editorIdDllHash = po3.Sha256;
                editorIdVersion = po3.Version;
                files.Add(new("po3_Tweaks.dll", editorIdDllHash, "editorid-dependency", Array.Empty<SkyPatcherRule>()));
            }
            if (inputs.Po3Failure is { } failure) throw failure;
            if (inputs.Po3IniUndecodable is { } undecodable) throw undecodable;
            if (inputs.Po3IniSha256 is { } iniHash)
                files.Add(new("po3_Tweaks.ini", iniHash, "editorid-dependency", Array.Empty<SkyPatcherRule>()));
            po3Captured = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            uncertain = true;
            diagnostics.Add("Could not capture SkyPatcher DLL/settings: " + ex.Message);
        }

        // Keep discovery order as evidence. It is NOT certified execution order.
        // Capture other categories too: later route proofs must account for their
        // equipment/consumer mutations without reopening files after Scan.
        if (rootEntry is not null)
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
                            var text = FrameworkFiles.DecodeStrict(bytes);
                            var itemCategory = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)[0]
                                .Equals("leveledList", StringComparison.OrdinalIgnoreCase);
                            var condition = fileName[..iniAt];
                            var conditional = new[] { ".esp", ".esm", ".esl" }.Any(ext => condition.Contains(ext, StringComparison.Ordinal));
                            var status = dllHash.Length == 0 ? "missing-dll"
                                : conditional && !resolver.HasPlugin(condition) ? "missing-plugin"
                                : !itemCategory ? "other-category-unanalysed"
                                : !enabled ? "disabled" : "active";
                            var rules = ReadRules(text, itemCategory && status == "active", resolver);
                            files.Add(new(relative, FrameworkFiles.Hash(bytes), status, rules));
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
        var supportedMajor = FrameworkRelease.MajorOf(version) == 8;
        var needsEditorIds = files.Where(f => f.Status == "active").SelectMany(f => f.Rules).Any(r =>
            r.Fields.Any(f => f.StartsWith("filterByEditorId", StringComparison.OrdinalIgnoreCase))
            || r.References.Any(reference => !reference.Identifier.Contains('|')));
        var editorIdCacheAvailable = po3Captured && inputs.EditorIdSupport == "available";
        var editorIdsSupported = !needsEditorIds || editorIdCacheAvailable;
        var statusOverall = uncertain ? "incomplete-capture" : dllHash.Length == 0 ? "missing-dll"
            : !enabled ? "disabled" : !editorIdsSupported ? "unsupported-editorid-dependency" : "input-contract-7.0";
        if (enabled && !editorIdsSupported)
            diagnostics.Add("SkyPatcher EditorID inputs require po3_Tweaks with [Fixes] Load EditorIDs=true; a missing DLL or disabled cache prevents distribution import.");
        if (enabled && needsEditorIds && editorIdsSupported && FrameworkRelease.MajorOf(editorIdVersion) != 1)
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
        return new(statusOverall, version, dllHash, FrameworkFiles.Hash(Encoding.UTF8.GetBytes(identity)),
            files.AsReadOnly(), diagnostics.AsReadOnly())
        {
            Information = diagnostics.Information,
            EditorIdCacheAvailable = editorIdCacheAvailable,
            CategorySwitches = categorySwitches,
            KeywordMembers = resolver.KeywordMembers(files.Where(f => f.Status == "active").SelectMany(f => f.Rules)
                .SelectMany(r => r.References).Where(r => r.Field.Equals("removeObjectsByKeyword", StringComparison.OrdinalIgnoreCase) && r.Status == "resolved")
                .Select(r => FormKey.Factory(r.FormKey!)).ToHashSet()),
            // po3's cache uses try_emplace: overrides retain the first nonempty
            // EditorID, even when the winning record has a different name.
            NativeListEditorIds = needsEditorIds ? resolver.Mods.SelectMany(m => m.LeveledItems)
                .Where(l => !string.IsNullOrEmpty(l.EditorID)).GroupBy(l => l.FormKey)
                .ToDictionary(g => g.Key, g => g.First().EditorID!).AsReadOnly()
                : new Dictionary<FormKey, string>()
        };
    }

    private static SkyPatcherInputReport Empty() => new("absent", "", "", "", Array.Empty<SkyPatcherFile>(), Array.Empty<string>());
    // The only SkyPatcher.ini reader. SkyPatcher gates each category on
    // GetPrivateProfileIntA("Patcher", "iEnable<Category>Patching", 0, ".\Data\SKSE\Plugins\SkyPatcher.ini")
    // (upstream Zzyxz/SkyPatcher main.cpp at 876662e5, not bound to the 8.0 DLL). Every rule below was measured
    // against that Windows call (review-fixes ticket 04): lines end at CR or LF; a header is a line starting with
    // '[' whose name runs to the first ']' or the line end; only the first [Patcher] section counts; a UTF-8
    // byte-order mark keeps the first line from being a header; keys, names and values are trimmed of characters
    // up to ' '; the first occurrence of a key counts. A switch line holding a byte that is not UTF-8, and a file
    // with NUL bytes but no UTF-16LE byte-order mark (null map), are listed in unverified.
    internal static IReadOnlyDictionary<string, bool>? CategorySwitches(byte[] bytes, List<string> unverified)
    {
        var start = bytes.AsSpan();
        var utf16 = start.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) && !start.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0, 0]);
        if (!utf16 && start.Contains((byte)0))
        {
            unverified.Add("the file holds NUL bytes without a UTF-16LE byte-order mark");
            return null;
        }
        var markedFirstLine = start.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]);
        var switches = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool inPatcher = false, seenPatcher = false, firstLine = true;
        foreach (var raw in FrameworkFiles.DecodeLenient(bytes).Split(['\r', '\n']))
        {
            var header = !(firstLine && markedFirstLine);
            firstLine = false;
            var line = TrimNative(raw);
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[')
            {
                if (!header) continue;
                var close = line.IndexOf(']');
                var patcher = TrimNative(line[1..(close < 0 ? line.Length : close)]).Equals("Patcher", StringComparison.OrdinalIgnoreCase);
                inPatcher = patcher && !seenPatcher;
                seenPatcher |= patcher;
                continue;
            }
            var split = line.IndexOf('=');
            if (!inPatcher || split < 0) continue;
            // The native key is ASCII, so a key holding a byte that is not UTF-8 never matches it.
            var key = TrimNative(line[..split]);
            if (key.Length < 15 || !key.StartsWith("iEnable", StringComparison.OrdinalIgnoreCase)
                || !key.EndsWith("Patching", StringComparison.OrdinalIgnoreCase)) continue;
            if (switches.TryAdd(key[7..^8], NativeIntegerNonZero(line[(split + 1)..])) && line.Contains('\uFFFD'))
                unverified.Add(key + " holds a byte that is not UTF-8");
        }
        return switches.AsReadOnly();
    }

    // The measured GetPrivateProfileIntA value: trimmed, one matching '' or "" pair removed, then leading
    // characters up to ' ' or above U+007F skipped (the bytes the native parser skips), one sign, a lowercase
    // 0x, 0o or 0b prefix, and digits of that base accumulated as an unchecked 32-bit integer up to the first
    // other character. An empty value returns the default, 0. Only zero versus nonzero matters.
    internal static bool NativeIntegerNonZero(string value)
    {
        var text = TrimNative(value).AsSpan();
        if (text.Length >= 2 && text[0] is '"' or '\'' && text[^1] == text[0]) text = text[1..^1];
        while (text.Length > 0 && (text[0] <= ' ' || text[0] > '\u007F')) text = text[1..];
        if (text.Length > 0 && text[0] is '+' or '-') text = text[1..];
        var radix = 10u;
        if (text.Length >= 2 && text[0] == '0' && text[1] is 'x' or 'o' or 'b')
        {
            radix = text[1] == 'x' ? 16u : text[1] == 'o' ? 8u : 2u;
            text = text[2..];
        }
        var total = 0u;
        foreach (var c in text)
        {
            var digit = c is >= '0' and <= '9' ? (uint)(c - '0') : c is >= 'a' and <= 'z' ? (uint)(c - 'a' + 10)
                : c is >= 'A' and <= 'Z' ? (uint)(c - 'A' + 10) : radix;
            if (digit >= radix) break;
            total = unchecked(total * radix + digit);
        }
        return total != 0;
    }

    private static string TrimNative(string text)
    {
        int first = 0, last = text.Length;
        while (first < last && text[first] <= ' ') first++;
        while (last > first && text[last - 1] <= ' ') last--;
        return text[first..last];
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
            var number = identifier[(separator + 1)..].Trim();
            if (number.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) number = number[2..];
            uint? id = uint.TryParse(number, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            // Full load-order IDs are normalized using the named loaded plugin,
            // never inferred from string length or a physically installed file.
            var (failure, located) = FormLocator.Locate(resolver, identifier[..separator].Trim(), id, FormIdRange.Mask);
            if (failure is not null) return Result(failure);
            record = located!;
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
