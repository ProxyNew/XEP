using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public sealed record CdfReference(string Field, string Identifier, string Status, string? FormKey);
public sealed record CdfRule(int Index, string? FriendlyName, string Status, string Json,
    IReadOnlyList<CdfReference> References, IReadOnlyList<string> Diagnostics)
{
    public string NativeDisposition { get; init; } = "unresolved";
    public CdfGuards? Guards { get; init; }
    public IReadOnlyList<CdfChange> Changes { get; init; } = [];
}
public sealed record CdfGuards(bool BypassUnsafeContainers, bool AllowVendors, bool OnlyVendors, bool RandomAdd);
public sealed record CdfChange(int Index, string Disposition, string Operation, uint? Count);
public sealed record CdfSettings(string Status, string? RawMaxRefLookupDistance, double? MaxRefLookupDistance);
public sealed record CdfFile(string Path, string Sha256, string Status, IReadOnlyList<CdfRule> Rules);
public sealed record CdfInputReport(string Status, string Version, string DllSha256, string SourceRevision,
    string Fingerprint, IReadOnlyList<CdfFile> Files, IReadOnlyList<string> Diagnostics)
{
    internal IReadOnlyList<string> Information { get; init; } = [];
    public CdfSettings Settings { get; init; } = new("default", null, 25000);
}

// Diagnostic inventory, not a distribution interpreter. The observed 3.1.0 DLL
// matches its PDB identity; all 31 project source checksums match the tree below.
// This binds the input grammar to source content, not to tested runtime effects.
// See the ticket's source-checksum-comparison.json and source-history-verification.md.
internal static partial class CdfInputs
{
    internal const string Source = "38ca39282e24ed8f7f8423f106d0a660689673c6";
    private const string SourceMatchedDll = "D809129FD959A684F10818C62E7DBB2CE999F46E7D4A0EA759D9A114716309A9";
    private static readonly HashSet<string> Conditions = new(StringComparer.Ordinal)
    {
        "plugins", "bypassUnsafeContainers", "allowVendors", "onlyVendors", "randomAdd",
        "containers", "!containers", "references", "!references", "locations", "!locations",
        "worldspaces", "!worldspaces", "locationKeywords", "!locationKeywords",
        "playerSkills", "!playerSkills", "globals", "!globals", "questConditions"
    };

    // The simple form for fixtures: builds the scan environment for one Data folder.
    public static CdfInputReport Capture(string? dataFolder, IReadOnlyList<ISkyrimModGetter> mods)
        => Capture(FrameworkInputs.Create(dataFolder, mods));

    internal static CdfInputReport Capture(FrameworkInputs inputs)
    {
        using var timing = PhaseTiming.Measure("cdf.capture");
        if (!inputs.Exists) return Empty();
        var plugins = inputs.PluginsFolder;
        var resolver = inputs.Resolver;
        var rootEntry = inputs.PluginDirectory("ContainerDistributionFramework");
        var dll = inputs.PluginFile("ContainerDistributionFramework.dll");
        var ini = inputs.PluginFile("ContainerDistributionFramework.ini");
        if (dll is null && ini is null && rootEntry is null) return Empty();
        var files = new List<CdfFile>();
        var notes = new DiagnosticMessages();
        var version = "";
        var dllHash = "";
        var incomplete = false;
        var settings = new CdfSettings("default", null, 25000);
        try
        {
            if (dll is not null)
            {
                var dllIdentity = DllIdentity.ReadFileVersion(dll.Path);
                dllHash = dllIdentity.Sha256;
                version = dllIdentity.Version;
            }
            if (ini is not null) CaptureFile(ini.Path, false);
            // The pinned source uses direct regular files, exact lowercase suffix,
            // then sorted paths. Do not borrow SkyPatcher's recursive discovery.
            using var discovery = PhaseTiming.Measure("cdf.discovery");
            if (rootEntry is not null)
            {
                if (rootEntry.IsLink)
                {
                    incomplete = true;
                    notes.Add("CDF configuration directory is a link; discovery was not followed.");
                }
                else foreach (var path in Directory.EnumerateFiles(rootEntry.Path).Where(p => Path.GetExtension(p) == ".json")
                    .Order(StringComparer.Ordinal)) CaptureFile(path, true);
            }
            else notes.AddInformation("CDF configuration directory is absent; the pinned source ends JSON discovery here. This differs from a present empty directory.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            incomplete = true;
            notes.Add("CDF capture incomplete: " + ex.Message);
        }
        var release = Release.Status(version, dllHash);
        var sourceMatched = release == "source-matched-3.1.0";
        var sameMajor = release == "accepted-major-3";
        var status = incomplete ? "incomplete-capture" : dllHash.Length == 0 ? "missing-dll" : release;
        notes.AddDiagnostic(dllHash.Length == 0
            ? "CDF configuration was captured without a projected DLL; no CDF distribution is imported."
            : $"CDF {version}: {(sourceMatched ? "project source content verified through matching DLL/PDB identities and source checksums" : sameMajor ? "same major version as the verified 3.1.0 release; its source semantics apply" : $"major version differs from the verified 3.1.0 release (DLL {dllHash}); its source semantics are applied unverified")}. " +
                "Captured rule statuses describe input parsing, not route admission. Shared analysis determines supported effects and distribution permission.", informational: dllHash.Length > 0 && (sourceMatched || sameMajor));
        notes.AddInformation("Pinned native defaults: bypassUnsafeContainers=false, allowVendors=false, onlyVendors=false, randomAdd=false; additions count=1, explicit removals count=0 (all). onlyVendors=true enables vendors but does not restrict to vendors in this source. Execution order: additions, removals, keyword removals, replacements, keyword replacements.");
        notes.AddInformation("Pinned INI General/fMaxRefLookupDistance defaults to 25000 and clamps finite values to [10000,150000]. Raw INI bytes are frozen; non-default numeric syntax remains uninterpreted and cannot authorize a route.");
        notes.AddInformation("CDF capture does not disable plugin-only distribution. Supported effects enter shared analysis; composition with unsupported effects remains uncertified and is explicitly diagnosed.");
        var unresolved = files.SelectMany(f => f.Rules).SelectMany(r => r.References).Count(r => r.Status != "resolved");
        notes.AddInformation($"CDF captured {files.Count(f => f.Status != "settings-uninterpreted")} configuration files and {files.Sum(f => f.Rules.Count)} uninterpreted rules; " +
            $"{files.Count(f => f.Status == "malformed-json")} JSON inventory failures and {files.Sum(f => f.Rules.Sum(r => r.Diagnostics.Count))} rule diagnostics. See the captured file/rule provenance.");
        // The capture summary is information; nonzero rule diagnostics still need attention.
        var ruleDiagnostics = files.Sum(f => f.Rules.Sum(r => r.Diagnostics.Count));
        if (ruleDiagnostics > 0) notes.Add($"CDF: {ruleDiagnostics} rule diagnostics; see the captured file/rule provenance.");
        if (unresolved > 0) notes.Add($"CDF: {unresolved} unresolved input references; no missing plugin is added as a master.");
        var identity = $"cdf-capture-v2\n{Source}\n{status}\n{version}\n{dllHash}\n" +
            string.Join('\n', files.Select(f => $"{f.Path}|{f.Sha256}|{f.Status}"));
        return new(status, version, dllHash, Source, FrameworkFiles.Hash(Encoding.UTF8.GetBytes(identity)), files.AsReadOnly(), notes.AsReadOnly())
        { Information = notes.Information, Settings = settings };

        void CaptureFile(string path, bool json)
        {
            var relative = Path.GetRelativePath(plugins, path).Replace('\\', '/');
            byte[]? content = null;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("File link was not followed: " + relative);
                content = File.ReadAllBytes(path);
                using var parsing = PhaseTiming.Measure("cdf.parse");
                var fileStatus = "settings-uninterpreted";
                var rules = json ? ReadRules(content, resolver, out fileStatus) : Array.Empty<CdfRule>();
                if (!json) settings = DescribeSettings(content);
                files.Add(new(relative, FrameworkFiles.Hash(content), fileStatus, rules));
            }
            catch (JsonException ex)
            {
                files.Add(new(relative, FrameworkFiles.Hash(content!), "malformed-json", Array.Empty<CdfRule>()));
                notes.Add($"CDF {relative}: JSON inventory failed: {ex.Message}. Native Reader may consume a partial document; this is not evidence that the file is inactive.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                incomplete = true;
                files.Add(new(relative, "", "unreadable", Array.Empty<CdfRule>()));
                notes.Add($"CDF {relative}: {ex.Message}");
            }
        }
    }

    // The maintainer accepts every build with the pinned semantics: the
    // verified major silently, any other or unreadable major with a warning.
    internal static readonly FrameworkRelease Release = new("3.1.0.0", SourceMatchedDll, 3, "3.1.0");

    private static CdfInputReport Empty() => new("absent", "", "", Source, "", Array.Empty<CdfFile>(), Array.Empty<string>());

    private static IReadOnlyList<CdfRule> ReadRules(byte[] bytes, NativeFormResolver resolver, out string status)
    {
        // JsonCpp accepts comments. Raw bytes remain available even where this
        // diagnostic reader cannot represent the native parser's permissiveness.
        var content = bytes.AsMemory();
        if (content.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) content = content[3..];
        using var document = JsonDocument.Parse(content, new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        status = "captured-uninterpreted";
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
        {
            status = "non-rule-document";
            return Array.Empty<CdfRule>();
        }
        var result = new List<CdfRule>();
        var stopped = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var notes = new List<string>();
            var refs = new List<CdfReference>();
            string? name = null;
            if (rule.ValueKind != JsonValueKind.Object) notes.Add("Rule is not an object.");
            else
            {
                Unknown(rule, ["friendlyName", "conditions", "changes"], "rule", notes);
                if (rule.TryGetProperty("friendlyName", out var label) && label.ValueKind == JsonValueKind.String) name = label.GetString();
                else notes.Add("Missing or non-string friendlyName.");
                if (rule.TryGetProperty("conditions", out var conditions))
                {
                    if (conditions.ValueKind != JsonValueKind.Object) notes.Add("Conditions are not an object; preserved without interpretation.");
                    else
                    {
                        Unknown(conditions, Conditions, "conditions", notes);
                        foreach (var field in conditions.EnumerateObject())
                        {
                            var key = field.Name.TrimStart('!');
                            var expected = key switch
                            {
                                "containers" => typeof(IContainerGetter), "references" => typeof(IPlacedGetter),
                                "locations" => typeof(ILocationGetter), "worldspaces" => typeof(IWorldspaceGetter),
                                "locationKeywords" => typeof(IKeywordGetter), "globals" => typeof(IGlobalGetter), _ => null
                            };
                            if (expected is not null) References(field.Value, "conditions." + field.Name, expected,
                                key is "locationKeywords" or "globals", key == "references", key == "globals");
                            else if (key == "plugins" && field.Value.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var plugin in field.Value.EnumerateArray())
                                    if (plugin.ValueKind == JsonValueKind.String && !resolver.HasPlugin(plugin.GetString()!))
                                        notes.Add("Required plugin is not loaded: " + plugin.GetString() + ". Pinned native source returns from this file, skipping its remaining rules.");
                            }
                            else if (key == "questConditions" && field.Value.ValueKind == JsonValueKind.Object &&
                                field.Value.TryGetProperty("questID", out var quest) && quest.ValueKind == JsonValueKind.String)
                                refs.Add(ResolveReference(resolver, "conditions.questConditions.questID", quest.GetString()!, typeof(IQuestGetter), true, false));
                        }
                    }
                }
                if (!rule.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                    notes.Add("Missing or non-array changes.");
                else
                {
                    var index = 0;
                    foreach (var change in changes.EnumerateArray())
                    {
                        var path = $"changes[{index++}]";
                        if (change.ValueKind != JsonValueKind.Object) { notes.Add(path + " is not an object."); continue; }
                        Unknown(change, ["add", "remove", "removeByKeywords", "count"], path, notes);
                        foreach (var field in change.EnumerateObject())
                            if (field.Name == "remove")
                            {
                                if (field.Value.ValueKind == JsonValueKind.String)
                                    refs.Add(ResolveReference(resolver, path + ".remove", field.Value.GetString()!, typeof(IItemGetter), false, false));
                                else notes.Add(path + ".remove is not a string.");
                            }
                            else if (field.Name is "add" or "removeByKeywords")
                                References(field.Value, path + "." + field.Name,
                                    field.Name == "removeByKeywords" ? typeof(IKeywordGetter) : typeof(IItemGetter), false, false, false);
                    }
                }
            }
            var native = DescribeNative(rule, resolver, refs, notes, ref stopped);
            result.Add(new(result.Count, name, "unsupported-effect", rule.GetRawText(), refs.AsReadOnly(), notes.AsReadOnly())
                { NativeDisposition = native.Disposition, Guards = native.Guards, Changes = native.Changes });

            void References(JsonElement values, string path, Type expected, bool editorIdOnly, bool formIdOnly, bool valuePair)
            {
                if (values.ValueKind != JsonValueKind.Array) { notes.Add(path + " is not an array."); return; }
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String) { notes.Add(path + " contains a non-string identifier."); continue; }
                    var identifier = value.GetString()!;
                    refs.Add(ResolveReference(resolver, path, identifier, expected, editorIdOnly, formIdOnly, valuePair));
                }
            }
        }
        return result.AsReadOnly();
    }

    private static void Unknown(JsonElement value, IEnumerable<string> known, string path, List<string> notes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            if (!seen.Add(field.Name)) notes.Add($"Duplicate field {path}.{field.Name}; pinned JsonCpp uses the last occurrence. Raw provenance retained; no partial rule is imported.");
            if (!known.Contains(field.Name, StringComparer.Ordinal)) notes.Add($"Unknown field {path}.{field.Name}; no partial rule is imported.");
        }
    }

    // CDF reference spellings: "0xHEX|Plugin" (the ID is masked to the plugin's local range) or an EditorID matched with case
    // against the winning record's own name.
    private static CdfReference ResolveReference(NativeFormResolver resolver, string field, string identifier, Type expected,
        bool editorIdOnly, bool formIdOnly, bool valuePair = false)
    {
        CdfReference Result(string status, IMajorRecordGetter? record = null) => new(field, identifier, status, record?.FormKey.ToString());
        var parts = identifier.Split('|');
        IMajorRecordGetter record;
        if (!editorIdOnly && parts.Length == 2)
        {
            if (!parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                !uint.TryParse(parts[0].AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id))
                return Result("malformed-identifier");
            var (failure, located) = FormLocator.Locate(resolver, parts[1], id, FormIdRange.Mask);
            if (failure is not null) return Result(failure);
            record = located!;
        }
        else
        {
            if (formIdOnly || valuePair && parts.Length != 2) return Result("malformed-identifier");
            // Only loaded winning records are evidence. Native EditorID cache
            // aliases/case behavior are not certified by this diagnostic index.
            var (status, found) = resolver.EditorId(valuePair ? parts[0] : identifier, caseSensitive: true);
            if (found is null) return Result(status);
            record = found;
        }
        if (record.IsDeleted) return Result("deleted-record");
        return expected.IsInstanceOfType(record) ? Result("resolved", record) : Result("wrong-type");
    }
}
