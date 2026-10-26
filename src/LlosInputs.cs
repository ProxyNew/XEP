using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public sealed record LlosReference(string Field, string Identifier, string Status, string? FormKey, int NativeType)
{
    // Records an ambiguous EditorID names: each may be the form the native reference binds.
    public IReadOnlyList<LlosCandidate> Candidates { get; init; } = [];
}
public sealed record LlosCandidate(string FormKey, int NativeType);
public sealed record LlosField(string Key, string Value, int Line, bool Used);
public sealed record LlosRule(int Index, string Section, int Line, string Status, string Domain, string Family,
    ushort Protocol, string Dispatch, IReadOnlyList<LlosField> Fields, IReadOnlyList<LlosReference> References,
    IReadOnlyList<string> Diagnostics)
{
    public ushort MinCount { get; init; } = 1;
    public ushort MaxCount { get; init; } = 1;
    public ushort MinLevel { get; init; } = 1;
    public ushort MaxLevel { get; init; } = 1;
    public float? Chance { get; init; } = 100;
    public byte UseAll { get; init; }
}
public sealed record LlosFile(string Path, string NativePath, string Sha256, string Status, IReadOnlyList<LlosRule> Rules);
public sealed record LlosInputReport(string Status, string Version, string DllSha256, string SourceRevision,
    string Fingerprint, IReadOnlyList<LlosFile> Files, IReadOnlyList<string> Diagnostics)
{
    public string DllFileName { get; init; } = "";
    internal IReadOnlyList<string> Information { get; init; } = [];
    internal IReadOnlyDictionary<string, ReadOnlyMemory<byte>> CapturedBytes { get; init; }
        = new Dictionary<string, ReadOnlyMemory<byte>>();
    // Plugin keyword membership for keyword-rule targets, in each record's keyword
    // order (native iterates a form's keywords in order).
    internal IReadOnlyDictionary<FormKey, FormKey[]> KeywordOrder { get; init; } = new Dictionary<FormKey, FormKey[]>();
    // Keyword-rule targets that a KID configuration may distribute at runtime.
    internal IReadOnlySet<FormKey> RuntimeKeywords { get; init; } = new HashSet<FormKey>();
}

// Input inventory bound to the pinned 1.3.0 source (LoadData, DirectProtocol and
// Utility parsing). No rule is distribution evidence yet: method tickets decide
// which dispatched operations XEP interprets and how they compose with output.
internal static class LlosInputs
{
    public const string Source = "c734ed54bb72fab6f71e21292aff742e382bdd8c";
    // Installed Nexus 1.3.0 build. Its PDB matches the DLL (RSDS GUID/age) and
    // records project sources matching the pinned tree; see the ticket's
    // release-verification-20261015.md. Other builds are accepted by major version.
    internal const string SourceMatchedDll = "9947D5AA6CC9BC61C2EDFDE0005C2BE62EB558C5032DA372D53EECA7E242E423";
    private static readonly string[] KnownKeys = ["Target", "Insert", "Protocol", "Count", "Level", "Chance", "UseAll"];

    public static LlosInputReport Capture(string? dataFolder, IReadOnlyList<ISkyrimModGetter> mods,
        Func<IReadOnlyDictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)>>? winners = null,
        NativeFormResolver? sharedResolver = null)
    {
        using var timing = PhaseTiming.Measure("llos.capture");
        if (string.IsNullOrWhiteSpace(dataFolder) || !Directory.Exists(dataFolder)) return Empty();
        var notes = new DiagnosticMessages();
        var bytes = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var incomplete = false;
        var plugins = Path.Combine(dataFolder, "SKSE", "Plugins");
        string? dll = null;
        var configs = new List<(string Physical, string Native, string Relative)>();
        using (PhaseTiming.Measure("llos.discovery"))
        try
        {
            // The DLL's name carries a load-order prefix (w_) in the release; any
            // prefix loads the same plugin, so match the suffix only.
            if (Directory.Exists(plugins))
                dll = Directory.EnumerateFiles(plugins, "*LeveledListObjectSwapper.dll").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            // Pinned LoadData: non-recursive Data\ and Data\LLOS, exact ".ini"
            // extension, case-sensitive "_LLOS" anywhere in the path string, then
            // a byte-ordinal sort of those strings across both folders.
            Discover(dataFolder, @"Data\", "");
            var llos = Path.Combine(dataFolder, "LLOS");
            if (Directory.Exists(llos)) Discover(llos, @"Data\LLOS\", "LLOS/");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            incomplete = true;
            notes.Add("LLOS discovery incomplete: " + ex.Message);
        }
        if (dll is null && configs.Count == 0) return Empty();

        var dllHash = "";
        var version = "";
        if (dll is not null)
            try
            {
                using var stream = File.OpenRead(dll);
                dllHash = Convert.ToHexString(SHA256.HashData(stream));
                version = SksePluginVersion.Read(dll) ?? "unknown";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                incomplete = true;
                notes.Add("Could not read the LLOS DLL: " + ex.Message);
            }

        var resolver = sharedResolver ?? new NativeFormResolver(mods, winners ?? (() => PatcherEngine.LoadOrderWinners(mods)));
        var editorIds = EditorIdSupport(plugins);
        var mergeMapper = File.Exists(Path.Combine(plugins, "MergeMapper.dll"));
        var files = new List<LlosFile>();
        var stopped = false;
        var index = 0;
        foreach (var (physical, native, relative) in configs.OrderBy(c => c.Native, StringComparer.Ordinal))
        {
            try
            {
                if ((File.GetAttributes(physical) & FileAttributes.Directory) != 0)
                {
                    // directory_iterator yields directories too; LoadFile then fails.
                    files.Add(new(relative, native, "", "not-a-file", []));
                    continue;
                }
                if ((File.GetAttributes(physical) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("File link was not followed: " + relative);
                var content = File.ReadAllBytes(physical);
                bytes.Add(relative, content);
                using var parsing = PhaseTiming.Measure("llos.parse");
                var rules = new List<LlosRule>();
                foreach (var section in Ini.Read(content))
                {
                    var rule = stopped ? Skipped(index, section) : ReadRule(index, section, resolver, editorIds, mergeMapper);
                    stopped |= rule.Status == "native-exception";
                    rules.Add(rule);
                    index++;
                }
                files.Add(new(relative, native, Hash(content), dll is null ? "missing-dll" : "active", rules.AsReadOnly()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                incomplete = true;
                files.Add(new(relative, native, "", "unreadable", []));
                notes.Add($"LLOS {relative}: {ex.Message}");
            }
        }

        var release = ReleaseStatus(version, dllHash);
        var status = incomplete ? "incomplete-capture" : dll is null ? "missing-dll" : release;
        notes.AddDiagnostic(dll is null
            ? "LLOS configuration was found without a projected LLOS DLL; nothing is applied natively and no LLOS distribution is imported."
            : release == "source-matched-1.3.0"
                ? $"LLOS {version}: DLL/PDB identities match the pinned {Source[..7]} source; captured statuses describe native input loading, not route admission."
                : release == "accepted-major-1"
                    ? $"LLOS {version}: same major version as the verified 1.3.0 release (DLL {dllHash}); its source contract applies."
                    : $"LLOS {version} differs in major version from the verified 1.3.0 release (DLL {dllHash}); its source contract is applied unverified.", informational: dll is not null && (release == "source-matched-1.3.0" || release == "accepted-major-1"));
        if (stopped)
            notes.Add("LLOS: a numeric field overflows the native std::stoull/std::stof conversion; LLOS 1.3.0 throws during LoadData, so no later section is loaded and game stability is uncertain.");
        var rulesAll = files.SelectMany(f => f.Rules).ToList();
        notes.AddRuleProblems("LLOS", rulesAll.Where(r => r.Status is not ("accepted" or "npc-out-of-scope")).Select(r => r.Status));
        var unresolved = rulesAll.SelectMany(r => r.References).Count(r => r.Status != "resolved" && r.Status != "absent");
        if (unresolved > 0)
            notes.Add($"LLOS: {unresolved} references do not resolve against the active load order; their sections are skipped natively and contribute nothing.");
        if (rulesAll.SelectMany(r => r.References).Any(r => r.Status == "editorid-cache-unavailable"))
            notes.Add("LLOS EditorID references require po3_Tweaks with [Fixes] Load EditorIDs=true; without it their native resolution is unverified and they are not imported.");
        var item = rulesAll.Where(r => r.Status == "accepted").ToList();
        if (item.Count > 0 && dll is not null)
            notes.AddInformation($"LLOS: {item.Count} accepted item leveled-list rules ({string.Join(", ", item.GroupBy(r => r.Family).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"))}) are captured; distribution analysis reports which it interprets and which lists it withholds.");
        notes.AddInformation($"LLOS captured {files.Count} configuration files and {rulesAll.Count} sections: " +
            string.Join(", ", rulesAll.GroupBy(r => r.Status).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}")) + ".");
        // Dependency state changes rule statuses, so it is part of the identity.
        // Keyword rules read plugin keyword membership, which KID can extend at
        // runtime; capture both so keyword routes are bounded or refused.
        // Every resolved keyword target counts, including rules XEP rejects only
        // because their Insert cannot be bound offline (they may still apply).
        var keywordTargets = rulesAll.Where(r => r.References.Count > 0 && r.References[0] is { Status: "resolved", NativeType: 8 })
            .Select(r => FormKey.Factory(r.References[0].FormKey!)).ToHashSet();
        var keywordOrder = resolver.OrderedKeywords(keywordTargets);
        var runtimeKeywords = new HashSet<FormKey>();
        var kidIdentity = "";
        if (keywordTargets.Count > 0)
            try
            {
                foreach (var kid in Directory.EnumerateFiles(dataFolder, "*_KID.ini").Order(StringComparer.OrdinalIgnoreCase))
                {
                    var content = File.ReadAllBytes(kid);
                    kidIdentity += $"\nkid={Path.GetFileName(kid)}|{Hash(content)}";
                    // KID: `<key> = <keyword>|<type>|...`; KID reads every value of the
                    // file whatever its key, and the first field names the distributed
                    // keyword by EditorID or 0xID~Plugin.
                    foreach (var raw in Encoding.UTF8.GetString(content).Split('\n'))
                    {
                        var line = raw.Trim().TrimStart('﻿');
                        var equals = line.IndexOf('=');
                        if (line.StartsWith(';') || line.StartsWith('#') || equals < 0) continue;
                        var distributed = line[(equals + 1)..].Split('|')[0].Trim();
                        var tilde = distributed.IndexOf('~');
                        foreach (var keyword in keywordTargets)
                        {
                            var matches = tilde > 0
                                ? resolver.TryGetPlugin(distributed[(tilde + 1)..].Trim(), out var plugin) && keyword.ModKey == plugin.ModKey
                                    && UnsignedLong(distributed[..tilde], 16, out _) == keyword.ID
                                : resolver.Winner(keyword)?.EditorID is { Length: > 0 } name && name.Equals(distributed, StringComparison.OrdinalIgnoreCase);
                            if (matches) runtimeKeywords.Add(keyword);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                runtimeKeywords.UnionWith(keywordTargets);
                notes.Add("LLOS keyword rules: KID configuration could not be read; keyword membership is unverified.");
            }
        if (runtimeKeywords.Count > 0)
            notes.Add($"LLOS keyword rules: {runtimeKeywords.Count} target keywords appear in KID configuration; their runtime membership is unanalysed.");
        var dllName = dll is null ? "" : Path.GetFileName(dll);
        var identity = $"llos-capture-v3\n{Source}\n{status}\n{version}\n{dllHash}\n{dllName}\neditorids={editorIds}\nmergemapper={mergeMapper}\n" +
            string.Join('\n', files.Select(f => $"{f.NativePath}|{f.Sha256}|{f.Status}")) + kidIdentity;
        return new(status, version, dllHash, Source, Hash(Encoding.UTF8.GetBytes(identity)), files.AsReadOnly(), notes.AsReadOnly())
        { DllFileName = dllName, Information = notes.Information, CapturedBytes = bytes.AsReadOnly(), KeywordOrder = keywordOrder, RuntimeKeywords = runtimeKeywords };

        void Discover(string directory, string prefix, string relativePrefix)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 && relativePrefix.Length > 0)
            {
                incomplete = true;
                notes.Add("LLOS configuration directory is a link; discovery was not followed.");
                return;
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(path);
                var native = prefix + name;
                if (NativeExtension(name) != ".ini" || !native.Contains("_LLOS", StringComparison.Ordinal)) continue;
                if (name.Any(c => c > 0x7F))
                    notes.Add($"LLOS {relativePrefix}{name}: non-ASCII file name; native ordering and path conversion are unverified.");
                configs.Add((path, native, relativePrefix + name));
            }
        }
    }

    // Framework version acceptance: the verified build silently, its major
    // version with a label, any other or unreadable major with a warning.
    internal static string ReleaseStatus(string version, string dllHash)
        => version == "1.3.0.0" && dllHash == SourceMatchedDll ? "source-matched-1.3.0"
            : SkyPatcherInputs.Major(version) == 1 ? "accepted-major-1" : "accepted-other-major";

    private static LlosInputReport Empty() => new("absent", "", "", Source, "", [], []);
    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    // std::filesystem::path::extension: from the last dot of the file name,
    // except a name whose only dot leads it (".ini" has no extension).
    internal static string NativeExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || name is "." or ".." ? "" : name[dot..];
    }

    // Shared with CID capture: both read po3_Tweaks' native EditorID cache.
    internal static string EditorIdSupport(string plugins)
    {
        try
        {
            var dll = Path.Combine(plugins, "po3_Tweaks.dll");
            var ini = Path.Combine(plugins, "po3_Tweaks.ini");
            if (!File.Exists(dll) || !File.Exists(ini)) return "unavailable";
            using var stream = File.OpenRead(dll);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return SkyPatcherInputs.EditorIdDependencySupported(hash, File.ReadAllText(ini)) ? "available" : "unavailable";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "unavailable"; }
    }

    private static LlosRule Skipped(int index, Ini.Section section)
        => new(index, section.Name, section.Line, "not-loaded-after-exception", "none", "none", 0, "none",
            section.Fields.Select(f => f with { Used = false }).ToList().AsReadOnly(), [], []);

    private static LlosRule ReadRule(int index, Ini.Section section, NativeFormResolver resolver, string editorIds, bool mergeMapper)
    {
        var notes = new List<string>();
        // CSimpleIni with multi-key: GetValue returns the first value of a key.
        var used = new HashSet<LlosField>(ReferenceEqualityComparer.Instance);
        string? Value(string key)
        {
            var field = section.Fields.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (field is null) return null;
            used.Add(field);
            if (section.Fields.Count(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) > 1)
                notes.Add($"Duplicate {key}: native uses the first value (line {field.Line}); later values are ignored.");
            return field.Value;
        }
        foreach (var key in section.Fields.Select(f => f.Key).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => !KnownKeys.Contains(k, StringComparer.OrdinalIgnoreCase)))
            notes.Add($"Unknown key {key} is ignored natively.");
        if (section.Merged) notes.Add("Section name repeats in this file; CSimpleIni merges the sections and the first value of each key wins.");

        var target = Reference("Target", Value("Target") ?? "", resolver, editorIds, mergeMapper);
        var insert = Reference("Insert", Value("Insert") ?? "", resolver, editorIds, mergeMapper);
        var protocolText = Value("Protocol") ?? "0";
        var countText = Value("Count") ?? "1";
        var levelText = Value("Level") ?? "1";
        var chanceText = Value("Chance") ?? "100.0";
        var useAllText = Value("UseAll") ?? "0";
        var fields = section.Fields.Select(f => f with { Used = used.Contains(f) }).ToList().AsReadOnly();
        var references = new[] { target, insert }.ToList().AsReadOnly();

        // LoadData evaluates the target first and skips an unresolved section before any numeric conversion, so a bad
        // number in a skipped section cannot throw. An ambiguous EditorID may bind natively to a candidate, so its numbers
        // are converted and validated exactly as for a resolved target.
        var ambiguous = target.Status == "ambiguous-editorid";
        if (target.NativeType == 0 && !ambiguous)
            return new(index, section.Name, section.Line, "invalid-target", "none", "none", 0, "skipped", fields, references, notes.AsReadOnly());
        var protocolValue = UnsignedInt(protocolText, 0, out var protocolOverflow);
        var (minCount, maxCount) = Range(countText);
        var (minLevel, maxLevel) = Range(levelText);
        var chance = Float(chanceText, 100f, out var chanceOverflow, out var chanceSyntax);
        var useAll = UnsignedInt(useAllText, 0, out var useAllOverflow);
        if (protocolOverflow || chanceOverflow || useAllOverflow)
            return new(index, section.Name, section.Line, "native-exception", "none", "none", 0, "load-aborted", fields, references, notes.AsReadOnly());
        if (target.NativeType == 0)
        {
            // Ambiguous target: its footprint is the candidates withheld by the authored protocol (LlosLists).
            return new(index, section.Name, section.Line, "invalid-target", "none", "none", (ushort)protocolValue, "skipped", fields, references, notes.AsReadOnly());
        }
        if (chanceSyntax) notes.Add($"Chance {chanceText} uses numeric syntax XEP does not evaluate; its native value is unknown.");
        var protocol = (ushort)protocolValue;
        if (protocol != protocolValue) notes.Add($"Protocol {protocolValue} wraps to {protocol} in the native 16-bit field.");
        if ((byte)useAll != useAll) notes.Add($"UseAll {useAll} wraps to {(byte)useAll} in the native 8-bit field.");
        if (chance is float c && float.IsNaN(c)) notes.Add("Chance is NaN; the native clamp keeps NaN.");
        var (dispatch, domain, family) = Dispatch(protocol, target.NativeType, insert.NativeType);
        var status = dispatch == "rejected" ? "rejected" : domain == "item" ? "accepted" : domain + "-out-of-scope";
        return new(index, section.Name, section.Line, status, domain, family, protocol, dispatch, fields, references, notes.AsReadOnly())
        {
            MinCount = minCount, MaxCount = maxCount, MinLevel = minLevel, MaxLevel = maxLevel,
            Chance = chance is null ? null : float.IsNaN(chance.Value) ? chance : Math.Clamp(chance.Value, 0f, 100f),
            UseAll = (byte)useAll
        };
    }

    // Utility::AcquireForm: "hex~Plugin" when a nonterminal tilde is present,
    // otherwise an EditorID. A failed FormID lookup falls back to an empty
    // EditorID and therefore fails.
    private static LlosReference Reference(string field, string identifier, NativeFormResolver resolver, string editorIds, bool mergeMapper)
    {
        LlosReference Result(string status, IMajorRecordGetter? record = null, IReadOnlyList<IMajorRecordGetter>? candidates = null)
            => new(field, identifier, status, record?.FormKey.ToString(), status == "resolved" ? NativeType(record!) : 0)
            {
                Candidates = (candidates ?? []).Select(c => new LlosCandidate(c.FormKey.ToString(), NativeType(c))).ToArray()
            };
        if (identifier.Length == 0) return Result("absent");
        IMajorRecordGetter? record;
        var tilde = identifier.IndexOf('~');
        if (tilde >= 0 && tilde + 1 < identifier.Length)
        {
            var id = UnsignedLong(identifier[..tilde], 16, out _);
            var name = identifier[(tilde + 1)..];
            if (!resolver.TryGetPlugin(name, out var plugin))
                return Result(mergeMapper ? "merge-mapping-unavailable" : "missing-plugin");
            // CommonLib composes the plugin's runtime prefix with the raw value;
            // a value outside the plugin's local range is not a stable identity.
            if (id > (plugin.IsSmallMaster ? 0xFFFu : 0xFFFFFFu)) return Result("outside-local-range");
            record = resolver.Winner(new FormKey(plugin.ModKey, id));
            if (record is null) return Result("missing-record");
        }
        else
        {
            if (editorIds != "available") return Result("editorid-cache-unavailable");
            var (status, found) = resolver.EditorId(identifier);
            if (found is null) return Result(status, candidates: status == "ambiguous-editorid" ? resolver.EditorIdMatches(identifier) : null);
            record = found;
        }
        if (record.IsDeleted) return Result("deleted-record");
        return NativeType(record) == 0 ? Result("unsupported-type") : Result("resolved", record);
    }

    // Utility::CheckFormType.
    internal static int NativeType(IMajorRecordGetter record) => record switch
    {
        IArmorGetter => 2,
        IWeaponGetter or IMiscItemGetter or IIngredientGetter or IIngestibleGetter or IBookGetter or IAmmunitionGetter
            or ISoulGemGetter or IScrollGetter or IKeyGetter or ILightGetter or IAlchemicalApparatusGetter => 1,
        ILeveledItemGetter => 3,
        INpcGetter => 4,
        ILeveledNpcGetter => 5,
        ISpellGetter => 6,
        ILeveledSpellGetter => 7,
        IKeywordGetter => 8,
        _ => 0
    };

    // Manager::DirectProtocol and its map insertion checks. Combined protocols
    // re-enter dispatch as 170, 450 and/or 750 on the same section.
    internal static (string Dispatch, string Domain, string Family) Dispatch(ushort protocol, int target, int insert)
    {
        static string DomainOf(int type) => type is >= 1 and <= 3 ? "item" : type is 4 or 5 ? "npc" : type is 6 or 7 ? "spell" : "none";
        static string Batch(ushort p) => p switch
        {
            <= 3 => "single-insert", >= 50 and <= 53 => "multi-insert", >= 100 and <= 103 => "single-swap", 104 => "single-remove",
            >= 150 and <= 153 => "multi-swap", 154 => "multi-remove", 170 => "generated-list-swap", _ => ""
        };
        var compatible = target is >= 1 and <= 3 && insert is >= 1 and <= 3
            || target is 4 or 5 && insert is 4 or 5 || target is 6 or 7 && insert is 6 or 7;
        if (target == 8 && insert is >= 1 and <= 7)
            return Batch(protocol) is { Length: > 0 } keyword ? ("keyword-" + (protocol == 170 ? "generated" : "batch"), DomainOf(insert), "keyword-" + keyword) : ("rejected", "none", "none");
        if (compatible)
        {
            var domain = DomainOf(target);
            if (Batch(protocol) is { Length: > 0 } batch) return (protocol == 170 ? "generated" : "batch", domain, batch);
            return protocol switch
            {
                200 or 300 or 350 => target is 3 or 5 or 7
                    ? ("focus", domain, protocol == 200 ? "direct-list-insert" : protocol == 300 ? "direct-list-single-remove" : "direct-list-multi-remove")
                    : ("rejected", "none", "none"),
                450 => target is 2 or 3 && insert is 2 or 3 ? ("outfit", "item", "outfit-generated-swap") : ("rejected", "none", "none"),
                750 => target is >= 1 and <= 3 && insert is >= 1 and <= 3 ? ("container", "item", "container-generated-swap") : ("rejected", "none", "none"),
                >= 1070 and <= 1073 => Combined(protocol, target, insert),
                _ => ("rejected", "none", "none")
            };
        }
        if (target is >= 1 and <= 7 && protocol is 104 or 154)
            return ("batch", DomainOf(target), protocol == 104 ? "single-remove" : "multi-remove");
        return ("rejected", "none", "none");
    }

    private static (string, string, string) Combined(ushort protocol, int target, int insert)
    {
        var parts = new List<ushort>();
        if (protocol is 1070 or 1071 or 1072) parts.Add(170);
        if (protocol is 1070 or 1071 or 1073) parts.Add(450);
        if (protocol is 1070 or 1072 or 1073) parts.Add(750);
        var accepted = parts.Select(p => (Protocol: p, Result: Dispatch(p, target, insert))).Where(p => p.Result.Dispatch != "rejected").ToList();
        if (accepted.Count == 0) return ("rejected", "none", "none");
        return ("combined:" + string.Join('+', accepted.Select(p => p.Protocol)), accepted[0].Result.Domain, "combined-generated-swap");
    }

    // std::stoull then truncation to unsigned int: leading space, optional sign,
    // decimal digits; no digits keeps the default; overflow throws.
    internal static uint UnsignedInt(string text, uint backup, out bool overflow)
    {
        overflow = false;
        var i = 0;
        while (i < text.Length && IsSpace(text[i])) i++;
        var negative = i < text.Length && text[i] == '-';
        if (i < text.Length && text[i] is '+' or '-') i++;
        var start = i;
        ulong value = 0;
        for (; i < text.Length && text[i] is >= '0' and <= '9'; i++)
        {
            var digit = (ulong)(text[i] - '0');
            if (value > (ulong.MaxValue - digit) / 10) { overflow = true; return backup; }
            value = value * 10 + digit;
        }
        if (i == start) return backup;
        return (uint)(negative ? unchecked(0 - value) : value);
    }

    // std::strtoul (32-bit unsigned long on Windows): never throws; overflow
    // saturates, a sign negates modulo 2^32 and no digits yield zero.
    internal static uint UnsignedLong(string text, int radix, out bool digits)
    {
        var i = 0;
        while (i < text.Length && IsSpace(text[i])) i++;
        var negative = i < text.Length && text[i] == '-';
        if (i < text.Length && text[i] is '+' or '-') i++;
        if (radix == 16 && i + 2 < text.Length && text[i] == '0' && text[i + 1] is 'x' or 'X' && Uri.IsHexDigit(text[i + 2])) i += 2;
        var start = i;
        ulong value = 0;
        var saturated = false;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            int digit = c is >= '0' and <= '9' ? c - '0' : radix == 16 && Uri.IsHexDigit(c) ? char.ToUpperInvariant(c) - 'A' + 10 : -1;
            if (digit < 0) break;
            value = value * (ulong)radix + (ulong)digit;
            if (value > uint.MaxValue) saturated = true;
        }
        digits = i > start;
        if (!digits) return 0;
        if (saturated) return uint.MaxValue;
        return negative ? unchecked((uint)(0 - (uint)value)) : (uint)value;
    }

    // Utility::AcquireRangeData: "a~b" (nonterminal tilde) or one value; each
    // is clamped through a signed cast to [1,16384]; empty keeps the default 1.
    internal static (ushort Min, ushort Max) Range(string text)
    {
        if (text.Length == 0) return (1, 1);
        static ushort Clamp(uint value) => (ushort)Math.Clamp(unchecked((int)value), 1, 16384);
        var tilde = text.IndexOf('~');
        if (tilde >= 0 && tilde + 1 < text.Length)
        {
            var a = UnsignedLong(text[..tilde], 10, out _);
            var b = UnsignedLong(text[(tilde + 1)..], 10, out _);
            return (Clamp(Math.Min(a, b)), Clamp(Math.Max(a, b)));
        }
        var value = Clamp(UnsignedLong(text, 10, out _));
        return (value, value);
    }

    // std::stof: decimal, inf and nan forms are evaluated; hexadecimal floats
    // are reported as unknown. Out-of-range values throw natively.
    internal static float? Float(string text, float backup, out bool overflow, out bool unknownSyntax)
    {
        overflow = unknownSyntax = false;
        var i = 0;
        while (i < text.Length && IsSpace(text[i])) i++;
        var body = text[i..];
        var sign = body.StartsWith('-') ? -1f : 1f;
        var unsigned = body.TrimStart('+', '-');
        if (body.Length - unsigned.Length > 1) return backup;
        if (unsigned.StartsWith("inf", StringComparison.OrdinalIgnoreCase)) return sign * float.PositiveInfinity;
        if (unsigned.StartsWith("nan", StringComparison.OrdinalIgnoreCase)) return float.NaN;
        if (unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { unknownSyntax = true; return null; }
        var match = System.Text.RegularExpressions.Regex.Match(unsigned, @"^([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?");
        if (!match.Success) return backup;
        var value = double.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (value > float.MaxValue || value != 0 && value < float.Epsilon) { overflow = true; return null; }
        return sign * (float)value;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    // CSimpleIni (SetUnicode, SetMultiKey, no multiline/quotes): ';'/'#' line
    // comments, "[name]" headers, "key=value" with trimmed whitespace, keys
    // before any header in the unnamed section, and case-insensitive section
    // names merged at their first appearance (GetAllSections LoadOrder).
    internal static class Ini
    {
        internal sealed record Section(string Name, int Line, IReadOnlyList<LlosField> Fields, bool Merged);

        public static IReadOnlyList<Section> Read(byte[] content)
        {
            var text = new UTF8Encoding(false, false).GetString(content);
            if (text.StartsWith('﻿')) text = text[1..];
            var order = new List<(string Name, int Line, List<LlosField> Fields, bool Merged)>();
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var current = -1;
            var lines = text.Split('\n');
            for (var n = 0; n < lines.Length; n++)
            {
                var line = lines[n].TrimEnd('\r').Trim(' ', '\t');
                if (line.Length == 0 || line[0] is ';' or '#') continue;
                if (line[0] == '[')
                {
                    var close = line.IndexOf(']');
                    if (close < 0) continue;
                    var name = line[1..close].Trim(' ', '\t');
                    current = Open(name, n + 1);
                    continue;
                }
                var split = line.IndexOf('=');
                if (split < 0) continue;
                var key = line[..split].TrimEnd(' ', '\t');
                if (key.Length == 0) continue;
                if (current < 0) current = Open("", n + 1);
                order[current].Fields.Add(new(key, line[(split + 1)..].Trim(' ', '\t'), n + 1, false));
            }
            return order.Select(s => new Section(s.Name, s.Line, s.Fields.AsReadOnly(), s.Merged)).ToList().AsReadOnly();

            int Open(string name, int line)
            {
                if (byName.TryGetValue(name, out var existing))
                {
                    order[existing] = order[existing] with { Merged = true };
                    return existing;
                }
                byName[name] = order.Count;
                order.Add((name, line, [], false));
                return order.Count - 1;
            }
        }
    }
}

// Reads the CommonLibSSE-NG SKSEPlugin_Version export: dataVersion, then the
// packed version major<<24 | minor<<16 | patch<<4 | build. LLOS ships no
// FileVersion resource, so this is the only embedded release number.
internal static class SksePluginVersion
{
    public static string? Read(string path)
    {
        try
        {
            var image = File.ReadAllBytes(path);
            uint U32(int at) => BitConverter.ToUInt32(image, at);
            var pe = (int)U32(0x3C);
            if (U32(pe) != 0x4550) return null;
            var sections = BitConverter.ToUInt16(image, pe + 6);
            var optional = pe + 24;
            var optionalSize = BitConverter.ToUInt16(image, pe + 20);
            var magic = BitConverter.ToUInt16(image, optional);
            var directories = optional + (magic == 0x20B ? 112 : 96);
            var exportRva = U32(directories);
            int Offset(uint rva)
            {
                var table = optional + optionalSize;
                for (var s = 0; s < sections; s++)
                {
                    var header = table + s * 40;
                    var virtualAddress = U32(header + 12);
                    var size = Math.Max(U32(header + 8), U32(header + 16));
                    if (rva >= virtualAddress && rva < virtualAddress + size) return (int)(rva - virtualAddress + U32(header + 20));
                }
                return -1;
            }
            var export = Offset(exportRva);
            if (exportRva == 0 || export < 0) return null;
            var names = (int)U32(export + 24);
            var functions = Offset(U32(export + 28));
            var nameTable = Offset(U32(export + 32));
            var ordinals = Offset(U32(export + 36));
            for (var i = 0; i < names; i++)
            {
                var nameAt = Offset(U32(nameTable + i * 4));
                var end = Array.IndexOf(image, (byte)0, nameAt);
                if (Encoding.ASCII.GetString(image, nameAt, end - nameAt) != "SKSEPlugin_Version") continue;
                var ordinal = BitConverter.ToUInt16(image, ordinals + i * 2);
                var data = Offset(U32(functions + ordinal * 4));
                if (data < 0) return null;
                var packed = U32(data + 4);
                return $"{packed >> 24}.{(packed >> 16) & 0xFF}.{(packed >> 4) & 0xFFF}.{packed & 0xF}";
            }
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or IOException) { return null; }
    }
}
