using System.Globalization;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Generated lists whose written content is identical are interchangeable for every
    // consumer, so the writer keeps one record and points duplicates at it. Content is
    // read from the same static-entry projection the writer materialises; referenced
    // generated lists compare by their own class. Lists touched by runtime insertions can
    // change in game, and entries with metadata are not compared, so both keep their own
    // records. Returns duplicate -> representative (the first in planning order).
    private static Dictionary<FormKey, FormKey> IdenticalGeneratedLists(Catalog catalog, StaticEntryProjection projection)
    {
        var runtime = catalog.RuntimePlacements.SelectMany(r => new[] { r.Target.List, r.Child }).ToHashSet();
        var classIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var classOf = new Dictionary<FormKey, int>();
        int Class(FormKey key)
        {
            if (classOf.TryGetValue(key, out var found)) return found;
            var text = new StringBuilder();
            var unique = runtime.Contains(key);
            var original = catalog.Fallbacks[key].Original;
            text.Append((int)original.Flags).Append('|')
                .Append(original.ChanceNone.Value.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(original.Global.FormKey);
            if (catalog.NativeLists?.HasEditorIdRules == true)
                text.Append("|native:").Append(catalog.NativeLists.SelectorIdentity(projection.GeneratedEditorIds[key]));
            void Append(StaticOverflowNode node)
            {
                foreach (var leaf in node.Leaves)
                {
                    if (leaf.Level is not { } level || leaf.HasExtraData) { unique = true; continue; }
                    text.Append(";");
                    if (leaf.Reference.GeneratedOccurrence is { } occurrence) text.Append('g').Append(occurrence);
                    else if (catalog.Fallbacks.ContainsKey(leaf.Reference.Existing)) text.Append('c').Append(Class(leaf.Reference.Existing));
                    else text.Append('e').Append(leaf.Reference.Existing);
                    text.Append('@').Append(level).Append('x').Append(leaf.Count);
                }
                foreach (var branch in node.Branches)
                {
                    text.Append("[").Append(branch.Level).Append(':');
                    Append(branch.Child);
                    text.Append(']');
                }
            }
            Append(projection[key].Tree);
            var signature = unique ? "own|" + key : text.ToString();
            if (!classIds.TryGetValue(signature, out var id)) classIds[signature] = id = classIds.Count;
            return classOf[key] = id;
        }
        var representatives = new Dictionary<int, FormKey>();
        var merged = new Dictionary<FormKey, FormKey>();
        foreach (var key in catalog.Fallbacks.Keys)
        {
            var id = Class(key);
            if (representatives.TryGetValue(id, out var representative)) merged[key] = representative;
            else representatives[id] = key;
        }
        return merged;
    }

    // Records a merged duplicate (and its overflow children) would have needed.
    private static int MergedSublistCount(Dictionary<FormKey, FormKey> merged, StaticEntryProjection projection)
        => merged.Keys.Sum(key => 1 + projection[key].Tree.ChildCount);

    // Exact structural collapses of written generated lists, decided after identical merging.
    // Inlined: a control-free list whose single count-1 entry is never reached below that
    // entry's level yields exactly that entry, so each reference takes the entry instead
    // (keeping its own level and count). Absorbed: a highest-eligible level window whose
    // other entry is a single-use, single-entry control list takes that list's Global and
    // Chance None and its entry; the control roll moves one level up a single path.
    private sealed record GeneratedListCollapse(Dictionary<FormKey, ProjectedStaticEntry> Inlined,
        Dictionary<FormKey, ProjectedStaticEntry> AbsorbedChildren, Dictionary<FormKey, FormKey> Absorbing,
        Dictionary<FormKey, List<FormKey>> HolderParents)
    {
        public int RemovedCount => Inlined.Count + AbsorbedChildren.Count;
        public bool Removed(FormKey key) => Inlined.ContainsKey(key) || AbsorbedChildren.ContainsKey(key);

        // The written lists that now hold a removed list's entry: its referencing lists,
        // looking through further removed lists. Reports name these instead of the removed list.
        public List<FormKey> Holders(FormKey key, Dictionary<FormKey, FormKey> merged)
        {
            var result = new List<FormKey>(); var seen = new HashSet<FormKey>();
            void Visit(FormKey list)
            {
                list = merged.GetValueOrDefault(list, list);
                if (!Removed(list)) { if (!result.Contains(list)) result.Add(list); return; }
                if (!seen.Add(list) || !HolderParents.TryGetValue(list, out var parents)) return;
                foreach (var parent in parents) Visit(parent);
            }
            Visit(key);
            return result;
        }

        // The reference and level a written leaf takes once removed lists are skipped.
        public (StaticEntryReference Reference, short? Level) Resolve(ProjectedStaticEntry leaf, Dictionary<FormKey, FormKey> merged)
            => Resolve(leaf.Reference, leaf.Level, merged);

        public (StaticEntryReference Reference, short? Level) Resolve(StaticEntryReference reference, short? level, Dictionary<FormKey, FormKey> merged)
        {
            for (var guard = 0; reference.GeneratedOccurrence is null && guard < 64; guard++)
            {
                var key = merged.GetValueOrDefault(reference.Existing, reference.Existing);
                if (Inlined.TryGetValue(key, out var inner)) reference = inner.Reference;
                else if (AbsorbedChildren.TryGetValue(key, out var absorbed)) { reference = absorbed.Reference; level = absorbed.Level; }
                else break;
            }
            return (reference, level);
        }
    }

    private static GeneratedListCollapse CollapseGeneratedLists(Catalog catalog, StaticEntryProjection projection, Dictionary<FormKey, FormKey> merged)
    {
        FormKey Canonical(FormKey key) => merged.GetValueOrDefault(key, key);
        // Lists that consumers other than written leveled-list entries hold must stay records.
        var pinned = new HashSet<FormKey>();
        foreach (var root in catalog.MerchantContainerRoots.Values) pinned.Add(Canonical(root));
        foreach (var rewrite in catalog.MerchantInventoryRewrites.Values) pinned.Add(Canonical(rewrite.Root));
        foreach (var rewrite in catalog.LootRecipientRewrites.Values) pinned.Add(Canonical(rewrite.Root));
        foreach (var runtime in catalog.RuntimePlacements) { pinned.Add(Canonical(runtime.Child)); pinned.Add(Canonical(runtime.Target.List)); }
        foreach (var root in catalog.CdfOutputRoots) pinned.Add(Canonical(root.Root));
        var uses = new Dictionary<FormKey, List<(ILeveledItemGetter Parent, ProjectedStaticEntry Leaf)>>();
        var holderParents = new Dictionary<FormKey, List<FormKey>>();
        var walking = FormKey.Null;
        ILeveledItemGetter Written(FormKey key) => catalog.Fallbacks.TryGetValue(key, out var node) ? node.Original : catalog.Lists[key];
        void Walk(ILeveledItemGetter parent, StaticOverflowNode node)
        {
            foreach (var leaf in node.Leaves)
                if (leaf.Reference.GeneratedOccurrence is null && catalog.Fallbacks.ContainsKey(leaf.Reference.Existing))
                {
                    var key = Canonical(leaf.Reference.Existing);
                    if (!uses.TryGetValue(key, out var list)) uses[key] = list = [];
                    list.Add((parent, leaf));
                    if (!holderParents.TryGetValue(key, out var holders)) holderParents[key] = holders = [];
                    if (!holders.Contains(walking)) holders.Add(walking);
                }
            foreach (var branch in node.Branches) Walk(parent, branch.Child);
        }
        foreach (var list in projection.ChangedLists)
            if (!merged.ContainsKey(list.List)) { walking = list.List; Walk(Written(list.List), list.Tree); }

        bool Collapsible(FormKey key) => catalog.Fallbacks.ContainsKey(key) && !merged.ContainsKey(key) && !pinned.Contains(key)
            && projection[key].Tree.Branches.IsEmpty
            && (catalog.NativeLists is not { } native || native.SelectorIdentity(projection.GeneratedEditorIds.GetValueOrDefault(key, catalog.Lists[key].EditorID)).Length == 0);
        bool Plain(ProjectedStaticEntry leaf) => leaf.HasData && !leaf.HasExtraData && leaf.Count == 1 && leaf.Level is not null;
        // Only XEP's own records move between lists: native and LLOS item rules name existing
        // equipment, so an existing item changing list could change what they insert or remove.
        bool Generated(ProjectedStaticEntry leaf) => leaf.Reference.GeneratedOccurrence is not null || catalog.Fallbacks.ContainsKey(leaf.Reference.Existing);

        var inlined = new Dictionary<FormKey, ProjectedStaticEntry>();
        var absorbedChildren = new Dictionary<FormKey, ProjectedStaticEntry>();
        var absorbing = new Dictionary<FormKey, FormKey>();
        var empty = Canonical(catalog.AvailabilityEmptyLeaf);
        foreach (var window in catalog.Fallbacks.Keys)
        {
            if (!Collapsible(window)) continue;
            var original = catalog.Fallbacks[window].Original;
            var leaves = projection[window].Tree.Leaves;
            if (original.Flags != 0 || !original.Global.IsNull || original.ChanceNone.Value != 0 || leaves.Length != 2) continue;
            var cutoff = leaves.FirstOrDefault(l => l.Reference.GeneratedOccurrence is null && Canonical(l.Reference.Existing) == empty);
            var branch = leaves.FirstOrDefault(l => !ReferenceEquals(l, cutoff));
            if (cutoff?.Level is not { } cutoffLevel || branch is null || !Plain(branch) || branch.Level != 1 || branch.Reference.GeneratedOccurrence is not null) continue;
            var child = Canonical(branch.Reference.Existing);
            if (!Collapsible(child) || absorbedChildren.ContainsKey(child) || uses.GetValueOrDefault(child)?.Count != 1) continue;
            var control = catalog.Fallbacks[child].Original;
            var childLeaves = projection[child].Tree.Leaves;
            // The entry keeps its level in the window, which gates it; a Give All child did not.
            if (control.Global.IsNull && control.ChanceNone.Value == 0 || !KnownSelectionFlags(control) || childLeaves.Length != 1
                || !Plain(childLeaves[0]) || !Generated(childLeaves[0]) || childLeaves[0].Level >= cutoffLevel
                || SelectionEntryMinimum(control, childLeaves[0].Level!.Value) != childLeaves[0].Level) continue;
            absorbedChildren[child] = childLeaves[0];
            absorbing[window] = child;
        }
        foreach (var pool in catalog.Fallbacks.Keys)
        {
            if (!Collapsible(pool) || absorbing.ContainsKey(pool) || absorbedChildren.ContainsKey(pool)) continue;
            var original = catalog.Fallbacks[pool].Original;
            var leaves = projection[pool].Tree.Leaves;
            if (!original.Global.IsNull || original.ChanceNone.Value != 0 || !KnownSelectionFlags(original)
                || leaves.Length != 1 || !Plain(leaves[0]) || !Generated(leaves[0])) continue;
            var inner = leaves[0];
            if (inner.Reference.GeneratedOccurrence is null && absorbedChildren.ContainsKey(Canonical(inner.Reference.Existing))) continue;
            // Every reference must already gate at least as high as the pool's own entry did,
            // read with each list's selection rules (Give All ignores entry levels).
            var innerMinimum = SelectionEntryMinimum(original, inner.Level!.Value);
            if (uses.GetValueOrDefault(pool) is not { Count: > 0 } references
                || references.Any(r => r.Leaf.HasExtraData || r.Leaf.Level is not { } level || !KnownSelectionFlags(r.Parent)
                    || SelectionEntryMinimum(r.Parent, level) < innerMinimum)) continue;
            inlined[pool] = inner;
        }
        return new(inlined, absorbedChildren, absorbing, holderParents);
    }
}
