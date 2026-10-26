using System.Reflection;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

// Mod-author defaults (docs/MOD-AUTHOR-RULES-FILE.md) as an overlay on the saved
// profile. Author values fill only choices the user has not saved; the overlay is
// rebuilt for every run and never written to settings.

internal sealed record AuthorValue<T>(T Value, string Rule, string? Reason)
{
    public string Describe(string field) => $"Author default ({Rule}): {field} = {Value}" + (string.IsNullOrWhiteSpace(Reason) ? "" : $". {Reason}");
}

internal sealed record AuthorConflict(string Field, string Key, string Target, string AuthorValue, string UserValue, string? Reason)
{
    // Includes both values, so a changed author or user value asks again.
    public string AnswerKey => $"{Field}|{Key}|{AuthorValue}|{UserValue}";
    // Author include under a plugin the user excluded: the user's value is the plugin
    // policy, which other items share, so accepting the author value is remembered
    // instead of deleting the user's choice.
    public bool CrossLevel => Field == AuthorDefaults.IncludeUnderExclude;
}

internal sealed class AuthorOverlay
{
    public required AuthorRulesResult Rules { get; init; }
    public Dictionary<string, AuthorValue<PatchPolicy>> Policies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuthorValue<bool>> Include { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuthorValue<string>> Material { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuthorValue<SpecializedLootMode>> Mode { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuthorValue<bool>> Enchantment { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuthorValue<FormKey[]>> EnchantmentLists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuthorValue<FormKey[]>> DestinationLists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsEmpty => Policies.Count + Include.Count + Material.Count + Mode.Count + Enchantment.Count + EnchantmentLists.Count + DestinationLists.Count == 0;
}

// The author values actually applied in one run, for provenance in Scan rows.
internal sealed class AuthorApplication
{
    public required ProfileSettings User { get; init; }
    public Dictionary<string, AuthorValue<PatchPolicy>> Policies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuthorValue<bool>> Include { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuthorValue<string>> Material { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuthorValue<SpecializedLootMode>> Mode { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuthorValue<bool>> Enchantment { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuthorValue<FormKey[]>> EnchantmentLists { get; } = new(StringComparer.OrdinalIgnoreCase);
    // Items whose list the user ignores (shown, not applied).
    public Dictionary<string, AuthorValue<FormKey[]>> IgnoredEnchantmentLists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuthorValue<FormKey[]>> DestinationLists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuthorValue<FormKey[]>> IgnoredDestinationLists { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Review rows take the author's plugin policy, as the effective profile does.
    public IReadOnlyDictionary<string, PatchPolicy> EffectivePolicies(IReadOnlyDictionary<string, PatchPolicy> policies)
    {
        var result = new Dictionary<string, PatchPolicy>(policies, StringComparer.OrdinalIgnoreCase);
        foreach (var (plugin, value) in Policies)
            if (!result.TryGetValue(plugin, out var policy) || policy == PatchPolicy.Review) result[plugin] = value.Value;
        return result;
    }
}

internal static class AuthorDefaults
{
    public const string KeepAnswer = "keep";
    public const string AuthorAnswer = "author";
    public const string IncludeUnderExclude = "includeUnderExcludedPlugin";

    // Profile fields the overlay fills. Every other field is copied back after a run.
    static readonly string[] OverlaidFields =
    [
        nameof(ProfileSettings.Policies), nameof(ProfileSettings.ItemOverrides), nameof(ProfileSettings.MaterialFamilyOverrides),
        nameof(ProfileSettings.SpecializedLootOverrides), nameof(ProfileSettings.EnchantmentOverrides),
    ];
    internal static IReadOnlyList<string> Overlaid => OverlaidFields;

    public static AuthorOverlay Build(AuthorRulesResult rules, IReadOnlyList<ISkyrimModGetter> mods)
    {
        var overlay = new AuthorOverlay { Rules = rules };
        foreach (var plugin in rules.Plugins)
        {
            var name = plugin.Plugin.FileName.String;
            if (plugin.PatchPolicy is { } policy) overlay.Policies[name] = new(policy, plugin.DefaultsLabel, plugin.DefaultsReason);
            if (plugin.DistributionMode is { } mode)
            {
                // A plugin-wide mode covers the equipment the plugin defines.
                var mod = mods.FirstOrDefault(m => m.ModKey == plugin.Plugin);
                var records = mod is null ? [] : mod.Armors.Select(r => r.FormKey).Concat(mod.Weapons.Select(r => r.FormKey))
                    .Where(k => k.ModKey == plugin.Plugin);
                foreach (var record in records) overlay.Mode[record.ToString()] = new(mode, plugin.DefaultsLabel, plugin.DefaultsReason);
            }
        }
        // Exact rules override every file's plugin defaults, independent of filename.
        foreach (var plugin in rules.Plugins)
        {
            // Item rules override the plugin defaults.
            foreach (var rule in plugin.Items)
                foreach (var record in rule.Records.Select(r => r.ToString()))
                {
                    if (rule.Include is { } include) overlay.Include[record] = new(include, rule.Label, rule.Reason);
                    if (rule.MaterialFamily is { } family) overlay.Material[record] = new(family, rule.Label, rule.Reason);
                    if (rule.DistributionMode is { } itemMode) overlay.Mode[record] = new(itemMode, rule.Label, rule.Reason);
                    if (rule.AllowedEnchantments is { } allowed) overlay.EnchantmentLists[record] = new(allowed.ToArray(), rule.Label, rule.Reason);
                    if (rule.AllowedDestinations is { } destinations) overlay.DestinationLists[record] = new(destinations.ToArray(), rule.Label, rule.Reason);
                }
            foreach (var rule in plugin.Enchantments)
                foreach (var record in rule.Records) overlay.Enchantment[record.ToString()] = new(rule.Enabled, rule.Label, rule.Reason);
        }
        return overlay;
    }

    static bool UserExcludes(ProfileSettings user, AuthorOverlay overlay, string record)
    {
        var plugin = FormKey.Factory(record).ModKey.FileName.String;
        return user.Policies.GetValueOrDefault(plugin, PatchPolicy.Review) == PatchPolicy.Exclude
            && overlay.Policies.GetValueOrDefault(plugin)?.Value != PatchPolicy.Exclude;
    }

    // Saved user values that differ from author values, in a stable order.
    public static IReadOnlyList<AuthorConflict> Conflicts(ProfileSettings user, AuthorOverlay overlay)
    {
        var conflicts = new List<AuthorConflict>();
        void Same<T>(string field, IReadOnlyDictionary<string, AuthorValue<T>> author, IReadOnlyDictionary<string, T> saved, Func<T, T, bool> equal)
        {
            foreach (var (key, value) in author)
                if (saved.TryGetValue(key, out var mine) && !equal(mine, value.Value))
                    conflicts.Add(new(field, key, key, value.Value!.ToString()!, mine!.ToString()!, value.Reason));
        }
        Same("patchPolicy", overlay.Policies, user.Policies.Where(p => p.Value != PatchPolicy.Review).ToDictionary(StringComparer.OrdinalIgnoreCase), (a, b) => a == b);
        Same("include", overlay.Include, user.ItemOverrides, (a, b) => a == b);
        Same("materialFamily", overlay.Material, user.MaterialFamilyOverrides, (a, b) => a.Equals(b, StringComparison.OrdinalIgnoreCase));
        Same("distributionMode", overlay.Mode, user.SpecializedLootOverrides, (a, b) => a == b);
        Same("enchantment", overlay.Enchantment, user.EnchantmentOverrides, (a, b) => a == b);
        foreach (var (key, value) in overlay.Include)
            if (value.Value && !user.ItemOverrides.ContainsKey(key) && UserExcludes(user, overlay, key))
                conflicts.Add(new(IncludeUnderExclude, key, key, "True", $"{FormKey.Factory(key).ModKey.FileName} = Exclude", value.Reason));
        return conflicts.OrderBy(c => c.Field, StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<AuthorConflict> Unanswered(ProfileSettings user, AuthorOverlay overlay)
        => Conflicts(user, overlay).Where(c => !user.AuthorConflictAnswers.ContainsKey(c.AnswerKey)).ToArray();

    // Records the user's answers. Using the author value deletes the user's saved
    // value; for an item under an excluded plugin it is remembered instead.
    public static void Answer(ProfileSettings user, IEnumerable<(AuthorConflict Conflict, bool UseAuthor)> answers)
    {
        foreach (var (conflict, useAuthor) in answers)
        {
            if (!useAuthor || conflict.CrossLevel)
            {
                user.AuthorConflictAnswers[conflict.AnswerKey] = useAuthor ? AuthorAnswer : KeepAnswer;
                continue;
            }
            _ = conflict.Field switch
            {
                "patchPolicy" => user.Policies.Remove(conflict.Key),
                "include" => user.ItemOverrides.Remove(conflict.Key),
                "materialFamily" => user.MaterialFamilyOverrides.Remove(conflict.Key),
                "distributionMode" => user.SpecializedLootOverrides.Remove(conflict.Key),
                "enchantment" => user.EnchantmentOverrides.Remove(conflict.Key),
                _ => throw new ArgumentException($"Unknown author conflict field {conflict.Field}."),
            };
        }
    }

    // A per-run copy of the settings whose profile carries the author values.
    public static (AppSettings Effective, AuthorApplication Applied) Apply(AppSettings settings, AuthorOverlay overlay)
    {
        var user = settings.CurrentProfile;
        var effective = JsonSerializer.Deserialize(JsonSerializer.Serialize(settings, JsonContext.Default.AppSettings), JsonContext.Default.AppSettings)!;
        // Restores the case-insensitive dictionaries the engine relies on.
        var profile = SettingsStore.SelectProfile(effective);
        var applied = new AuthorApplication { User = user };
        foreach (var (plugin, value) in overlay.Policies)
            if (user.Policies.GetValueOrDefault(plugin, PatchPolicy.Review) == PatchPolicy.Review)
            {
                profile.Policies[plugin] = value.Value;
                applied.Policies[plugin] = value;
            }
        foreach (var (key, value) in overlay.Include)
        {
            if (user.ItemOverrides.ContainsKey(key)) continue;
            if (value.Value && UserExcludes(user, overlay, key)
                && user.AuthorConflictAnswers.GetValueOrDefault(new AuthorConflict(IncludeUnderExclude, key, key, "True",
                    $"{FormKey.Factory(key).ModKey.FileName} = Exclude", value.Reason).AnswerKey) != AuthorAnswer)
                continue;
            profile.ItemOverrides[key] = value.Value;
            applied.Include[key] = value;
        }
        Fill(overlay.Material, user.MaterialFamilyOverrides, profile.MaterialFamilyOverrides, applied.Material);
        Fill(overlay.Mode, user.SpecializedLootOverrides, profile.SpecializedLootOverrides, applied.Mode);
        Fill(overlay.Enchantment, user.EnchantmentOverrides, profile.EnchantmentOverrides, applied.Enchantment);
        profile.AuthorEnchantments = applied.Enchantment.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Allow-lists only narrow; there is no saved user list to conflict with, only an ignore choice.
        foreach (var (key, value) in overlay.EnchantmentLists)
        {
            if (user.IgnoredAuthorEnchantmentLists.Contains(key)) { applied.IgnoredEnchantmentLists[key] = value; continue; }
            profile.AuthorEnchantmentLists[key] = value.Value.Select(k => k.ToString()).ToArray();
            applied.EnchantmentLists[key] = value;
        }
        foreach (var (key, value) in overlay.DestinationLists)
        {
            if (user.IgnoredAuthorDestinationLists.Contains(key)) { applied.IgnoredDestinationLists[key] = value; continue; }
            profile.AuthorDestinationLists[key] = value.Value.Select(k => k.ToString()).ToArray();
            applied.DestinationLists[key] = value;
        }
        return (effective, applied);
    }

    static void Fill<T>(IReadOnlyDictionary<string, AuthorValue<T>> author, IReadOnlyDictionary<string, T> saved,
        IDictionary<string, T> effective, IDictionary<string, AuthorValue<T>> applied)
    {
        foreach (var (key, value) in author)
            if (!saved.ContainsKey(key))
            {
                effective[key] = value.Value;
                applied[key] = value;
            }
    }

    // The engine's own writes during a run (automatic origins, generation
    // fingerprint and any later field) belong to the saved profile.
    public static void CopyBack(AppSettings effective, AppSettings settings)
    {
        var from = effective.CurrentProfile;
        var to = settings.CurrentProfile;
        foreach (var property in typeof(ProfileSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.CanWrite && property.CanRead && !OverlaidFields.Contains(property.Name))
                property.SetValue(to, property.GetValue(from));
    }
}
