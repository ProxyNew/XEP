using System.IO;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public sealed record CidReference(string Field, string Identifier, string Status, string? FormKey, string? RecordType);
public sealed record CidRule(int Index, int Line, string Container, string Value, string Operation, string Status,
    ushort Count, ushort Chance, string? Location, string? LocationKeyword,
    IReadOnlyList<CidReference> References, IReadOnlyList<string> Diagnostics)
{
    public string ContainerScope { get; init; } = "unresolved";
}
public sealed record CidFile(string Path, string NativePath, string Sha256, string Status, IReadOnlyList<CidRule> Rules);
public sealed record CidInputReport(string Status, string Version, string DllSha256, string SourceRevision,
    string Fingerprint, IReadOnlyList<CidFile> Files, IReadOnlyList<string> Diagnostics)
{
    internal IReadOnlyList<string> Information { get; init; } = [];
    internal IReadOnlyDictionary<string, ReadOnlyMemory<byte>> CapturedBytes { get; init; }
        = new Dictionary<string, ReadOnlyMemory<byte>>();
}

// Input inventory bound to the pinned Container Item Distributor 2.1.3 source
// (Parser, Utility, Distributor, Hooks). Captured rules are inventory evidence
// provenance; distribution models their additions separately (SpidCidConsumers).
internal static class CidInputs
{
    public const string Source = "75a66a701a604cd9626e92538188863f48e50044";
    // Installed Nexus 2.1.3.0 build. Its PDB matches the DLL (RSDS GUID/age) and
    // records project sources matching the pinned tree; see cid-213-contract.md.
    private const string SourceMatchedDll = "240973620D86B583078A7870D7EF1EA6D320CC7697654E9143416A12F03273F4";

    public static CidInputReport Capture(string? dataFolder, IReadOnlyList<ISkyrimModGetter> mods,
        Func<IReadOnlyDictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)>>? winners = null,
        NativeFormResolver? sharedResolver = null)
    {
        using var timing = PhaseTiming.Measure("cid.capture");
        if (string.IsNullOrWhiteSpace(dataFolder) || !Directory.Exists(dataFolder)) return Empty();
        var notes = new DiagnosticMessages();
        var bytes = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var incomplete = false;
        var plugins = Path.Combine(dataFolder, "SKSE", "Plugins");
        var dllPath = Path.Combine(plugins, "ContainerItemDistributor.dll");
        var dll = File.Exists(dllPath) ? dllPath : null;
        var configs = new List<(string Physical, string Native, string Relative)>();
        using (PhaseTiming.Measure("cid.discovery"))
        try
        {
            // Parser::ParseINIs: non-recursive Data\, exact ".ini" extension and a
            // case-sensitive "_CID.ini" suffix. Directory entries are listed too.
            foreach (var path in Directory.EnumerateFileSystemEntries(dataFolder))
            {
                var name = Path.GetFileName(path);
                if (LlosInputs.NativeExtension(name) != ".ini" || !name.EndsWith("_CID.ini", StringComparison.Ordinal)) continue;
                configs.Add((path, @"Data\" + name, name));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            incomplete = true;
            notes.Add("CID discovery incomplete: " + ex.Message);
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
                notes.Add("Could not read the CID DLL: " + ex.Message);
            }

        // Parser::ParseINIs runs only after Listener's kDataLoaded po3_Tweaks gate; without it
        // CID reports a fatal error, so no configuration is loaded.
        var resolver = sharedResolver ?? new NativeFormResolver(mods, winners ?? (() => PatcherEngine.LoadOrderWinners(mods)));
        var editorIds = LlosInputs.EditorIdSupport(plugins);
        var gated = dll is not null && !File.Exists(Path.Combine(plugins, "po3_Tweaks.dll"));
        var files = new List<CidFile>();
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
                var status = dll is null ? "missing-dll" : gated ? "dependency-missing" : "active";
                var rules = new List<CidRule>();
                if (!gated)
                {
                    using var parsing = PhaseTiming.Measure("cid.parse");
                    var sections = LlosInputs.Ini.Read(content);
                    if (sections.Any(s => !s.Name.Equals("General", StringComparison.OrdinalIgnoreCase) && s.Fields.Count > 0))
                        notes.Add($"CID {relative}: keys and sections other than [General] are ignored natively.");
                    var general = sections.Where(s => s.Name.Equals("General", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(s => s.Fields).ToList();
                    if (general.Any(f => f.Key.Any(c => c > 0x7F)))
                        notes.Add($"CID {relative}: non-ASCII keys; native key order is unverified.");
                    // CSimpleIni GetAllKeys: unique keys, case-insensitive ASCII order; keys differing
                    // only by case are one key spelled by its first occurrence, values in file order.
                    foreach (var key in general.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(g => AsciiLower(g.Key), StringComparer.Ordinal))
                        foreach (var field in key)
                        {
                            var rule = stopped ? Skipped(index, key.Key, field) : ReadRule(index, key.Key, field, resolver, editorIds);
                            stopped |= rule.Status == "native-exception";
                            rules.Add(rule);
                            index++;
                        }
                }
                files.Add(new(relative, native, Hash(content), status, rules.AsReadOnly()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                incomplete = true;
                files.Add(new(relative, native, "", "unreadable", []));
                notes.Add($"CID {relative}: {ex.Message}");
            }
        }

        var release = ReleaseStatus(version, dllHash);
        // A native std::stoul throw terminates the game during data load, so the report reads as such.
        var overall = incomplete ? "incomplete-capture" : dll is null ? "missing-dll" : gated ? "dependency-missing"
            : stopped ? "native-exception" : release;
        notes.AddDiagnostic(dll is null
            ? "CID configuration was found without a projected ContainerItemDistributor DLL; nothing is applied natively and no CID distribution is imported."
            : gated
                ? "CID requires powerofthree's Tweaks (po3_Tweaks.dll in SKSE\\Plugins) at kDataLoaded; it is absent, so CID reports a fatal error and loads no configuration."
                : release == "source-matched-2.1.3"
                    ? $"CID {version}: DLL/PDB identities match the pinned {Source[..7]} source; captured statuses describe native input loading, not distribution."
                    : release == "accepted-major-2"
                        ? $"CID {version}: same major version as the verified 2.1.3 release (DLL {dllHash}); its source contract applies."
                        : $"CID {version} differs in major version from the verified 2.1.3 release (DLL {dllHash}); its source contract is applied unverified.", informational: !gated && dll is not null && (release == "source-matched-2.1.3" || release == "accepted-major-2"));
        if (stopped)
            notes.Add("CID 2.1.3 terminates the game during data load: a count, chance or FormID field cannot be converted by native std::stoul (no digits or out of range) inside a noexcept function, so no CID rule takes effect. Rules before the failing line keep their parse statuses.");
        var rulesAll = files.SelectMany(f => f.Rules).ToList();
        notes.AddRuleProblems("CID", rulesAll.Where(r => r.Status != "accepted").Select(r => r.Status));
        var references = rulesAll.SelectMany(r => r.References).ToList();
        var unresolved = references.Count(r => ReferenceStatus.Classify(r.Status) == ReferenceOutcome.Failed);
        if (unresolved > 0)
            notes.Add($"CID: {unresolved} references definitely fail against the active load order. Failed items drop their rule natively; failed location predicates are ignored (fail-open); failed containers never fire.");
        var unverifiedRefs = references.Count(r => ReferenceStatus.Classify(r.Status) == ReferenceOutcome.Unverified);
        if (unverifiedRefs > 0)
            notes.Add($"CID: {unverifiedRefs} references are unverified (ambiguous EditorIDs or an unavailable EditorID cache); their rules are not interpreted as active or dropped.");
        if (references.Any(r => r.Status == "editorid-cache-unavailable"))
            notes.Add("CID EditorID references require po3_Tweaks with [Fixes] Load EditorIDs=true; without it their native resolution is unverified and they are not imported.");
        if (rulesAll.Count > 0)
        {
            notes.AddInformation($"CID captured {files.Count} configuration files and {rulesAll.Count} rules: " +
                string.Join(", ", rulesAll.GroupBy(r => r.Status).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}")) +
                ". Accepted additions that can reach equipment are modelled as container consumers; other effects are diagnosed per rule (see the CID route notes).");
            // Runtime facts from Distributor.cpp, Hooks.cpp and Events.cpp; source-level, runtime not tested.
            notes.AddInformation("CID runtime: distribution runs on each container reference's 3D load; a reference-keyed entry replaces its base's entries instead of merging with them.");
            notes.AddInformation("CID runtime: each rule rolls once per reference (1-100, passing at or below its chance); order is all adds, then all removes, then all remove-alls.");
            notes.AddInformation("CID runtime: remove and remove-all skip leveled-list payloads; leveled adds resolve against the player's level when distribution runs.");
            notes.AddInformation("CID runtime: respawning containers redistribute after ResetInventory; saving removes CID-added objects from live containers and loading clears the tracking.");
            notes.AddInformation("CID runtime: a location predicate skips the reference when it IS in the named location (the diagnostic text says the opposite); references without a current location are not skipped.");
        }
        var identity = $"cid-capture-v1\n{Source}\n{overall}\n{version}\n{dllHash}\n{editorIds}\n" +
            string.Join('\n', files.Select(f => $"{f.NativePath}|{f.Sha256}|{f.Status}")) +
            // Resolution depends on the load order as well as the configuration bytes.
            "\nrefs=" + Hash(Encoding.UTF8.GetBytes(string.Join('\n', rulesAll.SelectMany(r => r.References.Select(x =>
                $"{r.Index}|{x.Field}|{x.Identifier}|{x.Status}|{x.FormKey}")))));
        return new(overall, version, dllHash, Source, Hash(Encoding.UTF8.GetBytes(identity)), files.AsReadOnly(), notes.AsReadOnly())
        { Information = notes.Information, CapturedBytes = bytes.AsReadOnly() };
    }

    // Framework version acceptance: the verified build silently, its major
    // version with a label, any other or unreadable major with a warning.
    internal static string ReleaseStatus(string version, string dllHash)
        => version == "2.1.3.0" && dllHash == SourceMatchedDll ? "source-matched-2.1.3"
            : SkyPatcherInputs.Major(version) == 2 ? "accepted-major-2" : "accepted-other-major";

    private static CidInputReport Empty() => new("absent", "", "", Source, "", [], []);
    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));
    private static string AsciiLower(string text) => new(text.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c).ToArray());

    private static CidRule Skipped(int index, string container, LlosField field)
        => new(index, field.Line, container, field.Value, "none", "not-loaded-after-exception", 0, 100, null, null, [], []);

    // Parser::ClassifyString on the raw value, Parser::Tokenize, then Utility::BuildDistrObject
    // in its declared field order. std::stoul throws inside noexcept functions, which terminates
    // the game; that case is reported as native-exception by the caller's stop flag.
    private static CidRule ReadRule(int index, string to, LlosField field, NativeFormResolver resolver, string editorIds)
    {
        var value = field.Value;
        var diagnostics = new List<string>();
        var operation = value.StartsWith('-') ? (value.Contains('|') ? "remove" : "remove-all")
            : value.Contains('|') ? "add" : "error";
        if (operation == "error")
        {
            diagnostics.Add("Neither an ADD (no leading '-', with '|') nor a remove form; the line is skipped natively.");
            return new(index, field.Line, to, value, operation, "unclassified", 0, 100, null, null, [], diagnostics);
        }
        try
        {
            var text = operation == "add" ? value : value[1..];
            var chance = (ushort)100;
            var chanceAt = text.LastIndexOf('?');
            if (chanceAt >= 0)
            {
                chance = ToUnsignedInt(text[(chanceAt + 1)..]);
                text = text[..chanceAt];
            }
            var keyword = "";
            var keywordAt = text.LastIndexOf('@');
            if (keywordAt >= 0)
            {
                keyword = text[(keywordAt + 1)..];
                text = text[..keywordAt];
            }
            var pieces = text.Split('|');
            var (min, max) = operation == "remove-all" ? (1, 3) : (2, 4);
            if (pieces.Length < min || pieces.Length > max)
            {
                diagnostics.Add($"{text} is ill-formed natively; the rule is dropped.");
                return new(index, field.Line, to, value, operation, "ill-formed", 0, chance, null, Or(keyword), [], diagnostics);
            }
            var count = operation == "remove-all" ? (ushort)0 : ToUnsignedInt(pieces[1]);
            var location = pieces.Length > 2 && pieces[2].Length > 0 ? pieces[2] : null;
            if (pieces.Length > 3) diagnostics.Add("The fourth pipe field is ignored natively.");
            if (operation == "add" && count == 0) diagnostics.Add("Count 0 adds nothing natively.");
            if (chance == 0) diagnostics.Add("Chance 0 never passes the native roll.");
            var refs = new List<CidReference>();

            // Bound object first: a failure drops the rule before the other fields are read.
            var (itemStatus, found) = Find(pieces[0], resolver, editorIds);
            if (found is not null)
                itemStatus = found.IsDeleted ? "deleted-record" : IsBoundObject(found) ? "resolved" : "wrong-type";
            refs.Add(new("Item", pieces[0], itemStatus, found?.FormKey.ToString(), TypeName(found)));
            if (itemStatus != "resolved")
            {
                // An unverified item cannot be called dropped: its native outcome is not established.
                var unverifiedItem = ReferenceStatus.Classify(itemStatus) == ReferenceOutcome.Unverified;
                diagnostics.Add(unverifiedItem
                    ? $"Item {pieces[0]} is unverified ({itemStatus}); its native outcome is not established."
                    : $"Item {pieces[0]} is not a bound object ({itemStatus}); the rule is dropped natively.");
                if (!unverifiedItem)
                    return new(index, field.Line, to, value, operation, "missing-item", count, chance, location, Or(keyword), refs, diagnostics);
                // An unverified item may still apply natively, so its container is still
                // resolved: consumers of the rule (additions, removals) need its recipient.
                var (unverifiedContainerStatus, unverifiedTarget) = Find(to, resolver, editorIds);
                if (unverifiedTarget is { IsDeleted: true }) { unverifiedContainerStatus = "deleted-record"; unverifiedTarget = null; }
                refs.Add(new("Container", to, unverifiedContainerStatus, unverifiedTarget?.FormKey.ToString(), TypeName(unverifiedTarget)));
                return new(index, field.Line, to, value, operation, "unverified-reference",
                    count, chance, location, Or(keyword), refs, diagnostics) { ContainerScope = ContainerScope(unverifiedTarget, resolver) };
            }
            var item = found!;

            var (containerStatus, container) = Find(to, resolver, editorIds);
            // Deleted records follow the item policy: kept for the reference, not resolved.
            if (container is { IsDeleted: true }) containerStatus = "deleted-record";
            refs.Add(new("Container", to, containerStatus, container?.FormKey.ToString(), TypeName(container)));
            if (containerStatus == "deleted-record") container = null;
            var scope = ContainerScope(container, resolver);
            var unverifiedContainer = container is null && ReferenceStatus.Classify(containerStatus) == ReferenceOutcome.Unverified;
            if (unverifiedContainer) diagnostics.Add($"Container {to} is unverified ({containerStatus}); its native outcome is not established.");
            else if (container is null) diagnostics.Add($"Container {to} does not resolve natively; the rule is stored under no container and never fires.");
            else if (scope == "inert-type") diagnostics.Add($"Container {to} is not a container or container reference; CID distributes only onto non-actor container references, so the rule never fires.");

            var locationResolved = false;
            var unverifiedPredicate = false;
            if (location is not null)
            {
                var (status, record) = Find(location, resolver, editorIds);
                if (record is not null) status = record.IsDeleted ? "deleted-record" : record is ILocationGetter ? "resolved" : "wrong-type";
                refs.Add(new("Location", location, status, record?.FormKey.ToString(), TypeName(record)));
                locationResolved = status == "resolved";
                if (!locationResolved && ReferenceStatus.Classify(status) == ReferenceOutcome.Unverified)
                {
                    unverifiedPredicate = true;
                    diagnostics.Add($"Location {location} is unverified ({status}); its native location predicate is not established.");
                }
                else if (!locationResolved) diagnostics.Add($"Location {location} does not resolve natively ({status}); the rule applies without a location predicate (fail-open).");
            }
            var keywordResolved = false;
            if (keyword.Length > 0)
            {
                var (status, record) = Find(keyword, resolver, editorIds);
                if (record is not null) status = record.IsDeleted ? "deleted-record" : record is IKeywordGetter ? "resolved" : "wrong-type";
                refs.Add(new("LocationKeyword", keyword, status, record?.FormKey.ToString(), TypeName(record)));
                keywordResolved = status == "resolved";
                if (!keywordResolved && ReferenceStatus.Classify(status) == ReferenceOutcome.Unverified)
                {
                    unverifiedPredicate = true;
                    diagnostics.Add($"Location keyword {keyword} is unverified ({status}); its native keyword predicate is not established.");
                }
                else if (!keywordResolved) diagnostics.Add($"Location keyword {keyword} does not resolve natively ({status}); the rule applies without a keyword predicate (fail-open).");
            }
            if (locationResolved && keywordResolved)
            {
                diagnostics.Add("Both a location and a location keyword resolve; native CID rejects the rule.");
                return new(index, field.Line, to, value, operation, "both-location-predicates", count, chance, location, Or(keyword), refs, diagnostics);
            }

            var leveledRemove = operation != "add" && item is ILeveledItemGetter;
            if (leveledRemove) diagnostics.Add("Remove operations skip leveled-list payloads at runtime; this rule has no effect.");
            // A definitely failed container never fires, whatever the predicates say.
            var status3 = container is null ? (unverifiedContainer ? "unverified-reference" : "container-unresolved")
                : leveledRemove ? "leveled-remove-inert"
                : unverifiedPredicate ? "unverified-reference" : "accepted";
            return new(index, field.Line, to, value, operation, status3, count, chance, location, Or(keyword), refs, diagnostics)
            {
                ContainerScope = scope
            };
        }
        catch (NativeException ex)
        {
            diagnostics.Add(ex.Message + ": CID 2.1.3 terminates the game during data load, so no CID rule takes effect.");
            return new(index, field.Line, to, value, operation, "native-exception", 0, 100, null, null, [], diagnostics);
        }
    }

    private static string? Or(string text) => text.Length > 0 ? text : null;

    // Utility::GetBoundObject, GetContainerFormID, GetLocation and GetLocationKeyword: "hex~Plugin"
    // when a tilde is present, otherwise an EditorID. The hex part goes through Map::ToFormID (std::stoul),
    // so it throws when it has no digits or overflows. FormIDs above the plugin's local range are refused (outside-local-range).
    private static (string Status, IMajorRecordGetter? Record) Find(string identifier, NativeFormResolver resolver, string editorIds)
    {
        var tilde = identifier.IndexOf('~');
        if (tilde < 0)
        {
            var (status, found) = resolver.EditorId(identifier);
            // The native EditorID cache is needed for every EditorID except keyword records.
            if (editorIds != "available" && found is not IKeywordGetter) return ("editorid-cache-unavailable", null);
            return (status, found);
        }
        var formId = Stoul(identifier[..tilde], 16);
        var name = identifier[(tilde + 1)..];
        if (!resolver.TryGetPlugin(name, out var plugin)) return ("missing-plugin", null);
        // CommonLib composes the runtime prefix with the raw value; a value outside the local range is refused, not masked.
        if (formId > (plugin.IsSmallMaster ? 0xFFFu : 0xFFFFFFu)) return ("outside-local-range", null);
        var record = resolver.Winner(new FormKey(plugin.ModKey, formId));
        if (record is null) return ("missing-record", null);
        return ("resolved", record);
    }

    // Utility::ResolveLeveledList is not reached here; this is the bound-object type gate of
    // GetBoundObject, which accepts any TESBoundObject (equipment relevance is decided later).
    internal static bool IsBoundObject(IMajorRecordGetter record) => ReferenceStatus.IsBoundObject(record);

    // Distributor::Distribute keys reference FormIDs directly; base keys are looked up for the base.
    // A placed reference is distributable only when its base is a container (reference scope).
    private static string ContainerScope(IMajorRecordGetter? record, NativeFormResolver resolver) => record switch
    {
        null => "unresolved",
        IPlacedObjectGetter placed => resolver.Winner(placed.Base.FormKey) is IContainerGetter ? "reference" : "inert-type",
        IContainerGetter => "base",
        _ => "inert-type",
    };

    private static string? TypeName(IMajorRecordGetter? record) => record?.GetType().Name;

    // Map::ToUnsignedInt: std::stoul (base 10) truncated to u16.
    private static ushort ToUnsignedInt(string text) => (ushort)Stoul(text, 10);

    // std::stoul on a 32-bit unsigned long: leading whitespace, optional sign, digits of the radix
    // (an optional 0x prefix in base 16 when a hex digit follows); trailing text is ignored. No digits
    // or a magnitude above 2^32-1 throws (libstdc++ invalid_argument / out_of_range); a sign wraps modulo 2^32.
    internal static uint Stoul(string text, int radix)
    {
        var i = 0;
        while (i < text.Length && IsSpace(text[i])) i++;
        var negative = i < text.Length && text[i] == '-';
        if (i < text.Length && text[i] is '+' or '-') i++;
        if (radix == 16 && i + 2 < text.Length && text[i] == '0' && text[i + 1] is 'x' or 'X' && Uri.IsHexDigit(text[i + 2])) i += 2;
        var start = i;
        ulong value = 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            var digit = c is >= '0' and <= '9' ? c - '0' : radix == 16 && Uri.IsHexDigit(c) ? char.ToUpperInvariant(c) - 'A' + 10 : -1;
            if (digit < 0) break;
            value = value * (ulong)radix + (ulong)digit;
            if (value > uint.MaxValue) throw new NativeException($"std::stoul out of range for '{text}'");
        }
        if (i == start) throw new NativeException($"std::stoul invalid argument '{text}'");
        return negative ? unchecked(0u - (uint)value) : (uint)value;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    // Thrown where the native conversion would throw inside a noexcept function.
    private sealed class NativeException(string message) : Exception(message);
}
