using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

// One mod's configured choices, grouped by the plugin that defines each record.
internal sealed class ExportedModSettings
{
    public required string Plugin { get; init; }
    public PatchPolicy? PatchPolicy { get; set; }
    public SortedDictionary<string, bool> ItemOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortedDictionary<string, string> MaterialFamilyOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortedDictionary<string, SpecializedLootMode> SpecializedLootOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortedDictionary<string, bool> EnchantmentOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int ChoiceCount => (PatchPolicy is null ? 0 : 1) + ItemOverrides.Count + MaterialFamilyOverrides.Count
        + SpecializedLootOverrides.Count + EnchantmentOverrides.Count;
}

// The mod-author rules file (docs/MOD-AUTHOR-RULES-FILE.md) as Export writes it.
internal sealed class RulesFileDocument
{
    public int SchemaVersion => AuthorRules.SchemaVersion;
    public required IReadOnlyList<RulesFilePlugin> Plugins { get; init; }
}

internal sealed class RulesFilePlugin
{
    public required string Plugin { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RulesFileDefaults? Defaults { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RulesFileItemRule>? Items { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RulesFileEnchantmentRule>? Enchantments { get; init; }
}

internal sealed class RulesFileDefaults
{
    public required PatchPolicy PatchPolicy { get; init; }
}

internal sealed class RulesFileItemRule
{
    public required string[] Records { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Include { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MaterialFamily { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SpecializedLootMode? DistributionMode { get; init; }
    // Left empty for the author to explain the choice to users.
    public string Reason { get; init; } = "";
}

internal sealed class RulesFileEnchantmentRule
{
    public required string[] Records { get; init; }
    public required bool Enabled { get; init; }
    public string Reason { get; init; } = "";
}

// Exports configured choices as a shippable rules file. Recommendations stay
// recommendations: only decided policies and explicit overrides are written.
internal static class ModSettingsExporter
{
    public static string SuggestedFileName(IEnumerable<string> selectedPlugins)
        => $"xep_{Path.GetFileNameWithoutExtension(selectedPlugins.Order(StringComparer.OrdinalIgnoreCase).First()).ToLowerInvariant()}.json";

    public static IReadOnlyList<ExportedModSettings> Available(ProfileSettings profile)
    {
        var mods = new SortedDictionary<string, ExportedModSettings>(StringComparer.OrdinalIgnoreCase);
        ExportedModSettings Mod(string name)
        {
            name = ModKey.FromFileName(name).FileName.String;
            if (!mods.TryGetValue(name, out var mod)) mods[name] = mod = new() { Plugin = name };
            return mod;
        }
        foreach (var (plugin, policy) in profile.Policies)
            if (policy != PatchPolicy.Review) Mod(plugin).PatchPolicy = SettingsStore.NormalizePolicy(policy);

        void Copy<T>(IReadOnlyDictionary<string, T> source, Func<ExportedModSettings, IDictionary<string, T>> target, Func<T, bool>? offered = null)
        {
            foreach (var (key, value) in source)
            {
                if (offered is not null && !offered(value)) continue;
                var form = FormKey.Factory(key);
                target(Mod(form.ModKey.FileName.String)).Add(form.ToString(), value);
            }
        }
        Copy(profile.ItemOverrides, mod => mod.ItemOverrides);
        Copy(profile.MaterialFamilyOverrides, mod => mod.MaterialFamilyOverrides);
        // Retired distribution modes are not offered choices and cannot be shipped.
        Copy(profile.SpecializedLootOverrides, mod => mod.SpecializedLootOverrides, mode => SpecializedLootChoices.All.Any(c => c.Value == mode));
        Copy(profile.EnchantmentOverrides, mod => mod.EnchantmentOverrides);
        return mods.Values.ToArray();
    }

    public static RulesFileDocument Create(ProfileSettings profile, IEnumerable<string> selectedPlugins)
    {
        var selected = selectedPlugins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) throw new ArgumentException("Select at least one mod to export.");
        var mods = Available(profile).Where(mod => selected.Contains(mod.Plugin)).ToArray();
        var missing = selected.Except(mods.Select(mod => mod.Plugin), StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0) throw new ArgumentException("No configured settings to export for: " + string.Join(", ", missing));
        return new() { Plugins = mods.Select(ToRules).ToArray() };
    }

    // One item rule per record, carrying all of that record's choices.
    static RulesFilePlugin ToRules(ExportedModSettings mod)
    {
        var records = mod.ItemOverrides.Keys.Concat(mod.MaterialFamilyOverrides.Keys).Concat(mod.SpecializedLootOverrides.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var items = records.Select(record => new RulesFileItemRule
        {
            Records = [record],
            Include = mod.ItemOverrides.TryGetValue(record, out var include) ? include : null,
            MaterialFamily = mod.MaterialFamilyOverrides.GetValueOrDefault(record),
            DistributionMode = mod.SpecializedLootOverrides.TryGetValue(record, out var mode) ? mode : null,
        }).ToArray();
        var enchantments = mod.EnchantmentOverrides.Select(e => new RulesFileEnchantmentRule { Records = [e.Key], Enabled = e.Value }).ToArray();
        return new()
        {
            Plugin = mod.Plugin,
            Defaults = mod.PatchPolicy is { } policy ? new() { PatchPolicy = policy } : null,
            Items = items.Length > 0 ? items : null,
            Enchantments = enchantments.Length > 0 ? enchantments : null,
        };
    }

    public static string Serialize(RulesFileDocument document)
        => JsonSerializer.Serialize(document, RulesFileJsonContext.Default.RulesFileDocument);

    public static void Save(string path, RulesFileDocument document)
    {
        if (document.Plugins.Count == 0) throw new ArgumentException("Select at least one mod to export.");
        path = Path.GetFullPath(path);
        if (new[] { SettingsStore.PathName, SettingsStore.DefaultPathName, SettingsStore.LegacyPathName }
            .Any(settings => path.Equals(Path.GetFullPath(settings), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Choose a separate export file. The patcher's settings file cannot be replaced by a rules file.");
        // A file over the reader's limits would be ignored as a whole, so refuse it here.
        var json = Serialize(document);
        var records = document.Plugins.Sum(p => (p.Items?.Sum(r => r.Records.Length) ?? 0) + (p.Enchantments?.Sum(r => r.Records.Length) ?? 0));
        if (records > AuthorRules.MaxRecords || System.Text.Encoding.UTF8.GetByteCount(json) > AuthorRules.MaxFileBytes)
            throw new ArgumentException($"The selected mods have too many configured choices for one rules file (limits: {AuthorRules.MaxRecords:N0} records, {AuthorRules.MaxFileBytes / 1024} KiB). Export fewer mods.");
        AtomicFile.WriteText(path, json);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RulesFileDocument))]
internal partial class RulesFileJsonContext : JsonSerializerContext { }
