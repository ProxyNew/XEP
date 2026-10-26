using System.IO;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Built only when keyword removal is present, from winning loaded equipment.
    // Repeated predicates reuse their intersection across recipients and rules.
    private sealed class CdfKeywordMembership
    {
        private readonly HashSet<FormKey> equipment = [];
        private readonly Dictionary<FormKey, HashSet<FormKey>> keywordSets = [];
        private readonly Dictionary<FormKey, HashSet<FormKey>> members = [];
        private readonly HashSet<FormKey> uncertain = [];
        private readonly Dictionary<string, (FormKey[] Possible, IReadOnlySet<FormKey> Certain)> matches = new(StringComparer.Ordinal);

        public CdfKeywordMembership(Catalog catalog, List<string> notes)
        {
            using var timing = PhaseTiming.Measure("cdf.keyword-index");
            var unknownWeapons = catalog.SkyPatcher?.Status == "incomplete-capture";
            var unknownArmor = unknownWeapons;
            if (catalog.SkyPatcher is { } input)
                foreach (var file in input.Files.Where(f => f.Status == "other-category-unanalysed" && f.Rules.Count > 0))
                {
                    var category = file.Path.Split('/').ElementAtOrDefault(1) ?? "";
                    if (!SkyPatcherLists.CategoryMayRun(input, category)) continue;
                    if (category.Equals("weapon", StringComparison.OrdinalIgnoreCase)) unknownWeapons = true;
                    else if (category.Equals("armor", StringComparison.OrdinalIgnoreCase)) unknownArmor = true;
                }
            foreach (var item in catalog.Weapons.Values.Cast<IMajorRecordGetter>().Concat(catalog.Armors.Values))
            {
                equipment.Add(item.FormKey);
                if (unknownWeapons && catalog.Weapons.ContainsKey(item.FormKey) || unknownArmor && catalog.Armors.ContainsKey(item.FormKey))
                    uncertain.Add(item.FormKey);
                keywordSets[item.FormKey] = Keywords(item).Select(k => k.FormKey).ToHashSet();
                foreach (var keyword in keywordSets[item.FormKey])
                {
                    if (!members.TryGetValue(keyword, out var entries)) members[keyword] = entries = [];
                    entries.Add(item.FormKey);
                }
            }
            if (uncertain.Count > 0) notes.Add($"CDF keyword membership unresolved for {uncertain.Count} equipment forms: enabled unanalysed SkyPatcher weapon/armor rules or incomplete capture leave keyword mutations unproved. Keyword-removal recipient scopes retain possible erasure until that external mutation/order contract is proved.");
        }

        // Possible members may be erased; only certain members, whose keywords no
        // unanalysed external mutation can change, may prove that a rule fires.
        public FormKey[] Match(FormKey[] keywords) => Matches(keywords).Possible;
        public IReadOnlySet<FormKey> Certain(FormKey[] keywords) => Matches(keywords).Certain;

        private (FormKey[] Possible, IReadOnlySet<FormKey> Certain) Matches(FormKey[] keywords)
        {
            var identity = string.Join(',', keywords);
            if (matches.TryGetValue(identity, out var cached)) return cached;
            using var timing = PhaseTiming.Measure("cdf.keyword-match");
            var sets = keywords.Select(k => members.GetValueOrDefault(k) ?? []).OrderBy(s => s.Count).ToArray();
            var result = sets.Length == 0 ? new HashSet<FormKey>(equipment) : new HashSet<FormKey>(sets[0]);
            foreach (var set in sets.Skip(1)) result.IntersectWith(set);
            var certain = result.Except(uncertain).ToHashSet();
            result.UnionWith(uncertain);
            return matches[identity] = (result.Order().ToArray(), certain);
        }

        public bool MatchesGenerated(FormKey source, IMajorRecordGetter generated)
            => keywordSets[source].SetEquals(Keywords(generated).Select(k => k.FormKey));
    }

    private static void ValidateCdfGeneratedKeywords(IMajorRecordGetter source, IMajorRecordGetter generated, Catalog catalog)
    {
        if (catalog.CdfInventory?.KeywordMembership is not { } membership) return;
        // Admission projects a new variant from its source, exactly as the writer
        // does. Verify that contract on the actual generated record before publish.
        if (!membership.MatchesGenerated(source.FormKey, generated))
            throw new InvalidDataException("CDF keyword-removal output refused: generated keywords differ from the source membership used by shared analysis.");
    }
}
