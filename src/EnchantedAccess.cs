using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

// Enchanted-access evidence decides which plain items get enchanted variants and where.
// An item qualifies only where one of its consumer groups already receives leveled enchanted
// equipment of the same kind AND material, and its variants go only into those enchanted lists.
// Consumer groups: an exact NPC context (race + faction set), or the containers that hold the
// same set of leveled lists. Everything is derived from the load order's consumers and lists;
// there are no plugin, keyword-prefix or EditorID rules.
public static partial class PatcherEngine
{
    // Physical direct-entry budget: originals also write to their existing isolated copies.
    // Call CanAdd before allocating a new copy or reserving semantic coverage.
    private sealed class DirectPeerBudget(Func<FormKey, FormKey, int> baseline,
        Func<FormKey, FormKey> original, Func<FormKey, IEnumerable<FormKey>> mirrors)
    {
        private readonly Dictionary<FormKey, int> additions = [];
        public Func<FormKey, int> Ceiling { get; init; } = _ => 255;
        private int Positions(FormKey target, FormKey prototype)
        {
            var source = original(target);
            return baseline(target, prototype) + additions.GetValueOrDefault(target)
                + (source == target ? 0 : additions.GetValueOrDefault(source));
        }
        public bool CanAdd(FormKey target, FormKey prototype, bool freshCopy, out int projected)
            => CanAddMultiple(target, prototype, freshCopy, 1, out projected);

        public bool CanAddMultiple(FormKey target, FormKey prototype, bool freshCopy, int count, out int projected)
        {
            // Positions can scan runtime placements; evaluate each target once.
            projected = 0;
            var fits = true;
            foreach (var t in freshCopy ? new[] { target } : mirrors(target).Prepend(target).Distinct())
            {
                var positions = Positions(t, t == target ? prototype : original(t));
                projected = Math.Max(projected, positions);
                fits &= positions + count <= (freshCopy && t == target ? 255 : Ceiling(t));
            }
            return fits;
        }
        public void Commit(FormKey target) => additions[target] = additions.GetValueOrDefault(target) + 1;

        public static DirectPeerBudget ForPlanning(Catalog catalog)
        {
            using var timing = PhaseTiming.Measure("planning.direct-capacity");
            return catalog.Capacity.Capture();
        }
    }

    private enum AccessOutcome { NotRoutable, NoConsumer, NoAccess, MaterialUnknown, OtherMaterialOnly, Variants }

    // Root: top-most enchanted-only list (or a mixed list holding enchanted equipment directly).
    // Direct: every carrier group of the root also carries the source item.
    private sealed record AccessDestination(FormKey Root, short Level, bool Direct);

    private sealed record AccessVerdict(AccessOutcome Outcome, string? Kind, string? Material, HashSet<string> Groups,
        List<string> WithAccess, List<AccessDestination> Destinations, List<FormKey> KindRoots);

    private sealed class AccessGroup(string id, bool container)
    {
        public string Id { get; } = id;
        public bool Container { get; } = container;
        public HashSet<FormKey> Entries { get; } = [];
        public HashSet<FormKey> Members { get; } = [];
        public HashSet<FormKey> Factions { get; } = [];
        public HashSet<FormKey> Races { get; } = [];
        public Dictionary<(FormKey Root, string Kind), short>? Access { get; set; }
        public AccessGroup CopyForPass()
        {
            var copy = new AccessGroup(Id, Container) { Access = Access };
            copy.Entries.UnionWith(Entries); copy.Members.UnionWith(Members);
            copy.Factions.UnionWith(Factions); copy.Races.UnionWith(Races);
            return copy;
        }
    }

    // Lazily filled tables (group access, ancestry, roots) read live lists, which
    // planning changes. A pass therefore fills its own copy and never its source's;
    // published values are not changed afterwards and are shared.
    private sealed class AccessIndex
    {
        public AccessIndex() { }
        public AccessIndex(AccessIndex source)
        {
            foreach (var (id, group) in source.Groups) Groups[id] = group.CopyForPass();
            foreach (var (key, value) in source.EntryGroups) EntryGroups[key] = value;
            foreach (var (key, value) in source.Up) Up[key] = value;
            foreach (var (key, value) in source.Carriers) Carriers[key] = value;
            lock (source.Equipment) foreach (var (key, value) in source.Equipment) Equipment[key] = value;
            foreach (var (key, value) in source.Info) Info[key] = value;
            InfoActive.UnionWith(source.InfoActive);
            foreach (var (key, value) in source.Roots) Roots[key] = value;
            foreach (var (key, value) in source.RootMaterials) RootMaterials[key] = value;
            lock (source.MaterialBranchMaterials) foreach (var (key, value) in source.MaterialBranchMaterials) MaterialBranchMaterials[key] = value;
            lock (source.MaterialBranchSafety) foreach (var (key, value) in source.MaterialBranchSafety) MaterialBranchSafety[key] = value;
            Names = source.Names;
        }
        public Dictionary<string, AccessGroup> Groups { get; } = [];
        public Dictionary<FormKey, HashSet<string>> EntryGroups { get; } = [];
        public Dictionary<FormKey, HashSet<FormKey>> Up { get; } = [];
        public Dictionary<FormKey, HashSet<string>> Carriers { get; } = [];
        public Dictionary<FormKey, (string Kind, string? Material, bool Enchanted)?> Equipment { get; } = [];
        public Dictionary<FormKey, (bool Plain, bool Enchanted, HashSet<string> Kinds)> Info { get; } = [];
        public HashSet<FormKey> InfoActive { get; } = [];
        public Dictionary<FormKey, Dictionary<(FormKey Root, string Kind), short>> Roots { get; } = [];
        public Dictionary<FormKey, HashSet<(string Kind, string Material)>> RootMaterials { get; } = [];
        // Material expansion reads the frozen specialized graph, not AccessRootMaterials'
        // live-list traversal. Keep these facts separate and scoped to this index rebuild.
        public Dictionary<FormKey, IReadOnlySet<(string Kind, string Material)>> MaterialBranchMaterials { get; } = [];
        public Dictionary<(FormKey Point, string Kind), string?> MaterialBranchSafety { get; } = [];
        public Dictionary<FormKey, string>? Names { get; set; }
    }

    private static bool GenericNpcUse(TypedLootUse use) => use.Role.StartsWith("NPC ", StringComparison.Ordinal)
        && use.Role != "NPC death loot" && use.Known && !use.ScopeOnly && !use.Context.StartsWith("actor:", StringComparison.Ordinal);

    private static AccessIndex BuildAccessIndex(Catalog catalog)
    {
        var index = new AccessIndex();
        var graph = SpecializedGraph(catalog);
        var rows = graph.Uses.SelectMany(x => x.Value.Select(u => (Key: x.Key, Use: u))).ToArray();
        var consumerGroups = new Dictionary<FormKey, HashSet<string>>();
        AccessGroup Link(FormKey entry, TypedLootUse use, string id, bool container)
        {
            if (!index.Groups.TryGetValue(id, out var group)) index.Groups[id] = group = new(id, container);
            group.Entries.Add(entry); group.Members.Add(use.Consumer);
            if (!index.EntryGroups.TryGetValue(entry, out var ids)) index.EntryGroups[entry] = ids = [];
            ids.Add(id);
            if (!consumerGroups.TryGetValue(use.Consumer, out var mine)) consumerGroups[use.Consumer] = mine = [];
            mine.Add(id);
            return group;
        }
        foreach (var (key, use) in rows.Where(x => GenericNpcUse(x.Use)))
        {
            var parts = use.Context.Split('|');
            var group = Link(key, use, "npc:" + parts[0] + "|" + (parts.Length > 1 ? parts[1] : ""), false);
            if (FormKey.TryFactory(parts[0], out var race)) group.Races.Add(race);
            if (parts.Length > 1)
                foreach (var text in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (FormKey.TryFactory(text, out var faction)) group.Factions.Add(faction);
        }
        // Containers are grouped by the leveled lists they hold: identical chests merge, themes stay apart.
        var containerRows = rows.Where(x => x.Use.Role == "Container inventory").ToArray();
        var held = containerRows.GroupBy(x => x.Use.Consumer).ToDictionary(g => g.Key,
            g => string.Join(",", g.Select(x => x.Key).Where(catalog.Lists.ContainsKey).Distinct().Order()));
        foreach (var (key, use) in containerRows)
            Link(key, use, held[use.Consumer].Length > 0 ? "containers:" + held[use.Consumer] : "container:" + use.Consumer, true);
        // CDF/CID contexts are reference/predicate scoped. Equal held-list sets or a
        // shared CONT base do not make two native recipient predicates equivalent.
        foreach (var (key, use) in rows.Where(x => NativeContainerRole(x.Use.Role)))
            Link(key, use, use.Context, true);
        // Death items belong to the groups the same NPC already has through its generic inventory or outfit.
        foreach (var (key, use) in rows.Where(x => x.Use.Role == "NPC death loot"))
            foreach (var id in consumerGroups.GetValueOrDefault(use.Consumer, []).ToArray())
                Link(key, use, id, false);
        return index;
    }

    private static HashSet<FormKey> AccessUp(AccessIndex index, FormKey key, Catalog catalog)
    {
        if (index.Up.TryGetValue(key, out var found)) return found;
        found = []; var todo = new Stack<FormKey>(); todo.Push(key);
        while (todo.TryPop(out var next))
            if (found.Add(next))
                foreach (var parent in catalog.DirectParents.GetValueOrDefault(next, []))
                    if (catalog.Lists.TryGetValue(parent.List, out var list) && !list.IsDeleted) todo.Push(parent.List);
        return index.Up[key] = found;
    }

    // Every group that can receive this record through the lists above it. An item placed directly in
    // a container is a fixed placement and does not make the container a carrier. Verified quest
    // insertions add the carriers of the list the script inserts into.
    private static HashSet<string> AccessCarriers(AccessIndex index, FormKey key, Catalog catalog)
    {
        if (index.Carriers.TryGetValue(key, out var found)) return found;
        found = []; index.Carriers[key] = found;
        var isList = catalog.Lists.ContainsKey(key);
        var up = AccessUp(index, key, catalog);
        foreach (var ancestor in up)
            foreach (var id in index.EntryGroups.GetValueOrDefault(ancestor, []))
                if (isList || ancestor != key || !index.Groups[id].Container || NativeContainerRole(id)) found.Add(id);
        foreach (var ancestor in up)
            foreach (var unlock in catalog.ScriptUnlocks.GetValueOrDefault(ancestor, []))
                if (unlock.OriginalTarget != key) found.UnionWith(AccessCarriers(index, unlock.OriginalTarget, catalog));
        return found;
    }

    private static (string Kind, string? Material, bool Enchanted)? AccessEquipment(AccessIndex index, FormKey key, Catalog catalog)
        => SharedMemo(index.Equipment, key, () => UncachedAccessEquipment(key, catalog));

    private static (string Kind, string? Material, bool Enchanted)? UncachedAccessEquipment(FormKey key, Catalog catalog)
    {
        (string Kind, string? Material, bool Enchanted)? result = null;
        IMajorRecordGetter? item = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(key);
        if (item is not null && !item.IsDeleted)
        {
            var enchanted = TryEnchantment(item, out _);
            var baseKey = enchanted ? BaseIdentity(item, catalog) : key;
            var shapeItem = (baseKey.IsNull ? null
                : catalog.Weapons.GetValueOrDefault(baseKey) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(baseKey)) ?? item;
            if (enchanted && (!OrdinaryEquipment(item, catalog.KeywordNames)
                || !OrdinaryEquipment(shapeItem, catalog.KeywordNames) || baseKey.IsNull)) return null;
            if (CachedClass(shapeItem, catalog) is { } cls) result = (SpecializedGroup(cls), cls.Material, enchanted);
            else if (CachedShape(shapeItem, catalog) is { } shape)
                result = (SpecializedGroup(new EquipmentClass(shape.Signature, "", shape.Type, shape.ArmorType)), null, enchanted);
        }
        return result;
    }

    private static (bool Plain, bool Enchanted, HashSet<string> Kinds) AccessInfo(AccessIndex index, FormKey key, Catalog catalog)
    {
        if (index.Info.TryGetValue(key, out var cached)) return cached;
        if (!index.InfoActive.Add(key)) return (true, false, []);   // cycle: treat as mixed
        bool plain = false, enchanted = false; var kinds = new HashSet<string>();
        if (catalog.Lists.TryGetValue(key, out var list) && !list.IsDeleted)
            foreach (var entry in list.Entries ?? [])
            {
                if (entry.Data is not { } data) continue;
                var child = data.Reference.FormKey;
                if (catalog.Lists.ContainsKey(child))
                {
                    var c = AccessInfo(index, child, catalog);
                    plain |= c.Plain; enchanted |= c.Enchanted; kinds.UnionWith(c.Kinds);
                }
                else if (AccessEquipment(index, child, catalog) is { } e)
                {
                    if (e.Enchanted) { enchanted = true; kinds.Add(e.Kind); } else plain = true;
                }
                else plain = true; // Excluded/unknown leaves must not promote a mixed list to enchanted-only.
            }
        index.InfoActive.Remove(key);
        return index.Info[key] = (plain, enchanted, kinds);
    }

    // Enchanted destinations below a list: the top-most enchanted-only lists, or a mixed list that holds
    // enchanted equipment directly. Value: the highest entry level on the path (the level gate).
    private static Dictionary<(FormKey Root, string Kind), short> AccessRoots(AccessIndex index, FormKey key, Catalog catalog)
    {
        if (index.Roots.TryGetValue(key, out var cached)) return cached;
        var result = new Dictionary<(FormKey Root, string Kind), short>(); index.Roots[key] = result;
        var info = AccessInfo(index, key, catalog);
        if (info.Enchanted && !info.Plain) { foreach (var kind in info.Kinds) result[(key, kind)] = 0; return result; }
        void Put((FormKey Root, string Kind) root, short level) => result[root] = result.TryGetValue(root, out var old) ? Math.Min(old, level) : level;
        if (catalog.Lists.TryGetValue(key, out var list) && !list.IsDeleted)
            foreach (var entry in list.Entries ?? [])
            {
                if (entry.Data is not { } data) continue;
                var child = data.Reference.FormKey;
                if (catalog.Lists.ContainsKey(child))
                {
                    foreach (var (root, level) in AccessRoots(index, child, catalog)) Put(root, Math.Max(data.Level, level));
                }
                else if (AccessEquipment(index, child, catalog) is { } e && e.Enchanted) Put((key, e.Kind), data.Level);
            }
        return result;
    }

    private static HashSet<(string Kind, string Material)> AccessRootMaterials(AccessIndex index, FormKey root, Catalog catalog)
    {
        if (index.RootMaterials.TryGetValue(root, out var cached)) return cached;
        var found = new HashSet<(string Kind, string Material)>(); var seen = new HashSet<FormKey>();
        var todo = new Stack<FormKey>(); todo.Push(root);
        while (todo.TryPop(out var key))
        {
            if (!seen.Add(key)) continue;
            if (catalog.Lists.TryGetValue(key, out var list))
            {
                if (!list.IsDeleted) foreach (var e in list.Entries ?? []) if (e.Data is { } d) todo.Push(d.Reference.FormKey);
            }
            else if (AccessEquipment(index, key, catalog) is { } eq && eq.Enchanted && eq.Material is { } material)
                found.Add((eq.Kind, material));
        }
        return index.RootMaterials[root] = found;
    }

    private static Dictionary<(FormKey Root, string Kind), short> GroupAccess(AccessIndex index, AccessGroup group, Catalog catalog)
    {
        if (group.Access is { } cached) return cached;
        var result = new Dictionary<(FormKey Root, string Kind), short>();
        foreach (var entry in group.Entries.Where(catalog.Lists.ContainsKey))
            foreach (var (root, level) in AccessRoots(index, entry, catalog))
                result[root] = result.TryGetValue(root, out var old) ? Math.Min(old, level) : level;
        return group.Access = result;
    }

    private static AccessVerdict EvaluateAccess(AccessIndex index, FormKey key, Catalog catalog)
    {
        var equipment = AccessEquipment(index, key, catalog);
        var groups = AccessCarriers(index, key, catalog);
        AccessVerdict Make(AccessOutcome outcome, List<string>? withAccess = null, List<AccessDestination>? destinations = null,
            List<FormKey>? roots = null) => new(outcome, equipment?.Kind, equipment?.Material, groups, withAccess ?? [], destinations ?? [], roots ?? []);
        if (equipment is not { } item) return Make(AccessOutcome.NotRoutable);
        if (groups.Count == 0) return Make(AccessOutcome.NoConsumer);
        bool AllowedRoot(FormKey root) => AccessRootMaterials(index, root, catalog)
            .Any(e => e.Kind == item.Kind && (!IsOwnBaseMaterial(e.Material) || e.Material == item.Material));
        var withAccess = groups.Where(id => GroupAccess(index, index.Groups[id], catalog).Keys.Any(r => r.Kind == item.Kind && AllowedRoot(r.Root)))
            .Order(StringComparer.Ordinal).ToList();
        if (withAccess.Count == 0) return Make(AccessOutcome.NoAccess);
        var reached = new Dictionary<FormKey, short>();
        foreach (var id in withAccess)
            foreach (var (root, level) in GroupAccess(index, index.Groups[id], catalog))
                if (root.Kind == item.Kind && AllowedRoot(root.Root)) reached[root.Root] = reached.TryGetValue(root.Root, out var old) ? Math.Min(old, level) : level;
        var kindRoots = reached.Keys.Order().ToList();
        if (item.Material is not { } material) return Make(AccessOutcome.MaterialUnknown, withAccess, null, kindRoots);
        var destinations = reached.Where(r => AccessRootMaterials(index, r.Key, catalog).Contains((item.Kind, material)))
            .OrderBy(r => r.Key)
            .Select(r => new AccessDestination(r.Key, r.Value, AccessCarriers(index, r.Key, catalog).IsSubsetOf(groups))).ToList();
        return destinations.Count == 0 ? Make(AccessOutcome.OtherMaterialOnly, withAccess, null, kindRoots)
            : Make(AccessOutcome.Variants, withAccess, destinations, kindRoots);
    }

    private static string AccessGroupLabel(AccessIndex index, string id, Catalog catalog)
    {
        index.Names ??= catalog.Mods.SelectMany(m => m.Factions.Cast<IMajorRecordGetter>().Concat(m.Races).Concat(m.Containers))
            .GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.Last().EditorID ?? g.Key.ToString());
        string Name(FormKey k) => index.Names.GetValueOrDefault(k, k.ToString());
        var group = index.Groups[id];
        if (NativeContainerRole(id))
            return (id.StartsWith("CID", StringComparison.Ordinal) ? "CID recipient " : "CDF recipient ") + string.Join(", ", group.Members.Select(Name)) + " (native conditions retained)";
        if (group.Container)
            return "containers " + string.Join(", ", group.Members.Select(Name).Order(StringComparer.OrdinalIgnoreCase).Take(3))
                + (group.Members.Count > 3 ? $" (+{group.Members.Count - 3})" : "");
        var factions = group.Factions.Select(Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return string.Join("+", factions.Take(3)) + (factions.Length > 3 ? $"+{factions.Length - 3}" : "")
            + " [" + string.Join(",", group.Races.Select(Name).Order(StringComparer.OrdinalIgnoreCase)) + $"] ({group.Members.Count} NPC)";
    }

    private static (string Reason, string Details) DescribeAccess(AccessIndex index, AccessVerdict verdict, Catalog catalog)
    {
        string ListLabel(FormKey k) => $"{catalog.Lists.GetValueOrDefault(k)?.EditorID ?? "(no EditorID)"} [{k}]";
        string[] Materials(FormKey root) => AccessRootMaterials(index, root, catalog).Where(m => m.Kind == verdict.Kind && (!IsOwnBaseMaterial(m.Material) || m.Material == verdict.Material))
            .Select(m => m.Material).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var otherMaterials = verdict.KindRoots.SelectMany(Materials).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var reason = verdict.Outcome switch
        {
            AccessOutcome.NotRoutable => "Equipment kind unknown: no enchanted-access evidence can be matched.",
            AccessOutcome.NoConsumer => "No consumer: no NPC, outfit, leveled container or verified quest insertion carries this item through a leveled list. Items placed directly in a container are fixed placements and don't count.",
            AccessOutcome.NoAccess => $"No enchanted access: carried by {verdict.Groups.Count} consumer group(s), none of which receives leveled enchanted {verdict.Kind}.",
            AccessOutcome.MaterialUnknown => $"Uncertain: material unknown. {verdict.WithAccess.Count} of {verdict.Groups.Count} carrier group(s) receive enchanted {verdict.Kind} ({string.Join(", ", otherMaterials)}).",
            AccessOutcome.OtherMaterialOnly when verdict.Material is not null && IsOwnBaseMaterial(verdict.Material)
                => $"Uncertain: {verdict.WithAccess.Count} of {verdict.Groups.Count} carrier group(s) receive enchanted {verdict.Kind}, but this item's isolated own-base family has no matching enchanted template. Cross-material expansion is disabled because no evidence identifies a compatible material family.",
            AccessOutcome.OtherMaterialOnly => $"Uncertain: {verdict.WithAccess.Count} of {verdict.Groups.Count} carrier group(s) receive enchanted {verdict.Kind}, but only in other materials ({string.Join(", ", otherMaterials)}). Set 'Specialized enchanted lists only' and enable 'Allow new material lists for specialized enchanted distribution' to attempt a new material family at supported selection points.",
            _ => $"Enchanted access: {verdict.Destinations.Count} enchanted {verdict.Material} {verdict.Kind} list(s) reached by {verdict.WithAccess.Count} of {verdict.Groups.Count} carrier group(s)."
        };
        var text = new StringBuilder();
        text.AppendLine("ENCHANTED ACCESS");
        text.AppendLine(reason);
        text.AppendLine($"Kind: {verdict.Kind ?? "unknown"}; material: {verdict.Material ?? "unknown"}.");
        if (verdict.Destinations.Count > 0)
        {
            text.AppendLine().AppendLine($"Same-material candidate destinations ({verdict.Destinations.Count}); placement still requires route validation:");
            foreach (var d in verdict.Destinations.Take(20))
                text.AppendLine($"  {ListLabel(d.Root)} from level {d.Level}; {(d.Direct ? "every carrier of this list carries the item" : "shared with groups that don't carry the item")}");
        }
        if (verdict.KindRoots.Count > 0)
        {
            text.AppendLine().AppendLine($"Enchanted {verdict.Kind} lists the carriers reach ({verdict.KindRoots.Count}):");
            foreach (var root in verdict.KindRoots.Take(20)) text.AppendLine($"  {ListLabel(root)} [{string.Join(", ", Materials(root))}]");
        }
        if (verdict.WithAccess.Count > 0)
        {
            text.AppendLine().AppendLine($"Carrier groups with enchanted {verdict.Kind} ({verdict.WithAccess.Count}):");
            foreach (var id in verdict.WithAccess.Take(15)) text.AppendLine("  " + AccessGroupLabel(index, id, catalog));
        }
        text.AppendLine().AppendLine($"All carrier groups ({verdict.Groups.Count}):");
        foreach (var id in verdict.Groups.Order(StringComparer.Ordinal).Take(25)) text.AppendLine("  " + AccessGroupLabel(index, id, catalog));
        if (verdict.Groups.Count > 25) text.AppendLine($"  (+{verdict.Groups.Count - 25} more)");
        return (reason, text.ToString().TrimEnd());
    }

    // Materialize only missing-family routes approved by the shared specialized
    // assessment. This layer preserves branch topology and source availability;
    // it no longer searches independently for destinations.
    private sealed record OtherMaterialAttachment(ParentEntry Parent, FormKey Child, MaterialAvailabilityPath Availability, ScriptUnlock? Unlock);

    private sealed partial class Catalog
    {
        public Dictionary<string, FormKey> MaterialPathNodes { get; } = new(StringComparer.Ordinal);
        public PlanningPositions<FallbackEdge> MaterialEdgesFrom { get; } = new(e => e.Parent.List);
        public PlanningPositions<FallbackEdge> MaterialEdgesTo { get; } = new(e => e.Child);
        public PlanningPositions<RuntimePlacement> MaterialPlacementsTo { get; } = new(e => e.Child);
        private EdgeMultiplicity? edgeMultiplicity;
        // Occurrences of one exact edge, without scanning every planned edge.
        public int FallbackEdgeCount(FallbackEdge edge) => (edgeMultiplicity ??= new(FallbackEdges)).Count(edge);
    }

    // Exact per-edge counts, kept current by the collection's mutation hooks.
    private sealed class EdgeMultiplicity
    {
        private readonly Dictionary<FallbackEdge, int> counts = [];
        public EdgeMultiplicity(PlanningAdditions<FallbackEdge> edges)
        {
            foreach (var edge in edges) Change(edge, 1);
            edges.Added += edge => Change(edge, 1);
            edges.Removed += edge => Change(edge, -1);
            edges.Updated += (before, after) => { Change(before, -1); Change(after, 1); };
        }
        private void Change(FallbackEdge edge, int delta)
        {
            var count = counts.GetValueOrDefault(edge) + delta;
            if (count == 0) counts.Remove(edge); else counts[edge] = count;
        }
        public int Count(FallbackEdge edge) => counts.GetValueOrDefault(edge);
    }

    // share: the caller fixes the parent entry's level at attachment, so pools attached
    // to one entry under the same controls can share one container and one control chain.
    // The parent gains one entry per member as before, each pointing at the chain; the
    // container picks uniformly among its members, so every pool keeps its selection share.
    private static void AttachOtherMaterialBranch(FormKey source, OtherMaterialAttachment attachment, Catalog catalog, bool preserveChildControls = false, bool share = false)
    {
        var root = attachment.Child;
        (ParentEntry Parent, string Controls) sharedKey = default;
        var sharedContainer = FormKey.Null;
        if (share && attachment.Unlock is null && attachment.Availability.Structure is null && attachment.Availability.Controls.Length > 0
            && (catalog.Lists[attachment.Parent.List].Flags & LeveledItem.Flag.UseAll) == 0
            && catalog.Lists[root] is LeveledItem member && member.Global.IsNull && member.ChanceNone.Value == 0
            && !catalog.MaterialChanceApplications.ContainsKey(root) && !catalog.MaterialGlobalApplications.ContainsKey(root))
        {
            sharedKey = (attachment.Parent, attachment.Availability.Identity);
            if (catalog.SharedAvailabilityChains.TryGetValue(sharedKey, out var chain) && catalog.Fallbacks.ContainsKey(chain.Container))
            {
                catalog.SharedAvailabilityMembers.Add(root);
                catalog.FallbackEdges.Add(new(new(chain.Container, 1, 1), root));
                catalog.FallbackEdges.Add(new(attachment.Parent, chain.Top));
                return;
            }
            var container = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
            var list = new LeveledItem(container, SkyrimRelease.SkyrimSE)
            { EditorID = $"Shared_{member.EditorID}", Flags = (LeveledItem.Flag)3, ChanceNone = new Percent(0) };
            catalog.AddList(container, list);
            catalog.Fallbacks.Add(container, new($"material-shared|{attachment.Parent}|{attachment.Availability.Identity}", "Availability", list));
            catalog.SharedAvailabilityMembers.Add(root);
            catalog.FallbackEdges.Add(new(new(container, 1, 1), root));
            root = sharedContainer = container;
        }
        foreach (var control in attachment.Availability.Controls)
        {
            if (control.Kind == MaterialAvailabilityKind.ActorLevelWindow)
            {
                // Highest-eligible wrapper: the branch at level 1, an empty position just
                // above the bound. Minimum levels stay on the equipment leaves.
                var windowIdentity = (root, control.List, control.Maximum);
                if (!catalog.MaterialWindowWrappers.TryGetValue(windowIdentity, out var windowKey))
                {
                    var empty = catalog.AvailabilityEmptyLeaf;
                    if (empty.IsNull)
                    {
                        empty = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                        var leaf = new LeveledItem(empty, SkyrimRelease.SkyrimSE) { EditorID = "Availability_Empty", Flags = (LeveledItem.Flag)3, ChanceNone = new Percent(0), Entries = new() };
                        catalog.AddList(empty, leaf);
                        catalog.Fallbacks.Add(empty, new("availability-empty-leaf-v1", "Availability", leaf));
                        catalog.AvailabilityEmptyLeaf = empty;
                    }
                    windowKey = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                    var wrapper = new LeveledItem(windowKey, SkyrimRelease.SkyrimSE)
                    { EditorID = $"Availability_{catalog.Lists[root].EditorID}", Flags = 0, ChanceNone = new Percent(0) };
                    catalog.AddList(windowKey, wrapper);
                    catalog.Fallbacks.Add(windowKey, new($"material-window|{root}|{control.List}|{control.Maximum}", "Availability", wrapper));
                    catalog.FallbackEdges.Add(new(new(windowKey, 1, 1), root));
                    catalog.FallbackEdges.Add(new(new(windowKey, (short)(control.Maximum + 1), 1), empty));
                    catalog.MaterialWindowWrappers.Add(windowIdentity, windowKey);
                }
                root = windowKey;
                // The note text is a function of these keys; repeated notes are dropped by
                // the readers' Distinct, so format each once.
                if (catalog.OtherMaterialNotes.Add((source, root, control.Identity, 0)))
                    catalog.DistributionNotes.Add($"{source}: source {control.Describe(SpecializedGraph(catalog))} retained on {catalog.Lists[root].EditorID}.");
                continue;
            }
            var guaranteed = control.Kind == MaterialAvailabilityKind.Global
                ? ProvenanceGlobalGuaranteedAt(attachment.Parent.List, control.List, control.Global, catalog)
                : FixedChanceGuaranteedAt(attachment.Parent.List, new(control.List, control.Chance), catalog);
            if (guaranteed)
            {
                if (catalog.OtherMaterialNotes.Add((source, default, control.Identity, 1)))
                    catalog.DistributionNotes.Add($"{source}: source {control.Describe(SpecializedGraph(catalog))} already supplied by the imposing list on every incoming path; no second application.");
                continue;
            }

            if (control.Kind == MaterialAvailabilityKind.Global
                && catalog.MaterialGlobalApplications.GetValueOrDefault(root) == (control.List, control.Global))
                continue;
            if (control.Kind == MaterialAvailabilityKind.FixedChance
                && catalog.MaterialChanceApplications.GetValueOrDefault(root) == new MaterialChance(control.List, control.Chance))
                continue;

            var child = (LeveledItem)catalog.Lists[root];
            // Shared selectors keep their own controls immutable across recipients.
            var emptyControl = !preserveChildControls && child.Global.IsNull && child.ChanceNone.Value == 0
                && !catalog.MaterialChanceApplications.ContainsKey(root) && !catalog.SharedAvailabilityMembers.Contains(root);
            if (control.Kind == MaterialAvailabilityKind.Global)
            {
                if (emptyControl) child.Global.SetTo(control.Global);
                else
                {
                    var wrapperIdentity = (root, control.List, control.Global);
                    if (!catalog.MaterialGlobalWrappers.TryGetValue(wrapperIdentity, out var wrapperKey))
                    {
                        wrapperKey = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                        var wrapper = new LeveledItem(wrapperKey, SkyrimRelease.SkyrimSE)
                        { EditorID = $"Availability_{child.EditorID}", Flags = (LeveledItem.Flag)3, ChanceNone = new Percent(0) };
                        wrapper.Global.SetTo(control.Global);
                        catalog.AddList(wrapperKey, wrapper);
                        catalog.Fallbacks.Add(wrapperKey, new($"material-availability|{source}|{attachment.Parent}|{root}|{control.List}", "Availability", wrapper));
                        catalog.FallbackEdges.Add(new(new(wrapperKey, 1, 1), root));
                        catalog.MaterialGlobalWrappers.Add(wrapperIdentity, wrapperKey);
                    }
                    root = wrapperKey;
                }
                catalog.MaterialGlobalApplications[root] = (control.List, control.Global);
                catalog.DistributionNotes.Add($"{source}: source global {control.Global} from {catalog.Lists[control.List].EditorID} [{control.List}] retained on {catalog.Lists[root].EditorID}.");
            }
            else
            {
                var chance = new MaterialChance(control.List, control.Chance);
                if (emptyControl)
                {
                    child.ChanceNone = new Percent(chance.Value);
                    catalog.MaterialChanceApplications[root] = chance;
                }
                else
                {
                    var wrapperIdentity = (root, chance);
                    if (!catalog.MaterialChanceWrappers.TryGetValue(wrapperIdentity, out var wrapperKey))
                    {
                        wrapperKey = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                        var wrapper = new LeveledItem(wrapperKey, SkyrimRelease.SkyrimSE)
                        { EditorID = $"Availability_{child.EditorID}", Flags = (LeveledItem.Flag)3, ChanceNone = new Percent(chance.Value) };
                        catalog.AddList(wrapperKey, wrapper);
                        catalog.Fallbacks.Add(wrapperKey, new($"material-chance|{source}|{attachment.Parent}|{root}|{chance.List}", "Availability", wrapper));
                        catalog.FallbackEdges.Add(new(new(wrapperKey, 1, 1), root));
                        catalog.MaterialChanceWrappers.Add(wrapperIdentity, wrapperKey);
                    }
                    root = wrapperKey;
                    catalog.MaterialChanceApplications[root] = chance;
                }
                catalog.DistributionNotes.Add($"{source}: source fixed Chance None {chance.Value:P0} from {catalog.Lists[chance.List].EditorID} [{chance.List}] retained on {catalog.Lists[root].EditorID}.");
            }
        }
        if (sharedKey.Controls is not null) catalog.SharedAvailabilityChains[sharedKey] = (sharedContainer, root);
        if (attachment.Unlock is { } unlock)
            // The quest controls when the family is inserted. Its equipment's
            // minimum level is enforced at the leaves, not at this selector edge.
            catalog.RuntimePlacements.Add(new(source, attachment.Parent, root, unlock));
        else
            catalog.FallbackEdges.Add(new(attachment.Parent, root));
    }

    // observedSource: the routes carry a script-site proof (condition gate and original
    // controls) that replaces the static source availability and carrier checks.
    // Parents merged into an existing plan keep the template that proved them; the
    // plan's own parents keep its template, also when an added parent shares a list.
    private static IReadOnlyDictionary<FormKey, EnchantedTemplate> MergeParentTemplates(VariantPlan plan, IReadOnlyDictionary<FormKey, EnchantedTemplate> added)
    {
        var merged = plan.ParentTemplates?.ToDictionary() ?? [];
        foreach (var parent in plan.Parents) merged.TryAdd(parent.List, plan.Template);
        foreach (var (list, template) in added) merged.TryAdd(list, template);
        return merged;
    }

    private static IReadOnlyDictionary<FormKey, EnchantedTemplate> MergeParentTemplates(VariantPlan plan, VariantPlan added)
        => MergeParentTemplates(plan, added.Parents.Select(p => p.List).Distinct()
            .ToDictionary(list => list, list => added.ParentTemplates?.GetValueOrDefault(list) ?? added.Template));

    private static void MaterializeSpecializedExpansion(CandidateItem candidate, Catalog catalog, ProfileSettings profile, PlanningAdditions<VariantPlan> plans, MaterialRouteShape[] acceptedRoutes,
        bool observedSource = false)
    {
        var key = FormKey.Factory(candidate.FormKey);
        void Note(string reason)
        {
            candidate.Reason = "Specialized material expansion: " + reason;
            catalog.DistributionNotes.Add($"{candidate.Name} [{key}]: {candidate.Reason}");
        }
        if (!observedSource && catalog.UnsafeAvailability.TryGetValue(key, out var unsafeReason)) { Note("availability protected: " + unsafeReason); return; }
        if (!observedSource && (catalog.Access is null || !catalog.AccessVerdicts.TryGetValue(key, out var verdict)
            || verdict.Kind is null || verdict.WithAccess.Count == 0))
        { Note("no carrier group receives leveled enchanted equipment of this kind"); return; }
        if (acceptedRoutes.Length == 0) { Note("no supported selection point"); return; }
        using var timing = PhaseTiming.Measure("expansion.materialize");
        var graph = SpecializedGraph(catalog);
        IMajorRecordGetter source = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter)catalog.Armors[key];
        // Route ordering ranks millions of entries; resolve each origin plugin once.
        var ranks = new Dictionary<ModKey, int>();
        int Rank(EnchantedTemplate t)
        {
            var plugin = t.Enchantment.ModKey;
            if (ranks.TryGetValue(plugin, out var rank)) return rank;
            var i = profile.EnchantmentProviderPlugins.FindIndex(p => p.Equals(plugin.FileName.String, StringComparison.OrdinalIgnoreCase));
            return ranks[plugin] = i < 0 ? int.MaxValue : i;
        }
        // The first template by (rank, item): what a stable sort's first element was.
        EnchantedTemplate BestTemplate(ArraySegment<(EnchantedTemplate Template, ParentEntry Parent)> group)
        {
            EnchantedTemplate? best = null; var bestRank = 0;
            foreach (var (template, _) in group)
            {
                var rank = Rank(template);
                if (best is null || rank < bestRank
                    || rank == bestRank && FormKeyOrder.Instance.Compare(template.Item.FormKey, best.Item.FormKey) < 0)
                { best = template; bestRank = rank; }
            }
            return best!;
        }
        static short MinimumLevel(ArraySegment<(EnchantedTemplate Template, ParentEntry Parent)> group)
        {
            var minimum = short.MaxValue;
            foreach (var (_, parent) in group) minimum = Math.Min(minimum, parent.Level);
            return minimum;
        }
        var inventory = catalog.SourceEquipment!.GetValueOrDefault(key, []);
        // Use the effective graph: rewrites remove old links, and planned additions
        // are mirrored into existing isolation copies exactly as during serialization.
        // Incremental reverse views refreshed once here; answers equal a full rebuild at this point.
        using var graphTiming = PhaseTiming.Measure("expansion.graph");
        var copies = (catalog.ExpansionCloneCopies ??= new()).Refresh(catalog);
        var listParents = (catalog.AllListParents ??= new(PlanningListScope.All)).Refresh(catalog);
        var planningEdges = (catalog.ExpansionPlanningEdges ??= new()).Refresh(catalog);
        IEnumerable<FormKey> Parents(FormKey child)
        {
            foreach (var parent in listParents.Parents(child)) yield return parent;
            foreach (var edge in planningEdges.EdgesTo(child))
            {
                yield return edge.Parent.List;
                if (InheritableEdge(edge, catalog))
                    foreach (var target in InheritedCopyTargets(edge.Parent.List, copies, catalog)) yield return target;
            }
            foreach (var insertion in planningEdges.PlacementsTo(child))
                foreach (var target in InheritedCopyTargets(insertion.Target.List, copies, catalog).Prepend(insertion.Target.List))
                    yield return target;
        }
        graphTiming?.Dispose();
        var ancestorCache = new Dictionary<FormKey, HashSet<FormKey>>();
        HashSet<FormKey> Ancestors(FormKey start)
        {
            if (ancestorCache.TryGetValue(start, out var cached)) return cached;
            var found = new HashSet<FormKey>(); var todo = new Stack<FormKey>(); todo.Push(start);
            while (todo.TryPop(out var next))
                if (found.Add(next)) foreach (var parent in Parents(next)) todo.Push(parent);
            return ancestorCache[start] = found;
        }
        var merchantRoots = acceptedRoutes.Any(r => r.First.MerchantIsolation is not null || r.First.RecipientIsolation is not null)
            ? LootRecipientCoverageRoots(catalog).ToArray() : [];
        IEnumerable<(FormKey, string)> CoverageAncestors(FormKey start, bool authored = false)
        {
            var ancestry = Ancestors(start);
            foreach (var key in authored ? CdfCoveredAncestors(start, ancestry, catalog) : ancestry) yield return (key, "");
            var covered = authored ? CdfCoveredRecipients(start, merchantRoots.Select(pair => pair.Context), catalog) : null;
            foreach (var (root, container) in merchantRoots)
                if (ancestry.Contains(root) && (covered is null || covered.Contains(container))) yield return container;
        }
        var occupied = new Dictionary<string, HashSet<(FormKey, string)>>();
        HashSet<(FormKey, string)> Coverage(string identity)
        {
            if (!occupied.TryGetValue(identity, out var set)) occupied[identity] = set = [];
            return set;
        }
        // Observed source proofs can be available independently in the same consumer.
        // A family under one proof's controls cannot reserve another proof's coverage.
        var observedCoverage = new Dictionary<(string Variant, string Proof), HashSet<(FormKey, string)>>();
        HashSet<(FormKey, string)> AttachedCoverage(string identity, MaterialSourceProof proof)
        {
            if (!observedSource) return Coverage(identity);
            var key = (identity, proof.Identity);
            if (!observedCoverage.TryGetValue(key, out var set)) observedCoverage[key] = set = [];
            return set;
        }
        foreach (var item in inventory.Where(i => OrdinaryEquipment(i, catalog.KeywordNames)))
            if (TryEnchantment(item, out var enchantment))
                Coverage(VariantIdentity(item, enchantment, catalog)).UnionWith(CoverageAncestors(item.FormKey, authored: true));
        var authoredCoverage = occupied.ToDictionary(x => x.Key, x => x.Value.ToHashSet());
        foreach (var plan in plans.Where(p => p.Source.FormKey == key))
            foreach (var parent in plan.Parents)
                foreach (var target in InheritedCopyTargets(parent.List, copies, catalog).Prepend(parent.List))
                    Coverage(VariantIdentity(plan.Template.Item, plan.Template.Enchantment, catalog)).UnionWith(CoverageAncestors(target));
        // Templates holds the template that proved each parent; one variant can be placed
        // through different same-enchantment templates at different recipients.
        var variants = new Dictionary<string, (EnchantedTemplate Template, List<ParentEntry> Parents, Dictionary<FormKey, EnchantedTemplate> Templates)>();
        // Expansion is the last planner. Snapshot the already-planned physical
        // additions once instead of rebuilding the complete impact graph for
        // every enchantment in a flat terminal.
        var directBudget = DirectPeerBudget.ForPlanning(catalog);
        var pools = 0; var directEntries = 0; var covered = 0; var skipped = new List<string>();
        var routes = acceptedRoutes.Select(shape => new MaterialExpansionRoute(shape)).ToArray();
        var materialNodes = new Dictionary<string, FormKey>();
        FormKey Node(FormKey original, string identity, bool outerControlled,
            IReadOnlyList<(FormKey List, FormKey Global)> sourceGlobals, bool shared = false)
        {
            var nodes = shared ? catalog.MaterialPathNodes : materialNodes;
            if (nodes.TryGetValue(identity, out var old)) return old;
            var key = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
            var basis = graph.Lists[original];
            var list = new LeveledItem(key, SkyrimRelease.SkyrimSE)
            { EditorID = $"Variants_{DisplayName(source)}_beside_{basis.EditorID}", Flags = basis.Flags,
                ChanceNone = outerControlled ? new Percent(0) : basis.ChanceNone };
            var sourceAlreadySuppliesBasisGlobal = sourceGlobals.Any(control =>
                SuppliesGlobalApplication(original, basis, control.List, control.Global, catalog));
            if (!outerControlled && !sourceAlreadySuppliesBasisGlobal) list.Global.SetTo(basis.Global.FormKey);
            catalog.AddList(key, list);
            catalog.Fallbacks.Add(key, new(identity, candidate.Category, list));
            nodes[identity] = key;
            return key;
        }
        // Rank each semantic variant separately: a high-priority enchantment in a
        // mixed terminal must not promote another provider's unrelated enchantments.
        var routePools = new Dictionary<string, FormKey>();
        var isolationTargets = new Dictionary<MaterialExpansionRoute, FormKey>();
        var pathTails = new Dictionary<MaterialExpansionRoute, FormKey>();
        using var choicesTiming = PhaseTiming.Measure("expansion.route-choices");
        // Choices are ordered by rank, then by their route's keys, then stably. Rank the
        // routes once instead of comparing route keys across millions of choices.
        var routeOrdinals = new int[routes.Length];
        var ancestorCounts = new int[routes.Length];
        for (var i = 0; i < routes.Length; i++) ancestorCounts[i] = Ancestors(routes[i].Point).Count;
        var routeOrder = new int[routes.Length];
        for (var i = 0; i < routeOrder.Length; i++) routeOrder[i] = i;
        Array.Sort(routeOrder, (a, b) =>
        {
            var compared = ancestorCounts[b].CompareTo(ancestorCounts[a]);
            if (compared == 0) compared = FormKeyOrder.Instance.Compare(routes[a].Point, routes[b].Point);
            if (compared == 0) compared = FormKeyOrder.Instance.Compare(routes[a].Terminal, routes[b].Terminal);
            if (compared == 0) compared = string.CompareOrdinal(routes[a].PathText, routes[b].PathText);
            return compared != 0 ? compared : a.CompareTo(b);
        });
        for (var i = 0; i < routeOrder.Length; i++) routeOrdinals[routeOrder[i]] = i;
        // Each route's entries grouped by semantic variant, in first-appearance order and
        // stably within a group: the order the former per-route GroupBy produced. Variant
        // identities are interned per template, and each group is a range of one array.
        var variantTable = graph.Variants;
        var variantOf = new Dictionary<EnchantedTemplate, int>(ReferenceEqualityComparer.Instance);
        var variantKeys = new Dictionary<int, string>();
        int Variant(EnchantedTemplate template)
        {
            if (variantOf.TryGetValue(template, out var id)) return id;
            id = variantOf[template] = variantTable.Id(template, catalog);
            variantKeys.TryAdd(id, variantTable.Key(id));
            return id;
        }
        var grouped = new (EnchantedTemplate Template, ParentEntry Parent)[routes.Sum(r => r.Entries.Length)];
        var choices = new List<MaterialRouteChoice>();
        var localGroups = new Dictionary<int, int>();
        var groupVariants = new List<int>(); var groupRanks = new List<int>(); var groupStarts = new List<int>(); var entryGroups = new List<int>();
        for (int route = 0, next = 0; route < routes.Length; route++)
        {
            var entries = routes[route].Entries;
            localGroups.Clear(); groupVariants.Clear(); groupRanks.Clear(); groupStarts.Clear(); entryGroups.Clear();
            foreach (var (template, _) in entries)
            {
                var variant = Variant(template);
                if (!localGroups.TryGetValue(variant, out var group))
                { group = groupVariants.Count; localGroups.Add(variant, group); groupVariants.Add(variant); groupRanks.Add(int.MaxValue); groupStarts.Add(0); }
                entryGroups.Add(group); groupStarts[group]++;
                groupRanks[group] = Math.Min(groupRanks[group], Rank(template));
            }
            for (int group = 0, offset = next; group < groupStarts.Count; group++)
            {
                var count = groupStarts[group]; groupStarts[group] = offset;
                choices.Add(new(route, groupVariants[group], groupRanks[group], routeOrdinals[route], offset, count));
                offset += count;
            }
            for (var entry = 0; entry < entries.Length; entry++) grouped[groupStarts[entryGroups[entry]]++] = entries[entry];
            next += entries.Length;
        }
        var choiceOrder = new int[choices.Count];
        for (var i = 0; i < choiceOrder.Length; i++) choiceOrder[i] = i;
        Array.Sort(choiceOrder, (a, b) =>
        {
            var compared = choices[a].Rank.CompareTo(choices[b].Rank);
            if (compared == 0) compared = choices[a].Ordinal.CompareTo(choices[b].Ordinal);
            return compared != 0 ? compared : a.CompareTo(b);
        });
        // One delegate for the loop; Context only calls it when the route's context is not yet cached.
        Func<FormKey, HashSet<FormKey>> ancestors = Ancestors;
        foreach (var index in choiceOrder)
        {
            var choice = choices[index];
            var route = routes[choice.Route];
            var variantKey = variantKeys[choice.Variant];
            // Isolated routes reserve the consumer boundary, not the shared original.
            var context = route.Context(ancestors);
            if (Coverage(variantKey).Overlaps(context) || AttachedCoverage(variantKey, route.Source).Overlaps(context))
            {
                if (!observedSource && authoredCoverage.GetValueOrDefault(variantKey)?.Overlaps(context) == true)
                    catalog.AuthoredCounterpartCoverage.Add(key);
                covered++; continue;
            }
            Place(route, new(grouped, choice.Start, choice.Count), variantKey, context);
        }
        // An uncovered choice. Separate from the loop, so covered choices allocate no closure state.
        void Place(MaterialExpansionRoute route, ArraySegment<(EnchantedTemplate Template, ParentEntry Parent)> group, string variantKey,
            IReadOnlySet<(FormKey, string)> context)
        {
                var terminal = route.Terminal; var point = route.Point; var path = route.Path; var materialPath = route.MaterialPath;
                var sourceProof = route.Source; var merchant = route.Merchant; var recipient = route.Recipient;
                var isolated = recipient is not null || merchant is not null || path.Length > 1;
                FormKey Isolate()
                {
                    if (isolationTargets.TryGetValue(route, out var allocated)) return allocated;
                    allocated = recipient is not null ? LootRecipientCopy(path, recipient, candidate.Category, catalog)
                        : merchant is not null ? MerchantIsolatedCopy(path, merchant, candidate.Category, catalog)
                        : IsolatedCopy(path, candidate.Category, catalog);
                    isolationTargets.Add(route, allocated);
                    return allocated;
                }
                var sourceGlobals = route.SourceGlobals;
                var unlock = sourceProof.Unlock;
                var label = graph.Lists[terminal].EditorID ?? terminal.ToString();
                var pointLabel = graph.Lists[point].EditorID ?? point.ToString();
                // A placement reaches only its own boundary's recipients, narrowed to an
                // isolated recipient or merchant, as for specialized routes. CDF removals
                // there must leave every template and any reused variant in place.
                if (catalog.CdfInventory?.Removals.Any == true)
                {
                    var recipients = BoundaryRecipients(new(path.Length > 0 ? path[^1] : point, recipient, merchant), catalog).ToArray();
                    var proofs = group.Select(entry => entry.Template).Distinct().Select(template => (template, recipients)).ToArray();
                    if (!TryCdfOutput(key, proofs, CdfEquivalentOutputs(key, variantKey, catalog), catalog, out _))
                    {
                        skipped.Add($"{pointLabel}: CDF removals leave no proved survivor for this placement's recipients");
                        return;
                    }
                }
                var identity = route.Identity;
                var wrap = materialPath.Length > 1 && materialPath[^2] == point;
                var branch = materialPath[^2];
                var poolIdentity = route.PoolIdentity;
                // A repeated root means the approved material context is itself the
                // enchanted terminal. When its peers are equipment records, preserve
                // that shape by adding the generated equipment beside them. A nested
                // family here would turn one weighted equipment choice into a pool and
                // distort both depth and odds. Independent availability controls still
                // require a branch because LVLI entries cannot carry globals/chance.
                var flatTerminal = route.FlatTerminal(graph);
                bool AvailabilityGuaranteedAtPoint() => sourceProof.Availability.Controls.All(control => control.Kind == MaterialAvailabilityKind.ActorLevelWindow ? false
                    : control.Kind == MaterialAvailabilityKind.Global
                    ? ProvenanceGlobalGuaranteedAt(point, control.List, control.Global, catalog)
                    : FixedChanceGuaranteedAt(point, new(control.List, control.Chance), catalog));
                if (flatTerminal && unlock is null && AvailabilityGuaranteedAtPoint())
                {
                    var directTemplate = BestTemplate(group);
                    var templateLevel = MinimumLevel(group);
                    var directLevel = Math.Max(templateLevel, sourceProof.Level);
                    // Raising a direct entry in a highest-eligible selector would make
                    // it replace every lower tier. Such a requirement needs a controlled
                    // family and must not silently change a flat terminal's shape.
                    if ((((int)graph.Lists[point].Flags & 1) == 0) && directLevel > templateLevel)
                    {
                        skipped.Add($"{pointLabel}: flat enchanted terminal needs direct entry level {directLevel}, but raising it above peer level {templateLevel} would take over a highest-eligible selector");
                        return;
                    }
                    // Check the effective target before allocating a context copy or
                    // reserving semantic coverage. Expansion runs after the other
                    // planners, so this includes ordinary additions, mirrored clone
                    // additions, fallback edges, and earlier direct variants. A late
                    // withdrawal used to leave the rejected route marked covered and
                    // could suppress another independently valid route.
                    var existingTarget = !isolated ? point : catalog.SpecializedPathClones.GetValueOrDefault(identity);
                    var prototype = MaterialContents(point, catalog);
                    var projectedTarget = existingTarget.IsNull ? prototype : MaterialContents(existingTarget, catalog);
                    if (!directBudget.CanAdd(projectedTarget, prototype, isolated && existingTarget.IsNull, out var projectedPositions))
                    {
                        skipped.Add($"{pointLabel}: flat enchanted terminal has {projectedPositions} projected peer positions; another direct entry would require probability-changing overflow");
                        return;
                    }
                    var directTarget = MaterialContents(existingTarget.IsNull ? Isolate() : existingTarget, catalog);
                    directBudget.Commit(directTarget);
                    if (!variants.TryGetValue(variantKey, out var directVariant))
                        variants[variantKey] = directVariant = (directTemplate, [], []);
                    directVariant.Parents.Add(new(directTarget, directLevel, 1));
                    directVariant.Templates.TryAdd(directTarget, directTemplate);
                    catalog.ShapePreservingDirectPlacements.Add((key, variantKey, directTarget));
                    AttachedCoverage(variantKey, sourceProof).UnionWith(context);
                    directEntries++;
                    return;
                }
                // Refuse a runtime route before allocating copies or modifying the graph.
                // All weighted attachment entries need slots, including inherited targets.
                // A new wrap moves existing contents into Preserved and starts with one edge.
                if (unlock is not null && !routePools.ContainsKey(poolIdentity))
                {
                    var prospective = !isolated ? point : catalog.SpecializedPathClones.GetValueOrDefault(identity, point);
                    var slots = wrap ? 1 : graph.Lists[point].Entries!.Count(e => e.Data?.Reference.FormKey == branch);
                    var reservations = EffectiveStaticEntries.Reserve(catalog, plans);
                    var inherited = catalog.SpecializedCloneOriginals.ToLookup(p => p.Value, p => p.Key);
                    var targets = isolated && !catalog.SpecializedPathClones.ContainsKey(identity)
                        ? new[] { prospective }
                        : InheritedCopyTargets(prospective, inherited, catalog).Prepend(prospective).Distinct().ToArray();
                    var full = targets.FirstOrDefault(t =>
                    {
                        if (wrap && !catalog.MaterialFamilyOriginals.ContainsKey(t)) return 1 + slots > 255;
                        var statics = reservations.ReservationFor(t)?.ProposedEntries ?? catalog.Lists[t].Entries?.Count ?? 0;
                        var runtime = catalog.RuntimePlacements.Count(r => r.Target.List == t
                            || InheritedCopyTargets(r.Target.List, inherited, catalog).Contains(t));
                        return statics + runtime + slots > 255;
                    });
                    if (!full.IsNull)
                    { skipped.Add($"{pointLabel}: runtime attachment needs {slots} slot(s); target {catalog.Lists[full].EditorID} has insufficient capacity without changing selection weights"); return; }
                }
                // A family wrap changes flags and retains availability controls. Native
                // assignments are compatible only when that prospective header survives.
                if (wrap && !isolated && !routePools.ContainsKey(poolIdentity) && !catalog.MaterialFamilyOriginals.ContainsKey(point)
                    && catalog.NativeLists?.PreservesControls(point, catalog.Lists[point], flags: (LeveledItem.Flag)3) == false)
                { skipped.Add($"{pointLabel}: SkyPatcher startup rules reassign this list's controls; a material family cannot replace them"); return; }
                if (wrap && !isolated && !routePools.ContainsKey(poolIdentity) && !catalog.MaterialFamilyOriginals.ContainsKey(point)
                    && catalog.NativeLists is { HasInputs: true }
                    && EffectiveStaticEntries.ExistingWritingProblem(catalog, point, []) is { } nativeContents)
                { skipped.Add($"{pointLabel}: SkyPatcher cannot preserve this material family's native contents when wrapping ({nativeContents})"); return; }
                if (wrap && !isolated && !routePools.ContainsKey(poolIdentity) && !catalog.MaterialFamilyOriginals.ContainsKey(point)
                    && catalog.LlosLists?.PreservesUseAll(point, (LeveledItem.Flag)3) == false)
                { skipped.Add($"{pointLabel}: LLOS rules depend on this list's UseAll flag; a material family cannot replace it"); return; }
                if (catalog.LlosLists is { HasGenerated: true } llos && llos.TouchesGenerated(catalog.Lists, isolated ? path : [point]))
                { skipped.Add($"{pointLabel}: a list on this path is created by LLOS at startup; XEP cannot copy or wrap it"); return; }
                var target = !isolated ? point : Isolate();
                if (!routePools.TryGetValue(poolIdentity, out var branchPool))
                {
                    branchPool = Node(branch, $"material-family|{key}|{identity}|{branch}|{sourceProof.Identity}", wrap, sourceGlobals);
                    if (wrap)
                    {
                        EnsureMaterialFamily(target, candidate.Category, catalog);
                        AttachOtherMaterialBranch(key, new(new(target, 1, 1), branchPool, sourceProof.Availability, unlock), catalog);
                    }
                    else
                    {
                        var mirrored = graph.Lists[point].Entries!.Where(e => e.Data?.Reference.FormKey == branch);
                        foreach (var entry in mirrored)
                            AttachOtherMaterialBranch(key, new(new(target, entry.Data!.Level, entry.Data.Count), branchPool,
                                sourceProof.Availability, unlock), catalog);
                    }
                    routePools.Add(poolIdentity, branchPool); pools++;
                }
                // A route's path below its pool is the same for every variant group:
                // after the first group, its nodes exist and its edges are present.
                if (!pathTails.TryGetValue(route, out var poolKey))
                {
                    poolKey = branchPool;
                    for (var i = materialPath.Length - 3; i >= 0; i--)
                    {
                        // Path nodes below a family are shared by every source routed through the same
                        // context, path and controls; each variant entry keeps its own minimum level, and
                        // the per-source family above still carries that source's attachment.
                        var child = Node(materialPath[i], $"material-path|{identity}|{string.Join('>', materialPath.Skip(i))}|{sourceProof.ControlIdentity}",
                            false, sourceGlobals, shared: true);
                        var rows = LeveledEntryRows(graph.Lists[materialPath[i + 1]]);
                        foreach (var edge in rows.Where(e => e.Reference == materialPath[i]))
                        {
                            var parent = new ParentEntry(poolKey, edge.Level, edge.Count);
                            var weight = rows.Count(e => e.Reference == materialPath[i] && e.Level == edge.Level && e.Count == edge.Count);
                            var pathEdge = new FallbackEdge(parent, child);
                            for (var present = catalog.FallbackEdgeCount(pathEdge); present < weight; present++)
                                catalog.FallbackEdges.Add(pathEdge);
                        }
                        poolKey = child;
                    }
                    pathTails.Add(route, poolKey);
                }
                var template = BestTemplate(group);
                // Parent levels select a material/subtype family; the equipment leaves
                // carry source/quest minima and enchantment progression. Raising a
                // family edge would replace other choices in highest-level selectors.
                var level = Math.Max(MinimumLevel(group), Math.Max(sourceProof.Level, unlock?.Level ?? 1));
                if (!variants.TryGetValue(variantKey, out var variant)) variants[variantKey] = variant = (template, [], []);
                variant.Parents.Add(new(poolKey, level, 1));
                variant.Templates.TryAdd(poolKey, template);
                AttachedCoverage(variantKey, sourceProof).UnionWith(context);
        
        }
        choicesTiming?.Dispose();
        using var finalizationTiming = PhaseTiming.Measure("expansion.finalize");
        DeferGeneratedFamilyMinima(routePools.Values, variants.Values.SelectMany(v => v.Parents), catalog, $"{candidate.Name} [{key}]");
        var firstPlanByIdentity = IndexSourcePlans(plans, key, catalog);
        foreach (var (identity, variant) in variants.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var reused = inventory.FirstOrDefault(i => OrdinaryEquipment(i, catalog.KeywordNames) && TryEnchantment(i, out var e)
                && VariantIdentity(i, e, catalog) == identity);
            if (firstPlanByIdentity.TryGetValue(identity, out var prior))
                plans[prior] = plans[prior] with { Parents = plans[prior].Parents.Concat(variant.Parents).Distinct().ToArray(),
                    PlacementReason = "Specialized enchanted routes with user-approved other-material expansion",
                    ParentTemplates = MergeParentTemplates(plans[prior], variant.Templates) };
            else
            {
                firstPlanByIdentity.Add(identity, plans.Count);
                plans.Add(new(candidate, source, variant.Template, variant.Parents.Distinct().ToArray(), reused,
                    "User-approved other-material expansion of specialized enchanted routes", ParentTemplates: variant.Templates));
            }
        }
        Note(variants.Count == 0
            ? (covered > 0 ? "already covered in this tree; no new placement needed" : "no supported selection point")
                + (skipped.Count > 0 ? ": " + string.Join("; ", skipped.Distinct().Take(5)) : "")
            : $"{variants.Count} variants in {pools} new list(s) and {directEntries} shape-preserving terminal placement(s); {covered} overlapping placement(s) already covered in this tree"
                + (skipped.Count > 0 ? $"; skipped: {string.Join("; ", skipped.Distinct().Take(5))}" : ""));
    }

    private static void DeferGeneratedFamilyMinima(IEnumerable<FormKey> families, IEnumerable<ParentEntry> variantParents,
        Catalog catalog, string label)
    {
        // Refresh positions once for this materialization. Gate updates below
        // preserve parent/child keys and collection positions, so this view stays valid.
        var variantLevels = new Dictionary<FormKey, List<short>>();
        foreach (var parent in variantParents)
        {
            if (!variantLevels.TryGetValue(parent.List, out var levels)) variantLevels[parent.List] = levels = [];
            levels.Add(parent.Level);
        }
        using var positionsTiming = PhaseTiming.Measure("expansion.positions");
        var edgesFrom = catalog.MaterialEdgesFrom.Refresh(catalog.FallbackEdges);
        var edgesTo = catalog.MaterialEdgesTo.Refresh(catalog.FallbackEdges);
        var placementsTo = catalog.MaterialPlacementsTo.Refresh(catalog.RuntimePlacements);
        positionsTiming?.Dispose();
        short MinimumGate(FormKey list, HashSet<FormKey> path, Dictionary<FormKey, short> memo)
        {
            if (!path.Add(list)) return short.MaxValue;
            if (memo.TryGetValue(list, out var known)) return known;
            var levels = variantLevels.GetValueOrDefault(list, []).Concat(edgesFrom.GetValueOrDefault(list, [])
                .Select(i => catalog.FallbackEdges[i])
                .Select(e => Math.Max(e.Parent.Level, MinimumGate(e.Child, new(path), memo)))).ToArray();
            return memo[list] = levels.Length == 0 ? short.MaxValue : levels.Min();
        }
        var pools = families.Distinct().ToArray();
        var poolKeys = pools.ToHashSet();
        // Visit matching positions in the old collection order; other sources'
        // unrelated edges cannot contribute an availability parent for these pools.
        var gateRoots = pools.Concat(poolKeys.SelectMany(child => edgesTo.GetValueOrDefault(child, [])).Order()
            .Select(i => catalog.FallbackEdges[i])
            .Where(e => catalog.Fallbacks.TryGetValue(e.Parent.List, out var n) && n.Category == "Availability")
            .Select(e => e.Parent.List)).Distinct().ToList();
        // Several source controls can wrap one family. Visit the complete owned
        // availability chain so its outer static entry or quest insertion also waits.
        var knownGateRoots = gateRoots.ToHashSet();
        for (var root = 0; root < gateRoots.Count; root++)
            foreach (var i in edgesTo.GetValueOrDefault(gateRoots[root], []))
            {
                var parent = catalog.FallbackEdges[i].Parent.List;
                if (catalog.Fallbacks.TryGetValue(parent, out var node) && node.Category == "Availability"
                    && knownGateRoots.Add(parent)) gateRoots.Add(parent);
            }
        foreach (var family in gateRoots)
        {
            // The generated graph is acyclic (the writer refuses cyclic routes), so a
            // list's minimum does not depend on the path; edges are fixed during one family.
            var gate = MinimumGate(family, [], []);
            // Calculate-from-all-levels parents can safely defer the family until
            // its first leaf is eligible. Highest-eligible parents retain their
            // mirrored level because raising one branch would replace its peers.
            foreach (var i in edgesTo.GetValueOrDefault(family, []))
            {
                var edge = catalog.FallbackEdges[i];
                if (edge.Parent.Level >= gate || ((int)catalog.Lists[edge.Parent.List].Flags & 1) == 0) continue;
                catalog.FallbackEdges[i] = edge with { Parent = edge.Parent with { Level = gate } };
            }
            foreach (var i in placementsTo.GetValueOrDefault(family, []))
            {
                var insertion = catalog.RuntimePlacements[i];
                if (insertion.Target.Level >= gate || ((int)catalog.Lists[insertion.Target.List].Flags & 1) == 0) continue;
                catalog.RuntimePlacements[i] = insertion with { Target = insertion.Target with { Level = gate } };
            }
            var attachments = edgesTo.GetValueOrDefault(family, []).Select(i => catalog.FallbackEdges[i].Parent)
                .Concat(placementsTo.GetValueOrDefault(family, []).Select(i => catalog.RuntimePlacements[i].Target));
            foreach (var parent in attachments.Where(p => p.Level < gate).Distinct())
                catalog.DistributionNotes.Add($"{label}: nested family {catalog.Lists[family].EditorID} "
                    + $"keeps parent {catalog.Lists[parent.List].EditorID} entry level {parent.Level}; first eligible equipment path is level {gate}. "
                    + "The parent uses highest-eligible selection, so raising only this family would replace lower-level peers; it may return no item if selected below that level.");
        }
    }
}
