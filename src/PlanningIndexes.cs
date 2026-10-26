using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Lookups retain the original sequence within each identity. Ranking is still
    // performed by the caller with the current source/profile's priorities.
    private static ILookup<string, EnchantedTemplate> IndexTemplatesByVariant(IEnumerable<EnchantedTemplate> templates, Catalog catalog)
        => templates.ToLookup(t => VariantIdentity(t.Item, t.Enchantment, catalog), StringComparer.Ordinal);

    private static Dictionary<string, int> IndexSourcePlans(IReadOnlyList<VariantPlan> plans, FormKey source, Catalog catalog)
    {
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < plans.Count; i++)
            if (plans[i].Source.FormKey == source)
                first.TryAdd(VariantIdentity(plans[i].Template.Item, plans[i].Template.Enchantment, catalog), i);
        return first;
    }

    // Preserve the previous textual comparison, including case; do not parse or
    // normalize CandidateItem.FormKey when deciding which preview warnings to show.
    private static HashSet<string> PlannedSourceKeys(IEnumerable<VariantPlan> plans)
        => plans.Select(p => p.Source).Distinct<IMajorRecordGetter>(ReferenceEqualityComparer.Instance)
            .Select(source => source.FormKey.ToString()).ToHashSet(StringComparer.Ordinal);
}
