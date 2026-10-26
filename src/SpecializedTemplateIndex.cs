using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Frozen template order is significant. Lookups preserve it within each
    // material/group; only destinations are sorted, as in the original query.
    private sealed class SpecializedTemplateIndex
    {
        private readonly ILookup<(string Material, string Group), EnchantedTemplate> matching;
        private readonly ILookup<string, EnchantedTemplate> expandable;
        private readonly Dictionary<(string Material, string Group), IGrouping<FormKey, (EnchantedTemplate Template, ParentEntry Parent)>[]> exactDestinations = [];
        private readonly Dictionary<string, IGrouping<FormKey, (EnchantedTemplate Template, ParentEntry Parent)>[]> expandedDestinations = [];

        public SpecializedTemplateIndex(IEnumerable<EnchantedTemplate> templates)
        {
            var rows = templates.Select(t => (Template: t, Group: SpecializedGroup(t.Class))).ToArray();
            matching = rows.ToLookup(t => (t.Template.Class.Material, t.Group), t => t.Template);
            expandable = rows.Where(t => !IsOwnBaseMaterial(t.Template.Class.Material)).ToLookup(t => t.Group, t => t.Template);
        }

        public SpecializedTemplateIndex(SpecializedTemplateIndex frozen)
        {
            matching = frozen.matching; expandable = frozen.expandable;
            exactDestinations = new(frozen.exactDestinations);
            expandedDestinations = new(frozen.expandedDestinations);
        }

        public IEnumerable<EnchantedTemplate> Matching(EquipmentClass cls) => matching[(cls.Material, SpecializedGroup(cls))];
        public IEnumerable<EnchantedTemplate> Expandable(EquipmentClass cls) => expandable[SpecializedGroup(cls)];

        public IGrouping<FormKey, (EnchantedTemplate Template, ParentEntry Parent)>[] ObservedDestinations(EquipmentClass cls, bool expand)
        {
            var group = SpecializedGroup(cls);
            if (expand) return SharedMemo(expandedDestinations, group, () => Destinations(expandable[group]));
            var key = (cls.Material, group);
            return SharedMemo(exactDestinations, key, () => Destinations(matching[key]));
        }

        public static IGrouping<FormKey, (EnchantedTemplate Template, ParentEntry Parent)>[] Destinations(IEnumerable<EnchantedTemplate> templates)
            => templates.SelectMany(t => t.Parents.Select(p => (Template: t, Parent: p)))
                .GroupBy(x => x.Parent.List).OrderBy(g => g.Key).ToArray();
    }
}
