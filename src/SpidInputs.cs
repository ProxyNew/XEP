using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

// One resolved (or refused) identifier of a SPID line. Status values: resolved,
// resolved-unmatchable, plugin-filter, plugin-only, created-keyword, missing-plugin,
// missing-record, unknown-editorid, ambiguous-editorid, editorid-cache-unavailable,
// malformed-editorid, invalid-keyword, wrong-type, unsupported-type, not-whitelisted,
// bare-formid-unverified, merge-mapping-unavailable, outside-local-range, deleted-record.
// ReferenceStatus.Classify groups them as resolved, failed or unverified.
public sealed record SpidReference(string Field, string Identifier, string Status, string? FormKey, string? RecordType);
public sealed record SpidSkillRange(int Skill, byte Min, byte Max);
public sealed record SpidStringFilters(IReadOnlyList<string> Match, IReadOnlyList<string> Not, IReadOnlyList<string> All, IReadOnlyList<string> Any);
public sealed record SpidFormFilters(IReadOnlyList<string> Match, IReadOnlyList<string> Not, IReadOnlyList<string> All);
// Actor level: MAX is the native default when a single value is given ("at least").
public sealed record SpidLevels(ushort? ActorMin, ushort? ActorMax, IReadOnlyList<SpidSkillRange> Skills, IReadOnlyList<SpidSkillRange> Weights);
public sealed record SpidTraits(string? Sex, bool? Unique, bool? Summonable, bool? Child, bool? Leveled, bool? Teammate, bool? StartsDead);

public sealed record SpidRule(int Index, int Line, string Key, string RawValue, string SanitizedValue, string Parser, string Type,
    bool Final, string Family, string Status, IReadOnlyList<SpidReference> References, IReadOnlyList<string> Diagnostics)
{
    public SpidStringFilters? StringFilters { get; init; }
    public SpidFormFilters? FormFilters { get; init; }
    public SpidLevels? Levels { get; init; }
    public SpidTraits? Traits { get; init; }
    public int CountMin { get; init; } = 1;
    public int CountMax { get; init; } = 1;
    public int? PackageIndex { get; init; }
    // Native std::stod accepts inf and nan, so the report must serialize them.
    [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    public double ChancePercent { get; init; } = 100;
    public bool ChanceDeterministic { get; init; }
    // Linked rules: "local" (default) or "global".
    public string? Scope { get; init; }
    // Exclusive-group rules: the group name.
    public string? Group { get; init; }
}

public sealed record SpidFile(string Path, string NativePath, string Sha256, string Status, IReadOnlyList<SpidRule> Rules,
    int SanitizedRewrites, int IgnoredSectionEntries);

public sealed record SpidInputReport(string Status, string Version, string DllSha256, string SourceRevision,
    string Fingerprint, IReadOnlyList<SpidFile> Files, IReadOnlyList<string> Diagnostics)
{
    internal IReadOnlyList<string> Information { get; init; } = [];
    // po3_Tweaks native EditorID cache support ("available" when NPC EditorIDs are visible to SPID).
    public string EditorIds { get; init; } = "";
    // Keyword Item Distributor is projected: it adds keywords XEP does not capture, so keyword
    // facts an NPC lacks in the plugins may still be present natively.
    public bool KeywordDistributor { get; init; }
    internal IReadOnlyDictionary<string, ReadOnlyMemory<byte>> CapturedBytes { get; init; }
        = new Dictionary<string, ReadOnlyMemory<byte>>();
}

// Input inventory bound to the pinned SPID 7.3.2 source (LookupConfigs, Parser,
// FormData, LookupForms, ExclusiveGroups, LinkedDistribution, DeathDistribution).
// No rule is distribution evidence yet: method tickets decide which parsed
// operations XEP interprets. Filter results are not evaluated here.
internal static class SpidInputs
{
    public const string Source = "981314a56b4e2719c61874596b492b67f51be636";
    // Installed AE build (SKSEPlugin_Version export). The export carries only the
    // major version, so the code-visible version is 7.0.0.0; the hash binds the
    // exact DLL whose PDB matches the 7.3.2 tag (see spid-732-contract.md).
    private const string SourceMatchedDll = "C784948134179FEA6EF9BD019AD0150CB7B2A830B27234E702B3B199C4AAB162";
    private const string SourceMatchedVersion = "7.0.0.0";

    private static readonly string[] TypeNames =
        ["Form", "Spell", "Perk", "Item", "Shout", "LevSpell", "Package", "Outfit", "Keyword", "Faction", "SleepOutfit", "Skin"];
    // Form types a MATCH or NOT form filter may name (Cache.h whitelist).
    private static readonly HashSet<string> Whitelist =
        ["Faction", "Class", "CombatStyle", "Race", "Outfit", "NPC", "VoiceType", "FormList", "Spell", "Armor", "Location", "Perk", "ActorCharacter"];

    public static SpidInputReport Capture(string? dataFolder, IReadOnlyList<ISkyrimModGetter> mods,
        Func<IReadOnlyDictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)>>? winners = null,
        NativeFormResolver? sharedResolver = null)
    {
        using var timing = PhaseTiming.Measure("spid.capture");
        if (string.IsNullOrWhiteSpace(dataFolder) || !Directory.Exists(dataFolder)) return Empty();
        var notes = new DiagnosticMessages();
        var bytes = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var incomplete = false;
        var plugins = Path.Combine(dataFolder, "SKSE", "Plugins");
        string? dll = null;
        var configs = new List<(string Physical, string Native, string Relative)>();
        using (PhaseTiming.Measure("spid.discovery"))
        try
        {
            if (Directory.Exists(plugins))
                dll = Directory.EnumerateFiles(plugins, "*SpellPerkItemDistributor.dll").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            // Pinned distribution::get_configs: non-recursive Data\, exact ".ini",
            // case-sensitive "_DISTR" anywhere in the path string, ordinal sort.
            // Directory entries are returned too; LoadFile then fails.
            foreach (var path in Directory.EnumerateFileSystemEntries(dataFolder))
            {
                var name = Path.GetFileName(path);
                var native = @"Data\" + name;
                if (LlosInputs.NativeExtension(name) != ".ini" || !native.Contains("_DISTR", StringComparison.Ordinal)) continue;
                if (name.Any(c => c > 0x7F))
                    notes.Add($"SPID {name}: non-ASCII file name; native ordering and path conversion are unverified.");
                configs.Add((path, native, name));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            incomplete = true;
            notes.Add("SPID discovery incomplete: " + ex.Message);
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
                notes.Add("Could not read the SPID DLL: " + ex.Message);
            }

        var resolver = sharedResolver ?? new NativeFormResolver(mods, winners ?? (() => PatcherEngine.LoadOrderWinners(mods)));
        var lookup = new Lookup(resolver, mods, LlosInputs.EditorIdSupport(plugins), File.Exists(Path.Combine(plugins, "MergeMapper.dll")));
        var captured = new List<(string Path, string Native, string Sha, string Status, List<SpidRule> Rules, int Rewrites, int Ignored)>();
        var index = 0;
        var rewritten = 0;
        foreach (var (physical, native, relative) in configs.OrderBy(c => c.Native, StringComparer.Ordinal))
        {
            try
            {
                if ((File.GetAttributes(physical) & FileAttributes.Directory) != 0)
                {
                    captured.Add((relative, native, "", "not-a-file", new List<SpidRule>(), 0, 0));
                    continue;
                }
                if ((File.GetAttributes(physical) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("File link was not followed: " + relative);
                var content = File.ReadAllBytes(physical);
                bytes.Add(relative, content);
                using var parsing = PhaseTiming.Measure("spid.parse");
                var sections = LlosInputs.Ini.Read(content);
                // GetSection(""): only keys before the first header are read.
                var unnamed = sections.FirstOrDefault(s => s.Name.Length == 0);
                var ignored = sections.Where(s => s.Name.Length > 0).Sum(s => s.Fields.Count);
                if (ignored > 0)
                    notes.AddInformation($"SPID {relative}: {ignored} entries under named sections are ignored natively (only the unnamed section is read).");
                var rules = new List<SpidRule>();
                var fileRewrites = 0;
                if (unnamed is not null)
                {
                    if (unnamed.Fields.Any(f => f.Key.Any(c => c > 0x7F)))
                        notes.Add($"SPID {relative}: non-ASCII keys; native key order is unverified.");
                    foreach (var field in unnamed.Fields.Select((f, i) => (Field: f, Order: i))
                        .OrderBy(p => p.Field.Key, KeyOrder.Instance).ThenBy(p => p.Order).Select(p => p.Field))
                    {
                        using (PhaseTiming.Measure("spid.resolve"))
                        {
                            var rule = BuildRule(index, field, lookup, out var candidate);
                            if (candidate) fileRewrites++;
                            rules.Add(rule);
                        }
                        index++;
                    }
                }
                rewritten += fileRewrites;
                captured.Add((relative, native, Hash(content), dll is null ? "missing-dll" : "active", rules, fileRewrites, ignored));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                incomplete = true;
                captured.Add((relative, native, "", "unreadable", new List<SpidRule>(), 0, 0));
                notes.Add($"SPID {relative}: {ex.Message}");
            }
        }

        var grouped = ApplyExclusiveGroups(ApplyCreatedKeywords(captured.SelectMany(c => c.Rules).ToList()), notes);
        var files = new List<SpidFile>();
        var cursor = 0;
        foreach (var c in captured)
        {
            var list = grouped.GetRange(cursor, c.Rules.Count);
            cursor += c.Rules.Count;
            files.Add(new SpidFile(c.Path, c.Native, c.Sha, c.Status, list.AsReadOnly(), c.Rewrites, c.Ignored));
        }

        var release = ReleaseStatus(version, dllHash);
        var status = incomplete ? "incomplete-capture" : dll is null ? "missing-dll" : release;
        notes.AddDiagnostic(dll is null
            ? "SPID configuration was found without a projected SPID DLL; nothing is applied natively and no SPID distribution is imported."
            : release == "source-matched-7.3.2"
                ? $"SPID {version} (DLL {dllHash[..12]}...): the DLL matches the 7.3.2 tag by PDB; captured statuses describe native input loading, not distribution."
                : release == "accepted-major-7"
                    ? $"SPID {version}: same major version as the verified 7.3.2 release (DLL {dllHash}); its source contract applies."
                    : $"SPID {version} differs in major version from the verified 7.3.2 release (DLL {dllHash}); its source contract is applied unverified.", informational: dll is not null && (release == "source-matched-7.3.2" || release == "accepted-major-7"));
        if (dll is not null && configs.Count == 0)
            notes.AddInformation("SPID found no _DISTR configuration in Data; it does nothing.");
        if (rewritten > 0)
            notes.AddInformation($"SPID rewrites {rewritten} default-parser lines in its configuration files at game start (sanitizer); the captured sanitized values are what SPID parses.");
        var all = files.SelectMany(f => f.Rules).ToList();
        notes.AddRuleProblems("SPID", all.Where(r => r.Status is not ("accepted" or "out-of-scope")).Select(r => r.Status));
        if (all.Any(r => r.References.Any(x => x.Status == "bare-formid-unverified")))
            notes.Add("SPID: bare FormIDs were mapped through the supplied load order; that mapping is unverified against the runtime load order.");
        if (all.Any(r => r.References.Any(x => x.Status == "created-keyword")))
            notes.AddInformation("SPID creates keywords for unknown EditorIDs on generic Form lines and Keyword lines; those created keywords are reported per reference.");
        if (all.Any(r => r.References.Any(x => x.Status == "editorid-cache-unavailable")))
            notes.Add("SPID EditorID references require po3_Tweaks with [Fixes] Load EditorIDs=true; without it their native resolution is unverified and they are not captured as resolved.");
        var refsAll = all.SelectMany(r => r.References).ToList();
        var unresolved = refsAll.Count(r => ReferenceStatus.Classify(r.Status) == ReferenceOutcome.Failed);
        if (unresolved > 0)
            notes.Add($"SPID: {unresolved} references definitely fail against the active load order; their lines or filter entries are dropped natively.");
        var unverifiedRefs = refsAll.Count(r => ReferenceStatus.Classify(r.Status) == ReferenceOutcome.Unverified);
        if (unverifiedRefs > 0)
            notes.Add($"SPID: {unverifiedRefs} references are unverified (ambiguous EditorIDs, unavailable EditorID cache, merge mapping or bare FormIDs); their lines are not interpreted as resolved or dropped.");
        var equipment = all.Where(r => r.Status == "accepted").ToList();
        if (equipment.Count > 0 && dll is not null)
            notes.AddInformation($"SPID: {equipment.Count} accepted rules ({string.Join(", ", equipment.GroupBy(r => r.Family).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"))}) are captured; item, death-item and outfit rules that can reach equipment are modelled as NPC consumers where their filters are evaluable, others are diagnosed per rule (see the SPID route notes).");
        notes.AddInformation("SPID runtime facts: items and death items are added to the base NPC and shared by its references; outfits are actor-level and saved in SPID's co-save, sleeping outfits are set on the base NPC; keywords and factions SPID itself distributes reach later filters in native order.");
        notes.AddInformation($"SPID captured {files.Count} configuration files and {all.Count} entries: " +
            string.Join(", ", all.GroupBy(r => r.Status).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}")) + ".");
        var identity = $"spid-capture-v1\n{Source}\n{status}\n{version}\n{dllHash}\n{lookup.EditorIds}\nmerge-mapper={lookup.MergeMapper}\n" +
            string.Join('\n', files.Select(f => $"{f.NativePath}|{f.Sha256}|{f.Status}")) +
            // Resolution depends on the load order as well as the configuration bytes.
            "\nrefs=" + Hash(Encoding.UTF8.GetBytes(string.Join('\n', all.SelectMany(r => r.References.Select(x =>
                $"{r.Index}|{x.Field}|{x.Identifier}|{x.Status}|{x.FormKey}")))));
        return new(status, version, dllHash, Source, Hash(Encoding.UTF8.GetBytes(identity)), files.AsReadOnly(), notes.AsReadOnly())
        { Information = notes.Information, CapturedBytes = bytes.AsReadOnly(), EditorIds = lookup.EditorIds,
          KeywordDistributor = Directory.Exists(plugins) && Directory.EnumerateFiles(plugins, "*KeywordItemDistributor*.dll").Any() };
    }

    // Framework version acceptance: the verified AE build silently, its major
    // version 7 with a label, any other or unreadable version with a warning.
    internal static string ReleaseStatus(string version, string dllHash)
        => version == SourceMatchedVersion && dllHash == SourceMatchedDll ? "source-matched-7.3.2"
            : SkyPatcherInputs.Major(version) == 7 ? "accepted-major-7" : "accepted-other-major";

    private static SpidInputReport Empty() => new("absent", "", "", Source, "", [], []);
    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    // detail::sanitize (non-VR), in pinned order.
    internal static string Sanitize(string value)
    {
        var text = value;
        if (!text.Contains('~'))
        {
            var at = text.IndexOf(" - ", StringComparison.Ordinal);
            if (at >= 0) text = text[..at] + "~" + text[(at + 3)..];
        }
        text = SanitizeBar.Replace(text, "|");
        text = SanitizeComma.Replace(text, ",");
        text = SanitizeFormId.Replace(text, "0x$1");
        text = SanitizeZeros.Replace(text, "0x$2");
        return text;
    }

    private static readonly Regex SanitizeBar = new(@"\s*\|\s*", RegexOptions.ECMAScript);
    private static readonly Regex SanitizeComma = new(@"\s*,\s*", RegexOptions.ECMAScript);
    private static readonly Regex SanitizeFormId = new(@"\b00+([0-9a-fA-F]{1,6})\b", RegexOptions.ECMAScript);
    private static readonly Regex SanitizeZeros = new(@"(0x00+)([0-9a-fA-F]+)", RegexOptions.ECMAScript);

    // Routing order of GetConfigs: exclusive group, linked, death, default.
    private static SpidRule BuildRule(int index, LlosField field, Lookup lookup, out bool candidate)
    {
        var sanitized = Sanitize(field.Value);
        candidate = false;
        if (field.Key == "ExclusiveGroup")
            return BuildExclusive(index, field, sanitized, lookup);
        if (TryLinkedRoute(field.Key, out var linkedType, out var linkedFinal, out var linkedScope, out var linkedDeath))
            return BuildLinked(index, field, sanitized, lookup, linkedType ?? "", linkedFinal, linkedScope, linkedDeath);
        if (TryDeathRoute(field.Key, out var deathType, out var deathFinal))
            return BuildDefault(index, field, sanitized, lookup, deathType ?? "", deathFinal, true);
        candidate = sanitized != field.Value;
        var (type, final) = DefaultKey(field.Key, out var error);
        return error is null
            ? BuildDefault(index, field, sanitized, lookup, type!, final, false)
            : Dropped(index, field, sanitized, "default", "", error.Status, error.Message);
    }

    private static (string? Type, bool Final) DefaultKey(string key, out SpidError? error)
    {
        error = null;
        var final = key.StartsWith("Final", StringComparison.Ordinal);
        var name = final ? key[5..] : key;
        if (!TypeNames.Contains(name, StringComparer.Ordinal))
        {
            error = new SpidError("unsupported-key", $"Unsupported form type {name}");
            return (null, final);
        }
        return (name, final);
    }

    private static bool TryDeathRoute(string key, out string? type, out bool final)
    {
        type = null;
        final = key.StartsWith("Final", StringComparison.Ordinal);
        var rest = final ? key[5..] : key;
        if (!rest.StartsWith("Death", StringComparison.Ordinal)) return false;
        rest = rest[5..];
        type = TypeNames.Contains(rest, StringComparer.Ordinal) ? rest : "";
        return true;
    }

    private static bool TryLinkedRoute(string key, out string? type, out bool final, out string scope, out bool death)
    {
        type = null; final = false; death = false; scope = "local";
        var rest = key;
        if (rest.StartsWith("Global", StringComparison.Ordinal)) { scope = "global"; rest = rest[6..]; }
        if (!rest.StartsWith("Linked", StringComparison.Ordinal)) return false;
        rest = rest[6..];
        if (rest.StartsWith("Final", StringComparison.Ordinal)) { final = true; rest = rest[5..]; }
        if (rest.StartsWith("Death", StringComparison.Ordinal)) { death = true; rest = rest[5..]; }
        type = TypeNames.Contains(rest, StringComparer.Ordinal) ? rest : "";
        return true;
    }

    // Parse<Data, DefaultKey, Distributable, Strings, FormFilters, Levels, Traits, IndexOrCount, Chance>.
    // Typed lines outside the equipment scope are parsed but not resolved; generic
    // Form lines are resolved first because the record decides their family.
    private static SpidRule BuildDefault(int index, LlosField field, string sanitized, Lookup lookup, string type, bool final, bool death)
    {
        var parser = death ? "death" : "default";
        var prefix = death ? "death-" : "";
        if (type.Length == 0)
            return Dropped(index, field, sanitized, parser, "", "unsupported-key", $"Unsupported form type after Death in {field.Key}");
        try
        {
            var sections = SplitSections(sanitized, 7, out _);
            var form = Distributable(sections[0]);
            var strings = StringFilters(sections[1]);
            var forms = FormFilters(sections[2], combine: true, exclusion: true, required: false)!;
            var levels = Levels(sections[3]);
            var traits = Traits(sections[4]);
            var count = CountOf(sections[5], type);
            var (chance, deterministic) = Chance(sections[6]);
            var diagnostics = new List<string>();
            if (final && type != "Outfit")
            {
                final = false;
                diagnostics.Add("Final modifier can only be applied to Outfits; the flag is cleared.");
            }
            var references = new List<SpidReference>();
            var family = prefix + FamilyOf(type);
            IMajorRecordGetter? found = null;
            if (type == "Form" || InScope(family))
            {
                var (distributable, record) = ResolveDistributable(form, type, lookup, "Form");
                references.Add(distributable);
                found = record;
                if (type == "Form")
                    family = prefix + GenericFamily(distributable.Status, record);
            }
            if (type == "Form" && found is null && references[0].Status != "created-keyword")
                return Make(index, field, sanitized, parser, type, final, family, Unresolved(references[0].Status), references, diagnostics, count, form, strings, forms, levels, traits, chance, deterministic, null);
            if (!InScope(family))
                return Make(index, field, sanitized, parser, type, final, family, "out-of-scope", references, diagnostics, count, form, strings, forms, levels, traits, chance, deterministic, null);

            var status = "accepted";
            var refStatus = references[0].Status;
            if (refStatus is "wrong-type" or "unsupported-type") status = "wrong-type";
            else if (refStatus is not ("resolved" or "created-keyword")) status = Unresolved(refStatus);
            if (refStatus == "created-keyword") diagnostics.Add("Unknown EditorID creates a keyword with that name.");
            if (refStatus == "plugin-only") diagnostics.Add("A plugin name is not a distributable form.");

            var matchRefs = forms.Match.Select(t => FilterReference("Match", t, lookup, all: false, whitelist: true)).ToList();
            var notRefs = forms.Not.Select(t => FilterReference("Not", t, lookup, all: false, whitelist: true)).ToList();
            var allRefs = forms.All.Select(t => FilterReference("All", t, lookup, all: true, whitelist: true)).ToList();
            references.AddRange(matchRefs.Select(x => x.Ref));
            references.AddRange(notRefs.Select(x => x.Ref));
            references.AddRange(allRefs.Select(x => x.Ref));
            var filtersFailed = FiltersDefinitelyFail(matchRefs, notRefs, allRefs);
            var unverifiedFilters = FilterOutcomes(matchRefs, notRefs, allRefs, ReferenceOutcome.Unverified).Any();
            foreach (var failed in FilterOutcomes(matchRefs, notRefs, allRefs, ReferenceOutcome.Failed))
                diagnostics.Add($"Filter {failed.Identifier} ({failed.Status}) is dropped natively.");
            foreach (var unverified in FilterOutcomes(matchRefs, notRefs, allRefs, ReferenceOutcome.Unverified))
                diagnostics.Add($"Filter {unverified.Identifier} ({unverified.Status}) is unverified; it stays in its filter and its native outcome is not established.");
            if (status == "accepted" && filtersFailed) status = "filters-unresolved";
            else if (status == "accepted" && unverifiedFilters) status = "unverified-filter";

            return Make(index, field, sanitized, parser, type, final, family, status, references, diagnostics, count, form, strings, forms, levels, traits, chance, deterministic, null);
        }
        catch (SpidError error)
        {
            return Dropped(index, field, sanitized, parser, type, error.Status, error.Message);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return Dropped(index, field, sanitized, parser, type, "malformed", ex.Message);
        }
    }

    // Native MATCH/NOT lists drop a definitely failed entry and the line only when every entry of the
    // list failed; ALL drops the line on any definite failure. Unverified entries never cause a drop.
    private static bool FiltersDefinitelyFail(List<(SpidReference Ref, ReferenceOutcome Outcome)> match,
        List<(SpidReference Ref, ReferenceOutcome Outcome)> not, List<(SpidReference Ref, ReferenceOutcome Outcome)> all)
        => (match.Count > 0 && match.All(x => x.Outcome == ReferenceOutcome.Failed))
           || (not.Count > 0 && not.All(x => x.Outcome == ReferenceOutcome.Failed))
           || all.Any(x => x.Outcome == ReferenceOutcome.Failed);

    private static IEnumerable<SpidReference> FilterOutcomes(List<(SpidReference Ref, ReferenceOutcome Outcome)> match,
        List<(SpidReference Ref, ReferenceOutcome Outcome)> not, List<(SpidReference Ref, ReferenceOutcome Outcome)> all, ReferenceOutcome outcome)
        => match.Concat(not).Concat(all).Where(x => x.Outcome == outcome).Select(x => x.Ref);

    // A reference that cannot be decided keeps its rule's status off "accepted" without calling it failed.
    private static string Unresolved(string refStatus)
        => ReferenceStatus.Classify(refStatus) == ReferenceOutcome.Unverified ? "unverified-reference" : "unresolved";

    // Parse<RawLinkedForm, Linked, Distributable, FormFilters<kRequired>, IndexOrCount, Chance>.
    // Parent filters are MATCH only with no type whitelist; scope is "local" or "global".
    private static SpidRule BuildLinked(int index, LlosField field, string sanitized, Lookup lookup, string type, bool final, string scope, bool death)
    {
        const string parser = "linked";
        var prefix = "linked-" + (death ? "death-" : "");
        if (type.Length == 0)
            return Dropped(index, field, sanitized, parser, "", "unsupported-key", $"Unsupported linked form type in {field.Key}", scope);
        try
        {
            var sections = SplitSections(sanitized, 4, out _);
            var form = Distributable(sections[0]);
            var parents = FormFilters(sections[1], combine: false, exclusion: false, required: true)!;
            var count = CountOf(sections[2], type);
            var (chance, deterministic) = Chance(sections[3]);
            var diagnostics = new List<string>();
            if (final && type != "Outfit")
            {
                final = false;
                diagnostics.Add("Final modifier can only be applied to Outfits; the flag is cleared.");
            }
            var references = new List<SpidReference>();
            var family = prefix + FamilyOf(type);
            IMajorRecordGetter? found = null;
            if (type == "Form" || InScope(family))
            {
                var (distributable, record) = ResolveDistributable(form, type, lookup, "Form");
                references.Add(distributable);
                found = record;
                if (type == "Form") family = prefix + GenericFamily(distributable.Status, record);
            }
            if (type == "Form" && found is null && references[0].Status != "created-keyword")
                return Make(index, field, sanitized, parser, type, final, family, Unresolved(references[0].Status), references, diagnostics, count, form, null, parents, null, null, chance, deterministic, scope);
            if (!InScope(family))
                return Make(index, field, sanitized, parser, type, final, family, "out-of-scope", references, diagnostics, count, form, null, parents, null, null, chance, deterministic, scope);

            var status = references[0].Status is "resolved" or "created-keyword" ? "accepted"
                : references[0].Status is "wrong-type" or "unsupported-type" ? "wrong-type" : Unresolved(references[0].Status);
            var parentRefs = parents.Match.Select(t => FilterReference("Parent", t, lookup, all: false, whitelist: false)).ToList();
            references.AddRange(parentRefs.Select(x => x.Ref));
            if (status == "accepted" && FiltersDefinitelyFail(parentRefs, [], []))
                status = "filters-unresolved";
            else if (status == "accepted" && FilterOutcomes(parentRefs, [], [], ReferenceOutcome.Unverified).Any())
                status = "unverified-filter";
            foreach (var unverified in FilterOutcomes(parentRefs, [], [], ReferenceOutcome.Unverified))
                diagnostics.Add($"Parent {unverified.Identifier} ({unverified.Status}) is unverified; it stays in the parent filter.");
            return Make(index, field, sanitized, parser, type, final, family, status, references, diagnostics, count, form, null, parents, null, null, chance, deterministic, scope);
        }
        catch (SpidError error)
        {
            return Dropped(index, field, sanitized, parser, type, error.Status, error.Message, scope);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return Dropped(index, field, sanitized, parser, type, "malformed", ex.Message, scope);
        }
    }

    // Parse<RawExclusiveGroup, ExclusiveGroup, Name, FormFilters<kRequired|kAllowExclusionModifier>>.
    // The native parser accepts two sections (Name|Forms); a third section is refused.
    private static SpidRule BuildExclusive(int index, LlosField field, string sanitized, Lookup lookup)
    {
        const string parser = "exclusive-group";
        const string family = "exclusive-group";
        try
        {
            var sections = SplitSections(sanitized, 2, out _);
            var name = sections[0];
            var forms = FormFilters(sections[1], combine: false, exclusion: true, required: true)!;
            var references = new List<SpidReference>();
            references.AddRange(forms.Match.Select(t => FilterReference("Match", t, lookup, all: false, whitelist: false)).Select(x => x.Ref));
            references.AddRange(forms.Not.Select(t => FilterReference("Not", t, lookup, all: false, whitelist: false)).Select(x => x.Ref));
            return Make(index, field, sanitized, parser, "", false, family, "pending", references, [], (1, 1, null), null, null, forms, null, null, 100, false, null, name);
        }
        catch (SpidError error)
        {
            return Dropped(index, field, sanitized, parser, "", error.Status, error.Message);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return Dropped(index, field, sanitized, parser, "", "malformed", ex.Message);
        }
    }

    // Keywords SPID creates for unknown EditorIDs join the keyword array, so later lookups find them.
    // Distributables (default and death lines) are looked up before linked forms, and both before exclusive
    // groups, so linked parents see keywords created by distributables and groups see all of them. A keyword
    // created by another linked line, or used by a default ALL filter, depends on a per-type order that is not
    // established here. MATCH/NOT never accept keywords (not whitelisted), so those entries stay failed.
    private static List<SpidRule> ApplyCreatedKeywords(List<SpidRule> rules)
    {
        HashSet<string> Created(bool linked) => rules.Where(r => (r.Parser == "linked") == linked)
            .SelectMany(r => r.References).Where(x => x.Status == "created-keyword")
            .Select(x => x.Identifier).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var early = Created(linked: false);
        var late = Created(linked: true);
        if (early.Count + late.Count == 0) return rules;
        string? Upgrade(SpidRule rule, SpidReference x)
        {
            if (x.Status != "unknown-editorid") return null;
            var known = early.Contains(x.Identifier) || late.Contains(x.Identifier);
            if (!known) return null;
            return rule.Parser switch
            {
                "exclusive-group" => "created-keyword",
                "linked" when x.Field == "Parent" => early.Contains(x.Identifier) ? "created-keyword" : "created-keyword-order-unverified",
                _ when x.Field == "All" => "created-keyword-order-unverified",
                _ => null
            };
        }
        return rules.Select(rule =>
        {
            if (!rule.References.Any(x => Upgrade(rule, x) is not null))
                return rule;
            var references = rule.References.Select(x => Upgrade(rule, x) is { } s ? x with { Status = s } : x).ToList();
            var changed = references.Where((x, i) => !ReferenceEquals(x, rule.References[i])).ToList();
            var diagnostics = rule.Diagnostics.Where(d => !changed.Any(x => d == $"Filter {x.Identifier} (unknown-editorid) is dropped natively.")).ToList();
            diagnostics.AddRange(changed.Select(x => x.Status == "created-keyword"
                ? $"Filter {x.Identifier} names a keyword that SPID creates from another line."
                : $"Filter {x.Identifier} names a keyword that SPID creates from another line; whether it exists at this lookup is unverified."));
            var status = rule.Status;
            if (rule.Parser != "exclusive-group" && status is "accepted" or "filters-unresolved" or "unverified-filter")
            {
                List<(SpidReference Ref, ReferenceOutcome Outcome)> Of(params string[] fields)
                    => references.Where(x => fields.Contains(x.Field)).Select(x => (x, ReferenceStatus.Classify(x.Status))).ToList();
                var (match, not, all) = (Of("Match", "Parent"), Of("Not"), Of("All"));
                status = FiltersDefinitelyFail(match, not, all) ? "filters-unresolved"
                    : FilterOutcomes(match, not, all, ReferenceOutcome.Unverified).Any() ? "unverified-filter" : "accepted";
            }
            return rule with { References = references, Diagnostics = diagnostics, Status = status };
        }).ToList();
    }

    // Post-pass over file order: ExclusiveGroups::INI and Manager::LookupExclusiveGroups.
    // A line applies only when its MATCH and NOT lists each resolved at least one entry.
    private static List<SpidRule> ApplyExclusiveGroups(List<SpidRule> rules, DiagnosticMessages notes)
    {
        var groups = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var uncertain = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SpidRule>(rules.Count);
        // Created keywords have no FormKey; their EditorID identifies the group member.
        static string? Member(SpidReference r) => r.Status == "created-keyword" ? "created:" + r.Identifier.ToLowerInvariant()
            : r.Status == "resolved" ? r.FormKey : null;
        foreach (var rule in rules)
        {
            if (rule.Parser != "exclusive-group" || rule.Status != "pending")
            {
                result.Add(rule);
                continue;
            }
            var name = rule.Group ?? "";
            var matches = rule.References.Where(r => r.Field == "Match").ToList();
            var nots = rule.References.Where(r => r.Field == "Not").ToList();
            var matchOk = matches.Count == 0 || matches.Any(r => ReferenceStatus.Classify(r.Status) == ReferenceOutcome.Resolved);
            var notOk = nots.Count == 0 || nots.Any(r => ReferenceStatus.Classify(r.Status) == ReferenceOutcome.Resolved);
            if (!groups.TryGetValue(name, out var members)) groups[name] = members = new HashSet<string>(StringComparer.Ordinal);
            var unverified = matches.Concat(nots).Any(r => ReferenceStatus.Classify(r.Status) == ReferenceOutcome.Unverified);
            if (unverified) uncertain.Add(name);
            if (!matchOk || !notOk)
            {
                result.Add(unverified
                    ? (rule with { Status = "unverified-filter", Diagnostics = ["An exclusive-group entry is unverified; the group may change natively and is not established."] })
                    : (rule with { Status = "unresolved", Diagnostics = ["No exclusive-group form resolved for this line; the group is unchanged."] }));
                continue;
            }
            foreach (var key in matches.Select(Member).OfType<string>()) members.Add(key);
            foreach (var key in nots.Select(Member).OfType<string>()) members.Remove(key);
            result.Add(rule with { Status = unverified ? "unverified-filter" : "accepted" });
        }
        foreach (var group in groups.Where(g => uncertain.Contains(g.Key)).OrderBy(g => g.Key, StringComparer.Ordinal))
            notes.Add($"SPID ExclusiveGroup '{group.Key}' has unverified entries; its native membership ({group.Value.Count} established forms) is not established.");
        foreach (var group in groups.Where(g => !uncertain.Contains(g.Key) && g.Value.Count == 0).Select(g => g.Key))
            notes.AddInformation($"SPID ExclusiveGroup '{group}' resolves to no forms and is removed natively.");
        foreach (var group in groups.Where(g => !uncertain.Contains(g.Key) && g.Value.Count > 0).OrderBy(g => g.Key, StringComparer.Ordinal))
            notes.Add($"SPID ExclusiveGroup '{group.Key}' resolves to {group.Value.Count} forms; captured, not yet interpreted.");
        return result;
    }

    private static SpidRule Dropped(int index, LlosField field, string sanitized, string parser, string type, string status, string message, string? scope = null)
        => new(index, field.Line, field.Key, field.Value, sanitized, parser, type, false, "none", status, [], [message])
        {
            Scope = scope
        };

    private static SpidRule Make(int index, LlosField field, string sanitized, string parser, string type, bool final, string family, string status,
        List<SpidReference> references, List<string> diagnostics, (int Min, int Max, int? Package) count, RawId? form,
        SpidStringFilters? strings, SpidFormFilters? forms, SpidLevels? levels, SpidTraits? traits, double chance, bool deterministic,
        string? scope, string? group = null)
        => new(index, field.Line, field.Key, field.Value, sanitized, parser, type, final, family, status, references, diagnostics)
        {
            StringFilters = strings,
            FormFilters = forms,
            Levels = levels,
            Traits = traits,
            CountMin = count.Min,
            CountMax = count.Max,
            PackageIndex = count.Package,
            ChancePercent = chance,
            ChanceDeterministic = deterministic,
            Scope = scope,
            Group = group
        };

    // Families whose lines XEP will interpret later: items, outfits, keywords and factions (linked included).
    private static bool InScope(string family)
    {
        var bare = family.Replace("linked-", "").Replace("death-", "");
        return bare switch
        {
            "item" or "outfit" or "sleep-outfit" or "keyword" or "faction" => true,
            _ => false
        };
    }

    private static string FamilyOf(string type) => type switch
    {
        "Item" => "item", "Outfit" => "outfit", "SleepOutfit" => "sleep-outfit", "Keyword" => "keyword", "Faction" => "faction",
        "Spell" => "spell", "Perk" => "perk", "Shout" => "shout", "LevSpell" => "levspell", "Package" => "package", "Skin" => "skin",
        "Form" => "form", _ => "none"
    };

    // A generic Form's family: the record's inferred family, or keyword when the line created one.
    private static string GenericFamily(string status, IMajorRecordGetter? record)
        => record is not null ? InferFamily(record) : status == "created-keyword" ? "keyword" : "form";

    // Generic Form inference order: keyword, spell, levspell, perk, shout, bound object, outfit, faction, package/formlist.
    private static string InferFamily(IMajorRecordGetter record) => record switch
    {
        IKeywordGetter => "keyword",
        ISpellGetter => "spell",
        ILeveledSpellGetter => "levspell",
        IPerkGetter => "perk",
        IShoutGetter => "shout",
        _ when IsBoundItem(record) => "item",
        IOutfitGetter => "outfit",
        IFactionGetter => "faction",
        IPackageGetter or IFormListGetter => "package",
        _ => "unsupported-type"
    };

    // Any TESBoundObject is native-valid for Item lines and generic bound-object inference.
    private static bool IsBoundItem(IMajorRecordGetter record) => ReferenceStatus.IsBoundObject(record);

    private static string? FormTypeName(IMajorRecordGetter record) => record switch
    {
        IKeywordGetter => "Keyword", IOutfitGetter => "Outfit", IFactionGetter => "Faction", ISpellGetter => "Spell",
        ILeveledSpellGetter => "LevSpell", IPerkGetter => "Perk", IShoutGetter => "Shout", IPackageGetter => "Package",
        IFormListGetter => "FormList", IClassGetter => "Class", ICombatStyleGetter => "CombatStyle", IRaceGetter => "Race",
        INpcGetter => "NPC", IVoiceTypeGetter => "VoiceType", ILocationGetter => "Location", IArmorGetter => "Armor",
        IPlacedNpcGetter => "ActorCharacter",
        _ when IsBoundItem(record) => "Item",
        _ => null
    };

    // The record's type for the reference: the whitelist name when it has one, otherwise its concrete type.
    private static string RecordType(IMajorRecordGetter record) => FormTypeName(record) ?? record.GetType().Name;

    // Parsed identifier (distribution::get_record). Kind: formid-mod, formid, plugin, editorid.
    internal sealed record RawId(string Kind, uint FormId, string Plugin, string Text);

    private static RawId Distributable(string entry)
    {
        if (entry.Length == 0) throw new SpidError("missing-form", "Missing distributable form");
        return GetRecord(entry);
    }

    internal static RawId GetRecord(string entry)
    {
        if (entry.Contains('~'))
        {
            var parts = entry.Split("~", StringSplitOptions.None);
            return new("formid-mod", StoUL(parts[0], 16), parts[1], entry);
        }
        if (entry.Contains(".es", StringComparison.Ordinal)) return new("plugin", 0, entry, entry);
        if (IsOnlyHex(entry)) return new("formid", StoUL(entry, 16), "", entry);
        return new("editorid", 0, "", entry);
    }

    private static bool IsOnlyHex(string text)
        => text.Length > 2 && (text.StartsWith("0x", StringComparison.Ordinal) || text.StartsWith("0X", StringComparison.Ordinal))
           && text[2..].All(Uri.IsHexDigit);

    private static bool IsValidEntry(string text) => text.Length > 0 && !string.Equals(text, "NONE", StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitEntry(string text, string delimiter)
        => IsValidEntry(text) ? text.Split(delimiter, StringSplitOptions.None).ToList() : [];

    private static string[] SplitSections(string value, int components, out int count)
    {
        var raw = value.Split('|', StringSplitOptions.None);
        count = raw.Length;
        if (raw.Length > components)
            throw new SpidError("too-many-sections", $"Too many sections. Expected at most {components}, but got {raw.Length}");
        var sections = new string[components];
        for (var i = 0; i < components; i++) sections[i] = i < raw.Length ? raw[i] : "";
        return sections;
    }

    private static SpidStringFilters StringFilters(string entry)
    {
        var match = new List<string>();
        var not = new List<string>();
        var all = new List<string>();
        var any = new List<string>();
        foreach (var token in SplitEntry(entry, ","))
        {
            if (token.Contains('+'))
            {
                all.AddRange(token.Split('+', StringSplitOptions.None));
                continue;
            }
            var first = At0(token);
            if (first == '-') { not.Add(token[1..]); continue; }
            if (first == '*') { any.Add(token[1..]); continue; }
            match.Add(token);
        }
        return new(match, not, all, any);
    }

    private static char At0(string text)
        => text.Length == 0 ? throw new SpidError("malformed", "std::string::at: index out of range") : text[0];

    private static SpidFormFilters? FormFilters(string entry, bool combine, bool exclusion, bool required)
    {
        var tokens = SplitEntry(entry, ",");
        if (tokens.Count == 0)
        {
            if (required) throw new SpidError("missing-form", "Missing component parser");
            return new SpidFormFilters([], [], []);
        }
        var match = new List<RawId>();
        var not = new List<RawId>();
        var all = new List<RawId>();
        foreach (var token in tokens)
        {
            if (combine && token.Contains('+'))
            {
                all.AddRange(token.Split('+', StringSplitOptions.None).Select(GetRecord));
                continue;
            }
            if (exclusion && At0(token) == '-')
            {
                not.Add(GetRecord(token[1..]));
                continue;
            }
            match.Add(GetRecord(token));
        }
        return new(match.Select(r => r.Text).ToList(), not.Select(r => r.Text).ToList(), all.Select(r => r.Text).ToList());
    }

    private static SpidLevels Levels(string entry)
    {
        ushort? actorMin = null;
        ushort? actorMax = null;
        var skills = new List<SpidSkillRange>();
        var weights = new List<SpidSkillRange>();
        foreach (var token in SplitEntry(entry, ","))
        {
            if (token.Contains('('))
            {
                var weight = token.StartsWith('w');
                var sanitized = RemoveNonAlphanumeric(token);
                if (weight) sanitized = sanitized[1..];
                // Native string::split yields no parts for an empty token, so the skill is skipped, not malformed.
                if (sanitized.Length == 0) continue;
                var parts = sanitized.Split(' ', StringSplitOptions.None);
                var type = StoUL(parts[0], 10);
                if (type >= 18) continue;
                if (parts.Length < 2) throw new SpidError("malformed", "Skill level has no value");
                var min = (byte)StoUL(parts[1], 10);
                var max = parts.Length > 2 ? (byte)StoUL(parts[2], 10) : byte.MaxValue;
                (weight ? weights : skills).Add(new((int)type, min, max));
                continue;
            }
            var pair = token.Split('/', StringSplitOptions.None);
            if (pair.Length > 1)
            {
                actorMin = (ushort)StoUL(pair[0], 10);
                actorMax = (ushort)StoUL(pair[1], 10);
            }
            else
            {
                actorMin = (ushort)StoUL(token, 10);
                actorMax = ushort.MaxValue;
            }
        }
        return new(actorMin, actorMax, skills, weights);
    }

    private static SpidTraits Traits(string entry)
    {
        string? sex = null;
        bool? unique = null, summonable = null, child = null, leveled = null, teammate = null, startsDead = null;
        foreach (var trait in SplitEntry(entry, "/"))
        {
            switch (trait)
            {
                case "M": case "-F": sex = "male"; break;
                case "F": case "-M": sex = "female"; break;
                case "U": unique = true; break;
                case "-U": unique = false; break;
                case "S": summonable = true; break;
                case "-S": summonable = false; break;
                case "C": child = true; break;
                case "-C": child = false; break;
                case "L": leveled = true; break;
                case "-L": leveled = false; break;
                case "T": teammate = true; break;
                case "-T": teammate = false; break;
                case "D": startsDead = true; break;
                case "-D": startsDead = false; break;
            }
        }
        return new(sex, unique, summonable, child, leveled, teammate, startsDead);
    }

    private static (int Min, int Max, int? Package) CountOf(string entry, string type)
    {
        var package = type == "Package";
        if (!IsValidEntry(entry)) return (1, 1, package ? 0 : (int?)null);
        try
        {
            if (package) return (1, 1, StoI(entry));
            var pair = entry.Split('-', StringSplitOptions.None);
            if (pair.Length > 1) return (StoI(pair[0]), StoI(pair[1]), null);
            var count = StoI(entry);
            return (count, count, null);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new SpidError("malformed", $"Invalid index or count {entry}");
        }
    }

    private static (double Percent, bool Deterministic) Chance(string entry)
    {
        if (!IsValidEntry(entry)) return (100, false);
        var deterministic = entry.EndsWith('!');
        var numeric = deterministic ? entry[..^1] : entry;
        if (!IsValidEntry(numeric)) return (100, false);
        try
        {
            return (Stod(numeric), deterministic);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new SpidError("malformed", $"Invalid chance {entry}");
        }
    }

    // ---- Resolution -------------------------------------------------------

    private static (SpidReference Ref, IMajorRecordGetter? Found) ResolveDistributable(RawId raw, string declared, Lookup lookup, string field)
    {
        var (status, record) = Find(raw, declared, lookup, allowCreate: true);
        if (status is not "resolved" || record is null)
            return (new(field, raw.Text, status, record?.FormKey.ToString(), record is null ? null : RecordType(record)), null);
        var typeStatus = declared switch
        {
            "Keyword" => record is IKeywordGetter ? "ok" : "wrong-type",
            "Item" => IsBoundItem(record) ? "ok" : "wrong-type",
            "Outfit" or "SleepOutfit" => record is IOutfitGetter ? "ok" : "wrong-type",
            "Faction" => record is IFactionGetter ? "ok" : "wrong-type",
            "Form" => InferFamily(record) == "unsupported-type" ? "unsupported-type" : "ok",
            _ => "ok"
        };
        if (typeStatus != "ok")
            return (new(field, raw.Text, typeStatus, record.FormKey.ToString(), RecordType(record)), null);
        return (new(field, raw.Text, "resolved", record.FormKey.ToString(), RecordType(record)), record);
    }

    private static (SpidReference Ref, ReferenceOutcome Outcome) FilterReference(string field, string text, Lookup lookup, bool all, bool whitelist)
    {
        var raw = GetRecord(text);
        if (raw.Kind == "plugin")
        {
            var found = lookup.Resolver.TryGetPlugin(raw.Plugin, out _);
            return (new(field, text, found ? "plugin-filter" : "missing-plugin", null, null),
                found ? ReferenceOutcome.Resolved : ReferenceOutcome.Failed);
        }
        var (status, record) = Find(raw, "Form", lookup, allowCreate: false);
        if (status != "resolved" || record is null)
            return (new(field, text, status, record?.FormKey.ToString(), record is null ? null : RecordType(record)),
                ReferenceStatus.Classify(status));
        var type = FormTypeName(record);
        if (!all && whitelist && (type is null || !Whitelist.Contains(type)))
            return (new(field, text, "not-whitelisted", record.FormKey.ToString(), type), ReferenceOutcome.Failed);
        if (all && (type is null || !Whitelist.Contains(type)))
            return (new(field, text, "resolved-unmatchable", record.FormKey.ToString(), type), ReferenceOutcome.Resolved);
        return (new(field, text, "resolved", record.FormKey.ToString(), type), ReferenceOutcome.Resolved);
    }

    // Find: FormID~Plugin looks up the exact local FormID and refuses values above the plugin's local range
    // (as LLOS does). Bare FormIDs map through the supplied load order (regular plugins by index, light plugins
    // 0xFE + 12-bit index); that mapping is unverified. EditorIDs use the native EditorID map: Keyword lines and,
    // when the po3 cache is absent, generic Form lines search keyword records alone. Generic Form lines create a
    // keyword for an unknown EditorID when creation is allowed.
    private static (string Status, IMajorRecordGetter? Record) Find(RawId raw, string declared, Lookup lookup, bool allowCreate)
    {
        switch (raw.Kind)
        {
            case "formid-mod":
                {
                    if (!lookup.Resolver.TryGetPlugin(raw.Plugin, out var plugin))
                        return (lookup.MergeMapper ? "merge-mapping-unavailable" : "missing-plugin", null);
                    // MergeMapper remaps every numeric reference before lookup (FormData.h); XEP cannot read its maps.
                    if (lookup.MergeMapper) return ("merge-mapping-unverified", null);
                    if (raw.FormId > (plugin.IsSmallMaster ? 0xFFFu : 0xFFFFFFu)) return ("outside-local-range", null);
                    var record = lookup.Resolver.Winner(new FormKey(plugin.ModKey, raw.FormId));
                    return record is null ? ("missing-record", null) : Typed(record, declared);
                }
            case "formid":
                {
                    var top = raw.FormId >> 24;
                    ModKey owner;
                    uint local;
                    if (top < 0xFE && top < lookup.Regular.Count)
                    {
                        owner = lookup.Regular[(int)top].ModKey;
                        local = raw.FormId & 0xFFFFFFu;
                    }
                    else if (top == 0xFE && ((raw.FormId >> 12) & 0xFFF) < lookup.Light.Count)
                    {
                        owner = lookup.Light[(int)((raw.FormId >> 12) & 0xFFF)].ModKey;
                        local = raw.FormId & 0xFFFu;
                    }
                    else return ("bare-formid-unverified", null);
                    var record = lookup.Resolver.Winner(new FormKey(owner, local));
                    return record is null ? ("bare-formid-unverified", null) : ("bare-formid-unverified", record);
                }
            case "plugin":
                return ("plugin-only", null);
            default:
                {
                    var text = raw.Text;
                    if (text.Length == 0) return ("malformed-editorid", null);
                    if (declared == "Keyword" || lookup.EditorIds != "available")
                    {
                        var (keywordStatus, keyword) = lookup.Resolver.EditorId(text, r => r is IKeywordGetter);
                        if (keyword is not null) return Typed(keyword, declared);
                        if (keywordStatus == "ambiguous-editorid") return ("ambiguous-editorid", null);
                        if (declared == "Keyword") return allowCreate ? ("created-keyword", null) : ("unknown-editorid", null);
                    }
                    if (lookup.EditorIds != "available") return ("editorid-cache-unavailable", null);
                    var (status, found) = lookup.Resolver.EditorId(text);
                    if (found is not null) return Typed(found, declared);
                    if (status == "ambiguous-editorid") return ("ambiguous-editorid", null);
                    return declared == "Form" && allowCreate ? ("created-keyword", null) : ("unknown-editorid", null);
                }
        }
    }

    // Deleted records keep their FormKey for the reference but are not resolved.
    private static (string Status, IMajorRecordGetter? Record) Typed(IMajorRecordGetter record, string declared)
    {
        if (record.IsDeleted) return ("deleted-record", record);
        if (declared == "Keyword" && record is IKeywordGetter && string.IsNullOrEmpty(record.EditorID))
            return ("invalid-keyword", null);
        return ("resolved", record);
    }

    // Shared lookup state for one capture: plugins, winners, EditorID support and MergeMapper presence.
    private sealed class Lookup(NativeFormResolver resolver, IReadOnlyList<ISkyrimModGetter> mods, string editorIds, bool mergeMapper)
    {
        public NativeFormResolver Resolver { get; } = resolver;
        public string EditorIds { get; } = editorIds;
        public bool MergeMapper { get; } = mergeMapper;
        public List<ISkyrimModGetter> Regular { get; } = mods.Where(m => !m.IsSmallMaster).ToList();
        public List<ISkyrimModGetter> Light { get; } = mods.Where(m => m.IsSmallMaster).ToList();
    }

    // ---- Numeric parsing (std::stoul / std::stoi / std::stod) -------------

    // std::stoul on a 32-bit unsigned long: leading space, optional sign (negation
    // modulo 2^32), optional 0x prefix for base 16; no digits throws; overflow throws.
    internal static uint StoUL(string text, int radix)
    {
        var i = 0;
        while (i < text.Length && IsSpace(text[i])) i++;
        var negative = false;
        if (i < text.Length && text[i] is '+' or '-') { negative = text[i] == '-'; i++; }
        if (radix == 16 && i + 2 < text.Length && text[i] == '0' && text[i + 1] is 'x' or 'X' && Uri.IsHexDigit(text[i + 2])) i += 2;
        var start = i;
        ulong value = 0;
        var overflow = false;
        for (; i < text.Length; i++)
        {
            var digit = Digit(text[i], radix);
            if (digit < 0) break;
            if (!overflow)
            {
                value = value * (ulong)radix + (ulong)digit;
                if (value > uint.MaxValue) overflow = true;
            }
        }
        if (i == start) throw new FormatException("std::stoul: no conversion");
        if (overflow) throw new OverflowException("std::stoul: out of range");
        return negative ? unchecked(0u - (uint)value) : (uint)value;
    }

    // std::stoi on a 32-bit int.
    internal static int StoI(string text)
    {
        var i = 0;
        while (i < text.Length && IsSpace(text[i])) i++;
        var negative = false;
        if (i < text.Length && text[i] is '+' or '-') { negative = text[i] == '-'; i++; }
        var start = i;
        long value = 0;
        for (; i < text.Length && text[i] is >= '0' and <= '9'; i++)
        {
            value = value * 10 + (text[i] - '0');
            if (value > 2147483648L) throw new OverflowException("std::stoi: out of range");
        }
        if (i == start) throw new FormatException("std::stoi: no conversion");
        if (negative) value = -value;
        if (value > int.MaxValue || value < int.MinValue) throw new OverflowException("std::stoi: out of range");
        return (int)value;
    }

    // std::stod: decimal, inf and nan forms. Hexadecimal floats are not evaluated
    // (the leading "0" is read); overflow throws.
    internal static double Stod(string text)
    {
        var match = Regex.Match(text, @"^[ \t\n\v\f\r]*([+-]?)(?:(inf(?:inity)?)|(nan)|((?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) throw new FormatException("std::stod: no conversion");
        var sign = match.Groups[1].Value == "-" ? -1.0 : 1.0;
        if (match.Groups[2].Success) return sign * double.PositiveInfinity;
        if (match.Groups[3].Success) return double.NaN;
        var value = double.Parse(match.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (double.IsInfinity(value)) throw new OverflowException("std::stod: out of range");
        return sign * value;
    }

    private static int Digit(char c, int radix)
    {
        int value = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
        return value < radix ? value : -1;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private static string RemoveNonAlphanumeric(string text)
    {
        var chars = text.Select(c => c < 0x80 && char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray();
        return new string(chars).Trim();
    }

    // Case-insensitive ASCII key order of CSimpleIni (SI_GenericNoCase).
    private sealed class KeyOrder : IComparer<string>
    {
        public static readonly KeyOrder Instance = new();
        public int Compare(string? x, string? y) => string.CompareOrdinal(Fold(x!), Fold(y!));
        private static string Fold(string text) => string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
        });
    }

    private sealed class SpidError(string status, string message) : Exception(message)
    {
        public string Status { get; } = status;
    }
}
