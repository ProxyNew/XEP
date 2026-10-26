using System.Runtime.CompilerServices;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // One instance per evidence-graph build. Only immutable adjacency/record facts
    // are memoized; Resolve depends on both the active path and shared LVLN choices.
    private sealed class NpcBranchResolver(IReadOnlyDictionary<FormKey, INpcGetter> npcs,
        IReadOnlyDictionary<FormKey, ILeveledNpcGetter> leveled)
    {
        private enum Field { Inventory, Traits, Factions }
        private readonly record struct NpcFacts(FormKey Template, bool Inventory, bool Traits, bool Factions, bool Unique);
        private readonly Dictionary<FormKey, NpcFacts> facts = [];
        private readonly Dictionary<FormKey, FormKey[]> children = [];
        public readonly Dictionary<FormKey, HashSet<FormKey>> InventoryDependencies = [];

        private NpcFacts Facts(INpcGetter npc)
        {
            if (facts.TryGetValue(npc.FormKey, out var found)) return found;
            var configuration = npc.Configuration;
            // Retain the original token semantics, including numeric ToString output
            // for unknown enum bits; HasFlag would change those malformed records.
            var flags = configuration.TemplateFlags.ToString().Split(", ");
            return facts[npc.FormKey] = new(npc.Template.FormKey, flags.Contains("Inventory"),
                flags.Contains("Traits"), flags.Contains("Factions"), configuration.Flags.ToString().Split(", ").Contains("Unique"));
        }

        public bool IsUnique(INpcGetter npc) => Facts(npc).Unique;

        private FormKey[] Children(FormKey key, ILeveledNpcGetter list)
            => children.TryGetValue(key, out var cached) ? cached : children[key] = (list.Entries ?? [])
                .Where(e => e.Data is not null).Select(e => e.Data!.Reference.FormKey).Distinct().ToArray();

        private IEnumerable<(INpcGetter? Npc, Dictionary<FormKey, FormKey> Choices)> Resolve(FormKey key, Field field,
            Dictionary<FormKey, FormKey> choices, HashSet<FormKey> active, HashSet<FormKey>? inventoryDependencies = null)
        {
            if (field == Field.Inventory) inventoryDependencies?.Add(key);
            if (!active.Add(key)) { yield return (null, choices); yield break; }
            if (npcs.TryGetValue(key, out var npc))
            {
                var data = Facts(npc);
                var inherited = field switch { Field.Inventory => data.Inventory, Field.Traits => data.Traits, _ => data.Factions };
                if (!inherited) { yield return (npc, choices); yield break; }
                foreach (var result in Resolve(data.Template, field, choices, new(active), inventoryDependencies)) yield return result;
                yield break;
            }
            if (leveled.TryGetValue(key, out var list))
            {
                var childKeys = Children(key, list);
                if (childKeys.Length == 0) { yield return (null, choices); yield break; }
                foreach (var child in childKeys.Where(k => !choices.TryGetValue(key, out var picked) || picked == k))
                {
                    var branch = new Dictionary<FormKey, FormKey>(choices) { [key] = child };
                    foreach (var result in Resolve(child, field, branch, new(active), inventoryDependencies)) yield return result;
                }
                yield break;
            }
            yield return (null, choices);
        }

        public (INpcGetter? Inventory, INpcGetter? Traits, INpcGetter? Factions)[] Branches(FormKey key)
        {
            InventoryDependencies[key] = [];
            return (from inv in Resolve(key, Field.Inventory, [], [], InventoryDependencies[key])
                from traits in Resolve(key, Field.Traits, inv.Choices, [])
                from factions in Resolve(key, Field.Factions, traits.Choices, [])
                select (inv.Npc, traits.Npc, factions.Npc)).Distinct(BranchIdentityComparer.Instance).ToArray();
        }
        // All records come from the canonical winning-npc dictionary. Reference
        // equality is equivalent here and avoids Mutagen's deep record comparison.
        private sealed class BranchIdentityComparer : IEqualityComparer<(INpcGetter? Inventory, INpcGetter? Traits, INpcGetter? Factions)>
        {
            public static readonly BranchIdentityComparer Instance = new();
            public bool Equals((INpcGetter? Inventory, INpcGetter? Traits, INpcGetter? Factions) x,
                (INpcGetter? Inventory, INpcGetter? Traits, INpcGetter? Factions) y)
                => ReferenceEquals(x.Inventory, y.Inventory) && ReferenceEquals(x.Traits, y.Traits) && ReferenceEquals(x.Factions, y.Factions);
            public int GetHashCode((INpcGetter? Inventory, INpcGetter? Traits, INpcGetter? Factions) value)
                => HashCode.Combine(Hash(value.Inventory), Hash(value.Traits), Hash(value.Factions));
            private static int Hash(INpcGetter? npc) => npc is null ? 0 : RuntimeHelpers.GetHashCode(npc);
        }
    }
}
