using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record EnchantmentRecommendation(string Reason);
    private sealed partial class Catalog
    {
        public Dictionary<FormKey, IMagicEffectGetter> MagicEffectRecords { get; init; } = [];
        public Dictionary<FormKey, EnchantmentRecommendation> EnchantmentRecommendations { get; set; } = [];
        // Mod-author allow-lists: base item -> enchantment records or family roots it may use.
        public Dictionary<FormKey, HashSet<FormKey>> SourceEnchantmentAllow { get; set; } = [];
        // Mod-author destination lists: base item -> original leveled lists/containers it may reach.
        public Dictionary<FormKey, HashSet<FormKey>> SourceDestinationAllow { get; set; } = [];
    }

    // Selection policy only: recommendations never authorize reuse or placement.
    private static void ApplyEnchantmentRecommendations(Catalog catalog, ProfileSettings profile)
    {
        var recommendations = new Dictionary<FormKey, EnchantmentRecommendation>();
        AddSummerturgyRecommendations(catalog, recommendations);
        catalog.EnchantmentRecommendations = recommendations;
        catalog.SourceEnchantmentAllow = profile.AuthorEnchantmentLists.ToDictionary(x => FormKey.Factory(x.Key),
            x => x.Value.Select(k => FormKey.Factory(k)).ToHashSet());
        catalog.SourceDestinationAllow = profile.AuthorDestinationLists.ToDictionary(x => FormKey.Factory(x.Key),
            x => x.Value.Select(k => FormKey.Factory(k)).ToHashSet());
        SelectEnchantmentTemplates(catalog, profile);
    }

    private static void SelectEnchantmentTemplates(Catalog catalog, ProfileSettings? profile)
    {
        catalog.Templates = catalog.AllTemplates.GroupBy(t => t.Enchantment)
            .Where(g => profile is null || EffectiveEnchantmentSelection(g.Key, g.Any(t => HasTemplateDistribution(t, catalog)), catalog, profile))
            .SelectMany(g => g).ToList();
        catalog.TemplatesByClass = catalog.Templates.GroupBy(t => ClassKey(t.Class)).ToDictionary(g => g.Key, g => g.ToList());
        if (catalog.Templates.Count == 0)
            throw new InvalidOperationException("No enabled enchantments from the selected origins have a usable ordinary-equipment template.");
    }

    // Narrows one base item's templates after global selection; never enables anything.
    private static bool TemplateAllowed(FormKey source, EnchantedTemplate template, Catalog catalog)
        => !catalog.SourceEnchantmentAllow.TryGetValue(source, out var allowed) || allowed.Contains(template.Enchantment)
            || catalog.ObjectEffects.GetValueOrDefault(template.Enchantment) is { } effect && !effect.BaseEnchantment.IsNull
                && allowed.Contains(effect.BaseEnchantment.FormKey);

    // Unrestricted items keep the shared list, so reference-keyed template caches still apply.
    private static List<EnchantedTemplate>? SourceTemplates(FormKey source, List<EnchantedTemplate>? templates, Catalog catalog)
        => templates is null || !catalog.SourceEnchantmentAllow.ContainsKey(source) ? templates
            : templates.Where(t => TemplateAllowed(source, t, catalog)).ToList();

    private const string AuthorListLeavesNoTemplate = "The mod author's enchantment list for this item leaves no compatible enchantment template";

    private static bool RecommendedEnchantmentSelection(FormKey key, bool placed, Catalog catalog)
        => placed && !catalog.EnchantmentRecommendations.ContainsKey(key);

    private static bool HasTemplateDistribution(EnchantedTemplate template, Catalog catalog)
        => template.Parents.Count > 0 || catalog.CdfInventory?.DirectByPayload.Contains(template.Item.FormKey) == true;
    private static bool EffectiveEnchantmentSelection(FormKey key, bool placed, Catalog catalog, ProfileSettings profile)
        => AuthorRecommendationCancelled(key, catalog, profile) ? RecommendedEnchantmentSelection(key, placed, catalog)
            : profile.EnchantmentOverrides.GetValueOrDefault(key.ToString(), RecommendedEnchantmentSelection(key, placed, catalog));

    // A recommended-off rule (Summerturgy's) is more specific than a mod-author rule: the
    // rules file cannot re-enable an enchantment it turned off. The user's own choice still can.
    private static bool AuthorRecommendationCancelled(FormKey key, Catalog catalog, ProfileSettings profile)
        => profile.AuthorEnchantments.Contains(key.ToString()) && catalog.EnchantmentRecommendations.ContainsKey(key)
            && profile.EnchantmentOverrides.GetValueOrDefault(key.ToString());
}
