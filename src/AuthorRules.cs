using System.IO;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

// Mod-author rules file (docs/MOD-AUTHOR-RULES-FILE.md). Parsing and validation
// only: callers receive typed, fully valid rules plus diagnostics. Each rule is
// all-or-nothing, and nothing here changes selection, planning or output.

internal enum AuthorRuleSeverity { Info, Warning, Error }

internal sealed record AuthorRuleDiagnostic(AuthorRuleSeverity Severity, string Location, string Message)
{
    public override string ToString() => $"{Severity}: {Location}: {Message}";
}

internal sealed record AuthorItemRule(string Label, IReadOnlyList<FormKey> Records, bool? Include, string? MaterialFamily,
    SpecializedLootMode? DistributionMode, string? Reason, IReadOnlyList<FormKey>? AllowedEnchantments = null,
    IReadOnlyList<FormKey>? AllowedDestinations = null);

internal sealed record AuthorEnchantmentRule(string Label, IReadOnlyList<FormKey> Records, bool Enabled, string? Reason);

internal sealed record AuthorPluginRules(ModKey Plugin, PatchPolicy? PatchPolicy, SpecializedLootMode? DistributionMode,
    string? DefaultsReason, IReadOnlyList<AuthorItemRule> Items, IReadOnlyList<AuthorEnchantmentRule> Enchantments,
    string? SourceFile = null)
{
    public string DefaultsLabel => (SourceFile is null ? "" : SourceFile + ": ") + $"{Plugin.FileName} defaults";
}

internal sealed record AuthorRulesResult(bool Found, bool FileUsable, IReadOnlyList<AuthorPluginRules> Plugins,
    IReadOnlyList<AuthorRuleDiagnostic> Diagnostics, bool RecordsChecked)
{
    public int RuleCount => Plugins.Sum(p => p.Items.Count + p.Enchantments.Count);
    public bool HasErrors => Diagnostics.Any(d => d.Severity == AuthorRuleSeverity.Error);
}

// Destination: a leveled item list or container that can receive variants.
internal enum AuthorRecordKind { Missing, Equipment, Enchantment, Destination, Other }

// Record resolution against the loaded plugins. Optional: without it only the
// file's structure is checked.
internal interface IAuthorRecordLookup
{
    bool IsLoaded(ModKey plugin);
    AuthorRecordKind Kind(FormKey record);
}

internal static class AuthorRules
{
    public const int SchemaVersion = 1;
    const string DocumentLocation = "document";
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxRecords = 10_000;
    // Feature names a rule may list in "requires". v1 defines none.
    public const string AllowedEnchantmentsFeature = "allowedEnchantments";
    public const string AllowedDestinationsFeature = "allowedDestinations";
    static readonly HashSet<string> KnownFeatures = new(StringComparer.Ordinal) { AllowedEnchantmentsFeature, AllowedDestinationsFeature };

    static readonly System.Text.RegularExpressions.Regex FormKeyText = new(@"^[0-9A-Fa-f]{6}:[^:]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Isolated private GUI sessions only (SessionLaunch --author-rules-input): read the
    // rules file from the run folder instead of the Data folder.
    internal static string? PrivateInput { get; set; }
    public static string DirectoryIn(string dataFolder) => Path.Combine(dataFolder, "XEnchantmentPatcher");

    public static AuthorRulesResult LoadInstalled(string dataFolder, IAuthorRecordLookup? lookup)
    {
        // A private validation input replaces installed rules, including discovery.
        if (PrivateInput is { } path) return LoadFiles([path], lookup);
        var folder = DirectoryIn(dataFolder);
        return LoadFiles(Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(p => Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase))
            : [], lookup);
    }

    internal static AuthorRulesResult LoadFiles(IEnumerable<string> paths, IAuthorRecordLookup? lookup)
    {
        var files = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).Select(path =>
            {
                AuthorRulesResult result;
                try { result = Load(path, lookup, resolveConflicts: false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { result = Unusable(lookup, $"The rules file could not be read: {ex.Message}"); }
                return (Path: path, Result: result with
                {
                    Plugins = result.Plugins.Select(p => p with
                    {
                        SourceFile = path,
                        Items = p.Items.Select(r => r with { Label = path + ": " + r.Label }).ToArray(),
                        Enchantments = p.Enchantments.Select(r => r with { Label = path + ": " + r.Label }).ToArray(),
                    }).ToArray(),
                    Diagnostics = result.Diagnostics.Select(d => d with { Location = path + ": " + d.Location }).ToArray(),
                });
            }).ToArray();
        var conflicts = new List<AuthorRuleDiagnostic>();
        var plugins = RemoveConflicts(files.SelectMany(f => f.Result.Plugins).ToList(),
            (severity, location, message) => conflicts.Add(new(severity, location, message)));
        var diagnostics = files.SelectMany(f =>
        {
            var filePlugins = plugins.Where(p => p.SourceFile == f.Path).ToArray();
            var fileDiagnostics = f.Result.Diagnostics.Concat(conflicts.Where(d => d.Location.StartsWith(f.Path + ": ", StringComparison.Ordinal))).ToList();
            if (f.Result.FileUsable)
                Summarize(filePlugins, f.Result.RecordsChecked,
                    (severity, location, message) => fileDiagnostics.Add(new(severity, f.Path + ": " + location, message)));
            return fileDiagnostics;
        }).ToArray();
        return new(files.Any(f => f.Result.Found), files.Any(f => f.Result.FileUsable), plugins,
            diagnostics, lookup is not null);
    }

    public static AuthorRulesResult Load(string path, IAuthorRecordLookup? lookup) => Load(path, lookup, resolveConflicts: true);

    static AuthorRulesResult Load(string path, IAuthorRecordLookup? lookup, bool resolveConflicts)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return new(false, false, [], [new(AuthorRuleSeverity.Info, DocumentLocation, "No rules file is installed.")], lookup is not null);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return Unusable(lookup, "The rules file is a link or reparse point; only a regular file is read.");
        if (info.Length > MaxFileBytes)
            return Unusable(lookup, $"The rules file is larger than {MaxFileBytes / 1024} KiB and is not used.");
        return Parse(File.ReadAllBytes(path), lookup, resolveConflicts);
    }

    static AuthorRulesResult Unusable(IAuthorRecordLookup? lookup, string message)
        => new(true, false, [], [new(AuthorRuleSeverity.Error, DocumentLocation, message)], lookup is not null);

    public static AuthorRulesResult Parse(ReadOnlySpan<byte> bytes, IAuthorRecordLookup? lookup) => Parse(bytes, lookup, resolveConflicts: true);

    static AuthorRulesResult Parse(ReadOnlySpan<byte> bytes, IAuthorRecordLookup? lookup, bool resolveConflicts)
    {
        if (bytes.Length > MaxFileBytes)
            return Unusable(lookup, $"The rules file is larger than {MaxFileBytes / 1024} KiB and is not used.");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
        }
        catch (JsonException ex)
        {
            return Unusable(lookup, $"The file is not valid JSON ({ex.Message}). The whole file is not used.");
        }
        using (document)
            return new Reader(lookup, resolveConflicts).Read(document.RootElement);
    }

    // Two valid rules setting the same field of the same record differently
    // are both invalid; agreeing rules are kept.
    static List<AuthorPluginRules> RemoveConflicts(List<AuthorPluginRules> entries, Action<AuthorRuleSeverity, string, string> add)
    {
        var result = new List<AuthorPluginRules>();
        foreach (var groupEntries in entries.GroupBy(e => e.Plugin))
        {
            var groupPlugins = groupEntries.ToArray();
            var items = groupPlugins.SelectMany(p => p.Items).ToArray();
            var enchantments = groupPlugins.SelectMany(p => p.Enchantments).ToArray();
            var conflictingPolicies = groupPlugins.Where(p => p.PatchPolicy is not null).Select(p => p.PatchPolicy).Distinct().Count() > 1;
            var conflictingModes = groupPlugins.Where(p => p.DistributionMode is not null).Select(p => p.DistributionMode).Distinct().Count() > 1;
            var bad = new HashSet<object>(ReferenceEqualityComparer.Instance);
            void Check<TRule>(IEnumerable<TRule> rules, string field, Func<TRule, object?> value, Func<TRule, IReadOnlyList<FormKey>> records, Func<TRule, string> label) where TRule : class
            {
                foreach (var group in rules.SelectMany(r => value(r) is { } v ? records(r).Select(k => (Key: k, Value: v, Rule: r)) : [])
                             .GroupBy(x => x.Key))
                {
                    if (group.Select(x => x.Value).Distinct().Count() < 2) continue;
                    var rules2 = group.Select(x => x.Rule).Distinct(ReferenceEqualityComparer.Instance).Cast<TRule>().ToArray();
                    foreach (var rule in rules2)
                        if (bad.Add(rule))
                            add(AuthorRuleSeverity.Error, label(rule), $"Another installed rule sets \"{field}\" of {group.Key} to a different value. Both rules are not used.");
                }
            }
            Check(items, "include", r => r.Include, r => r.Records, r => r.Label);
            Check(items, "materialFamily", r => r.MaterialFamily?.ToUpperInvariant(), r => r.Records, r => r.Label);
            Check(items, "distributionMode", r => r.DistributionMode, r => r.Records, r => r.Label);
            static string? ListValue(IReadOnlyList<FormKey>? list) => list is null ? null
                : string.Join(",", list.Select(k => k.ToString().ToUpperInvariant()).Order(StringComparer.Ordinal).Distinct());
            Check(items, AllowedEnchantmentsFeature, r => ListValue(r.AllowedEnchantments), r => r.Records, r => r.Label);
            Check(items, AllowedDestinationsFeature, r => ListValue(r.AllowedDestinations), r => r.Records, r => r.Label);
            Check(enchantments, "enabled", r => (object)r.Enabled, r => r.Records, r => r.Label);
            foreach (var entry in groupPlugins)
            {
                var conflictingDefaults = (conflictingPolicies && entry.PatchPolicy is not null)
                    || (conflictingModes && entry.DistributionMode is not null);
                if (conflictingDefaults)
                    add(AuthorRuleSeverity.Error, entry.DefaultsLabel, "Installed files set different plugin defaults. The conflicting default blocks are not used.");
                result.Add(entry with
                {
                    PatchPolicy = conflictingDefaults ? null : entry.PatchPolicy,
                    DistributionMode = conflictingDefaults ? null : entry.DistributionMode,
                    Items = entry.Items.Where(r => !bad.Contains(r)).ToArray(),
                    Enchantments = entry.Enchantments.Where(r => !bad.Contains(r)).ToArray(),
                });
            }
        }
        return result;
    }

    static void Summarize(IReadOnlyList<AuthorPluginRules> plugins, bool recordsChecked, Action<AuthorRuleSeverity, string, string> add)
    {
        var rules = plugins.Sum(p => p.Items.Count + p.Enchantments.Count);
        var withDefaults = plugins.Count(p => p.PatchPolicy is not null || p.DistributionMode is not null);
        add(AuthorRuleSeverity.Info, DocumentLocation,
            $"{plugins.Count} loaded plugin entr{(plugins.Count == 1 ? "y" : "ies")} ({withDefaults} with defaults) and {rules} valid rule{(rules == 1 ? "" : "s")}."
            + (!recordsChecked ? " Records were not checked against a load order; scan first to check them." : ""));
    }

    sealed class Reader(IAuthorRecordLookup? lookup, bool resolveConflicts)
    {
        readonly List<AuthorRuleDiagnostic> diagnostics = [];
        int recordCount;

        void Add(AuthorRuleSeverity severity, string location, string message) => diagnostics.Add(new(severity, location, message));

        AuthorRulesResult Done(bool usable, IReadOnlyList<AuthorPluginRules> plugins)
            => new(true, usable, plugins, diagnostics, lookup is not null);

        public AuthorRulesResult Read(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                Add(AuthorRuleSeverity.Error, DocumentLocation, "The file must contain a JSON object. The whole file is not used.");
                return Done(false, []);
            }
            var props = Properties(root, DocumentLocation, ["schemaVersion", "plugins"], out var duplicate);
            if (duplicate)
            {
                Add(AuthorRuleSeverity.Error, DocumentLocation, "Top-level properties are repeated. The whole file is not used.");
                return Done(false, []);
            }
            if (!props.TryGetValue("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
            {
                Add(AuthorRuleSeverity.Error, DocumentLocation, "\"schemaVersion\" is missing or not an integer. The whole file is not used.");
                return Done(false, []);
            }
            if (number != SchemaVersion)
            {
                Add(AuthorRuleSeverity.Error, DocumentLocation, number > SchemaVersion
                    ? $"schemaVersion {number} needs a newer XEnchantmentPatcher (this version reads {SchemaVersion}). The whole file is not used."
                    : $"schemaVersion {number} is not supported. The whole file is not used.");
                return Done(false, []);
            }
            if (!props.TryGetValue("plugins", out var pluginArray) || pluginArray.ValueKind != JsonValueKind.Array)
            {
                Add(AuthorRuleSeverity.Error, DocumentLocation, "\"plugins\" is missing or not an array. The whole file is not used.");
                return Done(false, []);
            }

            var entries = new List<AuthorPluginRules>();
            var seen = new HashSet<ModKey>();
            var index = 0;
            foreach (var entry in pluginArray.EnumerateArray())
            {
                var location = $"plugins[{index++}]";
                var parsed = ReadPlugin(entry, location, seen);
                if (parsed is not null) entries.Add(parsed);
            }
            if (recordCount > MaxRecords)
            {
                Add(AuthorRuleSeverity.Error, DocumentLocation, $"The file lists {recordCount} records; the limit is {MaxRecords}. The whole file is not used.");
                return Done(false, []);
            }
            var resolved = resolveConflicts ? RemoveConflicts(entries, Add) : entries;
            if (resolveConflicts) Summarize(resolved, lookup is not null, Add);
            return Done(true, resolved);
        }

        AuthorPluginRules? ReadPlugin(JsonElement entry, string location, HashSet<ModKey> seen)
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                Add(AuthorRuleSeverity.Error, location, "A plugin entry must be an object. The entry is not used.");
                return null;
            }
            var props = Properties(entry, location, ["plugin", "defaults", "items", "enchantments"], out var duplicate);
            if (!props.TryGetValue("plugin", out var pluginValue) || pluginValue.ValueKind != JsonValueKind.String
                || !ModKey.TryFromFileName(pluginValue.GetString()!, out var plugin) || plugin.IsNull)
            {
                Add(AuthorRuleSeverity.Error, location, "\"plugin\" must be a plugin file name such as ExampleGear.esp. The entry is not used.");
                return null;
            }
            location = plugin.FileName;
            if (duplicate)
            {
                Add(AuthorRuleSeverity.Error, location, "Properties are repeated in this plugin entry. The entry is not used.");
                return null;
            }
            if (!seen.Add(plugin))
            {
                Add(AuthorRuleSeverity.Error, location, "This plugin appears in more than one entry. The repeated entry is not used.");
                return null;
            }
            var dormant = lookup is not null && !lookup.IsLoaded(plugin);
            if (dormant) Add(AuthorRuleSeverity.Warning, location, "The plugin is not loaded. Its entry is dormant.");

            PatchPolicy? policy = null;
            SpecializedLootMode? mode = null;
            string? reason = null;
            var defaultsValid = true;
            if (props.TryGetValue("defaults", out var defaults))
                defaultsValid = ReadDefaults(defaults, $"{location} defaults", out policy, out mode, out reason);

            var items = new List<AuthorItemRule>();
            var enchantments = new List<AuthorEnchantmentRule>();
            if (props.TryGetValue("items", out var itemArray))
                ReadRules(itemArray, location, "items", el => ReadItem(el, location, plugin, dormant), items);
            if (props.TryGetValue("enchantments", out var enchArray))
                ReadRules(enchArray, location, "enchantments", el => ReadEnchantment(el, location, plugin, dormant), enchantments);
            if (dormant) return null;
            return new(plugin, defaultsValid ? policy : null, defaultsValid ? mode : null, defaultsValid ? reason : null, items, enchantments);
        }

        bool ReadDefaults(JsonElement defaults, string location, out PatchPolicy? policy, out SpecializedLootMode? mode, out string? reason)
        {
            policy = null; mode = null; reason = null;
            if (defaults.ValueKind != JsonValueKind.Object)
            {
                Add(AuthorRuleSeverity.Error, location, "\"defaults\" must be an object. The defaults are not used.");
                return false;
            }
            var props = Properties(defaults, location, ["patchPolicy", "distributionMode", "reason"], out var duplicate);
            var valid = !duplicate;
            if (duplicate) Add(AuthorRuleSeverity.Error, location, "Properties are repeated. The defaults are not used.");
            if (props.TryGetValue("patchPolicy", out var p))
            {
                // The offered policies, minus Review: leaving the field out keeps the plugin undecided.
                var offered = PolicyValues.All.Select(c => c.Value).Where(v => v != PatchPolicy.Review).ToArray();
                var chosen = p.ValueKind == JsonValueKind.String ? offered.Where(v => v.ToString() == p.GetString()).Cast<PatchPolicy?>().FirstOrDefault() : null;
                if (chosen is not null) policy = chosen;
                else { valid = false; Add(AuthorRuleSeverity.Error, location, $"\"patchPolicy\" must be {string.Join(" or ", offered)}, not {Raw(p)}. The defaults are not used."); }
            }
            if (props.TryGetValue("distributionMode", out var m))
            {
                if (TryMode(m, out var parsed)) mode = parsed;
                else { valid = false; Add(AuthorRuleSeverity.Error, location, $"\"distributionMode\" {Raw(m)} is not a distribution mode. The defaults are not used."); }
            }
            if (props.TryGetValue("reason", out var r))
            {
                if (r.ValueKind == JsonValueKind.String) reason = r.GetString();
                else { valid = false; Add(AuthorRuleSeverity.Error, location, "\"reason\" must be text. The defaults are not used."); }
            }
            return valid;
        }

        void ReadRules<T>(JsonElement array, string location, string name, Func<JsonElement, T?> read, List<T> target) where T : class
        {
            if (array.ValueKind != JsonValueKind.Array)
            {
                Add(AuthorRuleSeverity.Error, $"{location} {name}", $"\"{name}\" must be an array. Its rules are not used.");
                return;
            }
            foreach (var element in array.EnumerateArray())
                if (read(element) is { } rule) target.Add(rule);
        }

        // Shared rule fields; returns null when the rule is invalid or dormant.
        (string Label, List<FormKey> Records, Dictionary<string, JsonElement> Props)? ReadCommon(JsonElement element, string location, string kind,
            ModKey plugin, bool dormant, string[] settings, AuthorRecordKind expected)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Add(AuthorRuleSeverity.Error, $"{location} {kind}", "A rule must be an object. The rule is not used.");
                return null;
            }
            string[] known = ["id", "reason", "requires", "records", .. settings];
            // Unknown-property warnings need a label, so read id/records before reporting.
            var props = Properties(element, null, known, out var duplicate);
            var id = props.TryGetValue("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
            var records = new List<FormKey>();
            var recordsValid = props.TryGetValue("records", out var recordArray) && recordArray.ValueKind == JsonValueKind.Array && recordArray.GetArrayLength() > 0;
            if (recordsValid)
                foreach (var r in recordArray.EnumerateArray())
                {
                    recordCount++;
                    if (r.ValueKind == JsonValueKind.String && FormKeyText.IsMatch(r.GetString()!) && FormKey.TryFactory(r.GetString()!, out var key) && !key.IsNull) records.Add(key);
                    else recordsValid = false;
                }
            var label = $"{location} {kind} " + (id is { Length: > 0 } ? $"\"{id}\"" : records.Count > 0 ? records[0].ToString() : "(unnamed)");
            WarnUnknown(element, label, known);

            var errors = new List<string>();
            if (duplicate) errors.Add("properties are repeated");
            if (props.ContainsKey("id") && id is not { Length: > 0 }) errors.Add("\"id\" must be non-empty text");
            if (props.TryGetValue("reason", out var reasonValue) && reasonValue.ValueKind != JsonValueKind.String) errors.Add("\"reason\" must be text");
            if (!recordsValid) errors.Add("\"records\" must be a non-empty array of FormKeys such as 000800:ExampleGear.esp");
            if (!settings.Any(props.ContainsKey)) errors.Add("the rule sets nothing (" + string.Join(", ", settings.Select(s => $"\"{s}\"")) + ")");
            if (props.TryGetValue("requires", out var requires))
            {
                if (requires.ValueKind != JsonValueKind.Array || requires.EnumerateArray().Any(f => f.ValueKind != JsonValueKind.String))
                    errors.Add("\"requires\" must be an array of feature names");
                else
                {
                    var unknown = requires.EnumerateArray().Select(f => f.GetString()!).Where(f => !KnownFeatures.Contains(f)).ToArray();
                    if (unknown.Length > 0 && errors.Count == 0 && !dormant)
                    {
                        Add(AuthorRuleSeverity.Warning, label, $"Needs a newer XEnchantmentPatcher (unknown feature {string.Join(", ", unknown)}). The rule is dormant.");
                        return null;
                    }
                }
            }
            foreach (var record in records)
            {
                if (record.ModKey != plugin) { errors.Add($"{record} is not defined by {plugin.FileName}"); continue; }
                if (lookup is null || dormant) continue;
                var kindFound = lookup.Kind(record);
                if (kindFound == AuthorRecordKind.Missing) errors.Add($"{record} does not exist in the loaded plugins");
                else if (kindFound != expected) errors.Add($"{record} is not {(expected == AuthorRecordKind.Equipment ? "armor or a weapon" : "an enchantment")}");
            }
            if (errors.Count > 0)
            {
                Add(AuthorRuleSeverity.Error, label, Capitalize(string.Join("; ", errors)) + ". The rule is not used.");
                return null;
            }
            // A dormant entry's rules are still checked for structure, but never applied.
            return dormant ? null : (label, records, props);
        }

        AuthorItemRule? ReadItem(JsonElement element, string location, ModKey plugin, bool dormant)
        {
            if (ReadCommon(element, location, "item rule", plugin, dormant, ["include", "materialFamily", "distributionMode", AllowedEnchantmentsFeature, AllowedDestinationsFeature], AuthorRecordKind.Equipment) is not var (label, records, props))
                return null;
            var errors = new List<string>();
            bool? include = null;
            if (props.TryGetValue("include", out var inc))
            {
                if (inc.ValueKind is JsonValueKind.True or JsonValueKind.False) include = inc.GetBoolean();
                else errors.Add("\"include\" must be true or false");
            }
            string? material = null;
            if (props.TryGetValue("materialFamily", out var mat))
            {
                if (mat.ValueKind == JsonValueKind.String && mat.GetString() is { Length: > 0 } family)
                    material = family;
                else errors.Add("\"materialFamily\" must be non-empty text");
            }
            SpecializedLootMode? mode = null;
            if (props.TryGetValue("distributionMode", out var m))
            {
                if (TryMode(m, out var parsed)) mode = parsed;
                else errors.Add($"\"distributionMode\" {Raw(m)} is not a distribution mode");
            }
            var allowed = FeatureList(props, AllowedEnchantmentsFeature, AuthorRecordKind.Enchantment, "an enchantment", errors);
            var destinations = FeatureList(props, AllowedDestinationsFeature, AuthorRecordKind.Destination, "a leveled item list or container", errors);
            if (errors.Count > 0)
            {
                Add(AuthorRuleSeverity.Error, label, Capitalize(string.Join("; ", errors)) + ". The rule is not used.");
                return null;
            }
            return new(label, records, include, material, mode, Reason(props), allowed, destinations);
        }

        // A narrowing list field (any plugin's records of one kind). It must be declared in
        // "requires" so an older reader leaves the rule dormant instead of dropping the list.
        List<FormKey>? FeatureList(Dictionary<string, JsonElement> props, string feature, AuthorRecordKind kind, string noun, List<string> errors)
        {
            if (!props.TryGetValue(feature, out var list)) return null;
            if (!(props.TryGetValue("requires", out var declared) && declared.ValueKind == JsonValueKind.Array
                && declared.EnumerateArray().Any(f => f.ValueKind == JsonValueKind.String && f.GetString() == feature)))
                errors.Add($"a rule using \"{feature}\" must list it in \"requires\"");
            var result = new List<FormKey>();
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                errors.Add($"\"{feature}\" must be a non-empty array of FormKeys");
                return result;
            }
            foreach (var entry in list.EnumerateArray())
            {
                recordCount++;
                if (entry.ValueKind != JsonValueKind.String || !FormKeyText.IsMatch(entry.GetString()!) || !FormKey.TryFactory(entry.GetString()!, out var key) || key.IsNull)
                    errors.Add($"{Raw(entry)} in \"{feature}\" is not a FormKey");
                else if (lookup is not null && lookup.Kind(key) is var found && found != kind)
                    errors.Add(found == AuthorRecordKind.Missing ? $"{key} does not exist in the loaded plugins" : $"{key} is not {noun}");
                else result.Add(key);
            }
            return result;
        }

        AuthorEnchantmentRule? ReadEnchantment(JsonElement element, string location, ModKey plugin, bool dormant)
        {
            if (ReadCommon(element, location, "enchantment rule", plugin, dormant, ["enabled"], AuthorRecordKind.Enchantment) is not var (label, records, props))
                return null;
            var enabled = props["enabled"];
            if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                Add(AuthorRuleSeverity.Error, label, "\"enabled\" must be true or false. The rule is not used.");
                return null;
            }
            return new(label, records, enabled.GetBoolean(), Reason(props));
        }

        Dictionary<string, JsonElement> Properties(JsonElement obj, string? location, string[] known, out bool duplicate)
        {
            var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            duplicate = false;
            foreach (var p in obj.EnumerateObject())
                if (!props.TryAdd(p.Name, p.Value) && known.Contains(p.Name)) duplicate = true;
            if (location is not null) WarnUnknown(obj, location, known);
            return props;
        }

        void WarnUnknown(JsonElement obj, string location, string[] known)
        {
            foreach (var name in obj.EnumerateObject().Select(p => p.Name).Distinct().Where(n => !known.Contains(n)))
                Add(AuthorRuleSeverity.Warning, location, $"Unknown property \"{name}\" is ignored (a typo, or a field from a newer version).");
        }

        static string? Reason(Dictionary<string, JsonElement> props) => props.TryGetValue("reason", out var r) ? r.GetString() : null;

        static bool TryMode(JsonElement value, out SpecializedLootMode mode)
        {
            // Exactly the patcher's offered distribution choices; retired values are not offered.
            var name = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var match = SpecializedLootChoices.All.FirstOrDefault(c => c.Value.ToString() == name);
            mode = match?.Value ?? default;
            return match is not null;
        }

        static string Raw(JsonElement value)
        {
            var text = value.GetRawText();
            return text.Length <= 60 ? text : text[..57] + "...";
        }

        static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
