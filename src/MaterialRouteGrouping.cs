using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // One materialization pass owns these detached route facts. Admission and
    // physical target allocation stay in the ordered variant loop.
    private sealed class MaterialExpansionRoute
    {
        public FormKey Terminal { get; }
        public FormKey Point { get; }
        public FormKey[] Path { get; }
        public FormKey[] MaterialPath { get; }
        public (EnchantedTemplate Template, ParentEntry Parent)[] Entries { get; }
        public MaterialSourceProof Source { get; }
        public MerchantInventoryEdge? Merchant { get; }
        public LootRecipientEdge? Recipient { get; }
        private string? pathText, identity, poolIdentity;
        private (FormKey List, FormKey Global)[]? sourceGlobals;
        private HashSet<(FormKey, string)>? context;
        private bool? flat;

        public MaterialExpansionRoute(MaterialRouteShape shape)
        {
            var first = shape.First;
            Terminal = first.Destination.List; Point = first.MaterialPath![^1];
            Path = first.IsolationPath!.ToArray(); MaterialPath = first.MaterialPath.ToArray();
            Entries = shape.Entries;
            Source = first.SourceProof!; Merchant = first.MerchantIsolation; Recipient = first.RecipientIsolation;
        }
        public string PathText => pathText ??= string.Join('>', Path);
        public string Identity => identity ??= Recipient is not null ? LootIsolationIdentity(Path, Recipient)
            : Merchant is not null ? IsolationIdentity(Path, Merchant) : Path.Length == 1 ? Point.ToString() : PathText;
        public string PoolIdentity => poolIdentity ??= Identity + "|" + MaterialPath[^2] + "|" + Source.Identity;
        public IReadOnlyList<(FormKey List, FormKey Global)> SourceGlobals => sourceGlobals ??=
            Source.Availability.Controls.Where(c => c.Kind == MaterialAvailabilityKind.Global).Select(c => (c.List, c.Global)).ToArray();
        public IReadOnlySet<(FormKey, string)> Context(Func<FormKey, HashSet<FormKey>> ancestors) => context ??=
            Recipient is not null ? [(Recipient.Consumer, Recipient.Role)]
            : Merchant is not null ? [(Merchant.Container, "Container inventory")]
            : ancestors(Path[^1]).Select(k => (k, "")).ToHashSet();
        public bool FlatTerminal(SpecializedEvidence graph) => flat ??=
            MaterialPath.Length > 1 && MaterialPath[^2] == Point && graph.Lists[Point].Entries!.All(e => e.Data is { } data
                && !graph.Lists.ContainsKey(data.Reference.FormKey));
    }

    // Interned semantic variants: each template maps once to an integer id, and ids map
    // back to the identity text used for coverage, plans and notes.
    private sealed class MaterialVariantTable
    {
        private readonly Dictionary<EnchantedTemplate, int> templates = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
        private readonly List<string> keys = [];
        public int Id(EnchantedTemplate template, Catalog catalog)
        {
            lock (templates)
            {
                if (templates.TryGetValue(template, out var id)) return id;
                var identity = VariantIdentity(template.Item, template.Enchantment, catalog);
                if (!ids.TryGetValue(identity, out id)) { id = keys.Count; ids.Add(identity, id); keys.Add(identity); }
                return templates[template] = id;
            }
        }
        public string Key(int id) { lock (templates) return keys[id]; }
    }

    // One semantic variant of one route: its interned variant, the best provider rank
    // among its templates, its route's ordinal, and its range of grouped entries.
    private readonly record struct MaterialRouteChoice(int Route, int Variant, int Rank, int Ordinal, int Start, int Count);

    // Accepted material routes of one shape (paths, isolation, proof): the first route,
    // which carries the shared facts, and every member's template and destination in order.
    private sealed record MaterialRouteShape(SpecializedRoute First, (EnchantedTemplate Template, ParentEntry Parent)[] Entries);

    // Shapes in first-appearance order, members in route order: the former GroupBy.
    private static MaterialRouteShape[] MaterialRouteShapes(IEnumerable<SpecializedRoute> routes)
    {
        using var timing = PhaseTiming.Measure("expansion.route-groups");
        return routes.GroupBy(r => r, MaterialRouteComparer.Instance)
            .Select(g => new MaterialRouteShape(g.First(), g.Select(r => (r.Template, r.Destination)).ToArray())).ToArray();
    }

    // Only used while grouping accepted, immutable routes. It replaces the old
    // joined string key, not the textual identities used for output allocation.
    // Without the proof it identifies a shape whose proof a later step supplies.
    private sealed class MaterialRouteComparer(bool proof) : IEqualityComparer<SpecializedRoute>
    {
        public static readonly MaterialRouteComparer Instance = new(true);
        public static readonly MaterialRouteComparer WithoutProof = new(false);

        public bool Equals(SpecializedRoute? x, SpecializedRoute? y)
        {
            if (ReferenceEquals(x, y)) return true;
            return x is not null && y is not null
                && (!proof || string.Equals(x.SourceProof!.Identity, y.SourceProof!.Identity, StringComparison.Ordinal))
                && x.MerchantIsolation?.Identity == y.MerchantIsolation?.Identity
                && x.RecipientIsolation?.Identity == y.RecipientIsolation?.Identity
                && new RoutePathIdentity(x.MaterialPath!).Equals(new(y.MaterialPath!))
                && new RoutePathIdentity(x.IsolationPath!).Equals(new(y.IsolationPath!));
        }

        public int GetHashCode(SpecializedRoute route)
        {
            var hash = new HashCode();
            hash.Add(new RoutePathIdentity(route.MaterialPath!));
            hash.Add(new RoutePathIdentity(route.IsolationPath!));
            if (proof) hash.Add(route.SourceProof!.Identity, StringComparer.Ordinal);
            hash.Add(route.MerchantIsolation?.Identity, StringComparer.Ordinal);
            hash.Add(route.RecipientIsolation?.Identity, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}
