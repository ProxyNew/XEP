using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

// Mod-author destination lists (docs/MOD-AUTHOR-RULES-FILE.md, allowedDestinations).
// They only narrow routes the engine has already accepted. Identity is the original
// load-order record: a named leveled list covers itself and every list below it; a
// named container covers itself and the lists placed directly in it. Generated copies,
// pools and siblings are never named, so checks run before they are created.
public static partial class PatcherEngine
{
    private const string OutsideAuthorDestinations = "Not in a destination the mod author lists for this item";

    private static bool HasAuthorDestinations(FormKey source, Catalog catalog) => catalog.SourceDestinationAllow.ContainsKey(source);

    // Named lists, plus the lists directly inside named containers.
    private static HashSet<FormKey> AuthorDestinationLists(HashSet<FormKey> named, Catalog catalog)
    {
        var lists = new HashSet<FormKey>();
        foreach (var key in named)
        {
            if (catalog.Lists.ContainsKey(key)) lists.Add(key);
            else if (catalog.ConsumerRecords.Containers.GetValueOrDefault(key)?.Items is { } items)
                foreach (var entry in items)
                    if (entry.Item.Item.FormKey is var item && catalog.Lists.ContainsKey(item)) lists.Add(item);
        }
        return lists;
    }

    // Authored ancestry only: generated lists cannot satisfy an author's name.
    private static HashSet<FormKey> AuthoredListAncestors(FormKey list, Catalog catalog)
    {
        var found = new HashSet<FormKey>(); var todo = new Stack<FormKey>(); todo.Push(list);
        while (todo.TryPop(out var current))
            if (found.Add(current))
                foreach (var parent in catalog.DirectParents.GetValueOrDefault(current, [])) todo.Push(parent.List);
        return found;
    }

    // A leveled list on the route (where the variant lands or a list it passes through).
    private static bool AuthorAllowsList(FormKey source, FormKey list, Catalog catalog)
        => !catalog.SourceDestinationAllow.TryGetValue(source, out var named)
            || AuthoredListAncestors(list, catalog).Overlaps(AuthorDestinationLists(named, catalog));

    // A container or other inventory owner the route places into.
    private static bool AuthorAllowsPlace(FormKey source, FormKey place, Catalog catalog)
        => !catalog.SourceDestinationAllow.TryGetValue(source, out var named) || named.Contains(place)
            || catalog.Lists.ContainsKey(place) && AuthorAllowsList(source, place, catalog);

    private static bool AuthorAllowsRoute(FormKey source, SpecializedRoute route, FormKey? receiver, Catalog catalog)
    {
        if (!HasAuthorDestinations(source, catalog)) return true;
        // Lists the variant is placed in or reached through. PeerPool is the base item's own
        // pool, not a placement, so naming a narrow pool must not admit a destination above it.
        IEnumerable<FormKey> lists = [route.Destination.List, route.ReplicatedSelector, .. route.IsolationPath ?? [], .. route.MaterialPath ?? []];
        if (receiver is { } observed) lists = lists.Append(observed);
        if (lists.Where(list => !list.IsNull && catalog.Lists.ContainsKey(list)).Any(list => AuthorAllowsList(source, list, catalog))) return true;
        FormKey[] places = [route.CountedMerchantSource?.Container ?? default, route.MerchantIsolation?.Container ?? default, route.RecipientIsolation?.Consumer ?? default];
        return places.Where(place => !place.IsNull).Any(place => AuthorAllowsPlace(source, place, catalog));
    }
}
